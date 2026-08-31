using DeskPuff.Bluetooth.Windows.Diagnostics;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class DiagnosticLoggingTests
{
    [TestMethod]
    public async Task TraceWrites_SuppressesTransmissionAndLogsCompleteFrame()
    {
        string directory = CreateTemporaryDirectory();
        string logPath = Path.Combine(directory, "trace.log");
        try
        {
            int transmissionCount = 0;
            byte[] body = LoraxProtocol.BuildWriteBody(
                LoraxPaths.StealthMode,
                offset: 0,
                flags: 0,
                new byte[] { 1 });
            byte[] expectedFrame = LoraxProtocol.BuildFrame(
                sequence: 0,
                LoraxOpcode.WriteShort,
                body);

            using (FileDiagnosticLog diagnosticLog = new(logPath))
            await using (SidecarLoraxTransport transport = new(
                diagnosticLog,
                traceWrites: true,
                (frame, sequence, cancellationToken) =>
                {
                    transmissionCount++;
                    return Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0, 0, 0 });
                }))
            {
                ReadOnlyMemory<byte> result = await transport.RunCommandAsync(
                    LoraxOpcode.WriteShort,
                    body,
                    maximumReplyLength: 0,
                    CancellationToken.None);

                Assert.IsTrue(result.IsEmpty);
            }

            string log = await File.ReadAllTextAsync(logPath);
            Assert.AreEqual(0, transmissionCount, "Trace mode must never call the frame sender.");
            StringAssert.Contains(log, "TRACE-WRITE SUPPRESSED");
            StringAssert.Contains(log, $"path=\"{LoraxPaths.StealthMode}\"");
            StringAssert.Contains(log, "value=01");
            StringAssert.Contains(log, $"frameHex={Convert.ToHexString(expectedFrame)}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void IdentityLogging_NeverIncludesSerialNumberAndUsesUtf8WithoutBom()
    {
        string directory = CreateTemporaryDirectory();
        string logPath = Path.Combine(directory, "identity.log");
        const string serialNumber = "SERIAL-DO-NOT-LOG";
        try
        {
            using (FileDiagnosticLog diagnosticLog = new(logPath))
            {
                DeviceIdentity identity = new(
                    DeviceFamily.PeakPro,
                    "Onyx Peak Pro",
                    13,
                    "AN",
                    serialNumber);
                BluetoothDiagnosticMessages.WriteIdentity(
                    diagnosticLog,
                    identity,
                    isHardwareVerified: true);
            }

            byte[] bytes = File.ReadAllBytes(logPath);
            string log = File.ReadAllText(logPath);
            Assert.IsFalse(log.Contains(serialNumber, StringComparison.Ordinal));
            StringAssert.Contains(log, "family=PeakPro modelCode=13 firmware=\"AN\" hardwareVerified=True");
            Assert.IsFalse(
                bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
                "The UTF-8 diagnostic log must not contain a BOM.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplyLogging_SeparatesFailureStatusFromEmptyPayload()
    {
        string directory = CreateTemporaryDirectory();
        string logPath = Path.Combine(directory, "status.log");
        try
        {
            using (FileDiagnosticLog diagnosticLog = new(logPath))
            await using (SidecarLoraxTransport transport = new(
                diagnosticLog,
                traceWrites: false,
                (frame, sequence, cancellationToken) =>
                    Task.FromResult<ReadOnlyMemory<byte>>(
                        new byte[] { (byte)sequence, (byte)(sequence >> 8), 0x7A })))
            {
                byte[] body = LoraxProtocol.BuildReadBody(
                    LoraxPaths.ProfileColor(0),
                    offset: 0,
                    size: 125);
                ReadOnlyMemory<byte> result = await transport.RunCommandAsync(
                    LoraxOpcode.ReadShort,
                    body,
                    maximumReplyLength: 125,
                    CancellationToken.None);

                Assert.IsTrue(result.IsEmpty);
            }

            string log = await File.ReadAllTextAsync(logPath);
            StringAssert.Contains(log, "status=0x7A payloadLength=0 payloadHex=-");
            Assert.IsFalse(log.Contains("payloadHex=7A", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "desk-puff-diagnostic-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
