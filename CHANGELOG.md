# Changelog

All notable changes to this package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-07-13

The **single-dependency** release: installing this package is now all a project
needs — no separate `com.blamcon.lightguns` install.

### Changed
- **Blamcon device code is now vendored** (MIT, © Props3D — see
  `THIRD-PARTY-NOTICES.md`): the `com.blamcon.lightguns` Runtime code (HID device
  layout, force-feedback commands, position remap processor) ships inside this
  package at `Runtime/Blamcon/Device/`, vendored from upstream tag `1.0.0` plus the
  `allowUnsafeCode` compile fix. The `Blamcon.Lightguns` namespace is unchanged, so
  existing game code (`using Blamcon.Lightguns;`) compiles as-is.
- `Palmware.Lightguns.Blamcon` assembly always compiles: the `PALMWARE_BLAMCON`
  define, its version-define on `com.blamcon.lightguns`, and the external assembly
  reference are gone.

### Migration
- Remove the embedded/installed `com.blamcon.lightguns` package from the project —
  keeping both installed will fail to compile with duplicate `Blamcon.Lightguns`
  type definitions.

## [0.3.0] - 2026-07-12

### Added
- Documentation consolidated from the game projects (Spell Slinger), updated to
  this package's current behavior (MnK-first policy, COM auto-detect):
  `Documentation~/scene-setup-guide.md` (beginner click-by-click scene walkthrough,
  Inspector field guide, troubleshooting), `Documentation~/architecture-map.md` +
  `.html` (visual diagrams of the layer), and `Documentation~/raw-input-research.md`
  (what Windows Raw Input is and when mouse-type guns would need it).
- `Setup` sample now also ships `LightgunSmokeTest` — a drop-in component that
  verifies the whole rig (crosshair, Fire/Reload events, per-gun recoil, hot-swap
  logs) before any gameplay code exists, and doubles as the reference for how game
  code should talk to the manager.

## [0.2.0] - 2026-07-08

The **MnK-first + multiplayer** release: a single gun is left in whatever mode it
arrived in (no forced switching); the moment 2+ physical guns are present — any mix
of Blamcons and RS3s, up to 4 — MnK-mode guns are switched to their per-device mode
automatically and every gun gets its own identity and crosshair. RS3 handling is
informed by Retro Shooter's own shipped titles (*Master Shooter: Warehouse Target* +
RetroGun calibration tool).

### Added
- `LightgunRoster` — shared physical-gun count (reported by each family's serial/
  registry scanner, so guns are counted in ANY input mode). Drives the mode policy:
  1 gun = keep its mode; 2+ = per-device modes required.
- **Per-device aim** for multiplayer: `ILightgunModule.TryGetAim(device, …)`,
  `LightgunManager.GetAimFor(device)` and `GetGunDevices()`. `FirePressed`/
  `ReloadPressed` events now carry the **firing device's own aim**, so each player's
  shot lands where their gun points.
- `RetroShooterModule`: **multi-gun support** — one serial link per detected RS3
  (COM ports auto-detected via the Windows registry by USB identity `VID_0483`,
  `PID_5750`–`5753` = P1–P4), each paired to its Input System joystick by the same
  PID. Per-gun recoil routing, per-gun crosshairs, per-gun debounce.
- `RetroShooterModule.SetJoystickMode(bool)` — runtime input-mode switch for all
  linked guns; anything the module switched is restored to mouse+keyboard on exit.
- `BlamconSerial`: **serial recoil/rumble routed as a module** — an MnK-mode Blamcon
  now gets `F.0.1.n` recoil on `Recoil()` (the Hook-of-the-Reaper trick), claims
  mouse shots while it's the only gun (`claimMouseWhileMnk`), and counts toward
  `AnyGunConnected`. `serialRecoilInMnk` can be turned off if the firmware's own
  trigger-recoil causes a double kick.
- Focus-regained recovery: `RetroShooterModule` re-checks its serial links and
  `BlamconSerial` re-scans (`rescanOnFocusRegained`) when the app regains focus.
- `BlamconSerial` re-scans (debounced) on device hot-plug, so a second gun plugged
  in mid-session triggers the multiplayer escalation.

### Fixed
- Blamcon serial recoil/rumble did nothing (and, once armed, the gun kicked on
  every trigger pull by itself — splash screen included): the game must **take
  serial FFB control** first. `BlamconSerial` now sends the documented
  `SM.0.1`/`SM.1.1` (solenoid/haptic serial control — the serial twin of the HID
  `EnableFFBControl` handshake) to each MnK-mode gun at detection time, so the
  gun only kicks on game commands, and releases with `ES` on quit. Verified on
  hardware, fw 1.0.16. Feedback writes are also serialized so simultaneous
  recoil+rumble don't race for the COM port.
- A real desktop mouse click no longer kicks an MnK-mode gun: mouse-claiming in
  `BlamconSerial` and `RetroShooterModule` now matches the gun's own mouse device
  by USB vendor/product id (new `UsbDeviceId` helper) when Unity exposes ids,
  falling back to the single-gun any-mouse assumption only when it doesn't.

### Changed
- **Mode policy is now MnK-first everywhere.** `RetroShooterModule.forceJoystickMode`
  defaults to off; `BlamconSerial`'s `autoSwitchToGamepadMode` bool is replaced by
  `modeSwitchPolicy` (`KeepMode` / `SwitchWhenMultiplayer` **(default)** /
  `AlwaysSwitch`). Single-gun MnK play routes recoil over serial; switching happens
  only for multiplayer (or when forced), and switched guns are restored on exit.
- `RetroShooterModule.portName` defaults to empty = **auto-detect** (manual value =
  override). `FindRetroShooterPorts()` is public, sorted P1-first.
- `RetroShooterModule` aim now tracks each RS3's own joystick device instead of
  reading `Joystick.current`, which broke as soon as a gamepad, wheel, or second
  joystick was present.
- `ILightgunModule` gained a second `TryGetAim(InputDevice, out Vector2)` member
  (breaking for custom modules; `LightgunModule` provides a no-op default).

## [0.1.0] - 2026-07-05

Initial extraction of the lightgun integration layer from the Spell Slinger and
Croc Hunter projects into a standalone, reusable Unity package.

### Added
- `LightgunManager` — the single hub game code talks to: device-agnostic
  Fire/Reload events, `AimPosition` arbitration, feedback routing, detection/hot-swap.
- `ILightgunModule` / `LightgunModule` — per-gun module contract and auto-registering
  MonoBehaviour base.
- `BlamconModule` — Blamcon USB-HID gun (aim + recoil/rumble/LED/ammo, FFB re-arm on
  hot-plug). Lives in the optional `Palmware.Lightguns.Blamcon` assembly.
- `BlamconSerial` — Blamcon serial companion (detects guns in any emulation mode,
  auto-switches Mouse/Keyboard-mode guns to Gamepad mode, restores on quit, serial
  recoil/rumble fallback).
- `RetroShooterModule` — Retro Shooter RS3 "Reaper" serial driver (mode switching,
  recoil/slide/rumble, joystick-stick aim + smoothing).
- `SindenModule` + `SindenBorder` — Sinden / mouse-emulating gun compatibility
  (detection + manual toggle, white tracking border overlay).
- `Setup` sample — the Fire/Reload/Point/Submit input action asset.

### Notes
- The Blamcon module is isolated in its own assembly guarded by the `PALMWARE_BLAMCON`
  define, so projects that don't use a Blamcon gun do not need the
  `com.blamcon.lightguns` package installed.
