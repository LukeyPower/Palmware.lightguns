# Changelog

All notable changes to this package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-10-21

**HID feedback for mouse-mode guns.** `BlamconMouseModeDevice` was bundled in 0.5.0 but
left unused; it is now the feedback path for a mouse-mode Blamcon on firmware **2.1.0+**.
An MnK gun gets recoil, rumble, LEDs and ammo over HID output reports without being
switched out of mouse mode — so it keeps driving the OS cursor, there's no re-enumeration
pause, and nothing is persisted to the gun. HID also survives unplug/replug (no COM port to
be reassigned) and is the only transport that works over Bluetooth, which is where Blamcon
is taking the platform.

### Changed
- `BlamconModule` resolves feedback to whichever device a player has — gamepad-mode gun
  **or** mouse-mode HID collection — via `BlamconLightgunHID.GetForceFeedback(player)`.
  `GunFor()` returns `IForceFeedback` instead of casting to `BlamconLightgunHID`, so a
  mouse-mode device is no longer discarded. `Owns()` claims mouse shots while a mouse-mode
  gun is reachable over HID, which routes its recoil here instead of to `BlamconSerial`.
  Like `BlamconSerial.Owns`, it checks the USB vendor id (`0x3673`) when Unity exposes it,
  so a click on a desk mouse does not kick the gun.
- `BlamconSerial` stands down entirely whenever HID owns the gun (`IsConnected`, `Owns`,
  `Recoil`, `Rumble`). Exactly one transport is ever live, so a shot never double-kicks.
  "HID owns the gun" means an **enabled** `BlamconModule` has *Claim Mouse While Mouse Mode
  Hid* on and a mouse-mode gun is reachable. With the toggle off, or no `BlamconModule` in
  the scene, serial keeps the gun, so it is never left with no feedback.
- `OnDeviceChange` re-arms the FFB-control handshake on a mouse-mode device too, so control
  survives a hot-plug in either mode.

- `BlamconModule` sends **one output report per gun per frame**. `Recoil`, `Rumble` and
  `SetAmmo` no longer send on the spot; whatever is asked for during a frame is packed into a
  single `BlamconHIDOutputReport` and sent in `LateUpdate`. The gun services one feedback
  command at a time, so two calls in the same instant (damage rumble + the shot's recoil)
  used to drop the second.

### Fixed
- **Two guns firing together lost a shot.** Two causes, both fixed:
  - Every device was read through one shared `Fire` action. While one gun's trigger was held,
    the Input System ignored every other gun's pull on that action, and two pulls in the same
    update raised one event. `LightgunManager` now makes a copy of the action map for each
    device Fire or Reload is bound to (`InputActionMap.devices` = that device) and raises
    `FirePressed` / `ReloadPressed` from those copies. The copies are rebuilt when devices are
    added, removed or reconnected. The shared map stays enabled, so `FireWasPressedThisFrame`,
    `SubmitWasPressedThisFrame` and game code reading the same asset are unchanged.
  - The Fire debounce (`minFireInterval`) used one timer for every device, so a second gun's
    pull inside the window was swallowed. It is now timed per device.

  Checked with two virtual mice in Play (Gun Knight, 2026-10-21): B alone, A held then B,
  and A and B in the same update gave 5 of 5 `FirePressed`; 0.5.1 gave 3 of 5.
- **Ammo display with *Control Ammo* on.** Taking ammo control zeroes the gun's display, and
  the module took control without sending a count, so the display read 0 until the next
  shot — and again after every hot-plug and every focus regain. The module now remembers
  the last count the game pushed (`Recoil`/`SetAmmo`) and sends it in the same report that
  takes control, as upstream documents.

### Added
- `BlamconModule` → *Claim Mouse While Mouse Mode Hid* (default on). Turn it off to force
  the old serial path for mouse-mode guns.

### Firmware
- Mouse-mode HID needs **2.1.0+** (released 2026-09-16). Below that the vendor HID
  collection doesn't exist, nothing is found, and the serial path carries feedback exactly
  as in 0.5.0 — no configuration change needed either way.

## [0.5.1] - 2026-10-18

### Fixed
- A shot fired in the same frame the gun moved used the previous frame's aim. Input callbacks run before
  `Update`, where `AimPosition` was refreshed, so a quick move-and-fire with the mouse or a gun in mouse mode
  landed where the gun had pointed one frame earlier. `FirePressed` and `ReloadPressed` now read the aim first:
  the firing pointer's own position, then the gun modules.

## [0.5.0] - 2026-09-17

Vendored Blamcon device code updated from upstream **1.0.0 → 2.0.0** (Props3D, MIT).

### Fixed (from upstream 1.1.0 / 2.0.0)
- A Blamcon input report with an unrecognized report ID no longer registers a phantom
  trigger press, releases held buttons and snaps aim to (0,0) — which surfaced as a stray
  `FirePressed` in the corner of the screen. Such reports are now dropped.
- Ammo counts clamp to 0–255 instead of wrapping (256 no longer shows as 0).
- Recoil/rumble/LED timing clamps match firmware 3.0 limits.

### Changed
- `BlamconModule` resolves guns **per player** (`BlamconLightgunHID.GetForceFeedback(player)`,
  `playerIndex`) instead of `InputSystem.GetDevice<BlamconLightgunHID>()` / every listed device,
  so a stale device left over from a firmware update no longer swallows commands or logs errors.
- `BlamconModule` now **hands FFB control back** when disabled, when play mode stops and on quit,
  and (new *Release On Focus Loss*, default on) while the app lacks focus — previously a
  gamepad-mode gun was left under game control after the game closed. Take/release only touches
  the ticked components, so leaving *Control Ammo* off no longer actively releases ammo control.
- Upstream's `allowUnsafeCode` fix landed, so the vendored sources are now unmodified.
- `package.json` now declares `com.unity.ugui` (2.0.0), which `SindenBorder` has always used —
  projects without uGUI installed previously failed to compile the package.

### Added (upstream, bundled but not used by the modules)
- `BlamconMouseModeDevice` — HID force feedback for mouse-mode guns on firmware 2.1.0+.
  MnK-mode feedback still goes over serial (`BlamconSerial`).
- `BlamconLightgunHID.GetForceFeedback(player)`, `SendCommand(player, ref report)`, public
  `playerIndex`.
- `LightgunSession` component. **Don't add it alongside `BlamconModule`** — both take FFB control.

### Removed (upstream, breaking for direct callers)
- `BlamconRecoilCommand`, `BlamconRumbleCommand`, `BlamconLEDCommand`, `BlamconAmmoCommand` and
  their `SendCommand` overloads. They never reached the gun. Use `BlamconHIDOutputReport`
  (`SetRecoil`/`SetRumble`/`SetColor`/`SetAmmo`) or the `IForceFeedback` methods. Code that only
  talks to `LightgunManager` is unaffected.

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
