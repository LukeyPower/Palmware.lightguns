using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  ILightgunModule
    // ---------------------------------------------------------------------------
    //  One physical gun family (or vendor SDK) that plugs into the LightgunManager.
    //  The game never talks to a module directly — it talks to the manager, and the
    //  manager routes each call to the module that owns the device in question.
    //
    //  A module bundles everything device-specific in one place:
    //    • device recognition            (Owns / IsConnected / OnDeviceChange)
    //    • an absolute aim source        (TryGetAim) — guns that aren't a mouse
    //    • physical feedback             (Recoil / Rumble / ReloadFeedback / SetAmmo)
    //
    //  Adding a new gun = write one module and register it with the manager
    //  (derive from LightgunModule for auto-registration). No changes to the
    //  manager or to gameplay code.
    // ===========================================================================
    public interface ILightgunModule
    {
        // Human-readable name for debug UI / logs (e.g. "Blamcon", "Retro Shooter RS3").
        string DisplayName { get; }

        // True while this module's hardware is usable (device present / port open).
        bool IsConnected { get; }

        // True if 'device' belongs to this module — used to route recoil to the gun
        // that actually pulled the trigger, and to tag input events with their module.
        bool Owns(InputDevice device);

        // Absolute aim source. Returns true when this module can currently supply a
        // screen-space aim position (in pixels); 'movedThisFrame' lets the manager
        // hand the crosshair to whichever source moved most recently. Modules with
        // no aim of their own return false.
        bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame);

        // Kick the gun after it fires. 'firingDevice' is the exact device that shot
        // (so multi-gun setups kick the right one); 'remainingAmmo' drives ammo LEDs
        // on guns that have them.
        void Recoil(InputDevice firingDevice, int remainingAmmo);

        // Buzz every gun this module owns (e.g. the player took damage).
        void Rumble();

        // Reset the gun on reload (e.g. return a physical slide). No-op if the gun
        // has nothing to reset.
        void ReloadFeedback();

        // Push the current ammo count to the gun's LEDs/display, if it has one.
        void SetAmmo(int remainingAmmo);

        // Forwarded from InputSystem.onDeviceChange for EVERY device — modules use it
        // for detection and hot-swap work (e.g. re-arm force feedback on reconnect).
        void OnDeviceChange(InputDevice device, InputDeviceChange change);
    }
}
