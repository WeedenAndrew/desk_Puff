using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed partial class LoraxDeviceClient
{
    private RuntimeSessionTransaction? runtimeSession;
    public bool HasPendingRecovery => runtimeSession?.HasRecovery == true;
    private string CurrentRuntimeOwner => connectedCandidate?.PlatformId ?? string.Empty;

    private RuntimeSessionTransaction RuntimeSession => runtimeSession ??= new(
        (path, size, cancellationToken) => path.EndsWith("/colr", StringComparison.Ordinal)
            ? ReadAllAsync(path, MaximumProfileLightingBytes, cancellationToken)
            : ReadAsync(path, checked((ushort)size), cancellationToken),
        (path, bytes, cancellationToken) => WriteAndVerifyAsync(LoraxWriteContext.RuntimeSession, path, bytes, cancellationToken),
        cancellationToken => RequireOverrideIdleAsync(DeviceAction.ApplySessionOverride, cancellationToken),
        Snapshot.Limits);

    private async Task ApplyRuntimeSessionAsync(SessionOverride request, CancellationToken cancellationToken)
    {
        if (traceWrites) { throw new DeviceSafetyException("Trace mode cannot establish runtime application or recovery."); }
        if (string.IsNullOrEmpty(CurrentRuntimeOwner)) { throw new DeviceSafetyException("Runtime session has no connected device owner."); }
        ReadOnlyMemory<byte> api = await ReadAsync("/p/sys/fw/api", 4, cancellationToken).ConfigureAwait(false);
        if (api.Length != 4 || LoraxValueCodec.ReadUInt32(api.Span) != 39 || Snapshot.Identity?.ModelCode != 13 || Snapshot.Chamber != ChamberKind.ThreeDXL)
        {
            throw new DeviceSafetyException("Runtime vapor requires captured model 13 / API 39 / 3DXL evidence; no parameters changed.");
        }
        try
        {
            await RuntimeSession.ApplyAsync(CurrentRuntimeOwner, request, cancellationToken).ConfigureAwait(false);
            AppliedSessionOverride = request with { Colors = request.Colors.ToArray() };
            HasUntrackedSessionParameters = false;
            diagnosticLog.Write("RUNTIME SESSION verified time/temp/intensity and app-only context; name/color remain device-owned, JSON colors display-only");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            HasUntrackedSessionParameters |= RuntimeSession.HasRecovery;
            if (RuntimeSession.HasRecovery && RuntimeSession.Owns(CurrentRuntimeOwner))
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
                try { await ClearRuntimeSessionAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception exception) { diagnosticLog.WriteException("Runtime session bounded rollback unconfirmed; start blocked", exception); }
            }
            throw;
        }
    }

    private async Task ClearRuntimeSessionAsync(CancellationToken cancellationToken)
    {
        DeviceSnapshot live = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        safetyPolicy.Evaluate(DeviceAction.ApplySessionOverride, live).ThrowIfDenied();
        // The recovery obligation is removed only after exact scalar/context/opaque
        // and persistent-slot comparisons all pass. Disconnect does not erase it.
        HasUntrackedSessionParameters = true;
        await RuntimeSession.ClearAsync(CurrentRuntimeOwner, cancellationToken).ConfigureAwait(false);
        AppliedSessionOverride = null;
        HasUntrackedSessionParameters = false;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
