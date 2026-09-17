# Lightgun Scene Setup — Step-by-Step Guide (beginner friendly)

How to wire the lightgun system into a Unity scene, click by click, and **why each step
exists**. Do this once in the game's first scene; the manager survives scene loads, so every
later scene gets lightguns for free.

If you only remember one thing: **components only run when they're on a GameObject in a
scene.** The manager and every gun module are MonoBehaviour scripts — until you attach them to
a GameObject, they are just files; nothing scans for guns, nothing listens for triggers. That's
why "drag them onto an object" is not optional.

---

## Part 1 — The mental model (read this first, it makes the clicks make sense)

There are two kinds of pieces:

**The manager (`LightgunManager`)** — the switchboard. The game only ever talks to this one
class. It does four jobs:

1. **Listens for input.** It subscribes to the *Fire* and *Reload* actions inside
   `LightgunInputActions.inputactions`. Those actions have bindings for the mouse button, the
   Blamcon trigger AND the RS3 trigger — so one event covers every device. When any of them
   fires, the manager raises its `FirePressed` C# event, and your gameplay code reacts to that.
2. **Tracks aim.** Every frame it asks "who moved last — the mouse, the Blamcon, the RS3
   stick?" and stores that position in `AimPosition`. Your crosshair and raycasts read this one
   value and never care which device produced it.
3. **Routes feedback.** When the game calls `Recoil(device, ammo)`, the manager asks each
   registered module "`Owns(device)` — is this trigger pull yours?" The module that says yes
   gets its `Recoil()` called, and *that module* sends the actual hardware command (a USB HID
   report for the Blamcon, a serial `Z` command for the RS3). A plain mouse click owns nothing,
   so nothing kicks.
4. **Watches for hot-swap.** It forwards Unity's device-connected/disconnected notifications to
   every module (so e.g. the Blamcon module can re-arm force feedback after a re-plug) and
   raises `GunConnected` / `GunDisconnected` events your UI can show.

**The modules (`BlamconModule`, `RetroShooterModule`, `SindenModule`)** — one "driver" per gun
family. Each one knows how to recognize its gun, read its aim, and speak its feedback protocol.
They plug themselves into the manager automatically: the moment a module component becomes
enabled in the scene, its `OnEnable()` runs `LightgunManager.Register(this)` — that's the whole
plug-in mechanism. **A gun whose module is not in the scene simply doesn't exist to the game.**
Conversely, having a module in the scene when the gun is unplugged is harmless — modules no-op
cleanly when their hardware is missing.

There is one companion that isn't a gun driver: **`BlamconSerial`**. Every Blamcon exposes a
USB serial config port *no matter what emulation mode it's in*, and this component watches it —
it detects Blamcons that Unity can't see (guns left in Mouse/Keyboard mode), fires **serial**
recoil/rumble at them, counts them toward the shared gun roster, and switches them to Gamepad
mode automatically when multiplayer makes that necessary (see Step 6).

### The conversation map — what talks to what, and what each piece is looking for

| Piece | What it's watching / looking for | Who it talks to, and what it says |
|---|---|---|
| **Unity's Input System** | Raw hardware: USB reports from the Blamcon, the RS3's joystick, mouse clicks | Fires the actions defined in `LightgunInputActions.inputactions` — "the **Fire** action just happened, and here is the exact **device** that caused it" |
| **`LightgunInputActions` (the asset)** | Nothing — it's data, not code | It's the lookup table the Input System uses: "Fire = mouse left button OR Blamcon buttonWest OR joystick trigger". Adding a gun = adding a row here, not writing code |
| **`LightgunManager`** | (1) the Fire/Reload actions from that asset, (2) Unity's "a device was plugged in / unplugged" notifications, (3) modules calling `Register(this)` | Talks **up** to the game: raises `FirePressed` / `ReloadPressed` / `GunConnected` events. Talks **down** to modules by asking questions: "`Owns(device)` — is this yours?", "`TryGetAim` — where is your gun pointing, did it move?", and giving orders: "`Recoil()`", "`Rumble()`" |
| **Each module** (Blamcon / RS3 / Sinden) | Its own hardware only — the Blamcon module looks for `BlamconLightgunHID` devices, the RS3 module for `3AGAME` devices + its COM port, the Sinden module for "Sinden" in device names | Answers the manager's questions, and talks to its **hardware** in that hardware's own language: USB HID output reports (Blamcon), serial text commands like `Z5` (RS3), or nothing at all (Sinden — its own driver software does feedback) |
| **Your gameplay code** | The manager's events — nothing else | Talks ONLY to the manager: reads `AimPosition`, and calls `Recoil` / `RumbleAll` / `ReloadAll` / `SetAmmoAll` when game logic decides feedback should happen |

Notice the shape: gameplay code and gun hardware **never talk to each other directly** — every
conversation passes through the manager. That's the entire point of the design: when you add or
swap a gun, the game code doesn't change, because it was never talking to the gun in the first
place.

The flow for one shot, end to end:

```
you pull the Blamcon trigger
  → Input System fires the "Fire" action (binding: <LightGun>/buttonWest)
    → LightgunManager.OnFirePerformed runs: debounces, asks modules Owns(device)?
      → BlamconModule says "mine" → manager raises FirePressed(args)
        → YOUR game code runs: raycast from args.AimPosition, spend ammo, then calls
          LightgunManager.Instance.Recoil(args.Device, ammoLeft)
          → manager finds BlamconModule again → BlamconModule.Recoil()
            → sends the HID recoil command → the gun physically kicks
```

And the flow for aiming, which runs every frame whether or not anyone shoots:

```
LightgunManager.Update() runs each frame and asks around:
  → "Mouse, did you move?"            (only counts if it ACTUALLY moved — an idle
                                       mouse must never steal the crosshair)
  → "BlamconModule, TryGetAim?"       (module reads the gun's absolute position control)
  → "RetroShooterModule, TryGetAim?"  (module reads the joystick stick + smooths jitter)
  → whichever one MOVED most recently wins, and its position becomes AimPosition
    → your crosshair UI and your raycasts just read AimPosition — they never know
      or care which device is currently "driving"
```

---

## Part 2 — Building it in the editor

### Step 0 — Install the package and import the Setup sample
Install `com.palmware.lightguns` (git URL or local path — see the README) — Blamcon
device support is bundled, so that's the only package needed. Then in
**Package Manager → Palmware Lightguns → Samples**, click **Import** on the **Setup** sample.
That copies two things into your project under
`Assets/Samples/Palmware Lightguns/<version>/Setup/`:

- `LightgunInputActions.inputactions` — the pre-made binding table (Step 4 needs it),
- `LightgunSmokeTest.cs` — the verification component (Step 7 uses it).

Also confirm **Project Settings → Player → Api Compatibility Level = .NET Framework** — the
serial drivers need `System.IO.Ports`, which doesn't exist under .NET Standard.

### Step 1 — Open the right scene
Open your game's **first scene** (boot/startup scene). The manager calls `DontDestroyOnLoad`
on itself, meaning it survives every scene change — so it belongs in the first scene the game
loads, and **only there** (a copy in a second scene would just destroy itself as a duplicate,
which is handled, but it's clutter).

### Step 2 — Create the GameObject
Menu: **GameObject → Create Empty**. Name it `Lightgun Manager`.
Set its position to (0,0,0) if you like — position is irrelevant; nothing here renders.

### Step 3 — Add the manager component
With `Lightgun Manager` selected: **Add Component** (Inspector) → type `LightgunManager` →
click it.

Why: this is the switchboard from Part 1. Until it exists in the scene there is no
`LightgunManager.Instance`, no aim tracking, no events.

### Step 4 — Give the manager its input actions ← the step people forget
In the Project window, find the imported
`Assets/Samples/Palmware Lightguns/<version>/Setup/LightgunInputActions.inputactions` (or your
own action asset with `Fire`/`Reload`/`Point`/`Submit` actions) and **drag it onto the
"Input Actions" field** of the LightgunManager component. Leave "Action Map Name" as `Player`.

Why: the manager doesn't read hardware directly — it listens to the *actions* in this asset,
and the asset holds the per-device bindings (mouse left button, Blamcon `buttonWest`, RS3
joystick trigger → all bound to the one "Fire" action). **If this field is empty you get a
warning in the Console and no Fire/Reload events ever fire.** Aim still works (it's polled, not
action-driven), which can be confusing — "the crosshair moves but shooting does nothing" almost
always means this field is empty.

### Step 5 — Add one module component per gun you want to support
Still on the same `Lightgun Manager` GameObject: **Add Component** for each gun family →
`BlamconModule`, `RetroShooterModule`, `SindenModule`.

Also add **`BlamconSerial`** if you support Blamcons. It talks to the Blamcon's serial config
port, which exists no matter what mode the gun is in — so it can see guns Unity can't. A single
Blamcon left in Mouse/Keyboard mode still *plays* (mouse-path aim + fire) and still *kicks*
(serial recoil, routed by `BlamconSerial`); the moment a second physical gun appears,
`BlamconSerial` switches MnK-mode Blamcons to Gamepad mode so each gun has its own identity,
and restores them on quit. Without this helper, a MnK-mode Blamcon is invisible — no detection,
no recoil, plain-mouse play only.

Why: as explained in Part 1, a module must be a live component in the scene to register itself
with the manager. All of them can coexist happily — each only claims its own hardware, and an
absent gun just means that module reports `IsConnected = false` and does nothing.

(They can also live on child GameObjects if you prefer one object per gun for tidiness — the
auto-registration works the same. Same object is simplest.)

### Step 6 — Configure each component in the Inspector

The defaults implement an **MnK-first policy**: a single gun is left in whatever mode it
arrived in (usually its power-on mouse+keyboard mode) and plays through the mouse path with
serial recoil; guns are switched to their per-device mode (Blamcon → Gamepad, RS3 → Joystick)
only when **2+ physical guns** make per-device identity unavoidable. Every field below has a
tooltip in the Inspector — hover for the details.

**LightgunManager**
- *Input Actions* / *Action Map Name*: Step 4.
- *Min Fire Interval* (0.06): debounces trigger bounce — real lightgun triggers can deliver
  two presses on one pull.
- *Mouse Aim* (on): lets the system mouse drive `AimPosition` for desktop testing. The mouse
  only takes the crosshair when it actually moves, so it never fights a gun.

**BlamconModule** — defaults are correct; just know what they mean:
- *Take FFB Control* (on): on connect, tells the gun "the game is in charge of recoil now".
  Without it the gun's firmware auto-kicks on every trigger pull, even dry fires. Control is
  handed back when the module is disabled, play mode stops or the game quits. Requires gun
  firmware 1.0.16+.
- *Release On Focus Loss* (on): also hands control back while the game window isn't focused
  (e.g. alt-tabbed, or clicking into the Inspector in the Editor) and retakes it on focus.
- Don't also add Blamcon's own *Lightgun Session* component — it does the same job, so every
  control command would be sent twice.
- *Control Ammo* (off): turn on if you want the gun's ammo LED bar driven by
  `Recoil`/`SetAmmoAll` calls.
- *Recoil Pulses / Rumble Pulses*: feedback strength per event.

**BlamconSerial** — defaults are correct:
- *Mode Switch Policy* (`SwitchWhenMultiplayer`): the MnK-first policy above. `AlwaysSwitch` is
  the old behavior (every MnK gun flipped to Gamepad mode on launch); `KeepMode` never switches.
- *Restore Mode On Exit* (on): on quit, puts back any gun *we* switched (the mode is a saved
  firmware setting, so it would otherwise stay switched after the game closes).
- *Claim Mouse While MnK* (on): while a single MnK-mode Blamcon is confirmed on serial, mouse
  shots are treated as that gun's shots so recoil routes to it. Note: a real desktop mouse
  click will also kick the gun, and this must not be combined with an active `SindenModule`
  (which claims the mouse the same way).
- *Serial Recoil In Mnk* (on): fire serial recoil (`F.0.1.n`) at MnK-mode guns. If a shot kicks
  **twice**, the firmware is also auto-recoiling on trigger pull — disable trigger recoil on
  the gun in Blamcon ARC, or turn this off and let the firmware do the kicking.
- *Rescan Interval* (0) / *Rescan On Focus Regained* (on): how MnK-mode guns plugged in
  mid-game get noticed (they never appear in the Input System, so nothing else can spot them).

**RetroShooterModule** — defaults are correct:
- *Port Name* (**empty = auto-detect**): the gun's serial side is found in the Windows registry
  by its USB identity (VID 0483, PID 5750–5753 = P1–P4). Only set this manually if detection
  misses (or off Windows).
- *Force Joystick Mode* (off): the MnK-first policy — a single gun stays in the mode it arrived
  in and plays through the mouse path (recoil still works; it's serial). Turn on only if you
  always want per-device joystick aim.
- *Auto Joystick When Multiplayer* (on): switches MnK-mode guns to joystick mode (`ZJ`) the
  moment 2+ physical guns are present — required, since two mice can't share one OS cursor.
  Leave on. Switched guns are restored to mouse mode on exit.
- *Claim Mouse While MnK* (on): same idea as the Blamcon one — while a single RS3 is confirmed
  on serial, mouse shots route recoil to it.
- *Invert X / Invert Y / Aim Smooth Time*: joystick-mode aim feel — flip an axis if aiming up
  moves the crosshair down; smoothing damps the IR jitter (higher = steadier but laggier).

**SindenModule** — nothing required:
- *Auto Detect* (on): looks for "Sinden" in connected device names. Detection is best-effort —
  if it misses, your options menu can call `SetSindenMode(true)` (or tick *Force Sinden Mode*
  to test).
- When active it shows the **white border** overlay the Sinden's camera needs to track the
  screen, and treats the system mouse as the gun (that's genuinely how a Sinden works — its
  driver software moves the OS cursor).

### Step 7 — Verify it works (smoke test)
Add one more component to the same GameObject: **`LightgunSmokeTest`** (imported with the
Setup sample in Step 0). Press **Play**. You should see:

- a red `+` crosshair following whatever you aim with (mouse, Blamcon, RS3),
- a status readout top-left: each module and whether it's connected,
- Console logs on every trigger pull saying which device and which module fired,
- **real recoil** on the gun that pulled the trigger (and none on plain mouse clicks —
  unless a single MnK-mode gun is claiming the mouse, which is the intended MnK-first behavior),
- plug/unplug a gun during Play Mode → `GUN CONNECTED` / `GUN DISCONNECTED` logs (hot-swap).

This component is also the reference for how gameplay code should talk to the manager —
subscribe to events in `OnEnable`, unsubscribe in `OnDisable`, call `Recoil` with the device
from the event args. **Remove it once real gameplay scripts exist.**

### Step 8 — Save
**File → Save** (Ctrl+S). Done — every scene loaded after this one inherits the manager.

---

## Part 3 — Using it from gameplay code (the contract)

What the manager expects FROM you, and what it gives you:

| You do | It does |
|---|---|
| Subscribe to `FirePressed` | Calls you once per trigger pull from ANY device, with `args.Device`, `args.Module`, `args.AimPosition` |
| Subscribe to `ReloadPressed` | Same, for the reload button |
| Read `AimPosition` any time | Screen-pixel aim from whichever device moved last |
| Call `Recoil(args.Device, ammoLeft)` after a successful shot | Kicks only the gun that fired; plain mouse = no kick. You decide WHEN (no recoil on dry fire, for example) |
| Call `RumbleAll()` on player damage | Buzzes every connected gun |
| Call `ReloadAll()` when a reload starts | Returns the RS3's physical slide, etc. |
| Call `SetAmmoAll(n)` when ammo changes | Updates ammo LEDs on guns that have them |
| Subscribe to `GunConnected` / `GunDisconnected` | Show "P1 gun disconnected!" UI, pause, etc. |

Minimal example:

```csharp
using Palmware.Lightguns;
using UnityEngine;

public class MyShooter : MonoBehaviour
{
    private int _ammo = 6;

    private void OnEnable()  => LightgunManager.Instance.FirePressed += OnFire;
    private void OnDisable()
    {
        if (LightgunManager.Instance != null)
            LightgunManager.Instance.FirePressed -= OnFire;
    }

    private void OnFire(LightgunEventArgs args)
    {
        if (_ammo <= 0) return;                     // dry fire: note — no Recoil call, so no kick
        _ammo--;

        Ray ray = Camera.main.ScreenPointToRay(args.AimPosition);
        if (Physics.Raycast(ray, out RaycastHit hit)) { /* damage the thing */ }

        LightgunManager.Instance.Recoil(args.Device, _ammo);  // kick the gun that shot
    }
}
```

For **multiplayer** (2–4 guns, mixed families), key players by `InputDevice`:
`GetGunDevices()` to assign players, `GetAimFor(device)` for each player's crosshair, and the
`Device` on every event tells you whose shot it was — see `integration-guide.md` §5.

Rules that keep a project clean:
- Gameplay code **never** references `BlamconModule` / `RetroShooterModule` / device classes —
  only `LightgunManager` and `LightgunEventArgs`.
- Never read `Mouse.current` for aiming — always `LightgunManager.Instance.AimPosition`
  (a Gamepad-mode Blamcon doesn't move the OS mouse, so `Mouse.current` silently does the
  wrong thing).

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| Crosshair moves, but shooting does nothing | *Input Actions* field on the manager is empty (Step 4), or the action map name isn't `Player` |
| `LightgunManager.Instance` is null | No `Lightgun Manager` object in the loaded scene — it must be in the boot scene |
| A gun gives input but never recoils | Its module component is missing/disabled on the GameObject (Step 5) — for a Blamcon in MnK mode, `BlamconSerial` specifically |
| A single gun plays "as a mouse" (no per-device aim) | That's the MnK-first default, not a bug — recoil still routes over serial. Want per-device mode always? Set *Mode Switch Policy* = `AlwaysSwitch` (Blamcon) / tick *Force Joystick Mode* (RS3) |
| `BlamconModule` never connects | Gun is in Mouse/Keyboard mode and `BlamconSerial` isn't in the scene (or policy is `KeepMode`); firmware must be 1.0.16+ |
| Blamcon recoils on EVERY pull, even dry fire | *Take FFB Control* is off, or firmware too old — the firmware is auto-kicking by itself. Also expected while the game window is unfocused (*Release On Focus Loss*) — click back into the Game view |
| Blamcon kicks TWICE per shot (MnK mode) | Firmware trigger-recoil + our serial recoil are both firing — disable trigger recoil in ARC, or turn off *Serial Recoil In Mnk* |
| RS3 shoots but no recoil/slide | Auto-detect missed the COM port (check Device Manager → Ports and set *Port Name* manually), or another app has the port open |
| RS3 crosshair jitters | Raise *Aim Smooth Time* (try 0.05–0.08) |
| RS3 aim axis feels mirrored | Tick *Invert X* / *Invert Y* |
| Idle mouse fights the gun for the crosshair | Shouldn't happen (mouse only takes over when it moves) — if it does, something else is warping the cursor |
| White border appears unexpectedly | SindenModule detected (or was forced into) Sinden mode — untick *Force Sinden Mode* / disable the module |
| `System.IO.Ports` compile errors | Player Settings → Api Compatibility Level must be **.NET Framework** (Step 0) |

Related reading in this folder: `architecture-map.md` (visual diagrams of everything in
Part 1), `integration-guide.md` (hardware deep-dive, per-gun protocols and gotchas, multiplayer
§5) and `raw-input-research.md` (why 2-player mouse-guns would need extra work).
