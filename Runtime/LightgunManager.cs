using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // Payload for trigger/reload events: which device pressed, which module owns it
    // (null = plain mouse/keyboard), and where the player was aiming at that moment.
    public readonly struct LightgunEventArgs
    {
        public readonly InputDevice Device;
        public readonly ILightgunModule Module;
        public readonly Vector2 AimPosition;

        public bool FromLightgun => Module != null;

        public LightgunEventArgs(InputDevice device, ILightgunModule module, Vector2 aimPosition)
        {
            Device = device;
            Module = module;
            AimPosition = aimPosition;
        }
    }

    // ===========================================================================
    //  LightgunManager
    // ---------------------------------------------------------------------------
    //  The ONE class the game talks to about lightguns. Everything device-specific
    //  lives in ILightgunModule plug-ins (one per gun family / SDK); this manager
    //  routes calls between the game and the active modules:
    //
    //    game  ←  events   : FirePressed / ReloadPressed (device-agnostic — mouse,
    //                        Blamcon and RS3 all arrive through the same actions),
    //                        GunConnected / GunDisconnected (detection & hot-swap).
    //    game  ←  aim      : AimPosition — one screen-space value fed by whichever
    //                        source moved most recently (mouse or a gun module).
    //    game  →  feedback : Recoil(device, ammo) goes to the module that owns the
    //                        firing device; RumbleAll / ReloadAll / SetAmmoAll
    //                        broadcast to every module. No per-gun branching here.
    //
    //  Why not route everything through Unity's PlayerInput? The Input System covers
    //  buttons/axes fine, but lightguns have extras it has no concept of — recoil,
    //  slide return, ammo LED counts, serial COM ports, per-vendor FFB handshakes.
    //  This hub gives those a home while still using Input System actions for the
    //  plain input side.
    //
    //  Scene setup: one GameObject with this component + one child (or same-object)
    //  component per gun module (BlamconModule, RetroShooterModule, ...). Assign
    //  the LightgunInputActions asset. The manager survives scene loads.
    // ===========================================================================
    [DefaultExecutionOrder(-100)] // aim is fresh before gameplay scripts read it
    public class LightgunManager : MonoBehaviour
    {
        public static LightgunManager Instance { get; private set; }

        [Header("Input")]
        [Tooltip("Action asset holding the device-agnostic Fire / Reload / Point / Submit actions " +
                 "(every gun AND the mouse are bound to the same actions).")]
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private string actionMapName = "Player";

        [Tooltip("Minimum seconds between Fire events. Lightgun triggers can bounce and deliver two " +
                 "presses on a single pull; anything inside this window is swallowed. 0 = off.")]
        [SerializeField] private float minFireInterval = 0.06f;

        [Header("Aim")]
        [Tooltip("Let the system mouse drive AimPosition (handy in the editor / for desktop testing). " +
                 "The mouse only takes the crosshair when it actually moves, so an idle mouse never " +
                 "fights a lightgun.")]
        [SerializeField] private bool mouseAim = true;

        [SerializeField] private bool logDebugMessages = false;

        // -----------------------------------------------------------------------
        //  Module registry — static so modules can register from OnEnable without
        //  caring whether the manager's Awake has run yet.
        // -----------------------------------------------------------------------
        private static readonly List<ILightgunModule> _modules = new();

        public static IReadOnlyList<ILightgunModule> Modules => _modules;

        public static void Register(ILightgunModule module)
        {
            if (module != null && !_modules.Contains(module)) _modules.Add(module);
        }

        public static void Unregister(ILightgunModule module) => _modules.Remove(module);

        // -----------------------------------------------------------------------
        //  What the game reads / subscribes to
        // -----------------------------------------------------------------------

        // Current aim point in screen pixels (0,0 = bottom-left). Fed by whichever
        // source moved most recently: the mouse or any module's aim.
        public Vector2 AimPosition { get; private set; }

        // True while at least one physical gun is usable.
        public bool AnyGunConnected
        {
            get
            {
                foreach (var m in _modules)
                    if (m.IsConnected) return true;
                return false;
            }
        }

        public event Action<LightgunEventArgs> FirePressed;
        public event Action<LightgunEventArgs> ReloadPressed;

        // Detection / hot-swap: raised when a device owned by a module comes or goes.
        public event Action<ILightgunModule, InputDevice> GunConnected;
        public event Action<ILightgunModule, InputDevice> GunDisconnected;

        // Device-agnostic "confirm" for menu screens — true on the frame the player
        // clicked the mouse or pulled any gun's trigger.
        public bool FireWasPressedThisFrame => _fire != null && _fire.WasPerformedThisFrame();
        public bool SubmitWasPressedThisFrame => _submit != null && _submit.WasPerformedThisFrame();

        // The module that owns 'device', or null (mouse/keyboard shots own nothing).
        public ILightgunModule FindOwner(InputDevice device)
        {
            if (device == null) return null;
            foreach (var m in _modules)
                if (m.Owns(device)) return m;
            return null;
        }

        // Aim for one SPECIFIC device — this is the multiplayer crosshair API.
        // Guns with their own absolute aim (Blamcon gamepad mode, RS3 joystick
        // mode) answer per-device; everything else (mouse path / MnK-mode guns)
        // falls back to the shared AimPosition.
        public Vector2 GetAimFor(InputDevice device)
        {
            var owner = FindOwner(device);
            if (owner != null && owner.TryGetAim(device, out Vector2 pos))
                return new Vector2(
                    Mathf.Clamp(pos.x, 0f, Screen.width),
                    Mathf.Clamp(pos.y, 0f, Screen.height));
            return AimPosition;
        }

        // The physical gun devices currently claimed by a module — for player
        // assignment in multiplayer. Key your players by InputDevice: Fire events
        // carry the device, and GetAimFor(device) is that player's crosshair.
        // (Mouse/keyboard are excluded: a single MnK-mode gun plays through the
        // mouse path and needs no per-player identity.)
        public List<InputDevice> GetGunDevices()
        {
            var list = new List<InputDevice>();
            foreach (var device in InputSystem.devices)
            {
                if (device is Mouse || device is Keyboard) continue;
                if (FindOwner(device) != null) list.Add(device);
            }
            return list;
        }

        // -----------------------------------------------------------------------
        //  Feedback routing (game → modules)
        // -----------------------------------------------------------------------

        // Kick ONLY the gun that fired this shot; a mouse shot owns nothing, so
        // nothing recoils. 'remainingAmmo' drives ammo LEDs on guns that have them.
        public void Recoil(InputDevice firingDevice, int remainingAmmo)
            => FindOwner(firingDevice)?.Recoil(firingDevice, remainingAmmo);

        // Buzz EVERY connected gun (e.g. player damage — whichever gun is in hand).
        public void RumbleAll()
        {
            foreach (var m in _modules) m.Rumble();
        }

        // Reset every gun on reload (e.g. return the RS3's physical slide).
        public void ReloadAll()
        {
            foreach (var m in _modules) m.ReloadFeedback();
        }

        // Push the ammo count to every gun with LEDs/display.
        public void SetAmmoAll(int remainingAmmo)
        {
            foreach (var m in _modules) m.SetAmmo(remainingAmmo);
        }

        // -----------------------------------------------------------------------
        //  Lifecycle
        // -----------------------------------------------------------------------
        private InputAction _fire, _reload, _submit;
        private ILightgunModule _aimOwner;   // null = mouse owns the crosshair
        private float _lastFireTime = -1f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            AimPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        private void OnEnable()
        {
            if (Instance != null && Instance != this) return;

            BindActions();
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        private void OnDisable()
        {
            if (Instance != this) return;

            InputSystem.onDeviceChange -= OnDeviceChange;
            if (_fire != null) _fire.performed -= OnFirePerformed;
            if (_reload != null) _reload.performed -= OnReloadPerformed;
        }

        private void BindActions()
        {
            if (inputActions == null)
            {
                Debug.LogWarning("[Lightgun] No InputActionAsset assigned — Fire/Reload events disabled.");
                return;
            }

            var map = inputActions.FindActionMap(actionMapName, throwIfNotFound: false);
            if (map == null)
            {
                Debug.LogWarning($"[Lightgun] Action map '{actionMapName}' not found in '{inputActions.name}'.");
                return;
            }

            _fire = map.FindAction("Fire");
            _reload = map.FindAction("Reload");
            _submit = map.FindAction("Submit");
            map.Enable();

            if (_fire != null) _fire.performed += OnFirePerformed;
            if (_reload != null) _reload.performed += OnReloadPerformed;
        }

        // -----------------------------------------------------------------------
        //  Input events (any device — mouse, Blamcon, RS3 — same code path)
        // -----------------------------------------------------------------------
        private void OnFirePerformed(InputAction.CallbackContext ctx)
        {
            // Debounce trigger bounce (two events on one physical pull).
            if (minFireInterval > 0f &&
                _lastFireTime >= 0f && Time.unscaledTime - _lastFireTime < minFireInterval)
                return;
            _lastFireTime = Time.unscaledTime;

            var device = ctx.control.device;
            // Per-device aim: in multiplayer each gun's shot lands where THAT gun
            // points, not where the shared crosshair is.
            var args = new LightgunEventArgs(device, FindOwner(device), GetAimFor(device));
            if (logDebugMessages)
                Debug.Log($"[Lightgun] Fire from '{device?.displayName}' (module: {args.Module?.DisplayName ?? "none"}) at {args.AimPosition}");
            FirePressed?.Invoke(args);
        }

        private void OnReloadPerformed(InputAction.CallbackContext ctx)
        {
            // The Reload action is PassThrough, so it also fires on button RELEASE
            // (value back to 0). Only react to an actual press.
            if (ctx.ReadValue<float>() < 0.5f) return;

            var device = ctx.control.device;
            ReloadPressed?.Invoke(new LightgunEventArgs(device, FindOwner(device), GetAimFor(device)));
        }

        // -----------------------------------------------------------------------
        //  Detection & hot-swap — forwarded to every module, then surfaced to the
        //  game as GunConnected/GunDisconnected when the device belongs to one.
        // -----------------------------------------------------------------------
        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            // Modules first, so they can (re)initialize their hardware — e.g. the
            // Blamcon module re-arms force feedback on every hot-plug.
            foreach (var m in _modules) m.OnDeviceChange(device, change);

            var owner = FindOwner(device);
            if (owner == null) return;

            switch (change)
            {
                case InputDeviceChange.Added:
                case InputDeviceChange.Reconnected:
                    if (logDebugMessages) Debug.Log($"[Lightgun] {owner.DisplayName} connected ({device.displayName}).");
                    GunConnected?.Invoke(owner, device);
                    break;
                case InputDeviceChange.Removed:
                case InputDeviceChange.Disconnected:
                    if (logDebugMessages) Debug.Log($"[Lightgun] {owner.DisplayName} disconnected ({device.displayName}).");
                    GunDisconnected?.Invoke(owner, device);
                    break;
            }
        }

        // -----------------------------------------------------------------------
        //  Aim arbitration — whichever source MOVED most recently owns the
        //  crosshair, and keeps it until another source moves.
        // -----------------------------------------------------------------------
        private void Update()
        {
            if (mouseAim && Mouse.current != null &&
                Mouse.current.delta.ReadValue().sqrMagnitude > 0f)
            {
                AimPosition = Mouse.current.position.ReadValue();
                _aimOwner = null; // mouse takes the crosshair back from the guns
            }

            foreach (var m in _modules)
            {
                if (!m.TryGetAim(out Vector2 pos, out bool moved)) continue;

                if (moved) _aimOwner = m;
                if (_aimOwner != m) continue;

                AimPosition = new Vector2(
                    Mathf.Clamp(pos.x, 0f, Screen.width),
                    Mathf.Clamp(pos.y, 0f, Screen.height));
            }
        }
    }
}
