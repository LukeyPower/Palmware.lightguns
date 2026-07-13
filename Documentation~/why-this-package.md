# Why this package exists

*Written 2026-07-05, when the lightgun code was extracted out of the game projects.*

This document is the "note to future me." If you come back in a year wondering why the
lightgun code lives in its own repo instead of just being in the game, or why it's
structured the way it is — this is the answer.

## The problem it solves

The lightgun integration was originally written inside **Croc Hunter**, then copied into
**Spell Slinger**, and the same code exists (in older or newer forms) across several
other projects (`Light-Gun-Game`, `GUN-KNIGHT`, `Gun_Knight_Official`,
`Udemy-Rail-Shooter-Project`, …). Every copy was a fork.

That caused exactly the failure you'd predict: **the copies drifted.** Spell Slinger got
real improvements — joystick-stick aim for the RS3, hot-plug reconnect, Blamcon
force-feedback re-arming, the serial auto-mode-switch, a proper manager — and Croc Hunter
never received them. When we reviewed Croc Hunter later, its lightgun layer looked *buggy*,
but the bugs were really just **missing updates that already existed in Spell Slinger**.
Reconciling them by hand meant re-reading both repos and diffing files.

A shared package makes that class of problem go away: there is **one** source of truth, it
is **versioned**, and every project pulls from it instead of forking it.

## The decision: a UPM package via Git URL

Unity has a native mechanism for exactly this — a **UPM (Unity Package Manager) package**
installed from a **Git URL**. The workflow it gives us:

- Edit the package in this repo.
- Commit, and **tag a version** (`v0.2.0`, …).
- In any game project, add the package by git URL and pin the tag. Bump the tag when you
  want that project to take the update — never silently, so shipping a game can't be broken
  by an unrelated package change.

### Why not the alternatives

- **Copy-paste (what we were doing):** every project is a fork; updates don't propagate;
  drift is guaranteed. This is the thing we're escaping.
- **Git submodule:** works, but submodules are fiddly (detached HEADs, easy to leave
  unpushed, extra clone steps) and don't integrate with Unity's Package Manager UI.
- **`.unitypackage` export:** a manual snapshot with no versioning and no update path —
  the copy-paste trap with extra steps.

UPM-via-git wins because versioning + per-project pinning is built in, and it's the same
mechanism the third-party Blamcon package already uses.

### The one workflow nuance to remember

A package installed **via git URL is read-only** in the consuming project (Unity caches it
under `Library/PackageCache`). **You do not edit it there.** During active development,
point one "host" project at the package by **local path**
(`"com.palmware.lightguns": "file:../../Palmware.lightguns"`) so your edits are live; commit
and push from this repo; other projects consume the git URL at a pinned tag.

## Why the architecture looks like this

The design goal is a hard rule: **gameplay code must never know which gun fired.** You read
input through Input System *actions*, and everything else — aim, recoil, rumble, reload,
detection, hot-swap — goes through one manager.

- **`LightgunManager`** is the only class the game talks to. It raises device-agnostic
  `FirePressed`/`ReloadPressed` events, exposes a single arbitrated `AimPosition`, and routes
  feedback calls to the right module.
- **`ILightgunModule` / `LightgunModule`** is the per-gun plug-in contract. Each gun family
  (Blamcon, RS3, Sinden) is one module that bundles its recognition, aim source, and
  feedback. Adding a gun is "write one module + add bindings," with **no** change to the
  manager or gameplay code.

Why not just use Unity's `PlayerInput` for all of it? Because the Input System covers
buttons and axes well, but lightguns have extras it has no concept of: recoil, slide return,
ammo-LED counts, serial COM ports, per-vendor force-feedback handshakes. The manager gives
those a home while still using Input System actions for the plain input.

## Why Blamcon is a separate assembly

Historically (≤ 0.3.0) the Blamcon module depended on the third-party
`com.blamcon.lightguns` package, so it lived in its own assembly guarded by a
`PALMWARE_BLAMCON` version-define — projects without that package simply skipped the
assembly. Since **0.4.0** the Blamcon device code is vendored into this package
(`Runtime/Blamcon/Device/`, MIT — see `THIRD-PARTY-NOTICES.md`), so the define and the
external dependency are gone and the assembly always compiles. The split assembly
remains because it's still a useful boundary: it keeps the `unsafe` HID event handling
and the `System.IO.Ports` precompiled reference out of the core assembly.

## Why .NET Framework is required

The RS3 driver and the Blamcon serial companion talk to the guns over a COM port using
`System.IO.Ports`, which **only exists under the .NET Framework API compatibility level**,
not .NET Standard. That's a per-project Player Setting, so it can't be baked into the
package — it's a documented prerequisite instead. (This was already true of the original
in-game code; Spell Slinger runs at API level ".NET Framework" for the same reason.)

## Trade-offs we accepted

- **A little ceremony:** tagging releases and bumping manifest versions per project. Worth it
  for a solo dev with 6+ lightgun projects; skip a shared package if you ever have only one.
- **Discipline required:** don't let one game's special-case hack leak back into the shared
  code. If a project needs something odd, solve it in the project, or make it a configurable
  option on a module — not a one-off edit to the package.
- **Can't be fully tested outside Unity:** the assembly-definition setup (especially the
  optional-Blamcon define and the `System.IO.Ports` reference) was authored without a live
  Editor. First time you open this in Unity, verify the Console is clean — see
  `known-verification-steps` below.

## Known verification steps (do these the first time in Unity)

1. Open a project that references this package (local path is easiest). Confirm **no compile
   errors** in the Console.
2. Confirm the **`Palmware.Lightguns`** assembly compiled (it should, unconditionally).
3. With the Blamcon package installed, confirm **`Palmware.Lightguns.Blamcon`** also compiled
   (no "skipped due to define constraints" message). Without it, confirm the core still
   compiles and only the Blamcon assembly is skipped.
4. Confirm `System.IO.Ports` resolves (no "type or namespace 'Ports' could not be found").
   If it fails: API Compatibility Level is not set to **.NET Framework**.
