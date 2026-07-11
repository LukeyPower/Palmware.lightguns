using UnityEngine;

namespace Palmware.Lightguns.Samples
{
    // ===========================================================================
    //  LightgunSmokeTest
    // ---------------------------------------------------------------------------
    //  Throwaway checker for verifying the lightgun rig works in a scene BEFORE
    //  any real gameplay exists. Drop it on any GameObject (e.g. the Lightgun
    //  Manager itself), press Play, and it:
    //    • draws a crosshair at LightgunManager.AimPosition (move the gun/mouse),
    //    • logs every Fire/Reload with the device + owning module,
    //    • fires real recoil back at the gun that pulled the trigger,
    //    • logs gun connects/disconnects (test hot-swap by unplugging).
    //
    //  It talks to the manager EXACTLY the way real gameplay code should — use it
    //  as a reference — and it can be deleted once the game has its own shooter.
    //  See Documentation~/scene-setup-guide.md for the full walkthrough.
    // ===========================================================================
    public class LightgunSmokeTest : MonoBehaviour
    {
        [Tooltip("Pretend ammo count, so the test exercises the ammo-LED path on guns that have one.")]
        [SerializeField] private int fakeAmmo = 5;

        private string _lastEvent = "none yet — pull a trigger / click the mouse";

        private void OnEnable()
        {
            var mgr = LightgunManager.Instance;
            if (mgr == null)
            {
                Debug.LogError("[SmokeTest] No LightgunManager in the scene — set one up first " +
                               "(see the package's scene-setup-guide.md).");
                enabled = false;
                return;
            }

            mgr.FirePressed += OnFire;
            mgr.ReloadPressed += OnReload;
            mgr.GunConnected += OnGunConnected;
            mgr.GunDisconnected += OnGunDisconnected;
        }

        private void OnDisable()
        {
            var mgr = LightgunManager.Instance;
            if (mgr == null) return;
            mgr.FirePressed -= OnFire;
            mgr.ReloadPressed -= OnReload;
            mgr.GunConnected -= OnGunConnected;
            mgr.GunDisconnected -= OnGunDisconnected;
        }

        private void OnFire(LightgunEventArgs args)
        {
            _lastEvent = $"FIRE from '{args.Device?.displayName}' " +
                         $"(module: {args.Module?.DisplayName ?? "none — plain mouse"}) at {args.AimPosition}";
            Debug.Log($"[SmokeTest] {_lastEvent}");

            if (fakeAmmo > 0) fakeAmmo--;

            // This is the real gameplay pattern: hand the firing device back to the
            // manager and let it kick whichever gun actually shot.
            LightgunManager.Instance.Recoil(args.Device, fakeAmmo);
        }

        private void OnReload(LightgunEventArgs args)
        {
            fakeAmmo = 5;
            _lastEvent = $"RELOAD from '{args.Device?.displayName}'";
            Debug.Log($"[SmokeTest] {_lastEvent}");
            LightgunManager.Instance.ReloadAll();
        }

        private void OnGunConnected(ILightgunModule module, UnityEngine.InputSystem.InputDevice device)
            => Debug.Log($"[SmokeTest] GUN CONNECTED: {module.DisplayName} ({device.displayName})");

        private void OnGunDisconnected(ILightgunModule module, UnityEngine.InputSystem.InputDevice device)
            => Debug.Log($"[SmokeTest] GUN DISCONNECTED: {module.DisplayName} ({device.displayName})");

        // Crosshair + status readout. OnGUI is fine for a throwaway dev tool.
        private void OnGUI()
        {
            var mgr = LightgunManager.Instance;
            if (mgr == null) return;

            // AimPosition is bottom-left origin; GUI space is top-left.
            Vector2 aim = mgr.AimPosition;
            float x = aim.x, y = Screen.height - aim.y;
            GUI.color = Color.red;
            GUI.Label(new Rect(x - 10, y - 12, 20, 24), "+", new GUIStyle(GUI.skin.label)
            {
                fontSize = 24, alignment = TextAnchor.MiddleCenter
            });

            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(10, 10, 700, 120));
            GUILayout.Label($"Aim: {aim}   Ammo: {fakeAmmo}   AnyGunConnected: {mgr.AnyGunConnected}");
            foreach (var m in LightgunManager.Modules)
                GUILayout.Label($"  module '{m.DisplayName}' connected: {m.IsConnected}");
            GUILayout.Label($"Last event: {_lastEvent}");
            GUILayout.EndArea();
        }
    }
}
