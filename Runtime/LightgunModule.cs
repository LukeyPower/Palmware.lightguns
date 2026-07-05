using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  LightgunModule
    // ---------------------------------------------------------------------------
    //  Convenience base for MonoBehaviour gun modules. Registers itself with the
    //  LightgunManager while enabled and provides no-op defaults, so a concrete
    //  module only overrides what its hardware actually supports.
    //
    //  Put concrete modules on (a child of) the LightgunManager's GameObject and
    //  they plug in automatically — disable the component to unplug the module.
    // ===========================================================================
    public abstract class LightgunModule : MonoBehaviour, ILightgunModule
    {
        public abstract string DisplayName { get; }
        public abstract bool IsConnected { get; }
        public abstract bool Owns(InputDevice device);

        public virtual bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            return false;
        }

        public virtual void Recoil(InputDevice firingDevice, int remainingAmmo) { }
        public virtual void Rumble() { }
        public virtual void ReloadFeedback() { }
        public virtual void SetAmmo(int remainingAmmo) { }
        public virtual void OnDeviceChange(InputDevice device, InputDeviceChange change) { }

        protected virtual void OnEnable() => LightgunManager.Register(this);
        protected virtual void OnDisable() => LightgunManager.Unregister(this);
    }
}
