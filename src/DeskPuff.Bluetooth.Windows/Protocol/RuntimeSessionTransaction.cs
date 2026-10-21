using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Protocol;

/// <summary>
/// One in-memory recovery obligation. Its opaque owner is never logged or persisted.
/// The caller serializes this entire transaction and supplies fresh idle checks.
/// </summary>
internal sealed class RuntimeSessionTransaction(
    Func<string, int, CancellationToken, Task<ReadOnlyMemory<byte>>> read,
    Func<string, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> write,
    Func<CancellationToken, Task> requireIdle,
    DeviceLimits? limits = null)
{
    private static readonly string[] RuntimeFields = ["time", "temp", "intn"];
    private static readonly string[] OpaqueFields = ["name", "colr"];
    private OriginalState? original;
    private SessionOverride? applied;
    private bool recoveryAttempted;
    private bool contextWasApplied;
    private bool restorationStarted;
    private readonly Dictionary<string, byte[]> sentRuntime = new(StringComparer.Ordinal);
    internal bool HasRecovery => original is not null;
    internal void InvalidateApplication() { applied = null; recoveryAttempted = true; }
    internal int? OriginalSlot => original?.Selection[0];
    internal bool Owns(string owner) => original is not null && string.Equals(original.Owner, owner, StringComparison.Ordinal);

    internal async Task ApplyAsync(string owner, SessionOverride request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (original is not null) { throw new InvalidOperationException("Restore the previous session before applying another."); }
        SessionVaporMode mode = request.VaporMode ?? throw new InvalidOperationException("Choose a named vapor mode explicitly.");
        byte[] intensity = RuntimeVaporCodec.Encode(mode);
        await requireIdle(cancellationToken).ConfigureAwait(false);
        byte[] selection = (await read(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false)).ToArray();
        if (selection.Length != 1 || selection[0] > 3)
        {
            throw new InvalidDataException("Original context is app-only or unknown; select a device profile explicitly before applying a saved session.");
        }
        Dictionary<string, byte[]> runtime = new(StringComparer.Ordinal);
        foreach (string field in RuntimeFields)
        {
            string path = $"/p/app/thc/{field}";
            runtime[path] = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
        }
        double originalTime = LoraxValueCodec.ReadSingle(runtime[LoraxPaths.ActiveProfileTime]);
        double originalTemperature = LoraxValueCodec.ReadSingle(runtime[LoraxPaths.ActiveProfileTemperature]);
        bool knownIntensity = Enum.GetValues<SessionVaporMode>().Any(value => runtime["/p/app/thc/intn"].AsSpan().SequenceEqual(RuntimeVaporCodec.Encode(value)));
        if (originalTime < (float)Math.Max(10, limits?.MinimumDuration.TotalSeconds ?? 10) || originalTime > (float)Math.Min(120, limits?.MaximumDuration.TotalSeconds ?? 120) ||
            originalTemperature < (float)Math.Max(190, limits?.MinimumTemperatureCelsius ?? 190) || originalTemperature > (float)Math.Min(327, limits?.MaximumTemperatureCelsius ?? 327) || !knownIntensity)
        {
            throw new InvalidDataException("Original runtime parameters cannot be safely restored; no session writes attempted.");
        }
        Dictionary<string, byte[]> opaque = new(StringComparer.Ordinal);
        foreach (string field in OpaqueFields)
        {
            string path = $"/p/app/thc/{field}";
            opaque[path] = (await read(path, field == "colr" ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
        }
        Dictionary<string, byte[]> slots = new(StringComparer.Ordinal);
        for (int slot = 0; slot < 4; slot++)
        {
            foreach (string field in RuntimeFields)
            {
                string path = $"/u/app/hc/{slot}/{field}";
                slots[path] = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
            }
            foreach (string field in OpaqueFields)
            {
                string path = $"/u/app/hc/{slot}/{field}";
                slots[path] = (await read(path, field == "colr" ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
            }
        }
        foreach (string field in OpaqueFields)
        {
            byte[] slotValue = (await read($"/u/app/hc/{selection[0]}/{field}", field == "colr" ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
            if (!slotValue.AsSpan().SequenceEqual(opaque[$"/p/app/thc/{field}"]))
            {
                throw new InvalidDataException("Runtime name/color differs from its slot; exact restoration cannot be established without overwriting it.");
            }
        }
        Dictionary<string, byte[]> boosts = new(StringComparer.Ordinal);
        foreach (string path in new[] { LoraxPaths.TemperatureOverride, LoraxPaths.TimeOverride })
        {
            byte[] value = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
            boosts[path] = value;
            if (LoraxValueCodec.ReadSingle(value) != 0)
            {
                throw new InvalidDataException("Existing boost offsets must be cleared on the device before a saved session can be applied.");
            }
        }
        await requireIdle(cancellationToken).ConfigureAwait(false);
        byte[] currentSelection = (await read(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false)).ToArray();
        if (!currentSelection.AsSpan().SequenceEqual(selection)) { throw new InvalidDataException("Device selection changed during session preflight."); }
        foreach ((string path, byte[] value) in runtime)
        {
            await VerifyAsync(path, value, cancellationToken).ConfigureAwait(false);
        }
        OriginalState captured = new(owner, selection, runtime, opaque, slots, boosts);
        await VerifySlotsAsync(captured, cancellationToken).ConfigureAwait(false);
        original = captured;
        recoveryAttempted = false;
        contextWasApplied = false;
        restorationStarted = false;
        // Record recovery before the first frame: a lost reply can still mean a write acted.
        await WriteVerifiedAsync(LoraxPaths.ActiveProfileTime, LoraxValueCodec.WriteSingle(request.Duration.TotalSeconds), cancellationToken).ConfigureAwait(false);
        await WriteVerifiedAsync(LoraxPaths.ActiveProfileTemperature, LoraxValueCodec.WriteSingle(request.TargetTemperatureCelsius), cancellationToken).ConfigureAwait(false);
        await WriteVerifiedAsync("/p/app/thc/intn", intensity, cancellationToken).ConfigureAwait(false);
        await VerifySlotsAsync(original, cancellationToken).ConfigureAwait(false);
        await WriteVerifiedAsync(LoraxPaths.ActiveProfile, new byte[] { 255 }, cancellationToken).ConfigureAwait(false);
        applied = request with { Colors = request.Colors.ToArray() };
        contextWasApplied = true;
        await VerifyAppliedAsync(owner, cancellationToken).ConfigureAwait(false);
    }

    internal async Task VerifyAppliedAsync(string owner, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        if (recoveryAttempted) { throw new InvalidOperationException("Runtime recovery has not completed; starting is blocked."); }
        SessionOverride expected = applied ?? throw new InvalidOperationException("Session application is incomplete; restore originals before starting.");
        await requireIdle(cancellationToken).ConfigureAwait(false);
        await VerifyAsync(LoraxPaths.ActiveProfile, new byte[] { 255 }, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(LoraxPaths.ActiveProfileTime, LoraxValueCodec.WriteSingle(expected.Duration.TotalSeconds), cancellationToken).ConfigureAwait(false);
        await VerifyAsync(LoraxPaths.ActiveProfileTemperature, LoraxValueCodec.WriteSingle(expected.TargetTemperatureCelsius), cancellationToken).ConfigureAwait(false);
        await VerifyAsync("/p/app/thc/intn", RuntimeVaporCodec.Encode(expected.VaporMode!.Value), cancellationToken).ConfigureAwait(false);
        await VerifySlotsAsync(original!, cancellationToken).ConfigureAwait(false);
        foreach ((string path, byte[] value) in original!.Opaque)
        {
            byte[] actual = (await read(path, path.EndsWith("colr", StringComparison.Ordinal) ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
            if (!actual.AsSpan().SequenceEqual(value)) { throw new IOException("Runtime name/color changed; display-only palette was not applied."); }
        }
    }

    internal async Task ClearAsync(string owner, CancellationToken cancellationToken)
    {
        OriginalState state = RequireOwner(owner);
        recoveryAttempted = true;
        await requireIdle(cancellationToken).ConfigureAwait(false);
        await VerifySlotsAsync(state, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> selection = await read(LoraxPaths.ActiveProfile, 1, cancellationToken).ConfigureAwait(false);
        if (selection.Length != 1 || (selection.Span[0] != 255 && !selection.Span.SequenceEqual(state.Selection)))
        {
            throw new InvalidOperationException("External selection replaced the owned session context; no recovery writes attempted.");
        }
        bool allOriginal = true;
        foreach ((string path, byte[] originalValue) in state.Runtime)
        {
            byte[] actual = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
            allOriginal &= actual.AsSpan().SequenceEqual(originalValue);
            bool known = actual.AsSpan().SequenceEqual(originalValue) ||
                (sentRuntime.TryGetValue(path, out byte[]? sent) && actual.AsSpan().SequenceEqual(sent));
            if (!known) { throw new InvalidOperationException("External runtime values changed; recovery ownership is uncertain and no write was attempted."); }
        }
        if (contextWasApplied)
        {
            foreach ((string path, byte[] value) in state.Opaque)
            {
                byte[] actual = (await read(path, path.EndsWith("colr", StringComparison.Ordinal) ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
                if (!actual.AsSpan().SequenceEqual(value)) { throw new InvalidOperationException("External runtime name/color changed; recovery ownership is uncertain."); }
            }
        }
        if (selection.Span.SequenceEqual(state.Selection) && contextWasApplied && !restorationStarted)
        {
            if (!allOriginal) { throw new InvalidOperationException("Original slot was externally reselected with different runtime values; no recovery write attempted."); }
            foreach (string path in new[] { LoraxPaths.TemperatureOverride, LoraxPaths.TimeOverride })
            {
                byte[] value = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
                if (LoraxValueCodec.ReadSingle(value) != 0) { throw new IOException("Original selection has unconfirmed boost state."); }
            }
            original = null;
            applied = null;
            sentRuntime.Clear();
            return;
        }
        applied = null;
        // Restore selection first; its reload may replace runtime fields. Then restore
        // exact scalar originals independently and verify every recorded obligation.
        restorationStarted = true;
        await WriteVerifiedAsync(LoraxPaths.ActiveProfile, state.Selection, cancellationToken).ConfigureAwait(false);
        foreach (string field in RuntimeFields)
        {
            string path = $"/p/app/thc/{field}";
            await WriteVerifiedAsync(path, state.Runtime[path], cancellationToken).ConfigureAwait(false);
        }
        foreach ((string path, byte[] value) in state.Opaque)
        {
            byte[] actual = (await read(path, path.EndsWith("colr", StringComparison.Ordinal) ? 4096 : 32, cancellationToken).ConfigureAwait(false)).ToArray();
            if (!actual.AsSpan().SequenceEqual(value)) { throw new IOException("Original runtime name/color was not restored; no unsupported color write attempted."); }
        }
        foreach ((string path, byte[] originalBoost) in state.Boosts)
        {
            byte[] value = await ReadNumberAsync(path, cancellationToken).ConfigureAwait(false);
            if (!value.AsSpan().SequenceEqual(originalBoost))
            {
                // Restore the captured zero offset, never add a boost during cleanup.
                await WriteVerifiedAsync(path, originalBoost, cancellationToken).ConfigureAwait(false);
            }
        }
        await VerifySlotsAsync(state, cancellationToken).ConfigureAwait(false);
        await VerifyAsync(LoraxPaths.ActiveProfile, state.Selection, cancellationToken).ConfigureAwait(false);
        original = null;
        sentRuntime.Clear();
    }

    private OriginalState RequireOwner(string owner) => original is { } state && string.Equals(state.Owner, owner, StringComparison.Ordinal)
        ? state : throw new InvalidOperationException("Session recovery belongs to a different or unknown device; no restoration write attempted.");

    private async Task<byte[]> ReadNumberAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = (await read(path, 4, cancellationToken).ConfigureAwait(false)).ToArray();
        if (bytes.Length != 4 || !float.IsFinite(LoraxValueCodec.ReadSingle(bytes))) { throw new InvalidDataException("Runtime session field did not return an exact finite float32."); }
        return bytes;
    }

    private async Task VerifySlotsAsync(OriginalState state, CancellationToken cancellationToken)
    {
        foreach ((string path, byte[] value) in state.Slots)
        {
            await VerifyAsync(path, value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteVerifiedAsync(string path, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        await requireIdle(cancellationToken).ConfigureAwait(false);
        if (RuntimeFields.Any(field => path == $"/p/app/thc/{field}")) { sentRuntime[path] = value.ToArray(); }
        if (!await write(path, value, cancellationToken).ConfigureAwait(false)) { throw new IOException("Runtime session write was not transmitted and verified."); }
        await VerifyAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyAsync(string path, ReadOnlyMemory<byte> expected, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> actual = await read(path, expected.Length, cancellationToken).ConfigureAwait(false);
        if (!actual.Span.SequenceEqual(expected.Span)) { throw new IOException($"Runtime session readback mismatch at {path}."); }
    }

    private sealed record OriginalState(string Owner, byte[] Selection, Dictionary<string, byte[]> Runtime, Dictionary<string, byte[]> Opaque, Dictionary<string, byte[]> Slots, Dictionary<string, byte[]> Boosts);
}
