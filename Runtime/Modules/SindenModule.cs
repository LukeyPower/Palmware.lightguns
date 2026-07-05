using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  SindenModule
    // ---------------------------------------------------------------------------
    //  Compatibility module for the Sinden lightgun (and by extension any
    //  mouse-emulating gun: AimTrak, Gun4IR, Wiimote+bar, ...).
    //
    //  How a Sinden works, and what that means for us:
    //    • The gun is a CAMERA. The Sinden driver software on the PC processes the
    //      video, finds the screen, and moves the OS MOUSE CURSOR; the trigger is
    //      a left mouse click. Game-side, the gun IS the system mouse — so aim and
    //      fire already flow through the manager's mouse path and the Fire action's
    //      <Mouse>/leftButton binding. Nothing to do there.
    //    • The camera needs a WHITE BORDER around the screen edge to track (that's
    //      how it edge-detects the display, especially on dark scenes). Games
    //      without a native border force players into overlay hacks — so this
    //      module's main job is driving the SindenBorder overlay.
    //    • Recoil/rumble are fired by the Sinden software itself (on trigger pull,
    //      or by tools like Demulshooter on hit) — there is no public per-game
    //      command API, so feedback methods here are deliberate no-ops.
    //    • TWO-PLAYER Sinden needs Windows Raw Input (two cursors can't share one
    //      OS mouse) — out of scope here; see Documentation~/integration-guide.md §4.
    //
    //  Detection is best-effort: the gun's input half often enumerates as a plain
    //  HID mouse/keyboard, but where Unity surfaces a device description containing
    //  "Sinden" we auto-detect. Always expose the manual toggle in the options menu
    //  (SetSindenMode) for setups the auto-detect misses.
    // ===========================================================================
    public class SindenModule : LightgunModule
    {
        [Tooltip("Scan device descriptions for 'Sinden' and enable automatically when found. " +
                 "Best-effort — the gun's input half can enumerate as a generic mouse, so keep " +
                 "the manual toggle available in the options menu too.")]
        [SerializeField] private bool autoDetect = true;

        [Tooltip("Manual override for when auto-detect can't see the gun (it's a settings-menu " +
                 "toggle at runtime via SetSindenMode). While active, the system mouse is treated " +
                 "as the lightgun and the border is shown.")]
        [SerializeField] private bool forceSindenMode = false;

        [Header("Border")]
        [Tooltip("Show the white tracking border while Sinden mode is active. Leave on — the gun " +
                 "cannot track without it.")]
        [SerializeField] private bool showBorder = true;

        [Tooltip("Border overlay to drive. Left empty, one is created on this GameObject.")]
        [SerializeField] private SindenBorder border;

        [SerializeField] private bool logDebugMessages = false;

        private bool _detected;

        public override string DisplayName => "Sinden";

        // Active = we are treating the system mouse as a lightgun + showing the border.
        public bool SindenModeActive => forceSindenMode || (autoDetect && _detected);

        public override bool IsConnected => SindenModeActive;

        // In Sinden mode the system mouse IS the gun (the driver moves the cursor,
        // the trigger clicks it) — so mouse events belong to this module. When the
        // mode is off, the mouse is just a mouse and owns nothing.
        public override bool Owns(InputDevice device) => SindenModeActive && device is Mouse;

        // Options-menu hook: "I'm using a Sinden / mouse-type lightgun".
        public void SetSindenMode(bool on)
        {
            forceSindenMode = on;
            RefreshBorder();
            if (logDebugMessages) Debug.Log($"[Sinden] Manual mode {(on ? "ON" : "OFF")}.");
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Detect();
            RefreshBorder();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (border != null) border.Hide();
        }

        public override void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (!autoDetect) return;
            if (change != InputDeviceChange.Added && change != InputDeviceChange.Removed &&
                change != InputDeviceChange.Reconnected && change != InputDeviceChange.Disconnected)
                return;

            Detect();
            RefreshBorder();
        }

        // Aim: the Sinden driver moves the OS cursor, and the manager's mouse-aim
        // path already follows it — this module supplies no aim of its own.

        // Recoil / Rumble / ReloadFeedback / SetAmmo: performed by the Sinden
        // driver software, not by the game — deliberate no-ops (base class).

        private void Detect()
        {
            bool was = _detected;
            _detected = false;

            foreach (var device in InputSystem.devices)
            {
                var d = device.description;
                if (Contains(d.manufacturer, "Sinden") || Contains(d.product, "Sinden"))
                {
                    _detected = true;
                    break;
                }
            }

            if (logDebugMessages && was != _detected)
                Debug.Log($"[Sinden] Auto-detect: {(_detected ? "found" : "not found")}.");
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void RefreshBorder()
        {
            if (!showBorder) return;

            if (border == null && SindenModeActive)
                border = gameObject.AddComponent<SindenBorder>();
            if (border == null) return;

            if (SindenModeActive) border.Show();
            else border.Hide();
        }
    }
}
