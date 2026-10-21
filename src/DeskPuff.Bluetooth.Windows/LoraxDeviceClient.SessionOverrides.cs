using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed partial class LoraxDeviceClient
{
    // Production apply/clear callers are serialized by SessionController.commandGate.
    // This records verified application only; it is never evidence of automatic clearing.
    internal SessionOverride? AppliedSessionOverride { get; private set; }
    internal bool HasUntrackedSessionParameters { get; private set; }

    public async Task ApplySessionOverrideAsync(SessionOverride sessionOverride, CancellationToken cancellationToken)
    {
        DeviceSnapshot live = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        EnsureAllowed(DeviceAction.ApplySessionOverride, safetyPolicy.ValidateSessionOverride(sessionOverride, live));
        if (sessionOverride.VaporMode is not null)
        {
            await ApplyRuntimeSessionAsync(sessionOverride, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (runtimeSession?.HasRecovery == true) { throw new DeviceSafetyException("Restore pending runtime recovery before a legacy session."); }
        // Freeze the local display palette; it must not become mutable proof owned by a caller.
        SessionOverride applied = sessionOverride with { Colors = sessionOverride.Colors.ToArray() };
        byte[] duration = LoraxValueCodec.WriteSingle(sessionOverride.Duration.TotalSeconds);
        byte[] temperature = LoraxValueCodec.WriteSingle(sessionOverride.TargetTemperatureCelsius);
        await RequireOverrideIdleAsync(DeviceAction.ApplySessionOverride, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write("SESSION OVERRIDE not-transmitted vapor=Standard (validation only, not written); colours=display-only (never sent); name=local-only");

        try
        {
            if (!traceWrites)
            {
                AppliedSessionOverride = null;
                // Until both writes verify, the device may hold a partial value even
                // if a reply is lost. A verified apply or hcs rollback clears this.
                HasUntrackedSessionParameters = true;
            }
            // Time FIRST: if only the first write survives, temperature remains original.
            // Temperature first could pair a new higher target with the old longer duration.
            bool timeVerified = await WriteAndVerifyAsync(LoraxWriteContext.ThcParameters,
                LoraxPaths.ActiveProfileTime, duration, cancellationToken).ConfigureAwait(false);
            if (!timeVerified && !traceWrites)
            {
                throw new IOException("Session override time write was not verified.");
            }
            await RequireOverrideIdleAsync(DeviceAction.ApplySessionOverride, cancellationToken).ConfigureAwait(false);
            bool temperatureVerified = await WriteAndVerifyAsync(LoraxWriteContext.ThcParameters,
                LoraxPaths.ActiveProfileTemperature, temperature, cancellationToken).ConfigureAwait(false);
            if (!timeVerified || !temperatureVerified)
            {
                if (traceWrites)
                {
                    diagnosticLog.Write("SESSION OVERRIDE result=not-applied reason=trace-write-suppressed; no verified state recorded");
                    return;
                }
                throw new IOException("Session override temperature write was not verified.");
            }
            AppliedSessionOverride = applied;
            HasUntrackedSessionParameters = false;
            diagnosticLog.Write("SESSION OVERRIDE result=applied timeVerified=true temperatureVerified=true");
        }
        catch (Exception exception)
        {
            diagnosticLog.WriteException("Apply session override", exception);
            if (!traceWrites)
            {
                // A failed first write may have executed despite a lost reply. Roll back
                // that uncertainty too, exactly once, while the controller still holds its gate.
                // Caller cancellation must not suppress this bounded cleanup attempt.
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
                try
                {
                    await ClearSessionOverrideAsync(cleanup.Token).ConfigureAwait(false);
                    diagnosticLog.Write("SESSION OVERRIDE partial-application rolled-back=true; hcs reload verified");
                }
                catch (Exception clearException)
                {
                    diagnosticLog.WriteException("Rollback session override", clearException);
                    diagnosticLog.Write(FormattableString.Invariant(
                        $"SESSION OVERRIDE rollback=failed; device may hold a partial override: thc/time may be {sessionOverride.Duration.TotalSeconds:R}s and thc/temp may be {sessionOverride.TargetTemperatureCelsius:R}C or their previous values; hcs reload unconfirmed; do not start; inspect/reselect on the device"));
                }
            }
            throw; // Preserve the ORIGINAL exception and stack, never the cleanup exception.
        }
    }

    public async Task ClearSessionOverrideAsync(CancellationToken cancellationToken)
    {
        if (runtimeSession?.HasRecovery == true)
        {
            await ClearRuntimeSessionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        DeviceSnapshot live = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        EnsureAllowed(DeviceAction.SelectProfile, safetyPolicy.Evaluate(DeviceAction.SelectProfile, live));
        ReadOnlyMemory<byte> slot = await ReadAsync(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false);
        if (slot.Length != 1 || slot.Span[0] > 3)
        {
            throw new InvalidDataException("Cannot clear session override: the active slot is invalid.");
        }
        int index = slot.Span[0];
        await RequireOverrideIdleAsync(DeviceAction.SelectProfile, cancellationToken).ConfigureAwait(false);
        bool verified = await WriteAndVerifyAsync(LoraxWriteContext.Normal,
            LoraxPaths.ActiveProfile, slot, cancellationToken).ConfigureAwait(false);
        if (!verified)
        {
            if (traceWrites)
            {
                diagnosticLog.Write("SESSION OVERRIDE clear=unverified reason=trace-write-suppressed; no cleared state recorded");
                return;
            }
            throw new IOException("Session override clear failed: hcs write was not verified.");
        }

        // hcs read-back alone proves only its byte, not the reload effect. This also
        // clears overrides left by a previous process; no local applied state is required.
        ReadOnlyMemory<byte> thcTemperature = await ReadAsync(LoraxPaths.ActiveProfileTemperature, 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> thcTime = await ReadAsync(LoraxPaths.ActiveProfileTime, 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> slotTemperature = await ReadAsync(LoraxPaths.ProfileTemperature(index), 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> slotTime = await ReadAsync(LoraxPaths.ProfileTime(index), 4, cancellationToken).ConfigureAwait(false);
        bool temperatureMatches = OverrideReloadMatches(thcTemperature, slotTemperature);
        bool timeMatches = OverrideReloadMatches(thcTime, slotTime);
        diagnosticLog.Write($"SESSION OVERRIDE clear-reload slot={index} temperatureMatches={temperatureMatches} timeMatches={timeMatches}");
        if (!temperatureMatches || !timeMatches)
        {
            HasUntrackedSessionParameters = true;
            throw new IOException("Session override clear failed: thc/temp or thc/time does not match the active slot after hcs reload.");
        }
        AppliedSessionOverride = null;
        HasUntrackedSessionParameters = false;
        diagnosticLog.Write("SESSION OVERRIDE clear=verified; both THC parameters match the slot");
    }

    private async Task DetectUntrackedSessionParametersAsync(CancellationToken cancellationToken)
    {
        SessionParameterState state = await ReadSessionParameterStateAsync(cancellationToken).ConfigureAwait(false);
        HasUntrackedSessionParameters = runtimeSession?.HasRecovery == true || !state.LiveMatchesSlot;
        diagnosticLog.Write(HasUntrackedSessionParameters
            ? $"SESSION OVERRIDE connect-check result=mismatch {DescribeLive(state)}; expected {DescribeSlot(state)}; writePerformed=false"
            : $"SESSION OVERRIDE connect-check result=slot-match {DescribeLive(state)}; expected {DescribeSlot(state)}; writePerformed=false");
    }

    private async Task EnsureSessionStartParametersAsync(
        SessionStartExpectation? expectedProbeParameters,
        CancellationToken cancellationToken)
    {
        if (runtimeSession?.HasRecovery == true)
        {
            await runtimeSession.VerifyAppliedAsync(CurrentRuntimeOwner, cancellationToken).ConfigureAwait(false);
            return;
        }
        SessionParameterState state = await ReadSessionParameterStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.SlotIndex == 255) { throw new DeviceSafetyException("Untracked app-only context; reselect a device profile before starting."); }
        if (expectedProbeParameters is SessionStartExpectation probe)
        {
            if (!SessionValuesMatch(state.LiveTemperature, state.LiveTime, probe.Temperature, probe.Time))
            {
                string reason = $"THC mc probe start refused: {DescribeLive(state)}; expected verified probe values {DescribeValues(probe.Temperature, probe.Time)}.";
                EnsureAllowed(DeviceAction.StartSession, SafetyDecision.Deny(reason));
            }

            diagnosticLog.Write($"SESSION OVERRIDE start-check result=verified-probe-match {DescribeLive(state)}");
            return;
        }

        if (AppliedSessionOverride is SessionOverride applied)
        {
            byte[] expectedTemperature = LoraxValueCodec.WriteSingle(applied.TargetTemperatureCelsius);
            byte[] expectedTime = LoraxValueCodec.WriteSingle(applied.Duration.TotalSeconds);
            if (!SessionValuesMatch(state.LiveTemperature, state.LiveTime, expectedTemperature, expectedTime))
            {
                HasUntrackedSessionParameters = true;
                string reason = $"Session start refused: {DescribeLive(state)}; expected applied override {DescribeValues(expectedTemperature, expectedTime)}.";
                EnsureAllowed(DeviceAction.StartSession, SafetyDecision.Deny(reason));
            }

            HasUntrackedSessionParameters = false;
            diagnosticLog.Write($"SESSION OVERRIDE start-check result=applied-match {DescribeLive(state)}");
            return;
        }

        if (state.LiveMatchesSlot)
        {
            HasUntrackedSessionParameters = false;
            diagnosticLog.Write($"SESSION OVERRIDE start-check result=slot-match {DescribeLive(state)}; expected {DescribeSlot(state)}");
            return;
        }

        HasUntrackedSessionParameters = true;
        diagnosticLog.Write($"SESSION OVERRIDE start-check result=untracked {DescribeLive(state)}; expected {DescribeSlot(state)}; action=reload-slot-before-start");
        bool reloadWriteVerified = await WriteAndVerifyAsync(
            LoraxWriteContext.Normal,
            LoraxPaths.ActiveProfile,
            new byte[] { checked((byte)state.SlotIndex) },
            cancellationToken).ConfigureAwait(false);
        SessionParameterState reloaded = await ReadSessionParameterStateAsync(cancellationToken).ConfigureAwait(false);
        if (!reloadWriteVerified || reloaded.SlotIndex != state.SlotIndex || !reloaded.LiveMatchesSlot)
        {
            string reason = $"Session start refused: active-slot reload did not restore known parameters (writeVerified={reloadWriteVerified}); {DescribeLive(reloaded)}; expected {DescribeSlot(reloaded)}.";
            EnsureAllowed(DeviceAction.StartSession, SafetyDecision.Deny(reason));
        }

        HasUntrackedSessionParameters = false;
        diagnosticLog.Write($"SESSION OVERRIDE start-check result=reloaded-slot-before-start {DescribeLive(reloaded)}; expected {DescribeSlot(reloaded)}");
    }

    private async Task<SessionParameterState> ReadSessionParameterStateAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> liveTemperature = await ReadAsync(
            LoraxPaths.ActiveProfileTemperature, 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> liveTime = await ReadAsync(
            LoraxPaths.ActiveProfileTime, 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> slot = await ReadAsync(
            LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false);
        if (slot.Length != 1 || (slot.Span[0] > 3 && slot.Span[0] != 255))
        {
            throw new InvalidDataException("Cannot verify session parameters: the active slot is invalid.");
        }
        if (slot.Span[0] == 255) { return new(255, liveTemperature, liveTime, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty); }

        int slotIndex = slot.Span[0];
        ReadOnlyMemory<byte> slotTemperature = await ReadAsync(
            LoraxPaths.ProfileTemperature(slotIndex), 4, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> slotTime = await ReadAsync(
            LoraxPaths.ProfileTime(slotIndex), 4, cancellationToken).ConfigureAwait(false);
        ValidateSessionValue(liveTemperature, LoraxPaths.ActiveProfileTemperature);
        ValidateSessionValue(liveTime, LoraxPaths.ActiveProfileTime);
        ValidateSessionValue(slotTemperature, LoraxPaths.ProfileTemperature(slotIndex));
        ValidateSessionValue(slotTime, LoraxPaths.ProfileTime(slotIndex));
        return new(slotIndex, liveTemperature, liveTime, slotTemperature, slotTime);
    }

    private static void ValidateSessionValue(ReadOnlyMemory<byte> value, string path)
    {
        if (value.Length != 4 || !float.IsFinite(LoraxValueCodec.ReadSingle(value.Span)))
        {
            throw new InvalidDataException($"Cannot verify session parameters: '{path}' did not return a finite f32 value.");
        }
    }

    private static bool SessionValuesMatch(
        ReadOnlyMemory<byte> liveTemperature,
        ReadOnlyMemory<byte> liveTime,
        ReadOnlyMemory<byte> expectedTemperature,
        ReadOnlyMemory<byte> expectedTime) =>
        OverrideReloadMatches(liveTemperature, expectedTemperature) &&
        OverrideReloadMatches(liveTime, expectedTime);

    private static string DescribeLive(SessionParameterState state) =>
        FormattableString.Invariant(
            $"live thc/temp={ReadSessionValue(state.LiveTemperature):R}C thc/time={ReadSessionValue(state.LiveTime):R}s");

    private static string DescribeSlot(SessionParameterState state) =>
        FormattableString.Invariant(
            $"slot={state.SlotIndex} temp={(state.SlotTemperature.Length == 4 ? ReadSessionValue(state.SlotTemperature) : double.NaN):R}C time={(state.SlotTime.Length == 4 ? ReadSessionValue(state.SlotTime) : double.NaN):R}s");

    private static string DescribeValues(ReadOnlyMemory<byte> temperature, ReadOnlyMemory<byte> time) =>
        FormattableString.Invariant(
            $"thc/temp={ReadSessionValue(temperature):R}C thc/time={ReadSessionValue(time):R}s");

    private static double ReadSessionValue(ReadOnlyMemory<byte> value) => LoraxValueCodec.ReadSingle(value.Span);

    private readonly record struct SessionParameterState(
        int SlotIndex,
        ReadOnlyMemory<byte> LiveTemperature,
        ReadOnlyMemory<byte> LiveTime,
        ReadOnlyMemory<byte> SlotTemperature,
        ReadOnlyMemory<byte> SlotTime)
    {
        internal bool LiveMatchesSlot => SessionValuesMatch(
            LiveTemperature,
            LiveTime,
            SlotTemperature,
            SlotTime);
    }

    // The mc diagnostic writes a downward temperature directly, then proves it
    // by read-back. Carry that observed pair through the same start guard without
    // misrepresenting the one-field probe as a fully applied production override.
    private readonly record struct SessionStartExpectation(
        ReadOnlyMemory<byte> Temperature,
        ReadOnlyMemory<byte> Time);

    private async Task RequireOverrideIdleAsync(DeviceAction action, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> state = await ReadAsync(LoraxPaths.OperatingState, 1, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write($"SESSION OVERRIDE idle-check action={action} hex={Convert.ToHexString(state.Span)}");
        EnsureAllowed(action, state.Length == 1 && state.Span[0] == 5
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("Session override apply/clear requires stat/id == 5 (idle); no further writes."));
    }

    private static bool OverrideReloadMatches(ReadOnlyMemory<byte> actual, ReadOnlyMemory<byte> expected) =>
        actual.Length == 4 && expected.Length == 4 &&
        float.IsFinite(LoraxValueCodec.ReadSingle(actual.Span)) && actual.Span.SequenceEqual(expected.Span);
}
