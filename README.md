# PadHotkey

[English](README.md) | [简体中文](README.zh-CN.md)

Turn a Windows gamepad into configurable keyboard shortcuts for coding and everyday desktop work. PadHotkey uses XInput for controller input and HIDMaestro for virtual keyboard output, with a local REST API and CLI for updating mappings.

**Status: early development (v0.1.2).** No shortcuts are assigned on first run. Saved mappings are enabled when the app starts.

## Features

The primary goal is to make a gamepad a shortcut remote for **vibe coding**: switch chats, adjust model effort, submit input, undo, or hold a voice-input shortcut while working with an AI coding assistant. The target application must already support the assigned keyboard shortcuts. PadHotkey sends keys; it does not implement speech recognition or control a model directly.

Mappings remain general-purpose and user-defined. The REST API and CLI let local scripts or coding agents inspect and update them without editing application code. T3 Code and voice-input bindings are optional examples, not built-in defaults.

- JSON mappings that persist across restarts; changes through the CLI or API apply immediately.
- Tap and hold actions, key combinations, and distinct left/right modifier keys.
- Stick and trigger thresholds with separate release thresholds.
- Global bindings or bindings limited to foreground process names.
- Pause, resume, bounded diagnostics, recent events, and an SSE event stream.
- Configuration backups and revision checks to prevent stale updates.
- A single background process without a WebView or database.

## Requirements

- Windows x64 and an XInput-compatible controller. Other controller protocols are not supported directly.
- HIDMaestro 2.3.0 and its driver for real keyboard output; HID mode requests administrator privileges.
- .NET 10 SDK to build from source. The self-contained build does not require a separately installed .NET runtime.

The application is portable, but its virtual keyboard driver is a system dependency. PadHotkey does not silently install or upgrade the driver. Stop AntiMicroX before starting HID mode.

## Controller compatibility

The development controller is a **Q37 Pro using its USB receiver**. In the earlier AntiMicroX setup, the receiver appeared as `Xbox 360 Controller`; this is its reported device identity, not the physical controller model. PadHotkey has detected the connected XInput slot on the development machine, but its physical controller-to-application workflow still needs end-to-end verification.

Compatibility is based on **XInput**, not a brand whitelist. Xbox-family controllers and third-party controllers are expected to work when their Windows driver and connection mode expose standard XInput input. Individual Xbox models and USB, wireless-adapter, or Bluetooth connections have not been tested in this project; this is not a blanket compatibility claim for every Xbox controller.

The current implementation reads A/B/X/Y, D-pad, shoulder buttons, Start/Back, stick clicks, two sticks, and two triggers from one selected XInput slot (0–3). Guide, Share, and extra paddles are not exposed as separate bindings. DirectInput-only and raw HID devices need an external XInput compatibility layer; PadHotkey does not provide one. Check `status` and a bounded `monitor --seconds 10` session to verify your setup.

## Build from source

Obtain the SDK from the [official HIDMaestro v2.3.0 release](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v2.3.0). Place `HIDMaestro.Core.dll` and its companion dependency DLLs from that distribution in `local/sdk/`. Follow upstream instructions to provision the driver for real output.

From the repository root, in PowerShell:

```powershell
.\build.ps1 -Portable
```

Output: `local/portable/PadHotkey.exe`. This is a self-contained Windows x64 single-file publish, not Native AOT; native dependencies may be extracted at runtime. The tested v0.1.2 EXE is approximately 129 MiB. SDK files and build output are excluded from Git.

Use `.\build.ps1` for a regular Release build, or `.\build.ps1 -Publish` for a framework-dependent publish. A custom SDK DLL location can be supplied with the MSBuild property `HidMaestroSdkPath` when invoking `dotnet` directly.

## Quick start

Run these commands from the portable output folder:

```powershell
.\PadHotkey.exe start
.\PadHotkey.exe status
.\PadHotkey.exe bind --button A --tap Enter
.\PadHotkey.exe bind --button B --hold RightAlt
.\PadHotkey.exe profile
```

Accept the administrator prompt for HID mode. Start returns before the service is necessarily ready; check status before configuring it. These bindings are examples, not defaults. Holding Right Alt may trigger an application shortcut such as voice input.

```powershell
.\PadHotkey.exe pause
.\PadHotkey.exe resume
.\PadHotkey.exe release
.\PadHotkey.exe stop
```

`release` pauses mapping and releases held keys. Double-clicking the EXE is equivalent to `start`. Use `start --paused true` to start paused. No login startup entry is created.

For configuration testing without sending keys, use `start --backend dry-run`. Stop any existing instance first; only one instance runs per Windows session. To inspect controller events for a bounded period, use `monitor --seconds 10`.

## JSON configuration

The default data directory is `%LOCALAPPDATA%\PadHotkey`. A new `profile.json` contains:

```json
{"version":1,"name":"default","bindings":[]}
```

Here is an optional profile mapping a right-stick direction to a shortcut:

```json
{
  "version": 1,
  "name": "my-shortcuts",
  "bindings": [
    {
      "id": "previous-chat",
      "input": "RS.Right",
      "mode": "tap",
      "keys": ["LeftCtrl", "LeftShift", "BracketLeft"],
      "threshold": 0.9,
      "releaseThreshold": 0.7,
      "processes": []
    }
  ]
}
```

An empty `processes` array means global; otherwise supply foreground process names. Input and key names are case-sensitive; query `GET /v1/schema` for supported names. Verify the actual stick direction for your controller orientation.

Import a saved JSON file with:

```powershell
.\PadHotkey.exe apply --file .\my-profile.json
```

Import replaces the current profile. `version` is the revision used for concurrency checks. Direct file edits are not watched: explicitly import the file, or stop the app before editing and restart it. Updates preserve backups named `profile-backup-*.json`; restore one with `apply --file`.

The repository includes an [optional T3/voice-input example](config/vibe.json). It is never imported automatically.

## Local REST API

The default address is `http://127.0.0.1:17843`. Every request requires a bearer token from `%LOCALAPPDATA%\PadHotkey\api-token.txt`; the CLI reads it automatically. Keep this file private.

| Endpoint | Purpose |
| --- | --- |
| GET /v1/status | Connection, inputs, pause state, and faults |
| GET /v1/profile | Current profile and ETag |
| PUT /v1/profile | Replace profile; requires If-Match |
| PUT /v1/profile/bindings/{id} | Update one binding; requires If-Match |
| PUT /v1/runtime | Set paused to true or false |
| POST /v1/release-all | Pause and release keys |
| POST /v1/shutdown | Release keys and exit |
| POST /v1/diagnostics | Bounded key output while paused |
| GET /v1/events | Recent events |
| GET /v1/events/stream | SSE events for local clients |
| GET /v1/schema | Supported keys and actions; not JSON Schema |

Read the profile's ETag before writing and send it as `If-Match`. A stale revision returns HTTP 412: re-read and merge before retrying. The server binds to loopback and rejects requests carrying an Origin header; this API is intended for local tools rather than browser pages.

## Development and verification

Run the mapping engine tests:

```powershell
dotnet run --project tests/PadHotkey.Tests/PadHotkey.Tests.csproj -c Release
```

Python API, profile lifecycle, and shutdown checks are under `tests/`; see each script's `--help` for its arguments.

Recorded v0.1.2 validation: 20 engine checks, 17 API checks, 9 lifecycle checks, and 3 shutdown checks passed. Real HID F13 press/release was observed on the development machine. Physical controller-to-application behavior and this version's voice-input integration have **not** been verified end to end.

Current limitations include a single active profile/controller index, US HID key encoding, no tray UI or built-in login startup, and unverified behavior after a forced process kill. Normal pause, disconnect, and shutdown paths release held keys.

## Documentation and agent integration

Detailed documents are currently in Chinese:

- [Usage and API](doc/user/usage.md)
- [Design](doc/dev/design.md)
- [Verification history](doc/dev/verification.md)
- [Portable deployment record](doc/user/portable-deployment.md) — development-machine paths and observations, not universal installation defaults.
- [Optional Codex skill](skill/pad-hotkey/SKILL.md) — adapt the executable path to your installation before installing it. The skill is separate from the application.

For bug reports, include the app version, Windows version, controller model, backend, and reproduction steps. Redact API tokens and personal configuration.

## License

A project license has not yet been selected. This repository is being prepared for an open-source release; no open-source license grant is currently provided. HIDMaestro and other dependencies retain their own licenses. Choose a project license and review third-party redistribution notices before publishing binaries.
