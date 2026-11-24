using System.Diagnostics;
using System.Security.Principal;
using HIDMaestro;
using PadHotkey.Core;

namespace PadHotkey.Windows;

/// <summary>Owns one virtual keyboard, using only the already installed UMDF2 backend.</summary>
public sealed class HidKeyboardOutput : IKeyboardOutput
{
    private readonly object _gate = new();
    private readonly HMContext _context;
    private readonly HMController _keyboard;
    private byte _modifiers;
    private byte[] _keys = [];
    private bool _disposed;

    public HidKeyboardOutput()
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("HID output requires an elevated process. Dry-run and configuration validation do not.");
        if (IsAntiMicroXRunning())
            throw new InvalidOperationException("Exit AntiMicroX before enabling HID output to prevent duplicate mappings.");

        _context = new HMContext(HMBackend.Umdf2);
        try
        {
            if (!_context.IsDriverInstalled)
                throw new InvalidOperationException("HIDMaestro UMDF2 driver is not installed. PadHotkey will not install or upgrade it automatically.");
            _context.LoadDefaultProfiles();
            var profile = _context.GetProfile("keychron-q6")
                ?? throw new InvalidOperationException("HIDMaestro SDK lacks the keychron-q6 keyboard profile.");
            _keyboard = _context.CreateController(profile);
            _keyboard.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
        }
        catch
        {
            _context.Dispose();
            throw;
        }
    }

    public void SetKeys(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var (modifiers, codes) = KeyCatalog.Encode(keys);
        // Keychron's boot-keyboard report has six ordinary key slots.
        // Refuse overflow instead of emitting HID ErrorRollOver.
        if (codes.Length > 6)
            throw new InvalidOperationException("The keyboard supports at most six simultaneous non-modifier keys.");
        Array.Sort(codes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Always submit explicit releases: a failed prior SDK call may have
            // delivered a report before throwing, leaving our cache stale.
            if ((modifiers != 0 || codes.Length != 0) && modifiers == _modifiers && codes.AsSpan().SequenceEqual(_keys)) return;
            Span<HMKey> report = stackalloc HMKey[codes.Length];
            for (var i = 0; i < codes.Length; i++) report[i] = (HMKey)codes[i];
            _keyboard.SubmitKeyboard((HMKeyModifiers)modifiers, report);
            _modifiers = modifiers;
            _keys = codes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _keyboard.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty); }
            finally
            {
                try { _keyboard.Dispose(); }
                finally { _context.Dispose(); }
            }
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsAntiMicroXRunning()
    {
        var processes = Process.GetProcessesByName("antimicrox");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // This SDK probe also performs its normal background warm-up. Do not call
    // it on every status request; create the output once and retain its status.
    public static bool IsDriverInstalled()
    {
        using var context = new HMContext(HMBackend.Umdf2);
        return context.IsDriverInstalled;
    }
}
