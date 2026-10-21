using System.Buffers.Binary;
using DeskPuff.Bluetooth.Windows.Compatibility;
using DeskPuff.Bluetooth.Windows.Diagnostics;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed partial class LoraxDeviceClient : IDeviceClient, ISessionOverrideClient
{
    // PEAKSHI V2 firmware 39 returned 517, 466, 517, and 799 bytes for profile slots
    // 0-3 on 2026-08-31. 4096 remains bounded headroom above the largest observed value, 799.
    private const int MaximumProfileLightingBytes = 4096;

    // Every PEAKSHI V2 firmware 39 survey from 2026-08-27 through 2026-08-31
    // found that these two paths answer reads with status 0x01 and no payload.
    // They are explicit measurements, not an empty-payload heuristic: a
    // successful write to either path cannot be confirmed by reading it back.
    private static readonly HashSet<string> PathsWithoutReadBackVerification = new(StringComparer.Ordinal)
    {
        LoraxPaths.LanternMode,
        LoraxPaths.ModeCommand,
    };

    private readonly ILoraxTransport transport;
    private readonly IDiagnosticLog diagnosticLog;
    private readonly bool traceWrites;
    private readonly DeviceSafetyPolicy safetyPolicy = new();
    private DeviceCandidate? connectedCandidate;
    private DeviceIdentity? connectedIdentity;
    private DeviceCapabilities? connectedCapabilities;
    private DateTimeOffset lastBatteryRead = DateTimeOffset.MinValue;
    private string? supportedBatteryPath;
    private bool disposed;

    public LoraxDeviceClient()
        : this(NullDiagnosticLog.Instance, traceWrites: false)
    {
    }

    public LoraxDeviceClient(IDiagnosticLog diagnosticLog, bool traceWrites = false)
        : this(
            new SidecarLoraxTransport(diagnosticLog, traceWrites),
            diagnosticLog,
            traceWrites)
    {
    }

    internal LoraxDeviceClient(ILoraxTransport transport)
        : this(transport, NullDiagnosticLog.Instance, traceWrites: false)
    {
    }

    internal LoraxDeviceClient(
        ILoraxTransport transport,
        IDiagnosticLog diagnosticLog,
        bool traceWrites = false)
    {
        this.transport = transport;
        this.diagnosticLog = diagnosticLog;
        this.traceWrites = traceWrites;
    }

    public DeviceSnapshot Snapshot { get; private set; } = DeviceSnapshot.Disconnected;

    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        transport.ScanAsync(duration, cancellationToken);

    public async Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        runtimeSession?.InvalidateApplication();
        AppliedSessionOverride = null; // A new connection must not inherit another connection's proof.
        HasUntrackedSessionParameters = false;
        SetSnapshot(DeviceSnapshot.Disconnected with
        {
            ConnectionState = DeviceConnectionState.Connecting,
        });

        try
        {
            await transport.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
            connectedCandidate = candidate;
            SetSnapshot(Snapshot with
            {
                ConnectionState = DeviceConnectionState.Authenticating,
            });
            await transport.TriggerBondingAsync(cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            DeviceSnapshot snapshot = await ReadInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await DetectUntrackedSessionParametersAsync(cancellationToken).ConfigureAwait(false);
            SetSnapshot(snapshot);
            if (runtimeSession?.HasRecovery == true && runtimeSession.Owns(CurrentRuntimeOwner) && snapshot.IsFirmwareVerified && snapshot.OperatingState == DeviceOperatingState.Idle)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
                await ClearRuntimeSessionAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnosticLog.WriteException("Connect device", exception);
            SetSnapshot(DeviceSnapshot.Disconnected with
            {
                ConnectionState = DeviceConnectionState.Faulted,
                Fault = SanitizeFault(exception.Message),
            });
            await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        runtimeSession?.InvalidateApplication();
        Exception? recoveryFailure = null;
        if (runtimeSession?.HasRecovery == true && runtimeSession.Owns(CurrentRuntimeOwner) && Snapshot.OperatingState == DeviceOperatingState.Idle)
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
            try { await ClearRuntimeSessionAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception exception) { recoveryFailure = exception; diagnosticLog.WriteException("Runtime session disconnect recovery", exception); }
        }
        await transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        // Forget local proof only after disconnect succeeds. A failed disconnect
        // may leave this client live; clearing the proof first would make it lie.
        AppliedSessionOverride = null;
        HasUntrackedSessionParameters = false;
        connectedCandidate = null;
        connectedIdentity = null;
        connectedCapabilities = null;
        lastBatteryRead = DateTimeOffset.MinValue;
        supportedBatteryPath = null;
        SetSnapshot(DeviceSnapshot.Disconnected);
        if (recoveryFailure is not null) { throw new IOException("Disconnected with pending runtime recovery; reconnect the same device before starting.", recoveryFailure); }
    }

    public async Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        DeviceSnapshot refreshed = await ReadTelemetrySnapshotAsync(
            forceProfileRead: false,
            forceBatteryRead: false,
            cancellationToken).ConfigureAwait(false);
        SetSnapshot(refreshed);
        return refreshed;
    }

    public async Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        List<HeatProfile> profiles = new(4);
        string[] defaultColors = ["#0000FF", "#6EE916", "#F80B00", "#FFFFFF"];
        for (int index = 0; index < 4; index++)
        {
            string name = LoraxValueCodec.ReadString(
                (await ReadAsync(LoraxPaths.ProfileName(index), 32, cancellationToken).ConfigureAwait(false)).Span);
            double temperature = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ProfileTemperature(index), 4, cancellationToken).ConfigureAwait(false)).Span);
            double durationSeconds = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ProfileTime(index), 4, cancellationToken).ConfigureAwait(false)).Span);
            double boostTemperature = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ProfileBoostTemperature(index), 4, cancellationToken).ConfigureAwait(false)).Span);
            double boostSeconds = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ProfileBoostTime(index), 4, cancellationToken).ConfigureAwait(false)).Span);
            string[] colorPalette;
            bool hasDeviceColor;
            string? colorwayName = null;
            string? lampName = null;
            try
            {
                ReadOnlyMemory<byte> lighting = await ReadAllAsync(
                    LoraxPaths.ProfileColor(index),
                    maximumLength: MaximumProfileLightingBytes,
                    cancellationToken).ConfigureAwait(false);
                ProfileLightingCodec.DecodedLighting decoded =
                    ProfileLightingCodec.DecodeLighting(lighting.Span);
                string paletteSourceName = decoded.Source switch
                {
                    ProfileLightingCodec.PaletteSource.MetaUserColors => "meta.userColors",
                    ProfileLightingCodec.PaletteSource.LampParamColor => "lamp.param.color",
                    _ => "none",
                };
                colorwayName = decoded.MoodName;
                lampName = decoded.LampName;
                diagnosticLog.Write(
                    $"PROFILE PALETTE index={index} source={paletteSourceName} colorCount={decoded.Colors.Count} " +
                    $"moodName={FormatDiagnosticMetadata(colorwayName)} lampName={FormatDiagnosticMetadata(lampName)}");
                hasDeviceColor = decoded.Colors.Count > 0;
                colorPalette = hasDeviceColor
                    ? decoded.Colors.ToArray()
                    : [defaultColors[index]];
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
            {
                diagnosticLog.WriteException($"Decode device color profile {index}", exception);
                diagnosticLog.Write(
                    $"PROFILE PALETTE index={index} source=app-default colorCount=1 " +
                    $"moodName=- lampName=- reason=decode-error");
                colorPalette = [defaultColors[index]];
                hasDeviceColor = false;
            }

            profiles.Add(new HeatProfile(
                index,
                name,
                temperature,
                SecondsOrZero(durationSeconds),
                VaporLevel.Standard,
                boostTemperature,
                SecondsOrZero(boostSeconds),
                colorPalette[0])
            {
                ColorPalette = colorPalette,
                HasDeviceColor = hasDeviceColor,
                ColorwayName = colorwayName,
                LampName = lampName,
            });
        }

        return profiles;
    }

    public async Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
    {
        EnsureAllowed(
            DeviceAction.SelectProfile,
            safetyPolicy.Evaluate(DeviceAction.SelectProfile, Snapshot));
        if (profileIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(profileIndex));
        }

        if (runtimeSession?.HasRecovery == true) { await ClearRuntimeSessionAsync(cancellationToken).ConfigureAwait(false); }
        bool transmitted = await WriteAsync(
            LoraxPaths.ActiveProfile,
            new byte[] { (byte)profileIndex },
            cancellationToken).ConfigureAwait(false);
        if (!transmitted)
        {
            return;
        }

        ReadOnlyMemory<byte> readBack = await ReadAsync(
            LoraxPaths.ActiveProfile,
            1,
            cancellationToken).ConfigureAwait(false);
        if (readBack.Span[0] != profileIndex)
        {
            throw new IOException("The device did not confirm the selected profile.");
        }

        // A confirmed hcs write reloads thc from the selected slot. Any prior
        // applied-override proof becomes stale at that moment, even if refresh fails.
        AppliedSessionOverride = null;
        HasUntrackedSessionParameters = false;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
    {
        EnsureAllowed(DeviceAction.UpdateProfile, safetyPolicy.ValidateProfile(profile, Snapshot));
        ReadOnlyMemory<byte> originalLighting = await ReadAllAsync(
            LoraxPaths.ProfileColor(profile.Index), MaximumProfileLightingBytes, cancellationToken).ConfigureAwait(false);
        // Refuse an unsupported color edit before changing any profile setting.
        byte[] updatedLighting = ProfileLightingCodec.PrepareColorUpdate(originalLighting.Span, profile.ColorPalette);
        await WriteAndVerifyAsync(
            LoraxPaths.ProfileName(profile.Index),
            LoraxValueCodec.WriteString(profile.Name),
            cancellationToken).ConfigureAwait(false);
        await WriteAndVerifyAsync(
            LoraxPaths.ProfileTemperature(profile.Index),
            LoraxValueCodec.WriteSingle(profile.TargetTemperatureCelsius),
            cancellationToken).ConfigureAwait(false);
        await WriteAndVerifyAsync(
            LoraxPaths.ProfileTime(profile.Index),
            LoraxValueCodec.WriteSingle(profile.Duration.TotalSeconds),
            cancellationToken).ConfigureAwait(false);
        await WriteAndVerifyAsync(
            LoraxPaths.ProfileBoostTemperature(profile.Index),
            LoraxValueCodec.WriteSingle(profile.BoostTemperatureCelsius),
            cancellationToken).ConfigureAwait(false);
        await WriteAndVerifyAsync(
            LoraxPaths.ProfileBoostTime(profile.Index),
            LoraxValueCodec.WriteSingle(profile.BoostDuration.TotalSeconds),
            cancellationToken).ConfigureAwait(false);
        await WriteProfileLightingAndVerifyAsync(
            profile.Index, originalLighting, updatedLighting, cancellationToken).ConfigureAwait(false);

        // Vapor remains read-only until its layout is captured and hardware-verified.
        DeviceSnapshot refreshed = await ReadTelemetrySnapshotAsync(
            forceProfileRead: true,
            forceBatteryRead: false,
            cancellationToken).ConfigureAwait(false);
        SetSnapshot(refreshed);
    }

    public Task StartSessionAsync(CancellationToken cancellationToken) =>
        StartSessionAsync(expectedProbeParameters: null, cancellationToken);

    private async Task StartSessionAsync(
        SessionStartExpectation? expectedProbeParameters,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(
            DeviceAction.StartSession,
            safetyPolicy.Evaluate(DeviceAction.StartSession, Snapshot));
        await EnsureSessionStartParametersAsync(expectedProbeParameters, cancellationToken).ConfigureAwait(false);
        bool transmitted = await WriteAsync(
            runtimeSession?.Owns(CurrentRuntimeOwner) == true ? LoraxWriteContext.RuntimeSession : LoraxWriteContext.Normal,
            LoraxPaths.ModeCommand, 0,
            new byte[] { (byte)DeviceModeCommand.StartHeatCycle },
            cancellationToken).ConfigureAwait(false);
        if (transmitted)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        EnsureAllowed(
            DeviceAction.StopSession,
            safetyPolicy.Evaluate(DeviceAction.StopSession, Snapshot));
        bool transmitted = await WriteAsync(
            runtimeSession?.Owns(CurrentRuntimeOwner) == true ? LoraxWriteContext.RuntimeSession : LoraxWriteContext.Normal,
            LoraxPaths.ModeCommand, 0,
            new byte[] { (byte)DeviceModeCommand.AbortHeatCycle },
            cancellationToken).ConfigureAwait(false);
        if (transmitted)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task BoostTemperatureAsync(
        double temperatureCelsius,
        CancellationToken cancellationToken) =>
        ApplyBoostAsync(DeviceAction.BoostTemperature, temperatureCelsius, cancellationToken);

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        ApplyBoostAsync(DeviceAction.BoostTime, duration.TotalSeconds, cancellationToken);

    private async Task ApplyBoostAsync(DeviceAction action, double increment, CancellationToken cancellationToken)
    {
        EnsureAllowed(action, safetyPolicy.Evaluate(action, Snapshot));
        if (!double.IsFinite(increment) || increment <= 0)
        {
            EnsureAllowed(action, SafetyDecision.Deny("The boost increment must be positive and finite."));
        }

        DeviceSnapshot live = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        EnsureAllowed(action, safetyPolicy.Evaluate(action, live));
        bool temperature = action == DeviceAction.BoostTemperature;
        string path = temperature ? LoraxPaths.TemperatureOverride : LoraxPaths.TimeOverride;
        double applied = await ReadBoostValueAsync(action, path, cancellationToken).ConfigureAwait(false);
        // Count device button steps, not app requests: tmpo / btmp or timo / btim.
        // The denominator is the profile's configured step, never the requested increment.
        int boostSlot = live.ActiveProfileIndex == 255 && runtimeSession?.Owns(CurrentRuntimeOwner) == true
            ? runtimeSession.OriginalSlot ?? throw new DeviceSafetyException("App-only boost has no verified source slot.")
            : live.ActiveProfileIndex;
        if (boostSlot is < 0 or > 3) { throw new DeviceSafetyException("App-only boost context is untracked; no boost sent."); }
        string stepPath = temperature
            ? LoraxPaths.ProfileBoostTemperature(boostSlot)
            : LoraxPaths.ProfileBoostTime(boostSlot);
        double countIncrement = await ReadBoostValueAsync(action, stepPath, cancellationToken).ConfigureAwait(false);
        // Zero is a legitimate profile setting, but cannot establish a device-step count.
        // Fail closed with an actionable message before division; do not invent a step.
        if (!double.IsFinite(countIncrement) || countIncrement <= 0)
        {
            string reason = $"Cannot boost: the profile's configured {(temperature ? "temperature" : "time")} boost step must be positive. No boost was sent.";
            EnsureAllowed(action, SafetyDecision.Deny(reason));
        }
        double count = Math.Round(applied / countIncrement, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(count) || count > int.MaxValue)
        {
            EnsureAllowed(action, SafetyDecision.Deny("The device boost count cannot be determined."));
        }
        double limit = temperature ? live.Limits!.MaximumBoostTemperatureCelsius : live.Limits!.MaximumBoostDuration.TotalSeconds;
        // Clamp the last increment to the cumulative ceiling. Round down when f32
        // encoding would exceed that ceiling; refuse if no representable increase remains.
        float total = (float)Math.Min(applied + increment, limit);
        if (total > limit) { total = MathF.BitDecrement(total); }
        if (total <= applied) { EnsureAllowed(action, SafetyDecision.Deny("The cumulative boost ceiling has been reached.")); }
        double effectiveIncrement = total - applied;
        if (temperature)
        {
            live = live with
            {
                TargetTemperatureCelsius = await ReadBoostValueAsync(
                action, LoraxPaths.HeaterTargetTemperature, cancellationToken).ConfigureAwait(false)
            };
        }
        EnsureAllowed(action, temperature
            ? safetyPolicy.ValidateTemperatureBoost(live, (int)count, effectiveIncrement, applied)
            : safetyPolicy.ValidateTimeBoost(live, (int)count, TimeSpan.FromSeconds(effectiveIncrement), applied));
        // Recheck state after the reads; a session may have ended while they ran.
        byte state = (await ReadAsync(LoraxPaths.OperatingState, 1, cancellationToken).ConfigureAwait(false)).Span[0];
        EnsureAllowed(action, safetyPolicy.Evaluate(action, live with { OperatingState = MapOperatingState(state) }));
        await WriteAndVerifyAsync(
            runtimeSession?.Owns(CurrentRuntimeOwner) == true && live.ActiveProfileIndex == 255 ? LoraxWriteContext.RuntimeSession : LoraxWriteContext.Normal,
            path, LoraxValueCodec.WriteSingle(total), cancellationToken).ConfigureAwait(false);
        if (!traceWrites) { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
    }

    private async Task<double> ReadBoostValueAsync(DeviceAction action, string path, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> value = await ReadAsync(path, 4, cancellationToken).ConfigureAwait(false);
        if (value.Length != 4) { throw new InvalidDataException("A boost read must contain exactly four bytes."); }
        double number = LoraxValueCodec.ReadSingle(value.Span);
        if (!double.IsFinite(number) || number < 0)
        {
            EnsureAllowed(action, SafetyDecision.Deny("The device returned an invalid boost value."));
        }
        return number;
    }

    public async Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        EnsureAllowed(
            DeviceAction.SetStealthMode,
            safetyPolicy.Evaluate(DeviceAction.SetStealthMode, Snapshot));
        await WriteAndVerifyAsync(
            LoraxPaths.StealthMode,
            new byte[] { enabled ? (byte)1 : (byte)0 },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        EnsureAllowed(
            DeviceAction.SetLanternMode,
            safetyPolicy.Evaluate(DeviceAction.SetLanternMode, Snapshot));
        await WriteAndVerifyAsync(
            LoraxPaths.LanternMode,
            new byte[] { enabled ? (byte)1 : (byte)0 },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        if (runtimeSession?.HasRecovery == true && runtimeSession.Owns(CurrentRuntimeOwner) && Snapshot.OperatingState == DeviceOperatingState.Idle)
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
            try { await ClearRuntimeSessionAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception exception) { diagnosticLog.WriteException("Runtime recovery unconfirmed at disposal", exception); }
        }
        disposed = true;
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> seed = await transport.RunCommandAsync(
            LoraxOpcode.GetAccessSeed,
            ReadOnlyMemory<byte>.Empty,
            16,
            cancellationToken).ConfigureAwait(false);
        if (seed.Length != 16)
        {
            throw new InvalidDataException("The device returned an invalid Lorax access seed.");
        }

        byte[] key = LoraxProtocol.DeriveUnlockKey(seed.Span);
        ReadOnlyMemory<byte> result = await transport.RunCommandAsync(
            LoraxOpcode.UnlockAccess,
            key,
            0,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsEmpty)
        {
            throw new InvalidDataException("The device rejected Lorax authentication.");
        }
    }

    private async Task<DeviceSnapshot> ReadInitialSnapshotAsync(CancellationToken cancellationToken)
    {
        uint modelCode = LoraxValueCodec.ReadUInt32(
            (await ReadAsync(LoraxPaths.ModelCode, 4, cancellationToken).ConfigureAwait(false)).Span);
        string name = LoraxValueCodec.ReadString(
            (await ReadAsync(LoraxPaths.DeviceName, 32, cancellationToken).ConfigureAwait(false)).Span);
        ReadOnlyMemory<byte> firmwareBytes = await ReadAsync(
            LoraxPaths.FirmwareVersion,
            12,
            cancellationToken).ConfigureAwait(false);
        string firmware = LoraxValueCodec.ReadFirmwareIdentity(firmwareBytes.Span);
        connectedIdentity = CompatibilityCatalog.Identify(
            connectedCandidate?.Name ?? transport.AdvertisedName,
            name,
            modelCode,
            firmware,
            serialNumber: null) with
        { FirmwareWireHex = Convert.ToHexString(firmwareBytes.Span) };
        bool isHardwareVerified = CompatibilityCatalog.IsHardwareVerified(connectedIdentity);
        BluetoothDiagnosticMessages.WriteIdentity(
            diagnosticLog,
            connectedIdentity,
            isHardwareVerified);
        connectedCapabilities = CompatibilityCatalog.CapabilitiesFor(connectedIdentity);
        return await ReadTelemetrySnapshotAsync(
            forceProfileRead: true,
            forceBatteryRead: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceSnapshot> ReadTelemetrySnapshotAsync(
        bool forceProfileRead,
        bool forceBatteryRead,
        CancellationToken cancellationToken)
    {
        DeviceIdentity identity = connectedIdentity
            ?? throw new InvalidOperationException("The connected device identity is unavailable.");
        DeviceCapabilities capabilities = connectedCapabilities
            ?? throw new InvalidOperationException("The connected device capabilities are unavailable.");

        double battery = Snapshot.BatteryPercent;
        bool isCharging = Snapshot.IsCharging;
        if (forceBatteryRead || DateTimeOffset.UtcNow - lastBatteryRead >= TimeSpan.FromSeconds(10))
        {
            battery = await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
            byte chargeState = (await ReadAsync(
                LoraxPaths.BatteryChargeState,
                1,
                cancellationToken).ConfigureAwait(false)).Span[0];
            // Both sides confirmed on a PEAKSHI V2, firmware 39, 2026-08-27, by
            // capturing /p/bat/chg/stat in each state:
            //
            //     off the charger   4
            //     on the charger    0
            //
            // So 0 means charging and this expression is right. It was a guess
            // when written and it happened to be correct.
            //
            // 1 remains unobserved. It is kept because a two-value "charging"
            // range is the likelier design, but nothing has ever read it. If a
            // device is ever seen reporting 1, record what it was doing.
            isCharging = chargeState is 0 or 1;
            lastBatteryRead = DateTimeOffset.UtcNow;
        }

        byte chamberValue = (await ReadAsync(
            LoraxPaths.ChamberType,
            1,
            cancellationToken).ConfigureAwait(false)).Span[0];
        byte operatingValue = (await ReadAsync(
            LoraxPaths.OperatingState,
            1,
            cancellationToken).ConfigureAwait(false)).Span[0];
        int activeProfile = (await ReadAsync(
            LoraxPaths.ActiveProfile,
            1,
            cancellationToken).ConfigureAwait(false)).Span[0];
        bool profileChanged = forceProfileRead ||
            Snapshot.Identity is null ||
            activeProfile != Snapshot.ActiveProfileIndex;
        string profileName = Snapshot.ActiveProfileName;
        double targetTemperature = Snapshot.TargetTemperatureCelsius;
        TimeSpan profileDuration = Snapshot.SessionTotal;
        if (profileChanged)
        {
            profileName = LoraxValueCodec.ReadString(
                (await ReadAsync(LoraxPaths.ActiveProfileName, 32, cancellationToken).ConfigureAwait(false)).Span);
            targetTemperature = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ActiveProfileTemperature, 4, cancellationToken).ConfigureAwait(false)).Span);
            double durationSeconds = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.ActiveProfileTime, 4, cancellationToken).ConfigureAwait(false)).Span);
            profileDuration = SecondsOrZero(durationSeconds);
        }

        double currentTemperature = LoraxValueCodec.ReadSingle(
            (await ReadAsync(LoraxPaths.HeaterTemperature, 4, cancellationToken).ConfigureAwait(false)).Span);
        double totalSeconds = LoraxValueCodec.ReadSingle(
            (await ReadAsync(LoraxPaths.StateTotalTime, 4, cancellationToken).ConfigureAwait(false)).Span);
        double elapsedSeconds = LoraxValueCodec.ReadSingle(
            (await ReadAsync(LoraxPaths.StateElapsedTime, 4, cancellationToken).ConfigureAwait(false)).Span);
        bool verified = CompatibilityCatalog.IsHardwareVerified(identity);
        DeviceLimits? limits = CompatibilityCatalog.LimitsFor(identity);
        DeviceOperatingState operatingState = MapOperatingState(operatingValue);
        TimeSpan stateTotal = SecondsOrZero(totalSeconds);

        SessionVaporMode? runtimeVapor = null;
        if (capabilities.SupportsSessionVaporControl && (activeProfile == 255 || AppliedSessionOverride?.VaporMode is not null))
        {
            ReadOnlyMemory<byte> intensity = await ReadAsync("/p/app/thc/intn", 4, cancellationToken).ConfigureAwait(false);
            runtimeVapor = RuntimeVaporCodec.Decode(intensity.Span);
        }
        return new DeviceSnapshot(
            verified
                ? DeviceConnectionState.ConnectedControlEnabled
                : DeviceConnectionState.ConnectedReadOnly,
            identity,
            limits,
            capabilities,
            MapChamber(chamberValue),
            operatingState,
            activeProfile,
            profileName,
            VaporLevel.Standard,
            Math.Clamp(battery, 0, 100),
            isCharging,
            targetTemperature,
            double.IsFinite(currentTemperature) ? currentTemperature : null,
            operatingState is DeviceOperatingState.Preheating or DeviceOperatingState.Active
                ? stateTotal
                : profileDuration,
            SecondsOrZero(elapsedSeconds),
            true,
            verified,
            verified ? null : "Read-only until this exact firmware passes hardware safety verification.")
        { RuntimeVaporMode = runtimeVapor };
    }

    /// <summary>
    /// Turns a duration the device reported into a <see cref="TimeSpan"/>, or
    /// zero when the value cannot be one.
    /// </summary>
    /// <remarks>
    /// A four byte read that does not decode to a sensible float yields a
    /// number no <see cref="TimeSpan"/> can hold, and
    /// <see cref="TimeSpan.FromSeconds(double)"/> throws rather than
    /// saturating. That exception escaped snapshot building, stopped the
    /// refresh, and left the interface frozen on stale values until the device
    /// was reconnected — which read as three separate faults and was one.
    ///
    /// <c>Math.Max(value, 0)</c> did not help: it returns NaN when given NaN,
    /// and does nothing about a value that is merely enormous. Temperature was
    /// already guarded with <c>double.IsFinite</c> a few lines below. These
    /// three were not.
    /// </remarks>
    private static TimeSpan SecondsOrZero(double seconds) =>
        double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaximumReportedDurationSeconds))
            : TimeSpan.Zero;

    /// <summary>
    /// A day. Generous for a device whose cycles run in seconds, and low enough
    /// that nothing it reports can overflow a <see cref="TimeSpan"/>.
    /// </summary>
    private const double MaximumReportedDurationSeconds = 86_400;

    private async Task<double> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        if (supportedBatteryPath is not null)
        {
            return LoraxValueCodec.ReadSingle(
                (await ReadAsync(supportedBatteryPath, 4, cancellationToken).ConfigureAwait(false)).Span);
        }

        // State of charge first, and capacity only as a fallback.
        //
        // These were the other way round, which looked harmless because both
        // paths read cleanly. On a PEAKSHI V2, /p/bat/cap answers 6018.443 —
        // the pack's capacity, a fixed property of the hardware and not a
        // charge level at all. The caller clamps to 0..100, so that pinned the
        // reading at 100% forever and the battery appeared not to update.
        // /p/bat/soc on the same device reads 64.679, which is the percentage
        // actually wanted. Measured 2026-08-27.
        try
        {
            double stateOfCharge = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.BatteryStateOfCharge, 4, cancellationToken).ConfigureAwait(false)).Span);
            supportedBatteryPath = LoraxPaths.BatteryStateOfCharge;
            return stateOfCharge;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
        {
            diagnosticLog.WriteException("Read battery state of charge", exception);
            double capacity = LoraxValueCodec.ReadSingle(
                (await ReadAsync(LoraxPaths.BatteryCapacity, 4, cancellationToken).ConfigureAwait(false)).Span);
            supportedBatteryPath = LoraxPaths.BatteryCapacity;
            return capacity;
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadAsync(
        string path,
        ushort size,
        CancellationToken cancellationToken) =>
        await ReadAsync(path, offset: 0, size, cancellationToken).ConfigureAwait(false);

    private async Task<ReadOnlyMemory<byte>> ReadAsync(
        string path,
        ushort offset,
        ushort size,
        CancellationToken cancellationToken)
    {
        byte[] body = LoraxProtocol.BuildReadBody(path, offset, size);
        return await transport.RunCommandAsync(
            LoraxOpcode.ReadShort,
            body,
            size,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReadOnlyMemory<byte>> ReadAllAsync(
        string path,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 125;
        if (maximumLength is < 1 or > MaximumProfileLightingBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        using MemoryStream output = new(capacity: Math.Min(maximumLength, chunkSize));
        while (output.Length < maximumLength)
        {
            int requested = Math.Min(chunkSize, maximumLength - checked((int)output.Length));
            ReadOnlyMemory<byte> chunk = await ReadAsync(
                path,
                checked((ushort)output.Length),
                checked((ushort)requested),
                cancellationToken).ConfigureAwait(false);
            if (chunk.Length > requested)
            {
                throw new InvalidDataException("A Lorax chunk exceeded its requested size.");
            }

            if (chunk.IsEmpty)
            {
                break;
            }

            output.Write(chunk.Span);
            if (chunk.Length < requested)
            {
                break;
            }
        }

        diagnosticLog.Write(
            $"READ ASSEMBLED path=\"{path}\" totalLength={output.Length}");
        if (output.Length == maximumLength)
        {
            throw new InvalidDataException("A Lorax value reached its bounded read limit.");
        }

        return output.ToArray();
    }

    private async Task<bool> WriteAsync(
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken) =>
        await WriteAsync(path, offset: 0, value, cancellationToken).ConfigureAwait(false);

    private async Task<bool> WriteAsync(
        string path,
        ushort offset,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
        => await WriteAsync(LoraxWriteContext.Normal, path, offset, value, cancellationToken).ConfigureAwait(false);

    private async Task<bool> WriteAsync(
        LoraxWriteContext context,
        string path,
        ushort offset,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        byte flags = context == LoraxWriteContext.RuntimeSession ? (byte)0x04 : (byte)0;
        byte[] body = LoraxProtocol.BuildWriteBody(context, path, offset, flags, value.Span);
        ReadOnlyMemory<byte> response = await transport.RunCommandAsync(
            LoraxOpcode.WriteShort,
            body,
            0,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsEmpty)
        {
            throw new InvalidDataException("The device returned an unexpected write response.");
        }

        return !traceWrites;
    }

    private async Task WriteProfileLightingAndVerifyAsync(
        int profileIndex,
        ReadOnlyMemory<byte> original,
        ReadOnlyMemory<byte> updated,
        CancellationToken cancellationToken)
    {
        if (original.Span.SequenceEqual(updated.Span))
        {
            return;
        }

        string path = LoraxPaths.ProfileColor(profileIndex);
        ReadOnlyMemory<byte> current = await ReadAllAsync(
            path, MaximumProfileLightingBytes, cancellationToken).ConfigureAwait(false);
        if (!current.Span.SequenceEqual(original.Span))
        {
            throw new IOException("The device colorway changed while updating the profile. Reload it before editing colors.");
        }

        // Equal-length payload patches never shorten the CBOR file or overwrite
        // its headers/metadata. Twelve bytes is at most the supported four RGB
        // stops and stays below the existing 128-byte protocol admission limit.
        const int chunkSize = 12;
        bool allTransmitted = true;
        for (int offset = 0; offset < updated.Length;)
        {
            if (original.Span[offset] == updated.Span[offset])
            {
                offset++;
                continue;
            }

            int length = 1;
            while (length < chunkSize && offset + length < updated.Length &&
                original.Span[offset + length] != updated.Span[offset + length])
            {
                length++;
            }

            bool transmitted = await WriteAsync(
                path,
                checked((ushort)offset),
                updated.Slice(offset, length),
                cancellationToken).ConfigureAwait(false);
            allTransmitted &= transmitted;
            offset += length;
        }

        if (!allTransmitted)
        {
            return;
        }

        ReadOnlyMemory<byte> confirmed = await ReadAllAsync(
            path,
            maximumLength: MaximumProfileLightingBytes,
            cancellationToken).ConfigureAwait(false);
        if (!confirmed.Span.SequenceEqual(updated.Span))
        {
            throw new IOException("The device did not confirm the complete profile lighting document; colors or metadata differ.");
        }
    }

    private async Task WriteAndVerifyAsync(
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
        => await WriteAndVerifyAsync(LoraxWriteContext.Normal, path, value, cancellationToken).ConfigureAwait(false);

    private async Task<bool> WriteAndVerifyAsync(
        LoraxWriteContext context,
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        bool transmitted = await WriteAsync(context, path, 0, value, cancellationToken).ConfigureAwait(false);
        if (!transmitted)
        {
            diagnosticLog.Write(
                $"WRITE VERIFICATION path=\"{path}\" result=skipped reason=trace-write-suppressed");
            return false;
        }

        if (PathsWithoutReadBackVerification.Contains(path))
        {
            diagnosticLog.Write(
                $"WRITE VERIFICATION path=\"{path}\" result=skipped reason=read-back-unsupported");
            return false;
        }

        ReadOnlyMemory<byte> confirmed = await ReadAsync(
            path,
            checked((ushort)value.Length),
            cancellationToken).ConfigureAwait(false);
        if (!confirmed.Span.SequenceEqual(value.Span))
        {
            diagnosticLog.Write(
                $"WRITE VERIFICATION path=\"{path}\" result=failed " +
                $"expectedLength={value.Length} actualLength={confirmed.Length}");
            throw new IOException("The device did not confirm a settings write.");
        }

        diagnosticLog.Write(
            $"WRITE VERIFICATION path=\"{path}\" result=verified length={confirmed.Length}");
        return true;
    }

    private void SetSnapshot(DeviceSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!transport.IsConnected || !Snapshot.IsAuthenticated)
        {
            throw new IOException("The device is not connected and authenticated.");
        }
    }

    private void EnsureAllowed(DeviceAction action, SafetyDecision decision)
    {
        if (!decision.IsAllowed)
        {
            diagnosticLog.Write(
                $"WRITE BLOCKED action={action} reason=\"{decision.Reason}\"");
        }

        decision.ThrowIfDenied();
    }

    /// <summary>
    /// Maps the byte at <c>/p/htr/chmt</c> to a chamber.
    /// </summary>
    /// <remarks>
    /// <c>2 =&gt; ThreeDXL</c> is **confirmed on hardware**: a PEAKSHI V2 with a
    /// 3DXL fitted reads 2, verified 2026-08-27. Do not "correct" it.
    ///
    /// The other four arms are still assumptions carried over from the original
    /// implementation. In particular nothing has ever read this path with the
    /// chamber removed, so <c>0 =&gt; None</c> is unverified and it is not known
    /// whether this path reports presence at all.
    /// </remarks>
    private static ChamberKind MapChamber(byte value) => value switch
    {
        0 => ChamberKind.None,
        1 => ChamberKind.Classic,
        2 => ChamberKind.ThreeDXL,
        3 => ChamberKind.ThreeD,
        _ => ChamberKind.Unknown,
    };

    private static DeviceOperatingState MapOperatingState(byte value) =>
        Enum.IsDefined(typeof(DeviceOperatingState), (int)value)
            ? (DeviceOperatingState)value
            : DeviceOperatingState.Unknown;

    private static string SanitizeFault(string fault)
    {
        const int maximumLength = 240;
        string oneLine = fault.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= maximumLength ? oneLine : oneLine[..maximumLength];
    }

    private static string FormatDiagnosticMetadata(string? value)
    {
        if (value is null)
        {
            return "-";
        }

        string oneLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('"', '\'');
        return $"\"{oneLine}\"";
    }
}
