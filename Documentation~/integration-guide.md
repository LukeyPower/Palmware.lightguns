# Lightgun Integration Guide (Blamcon + Retro Shooter RS3 + Sinden)

A portable reference for the hardware behind this package and how it's wired into Unity's
Input System. Originally written for Croc Hunter, evolved in Spell Slinger, now the canonical
guide for the `com.palmware.lightguns` package.

The golden rule: **game logic should never know which gun fired.** Read input through Input
System *actions*, and route everything else (aim, recoil, rumble, reload, detection,
hot-swap) through the `LightgunManager`. Adding a gun then means writing one module + adding
bindings, not touching gameplay code.

---

## 1. The three guns at a glance

| | Blamcon | RS3 Reaper | Sinden / mouse-type |
|---|---|---|---|
| **Input transport** | USB HID | USB HID | System mouse (driver moves cursor) |
| **Feedback transport** | HID output reports **or** serial | **Serial** COM port | Driver-side (no game API) |
| **Aiming** | HID `position` (absolute) | Joystick stick (absolute) or mouse | System mouse cursor |
| **Modes** | Mouse/KB **or** Gamepad (each + Bluetooth) | Mouse+KB **or** Joystick | n/a |
| **Switch mode from code?** | ✅ `ZY.3.<mode>` (serial) | ✅ `ZJ`/`ZM` (serial) | n/a |
| **Mode remembered on power-off?** | Yes (saved firmware setting) | ❌ No — re-send on connect | n/a |
| **Recoil depends on input mode?** | HID recoil needs Gamepad; **serial recoil works in any mode** | ❌ No (serial is independent) | Driver handles it |

---

## 2. Blamcon

### Recognition
The gun presents to USB in three emulation modes (set in Blamcon ARC, or over serial):
**Mouse+Keyboard (composite device)**, **Gamepad**, or **Joystick**. The
`com.blamcon.lightguns` package registers a `BlamconLightgunHID` layout that matches **only**:
- `vendorId 0x3673`, `productId 0x0100`–`0x0103` (P1–P4), `usagePage 0x01`, `usage 0x05` (Gamepad).

So the gun must be in **Gamepad mode** for the package (and `BlamconModule`) to claim it.

Why the other modes don't produce a `BlamconLightgunHID`:
- **Mouse/Keyboard mode**: the gun re-enumerates as a *standard composite mouse+keyboard*.
  Windows' generic drivers claim it, aiming moves the real OS cursor, Unity sees just another
  `Mouse` — nothing Blamcon-specific to match, so `InputSystem.GetDevice<BlamconLightgunHID>()`
  returns **null**. The gun still *plays* through the manager's mouse fallback (cursor aim +
  `<Mouse>/leftButton` fire), and recoil/rumble/LED are still reachable over the serial config
  port (§2 serial). What MnK mode loses for good: HID absolute aim, per-player identity,
  multiplayer.
- **Joystick mode**: the package registers no layout for the joystick usage, so it's equally
  invisible. Gamepad mode is the only mode `BlamconModule` sees.

The point of Gamepad mode is that the gun is *not* a mouse: the package exposes it as a
pointer-like device with an absolute `position` without touching the OS cursor — which is what
makes 4 simultaneous guns possible (distinct P1–P4 product IDs, no fighting over one cursor).

Requirements: **Unity 6**, **Input System 1.14+**, gun firmware **1.0.16+**.

### The serial configuration port (present in EVERY emulation mode)

> Source: Blamcon's **published serial-command reference**
> (https://blamcon.com/get-started-with-blamcon/serial-commands/). Every Blamcon exposes a
> **USB CDC virtual COM port in all emulation modes** — this is how Blamcon's own tools find and
> configure guns regardless of mode.

**Finding the port (Windows)**: enumerate `HKLM\SYSTEM\CurrentControlSet\Enum`, match device IDs
`VID_3673&PID_xxxx` (PIDs `0100`–`0103` = wired P1–P4, `0200`–`0203` = wireless), read
`Device Parameters\PortName` (e.g. `COM5`). On Linux the guns are the `/dev/ttyACM*` ports.
Filtering by VID means we never write probe commands at some other device's COM port (e.g. the
RS3's).

**Port settings**: `9600` baud, 8-N-1, no handshake, **DTR + RTS enabled**, newline-terminated
ASCII (`SerialPort.WriteLine`), ~500 ms timeouts.

**Command set** (firmware ≥ 1.0.14 unless noted):

| Command | Meaning |
|---|---|
| `ZD.1024` | Handshake / firmware version (confirm "this COM port is a Blamcon") |
| `ZD.1023` / `ZD.1022` | Product name / manufacturer |
| `ZD.1020` / `ZD.1021` | USB VID / PID |
| `ZD.<idx>` | Read setting by index (integer reply) |
| `ZY.<idx>.<value>` | Write setting **and reboot/re-enumerate** |
| `ZY.<idx>.<value>.0` | Write setting **without** rebooting (fw ≥ 1.0.14) |
| `ZE` / `ZC` | Reboot / factory reset |
| `S6` / `E` | Start / end serial feedback (external control) mode |
| `F.0.1.<pulses>` | **Recoil** (works in any emulation mode) |
| `F.1.1.<pulses>` | Rumble/haptic |
| `F.2.1.<rgbInt>[.<pulses>]` / `F.2.0` | LED color on (24-bit int) / off |
| `CB1` / `CB0`, `T1` / `T0` | Calibration start/stop, test mode |

**Setting index 3 = emulation mode** (`0` = Mouse/KB, `1` = Gamepad, `2` = Mouse/KB Bluetooth,
`3` = Gamepad Bluetooth). Other useful indices: `0` = player ID, `4` = full-auto recoil,
`5` = offscreen reload, `7` = IR sensitivity.

**Switching modes at runtime** (what `BlamconSerial` does):
1. Read current mode: `ZD.3`, parse the integer (remember it to restore later).
2. Write new mode: `ZY.3.1` — the gun **saves, reboots, and re-enumerates** in Gamepad mode a
   couple of seconds later. Wait for `InputSystem.onDeviceChange` to see the `BlamconLightgunHID`.
3. Restore on exit: `ZY.3.0`. The setting persists across power-off, so restoring is a courtesy
   to the user's other software, not a requirement.

**In this package** `BlamconSerial` implements all of the above: drop it next to
`LightgunManager` and a gun plugged in wearing the "wrong" mode is detected, switched to Gamepad
mode, claimed by `BlamconModule`, and put back on quit. `SerialRecoilAll` is a serial
recoil/rumble fallback (the "Hook of the Reaper" trick) for the auto-switch-off case.

### Aiming
In Gamepad mode the gun reports an absolute on-screen position via `Lightgun.current.position`,
already remapped to screen pixels by the package's `AbsolutePositionRemapProcessor`. Bind the
aim action to `<LightGun>/position`.

### Force feedback (HID output reports)
Via `BlamconLightgunHID`:
- `EnableFFBControl(recoil, rumble, led, ammo)` — hand control to the game so the gun only acts
  on game commands (otherwise firmware auto-recoils on every trigger pull). Call on connect;
  **re-send on hot-plug** (`OnDeviceChange`). Requires firmware 1.0.16+.
- `ActivateRecoil(pulses)`, `ActivateRumble(pulses)`, `SendAmmoCount(n)`, LED color commands.
- **One IOCTL at a time** (upstream limitation): the driver services one feedback command at a
  time; back-to-back commands are silently dropped. To fire several effects together (recoil +
  ammo), pack them into a single `BlamconHIDOutputReport` (`SetRecoil` + `SetAmmo` + …) and send
  with `SendCommand(ref cmd)`. `BlamconModule.Recoil()` does this when ammo control is on.

### Gotchas
- Gamepad mode does **not** drive the OS mouse — read `<LightGun>/position`, not `Mouse.current`.
- An idle system mouse can overwrite the gun's aim every frame — only follow the mouse when it
  actually moved (`Mouse.current.delta != 0`). See `LightgunManager.Update()`.
- **Trigger mapping can differ per gun**: the package designates `buttonWest` as the primary
  trigger, but the ARC manual lists the gamepad-mode default as *South*, and users can remap. If
  the trigger doesn't register, check the gun's ARC button assignments before suspecting code.

---

## 3. Retro Shooter RS3 Reaper

### Input
A HID input interface (trigger/buttons) **plus** a separate virtual **serial COM port** used
purely for output (recoil, ammo LEDs, rumble, slide). Inputs come through the Input System; the
COM port is output-only.

### Two input modes
- **Mouse + Keyboard mode**: aiming drives the OS cursor; trigger = mouse click.
- **Joystick mode** (preferred): enumerates as a generic **Joystick**. Aiming X/Y form
  `<Joystick>/stick` (absolute, ≈ −1..1); trigger/buttons are joystick buttons.

Switch physically: hold **Start** + push the **joystick right ~3 s** (LED3 **yellow** = joystick,
**green** = mouse+keyboard). Switch from code: `ZJ` / `ZM` over serial. **The mode is forgotten
on power-off**, so send the desired mode on connect.

### Serial command set
Format: **`115200, 8, 1, n`**. Short ASCII strings written raw (no terminator needed):

| Command | Meaning |
|---|---|
| `ZS` | Enter external control mode (send first) |
| `ZX` | Exit external control mode |
| `ZJ` / `ZM` | Switch to **joystick** / **mouse+keyboard** mode |
| `Z0`–`Z5` | Recoil + light that many ammo LEDs (`Z5` = full) |
| `ZZ` | Rumble |
| `Z6` | Slide return (reload: slide returns, 5 orange LEDs) |

Recoil rate limit: ~8–12/sec, ≥83 ms between commands. (Protocol from the MIT-licensed Retro
Shooter example — see `THIRD-PARTY-NOTICES.md`.)

### Recognition (for routing recoil)
An RS3 appears as a HID device whose description contains manufacturer **`3AGAME`** or product
**`Retro Shooter`** (e.g. `<HID::3AGAME 3A-3H Retro Shooter 1>`). `RetroShooterModule.Owns` matches
on those strings.

### Why joystick mode
Recoil is serial and works in **either** mode. But for the trigger to be *identifiable* as the
RS3 (so recoil routes to it and not a real mouse), joystick mode is cleanest: the trigger arrives
as `<Joystick>/trigger` on the recognizable `3AGAME` device. Trade-off: joystick mode no longer
drives the mouse, so aim + reload are read from the joystick (stick + a joystick button).

### Gotchas
- Switching mode **re-enumerates** the gun on USB. Send `ZJ` shortly after opening the port;
  `RetroShooterModule.OnDeviceChange` also reconnects the serial side on hot-plug.
- The generic joystick `stick` applies a centre **deadzone** — read `stick.ReadUnprocessedValue()`
  for absolute aim so the screen centre isn't dead-spotted.
- The IR sensor **jitters** — run the mapped position through a low-pass filter
  (`Vector2.SmoothDamp`, ~0.04–0.08 s). See `RetroShooterModule.aimSmoothTime`.
- If the COM port is wrong/unavailable, all output is a silent no-op — keep the port name
  configurable and log the connection state.
- `System.IO.Ports` needs the **.NET Framework** API compatibility level (it doesn't exist under
  .NET Standard).

---

## 4. Sinden (and other mouse-emulating guns)

The Sinden is a **camera in the barrel**: its PC driver processes the video, finds the screen,
and **moves the OS mouse cursor**; the trigger is a left click. Game-side, a Sinden IS the system
mouse — so single-player support is almost free (the manager's mouse aim + `<Mouse>/leftButton`
Fire binding cover it). Same for AimTrak, Gun4IR, Wiimote+DolphinBar.

What a native game must provide:
- **A white border around the screen edge, always visible while the gun is in use** — the camera
  edge-detects the display through it (critical on dark scenes). `SindenBorder` renders it;
  `SindenModule` shows/hides it.
- **Recoil**: fired by the Sinden driver itself — no public per-game API, so the module's feedback
  methods are deliberate no-ops.
- **Detection**: best-effort — the input half often enumerates as a generic HID mouse, so
  `SindenModule` scans device descriptions for "Sinden" AND exposes a manual toggle
  (`SetSindenMode`). While active, the system mouse is treated as the lightgun (`Owns(Mouse)`).

Limit: **two-player** mouse-type guns need Windows Raw Input (two cursors can't share one OS
mouse) — out of scope for this package.

---

## 5. Architecture: LightgunManager + modules

The game talks to **one class** — `LightgunManager`. Everything device-specific lives in
**`ILightgunModule`** plug-ins. The Input System carries plain button/axis input; the extras a
lightgun has (recoil, slide, ammo LEDs, serial ports, FFB handshakes) go through the manager.

```
                    ┌────────────────────┐
   game code  ←──►  │   LightgunManager  │   Fire/Reload events, AimPosition,
                    │  (scene singleton) │   Recoil/RumbleAll/ReloadAll/SetAmmoAll,
                    └─────────┬──────────┘   GunConnected/GunDisconnected
                              │ registry (ILightgunModule)
        ┌──────────────┬──────┴───────┬───────────────┐
   ┌────┴─────┐  ┌─────┴──────┐  ┌────┴─────┐   ... one module per gun
   │ Blamcon  │  │ RetroShoot │  │ Sinden   │
   │ Module   │  │ er Module  │  │ Module   │
   └────┬─────┘  └─────┬──────┘  └────┬─────┘
    HID output     serial COM      driver-side
    reports        ("Z0".."Z6")    + white border
```

**Input — device-agnostic via Input System actions** (`LightgunInputActions.inputactions`, in the
`Setup` sample):
- `Fire`   → `<Mouse>/leftButton`, `<LightGun>/buttonWest`, `<Joystick>/trigger`
- `Reload` → `<Mouse>/rightButton`, `<LightGun>/buttonSouth`, `<Joystick>/button4`
- `Point`  → `<Mouse>/position`, `<LightGun>/position`

The manager subscribes to `Fire`/`Reload` and re-raises them as C# events
(`FirePressed`/`ReloadPressed`) carrying the firing `InputDevice`, its owning module (null =
mouse), and the aim position at that moment. It debounces trigger bounce (`minFireInterval`).

**Aim** is centralized in `LightgunManager.AimPosition` (screen pixels): the mouse and every
module's `TryGetAim` feed it, and whichever source **moved most recently** owns the crosshair.

**Feedback** goes game → manager → module:
- `Recoil(device, ammo)` → only the module whose `Owns(device)` is true (mouse shots kick nothing).
- `RumbleAll()` / `ReloadAll()` / `SetAmmoAll(n)` → every module.

**Detection & hot-swap**: the manager forwards `InputSystem.onDeviceChange` to every module (the
Blamcon module re-arms FFB on reconnect; the RS3 module retries its serial port), then raises
`GunConnected`/`GunDisconnected` for the game/UI.

### Adding another gun
1. Make sure Unity recognizes it (a HID layout / the right mode).
2. Add its bindings to the `Fire`/`Reload`/`Point` actions.
3. Write a module deriving from `LightgunModule` (auto-registers while enabled): implement `Owns`,
   `IsConnected`, and whichever of `TryGetAim` / `Recoil` / `Rumble` / `ReloadFeedback` /
   `SetAmmo` / `OnDeviceChange` the hardware supports.
4. Drop the component next to `LightgunManager`. **No changes to the manager or gameplay code.**

---

## 6. Key source files (this package)

- `Runtime/LightgunManager.cs` — the hub (events, aim arbitration, feedback routing, hot-swap).
- `Runtime/ILightgunModule.cs` — the per-gun module interface.
- `Runtime/LightgunModule.cs` — MonoBehaviour base with auto-registration + no-op defaults.
- `Runtime/Modules/RetroShooterModule.cs` — RS3 serial driver (mode switching, recoil/slide/rumble,
  joystick aim + smoothing).
- `Runtime/Modules/SindenModule.cs` — Sinden / mouse-gun compatibility (detection + manual toggle,
  drives the border).
- `Runtime/SindenBorder.cs` — the white tracking border overlay.
- `Runtime/Blamcon/BlamconModule.cs` — Blamcon HID module (aim + recoil/rumble/LED/ammo, FFB re-arm
  on hot-plug). In the optional `Palmware.Lightguns.Blamcon` assembly.
- `Runtime/Blamcon/BlamconSerial.cs` — Blamcon serial companion (detect in any mode, auto-switch to
  Gamepad, restore on quit, serial recoil fallback).
- `Samples~/Setup/LightgunInputActions.inputactions` — Fire/Reload/Point/Submit bindings.

## 7. Sources
- Retro Shooter RS3 Reaper — https://retroshooter.com/rs3-reaper/ ; user manual on manuals.plus.
- Blamcon serial commands (official) — https://blamcon.com/get-started-with-blamcon/serial-commands/
- Blamcon ARC manual — https://blamcon.com/support/manual/advanced-remote-console/
- Blamcon Unity package (Props3D, MIT) — https://github.com/Props3D/UnityLightguns
- Retro Shooter example (Grimbar Interactive, MIT) — source of the RS3 serial protocol.
- Hook of the Reaper (arcade-drive gun feedback) — https://hotr.6bolt.com/
- Unity Input System — HID — https://docs.unity3d.com/Packages/com.unity.inputsystem@1.14/manual/HID.html
