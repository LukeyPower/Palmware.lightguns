# Raw Input & Light Guns — Research Notes

Research into whether games built on this package need Windows **Raw Input**, gathered
July 2026 (originally for Spell Slinger). Covers: what raw input is, why light gun games
use it, how it's set up, whether it can be "always on" for every device, how Sinden /
Retro Shooter / Blamcon each behave, the Blue Estate case study, and a recommendation.

**TL;DR:** Raw input exists to solve ONE problem — telling apart devices that Windows merges
into a single system cursor (i.e. guns that pretend to be **mice**). The Blamcon in Gamepad mode
and the RS3 in Joystick mode are *not* mice — each shows up as its own distinct HID device — so
this package's architecture works without raw input. You'd only need it to support **mouse-mode
guns (Sinden, AimTrak, Gun4IR, RS3 in mouse mode) — and even then only really for 2+ players**.

---

## 1. What is Raw Input?

Raw Input is a Win32 API (Windows XP+) that lets an application receive input **directly from
each HID device**, bypassing Windows' normal input pipeline (the merged cursor, pointer
acceleration, ballistics).

Two things it gives you that normal input can't:

1. **Per-device identity.** Windows normally merges *every* pointing device — all mice, and any
   light gun emulating a mouse — into ONE system cursor. A regular app can't tell which physical
   device moved it. Raw input events carry a device handle, so you know *which* mouse/gun sent
   each movement or click.
2. **Unprocessed values.** No pointer acceleration, no clamping to the screen, sub-device-unit
   deltas — and for absolute devices (light guns, tablets), the actual absolute coordinates the
   device reported rather than the OS cursor position.

Historically: DirectInput supported multiple mice before Windows XP removed it; raw input is the
replacement, originally driven by MAME needing multiple light guns/trackballs
([jstookey.com](https://jstookey.com/multiple-mice-raw-input/),
[MS docs — Raw Input Overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)).

## 2. How it's set up (the mechanics)

- The app calls **`RegisterRawInputDevices()`**, registering for HID *usage pages/usages*
  (e.g. "all mice", "all keyboards", "all gamepads") — **not** for one specific device.
- Windows then posts **`WM_INPUT`** messages to the app's window queue; each message carries the
  raw data plus a **device handle** identifying the exact physical device.
- `GetRawInputDeviceList()` / `GetRawInputDeviceInfo()` enumerate what's connected (VID/PID,
  device path), which is how you'd say "handle 0x123 = Sinden P1, handle 0x456 = Sinden P2".
- Gotchas (from the excellent [PH3 write-up](https://ph3at.github.io/posts/Windows-Input/)):
  - Registration is **process-wide** and effectively can't be toggled off cleanly afterwards.
  - Raw input **coexists** with normal input by default — you keep the regular cursor and all
    existing input code; raw input is an *additional* stream. (`RIDEV_NOLEGACY` suppresses the
    normal messages, but then window dragging/clicking UI breaks — games rarely want it.)
  - High-polling-rate mice can flood the message queue; batched reads (`GetRawInputBuffer`)
    exist for that.

**In Unity specifically:** Unity's Input System already uses WM_INPUT internally for mouse
*deltas*, but it still exposes only **one merged `Mouse` device** — it does not distinguish
multiple mice, and there is no built-in per-device mouse support
([Unity discussions](https://discussions.unity.com/t/multiple-mice-with-input-system/944203)).
Getting per-device raw input in Unity means a **native plugin / Win32 interop layer** that hooks
the window's message loop (or a hidden message window) and surfaces WM_INPUT per-device. Community
examples: [Unity-MultiInput](https://github.com/ElishaAz/Unity-MultiInput),
[Many-Mouse-Unity](https://github.com/jackyyang09/Many-Mouse-Unity),
[petersvp's C++ DLL + C# gist](https://gist.github.com/petersvp/5e0a60c3d6e1ba6b7685807abf4ad799).
It's Windows-only and lives outside the Input System's action/binding pipeline — exactly the kind
of thing that would be wrapped inside a `RawInputMouseGunModule` in this package's architecture,
never touching game code.

## 3. Can it be "always on" for all devices, or is it per-gun?

It's **app-wide, per device *class*** — you register once for e.g. all HID mice and you then get
raw events from *every* mouse-like device (real mice included) alongside normal input. It is
**not** a per-gun setting, and nothing about it needs to be turned on per device. Your code then
*chooses* which device handles to treat as guns (by VID/PID) and which to ignore (real mouse).

So yes — a game can leave raw input on permanently, keep reading keyboard/gamepads through the
normal input system, and use the raw stream only to disambiguate mouse-type guns. The usual
pattern in light gun games (and Blue Estate does exactly this as a menu option) is a settings
toggle, because raw absolute coordinates from a gun behave slightly differently from the OS
cursor (no OS smoothing/acceleration, and window-relative mapping is on you).

## 4. The gun-by-gun survey

### Sinden — mouse emulation; raw input is how games do 2 players
The Sinden works by **controlling the OS mouse cursor** — that's its whole compatibility story
("works with anything that accepts a mouse", [sindenlightgun.com](https://sindenlightgun.com/details/)).
Consequences:
- **1 player**: no raw input needed — the game just reads the cursor.
- **2 players**: impossible through normal input (both guns fight over one cursor). Emulators
  solve it with raw input device selection: RetroArch's multi-mouse indexes, PCSX2's
  "HID Mouse API = RawInput", Flycast's per-port raw devices, RPCS3's raw input 2-gun support
  ([sindenwiki 2-player](https://www.sindenwiki.org/wiki/2_player),
  [emuline RPCS3 thread](https://www.emuline.org/topic/3665-emu-rpcs3-2-lightguns-raw-input-support-is-available/)).
- Native PC games that "support Sinden" for 2P are the ones that implemented raw input.

### Retro Shooter RS3 — raw input NOT needed
The RS3 enumerates as mouse+keyboard by default, or as a **generic joystick** after switching
mode (physical combo or `ZJ` serial command; see `integration-guide.md` §3). In joystick mode the
gun is a *distinct* device with absolute stick coordinates — Unity's Input System sees it
individually, no cursor merging, no raw input. In **mouse mode** the *aim* has the same merged
cursor as a Sinden, but recoil routing still works without raw input: `RetroShooterModule`
claims mouse-device shots while its serial port is open (single-gun MnK-first policy), and the
moment 2+ guns exist the module escalates MnK-mode guns to joystick mode (`ZJ`) so each has its
own identity. Multi-player: each RS3 is its own joystick + its own COM port, so 2P works without
raw input too
([Retro Shooter manual](https://retroshooter.com/wp-content/uploads/2025/02/Retro-Shooter-Lightgun-User-Manual-2025-1.pdf)).

### Blamcon — raw input NOT needed (in gamepad mode); that's why it "just worked"
In **Gamepad mode** the Blamcon enumerates as its own HID gamepad — VID `0x3673`, PID
`0x0100`–`0x0103` for P1–P4 — reporting **absolute position in the HID report itself**. The
bundled Blamcon device code (vendored from `com.blamcon.lightguns`) registers a custom Input System layout that claims exactly those
devices, and the `AbsolutePositionRemapProcessor` maps the raw 0–32768 report values to screen
pixels. So the game gets per-gun identity AND absolute aim through Unity's normal input path —
the two things raw input exists to provide, already solved at the HID layout level. **That is
why Croc Hunter worked without raw input**, and it stays true for up to 4 Blamcons
simultaneously (P1–P4 are distinct products).
In the Blamcon's **Mouse/Keyboard mode** the aim is back in Sinden territory (merged cursor, no
HID FFB) — this package covers a single MnK-mode gun via the mouse path plus **serial** recoil
(`BlamconSerial`), and switches guns to Gamepad mode when 2+ guns need per-device identity.

### Blue Estate — the case study for "native lightgun PC game"
Blue Estate (Steam, 2015) is the go-to native rail shooter people run Sindens with. Relevant
findings:
- It has an in-game **"Mouse in RAW mode"** option — the Sinden wiki tells you to turn it on
  ([sindenwiki Blue Estate](https://www.sindenwiki.org/wiki/Blue_Estate)). That mode makes the
  game read the mouse through raw input instead of the smoothed OS cursor, which reads absolute
  gun coordinates 1:1 (no OS acceleration/smoothing lag on a jittery IR sensor).
- Even with that option, shipping Blue Estate **only supports one mouse** — so one gun. 2-player
  needed a fan patch (by "Mystery Wizard") that adds per-device raw input routing
  ([Steam discussion](https://steamcommunity.com/app/305380/discussions/0/2963893984923196595/)).
- Lesson: a lightgun game aiming (only) at mouse-type guns should implement raw input from day
  one *if it wants 2P*; a game aiming at gamepad-type guns (Blamcon) doesn't need it at all.

## 5. So why would this package ever want raw input?

Reasons **for**:
1. **Sinden / AimTrak / Gun4IR / Wiimote-bar support** — the whole ecosystem of guns that only
   emulate a mouse. That's a big chunk of the home lightgun market; "works with my Sinden" is a
   common ask for PC lightgun games.
2. **2 players with mouse-type guns** — flatly impossible without it.
3. **Cleaner absolute aim from mouse-type guns** — bypasses OS pointer acceleration/smoothing
   (Blue Estate's RAW mode exists for this even in 1P).

Reasons **against / why it hasn't been needed**:
1. Blamcon (Gamepad mode) and RS3 (Joystick mode) both already give per-device absolute input —
   the package's primary guns don't need it, including multi-gun setups.
2. It's Windows-only native-plugin work outside Unity's action system (editor quirks included).
3. It adds a second input path to maintain and test.

## 6. Recommendation

- **Don't build raw input now.** The supported guns are covered without it, 1–4 guns work,
  and the manager/module architecture means nothing about consuming game code would change later.
- **Design for it, cheaply (already done):** if 2-player Sinden-class support is ever wanted, it
  slots in as one new module — e.g. `RawInputGunModule` wrapping a small Win32 WM_INPUT plugin,
  exposing `Owns()`/`TryGetAim()` per device handle like any other module. Game code and
  `LightgunManager` stay untouched. That decision can wait until there's real demand (e.g. a
  Steam release wanting multi-player Sinden compatibility).
- If it's ever done: keep it as an **options toggle** (like Blue Estate), register for the mouse
  usage page only, don't use `RIDEV_NOLEGACY`, and filter gun VID/PIDs from real mice.

## Sources

- [Raw Input Overview — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)
- [Using Raw Input — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input)
- [Things you really should know about Windows Input — PH3 blog](https://ph3at.github.io/posts/Windows-Input/)
- [Multiple Mice in Windows using Raw Input — jstookey.com](https://jstookey.com/multiple-mice-raw-input/)
- [Handling Multiple Mice with Raw Input — asawicki.info](https://asawicki.info/news_1533_handling_multiple_mice_with_raw_input)
- [Sinden wiki — Blue Estate](https://www.sindenwiki.org/wiki/Blue_Estate) · [2 player](https://www.sindenwiki.org/wiki/2_player) · [PCSX2](https://www.sindenwiki.org/wiki/PCSX2) · [Flycast](https://www.sindenwiki.org/wiki/Flycast_(Windows))
- [Sinden Lightgun — how it works](https://sindenlightgun.com/details/)
- [Blue Estate Steam discussion — Sinden/2nd mouse support](https://steamcommunity.com/app/305380/discussions/0/2963893984923196595/)
- [RPCS3 2-lightgun raw input — emuline](https://www.emuline.org/topic/3665-emu-rpcs3-2-lightguns-raw-input-support-is-available/)
- [PCSX2 two-lightgun feature request](https://github.com/PCSX2/pcsx2/issues/7888)
- [Retro Shooter RS3 Reaper](https://retroshooter.com/rs3-reaper/) · [User manual (PDF)](https://retroshooter.com/wp-content/uploads/2025/02/Retro-Shooter-Lightgun-User-Manual-2025-1.pdf)
- [Blamcon ARC manual](https://blamcon.com/support/manual/advanced-remote-console/) · [blamcon.com](https://blamcon.com/)
- [Multiple mice with Input System — Unity discussions](https://discussions.unity.com/t/multiple-mice-with-input-system/944203)
- [Unity-MultiInput](https://github.com/ElishaAz/Unity-MultiInput) · [Many-Mouse-Unity](https://github.com/jackyyang09/Many-Mouse-Unity) · [RawInput C++/C# gist](https://gist.github.com/petersvp/5e0a60c3d6e1ba6b7685807abf4ad799)
- [Configuring RetroArch for multi-mouse — libretro forums](https://forums.libretro.com/t/configuring-ra-for-multi-mouse-spinners-lightguns-tackballs/12619)
