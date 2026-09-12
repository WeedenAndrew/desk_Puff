using System.Buffers.Binary;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class LoraxDeviceClientTests
{
    private const string FirstDeviceColorBytes =
        "7B07FF6F4FEC5F8AD74CC1C72CEDB507FFAB07F9B607E9CE07D6E607C6F807BFFF72B2F2BC92D3E667ADFA358CFF077DFF0D8BFF14A9FF16CCFF0FEAFF07F7F307F6D707F5B207F68E07FB";
    private static readonly string[] FirstDevicePalette =
        ["#7B07FF", "#07FFAB", "#07BFFF", "#FF077D", "#FF07F7"];
    private static readonly byte[] FirstDeviceLighting = BuildCompletePikaledFixtureWithUserColors(
        FirstDeviceColorBytes,
        ["#7b07ff", "#07ffab", "#07bfff", "#ff077d", "#ff07f7"]);
    private static readonly string[] FourthProfileFallback = ["#FFFFFF"];
    private static readonly string[] ProfilePrimaryColors = ["#102030", "#202030", "#302030", "#402030"];
    private static readonly byte[] MigrationPathLighting = Convert.FromHexString(
        "A1646C616D70A2646E616D65676D696772746E3165706172616DA165706174687380");

    [TestMethod]
    [DataRow(DeviceAction.BoostTemperature)]
    [DataRow(DeviceAction.BoostTime)]
    public async Task ActiveVerifiedPeakPro_FirstBoostWritesAndVerifiesCumulativeValue(DeviceAction action)
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
            OperatingState = DeviceOperatingState.Active,
        };
        LoraxDeviceClient client = new(transport);
        DeviceSafetyPolicy policy = new();
        await using SessionController controller = new(client, policy);
        await controller.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        DeviceSnapshot snapshot = controller.Snapshot;
        Assert.AreEqual(DeviceOperatingState.Active, snapshot.OperatingState);
        Assert.AreEqual(4, snapshot.Capabilities!.MaximumConsecutiveBoosts);
        Assert.IsTrue(snapshot.Capabilities.SupportsIndependentBoost);
        SafetyDecision decision = action == DeviceAction.BoostTemperature
            ? policy.ValidateTemperatureBoost(snapshot, 0, 5 * 5.0 / 9.0)
            : policy.ValidateTimeBoost(snapshot, 0, TimeSpan.FromSeconds(10));
        Assert.IsTrue(decision.IsAllowed, $"Fixture must pass policy before the client call: {decision.Reason}");
        SafetyDecision idleDecision = policy.Evaluate(action, snapshot with { OperatingState = DeviceOperatingState.Idle });
        Assert.IsFalse(idleDecision.IsAllowed);
        Assert.AreEqual("Boost is available only during an active session.", idleDecision.Reason);

        if (action == DeviceAction.BoostTemperature)
        {
            await controller.BoostTemperatureAsync(5 * 5.0 / 9.0, CancellationToken.None);
        }
        else
        {
            await controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        Assert.AreEqual(1, transport.WriteCount);
        string path = action == DeviceAction.BoostTemperature ? LoraxPaths.TemperatureOverride : LoraxPaths.TimeOverride;
        Assert.AreEqual(path, transport.Writes[0].Path);
        Assert.AreEqual(action == DeviceAction.BoostTemperature ? "1CC73140" : "00002041",
            Convert.ToHexString(transport.Writes[0].Value));
        Assert.IsTrue(transport.ReadPaths.Count(value => value == path) >= 2, "Read current value, then verify write.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Boost_AccumulatesThreeValuesAndRefusesFourthAtCeiling(bool temperature)
    {
        FakeLoraxTransport transport = BoostTransport();
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            if (temperature) { await client.BoostTemperatureAsync(2.778, CancellationToken.None); }
            else { await client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        }
        float[] values = transport.Writes.Select(write => BinaryPrimitives.ReadSingleLittleEndian(write.Value)).ToArray();
        Assert.AreEqual(temperature ? 2.778f : 10f, values[0]);
        Assert.AreEqual(temperature ? 5.556f : 20f, values[1]);
        Assert.AreEqual(temperature ? (float)(15 * 5.0 / 9.0) : 30f, values[2]);
        Assert.IsTrue(values[2] <= (temperature ? 15 * 5.0 / 9.0 : 30));
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(async () =>
        {
            if (temperature) { await client.BoostTemperatureAsync(2.778, CancellationToken.None); }
            else { await client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        });
        Assert.AreEqual(3, transport.WriteCount);
        Assert.IsFalse(transport.Writes.Any(write => write.Path == LoraxPaths.HeaterTargetTemperature));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Boost_CountIncludesPhysicalBoostsFromMatchingOverride(bool temperature)
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.TemperatureApplied = 2;
        transport.ProfileTemperatureIncrement = 0.5f;
        transport.TimeApplied = 20;
        transport.ProfileTimeIncrement = 5;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        DeviceSafetyException error = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(async () =>
        {
            if (temperature) { await client.BoostTemperatureAsync(0.5, CancellationToken.None); }
            else { await client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        });
        StringAssert.Contains(error.Message, "per-session boost limit");
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    [DataRow(DeviceOperatingState.Idle)]
    [DataRow(DeviceOperatingState.Preheating)]
    [DataRow(DeviceOperatingState.Fading)]
    public async Task Boost_RefusesOutsideActive(DeviceOperatingState state)
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.OperatingState = state;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTemperatureAsync(2.778, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    [DataRow(-1f)]
    public async Task Boost_RejectsInvalidOverrideReads(float applied)
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.TemperatureApplied = applied;
        transport.TimeApplied = applied;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTemperatureAsync(2.778, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public async Task Boost_SessionEndsDuringRead_RefusesWrite()
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.EndSessionOnOverrideRead = true;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTemperatureAsync(2.778, CancellationToken.None));
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Boost_TraceSuppressionSkipsReadBackAndPostWriteRefresh(bool temperature)
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.SuppressWrites = true;
        RecordingDiagnosticLog log = new();
        await using LoraxDeviceClient client = new(transport, log, traceWrites: true);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        transport.ReadPaths.Clear();
        if (temperature) { await client.BoostTemperatureAsync(2.778, CancellationToken.None); }
        else { await client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        string path = temperature ? LoraxPaths.TemperatureOverride : LoraxPaths.TimeOverride;
        Assert.AreEqual(0, transport.WriteCount);
        Assert.AreEqual(1, transport.SuppressedWriteCount);
        Assert.AreEqual(1, transport.ReadPaths.Count(value => value == path), "Only the pre-write override read is expected.");
        Assert.AreEqual(1, transport.ReadPaths.Count(value => value == LoraxPaths.HeaterTemperature), "Only the pre-write refresh is expected.");
    }

    [TestMethod]
    public async Task Boost_UsesLiveHeaterTargetForAbsoluteCeiling()
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.HeaterTarget = 315;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.BoostTemperatureAsync(2.778, CancellationToken.None));
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    [DataRow(LoraxPaths.TemperatureOverride)]
    [DataRow(LoraxPaths.TimeOverride)]
    public async Task Boost_ReadBackMismatchFailsWithoutRetry(string path)
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            OperatingState = DeviceOperatingState.Active,
            MismatchedReadBackPath = path,
        };
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            if (path == LoraxPaths.TemperatureOverride) { await client.BoostTemperatureAsync(2.778, CancellationToken.None); }
            else { await client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        });
        Assert.AreEqual(1, transport.WriteCount);
    }

    [TestMethod]
    public async Task Boost_TwoTemperatureThenThreeTimeWrites_ReachIndependentCeilings()
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.ProfileTemperatureIncrement = 2.777778f;
        await using SessionController controller = new(new LoraxDeviceClient(transport), new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);

        await controller.BoostTemperatureAsync(10 * 5.0 / 9.0, CancellationToken.None);
        await controller.BoostTemperatureAsync(10 * 5.0 / 9.0, CancellationToken.None);
        Assert.AreEqual(8.333333, BinaryPrimitives.ReadSingleLittleEndian(transport.Writes[1].Value), 0.000001);
        DeviceSafetyException temperatureError = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            controller.BoostTemperatureAsync(10 * 5.0 / 9.0, CancellationToken.None));
        StringAssert.Contains(temperatureError.Message, "cumulative boost ceiling");

        for (int index = 0; index < 3; index++)
        {
            await controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.AreEqual((index + 1) * 10f,
                BinaryPrimitives.ReadSingleLittleEndian(transport.Writes[index + 2].Value));
            Assert.AreEqual(LoraxPaths.TimeOverride, transport.Writes[index + 2].Path);
        }
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.AreEqual(5, transport.WriteCount);
        Assert.IsFalse(transport.Writes.Any(write => write.Path == LoraxPaths.HeaterTargetTemperature));
    }

    [TestMethod]
    public async Task TimeBoost_FiveSecondDeviceStep_RefusesThirdTenSecondRequest()
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.ProfileTimeIncrement = 5;
        await using SessionController controller = new(new LoraxDeviceClient(transport), new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        DeviceSafetyException error = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            controller.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        StringAssert.Contains(error.Message, "per-session boost limit");
        Assert.AreEqual(2, transport.WriteCount);
        Assert.AreEqual(20f, BinaryPrimitives.ReadSingleLittleEndian(transport.Writes[1].Value));
    }

    [TestMethod]
    public async Task TimeBoost_ZeroConfiguredStep_LoadsButRefusesBeforeAnyWrite()
    {
        FakeLoraxTransport transport = BoostTransport();
        transport.ProfileTimeIncrement = 0;
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);
        Assert.IsTrue(profiles.All(profile => profile.BoostDuration == TimeSpan.Zero));
        DeviceSafetyException error = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            client.BoostTimeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        StringAssert.Contains(error.Message, "configured time boost step must be positive");
        StringAssert.Contains(error.Message, "No boost was sent");
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    [DataRow(DeviceAction.BoostTemperature, "increment", "The boost increment must be positive and finite.")]
    [DataRow(DeviceAction.BoostTime, "increment", "The boost increment must be positive and finite.")]
    [DataRow(DeviceAction.BoostTemperature, "zero-step", "Cannot boost: the profile's configured temperature boost step must be positive. No boost was sent.")]
    [DataRow(DeviceAction.BoostTime, "zero-step", "Cannot boost: the profile's configured time boost step must be positive. No boost was sent.")]
    [DataRow(DeviceAction.BoostTemperature, "count", "The device boost count cannot be determined.")]
    [DataRow(DeviceAction.BoostTime, "count", "The device boost count cannot be determined.")]
    [DataRow(DeviceAction.BoostTemperature, "ceiling", "The cumulative boost ceiling has been reached.")]
    [DataRow(DeviceAction.BoostTime, "ceiling", "The cumulative boost ceiling has been reached.")]
    [DataRow(DeviceAction.BoostTemperature, "invalid-override", "The device returned an invalid boost value.")]
    [DataRow(DeviceAction.BoostTime, "invalid-override", "The device returned an invalid boost value.")]
    [DataRow(DeviceAction.BoostTemperature, "invalid-step", "The device returned an invalid boost value.")]
    [DataRow(DeviceAction.BoostTime, "invalid-step", "The device returned an invalid boost value.")]
    [DataRow(DeviceAction.BoostTemperature, "invalid-target", "The device returned an invalid boost value.")]
    public async Task BoostRefusal_LogsExactlyOneBlockWithOriginalException(
        DeviceAction action, string scenario, string expectedReason)
    {
        FakeLoraxTransport transport = BoostTransport();
        switch (scenario)
        {
            case "zero-step":
                transport.ProfileTemperatureIncrement = 0;
                transport.ProfileTimeIncrement = 0;
                break;
            case "count":
                transport.TemperatureApplied = 1;
                transport.TimeApplied = 1;
                transport.ProfileTemperatureIncrement = float.Epsilon;
                transport.ProfileTimeIncrement = float.Epsilon;
                break;
            case "ceiling":
                transport.TemperatureApplied = (float)(15 * 5.0 / 9.0);
                transport.TimeApplied = 30;
                break;
            case "invalid-override":
                transport.TemperatureApplied = float.NaN;
                transport.TimeApplied = float.NaN;
                break;
            case "invalid-step":
                transport.ProfileTemperatureIncrement = -1;
                transport.ProfileTimeIncrement = -1;
                break;
            case "invalid-target":
                transport.HeaterTarget = float.NaN;
                break;
        }
        RecordingDiagnosticLog log = new();
        await using LoraxDeviceClient client = new(transport, log, traceWrites: false);
        await client.ConnectAsync(new("boost", "PUFFCO PEAK", -40), CancellationToken.None);
        double increment = scenario == "increment" ? 0 : 1;

        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            action == DeviceAction.BoostTemperature
                ? client.BoostTemperatureAsync(increment, CancellationToken.None)
                : client.BoostTimeAsync(TimeSpan.FromSeconds(increment), CancellationToken.None));

        Assert.AreEqual(expectedReason, exception.Message);
        string[] blocks = log.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("WRITE BLOCKED", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, blocks, "A refusal must log once, not silently or twice.");
        Assert.AreEqual($"WRITE BLOCKED action={action} reason=\"{expectedReason}\"", blocks[0]);
        Assert.AreEqual(0, transport.WriteCount);
    }

    private static FakeLoraxTransport BoostTransport() => new()
    {
        ModelCode = 13,
        FirmwareRevision = 39,
        OperatingState = DeviceOperatingState.Active,
        ProfileTemperatureIncrement = 2.778f,
    };

    [TestMethod]
    public async Task Connect_AuthenticatesAndKeepsUnknownFirmwareReadOnly()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.IsTrue(transport.UnlockKeyMatched);
        Assert.AreEqual(DeviceFamily.PeakPro, client.Snapshot.Identity!.Family);
        Assert.AreEqual(DeviceConnectionState.ConnectedReadOnly, client.Snapshot.ConnectionState);
        Assert.IsTrue(client.Snapshot.IsAuthenticated);
        Assert.IsFalse(client.Snapshot.IsFirmwareVerified);
        Assert.AreEqual(0, transport.WriteCount);
        Assert.IsFalse(transport.ReadPaths.Contains("/p/sys/hw/ser", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task CharacterisedSnapshot_HasSaneLimitsAndReachesTheWriteActionSwitch()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.IsTrue(client.Snapshot.IsFirmwareVerified);
        Assert.AreEqual(DeviceConnectionState.ConnectedControlEnabled, client.Snapshot.ConnectionState);
        DeviceLimits? limits = client.Snapshot.Limits;
        Assert.IsNotNull(limits);
        Assert.IsTrue(limits.IsSane);

        await client.SelectProfileAsync(0, CancellationToken.None);

        Assert.AreEqual(1, transport.WriteCount, "The characterised snapshot must reach the allowed write action.");
    }

    [TestMethod]
    public async Task LanternWrites_SendOneThenZeroWithoutReadBackAndLogSkippedVerification()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.SetLanternModeAsync(enabled: true, CancellationToken.None);
        await client.SetLanternModeAsync(enabled: false, CancellationToken.None);

        Assert.AreEqual(2, transport.WriteCount);
        Assert.HasCount(2, transport.Writes);
        Assert.AreEqual(LoraxPaths.LanternMode, transport.Writes[0].Path);
        Assert.AreEqual(LoraxPaths.LanternMode, transport.Writes[1].Path);
        CollectionAssert.AreEqual(new byte[] { 1 }, transport.Writes[0].Value);
        CollectionAssert.AreEqual(new byte[] { 0 }, transport.Writes[1].Value);
        Assert.IsFalse(transport.ReadPaths.Contains(LoraxPaths.LanternMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.LanternMode}\" " +
            "result=skipped reason=read-back-unsupported");
    }

    [TestMethod]
    public async Task StealthWrite_NormalPathStillReadsBackAndLogsVerifiedSuccess()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.SetStealthModeAsync(enabled: true, CancellationToken.None);

        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.StealthMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.StealthMode}\" result=verified length=1");
    }

    [TestMethod]
    public async Task StealthWrite_NormalPathStillFailsWhenReadBackDisagrees()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
            MismatchedReadBackPath = LoraxPaths.StealthMode,
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            client.SetStealthModeAsync(enabled: true, CancellationToken.None));

        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.StealthMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.StealthMode}\" result=failed");
    }

    [TestMethod]
    public async Task Connect_ReportsBatteryPercentageFromStateOfChargeInsteadOfCapacity()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.AreEqual(
            64.7d,
            client.Snapshot.BatteryPercent,
            0.1d,
            "BatteryPercent must come from state of charge.");
        Assert.AreNotEqual(
            100d,
            client.Snapshot.BatteryPercent,
            "Capacity must not be treated as a percentage.");
    }

    [TestMethod]
    public async Task Refresh_ReadsOnlyChangingTelemetryWithinBatteryInterval()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[]
            {
                LoraxPaths.ChamberType,
                LoraxPaths.OperatingState,
                LoraxPaths.ActiveProfile,
                LoraxPaths.HeaterTemperature,
                LoraxPaths.StateTotalTime,
                LoraxPaths.StateElapsedTime,
            },
            transport.ReadPaths);
    }

    [TestMethod]
    public async Task Start_UnverifiedFirmwareNeverReachesTransportWrite()
    {
        FakeLoraxTransport transport = new();
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(
            transport,
            diagnosticLog,
            traceWrites: true);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            client.StartSessionAsync(CancellationToken.None));

        Assert.AreEqual(0, transport.WriteCount);
        StringAssert.Contains(
            diagnosticLog.Text,
            "WRITE BLOCKED action=StartSession",
            "Trace mode must not bypass or hide an existing policy denial.");
    }

    [TestMethod]
    public async Task AppEnabledProxyName_IsRecognizedButStillReadOnly()
    {
        FakeLoraxTransport transport = new()
        {
            AdvertisedName = "PUFFCO PROXY",
            ModelCode = 999,
            DeviceName = "PROXY",
        };
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-proxy", "PUFFCO PROXY", -40),
            CancellationToken.None);

        Assert.AreEqual(DeviceFamily.NewProxy, client.Snapshot.Identity!.Family);
        Assert.AreEqual(DeviceConnectionState.ConnectedReadOnly, client.Snapshot.ConnectionState);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public async Task Profiles_UseAuthoredColorsAndLogPaletteSource()
    {
        FakeLoraxTransport transport = new();
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);

        Assert.HasCount(4, profiles);
        Assert.IsTrue(profiles.All(profile => profile.HasDeviceColor));
        Assert.HasCount(5, profiles[0].ColorPalette);
        CollectionAssert.AreEqual(
            FirstDevicePalette,
            profiles[0].ColorPalette.ToArray());
        Assert.AreEqual("DISCO", profiles[0].ColorwayName);
        Assert.AreEqual("pikaled2", profiles[0].LampName);
        StringAssert.Contains(
            diagnosticLog.Text,
            "PROFILE PALETTE index=0 source=meta.userColors colorCount=5 " +
            "moodName=\"DISCO\" lampName=\"pikaled2\"");
        Assert.AreEqual(
            4,
            diagnosticLog.Text.Split("PROFILE PALETTE index=", StringSplitOptions.None).Length - 1,
            "Each profile read must produce exactly one palette-source diagnostic line.");
        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.ProfileColor(0), StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task Profiles_PathAnimationWithoutColorUsesNormalFallback()
    {
        FakeLoraxTransport transport = new() { FourthProfileLighting = MigrationPathLighting };
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);

        Assert.IsFalse(profiles[3].HasDeviceColor);
        CollectionAssert.AreEqual(FourthProfileFallback, profiles[3].ColorPalette.ToArray());
        Assert.AreEqual(0, transport.WriteCount);
    }

    private sealed class FakeLoraxTransport : ILoraxTransport
    {
        private static readonly byte[] Seed = Enumerable.Range(0, 16)
            .Select(value => (byte)value)
            .ToArray();
        private readonly Dictionary<string, byte[]> writtenValues = new(StringComparer.Ordinal);

        public bool IsConnected { get; private set; }

        public string AdvertisedName { get; init; } = "PUFFCO PEAK";

        public uint ModelCode { get; init; }

        public byte FirmwareRevision { get; init; }

        public DeviceOperatingState OperatingState { get; set; } = DeviceOperatingState.Idle;

        public float TemperatureApplied { get; set; }

        public float ProfileTemperatureIncrement { get; set; } = 5;

        public float ProfileTimeIncrement { get; set; } = 10;

        public float TimeApplied { get; set; }

        public float HeaterTarget { get; set; } = 260;

        public bool EndSessionOnOverrideRead { get; set; }

        public bool SuppressWrites { get; set; }

        public int SuppressedWriteCount { get; private set; }

        public string DeviceName { get; init; } = "TEST PEAK";

        public byte[]? FourthProfileLighting { get; init; }

        public string? MismatchedReadBackPath { get; init; }

        public bool UnlockKeyMatched { get; private set; }

        public int WriteCount { get; private set; }

        public List<string> ReadPaths { get; } = [];

        public List<(string Path, byte[] Value)> Writes { get; } = [];

        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
            TimeSpan duration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);

        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task TriggerBondingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ReadOnlyMemory<byte>> RunCommandAsync(
            LoraxOpcode opcode,
            ReadOnlyMemory<byte> body,
            int maximumReplyLength,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return opcode switch
            {
                LoraxOpcode.GetAccessSeed => Result(Seed),
                LoraxOpcode.UnlockAccess => Unlock(body),
                LoraxOpcode.ReadShort => Read(body),
                LoraxOpcode.WriteShort => CountWrite(body),
                _ => throw new InvalidOperationException($"Unexpected fake opcode {opcode}."),
            };
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private Task<ReadOnlyMemory<byte>> Unlock(ReadOnlyMemory<byte> body)
        {
            UnlockKeyMatched = body.Span.SequenceEqual(LoraxProtocol.DeriveUnlockKey(Seed));
            return Result([]);
        }

        private Task<ReadOnlyMemory<byte>> Read(ReadOnlyMemory<byte> body)
        {
            ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[2..]);
            string path = Encoding.UTF8.GetString(body.Span[4..]);
            ReadPaths.Add(path);
            byte[] value = ReadValue(path);
            if (offset >= value.Length)
            {
                return Result([]);
            }

            int length = Math.Min(size, value.Length - offset);
            return Result(value.AsSpan(offset, length).ToArray());
        }

        private byte[] ReadValue(string path)
        {
            if (path == LoraxPaths.TemperatureOverride && EndSessionOnOverrideRead)
            {
                OperatingState = DeviceOperatingState.Idle;
            }
            if (writtenValues.TryGetValue(path, out byte[]? writtenValue))
            {
                return string.Equals(path, MismatchedReadBackPath, StringComparison.Ordinal)
                    ? writtenValue.Select(value => (byte)(value ^ 0xFF)).ToArray()
                    : writtenValue;
            }

            byte[]? profileValue = ReadProfileValue(path);
            return profileValue ?? path switch
            {
                LoraxPaths.ModelCode => UInt32(ModelCode),
                LoraxPaths.DeviceName => Encoding.UTF8.GetBytes(DeviceName),
                LoraxPaths.FirmwareVersion => [FirmwareRevision],
                LoraxPaths.BatteryStateOfCharge => Single(64.679f),
                LoraxPaths.BatteryCapacity => Single(6018.443f),
                LoraxPaths.BatteryChargeState => [4],
                LoraxPaths.ChamberType => [2],
                LoraxPaths.OperatingState => [(byte)OperatingState],
                LoraxPaths.ActiveProfile => [0],
                LoraxPaths.ActiveProfileName => Encoding.UTF8.GetBytes("BLUE"),
                LoraxPaths.ActiveProfileTemperature => Single(260),
                LoraxPaths.TemperatureOverride => Single(TemperatureApplied),
                LoraxPaths.TimeOverride => Single(TimeApplied),
                LoraxPaths.HeaterTargetTemperature => Single(HeaterTarget),
                LoraxPaths.ActiveProfileTime => Single(40),
                LoraxPaths.HeaterTemperature => Single(25),
                LoraxPaths.StateTotalTime => Single(40),
                LoraxPaths.StateElapsedTime => Single(0),
                _ => throw new InvalidOperationException($"Unexpected fake read path {path}."),
            };
        }

        private byte[]? ReadProfileValue(string path)
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is not ["u", "app", "hc", _, _] ||
                !int.TryParse(segments[3], out int index) ||
                index is < 0 or > 3)
            {
                return null;
            }

            return segments[4] switch
            {
                "name" => Encoding.UTF8.GetBytes($"PROFILE {index + 1}"),
                "temp" => Single(260 + index),
                "time" => Single(40 + index),
                "btmp" => Single(ProfileTemperatureIncrement),
                "btim" => Single(ProfileTimeIncrement),
                "colr" when index == 0 => FirstDeviceLighting,
                "colr" when index == 3 && FourthProfileLighting is not null => FourthProfileLighting,
                "colr" => ProfileLightingCodec.EncodeSolid(
                    [ProfilePrimaryColors[index], "#FFFFFF"]),
                _ => null,
            };
        }

        private Task<ReadOnlyMemory<byte>> CountWrite(ReadOnlyMemory<byte> body)
        {
            if (SuppressWrites)
            {
                SuppressedWriteCount++;
                return Result([]);
            }
            int terminator = body.Span[3..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidOperationException("Fake write path was not terminated.");
            }

            string path = Encoding.UTF8.GetString(body.Span.Slice(3, terminator));
            int valueOffset = 4 + terminator;
            byte[] value = body[valueOffset..].ToArray();
            writtenValues[path] = value;
            Writes.Add((path, value));
            WriteCount++;
            return Result([]);
        }

        private static Task<ReadOnlyMemory<byte>> Result(byte[] value) =>
            Task.FromResult<ReadOnlyMemory<byte>>(value);

        private static byte[] UInt32(uint value)
        {
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] Single(float value)
        {
            byte[] bytes = new byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            return bytes;
        }
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        private readonly StringBuilder text = new();

        internal string Text => text.ToString();

        public void Write(string message) => text.AppendLine(message);

        public void WriteException(string context, Exception exception) =>
            text.Append(context)
                .Append(": ")
                .Append(exception.GetType().Name)
                .Append(": ")
                .AppendLine(exception.Message);
    }

    private static byte[] BuildCompletePikaledFixtureWithUserColors(
        string colorBytesHex,
        string[] userColors)
    {
        byte[] header = Convert.FromHexString(
            "A2646C616D70A2646E616D656870696B616C65643265706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(colorBytesHex);
        byte[] metadataHeader = Convert.FromHexString(
            "646D657461A2686D6F6F644E616D6565444953434F6A75736572436F6C6F7273");
        List<byte> fixture = new(header.Length + colorBytes.Length + metadataHeader.Length + 64);
        fixture.AddRange(header);
        fixture.Add(checked((byte)colorBytes.Length));
        fixture.AddRange(colorBytes);
        fixture.AddRange(metadataHeader);
        fixture.Add((byte)(0x80 | userColors.Length));
        foreach (string color in userColors)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(color);
            fixture.Add((byte)(0x60 | encoded.Length));
            fixture.AddRange(encoded);
        }

        return fixture.ToArray();
    }
}
