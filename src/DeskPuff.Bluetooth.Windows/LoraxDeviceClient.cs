using System.Buffers.Binary;
using DeskPuff.Bluetooth.Windows.Compatibility;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows;

public sealed class LoraxDeviceClient : IDeviceClient
{
    private readonly ILoraxTransport transport;
    private readonly DeviceSafetyPolicy safetyPolicy = new();
    private DeviceCandidate? connectedCandidate;
    private DeviceIdentity? connectedIdentity;
    private DeviceCapabilities? connectedCapabilities;
    private DateTimeOffset lastBatteryRead = DateTimeOffset.MinValue;
    private string? supportedBatteryPath;
    private bool disposed;

    public LoraxDeviceClient()
        : this(new SidecarLoraxTransport())
    {
    }

    internal LoraxDeviceClient(ILoraxTransport transport)
    {
        this.transport = transport;
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
            SetSnapshot(snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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
        await transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        connectedCandidate = null;
        connectedIdentity = null;
        connectedCapabilities = null;
        lastBatteryRead = DateTimeOffset.MinValue;
        supportedBatteryPath = null;
        SetSnapshot(DeviceSnapshot.Disconnected);
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
            try
            {
                ReadOnlyMemory<byte> lighting = await ReadAllAsync(
                    LoraxPaths.ProfileColor(index),
                    maximumLength: 512,
                    cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string> decodedPalette = ProfileLightingCodec.DecodeColors(lighting.Span);
                hasDeviceColor = decodedPalette.Count > 0;
                colorPalette = hasDeviceColor
                    ? decodedPalette.ToArray()
                    : [defaultColors[index]];
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException)
            {
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
            });
        }

        return profiles;
    }

    public async Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
    {
        safetyPolicy.Evaluate(DeviceAction.SelectProfile, Snapshot).ThrowIfDenied();
        if (profileIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(profileIndex));
        }

        await WriteAsync(
            LoraxPaths.ActiveProfile,
            new byte[] { (byte)profileIndex },
            cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> readBack = await ReadAsync(
            LoraxPaths.ActiveProfile,
            1,
            cancellationToken).ConfigureAwait(false);
        if (readBack.Span[0] != profileIndex)
        {
            throw new IOException("The device did not confirm the selected profile.");
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
    {
        safetyPolicy.ValidateProfile(profile, Snapshot).ThrowIfDenied();
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
        await WriteProfileLightingAndVerifyAsync(profile, cancellationToken).ConfigureAwait(false);

        // Vapor remains read-only until its layout is captured and hardware-verified.
        DeviceSnapshot refreshed = await ReadTelemetrySnapshotAsync(
            forceProfileRead: true,
            forceBatteryRead: false,
            cancellationToken).ConfigureAwait(false);
        SetSnapshot(refreshed);
    }

    public async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        safetyPolicy.Evaluate(DeviceAction.StartSession, Snapshot).ThrowIfDenied();
        await WriteAsync(
            LoraxPaths.ModeCommand,
            new byte[] { (byte)DeviceModeCommand.StartHeatCycle },
            cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        safetyPolicy.Evaluate(DeviceAction.StopSession, Snapshot).ThrowIfDenied();
        await WriteAsync(
            LoraxPaths.ModeCommand,
            new byte[] { (byte)DeviceModeCommand.AbortHeatCycle },
            cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task BoostTemperatureAsync(
        double temperatureCelsius,
        CancellationToken cancellationToken)
    {
        safetyPolicy.ValidateTemperatureBoost(Snapshot, 0, temperatureCelsius).ThrowIfDenied();
        throw new DeviceSafetyException(
            "Independent temperature boost awaits hardware validation for this firmware.");
    }

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        safetyPolicy.ValidateTimeBoost(Snapshot, 0, duration).ThrowIfDenied();
        throw new DeviceSafetyException(
            "Independent time boost awaits hardware validation for this firmware.");
    }

    public async Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        safetyPolicy.Evaluate(DeviceAction.SetStealthMode, Snapshot).ThrowIfDenied();
        await WriteAndVerifyAsync(
            LoraxPaths.StealthMode,
            new byte[] { enabled ? (byte)1 : (byte)0 },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        safetyPolicy.Evaluate(DeviceAction.SetLanternMode, Snapshot).ThrowIfDenied();
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
        string firmware = firmwareBytes.IsEmpty
            ? "Unknown"
            : LoraxValueCodec.RevisionNumberToString(firmwareBytes.Span[0]);
        connectedIdentity = CompatibilityCatalog.Identify(
            connectedCandidate?.Name ?? transport.AdvertisedName,
            name,
            modelCode,
            firmware,
            serialNumber: null);
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
        DeviceOperatingState operatingState = MapOperatingState(operatingValue);
        TimeSpan stateTotal = SecondsOrZero(totalSeconds);

        return new DeviceSnapshot(
            verified
                ? DeviceConnectionState.ConnectedControlEnabled
                : DeviceConnectionState.ConnectedReadOnly,
            identity,
            null,
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
            verified ? null : "Read-only until this exact firmware passes hardware safety verification.");
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
        if (maximumLength is < 1 or > 1024)
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

        if (output.Length == maximumLength)
        {
            throw new InvalidDataException("A Lorax value reached its bounded read limit.");
        }

        return output.ToArray();
    }

    private async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken) =>
        await WriteAsync(path, offset: 0, value, cancellationToken).ConfigureAwait(false);

    private async Task WriteAsync(
        string path,
        ushort offset,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        byte[] body = LoraxProtocol.BuildWriteBody(path, offset, 0, value.Span);
        ReadOnlyMemory<byte> response = await transport.RunCommandAsync(
            LoraxOpcode.WriteShort,
            body,
            0,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsEmpty)
        {
            throw new InvalidDataException("The device returned an unexpected write response.");
        }
    }

    private async Task WriteProfileLightingAndVerifyAsync(
        HeatProfile profile,
        CancellationToken cancellationToken)
    {
        byte[] encoded = ProfileLightingCodec.EncodeSolid(profile.ColorPalette);
        const int chunkSize = 80;
        for (int offset = 0; offset < encoded.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, encoded.Length - offset);
            await WriteAsync(
                LoraxPaths.ProfileColor(profile.Index),
                checked((ushort)offset),
                encoded.AsMemory(offset, length),
                cancellationToken).ConfigureAwait(false);
        }

        ReadOnlyMemory<byte> confirmed = await ReadAllAsync(
            LoraxPaths.ProfileColor(profile.Index),
            maximumLength: 512,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> confirmedColors = ProfileLightingCodec.DecodeColors(confirmed.Span);
        if (!confirmedColors.SequenceEqual(profile.ColorPalette, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("The device did not confirm the profile color palette.");
        }
    }

    private async Task WriteAndVerifyAsync(
        string path,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        await WriteAsync(path, value, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> confirmed = await ReadAsync(
            path,
            checked((ushort)value.Length),
            cancellationToken).ConfigureAwait(false);
        if (!confirmed.Span.SequenceEqual(value.Span))
        {
            throw new IOException("The device did not confirm a settings write.");
        }
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
}
