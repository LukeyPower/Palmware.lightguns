# Lightgun Layer — Architecture Map

Visual companion to `scene-setup-guide.md`. The diagrams below render automatically on GitHub
(and in VS Code with a Mermaid preview extension). A hand-styled single-page version is
`architecture-map.html` in this folder.

Reading key: **game code and gun hardware never talk to each other directly** — every
conversation passes through the `LightgunManager` switchboard.

## The layers — what talks to what

```mermaid
flowchart TB
    subgraph GAME ["LAYER 1 — your gameplay code (knows nothing about guns)"]
        SHOOT["Shooter script"]
        UI["Crosshair UI"]
        MENU["Options menu"]
    end

    subgraph MGR ["LAYER 2 — LightgunManager (the switchboard, a scene GameObject)"]
        M["listens to Fire/Reload actions ·
        keeps the module registry ·
        arbitrates AimPosition every frame ·
        watches device plug/unplug"]
    end

    ACTIONS["LightgunInputActions.inputactions
    (the binding table: mouse click, Blamcon trigger,
    RS3 trigger → all ONE 'Fire' action)"]
    UNITY["Unity Input System
    (watches the raw hardware)"]
    ROSTER["LightgunRoster (static count of
    physical guns, all families —
    2+ guns ⇒ modules switch MnK guns
    to their per-device mode)"]

    subgraph MODS ["LAYER 3 — one plug-in module per gun family"]
        BM["BlamconModule
        owns: BlamconLightgunHID
        aim: absolute position control
        re-arms FFB on re-plug"]
        BS["BlamconSerial (companion)
        finds Blamcons on serial in ANY mode ·
        serial recoil for MnK guns ·
        switches to Gamepad mode at 2+ guns"]
        RM["RetroShooterModule
        owns: '3AGAME' devices + COM port
        aim: joystick stick + smoothing
        MnK-first; Joystick mode (ZJ) at 2+ guns"]
        SM["SindenModule
        owns: system Mouse (when active)
        aim: none (driver moves OS cursor)
        drives the white SindenBorder"]
    end

    subgraph HW ["LAYER 4 — physical guns"]
        BG["Blamcon gun (P1–P4)"]
        RG["RS3 'Reaper' (P1–P4)"]
        SG["Sinden gun (camera)"]
    end

    SDRV["Sinden driver software
    (moves the OS mouse cursor)"]

    %% input path (events flowing up)
    UNITY -->|"'Fire' action fired + which device"| ACTIONS
    ACTIONS -->|"action events"| M
    M -->|"FirePressed / ReloadPressed / GunConnected events · AimPosition"| GAME

    %% game orders (flowing down)
    GAME -->|"Recoil(device, ammo) · RumbleAll() · ReloadAll() · SetAmmoAll(n)"| M

    %% manager <-> modules
    BM -->|"Register(this)"| M
    BS -->|"Register(this)"| M
    RM -->|"Register(this)"| M
    SM -->|"Register(this)"| M
    M -->|"Owns(device)? · TryGetAim()? · Recoil()!"| BM
    M -->|"same questions & orders"| BS
    M -->|"same questions & orders"| RM
    M -->|"same questions & orders"| SM

    %% mode policy
    BS <-->|"gun counts"| ROSTER
    RM <-->|"gun counts"| ROSTER

    %% modules -> hardware
    BM -->|"USB HID output reports"| BG
    BS -->|"serial COM: F.0.1.n recoil · ZY.3.x mode"| BG
    RM -->|"serial COM: Z0–Z6, ZZ · ZJ/ZM mode"| RG

    %% the Sinden loop
    SG -->|"camera video"| SDRV
    SDRV -->|"moves OS cursor / clicks"| UNITY
```

Note the **Sinden loop** at the bottom: the Sinden's hardware never receives anything from us.
Its camera feeds its own driver software, which moves the OS cursor — which re-enters the map at
the top through Unity's mouse input. Our only job for it is rendering the border
(`SindenBorder`) its camera needs to see.

And the **mode policy** on the left: `BlamconSerial` and `RetroShooterModule` each report their
physical gun count (countable in ANY input mode, via each family's serial side) to the static
`LightgunRoster`. One gun ⇒ it stays in whatever mode it arrived in and plays through the mouse
path with serial recoil (MnK-first). The moment the roster sees **2+ guns**, MnK-mode guns are
switched to their per-device mode — Blamcon → Gamepad, RS3 → Joystick — because two mice can't
share one OS cursor. Switched guns are restored on exit.

## One shot, end to end

```mermaid
sequenceDiagram
    autonumber
    actor P as Player
    participant U as Unity Input System
    participant M as LightgunManager
    participant B as BlamconModule
    participant G as Your shooter script
    participant HW as Blamcon gun

    P->>U: pulls the Blamcon trigger
    U->>M: "Fire" action performed (device = the Blamcon)
    Note over M: debounce trigger bounce
    M->>B: Owns(device)?
    B-->>M: yes, mine
    M->>G: FirePressed(device, module, AimPosition)
    Note over G: raycast from AimPosition,<br/>spend ammo (game's decision)
    G->>M: Recoil(device, ammoLeft)
    M->>B: Recoil(device, ammoLeft)
    B->>HW: USB HID recoil command
    Note over HW: the gun physically kicks
```

A **plain mouse click** follows steps 1–6 identically (that's the device-agnostic part) but
stops there: no module owns a mouse, so `Recoil` finds nobody and nothing kicks. (Exception by
design: while a single MnK-mode gun is confirmed on serial, its module claims mouse shots so
recoil still reaches the gun — see the MnK-first policy in `scene-setup-guide.md` Step 6.)

## Aiming, every frame

```mermaid
flowchart LR
    T["LightgunManager.Update()"] --> Q1{"Mouse moved
    this frame?"}
    Q1 -->|yes| MO["mouse owns the crosshair"]
    Q1 -->|no| Q2{"a module's TryGetAim
    reports movement?"}
    Q2 -->|yes| GO["that module owns it"]
    Q2 -->|no| KEEP["last owner keeps it"]
    MO --> A["AimPosition (screen px)"]
    GO --> A
    KEEP --> A
    A --> R["your crosshair UI + raycasts
    read this ONE value —
    never knowing who's driving"]
```

## Where everything lives

| Piece | File |
|---|---|
| The switchboard | `Runtime/LightgunManager.cs` |
| Module contract | `Runtime/ILightgunModule.cs` + `Runtime/LightgunModule.cs` (base) |
| Mode-policy gun count | `Runtime/LightgunRoster.cs` |
| Gun drivers | `Runtime/Modules/RetroShooterModule.cs`, `Runtime/Modules/SindenModule.cs`, `Runtime/Blamcon/BlamconModule.cs` |
| Blamcon serial companion | `Runtime/Blamcon/BlamconSerial.cs` |
| Sinden border overlay | `Runtime/SindenBorder.cs` |
| Binding table + smoke test | `Samples~/Setup/` (import via Package Manager) |
| Blamcon HID device layout | `Runtime/Blamcon/Device/` (vendored from `com.blamcon.lightguns`, MIT) |
| Setup walkthrough | `Documentation~/scene-setup-guide.md` |
