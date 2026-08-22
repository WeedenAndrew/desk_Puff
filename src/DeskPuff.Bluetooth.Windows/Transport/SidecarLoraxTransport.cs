using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Transport;

internal sealed class SidecarLoraxTransport : ILoraxTransport
{
    private const int MaximumResponseCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim rpcGate = new(1, 1);
    private Process? helper;
    private StreamWriter? requestWriter;
    private StreamReader? responseReader;
    private long nextRequestId;
    private ushort nextSequence;
    private bool disposed;

    public bool IsConnected { get; private set; }

    public string AdvertisedName { get; private set; } = string.Empty;

    public async Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        SidecarResponse response = await SendAsync(
            new SidecarRequest
            {
                Operation = "scan",
                DurationMilliseconds = checked((int)duration.TotalMilliseconds),
            },
            cancellationToken).ConfigureAwait(false);
        return response.Candidates?
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Id) &&
                !string.IsNullOrWhiteSpace(candidate.Name))
            .Select(candidate => new DeviceCandidate(
                candidate.Id,
                candidate.Name,
                candidate.SignalStrength))
            .ToArray() ?? [];
    }

    public async Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.PlatformId);
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        SidecarResponse response = await SendAsync(
            new SidecarRequest
            {
                Operation = "connect",
                CandidateId = candidate.PlatformId,
            },
            cancellationToken).ConfigureAwait(false);
        AdvertisedName = string.IsNullOrWhiteSpace(response.AdvertisedName)
            ? candidate.Name
            : response.AdvertisedName;
        IsConnected = true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (helper is { HasExited: false })
        {
            try
            {
                await SendAsync(
                    new SidecarRequest { Operation = "disconnect" },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or
                InvalidDataException or
                OperationCanceledException)
            {
                // A safe disconnect is best-effort and the helper is reset below on transport failure.
            }
        }

        IsConnected = false;
        AdvertisedName = string.Empty;
    }

    public async Task TriggerBondingAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await SendAsync(
            new SidecarRequest { Operation = "triggerBonding" },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReadOnlyMemory<byte>> RunCommandAsync(
        LoraxOpcode opcode,
        ReadOnlyMemory<byte> body,
        int maximumReplyLength,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (body.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        if (maximumReplyLength is < 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReplyLength));
        }

        ushort sequence = nextSequence++;
        byte[] frame = LoraxProtocol.BuildFrame(sequence, opcode, body.Span);
        SidecarResponse response = await SendAsync(
            new SidecarRequest
            {
                Operation = "runCommand",
                FrameBase64 = Convert.ToBase64String(frame),
                ExpectedSequence = sequence,
            },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.FrameBase64))
        {
            throw new InvalidDataException("The Bluetooth helper returned no Lorax reply.");
        }

        byte[] replyBytes;
        try
        {
            replyBytes = Convert.FromBase64String(response.FrameBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Bluetooth helper returned malformed data.", exception);
        }

        LoraxReply reply = LoraxProtocol.ParseReply(replyBytes);
        if (reply.Sequence != sequence)
        {
            throw new InvalidDataException("The Bluetooth helper returned a mismatched Lorax sequence.");
        }

        if (reply.Payload.Length > maximumReplyLength)
        {
            throw new InvalidDataException("Lorax reply exceeded its expected maximum size.");
        }

        return reply.Payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            if (helper is { HasExited: false })
            {
                await SendAsync(
                    new SidecarRequest { Operation = "shutdown" },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
        }
        finally
        {
            StopHelper();
            rpcGate.Dispose();
            disposed = true;
        }
    }

    private async Task<SidecarResponse> SendAsync(
        SidecarRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await rpcGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureHelperStarted();
            long id = Interlocked.Increment(ref nextRequestId);
            SidecarRequest identified = request with { Id = id };
            string json = JsonSerializer.Serialize(identified, SerializerOptions);
            if (json.Length > 4096)
            {
                throw new InvalidDataException("Bluetooth helper request exceeded its size limit.");
            }

            await requestWriter!.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await requestWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            string line = await ReadLineBoundedAsync(responseReader!, cancellationToken).ConfigureAwait(false);
            SidecarResponse response = JsonSerializer.Deserialize<SidecarResponse>(line, SerializerOptions)
                ?? throw new InvalidDataException("Bluetooth helper returned an empty response.");
            if (response.Id != id)
            {
                throw new InvalidDataException("Bluetooth helper response order was invalid.");
            }

            if (!response.Success)
            {
                throw new IOException(SanitizeError(response.Error));
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            StopHelper();
            IsConnected = false;
            AdvertisedName = string.Empty;
            throw;
        }
        catch (Exception exception) when (exception is
            EndOfStreamException or
            InvalidDataException or
            IOException or
            JsonException)
        {
            StopHelper();
            IsConnected = false;
            AdvertisedName = string.Empty;
            throw;
        }
        finally
        {
            rpcGate.Release();
        }
    }

    private static async Task<string> ReadLineBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        char[] character = new char[1];
        while (builder.Length <= MaximumResponseCharacters)
        {
            int read = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The Bluetooth helper exited unexpectedly.");
            }

            if (character[0] == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append(character[0]);
        }

        throw new InvalidDataException("Bluetooth helper response exceeded its size limit.");
    }

    private void EnsureHelperStarted()
    {
        if (helper is { HasExited: false })
        {
            return;
        }

        StopHelper();
        string executablePath = HelperPath();
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The platform Bluetooth helper is missing. Install a complete desk_Puff package for this operating system.",
                executablePath);
        }

        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--stdio");
        helper = Process.Start(startInfo)
            ?? throw new IOException("The platform Bluetooth helper could not be started.");
        helper.ErrorDataReceived += static (_, _) => { };
        helper.BeginErrorReadLine();
        requestWriter = helper.StandardInput;
        responseReader = helper.StandardOutput;
    }

    private void StopHelper()
    {
        requestWriter?.Dispose();
        responseReader?.Dispose();
        requestWriter = null;
        responseReader = null;
        if (helper is not null)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            helper.Dispose();
            helper = null;
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsConnected)
        {
            throw new IOException("The Lorax transport is not connected.");
        }
    }

    private static string HelperPath()
    {
        string executableName = OperatingSystem.IsWindows()
            ? "desk-puff-ble.exe"
            : "desk-puff-ble";
        return Path.Combine(AppContext.BaseDirectory, "ble", executableName);
    }

    private static string SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "The platform Bluetooth helper rejected the request.";
        }

        string oneLine = error.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 240 ? oneLine : oneLine[..240];
    }

    private sealed record SidecarRequest
    {
        public long Id { get; init; }

        public required string Operation { get; init; }

        public int? DurationMilliseconds { get; init; }

        public string? CandidateId { get; init; }

        public string? FrameBase64 { get; init; }

        public ushort? ExpectedSequence { get; init; }
    }

    private sealed record SidecarResponse
    {
        public long Id { get; init; }

        public bool Success { get; init; }

        public string? Error { get; init; }

        public string? AdvertisedName { get; init; }

        public string? FrameBase64 { get; init; }

        public SidecarCandidate[]? Candidates { get; init; }
    }

    private sealed record SidecarCandidate
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public short SignalStrength { get; init; }
    }
}
