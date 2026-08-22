using DeskPuff.Core.Devices;

namespace DeskPuff.Core.Safety;

public static class DeviceHandoffPolicy
{
    public static SafetyDecision EvaluateSource(DeviceSnapshot snapshot)
    {
        if (snapshot.ConnectionState is not (
            DeviceConnectionState.ConnectedReadOnly or
            DeviceConnectionState.ConnectedControlEnabled))
        {
            return SafetyDecision.Deny("A connected device is required for handoff.");
        }

        if (!snapshot.IsAuthenticated)
        {
            return SafetyDecision.Deny("Handoff requires an authenticated source device.");
        }

        if (snapshot.IsHeating)
        {
            return SafetyDecision.Deny("Device handoff is blocked during a heat cycle.");
        }

        return snapshot.OperatingState is
            DeviceOperatingState.Idle or
            DeviceOperatingState.Sleeping or
            DeviceOperatingState.PoweredOff
                ? SafetyDecision.Allow()
                : SafetyDecision.Deny("The source device must be idle, sleeping, or powered off.");
    }

    public static SafetyDecision EvaluateCandidate(DeviceCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.PlatformId) || string.IsNullOrWhiteSpace(candidate.Name))
        {
            return SafetyDecision.Deny("The nearby device does not expose a usable e-rig identity.");
        }

        bool isPeak = candidate.Name.Contains("PEAK", StringComparison.OrdinalIgnoreCase);
        bool isProxy = candidate.Name.Contains("PROXY", StringComparison.OrdinalIgnoreCase);
        return isPeak && !isProxy
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("Safe handoff only accepts nearby Peak e-rigs.");
    }

    public static SafetyDecision EvaluateDestination(DeviceSnapshot snapshot)
    {
        if (!snapshot.IsAuthenticated || snapshot.Identity?.Family != DeviceFamily.PeakPro)
        {
            return SafetyDecision.Deny("The destination did not authenticate as a supported Peak e-rig.");
        }

        return SafetyDecision.Allow();
    }
}
