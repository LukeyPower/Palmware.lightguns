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
    //   - Feedback in MnK mode goes over the gun's USB serial COM port instead
    //     (BlamconSerial: ZD.1024 handshake, F.0.1.<n> recoil, ZY.3.x mode
    //     switch — works on every firmware). Firmware 2.1.0+ ALSO exposes a
    //     vendor-defined HID collection in mouse mode (upstream 2.0's
    //     BlamconMouseModeDevice). This module deliberately leaves that device
    //     alone: driving it as well as the serial path would double-kick.
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
    //             command at a time — commands sent back to back get dropped. To
    //             fire several effects at once, pack them into a single
    //             BlamconHIDOutputReport and SendCommand it.
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

        [Header("Aim")]
        [Tooltip("Feed the gun's absolute aim position into LightgunManager.AimPosition.")]
        [SerializeField] private bool provideAim = true;

        [SerializeField] private bool logDebugMessages = false;

        private Vector2 _lastAim;
        private bool _haveLastAim;
        private bool _holdingControl;

        public override string DisplayName => "Blamcon";

        public override bool IsConnected
        {
            get
            {
                for (int player = 0; player < PlayerCount; player++)
                    if (GunFor(player) != null) return true;
                return false;
            }
        }

        public override bool Owns(InputDevice device) => device is BlamconLightgunHID;

        protected override void OnEnable()
        {
            base.OnEnable();

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
            if (device is not BlamconLightgunHID gun) return;

            // The FFB-control handshake does not survive a power cycle — re-send it
            // every time the gun (re)appears.
            if (_holdingControl &&
                (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected))
                SendControl(gun, take: true);
        }

        private void SetControlOnAllGuns(bool take)
        {
            _holdingControl = take;
            for (int player = 0; player < PlayerCount; player++)
            {
                var gun = GunFor(player);
                if (gun != null) SendControl(gun, take);
            }
        }

        private void SendControl(BlamconLightgunHID gun, bool take)
        {
            if (!controlRecoil && !controlRumble && !controlLED && !controlAmmo) return;

            // Only touches the ticked components (unticked ones are left as they
            // are), so releasing recoil/rumble/LED never also resets ammo control.
            var report = BlamconForceFeedback.ControlReport(controlRecoil, controlRumble, controlLED, controlAmmo, take);
            bool ok = gun.SendCommand(ref report);
            if (logDebugMessages)
                Debug.Log($"[Blamcon] FFB control {(take ? "take" : "release")} {(ok ? "sent" : "FAILED")} " +
                          $"on '{gun.displayName}' (P{gun.playerIndex + 1}).");
        }

        // The live gamepad-mode device for a player (0 = P1), or null. Most
        // recently added wins, skipping stale devices left over from a firmware
        // update; a mouse-mode-only gun returns null (BlamconSerial's job).
        private static BlamconLightgunHID GunFor(int player)
            => BlamconLightgunHID.GetForceFeedback(player) as BlamconLightgunHID;

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
            // Kick the specific player whose gun fired (multi-gun ready); fall back
            // to the first connected Blamcon if the device can't be identified.
            var gun = firingDevice is BlamconLightgunHID fired && fired.playerIndex >= 0
                ? GunFor(fired.playerIndex)
                : null;
            if (gun == null)
                for (int player = 0; player < PlayerCount && gun == null; player++)
                    gun = GunFor(player);
            if (gun == null) return;

            if (controlAmmo)
            {
                // Recoil + ammo must travel in ONE output report: the gun only
                // services one feedback command at a time, so two back-to-back
                // calls (ActivateRecoil then SendAmmoCount) can drop the second.
                var cmd = BlamconHIDOutputReport.Create();
                cmd.SetRecoil(recoilPulses);
                cmd.SetAmmo(remainingAmmo);
                gun.SendCommand(ref cmd);
            }
            else
            {
                gun.ActivateRecoil(recoilPulses);
            }
        }

        public override void Rumble()
        {
            for (int player = 0; player < PlayerCount; player++)
                GunFor(player)?.ActivateRumble(rumblePulses);
        }

        public override void SetAmmo(int remainingAmmo)
        {
            if (!controlAmmo) return;
            for (int player = 0; player < PlayerCount; player++)
                GunFor(player)?.SendAmmoCount(remainingAmmo);
        }

        // ReloadFeedback: the Blamcon has no physical slide — nothing to reset.
    }
}
