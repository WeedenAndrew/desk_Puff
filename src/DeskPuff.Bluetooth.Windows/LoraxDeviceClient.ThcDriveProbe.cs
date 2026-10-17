using System.Globalization;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed partial class LoraxDeviceClient
{
    internal Task RunThcDriveProbeAsync(CancellationToken cancellationToken) =>
        RunThcDriveProbeAsync(TimeSpan.FromSeconds(1), 300, cancellationToken);

    internal Task RunThcMcProbeAsync(CancellationToken cancellationToken) =>
        RunThcMcProbeAsync(TimeSpan.FromSeconds(1), 300, cancellationToken);

    internal Task RunThcMcProbeAsync(TimeSpan interval, int maximumPolls, CancellationToken cancellationToken) =>
        RunThcCycleProbeAsync(true, interval, maximumPolls, cancellationToken);

    // Internal cadence/count injection keeps fake-wire tests fast and deterministic.
    // Production observes up to 300 one-second ticks (plus read latency), bounded
    // additionally by the command-line runner's cancellation deadline.
    internal Task RunThcDriveProbeAsync(TimeSpan interval, int maximumPolls, CancellationToken cancellationToken) =>
        RunThcCycleProbeAsync(false, interval, maximumPolls, cancellationToken);

    private async Task RunThcCycleProbeAsync(bool mcStart, TimeSpan interval, int maximumPolls, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interval.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPolls);
        string driveFinding = "inconclusive";
        string clearingFinding = "unobserved";
        string survivedMcStart = mcStart ? "unobserved" : "not-applicable";
        bool sawCycle = false;
        bool sawFirstSeven = false;
        bool leftTargetDuringCycle = false;
        float? lastHeatingThc = null;
        float? lastHeatingElapsed = null;
        int evidencePhase = mcStart ? 7 : 5;
        int restorePhase = mcStart ? 8 : 6;
        diagnosticLog.Write($"THC DRIVE mode={(mcStart ? "mc" : "physical-button")}");
        try
        {
            await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> originalSlot = await ReadAsync(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write($"THC DRIVE phase=1 path=\"{LoraxPaths.ActiveProfile}\" hex={Convert.ToHexString(originalSlot.Span)}");
            if (originalSlot.Length != 1 || originalSlot.Span[0] > 3)
            {
                throw new InvalidDataException("THC drive probe requires a valid active slot.");
            }
            string slotPath = LoraxPaths.ProfileTemperature(originalSlot.Span[0]);
            float original = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
            float originalTime = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTime, cancellationToken).ConfigureAwait(false);
            float tmpo = await ReadProbeNumberAsync(LoraxPaths.TemperatureOverride, cancellationToken).ConfigureAwait(false);
            float timo = await ReadProbeNumberAsync(LoraxPaths.TimeOverride, cancellationToken).ConfigureAwait(false);
            float slotTemperature = await ReadProbeNumberAsync(slotPath, cancellationToken).ConfigureAwait(false);
            if (tmpo != 0 || timo != 0)
            {
                throw new DeviceSafetyException("THC drive probe requires tmpo and timo both zero; no write sent.");
            }
            float target = (float)(original - 16.666667);
            if (target < Snapshot.Limits!.MinimumTemperatureCelsius || !float.IsFinite(target) || target >= original)
            {
                throw new DeviceSafetyException("THC drive probe downward target is below the minimum or is not a finite decrease; no write sent.");
            }
            diagnosticLog.Write(FormattableString.Invariant($"THC DRIVE phase=3 original={original:R} target={target:R}"));
            await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
            bool verified = await WriteAndVerifyAsync(LoraxWriteContext.ThcParameters,
                LoraxPaths.ActiveProfileTemperature, LoraxValueCodec.WriteSingle(target), cancellationToken).ConfigureAwait(false);
            if (!verified)
            {
                diagnosticLog.Write("THC DRIVE outcome=trace-only hardwareEvidence=none; no cycle requested or observed.");
                return;
            }
            if (mcStart)
            {
                float reread = await ReadProbeNumberAsync(LoraxPaths.ActiveProfileTemperature, cancellationToken).ConfigureAwait(false);
                diagnosticLog.Write(FormattableString.Invariant($"THC MC phase=4 target={target:R} reread={reread:R} matchesTarget={reread == target}"));
                if (reread != target)
                {
                    throw new DeviceSafetyException("THC mc probe target did not survive the phase-4 re-read; no start sent.");
                }
                // StartSessionAsync evaluates Snapshot: refresh immediately before calling it,
                // so chamber, idle state and sensor validity are not taken from the connect cache.
                DeviceSnapshot live = await RefreshAsync(cancellationToken).ConfigureAwait(false);
                EnsureAllowed(DeviceAction.StartSession, safetyPolicy.Evaluate(DeviceAction.StartSession, live));
                diagnosticLog.Write("THC MC phase=5 start-attempt mc=07; at-most-once, never retry even if the reply or subsequent refresh fails.");
                try
                {
                    await StartSessionAsync(
                        new SessionStartExpectation(
                            LoraxValueCodec.WriteSingle(target),
                            LoraxValueCodec.WriteSingle(originalTime)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    diagnosticLog.WriteException("THC mc start outcome unknown", exception);
                    diagnosticLog.Write("THC MC start-outcome=unknown; no retry; check the device and press-and-hold to abort physically if heating; no further writes.");
                    throw;
                }
            }
            else
            {
                diagnosticLog.Write("THC DRIVE phase=4 waiting for Andrew to start the cycle physically; no mc command will be constructed.");
            }

            int failures = 0;
            bool ended = false;
            for (int tick = 0; tick < maximumPolls; tick++)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (!transport.IsConnected)
                {
                    diagnosticLog.Write("THC DRIVE outcome=disconnected clearing=unobserved");
                    return;
                }
                try
                {
                    // This is an independent raw read set, not the UI refresh cache.
                    byte state = await ReadDriveStateAsync(tick, cancellationToken).ConfigureAwait(false);
                    if (state == 3)
                    {
                        diagnosticLog.Write("THC DRIVE outcome=powered-off clearing=unobserved");
                        return;
                    }
                    bool firstSeven = state == 7 && !sawFirstSeven;
                    if (firstSeven)
                    {
                        // Reserve the FIRST 7 even if a later read in this sample fails.
                        // A later successful sample must not impersonate ignition evidence.
                        sawFirstSeven = true;
                        diagnosticLog.Write($"THC DRIVE first-seven tick={tick}; stat/elap pending, tick is not elapsed time.");
                    }
                    if (state is 7 or 8)
                    {
                        sawCycle = true;
                        lastHeatingThc = null;
                        lastHeatingElapsed = null;
                    }
                    float thc = await ReadDriveObservationAsync(LoraxPaths.ActiveProfileTemperature, tick, cancellationToken).ConfigureAwait(false);
                    if (state is 7 or 8)
                    {
                        lastHeatingThc = thc;
                        if (float.IsFinite(thc) && thc != target) { leftTargetDuringCycle = true; }
                    }
                    float command = await ReadDriveObservationAsync(LoraxPaths.HeaterTargetTemperature, tick, cancellationToken).ConfigureAwait(false);
                    if (mcStart && command > original)
                    {
                        // Act before any other observation can fail. This branch always exits;
                        // neither an uncertain reply nor the read-failure loop can retry mc=08.
                        diagnosticLog.Write(FormattableString.Invariant($"THC MC abort-trigger tcmd={command:R} original={original:R}; clearing=unobserved"));
                        await AbortThcMcProbeAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await ReadDriveObservationAsync(LoraxPaths.HeaterTemperature, tick, cancellationToken).ConfigureAwait(false);
                    float elapsed = await ReadDriveObservationAsync(LoraxPaths.StateElapsedTime, tick, cancellationToken).ConfigureAwait(false);
                    if (state is 7 or 8) { lastHeatingElapsed = elapsed; }
                    if (firstSeven && mcStart)
                    {
                        survivedMcStart = !float.IsFinite(thc) ? "inconclusive"
                            : thc == target ? "survived"
                            : thc == slotTemperature ? "reverted-to-slot" : "not-at-target";
                        diagnosticLog.Write(FormattableString.Invariant(
                            $"THC MC finding=first-seven survivedMcStart={survivedMcStart} thc={thc:R} target={target:R} stat/elap={elapsed:R}; firmware reload cause remains a hypothesis"));
                    }
                    float boost = await ReadDriveObservationAsync(LoraxPaths.TemperatureOverride, tick, cancellationToken).ConfigureAwait(false);
                    if (state is 7 or 8 && thc == target && command == target && boost == 0 && driveFinding != "observed")
                    {
                        // Corroborate the slot during the cycle, independently of whether
                        // the link survives to the end. Do not couple the two findings.
                        float liveSlot = await ReadDriveObservationAsync(slotPath, tick, cancellationToken).ConfigureAwait(false);
                        if (liveSlot == slotTemperature && liveSlot != target)
                        {
                            driveFinding = "observed";
                            diagnosticLog.Write(FormattableString.Invariant(
                                $"THC DRIVE finding=drive-observed tick={tick} stat/elap={elapsed:R} target={target:R} tcmd={command:R} tmpo={boost:R} slotTemperature={liveSlot:R}"));
                        }
                    }
                    failures = 0;
                    if (state == 5 && sawCycle) { ended = true; break; }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException)
                {
                    diagnosticLog.WriteException("THC drive observation read", exception);
                    failures++;
                    diagnosticLog.Write($"THC DRIVE tick={tick} observation=gap consecutiveFailures={failures}");
                    if (!transport.IsConnected || failures >= 3)
                    {
                        diagnosticLog.Write("THC DRIVE outcome=disconnected-or-unavailable reason=consecutive-read-failures clearing=unobserved");
                        return;
                    }
                }
            }
            if (!ended)
            {
                diagnosticLog.Write(sawCycle
                    ? "THC DRIVE outcome=timeout cycleObserved=true clearing=unobserved"
                    : "THC DRIVE outcome=timeout no cycle observed; drive=inconclusive clearing=unobserved");
                return;
            }

            // MOST IMPORTANT ORDERING CONSTRAINT: phase 5 must finish before phase 6
            // in drive mode; equivalently phase 7 before phase 8 in mc mode.
            // An hcs write here would destroy the only evidence of spontaneous clearing.
            float finalThc = await ReadDriveObservationAsync(LoraxPaths.ActiveProfileTemperature, -1, cancellationToken).ConfigureAwait(false);
            float finalSlot = await ReadDriveObservationAsync(slotPath, -1, cancellationToken).ConfigureAwait(false);
            bool heldToEnd = !leftTargetDuringCycle && lastHeatingThc == target;
            string clearingReason = leftTargetDuringCycle ? "thc-left-target-during-cycle"
                : lastHeatingThc != target ? "last-heating-target-unobserved"
                : "last-heating-target-observed";
            clearingFinding = !heldToEnd ? "unobserved"
                : !float.IsFinite(finalThc) || finalSlot != slotTemperature ? "inconclusive"
                : finalThc == slotTemperature ? "cleared-to-slot"
                : finalThc == target ? "persists-at-session-end"
                : "inconclusive";
            diagnosticLog.Write(FormattableString.Invariant(
                $"THC DRIVE phase={evidencePhase} complete thc={finalThc:R} slot={finalSlot:R} clearing={clearingFinding} reason={clearingReason} lastHeatingThc={lastHeatingThc:R} lastHeatingStat/elap={lastHeatingElapsed:R}; sequential samples bound the transition, not its exact instant"));

            await RequireProbeIdleAsync(cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write($"THC DRIVE phase={restorePhase} begin; phase-{evidencePhase} evidence recorded");
            await WriteAndVerifyAsync(LoraxWriteContext.ThcParameters, LoraxPaths.ActiveProfile, originalSlot, cancellationToken).ConfigureAwait(false);
            float restored = await ReadDriveObservationAsync(LoraxPaths.ActiveProfileTemperature, -2, cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write(FormattableString.Invariant($"THC DRIVE phase={restorePhase} complete thc={restored:R} matchesOriginalSlot={restored == slotTemperature}"));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            diagnosticLog.WriteException("THC drive cancelled or deadline reached", exception);
            diagnosticLog.Write($"THC DRIVE outcome=cancelled-or-timeout cycleObserved={sawCycle}; no further writes");
            if (!sawCycle) { diagnosticLog.Write("THC DRIVE no cycle observed; drive=inconclusive clearing=unobserved"); }
        }
        catch (IOException exception)
        {
            diagnosticLog.WriteException("THC drive halted", exception);
            diagnosticLog.Write("THC DRIVE outcome=read-or-write-failure; disconnect is not evidence of clearing; no further writes");
            throw;
        }
        finally
        {
            diagnosticLog.Write($"THC DRIVE SUMMARY drive={driveFinding} clearing={clearingFinding} survivedMcStart={survivedMcStart}");
        }
    }

    private async Task AbortThcMcProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The normal stop policy requires a live heating state. Do not weaken it.
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write("THC MC abort-attempt mc=08; at-most-once; Andrew's press-and-hold is the physical escape.");
            await StopSessionAsync(cancellationToken).ConfigureAwait(false);
            diagnosticLog.Write("THC MC abort-request-completed; run ended, clearing=unobserved; use press-and-hold if heating continues.");
        }
        catch (Exception exception)
        {
            diagnosticLog.WriteException("THC mc abort outcome unknown", exception);
            diagnosticLog.Write("THC MC abort-outcome=unknown; no retry; a missing reply does not mean the device did not act; press-and-hold the device now to abort physically.");
        }
    }

    private async Task<byte> ReadDriveStateAsync(int tick, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> bytes = await ReadAsync(LoraxPaths.OperatingState, 1, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write($"THC DRIVE tick={tick} path=\"{LoraxPaths.OperatingState}\" hex={Convert.ToHexString(bytes.Span)}");
        if (bytes.Length != 1) { throw new InvalidDataException("THC drive state observation must contain one byte."); }
        return bytes.Span[0];
    }

    private async Task<float> ReadDriveObservationAsync(string path, int tick, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> bytes = await ReadAsync(path, 4, cancellationToken).ConfigureAwait(false);
        diagnosticLog.Write($"THC DRIVE tick={tick} path=\"{path}\" hex={Convert.ToHexString(bytes.Span)}");
        if (bytes.Length != 4) { throw new InvalidDataException("THC drive observation must contain four bytes."); }
        float value = (float)LoraxValueCodec.ReadSingle(bytes.Span);
        string rendered = float.IsNaN(value) ? "NaN"
            : float.IsPositiveInfinity(value) ? "PositiveInfinity"
            : float.IsNegativeInfinity(value) ? "NegativeInfinity"
            : value.ToString("R", CultureInfo.InvariantCulture);
        diagnosticLog.Write($"THC DRIVE tick={tick} path=\"{path}\" observation={rendered} finite={float.IsFinite(value)}");
        return value; // Preserve sentinels as observations; never sanitize them to zero.
    }
}
