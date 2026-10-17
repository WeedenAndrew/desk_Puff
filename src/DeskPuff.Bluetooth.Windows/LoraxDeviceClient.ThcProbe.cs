using System.Globalization;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed partial class LoraxDeviceClient
{
    // Invoked only by the one-shot diagnostic runner, never by a typed UI operation.
    internal async Task RunThcProbeAsync(CancellationToken cancellationToken)
    {
        ProbeObservations observations = new();
        try
        {
            await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
            float originalTime = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTime, cancellationToken).ConfigureAwait(false);
            float originalTemperature = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> originalSlot = await ReadAsync(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false);
            if (originalSlot.Length != 1 || originalSlot.Span[0] > 3)
            {
                throw new InvalidDataException("THC probe requires a valid active slot index.");
            }
            int slot = originalSlot.Span[0];
            diagnosticLog.Write($"THC PROBE READ path=\"{LoraxPaths.ActiveProfile}\" hex={Convert.ToHexString(originalSlot.Span)}");
            string slotTimePath = LoraxPaths.ProfileTime(slot);
            string slotTemperaturePath = LoraxPaths.ProfileTemperature(slot);
            float slotTime = await ReadProbeNumberAsync(slotTimePath, cancellationToken).ConfigureAwait(false);
            float slotTemperature = await ReadProbeNumberAsync(slotTemperaturePath, cancellationToken).ConfigureAwait(false);
            (int otherSlot, float otherSlotTime, float otherSlotTemperature) =
                await FindOtherProbeSlotAsync(slot, cancellationToken).ConfigureAwait(false);

            await WriteProbeNumberAsync(observations, LoraxPaths.ActiveProfileTime, originalTime, originalTime, cancellationToken).ConfigureAwait(false);
            float lowerTime = originalTime - 10;
            if (lowerTime < Snapshot.Limits!.MinimumDuration.TotalSeconds)
            {
                throw new DeviceSafetyException("THC probe duration reduction is below DeviceLimits.MinimumDuration.");
            }
            await WriteProbeNumberAsync(observations, LoraxPaths.ActiveProfileTime, lowerTime, originalTime, cancellationToken).ConfigureAwait(false);
            await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTime, cancellationToken).ConfigureAwait(false);
            float afterSlotTime = await ReadProbeNumberAsync(slotTimePath, cancellationToken).ConfigureAwait(false);
            RequireProbeSlotUnchanged(observations, slotTimePath, slotTime, afterSlotTime);

            await WriteProbeNumberAsync(observations, LoraxPaths.ActiveProfileTemperature, originalTemperature, originalTemperature, cancellationToken).ConfigureAwait(false);
            float lowerTemperature = (float)(originalTemperature - 2.777778);
            if (lowerTemperature < Snapshot.Limits!.MinimumTemperatureCelsius)
            {
                throw new DeviceSafetyException("THC probe temperature reduction is below DeviceLimits.MinimumTemperatureCelsius.");
            }
            await WriteProbeNumberAsync(observations, LoraxPaths.ActiveProfileTemperature, lowerTemperature, originalTemperature, cancellationToken).ConfigureAwait(false);
            await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
            float afterSlotTemperature = await ReadProbeNumberAsync(slotTemperaturePath, cancellationToken).ConfigureAwait(false);
            RequireProbeSlotUnchanged(observations, slotTemperaturePath, slotTemperature, afterSlotTemperature);

            // Force a slot transition: an identity hcs write may be a firmware no-op.
            bool otherVerified = await WriteProbeAsync(observations, LoraxPaths.ActiveProfile,
                new byte[] { (byte)otherSlot }, cancellationToken).ConfigureAwait(false);
            float otherTime = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTime, cancellationToken).ConfigureAwait(false);
            float otherTemperature = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write(FormattableString.Invariant(
                $"THC PROBE RESELECT phase=other slot={otherSlot} observedTime={otherTime:R} expectedTime={otherSlotTime:R} observedTemperature={otherTemperature:R} expectedTemperature={otherSlotTemperature:R} verified={ProbeEvidence(otherVerified)} matchesSlotTime={ProbeEvidence(otherVerified ? otherTime == otherSlotTime : null)} matchesSlotTemperature={ProbeEvidence(otherVerified ? otherTemperature == otherSlotTemperature : null)}"));

            // Return via hcs only; never write an increased THC value, even to restore it.
            bool originalVerified = await WriteProbeAsync(observations, LoraxPaths.ActiveProfile, originalSlot, cancellationToken).ConfigureAwait(false);
            float restoredTime = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTime, cancellationToken).ConfigureAwait(false);
            float restoredTemperature = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
            await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
            observations.RestoredTime = originalVerified ? restoredTime == slotTime : null;
            observations.RestoredTemperature = originalVerified ? restoredTemperature == slotTemperature : null;
            diagnosticLog.Write(FormattableString.Invariant(
                $"THC PROBE RESELECT phase=original slot={slot} observedTime={restoredTime:R} expectedTime={slotTime:R} observedTemperature={restoredTemperature:R} expectedTemperature={slotTemperature:R}"));
        }
        finally
        {
            // False means not established; the counts distinguish missing evidence from a
            // failed comparison. Never turn a skipped or failed write into positive evidence.
            bool writesAccepted = observations.VerifiedWrites == 6 && observations.AttemptedWrites == 6;
            bool slotValuesUnchanged = observations.SlotComparisons == 2 && observations.UnchangedSlots == 2;
            diagnosticLog.Write($"THC PROBE writesAccepted={ProbeEvidence(writesAccepted)} slotValuesUnchanged={ProbeEvidence(slotValuesUnchanged)} verifiedWrites={observations.VerifiedWrites} attemptedWrites={observations.AttemptedWrites} slotComparisons={observations.SlotComparisons} unchangedSlotComparisons={observations.UnchangedSlots} reselectRestoredTime={ProbeEvidence(observations.RestoredTime)} reselectRestoredTemperature={ProbeEvidence(observations.RestoredTemperature)}");
            if (traceWrites)
            {
                diagnosticLog.Write("THC PROBE result=trace-only hardwareEvidence=none; suppressed frames do not establish write acceptance, aliasing, or reselect behavior.");
            }
            diagnosticLog.Write("THC PROBE no claim about application to a heat cycle or clearing at session end.");
        }
    }

    private async Task RequireProbeIdleAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        ReadOnlyMemory<byte> state = await ReadAsync(LoraxPaths.OperatingState, 1, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write($"THC PROBE IDLE CHECK hex={Convert.ToHexString(state.Span)}");
        if (state.Length != 1 || state.Span[0] != 5)
        {
            throw new DeviceSafetyException("THC probe requires stat/id == 5 (idle); no further writes.");
        }
        // Reuse the conservative configuration-write prerequisites, not a new policy action.
        // Identity, firmware, authentication, connection and sane limits remain mandatory.
        EnsureAllowed(DeviceAction.UpdateProfile, safetyPolicy.Evaluate(
            DeviceAction.UpdateProfile, Snapshot with { OperatingState = DeviceOperatingState.Idle }));
    }

    private async Task<float> ReadProbeNumberAsync(string path, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> bytes = await ReadAsync(path, 4, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write($"THC PROBE READ path=\"{path}\" hex={Convert.ToHexString(bytes.Span)}");
        if (bytes.Length != 4) { throw new InvalidDataException("THC probe expected exactly four bytes."); }
        float value = (float)LoraxValueCodec.ReadSingle(bytes.Span);
        if (!float.IsFinite(value)) { throw new InvalidDataException("THC probe requires finite values."); }
        diagnosticLog.Write($"THC PROBE VALUE path=\"{path}\" value={value.ToString("R", CultureInfo.InvariantCulture)}");
        return value;
    }

    private async Task WriteProbeNumberAsync(ProbeObservations observations, string path, float value, float original, CancellationToken cancellationToken)
    {
        if (value > original || !float.IsFinite(value))
        {
            throw new DeviceSafetyException("THC probe refuses an increase above its initial read.");
        }
        await WriteProbeAsync(observations, path, LoraxValueCodec.WriteSingle(value), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteProbeAsync(ProbeObservations observations, string path, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            observations.AttemptedWrites++;
            bool verified = await WriteAndVerifyAsync(LoraxWriteContext.ThcParameters, path, value, cancellationToken).ConfigureAwait(false);
            if (verified) { observations.VerifiedWrites++; }
            return verified;
        }
        catch (Exception exception)
        {
            // Rejection statuses are already logged by the transport. Also identify the
            // failed probe step when no read-back could occur; never retry or continue.
            diagnosticLog.Write($"THC PROBE WRITE path=\"{path}\" result=failed reason=\"{exception.Message}\"");
            throw;
        }
    }

    private void RequireProbeSlotUnchanged(ProbeObservations observations, string path, float before, float after)
    {
        if (traceWrites) { return; }
        observations.SlotComparisons++;
        if (before == after) { observations.UnchangedSlots++; }
        if (before != after)
        {
            diagnosticLog.Write($"THC PROBE result=alias-detected path=\"{path}\" hypothesis=false; slot value changed, stopping without restoration writes.");
            throw new InvalidDataException("THC probe observed a changed profile slot; the non-alias hypothesis failed.");
        }
    }

    private async Task<(int Slot, float Time, float Temperature)> FindOtherProbeSlotAsync(int originalSlot, CancellationToken cancellationToken)
    {
        DeviceLimits limits = Snapshot.Limits!;
        for (int slot = 0; slot < 4; slot++)
        {
            if (slot == originalSlot) { continue; }
            // Failed reads still halt. Well-formed but out-of-range slot data is not a
            // valid alternate; try the remaining bounded slot indices without writing.
            float time = await ReadProbeNumberAsync(LoraxPaths.ProfileTime(slot), cancellationToken).ConfigureAwait(false);
            float temperature = await ReadProbeNumberAsync(LoraxPaths.ProfileTemperature(slot), cancellationToken).ConfigureAwait(false);
            if (time >= limits.MinimumDuration.TotalSeconds && time <= limits.MaximumDuration.TotalSeconds &&
                temperature >= limits.MinimumTemperatureCelsius && temperature <= limits.MaximumTemperatureCelsius)
            {
                diagnosticLog.Write($"THC PROBE alternateSlot={slot}");
                return (slot, time, temperature);
            }
            diagnosticLog.Write($"THC PROBE invalidAlternateSlot={slot}; parameters outside verified limits.");
        }
        throw new DeviceSafetyException("THC probe requires a different valid slot; refusing identity reselect.");
    }

    private static string ProbeEvidence(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "unknown",
    };

    private sealed class ProbeObservations
    {
        internal int AttemptedWrites { get; set; }
        internal int VerifiedWrites { get; set; }
        internal int SlotComparisons { get; set; }
        internal int UnchangedSlots { get; set; }
        internal bool? RestoredTime { get; set; }
        internal bool? RestoredTemperature { get; set; }
    }
}
