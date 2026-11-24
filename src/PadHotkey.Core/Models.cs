namespace PadHotkey.Core;

public sealed record Profile
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "default";
    public List<Binding> Bindings { get; set; } = [];

    public Profile DeepCopy() => this with
    {
        Bindings = Bindings.Select(b => b with
        {
            Keys = [.. b.Keys], Processes = [.. b.Processes]
        }).ToList()
    };
}

public sealed record Binding
{
    public string Id { get; set; } = "";
    public string Input { get; set; } = "";
    public string Mode { get; set; } = "tap";
    public string[] Keys { get; set; } = [];
    public double Threshold { get; set; } = .9;
    public double ReleaseThreshold { get; set; } = .7;
    public string[] Processes { get; set; } = [];
}

public sealed record InputSnapshot(bool Connected,
    IReadOnlyDictionary<string, double> Values, string ForegroundProcess);

public interface IKeyboardOutput : IDisposable
{
    void SetKeys(IReadOnlyCollection<string> keys);
}

public sealed record KeyboardReport(byte Modifiers, byte[] Keys);

public static class KeyCatalog
{
    private static readonly Dictionary<string, byte> Modifiers = new(StringComparer.Ordinal)
    {
        ["LeftCtrl"] = 1, ["LeftShift"] = 2, ["LeftAlt"] = 4, ["LeftWin"] = 8,
        ["RightCtrl"] = 16, ["RightShift"] = 32, ["RightAlt"] = 64, ["RightWin"] = 128
    };
    private static readonly Dictionary<string, byte> Codes = CreateCodes();
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(Modifiers.Keys.Concat(Codes.Keys).ToArray());
    public static bool IsModifier(string key) => key is not null && Modifiers.ContainsKey(key);
    public static bool IsKnown(string key) => key is not null && (Modifiers.ContainsKey(key) || Codes.ContainsKey(key));

    public static KeyboardReport Encode(IEnumerable<string> keys)
    {
        byte modifiers = 0;
        var codes = new HashSet<byte>();
        foreach (var key in keys)
        {
            if (key is not null && Modifiers.TryGetValue(key, out var modifier)) modifiers |= modifier;
            else if (key is not null && Codes.TryGetValue(key, out var code)) codes.Add(code);
            else throw new ArgumentException($"Unknown key: {key}");
        }
        // The selected keyboard backend is used with boot-keyboard sized reports.
        if (codes.Count > 6) throw new ArgumentException("At most six non-modifier keys may be held together.");
        return new KeyboardReport(modifiers, codes.Order().ToArray());
    }

    private static Dictionary<string, byte> CreateCodes()
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal)
        {
            ["Enter"] = 0x28, ["Escape"] = 0x29, ["Backspace"] = 0x2a, ["Tab"] = 0x2b,
            ["Space"] = 0x2c, ["Minus"] = 0x2d, ["Equal"] = 0x2e,
            ["BracketLeft"] = 0x2f, ["BracketRight"] = 0x30, ["Backslash"] = 0x31,
            ["Semicolon"] = 0x33, ["Quote"] = 0x34, ["Backquote"] = 0x35,
            ["Comma"] = 0x36, ["Period"] = 0x37, ["Slash"] = 0x38,
            ["CapsLock"] = 0x39, ["PrintScreen"] = 0x46, ["ScrollLock"] = 0x47,
            ["Pause"] = 0x48, ["Insert"] = 0x49, ["Home"] = 0x4a, ["PageUp"] = 0x4b,
            ["Delete"] = 0x4c, ["End"] = 0x4d, ["PageDown"] = 0x4e,
            ["ArrowRight"] = 0x4f, ["ArrowLeft"] = 0x50, ["ArrowDown"] = 0x51,
            ["ArrowUp"] = 0x52
        };
        for (int i = 0; i < 26; i++) result[((char)('A' + i)).ToString()] = (byte)(4 + i);
        for (int i = 1; i <= 9; i++) result[i.ToString()] = (byte)(0x1d + i);
        result["0"] = 0x27;
        for (int i = 1; i <= 12; i++) result[$"F{i}"] = (byte)(0x39 + i);
        for (int i = 13; i <= 24; i++) result[$"F{i}"] = (byte)(0x68 + i - 13);
        return result;
    }
}

public static class ProfileValidator
{
    public static IReadOnlyList<string> Inputs { get; } = Array.AsReadOnly(new[]
    {
        "A", "B", "X", "Y", "Back", "Start", "LB", "RB", "LS.Click", "RS.Click",
        "DPad.Up", "DPad.Down", "DPad.Left", "DPad.Right", "LT", "RT",
        "LS.Up", "LS.Down", "LS.Left", "LS.Right", "RS.Up", "RS.Down", "RS.Left", "RS.Right"
    });

    public static void Validate(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version < 1) throw new ArgumentException("Profile version must be positive.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64)
            throw new ArgumentException("Profile name must contain 1–64 characters.");
        if (profile.Bindings is null || profile.Bindings.Count > 64)
            throw new ArgumentException("Profile must contain a bindings array of at most 64 items.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in profile.Bindings)
        {
            if (binding is null) throw new ArgumentException("Binding cannot be null.");
            if (string.IsNullOrWhiteSpace(binding.Id) || binding.Id.Length > 64 || !ids.Add(binding.Id))
                throw new ArgumentException("Binding IDs must be unique, non-empty and at most 64 characters.");
            if (!Inputs.Contains(binding.Input) || !inputs.Add(binding.Input))
                throw new ArgumentException($"Unknown or duplicate input: {binding.Input}");
            if (binding.Mode is not ("tap" or "hold")) throw new ArgumentException("Mode must be tap or hold.");
            if (!double.IsFinite(binding.Threshold) || !double.IsFinite(binding.ReleaseThreshold) ||
                binding.Threshold <= 0 || binding.Threshold > 1 || binding.ReleaseThreshold < 0 ||
                binding.ReleaseThreshold >= binding.Threshold)
                throw new ArgumentException("Threshold must be in (0,1]; releaseThreshold must be in [0,threshold).");
            if (binding.Keys is null || binding.Keys.Length is < 1 or > 14 ||
                binding.Keys.Distinct(StringComparer.Ordinal).Count() != binding.Keys.Length)
                throw new ArgumentException("Keys must contain 1–14 unique key names.");
            _ = KeyCatalog.Encode(binding.Keys);
            if (binding.Processes is null || binding.Processes.Length > 16 || binding.Processes.Any(p =>
                    string.IsNullOrWhiteSpace(p) || p.Length > 128 || p.Contains('/') || p.Contains('\\')))
                throw new ArgumentException("Processes must contain at most 16 executable names, without paths.");
        }
    }
}
