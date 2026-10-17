namespace DeskPuff.Core.Devices;

/// <summary>
/// The parameters a profile runs with without becoming a device profile slot.
/// The override persists on the device until explicitly cleared; it is not
/// dropped automatically at session end. Clearing it is the implementation's
/// responsibility (observed on Peak Pro model 13, firmware AN, 2026-09-12).
/// </summary>
public sealed record SessionOverride(
    string Name,
    double TargetTemperatureCelsius,
    TimeSpan Duration,
    VaporLevel Vapor,
    IReadOnlyList<string> Colors);

/// <summary>
/// Implemented by device clients that can run a set of parameters for a single
/// session. Deliberately separate from <see cref="IDeviceClient"/>: a client
/// that cannot do this simply does not implement it, and the app reports the
/// feature unavailable rather than starting the wrong profile.
/// </summary>
public interface ISessionOverrideClient
{
    /// <summary>
    /// Applies the parameters for the next session. An implementation that
    /// writes to hardware must validate independently and must not assume
    /// that a caller has already validated the request. The interface itself
    /// enforces no validation. The UI's current validation is a convenience,
    /// not a guarantee: it can use <c>PreferenceFallbackLimits</c> instead of
    /// the device's recorded limits, so the hardware envelope is not assured.
    /// </summary>
    Task ApplySessionOverrideAsync(SessionOverride sessionOverride, CancellationToken cancellationToken);

    /// <summary>Drops any override, returning the device to its own slot.</summary>
    Task ClearSessionOverrideAsync(CancellationToken cancellationToken);
}
