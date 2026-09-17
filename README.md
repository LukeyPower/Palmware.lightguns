# Palmware Lightguns

A device-agnostic lightgun integration layer for Unity's Input System. Your game
talks to **one** `LightgunManager`; everything device-specific lives in per-gun
modules. Aim, fire/reload events, and physical feedback (recoil, rumble, ammo LEDs,
slide return) are routed to whichever gun is connected — so **gameplay code never has
to know which gun fired.**

Supported guns:

| Gun | Input | Feedback | Notes |
|---|---|---|---|
| **Blamcon** | USB-HID (MnK or Gamepad mode) | HID output reports **or** serial | Device support bundled; P1–P4 |
| **Retro Shooter RS3 "Reaper"** | USB-HID (MnK or joystick mode) | Serial COM port (auto-detected by USB VID/PID) | P1–P4 |
| **Sinden** (and AimTrak / Gun4IR / mouse-type) | System mouse | Driver-side | White tracking border overlay; single player |

**MnK-first mode policy:** a single gun is left in whatever mode it arrived in — usually its
power-on mouse+keyboard mode, where it plays through the mouse path with **serial** recoil.
The moment **2+ physical guns** are detected (any mix — Blamcons, RS3s, or both, up to 4),
MnK-mode guns are switched automatically to their per-device mode (Blamcon → Gamepad, RS3 →
Joystick) so every gun has its own identity and crosshair, and restored on exit. Multiplayer
game code keys players by `InputDevice` — see the integration guide §5.

> **Why this package exists** and the full design rationale live in
> [`Documentation~/why-this-package.md`](Documentation~/why-this-package.md). Hardware
> details and the per-gun protocols are in
> [`Documentation~/integration-guide.md`](Documentation~/integration-guide.md). A
> beginner click-by-click scene walkthrough is
> [`Documentation~/scene-setup-guide.md`](Documentation~/scene-setup-guide.md), with visual
> diagrams in [`Documentation~/architecture-map.md`](Documentation~/architecture-map.md).
> Licensing is covered in [`Documentation~/licensing.md`](Documentation~/licensing.md).

## Requirements

- **Unity 6** (6000.0+)
- **Input System** 1.14+ (declared as a package dependency; installs automatically)
- **API Compatibility Level = .NET Framework.** The RS3 and Blamcon-serial drivers use
  `System.IO.Ports`, which does **not** exist under .NET Standard. Set it in
  *Project Settings → Player → Other Settings → Api Compatibility Level → .NET Framework*.
- For Blamcon guns: firmware **1.0.16+**. The device code (HID layout, FFB commands)
  is bundled — vendored from [Props3D's `com.blamcon.lightguns`](https://github.com/Props3D/UnityLightguns)
  2.0.0 under MIT; do **not** install that package alongside this one (duplicate types).
  Firmware 2.1.0+/4.0.0+ unlock extra upstream features (mouse-mode HID feedback, Bluetooth) —
  see the firmware table in the integration guide, §2.

## Installing

### In a shipping project — via Git URL (recommended)

*Package Manager → Add package from git URL…*:

```
https://github.com/LukeyPower/Palmware.lightguns.git
```

Pin to a released version by appending a tag — e.g. `…Palmware.lightguns.git#v0.1.0`.
A git-URL package is cached read-only; you don't edit it in place.

### While developing the package — via local path

Add this to the **consuming project's** `Packages/manifest.json` so edits are live:

```json
"com.palmware.lightguns": "file:../../Palmware.lightguns"
```

(Path is relative to the project's `Packages/` folder. Adjust to wherever you cloned
this repo.)

## Project setup (once per project)

1. Set **API Compatibility Level** to **.NET Framework** (see Requirements).
2. Import the **Setup** sample (*Package Manager → Palmware Lightguns → Samples → Import*)
   to get `LightgunInputActions.inputactions` (or use your own action asset with
   `Fire` / `Reload` / `Point` / `Submit` actions) and `LightgunSmokeTest` (a drop-in
   component that verifies the whole rig — crosshair, events, recoil, hot-swap).
3. Create a GameObject, add **`LightgunManager`**, and assign the input action asset.
   Add the gun-module components you need next to it (`BlamconModule`, `BlamconSerial`,
   `RetroShooterModule`, `SindenModule`). The scene setup guide walks through this
   click by click, including what every Inspector field does.

## Quick usage

```csharp
using Palmware.Lightguns;

void OnEnable()
{
    LightgunManager.Instance.FirePressed   += OnFire;
    LightgunManager.Instance.ReloadPressed += OnReload;
}

void OnFire(LightgunEventArgs args)
{
    Ray ray = Camera.main.ScreenPointToRay(args.AimPosition);
    // ...hit detection, spend ammo...
    LightgunManager.Instance.Recoil(args.Device, currentAmmo); // kicks only the gun that fired
}

void OnReload(LightgunEventArgs args)
{
    LightgunManager.Instance.ReloadAll(); // e.g. returns the RS3's physical slide
}
```

## Assemblies

- **`Palmware.Lightguns`** — core: manager, interfaces, `RetroShooterModule`,
  `SindenModule`, `SindenBorder`. Always compiles.
- **`Palmware.Lightguns.Blamcon`** — `BlamconModule` + `BlamconSerial`, plus the vendored
  Blamcon device code (`Runtime/Blamcon/Device/` — HID layout, FFB commands, remap
  processor; namespace `Blamcon.Lightguns`). Always compiles. Keeps the serial +
  `unsafe` HID code out of the core assembly.

## Adding another gun

Write one class deriving from `LightgunModule`, add its bindings to the input actions,
and drop the component next to `LightgunManager`. No changes to the manager or gameplay
code. See the integration guide, §5.

## Updating the package (future you)

When you improve the package, release the change as a **new version tag** so projects adopt it
deliberately instead of being surprised by it.

1. **Edit via local path.** In a host project's `Packages/manifest.json`, point at your working
   copy — `"com.palmware.lightguns": "file:../../Palmware.lightguns"` — so edits are live. (A
   git-URL install is cached read-only under `Library/PackageCache` and can't be edited in place.)
2. **Test in Unity**, then commit.
3. **Bump the version** in `package.json` (`"version": "0.2.0"`) following semver — patch = fixes,
   minor = new backward-compatible features, major = breaking changes — and add a `CHANGELOG.md`
   entry.
4. **Tag it and push:**
   ```
   git commit -am "your change"
   git tag v0.2.0
   git push && git push --tags
   ```

**Pin projects to a tag, never `main`.** Install with an explicit version —
`https://github.com/LukeyPower/Palmware.lightguns.git#v0.2.0` — so an experiment in the package
can't unexpectedly change a game you've already shipped. A project takes an update only when you
change its `#tag` and let Unity re-resolve (Package Manager → refresh; if it's stubborn, remove the
package's entry from `Library/PackageCache` so Unity re-fetches).

> **Getting AI help on this later?** Point it at `Documentation~/why-this-package.md` (design +
> rationale) and `Documentation~/integration-guide.md` (the per-gun hardware protocols). The
> one-line summary: this is a UPM package consumed by git URL — edit via local path, bump the
> semver version + tag, and each consuming project pins a specific tag.

## License

MIT — see [`LICENSE.md`](LICENSE.md). Third-party attributions (Blamcon, Retro Shooter
example) are in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
