using System.Globalization;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;

namespace DeskPuff.Bluetooth.Windows.Diagnostics;

internal static class BluetoothDiagnosticMessages
{
    internal static void WriteIdentity(
        IDiagnosticLog diagnosticLog,
        DeviceIdentity identity,
        bool isHardwareVerified)
    {
        diagnosticLog.Write(
            $"DEVICE IDENTITY family={identity.Family} " +
            $"modelCode={identity.ModelCode.ToString(CultureInfo.InvariantCulture)} " +
            $"firmware=\"{identity.FirmwareVersion}\" " +
            $"hardwareVerified={isHardwareVerified.ToString(CultureInfo.InvariantCulture)}");
    }
}
