using PadHotkey.Core;
using PadHotkey.Windows;

namespace PadHotkey;

public sealed record EventEntry(long Sequence, DateTimeOffset Time, string Message);
public sealed record RuntimeUpdate
{
    public required bool Paused { get; init; }
}
public sealed record DiagnosticRequest(string[] Keys, string Mode = "tap", int DurationMs = 1000, int DelayMs = 1000);

public sealed class DryKeyboard : IKeyboardOutput
{
    public string[] Keys { get; private set; } = [];
    public void SetKeys(IReadOnlyCollection<string> keys) => Keys = keys.Order().ToArray();
    public void Dispose() => Keys = [];
}

public sealed class Runtime : IDisposable
{
    private readonly object gate = new();
    private readonly Storage storage;
    private readonly IKeyboardOutput output;
    private readonly MappingEngine engine;
    private readonly Queue<EventEntry> events = new();
    private readonly int index;
    private readonly string backend;
    private Profile profile;
    private InputSnapshot last = new(false, new Dictionary<string, double>(), "");
    private long sequence;
    private string? fault;
    private MappingEngine? diagnostic;
    private DateTimeOffset diagnosticStart, diagnosticEnd;
    private string? diagnosticId;
    private string? diagnosticState;
    private bool disposed;

    public Runtime(Storage storage, string backend, int index, bool paused = false)
    {
        this.storage = storage; this.backend = backend; this.index = index;
        profile = storage.Load();
        output = backend == "hid" ? new HidKeyboardOutput() : new DryKeyboard();
        engine = new MappingEngine(profile, output);
        engine.Event += AddEvent;
        engine.SetPaused(paused);
        AddEvent(paused ? "started-paused" : "started-active-neutral-required");
    }

    private void AddEvent(string message)
    {
        lock (gate)
        {
            events.Enqueue(new(++sequence, DateTimeOffset.UtcNow, message));
            while (events.Count > 200) events.Dequeue();
        }
    }

    public Profile GetProfile() { lock (gate) return Storage.Clone(profile); }
    public EventEntry[] Events(long after = 0) { lock (gate) return events.Where(e => e.Sequence > after).ToArray(); }
    public object Status()
    {
        lock (gate) return new
        {
            version = "0.1.2", backend, index, paused = engine.Paused,
            connected = last.Connected, foregroundProcess = last.ForegroundProcess,
            inputs = last.Values, profile = profile.Name, revision = profile.Version, fault,
            diagnostic = diagnosticId == null ? null : new { id = diagnosticId, state = diagnosticState },
            dryRunKeys = (output as DryKeyboard)?.Keys
        };
    }

    public Profile Update(Profile next, int expected)
    {
        lock (gate)
        {
            if (profile.Version != expected) throw new RevisionException();
            if (diagnostic != null) throw new InvalidOperationException("A diagnostic is running.");
            var candidate = Storage.Clone(next);
            candidate.Version = checked(profile.Version + 1);
            ProfileValidator.Validate(candidate);
            storage.Save(candidate);
            try { engine.ReplaceProfile(candidate); }
            catch (Exception ex)
            {
                try { storage.Save(profile); }
                catch (Exception rollback) { Fail(new AggregateException("Apply and persistence rollback failed; inspect profile.json before restarting.", ex, rollback)); throw; }
                Fail(ex); throw;
            }
            profile = candidate;
            AddEvent($"profile-applied revision={profile.Version}");
            return Storage.Clone(profile);
        }
    }

    public Profile UpdateBinding(string id, Binding binding, int expected)
    {
        lock (gate)
        {
            var next = Storage.Clone(profile);
            binding.Id = id;
            var found = next.Bindings.FindIndex(b => b.Id == id);
            if (found < 0) next.Bindings.Add(binding); else next.Bindings[found] = binding;
            return Update(next, expected);
        }
    }

    public void Pause(bool paused)
    {
        lock (gate)
        {
            if (!paused && diagnostic != null) throw new InvalidOperationException("A diagnostic is running.");
            if (!paused && fault != null) throw new InvalidOperationException("Output fault; restart after resolving it.");
            try
            {
                if (paused) CancelDiagnostic();
                engine.SetPaused(paused);
                if (paused) engine.ReleaseAll();
            }
            catch (Exception ex) { Fail(ex); throw; }
            AddEvent(paused ? "paused-and-released" : "resumed-neutral-required");
        }
    }

    public string Diagnose(DiagnosticRequest request)
    {
        lock (gate)
        {
            if (!engine.Paused || diagnostic != null || fault != null) throw new InvalidOperationException("Diagnostics require a paused, healthy, idle runtime.");
            if (request.DurationMs is < 100 or > 15000 || request.DelayMs is < 100 or > 15000) throw new ArgumentException("Duration/delay must be 100..15000 ms.");
            if (request.Mode == "tap" && request.DurationMs < 400) throw new ArgumentException("Tap diagnostics need at least 400 ms.");
            var p = new Profile { Name = "diagnostic", Bindings = [new Binding { Id = "probe", Input = "B", Mode = request.Mode, Keys = request.Keys }] };
            ProfileValidator.Validate(p);
            diagnostic = new MappingEngine(p, output);
            diagnostic.Event += AddEvent;
            diagnostic.Tick(new(true, new Dictionary<string, double>(), last.ForegroundProcess), DateTimeOffset.UtcNow);
            diagnosticStart = DateTimeOffset.UtcNow.AddMilliseconds(request.DelayMs);
            diagnosticEnd = diagnosticStart.AddMilliseconds(request.DurationMs);
            diagnosticId = Guid.NewGuid().ToString("N"); diagnosticState = "scheduled";
            AddEvent($"diagnostic-scheduled {diagnosticId}");
            return diagnosticId;
        }
    }

    private void CancelDiagnostic()
    {
        if (diagnostic == null) return;
        var ending = diagnostic;
        diagnostic = null;
        try { ending.ReleaseAll(); }
        catch { diagnosticState = "failed"; throw; }
        // Output belongs to Runtime; disposing a temporary engine may dispose it.
        diagnosticState = "completed";
        AddEvent("diagnostic-ended-keys-released");
    }

    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                last = WindowsInput.Read(index);
                if (fault != null) return;
                var now = DateTimeOffset.UtcNow;
                if (diagnostic != null)
                {
                    if (now >= diagnosticEnd) CancelDiagnostic();
                    else
                    {
                        bool down = now >= diagnosticStart;
                        diagnosticState = down ? "running" : "scheduled";
                        diagnostic.Tick(new(true, new Dictionary<string, double> { ["B"] = down ? 1 : 0 }, last.ForegroundProcess), now);
                    }
                }
                else if (fault == null) engine.Tick(last, now);
            }
            catch (Exception ex) { Fail(ex); }
        }
    }

    private void Fail(Exception ex)
    {
        fault ??= ex.GetType().Name + ": " + ex.Message;
        try { CancelDiagnostic(); } catch { }
        try { engine.SetPaused(true); } catch { }
        try { engine.ReleaseAll(); } catch { }
        try { output.SetKeys(Array.Empty<string>()); } catch { }
        AddEvent("fault: " + fault);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { CancelDiagnostic(); engine.ReleaseAll(); } finally { output.Dispose(); }
        }
    }
}

public sealed class RevisionException : Exception;
