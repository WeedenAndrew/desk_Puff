using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;

namespace DeskPuff.Bluetooth.Windows;

/// <summary>One-shot diagnostics. Only the explicitly selected mc mode starts heat.</summary>
public static class ThcDiagnosticProbe
{
    public static async Task<int> RunAsync(IDiagnosticLog log, bool traceWrites, CancellationToken cancellationToken)
        => await RunAsync(log, traceWrites, drive: false, mc: false, cancellationToken).ConfigureAwait(false);

    public static async Task<int> RunDriveAsync(IDiagnosticLog log, bool traceWrites, CancellationToken cancellationToken)
        => await RunAsync(log, traceWrites, drive: true, mc: false, cancellationToken).ConfigureAwait(false);

    public static async Task<int> RunMcAsync(IDiagnosticLog log, bool traceWrites, CancellationToken cancellationToken)
        => await RunAsync(log, traceWrites, drive: false, mc: true, cancellationToken).ConfigureAwait(false);

    private static async Task<int> RunAsync(IDiagnosticLog log, bool traceWrites, bool drive, bool mc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        await using LoraxDeviceClient client = new(log, traceWrites);
        int exitCode = 0;
        try
        {
            log.Write($"THC PROBE START drive={drive} mc={mc} traceWrites={traceWrites}; " +
                (mc ? "mc mode can start heat; Andrew must remain present; press-and-hold is the physical escape."
                    : "no heat command is issued."));
            IReadOnlyList<DeviceCandidate> candidates = await client.ScanAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (candidates.Count != 1)
            {
                throw new InvalidOperationException("THC probe requires exactly one nearby scan candidate; isolate the intended device and retry.");
            }
            await client.ConnectAsync(candidates[0], cancellationToken).ConfigureAwait(false);
            if (mc) { await client.RunThcMcProbeAsync(cancellationToken).ConfigureAwait(false); }
            else if (drive) { await client.RunThcDriveProbeAsync(cancellationToken).ConfigureAwait(false); }
            else { await client.RunThcProbeAsync(cancellationToken).ConfigureAwait(false); }
        }
        catch (Exception exception)
        {
            log.WriteException("THC probe halted", exception);
            exitCode = 1;
        }
        finally
        {
            try
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
                await client.DisconnectAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                log.WriteException("THC probe disconnect", exception);
                exitCode = 1;
            }
        }
        log.Write($"THC PROBE EXIT code={exitCode}");
        return exitCode;
    }
}
