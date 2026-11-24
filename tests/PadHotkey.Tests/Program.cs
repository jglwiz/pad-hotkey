using PadHotkey.Core;

var tests = new (string Name, Action Run)[]
{
    ("startup-held blocked until neutral", StartupHeld),
    ("hold tracks release and disconnect", HoldDisconnect),
    ("Ctrl F10 natural ordering and single fire", ChordOrder),
    ("axis hysteresis and direction exclusion", AxisHysteresis),
    ("diagonal dominance changes do not rearm unreleased axes", DiagonalHysteresis),
    ("new stick direction cancels unfinished sibling chord", DirectionCancelsTap),
    ("profile hot swap releases and requires neutral", HotSwap),
    ("invalid profile retains active hold", InvalidUpdate),
    ("pause and resume require neutral", PauseResume),
    ("scope loss releases without re-trigger on return", ScopeChange),
    ("shared modifier remains owned", SharedModifiers),
    ("reconnect-held blocked", Reconnect),
    ("overlapping tap edges never create backlog", NoTapBacklog),
    ("delayed tick preserves chord stages", DelayedTick),
    ("config validates unsafe or unknown fields", Validation),
    ("profile is defensively copied", DefensiveCopy),
    ("output failure pauses and attempts release", OutputFailure),
    ("HID key codes distinguish modifier sides", HidCodes),
    ("explicit JSON example preserves saved right-stick directions", ExampleImport),
    ("new profile has no bindings or output", EmptyProfile)
};
var failed = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static Binding Hold(string id = "b", string input = "B", params string[] keys) =>
    new() { Id = id, Input = input, Mode = "hold", Keys = keys.Length == 0 ? ["RightAlt"] : keys };
static Profile P(params Binding[] bindings) => new() { Bindings = [.. bindings] };
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
static void Throws(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Expected argument rejection");
}
static void StartupHeld()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0, ("B", 1)); f.Tick(50, ("B", 1)); f.Expect("");
    f.Tick(60); f.Tick(70, ("B", 1)); f.Expect("RightAlt");
}
static void HoldDisconnect()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0); f.Tick(10, ("B", 1)); f.Expect("RightAlt");
    f.Tick(20); f.Expect("");
    f.Tick(30, ("B", 1)); f.Disconnect(40); f.Expect("");
}
static void ChordOrder()
{
    using var f = new Fixture(P(Hold() with { Mode = "tap", Keys = ["LeftCtrl", "F10"] }));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Expect("LeftCtrl");
    f.Tick(101, ("B", 1)); f.Expect("F10,LeftCtrl");
    f.Tick(201, ("B", 1)); f.Expect("LeftCtrl");
    f.Tick(301, ("B", 1)); f.Expect("");
    f.Tick(9999, ("B", 1)); Equal(1, f.Triggers);
    f.Tick(10000); f.Tick(10001, ("B", 1)); Equal(2, f.Triggers);
}
static void AxisHysteresis()
{
    using var f = new Fixture(P(Hold("up", "LS.Up", "A"), Hold("right", "LS.Right", "B")));
    f.Tick(0); f.Tick(10, ("LS.Up", .89)); f.Expect("");
    f.Tick(20, ("LS.Up", .91), ("LS.Right", .9)); f.Expect("A");
    f.Tick(30, ("LS.Up", .8)); f.Expect("A"); Equal(1, f.Triggers);
    f.Tick(40, ("LS.Up", .7)); f.Expect("");
    f.Tick(50, ("LS.Up", .95)); f.Expect("A"); Equal(2, f.Triggers);
}
static void HotSwap()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Engine.ReplaceProfile(P(Hold() with { Keys = ["LeftAlt"] }));
    f.Expect(""); f.Tick(2, ("B", 1)); f.Expect("");
    f.Tick(3); f.Tick(4, ("B", 1)); f.Expect("LeftAlt");
}
static void DiagonalHysteresis()
{
    using var f = new Fixture(P(Hold("up", "LS.Up", "A"), Hold("right", "LS.Right", "B")));
    f.Tick(0); f.Tick(1, ("LS.Up", .95), ("LS.Right", .91)); f.Expect("A");
    f.Tick(2, ("LS.Up", .91), ("LS.Right", .95)); f.Expect("B");
    f.Tick(3, ("LS.Up", .95), ("LS.Right", .91)); f.Expect(""); Equal(2, f.Triggers);
    f.Tick(4); f.Tick(5, ("LS.Up", .95)); f.Expect("A");
}
static void DirectionCancelsTap()
{
    using var f = new Fixture(P(
        Hold("up", "LS.Up", "LeftCtrl", "A") with { Mode = "tap" },
        Hold("right", "LS.Right", "LeftShift", "B") with { Mode = "tap" }));
    f.Tick(0); f.Tick(1, ("LS.Up", 1)); f.Tick(101, ("LS.Up", 1)); f.Expect("A,LeftCtrl");
    f.Tick(102, ("LS.Right", 1)); f.Expect("LeftShift");
    f.Tick(202, ("LS.Right", 1)); f.Expect("B,LeftShift");
}
static void InvalidUpdate()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0); f.Tick(1, ("B", 1));
    Throws(() => f.Engine.ReplaceProfile(P(Hold() with { Keys = ["NoSuchKey"] })));
    f.Expect("RightAlt");
}
static void PauseResume()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Engine.SetPaused(true); f.Expect("");
    f.Tick(2); f.Engine.SetPaused(false); f.Tick(3, ("B", 1)); f.Expect("");
    f.Tick(4); f.Tick(5, ("B", 1)); f.Expect("RightAlt");
}
static void ScopeChange()
{
    using var f = new Fixture(P(Hold() with { Processes = ["editor.exe"] }));
    f.Process = "EDITOR"; f.Tick(0); f.Tick(1, ("B", 1)); f.Expect("RightAlt");
    f.Process = "other"; f.Tick(2, ("B", 1)); f.Expect("");
    f.Process = "editor"; f.Tick(3, ("B", 1)); f.Expect("");
    f.Tick(4); f.Tick(5, ("B", 1)); f.Expect("RightAlt");
}
static void SharedModifiers()
{
    using var f = new Fixture(P(Hold("b", "B", "LeftCtrl"), Hold("a", "A", "LeftCtrl", "Z")));
    f.Tick(0); f.Tick(1, ("B", 1), ("A", 1)); f.Expect("LeftCtrl,Z");
    f.Tick(2, ("B", 1)); f.Expect("LeftCtrl");
    f.Tick(3); f.Expect("");
}
static void Reconnect()
{
    using var f = new Fixture(P(Hold()));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Disconnect(2);
    f.Tick(3, ("B", 1)); f.Expect(""); f.Tick(4); f.Tick(5, ("B", 1)); f.Expect("RightAlt");
}
static void NoTapBacklog()
{
    using var f = new Fixture(P(Hold() with { Mode = "tap", Keys = ["LeftCtrl", "F10"] }));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Tick(50); f.Tick(60, ("B", 1));
    f.Tick(101, ("B", 1)); f.Tick(201, ("B", 1)); f.Tick(301, ("B", 1));
    f.Tick(500, ("B", 1)); Equal(1, f.Triggers); f.Expect("");
}
static void DelayedTick()
{
    using var f = new Fixture(P(Hold() with { Mode = "tap", Keys = ["LeftCtrl", "F10"] }));
    f.Tick(0); f.Tick(1, ("B", 1)); f.Tick(10000, ("B", 1)); f.Expect("F10,LeftCtrl");
    f.Tick(10001, ("B", 1)); f.Expect("F10,LeftCtrl");
    f.Tick(10100, ("B", 1)); f.Expect("LeftCtrl");
}
static void Validation()
{
    Throws(() => ProfileValidator.Validate(P(Hold() with { Threshold = double.NaN })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { ReleaseThreshold = .9 })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { Input = "unknown" })));
    Throws(() => ProfileValidator.Validate(P(Hold(), Hold() with { Id = "second" })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { Keys = ["A", "B", "C", "D", "E", "F", "G"] })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { Processes = ["C:\\program.exe"] })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { Keys = null! })));
    Throws(() => ProfileValidator.Validate(P(Hold() with { Processes = null! })));
}
static void DefensiveCopy()
{
    var profile = P(Hold());
    using var f = new Fixture(profile);
    profile.Bindings[0].Keys[0] = "LeftAlt";
    f.Tick(0); f.Tick(1, ("B", 1)); f.Expect("RightAlt");
}
static void OutputFailure()
{
    var output = new FaultOutput();
    using var engine = new MappingEngine(P(Hold()), output);
    engine.Tick(new(true, new Dictionary<string, double>(), ""), DateTimeOffset.UnixEpoch);
    try
    {
        engine.Tick(new(true, new Dictionary<string, double> { ["B"] = 1 }, ""), DateTimeOffset.UnixEpoch.AddSeconds(1));
        throw new Exception("Expected output failure");
    }
    catch (InvalidOperationException) { }
    Equal(true, engine.Paused); Equal(true, output.ReleaseAttempted);
}
static void HidCodes()
{
    var (modifiers, keys) = KeyCatalog.Encode(["RightAlt", "LeftCtrl", "F10"]);
    Equal((byte)65, modifiers); Equal((byte)0x43, keys.Single());
    Equal((byte)0x2f, KeyCatalog.Encode(["BracketLeft"]).Keys.Single());
}
static void EmptyProfile()
{
    var profile = new Profile();
    ProfileValidator.Validate(profile);
    Equal(0, profile.Bindings.Count);
    using var f = new Fixture(profile);
    f.Tick(0); f.Tick(1, ("B", 1), ("A", 1), ("RS.Right", 1));
    f.Tick(101, ("B", 1), ("A", 1), ("RS.Right", 1));
    f.Expect(""); Equal(0, f.Triggers);
}
static void ExampleImport()
{
    var profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "vibe.json")), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    using var f = new Fixture(profile);
    f.Process = "T3 Code (Beta)";
    f.Tick(0); f.Tick(1, ("RS.Right", 1)); f.Tick(101, ("RS.Right", 1));
    f.Expect("BracketLeft,LeftCtrl,LeftShift");
    f.Engine.ReleaseAll(); f.Tick(200); f.Tick(201, ("RS.Left", 1)); f.Tick(301, ("RS.Left", 1));
    f.Expect("BracketRight,LeftCtrl,LeftShift");
    f.Engine.ReleaseAll(); f.Tick(400); f.Tick(401, ("RS.Up", .65)); f.Tick(501, ("RS.Up", .65));
    f.Expect("ArrowUp,LeftCtrl,LeftShift");
    f.Engine.ReleaseAll(); f.Tick(600); f.Tick(601, ("RS.Down", .65)); f.Tick(701, ("RS.Down", .65));
    f.Expect("ArrowDown,LeftCtrl,LeftShift");
    f.Engine.ReleaseAll(); f.Process = "other"; f.Tick(800); f.Tick(801, ("RS.Right", 1));
    f.Expect(""); f.Tick(802, ("B", 1)); f.Expect("RightAlt");
}

sealed class Fixture : IDisposable
{
    private readonly CaptureOutput _output = new();
    public MappingEngine Engine { get; }
    public string Process { get; set; } = "";
    public int Triggers { get; private set; }
    public Fixture(Profile profile)
    {
        Engine = new(profile, _output);
        Engine.Event += e => { if (e.StartsWith("trigger:")) Triggers++; };
    }
    public void Tick(int milliseconds, params (string Key, double Value)[] values) => Engine.Tick(
        new(true, values.ToDictionary(v => v.Key, v => v.Value), Process), DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds));
    public void Disconnect(int milliseconds) => Engine.Tick(new(false, new Dictionary<string, double>(), Process), DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds));
    public void Expect(string keys)
    {
        var actual = string.Join(',', _output.Keys.Order(StringComparer.Ordinal));
        if (actual != keys) throw new Exception($"Expected held [{keys}], got [{actual}]");
    }
    public void Dispose() => Engine.Dispose();
}
sealed class CaptureOutput : IKeyboardOutput
{
    public string[] Keys { get; private set; } = [];
    public void SetKeys(IReadOnlyCollection<string> keys) => Keys = [.. keys];
    public void Dispose() { }
}
sealed class FaultOutput : IKeyboardOutput
{
    public bool ReleaseAttempted { get; private set; }
    public void SetKeys(IReadOnlyCollection<string> keys)
    {
        if (keys.Count > 0) throw new IOException("simulated output error");
        ReleaseAttempted = true;
    }
    public void Dispose() { }
}
