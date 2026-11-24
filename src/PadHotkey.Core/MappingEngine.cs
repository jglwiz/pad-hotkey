namespace PadHotkey.Core;

/// <summary>Single-owner deterministic engine. The caller serializes API mutations and ticks.</summary>
public sealed class MappingEngine : IDisposable
{
    private readonly IKeyboardOutput _output;
    private List<State> _states = [];
    private HashSet<string> _lastKeys = new(StringComparer.Ordinal);
    private bool _connected;
    private string _foreground = "";
    private bool _disposed;
    public bool Paused { get; private set; }
    public event Action<string>? Event;

    public MappingEngine(Profile profile, IKeyboardOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ProfileValidator.Validate(profile);
        _output = output;
        Load(profile);
    }

    public void ReplaceProfile(Profile profile)
    {
        ThrowIfDisposed();
        ProfileValidator.Validate(profile); // Invalid updates preserve the active profile and held keys.
        ReleaseAll();
        Load(profile);
        Event?.Invoke("profile-applied");
    }

    public void SetPaused(bool paused)
    {
        ThrowIfDisposed();
        if (Paused == paused) return;
        ReleaseAll();
        Paused = paused;
        Event?.Invoke(paused ? "paused" : "resumed");
    }

    public void ReleaseAll()
    {
        ThrowIfDisposed();
        foreach (var state in _states) state.Reset();
        Send([], force: true);
    }

    public void Tick(InputSnapshot snapshot, DateTimeOffset now)
    {
        ThrowIfDisposed();
        if (!snapshot.Connected)
        {
            if (_connected) { ReleaseAll(); Event?.Invoke("disconnected"); }
            _connected = false;
            return;
        }
        if (!_connected)
        {
            foreach (var state in _states) state.Reset();
            _connected = true;
            Event?.Invoke("connected");
        }
        var foregroundChanged = !string.Equals(_foreground, snapshot.ForegroundProcess, StringComparison.OrdinalIgnoreCase);
        _foreground = snapshot.ForegroundProcess;
        if (Paused) return;
        var winners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stick in new[] { "LS", "RS" })
        {
            var directions = new[] { $"{stick}.Up", $"{stick}.Down", $"{stick}.Left", $"{stick}.Right" };
            winners[stick] = directions.OrderByDescending(d => Value(snapshot, d)).First();
        }
        foreach (var state in _states)
        {
            var b = state.Binding;
            var scoped = b.Processes.Length != 0;
            if ((scoped && foregroundChanged) || !IsEligible(b, snapshot.ForegroundProcess)) state.Reset();
            if (!IsEligible(b, snapshot.ForegroundProcess)) continue;

            var rawValue = Value(snapshot, b.Input);
            var value = rawValue;
            if (b.Input is "LS.Up" or "LS.Down" or "LS.Left" or "LS.Right" or
                "RS.Up" or "RS.Down" or "RS.Left" or "RS.Right")
            {
                if (winners[b.Input[..2]] != b.Input) value = 0;
            }

            // Losing directional dominance may end a hold, but it is not a physical
            // release. Rearming must use the actual axis value to preserve hysteresis.
            if (rawValue <= b.ReleaseThreshold) state.Armed = true;
            if (value <= b.ReleaseThreshold && b.Mode == "hold") state.Active = false;
            if (state.Armed && value >= b.Threshold)
            {
                state.Armed = false;
                if (!state.Active)
                {
                    if (IsStickDirection(b.Input))
                    {
                        // A new direction cancels an unfinished tap on the same stick,
                        // so diagonals cannot combine two unrelated shortcuts.
                        foreach (var other in _states)
                            if (!ReferenceEquals(other, state) && IsStickDirection(other.Binding.Input) &&
                                other.Binding.Input[..2] == b.Input[..2]) other.Active = false;
                    }
                    state.Active = true;
                    state.Phase = 0;
                    state.Due = now.AddMilliseconds(100);
                    Event?.Invoke($"trigger:{b.Id}:{b.Mode}");
                }
            }

            // At most one phase per tick: delayed polls never collapse press/release reports.
            if (state.Active && b.Mode == "tap" && now >= state.Due)
            {
                state.Phase++;
                state.Due = now.AddMilliseconds(100);
                var hasModifiers = b.Keys.Any(KeyCatalog.IsModifier);
                if (state.Phase >= (hasModifiers ? 3 : 1)) state.Active = false;
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in _states.Where(s => s.Active))
        {
            var b = state.Binding;
            if (b.Mode == "hold" || !b.Keys.Any(KeyCatalog.IsModifier) || state.Phase == 1)
                keys.UnionWith(b.Keys);
            else keys.UnionWith(b.Keys.Where(KeyCatalog.IsModifier));
        }
        Send(keys);
    }

    private static bool IsEligible(Binding binding, string process) => binding.Processes.Length == 0 ||
        binding.Processes.Any(p => string.Equals(WithoutExe(p), WithoutExe(process), StringComparison.OrdinalIgnoreCase));
    private static string WithoutExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    private static bool IsStickDirection(string input) => input is
        "LS.Up" or "LS.Down" or "LS.Left" or "LS.Right" or
        "RS.Up" or "RS.Down" or "RS.Left" or "RS.Right";
    private static double Value(InputSnapshot snapshot, string input) =>
        snapshot.Values.TryGetValue(input, out var value) && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private void Send(IEnumerable<string> keys, bool force = false)
    {
        var next = keys.ToHashSet(StringComparer.Ordinal);
        if (!force && _lastKeys.SetEquals(next)) return;
        try
        {
            _ = KeyCatalog.Encode(next);
            _output.SetKeys(next);
            _lastKeys = next;
        }
        catch (Exception ex)
        {
            Paused = true;
            foreach (var state in _states) state.Reset();
            _lastKeys.Clear();
            try { _output.SetKeys(Array.Empty<string>()); } catch { /* Preserve original fault. */ }
            throw new InvalidOperationException("Keyboard output failed; mapping paused.", ex);
        }
    }

    private void Load(Profile profile) => _states = profile.DeepCopy().Bindings.Select(b => new State(b)).ToList();
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        try { ReleaseAll(); }
        finally { _disposed = true; _output.Dispose(); }
    }

    private sealed class State(Binding binding)
    {
        public Binding Binding { get; } = binding;
        public bool Armed;
        public bool Active;
        public int Phase;
        public DateTimeOffset Due;
        public void Reset() { Armed = false; Active = false; Phase = 0; }
    }
}
