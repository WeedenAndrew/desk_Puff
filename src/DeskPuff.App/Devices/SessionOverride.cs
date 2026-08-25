using DeskPuff.Core.Devices;

namespace DeskPuff.App.Devices;

/// <summary>
/// The parameters a profile runs with for one session. This is not a profile
/// slot and never becomes one: it is applied when a session starts and dropped
/// when it ends, which is what lets a saved profile run without being written
/// onto the device.
/// </summary>
internal sealed record SessionOverride(
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
internal interface ISessionOverrideClient
{
    /// <summary>
    /// Applies the parameters for the next session. Callers validate through
    /// <c>DeviceSafetyPolicy.ValidateProfileConfiguration</c> first, so the
    /// reachable envelope is exactly what a profile slot could already hold.
    /// </summary>
    Task ApplySessionOverrideAsync(SessionOverride sessionOverride, CancellationToken cancellationToken);

    /// <summary>Drops any override, returning the device to its own slot.</summary>
    Task ClearSessionOverrideAsync(CancellationToken cancellationToken);
}
