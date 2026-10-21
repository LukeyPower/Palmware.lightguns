using System.Collections.Generic;
using Blamcon.Lightguns;
using Blamcon.Lightguns.LowLevel;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  BlamconModule
    // ---------------------------------------------------------------------------
    //  Gun module for the Blamcon USB-HID lightgun (via the vendored Blamcon
    //  device code in Runtime/Blamcon/Device/). The gun must be in GAMEPAD mode
    //  (set in Blamcon ARC) for the BlamconLightgunHID layout to claim it; in
    //  mouse/keyboard mode it plays as a plain mouse and this module simply
    //  reports not-connected (BlamconSerial handles MnK-mode guns).
    //
    //  WHY gamepad mode only (verified against the upstream package + Blamcon
    //  docs — see Documentation~/integration-guide.md §2):
    //   - BlamconLightgunHID claims ONLY HID devices advertising usagePage 0x01 +
    //     usage 0x05 (Gamepad), vendorId 0x3673, productId 0x0100–0x0103
    //     (P1–P4). That HID fingerprint only exists in gamepad mode.
    //   - In mouse/keyboard mode the gun re-enumerates as a standard composite
    //     mouse+keyboard: Windows' generic input drivers claim it, it moves the
    //     real OS cursor, and Unity sees just another Mouse — no absolute aim,
    //     no per-player identity, one shared cursor.
    //   - Feedback in MnK mode: firmware 2.1.0+ exposes a vendor-defined HID
    //     collection alongside the mouse and keyboard collections (upstream 2.0's
    //     BlamconMouseModeDevice), and this module drives it — so an MnK gun gets
    //     HID feedback without being switched out of mouse mode. That is the
    //     transport Blamcon is standardising on: it survives unplug/replug, has no
    //     COM port to be reassigned, and is the only one that works over Bluetooth.
    //     On firmware below 2.1.0 the collection doesn't exist, GunFor() finds
    //     nothing, and BlamconSerial's serial path (ZD.1024 handshake, F.0.1.<n>
    //     recoil) carries it instead. Exactly one of the two is ever live: see
    //     BlamconSerial.HidOwnsMouseModeFeedback, which stands the serial path
    //     down whenever HID is available, so a shot never double-kicks.
    //
    //  Aim      : the gun reports an absolute on-screen position on its 'position'
    //             control (raw range 0–32768), already remapped to screen pixels
    //             by AbsolutePositionRemapProcessor. (Gamepad mode does NOT move
    //             the OS mouse — that's why aim is read here, not from Mouse.current.)
    //  Feedback : HID output report 0x10 — recoil, rumble, ammo LEDs, RGB LEDs.
    //             Taking FFB control stops the firmware auto-recoiling on every
    //             trigger pull; it must be re-sent on every hot-plug (handled in
    //             OnDeviceChange) and is handed back on disable/quit and, by
    //             default, while the app lacks focus. Requires firmware 1.0.16+.
    //             NOTE (upstream limitation): the gun services only ONE feedback
    //             command at a time — commands sent back to back get dropped. So
    //             Recoil/Rumble/SetAmmo don't send on the spot: everything asked
    //             for in a frame is packed into a single BlamconHIDOutputReport
    //             per player and sent once in LateUpdate.
    //  Players  : devices are resolved per player (BlamconLightgunHID.playerIndex,
    //             from the USB product id) and the most recently added device
    //             wins — after a firmware update Unity can keep a gun's old,
    //             dead device listed, which fails every command.
    //
    //  Don't also add Blamcon's own "Lightgun Session" component: it takes the
    //  same FFB control and every control command would be sent twice.
    // ===========================================================================
    public class BlamconModule : LightgunModule
    {
        private const int PlayerCount = 4;

        [Header("Force feedback control (sent to the gun on connect)")]
        [Tooltip("Take feedback control away from the firmware so the gun only recoils/rumbles when " +
                 "the game says so. Off = the gun auto-recoils on every trigger pull by itself.")]
        [SerializeField] private bool takeFFBControl = true;
        [SerializeField] private bool controlRecoil = true;
        [SerializeField] private bool controlRumble = true;
        [SerializeField] private bool controlLED = true;
        [Tooltip("Also take over the ammo LED bar. When on, Recoil()/SetAmmo() push the remaining " +
                 "ammo count to the gun.")]
        [SerializeField] private bool controlAmmo = false;
        [Tooltip("Hand feedback control back to the gun while the application doesn't have focus, and " +
                 "take it again when focus returns. Control is always handed back on disable/quit.")]
        [SerializeField] private bool releaseOnFocusLoss = true;

        [Header("Feedback strength")]
        [SerializeField] private int recoilPulses = 1;
        [SerializeField] private int rumblePulses = 1;

        [Header("Mouse-mode guns (firmware 2.1.0+)")]
        [Tooltip("Drive a mouse-mode gun's feedback over its vendor HID collection instead of leaving it " +
                 "to BlamconSerial. Needs firmware 2.1.0+; on older firmware the collection doesn't exist, " +
                 "this finds nothing, and BlamconSerial's serial path takes over automatically.")]
        [SerializeField] private bool claimMouseWhileMouseModeHid = true;

        [Header("Aim")]
        [Tooltip("Feed the gun's absolute aim position into LightgunManager.AimPosition.")]
        [SerializeField] private bool provideAim = true;

        [SerializeField] private bool logDebugMessages = false;

        // Every enabled BlamconModule. BlamconSerial stands aside for a mouse-mode gun only
        // while one of these has claimMouseWhileMouseModeHid on, so turning that off (or not
        // having a BlamconModule at all) hands the gun back to serial instead of to nothing.
        private static readonly List<BlamconModule> _enabledModules = new List<BlamconModule>();

        private Vector2 _lastAim;
        private bool _haveLastAim;
        private bool _holdingControl;
        private int _lastAmmo = -1;   // last count the game pushed; -1 = none yet

        public override string DisplayName => "Blamcon";

        // True while an enabled module will drive a mouse-mode gun over HID.
        internal static bool ClaimsMouseModeHid
        {
            get
            {
                foreach (var module in _enabledModules)
                    if (module.claimMouseWhileMouseModeHid) return true;
                return false;
            }
        }

        // Enter Play Mode without a domain reload keeps statics; start each session empty.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _enabledModules.Clear();

        public override bool IsConnected
        {
            get
            {
                for (int player = 0; player < PlayerCount; player++)
                    if (GunFor(player) != null) return true;
                return false;
            }
        }

        // A gamepad-mode gun is ours outright. A mouse-mode gun fires through the
        // system Mouse (its vendor HID collection carries no input reports), so we
        // claim mouse shots too while one is present and reachable — that's what
        // routes their recoil here instead of to BlamconSerial.
        public override bool Owns(InputDevice device)
        {
            if (device is BlamconLightgunHID) return true;
            if (!claimMouseWhileMouseModeHid || !(device is Mouse) || !HasMouseModeGun) return false;

            // Same rule as BlamconSerial.Owns: when Unity exposes USB ids, only the gun's own
            // mouse (Blamcon's VID 0x3673) counts, so a desk-mouse click doesn't kick the gun.
            // Without ids, assume the single-gun case: any mouse is the gun.
            if (UsbDeviceId.TryGetVendorId(device, out int vid))
                return vid == 0x3673;
            return true;
        }

        // True while any player has a mouse-mode gun reachable over HID, i.e. the
        // gun is on firmware 2.1.0+. On older firmware this is always false and
        // BlamconSerial keeps the serial path.
        internal static bool HasMouseModeGun
        {
            get
            {
                for (int player = 0; player < PlayerCount; player++)
                    if (MouseModeGunFor(player) != null) return true;
                return false;
            }
        }

        // The mouse-mode HID device for a player, or null.
        internal static BlamconMouseModeDevice MouseModeGunFor(int player)
            => BlamconLightgunHID.GetForceFeedback(player) as BlamconMouseModeDevice;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (!_enabledModules.Contains(this)) _enabledModules.Add(this);

            // Arm FFB on every gun already plugged in (P1–P4); late plug-ins are
            // handled by OnDeviceChange.
            if (takeFFBControl && (!releaseOnFocusLoss || Application.isFocused))
                SetControlOnAllGuns(take: true);
        }

        protected override void OnDisable()
        {
            // Covers leaving play mode and quitting: never leave a gun under game
            // control, or it stays silent on trigger pulls after the game closes.
            if (_holdingControl)
                SetControlOnAllGuns(take: false);
            System.Array.Clear(_hasPending, 0, _hasPending.Length); // nothing queued outlives the release
            _enabledModules.Remove(this);
            base.OnDisable();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!takeFFBControl || !releaseOnFocusLoss || !isActiveAndEnabled) return;
            if (hasFocus != _holdingControl)
                SetControlOnAllGuns(take: hasFocus);
        }

        public override void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            // Either kind of Blamcon feedback device counts: a gamepad-mode gun, or
            // a mouse-mode gun's vendor HID collection (firmware 2.1.0+).
            if (device is not BlamconLightgunHID && device is not BlamconMouseModeDevice) return;

            // The FFB-control handshake does not survive a power cycle — re-send it
            // every time the gun (re)appears.
            if (_holdingControl &&
                (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected))
                SendControl(PlayerIndexOf(device), take: true);
        }

        private void SetControlOnAllGuns(bool take)
        {
            _holdingControl = take;
            for (int player = 0; player < PlayerCount; player++)
                if (GunFor(player) != null) SendControl(player, take);
        }

        private void SendControl(int player, bool take)
        {
            if (player < 0) return;
            if (!controlRecoil && !controlRumble && !controlLED && !controlAmmo) return;

            // Only touches the ticked components (unticked ones are left as they
            // are), so releasing recoil/rumble/LED never also resets ammo control.
            var report = BlamconForceFeedback.ControlReport(controlRecoil, controlRumble, controlLED, controlAmmo, take);

            // Taking ammo control zeroes the gun's display, so the current count rides
            // in the SAME report (a separate SetAmmo right behind it can be dropped).
            // Covers hot-plug and focus-regained, where the game has no reason to re-send.
            if (take && controlAmmo && _lastAmmo >= 0)
                report.SetAmmo(_lastAmmo);

            bool ok = BlamconLightgunHID.SendCommand(player, ref report);
            if (logDebugMessages)
                Debug.Log($"[Blamcon] FFB control {(take ? "take" : "release")} {(ok ? "sent" : "FAILED")} " +
                          $"on P{player + 1} ({(MouseModeGunFor(player) != null ? "mouse mode" : "gamepad mode")}).");
        }

        // The device feedback for a player goes to (0 = P1), or null: a gamepad-mode
        // gun if there is one, otherwise the mouse-mode HID collection on firmware
        // 2.1.0+. Most recently added wins, skipping stale devices left over from a
        // firmware update. Null means nothing reachable over HID — on pre-2.1.0
        // firmware in mouse mode that's expected, and BlamconSerial covers it.
        private static IForceFeedback GunFor(int player)
            => BlamconLightgunHID.GetForceFeedback(player);

        private static int PlayerIndexOf(InputDevice device) => device switch
        {
            BlamconLightgunHID gun => gun.playerIndex,
            BlamconMouseModeDevice mouseMode => mouseMode.playerIndex,
            _ => -1,
        };

        // ── Aim ────────────────────────────────────────────────────────────────

        public override bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            if (!provideAim) return false;

            var gun = Lightgun.current;
            if (gun == null) return false;

            // Already in screen pixels thanks to the remap processor.
            screenPosition = gun.position.ReadValue();
            movedThisFrame = !_haveLastAim || (screenPosition - _lastAim).sqrMagnitude > 0f;
            _lastAim = screenPosition;
            _haveLastAim = true;
            return true;
        }

        // Per-device aim (multiplayer): each Blamcon P1–P4 is its own crosshair.
        public override bool TryGetAim(InputDevice device, out Vector2 screenPosition)
        {
            screenPosition = default;
            if (!provideAim || device is not BlamconLightgunHID gun) return false;
            screenPosition = gun.position.ReadValue();
            return true;
        }

        // ── Feedback ───────────────────────────────────────────────────────────

        public override void Recoil(InputDevice firingDevice, int remainingAmmo)
        {
            // Kick the specific player whose gun fired (multi-gun ready). A mouse-mode
            // gun fires as the system Mouse, which carries no player identity, so fall
            // back to the first connected Blamcon — correct for single-player, and the
            // reason mouse-mode multiplayer still needs the gamepad-mode switch.
            _lastAmmo = remainingAmmo;

            int player = PlayerIndexOf(firingDevice);
            if (player < 0 || GunFor(player) == null)
            {
                player = -1;
                for (int p = 0; p < PlayerCount && player < 0; p++)
                    if (GunFor(p) != null) player = p;
            }
            if (player < 0) return;

            ref var cmd = ref Pending(player);
            cmd.SetRecoil(recoilPulses);
            if (controlAmmo) cmd.SetAmmo(remainingAmmo);
        }

        public override void Rumble()
        {
            for (int player = 0; player < PlayerCount; player++)
                if (GunFor(player) != null) Pending(player).SetRumble(rumblePulses);
        }

        public override void SetAmmo(int remainingAmmo)
        {
            // Remembered even with no gun plugged in, so one that connects later
            // shows the right count straight away (see SendControl).
            _lastAmmo = remainingAmmo;
            if (!controlAmmo) return;
            for (int player = 0; player < PlayerCount; player++)
                if (GunFor(player) != null) Pending(player).SetAmmo(remainingAmmo);
        }

        // ── One report per gun per frame ───────────────────────────────────────
        //
        // The gun services ONE feedback command at a time and drops the rest (see
        // the class notes), and gameplay does ask for several in the same instant —
        // a shot that detonates a barrel in the player's face rumbles for the damage
        // and recoils for the shot. So nothing is sent on the spot: every effect
        // requested during a frame is packed into a single output report per player
        // and sent once, here.
        private readonly BlamconHIDOutputReport[] _pending = new BlamconHIDOutputReport[PlayerCount];
        private readonly bool[] _hasPending = new bool[PlayerCount];

        private ref BlamconHIDOutputReport Pending(int player)
        {
            if (!_hasPending[player])
            {
                _pending[player] = BlamconHIDOutputReport.Create();
                _hasPending[player] = true;
            }
            return ref _pending[player];
        }

        private void LateUpdate()
        {
            for (int player = 0; player < PlayerCount; player++)
            {
                if (!_hasPending[player]) continue;
                _hasPending[player] = false;
                BlamconLightgunHID.SendCommand(player, ref _pending[player]);
            }
        }

        // ReloadFeedback: the Blamcon has no physical slide — nothing to reset.
    }
}
