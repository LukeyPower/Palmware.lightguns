using Blamcon.Lightguns;
using Blamcon.Lightguns.LowLevel;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  BlamconModule
    // ---------------------------------------------------------------------------
    //  Gun module for the Blamcon USB-HID lightgun (via the com.blamcon.lightguns
    //  package). The gun must be in GAMEPAD mode (set in Blamcon ARC) for the
    //  package's BlamconLightgunHID layout to claim it; in mouse/keyboard mode it
    //  enumerates as a plain mouse and this module simply reports not-connected.
    //
    //  WHY gamepad mode only (verified against the upstream package + Blamcon
    //  docs — see Documentation~/integration-guide.md §2):
    //   - The package's InputDeviceMatcher claims ONLY HID devices advertising
    //     usagePage 0x01 + usage 0x05 (Gamepad), vendorId 0x3673, productId
    //     0x0100–0x0103 (P1–P4). That HID fingerprint only exists in gamepad mode.
    //   - In mouse/keyboard mode the gun re-enumerates as a standard composite
    //     mouse+keyboard: Windows' generic input drivers claim it, it moves the
    //     real OS cursor, and Unity sees just another Mouse. Nothing Blamcon-
    //     specific is left to match — and there is no writable Blamcon HID
    //     endpoint, so recoil/rumble/LED/ammo commands have nowhere to go.
    //   - MnK mode is NOT a dead end though: the gun still plays through
    //     LightgunManager's mouse fallback (cursor aim + left-click fire), and
    //     in EVERY emulation mode the gun also exposes a USB serial COM port
    //     (VID 3673, 9600 8N1) that can detect it (ZD.1024 handshake), fire
    //     recoil (F.0.1.<n> — how Hook of the Reaper does it), and even switch
    //     the emulation mode at runtime (ZY.3.1 = gamepad, ZY.3.0 = MnK; the
    //     gun saves the setting and re-enumerates). See Blamcon's published
    //     serial-command reference — full protocol in Documentation~/integration-guide.md.
    //     What MnK mode permanently lacks: HID absolute aim, per-player
    //     identity, and multiplayer (one OS cursor).
    //
    //  Aim      : the gun reports an absolute on-screen position on its 'position'
    //             control (raw range 0–32768), already remapped to screen pixels
    //             by the package's AbsolutePositionRemapProcessor. (Gamepad mode
    //             does NOT move the OS mouse — that's why aim is read here, not
    //             from Mouse.current.)
    //  Feedback : HID output reports — recoil, rumble, ammo LEDs, RGB LEDs.
    //             EnableFFBControl hands feedback control to the game (otherwise
    //             the firmware auto-recoils on every trigger pull); it must be
    //             re-sent on every hot-plug, which OnDeviceChange handles.
    //             Requires firmware 1.0.16+.
    //             NOTE (upstream limitation): the driver/hardware services only
    //             ONE feedback IOCTL at a time — commands sent in quick
    //             succession get dropped. To fire several effects at once, pack
    //             them into a single BlamconHIDOutputReport and SendCommand it.
    // ===========================================================================
    public class BlamconModule : LightgunModule
    {
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

        [Header("Feedback strength")]
        [SerializeField] private int recoilPulses = 1;
        [SerializeField] private int rumblePulses = 1;

        [Header("Aim")]
        [Tooltip("Feed the gun's absolute aim position into LightgunManager.AimPosition.")]
        [SerializeField] private bool provideAim = true;

        [SerializeField] private bool logDebugMessages = false;

        private Vector2 _lastAim;
        private bool _haveLastAim;

        public override string DisplayName => "Blamcon";

        public override bool IsConnected => InputSystem.GetDevice<BlamconLightgunHID>() != null;

        public override bool Owns(InputDevice device) => device is BlamconLightgunHID;

        protected override void OnEnable()
        {
            base.OnEnable();

            // Arm FFB on every gun already plugged in (P1–P4); late plug-ins are
            // handled by OnDeviceChange.
            if (takeFFBControl)
                foreach (var device in InputSystem.devices)
                    if (device is BlamconLightgunHID gun)
                        ArmFFB(gun);
        }

        public override void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is not BlamconLightgunHID gun) return;

            // The FFB-control handshake does not survive a power cycle — re-send it
            // every time the gun (re)appears or the firmware falls back to auto-recoil.
            if (takeFFBControl &&
                (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected))
                ArmFFB(gun);
        }

        private void ArmFFB(BlamconLightgunHID gun)
        {
            bool ok = gun.EnableFFBControl(controlRecoil, controlRumble, controlLED, controlAmmo);
            if (logDebugMessages)
                Debug.Log($"[Blamcon] FFB control {(ok ? "armed" : "FAILED")} on '{gun.displayName}'.");
        }

        // ── Aim ────────────────────────────────────────────────────────────────

        public override bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            if (!provideAim) return false;

            var gun = Lightgun.current;
            if (gun == null) return false;

            // Already in screen pixels thanks to the package's remap processor.
            screenPosition = gun.position.ReadValue();
            movedThisFrame = !_haveLastAim || (screenPosition - _lastAim).sqrMagnitude > 0f;
            _lastAim = screenPosition;
            _haveLastAim = true;
            return true;
        }

        // ── Feedback ───────────────────────────────────────────────────────────

        public override void Recoil(InputDevice firingDevice, int remainingAmmo)
        {
            // Kick the specific gun that fired (multi-gun ready); fall back to the
            // first connected Blamcon if the device is somehow gone already.
            var gun = firingDevice as BlamconLightgunHID ?? InputSystem.GetDevice<BlamconLightgunHID>();
            if (gun == null) return;

            if (controlAmmo)
            {
                // Recoil + ammo must travel in ONE output report: the driver only
                // services one feedback IOCTL at a time, so two back-to-back calls
                // (ActivateRecoil then SendAmmoCount) can drop the second.
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
            foreach (var device in InputSystem.devices)
                if (device is BlamconLightgunHID gun)
                    gun.ActivateRumble(rumblePulses);
        }

        public override void SetAmmo(int remainingAmmo)
        {
            if (!controlAmmo) return;
            foreach (var device in InputSystem.devices)
                if (device is BlamconLightgunHID gun)
                    gun.SendAmmoCount(remainingAmmo);
        }

        // ReloadFeedback: the Blamcon has no physical slide — nothing to reset.
    }
}
