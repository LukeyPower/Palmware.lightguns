# Changelog

All notable changes to this package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
