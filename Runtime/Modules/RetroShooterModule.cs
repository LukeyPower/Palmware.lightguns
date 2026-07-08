using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using Microsoft.Win32;
#endif

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  RetroShooterModule
    // ---------------------------------------------------------------------------
    //  Gun module for the Retro Shooter RS3 "Reaper" lightgun — up to FOUR guns.
    //
    //  Each physical gun is a "link": its serial COM port (feedback output,
    //  auto-detected from the gun's USB identity — VID 0483, PID 5750–5753 =
    //  P1–P4, the IDs Retro Shooter's own software matches) paired with its
    //  Input System device (matched back by the same PID).
    //
    //  MODE POLICY (never switch unless needed):
    //   • 1 gun — leave it in whatever mode it arrived in. In its power-on
    //     mouse+keyboard mode it aims via the OS cursor through the manager's
    //     mouse path, and recoil still routes here because the module claims
    //     mouse shots while the gun is confirmed on serial. If it arrived in
    //     joystick mode instead, that works identically (per-device aim from
    //     the stick). Same gameplay either way.
    //   • 2+ guns (any mix of RS3s and Blamcons, counted via LightgunRoster) —
    //     two mice can't share one OS cursor, so every MnK-mode RS3 is switched
    //     to joystick mode ('ZJ'), giving each gun its own identifiable device
    //     and its own crosshair (per-device TryGetAim). Restored to MnK on exit.
    //
    //  Output commands are short ASCII over serial (115200). If a gun's port
    //  can't be opened, its feedback is a silent no-op and the game keeps
    //  running on whatever input is available.
    // ===========================================================================
    public class RetroShooterModule : LightgunModule
    {
        private static class Commands
        {
            public const string EnterExternalControlMode = "ZS";
            public const string ExitExternalControlMode  = "ZX";
            public const string JoystickMode             = "ZJ"; // trigger/buttons arrive as <Joystick>
            public const string MouseKeyboardMode        = "ZM"; // trigger arrives as mouse/keyboard
            public const string SlideReturn              = "Z6"; // reload: slide returns, 5 orange LEDs
            public const string Rumble                   = "ZZ";

            // "Z0"-"Z5": recoil + light that many ammo LEDs.
            public static string Bullet(int remaining) => $"Z{Mathf.Clamp(remaining, 0, 5)}";
        }

        // RS3 USB identity — VID 0483, one PID per player slot (5750–5753 =
        // P1–P4), as matched by Retro Shooter's own games and calibration tool.
        public const string RosterFamily = "RetroShooter";
        private const string VendorId = "0483";
        private static readonly Regex DeviceIdPattern =
            new Regex("^VID_" + VendorId + ".PID_(575[0-3])", RegexOptions.IgnoreCase);

        // One physical gun: serial side + (in joystick mode) its input device.
        private class GunLink
        {
            public string Pid;              // "5750".."5753", or null for a manual port
            public string PortName;
            public SerialPort Serial;
            public Thread ReadThread;
            public volatile bool Reading;
            public bool JoystickCommanded;  // we sent ZJ → restore ZM on exit
            public Joystick Joystick;       // matched by PID (or first free link)
            public float LastFireTime = -1f;

            // Per-gun aim state (movement detection + SmoothDamp), refreshed at
            // most once per frame whichever aim path asks first.
            public int AimFrame = -1;
            public bool MovedThisFrame;
            public Vector2 LastStick;  public bool HaveLastStick;
            public Vector2 SmoothedAim; public Vector2 AimVelocity; public bool HaveSmoothedAim;

            public bool IsOpen => Serial != null && Serial.IsOpen;
        }

        [Header("Serial (feedback output)")]
        [Tooltip("COM port override (e.g. 'COM4') — single gun only. Leave EMPTY to auto-detect: every " +
                 "RS3's serial side is found in the Windows registry by its USB identity (VID 0483, " +
                 "PID 5750–5753 = P1–P4), which is also what enables multiple guns.")]
        [SerializeField] private string portName = "";
        [SerializeField] private int    baudRate = 115200;

        [Header("Mode policy")]
        [Tooltip("Always switch guns into JOYSTICK mode on connect. Default OFF — a single gun stays " +
                 "in whatever mode it arrived in (usually its power-on mouse+keyboard mode, playing " +
                 "through the manager's mouse path). Mouse+keyboard mode is restored on exit if we " +
                 "switched.")]
        [SerializeField] private bool forceJoystickMode = false;

        [Tooltip("Automatically switch MnK-mode guns to joystick mode when 2+ physical guns are " +
                 "present (any mix of families, via LightgunRoster) — required for multiplayer, since " +
                 "two mice can't share one OS cursor. Leave on.")]
        [SerializeField] private bool autoJoystickWhenMultiplayer = true;

        [Tooltip("In mouse+keyboard mode the trigger arrives as a mouse click, indistinguishable " +
                 "from a real mouse. While a single RS3 is confirmed on serial, treat mouse shots as " +
                 "RS3 shots so recoil still fires. (A desktop mouse click will also kick the gun. " +
                 "Don't combine with an active SindenModule, which claims the mouse the same way.)")]
        [SerializeField] private bool claimMouseWhileMnk = true;

        [Tooltip("Minimum seconds between recoil commands per gun. The firmware can't recoil faster " +
                 "than ~12/sec (≥83ms apart); this also swallows the occasional double-fire from a " +
                 "single trigger pull.")]
        [SerializeField] private float minRecoilInterval = 0.083f;

        [Header("Aim (joystick mode only)")]
        [Tooltip("Map each gun's joystick stick to a crosshair when in joystick mode. In mouse+" +
                 "keyboard mode the gun moves the OS cursor and the manager's mouse path supplies " +
                 "aim instead. Off = this module never provides aim.")]
        [SerializeField] private bool provideAim = true;
        [Tooltip("Flip the horizontal axis if aiming LEFT moves the crosshair RIGHT.")]
        [SerializeField] private bool invertX = false;
        [Tooltip("Flip the vertical axis if aiming UP moves the crosshair DOWN.")]
        [SerializeField] private bool invertY = false;
        [Tooltip("Low-pass smoothing (seconds) to damp the IR sensor's jitter. Higher = steadier " +
                 "but more lag; 0 = raw/instant. ~0.04–0.08 is a good starting range. Tune live.")]
        [SerializeField] private float aimSmoothTime = 0.05f;

        [SerializeField] private bool logDebugMessages = false;

        private readonly List<GunLink> _links = new List<GunLink>();
        private GunLink _lastAimLink; // most recently moved gun drives the shared crosshair
        private bool _warnedNoPort;

        public bool IsSerialConnected => AnySerialOpen;
        public int  GunCount => _links.Count;

        private bool AnySerialOpen
        {
            get
            {
                foreach (var l in _links) if (l.IsOpen) return true;
                return false;
            }
        }

        private bool AnyJoystickAttached
        {
            get
            {
                foreach (var l in _links) if (l.Joystick != null) return true;
                return false;
            }
        }

        public override string DisplayName => "Retro Shooter RS3";

        // "Usable" here means at least one feedback port is open — input alone
        // still works without it, but then the gun is indistinguishable from a
        // generic mouse or joystick.
        public override bool IsConnected => AnySerialOpen;

        // Routes recoil to us: any RS3 HID device (joystick mode), or — MnK-mode
        // gun confirmed on serial — the mouse device the gun is driving. Precise
        // when Unity exposes USB ids (the gun's mouse carries VID 0483 / PID
        // 575x; a real desktop mouse doesn't, so its clicks don't kick the gun);
        // falls back to the single-gun any-mouse assumption otherwise.
        public override bool Owns(InputDevice device)
        {
            if (device == null) return false;
            if (IsRetroShooterDevice(device)) return true;
            if (!claimMouseWhileMnk || !(device is Mouse) || !AnySerialOpen) return false;
            if (UsbDeviceId.TryGetVendorId(device, out int vid))
                return vid == 0x0483 &&
                       UsbDeviceId.TryGetProductId(device, out int pid) &&
                       pid >= 0x5750 && pid <= 0x5753;
            return !AnyJoystickAttached && LightgunRoster.TotalGuns <= 1;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        protected override void OnEnable()
        {
            base.OnEnable();

            RefreshLinks();
            foreach (var device in InputSystem.devices)
                if (device is Joystick js && IsRetroShooterDevice(js))
                    AttachJoystick(js);

            // Second gun (of any family) appearing mid-session escalates us to
            // joystick mode; serial links can also die while unfocused.
            LightgunRoster.Changed += ApplyModePolicy;
            Application.focusChanged += OnFocusChanged;

            if (logDebugMessages)
                Debug.Log($"[RS3] Enabled — guns={_links.Count}, serialOpen={AnySerialOpen}.");
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            LightgunRoster.Changed -= ApplyModePolicy;
            Application.focusChanged -= OnFocusChanged;
            DisconnectAll();
            LightgunRoster.Report(RosterFamily, 0);
        }

        private void OnFocusChanged(bool focused)
        {
            if (!focused) return;
            if (logDebugMessages) Debug.Log("[RS3] Focus regained — rechecking serial ports.");
            RefreshLinks();
        }

        public override void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (!IsRetroShooterDevice(device)) return;

            switch (change)
            {
                case InputDeviceChange.Added:
                case InputDeviceChange.Reconnected:
                    if (device is Joystick js) AttachJoystick(js);
                    // Gun plugged in after startup (or re-enumerated after a mode
                    // switch) — pick up its serial side too.
                    RefreshLinks();
                    break;

                case InputDeviceChange.Removed:
                case InputDeviceChange.Disconnected:
                    foreach (var l in _links)
                        if (ReferenceEquals(l.Joystick, device)) l.Joystick = null;
                    RefreshLinks(); // prunes links whose COM port vanished
                    break;
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        // Options-menu hook: switch every gun's input mode at runtime. The guns
        // re-enumerate on USB after the switch. Joystick mode is forgotten on
        // power-off; anything we switched is restored to MnK on exit.
        public void SetJoystickMode(bool on)
        {
            foreach (var link in _links)
            {
                if (!link.IsOpen || link.JoystickCommanded == on) continue;
                Write(link, on ? Commands.JoystickMode : Commands.MouseKeyboardMode);
                link.JoystickCommanded = on;
            }
            if (logDebugMessages) Debug.Log($"[RS3] Mode switch → {(on ? "joystick" : "mouse+keyboard")} (guns re-enumerate).");
        }

        // ── Link management ────────────────────────────────────────────────────

        // Reconcile links with reality: open a link per detected gun, drop links
        // whose port vanished, report the physical count, re-run the mode policy.
        private void RefreshLinks()
        {
            var live = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

            // Drop links whose COM port no longer exists (gun unplugged).
            for (int i = _links.Count - 1; i >= 0; i--)
            {
                if (live.Contains(_links[i].PortName)) continue;
                CloseLink(_links[i], restoreMode: false); // port is gone — nothing to restore
                _links.RemoveAt(i);
            }

            if (!string.IsNullOrEmpty(portName))
            {
                // Manual override: exactly one link on the configured port.
                if (live.Contains(portName)) EnsureLink(null, portName);
            }
            else
            {
                foreach (var pair in FindRetroShooterPortPairs())
                    EnsureLink(pair.Key, pair.Value);
            }

            if (_links.Count == 0)
            {
                if (!_warnedNoPort || logDebugMessages)
                    Debug.LogWarning("[RS3] No RS3 serial port found (VID 0483, PID 5750–5753) and no manual " +
                                     "port set. Recoil/reload output disabled — game still runs.");
                _warnedNoPort = true;
            }
            else _warnedNoPort = false;

            LightgunRoster.Report(RosterFamily, _links.Count);
            ApplyModePolicy();
        }

        private void EnsureLink(string pid, string port)
        {
            var link = _links.Find(l => string.Equals(l.PortName, port, StringComparison.OrdinalIgnoreCase));
            if (link == null)
            {
                link = new GunLink { Pid = pid, PortName = port };
                _links.Add(link);
                _links.Sort((a, b) => string.CompareOrdinal(a.Pid ?? "", b.Pid ?? "")); // P1 first
            }
            if (!link.IsOpen) OpenLink(link);
        }

        private void OpenLink(GunLink link)
        {
            try
            {
                link.Serial?.Dispose();
                link.Serial = new SerialPort(link.PortName, baudRate) { ReadTimeout = 50 };
                link.Serial.Open();

                link.Reading = true;
                link.ReadThread = new Thread(() => ReadLoop(link)) { IsBackground = true };
                link.ReadThread.Start();

                Write(link, Commands.EnterExternalControlMode);
                if (logDebugMessages) Debug.Log($"[RS3] Serial port {link.PortName} opened (PID {link.Pid ?? "manual"}).");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RS3] Could not open {link.PortName}: {e.Message}. " +
                                 "Recoil/reload output disabled for this gun — game still runs.");
            }
        }

        private void CloseLink(GunLink link, bool restoreMode)
        {
            link.Reading = false;
            try
            {
                if (link.IsOpen)
                {
                    // Hand the gun back to mouse+keyboard mode if WE switched it,
                    // so it works as a normal pointer once the game stops.
                    if (restoreMode && link.JoystickCommanded) Write(link, Commands.MouseKeyboardMode);
                    if (restoreMode) Write(link, Commands.ExitExternalControlMode);
                    link.Serial.Close();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RS3] Error closing {link.PortName}: {e.Message}");
            }

            link.JoystickCommanded = false;
            if (link.ReadThread != null && link.ReadThread.IsAlive) link.ReadThread.Join(200);
        }

        private void DisconnectAll()
        {
            foreach (var link in _links) CloseLink(link, restoreMode: true);
            _links.Clear();
            _lastAimLink = null;
            if (logDebugMessages) Debug.Log("[RS3] All serial ports closed.");
        }

        // The mode policy: joystick mode when forced, or as soon as 2+ physical
        // guns exist (two mice can't share one cursor). Escalation only — we
        // never switch a gun back mid-session (disruptive); restore is on exit.
        private void ApplyModePolicy()
        {
            bool want = forceJoystickMode ||
                        (autoJoystickWhenMultiplayer && LightgunRoster.TotalGuns >= 2);
            if (!want) return;

            foreach (var link in _links)
            {
                if (link.JoystickCommanded || !link.IsOpen) continue;
                if (link.Joystick != null) continue; // already a joystick (arrived that way)
                Write(link, Commands.JoystickMode);
                link.JoystickCommanded = true;
                if (logDebugMessages)
                    Debug.Log($"[RS3] {link.PortName}: switching to joystick mode " +
                              $"({(forceJoystickMode ? "forced" : "multiplayer")}) — gun re-enumerates.");
            }
        }

        // Pair an Input System joystick with its serial link — by USB PID when
        // available, else the first link without a joystick.
        private void AttachJoystick(Joystick js)
        {
            foreach (var l in _links)
                if (ReferenceEquals(l.Joystick, js)) return;

            GunLink target = null;
            if (UsbDeviceId.TryGetProductId(js, out int pid))
            {
                string pidHex = pid.ToString("X4");
                target = _links.Find(l => string.Equals(l.Pid, pidHex, StringComparison.OrdinalIgnoreCase));
            }
            target ??= _links.Find(l => l.Joystick == null);

            if (target != null)
            {
                target.Joystick = js;
                if (logDebugMessages)
                    Debug.Log($"[RS3] Joystick '{js.displayName}' paired with {target.PortName} (PID {target.Pid ?? "?"}).");
            }
        }

        private GunLink FindLinkFor(InputDevice device)
        {
            if (device is Joystick)
            {
                foreach (var l in _links)
                    if (ReferenceEquals(l.Joystick, device)) return l;
            }
            // Mouse shot (MnK mode) or unmatched device: single-gun assumption.
            return _links.Count > 0 ? _links[0] : null;
        }

        // ── Serial plumbing ────────────────────────────────────────────────────

        // Background drain of incoming bytes so the OS buffer doesn't fill. We don't
        // act on inbound data — inputs come through the Input System, not the port.
        private void ReadLoop(GunLink link)
        {
            while (link.Reading && link.Serial != null && link.Serial.IsOpen)
            {
                try
                {
                    string message = link.Serial.ReadLine();
                    if (logDebugMessages && !string.IsNullOrEmpty(message))
                        Debug.Log($"[RS3] {link.PortName} received: {message}");
                }
                catch (TimeoutException) { /* expected — keep polling */ }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void Write(GunLink link, string command)
        {
            if (!link.IsOpen) return;
            try { link.Serial.Write(command); }
            catch (Exception e) { Debug.LogWarning($"[RS3] Write '{command}' → {link.PortName} failed: {e.Message}"); }
        }

        // ── Aim ────────────────────────────────────────────────────────────────

        // Refresh one gun's smoothed aim (at most once per frame, whichever aim
        // path asks first). Returns true if the stick moved this frame.
        private bool UpdateLinkAim(GunLink link)
        {
            if (link.Joystick == null) return false;
            if (link.AimFrame == Time.frameCount) return link.MovedThisFrame;
            link.AimFrame = Time.frameCount;

            // Raw value, so the stick's default centre deadzone doesn't dead-spot the screen.
            Vector2 raw = link.Joystick.stick.ReadUnprocessedValue();

            link.MovedThisFrame = !link.HaveLastStick || (raw - link.LastStick).sqrMagnitude > 1e-6f;
            link.LastStick = raw;
            link.HaveLastStick = true;

            float nx = invertX ? -raw.x : raw.x;
            float ny = invertY ? -raw.y : raw.y;
            Vector2 target = new Vector2(
                (nx * 0.5f + 0.5f) * Screen.width,
                (ny * 0.5f + 0.5f) * Screen.height);

            if (!link.HaveSmoothedAim)
            {
                link.SmoothedAim = target;
                link.HaveSmoothedAim = true;
            }

            // Low-pass smoothing damps the IR sensor's jitter; 0 = instant.
            link.SmoothedAim = aimSmoothTime > 0f
                ? Vector2.SmoothDamp(link.SmoothedAim, target, ref link.AimVelocity, aimSmoothTime)
                : target;

            return link.MovedThisFrame;
        }

        // Shared-crosshair aim (single player): whichever gun moved most recently.
        public override bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            if (!provideAim) return false;

            foreach (var link in _links)
                if (UpdateLinkAim(link)) { _lastAimLink = link; movedThisFrame = true; }

            if (_lastAimLink == null || _lastAimLink.Joystick == null) return false;
            screenPosition = _lastAimLink.SmoothedAim;
            return true;
        }

        // Per-device aim (multiplayer): each gun is its own crosshair.
        public override bool TryGetAim(InputDevice device, out Vector2 screenPosition)
        {
            screenPosition = default;
            if (!provideAim || device is not Joystick) return false;

            foreach (var link in _links)
            {
                if (!ReferenceEquals(link.Joystick, device)) continue;
                UpdateLinkAim(link);
                screenPosition = link.SmoothedAim;
                return true;
            }
            return false;
        }

        // ── Feedback ───────────────────────────────────────────────────────────

        // Recoil + set the ammo LEDs — on the specific gun that fired.
        public override void Recoil(InputDevice firingDevice, int remainingAmmo)
        {
            var link = FindLinkFor(firingDevice);
            if (link == null) return;

            // Debounce per gun: a single trigger pull can occasionally land twice, and
            // the firmware can't keep up with back-to-back pulses anyway.
            if (link.LastFireTime >= 0f && Time.unscaledTime - link.LastFireTime < minRecoilInterval)
            {
                if (logDebugMessages) Debug.Log("[RS3] Recoil ignored — within cooldown.");
                return;
            }
            link.LastFireTime = Time.unscaledTime;

            string cmd = Commands.Bullet(remainingAmmo);
            if (logDebugMessages) Debug.Log($"[RS3] Recoil → '{cmd}' on {link.PortName}");
            Write(link, cmd);
        }

        // Return every gun's slide to its forward position (reload).
        public override void ReloadFeedback()
        {
            foreach (var link in _links) Write(link, Commands.SlideReturn);
        }

        public override void Rumble()
        {
            foreach (var link in _links) Write(link, Commands.Rumble);
        }

        // SetAmmo: the RS3 has no standalone ammo command — its LEDs only update as
        // part of a recoil pulse ("Z0"-"Z5"), which Recoil() already sends.

        // ── Recognition ────────────────────────────────────────────────────────

        // True when the given input device is an RS3, so recoil can be routed to
        // the serial gun rather than the Blamcon HID device.
        public static bool IsRetroShooterDevice(InputDevice device)
        {
            if (device == null) return false;
            var d = device.description;
            return (!string.IsNullOrEmpty(d.manufacturer) &&
                    d.manufacturer.IndexOf("3AGAME", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrEmpty(d.product) &&
                    d.product.IndexOf("Retro Shooter", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // (The HID input side and the serial side are interfaces of the same USB
        // device, so the PID from UsbDeviceId pairs a joystick with its COM port.)

        // ── Port discovery ─────────────────────────────────────────────────────

        // Windows: walk HKLM\SYSTEM\CurrentControlSet\Enum for device IDs matching
        // VID_0483&PID_575x and read Device Parameters\PortName — the same
        // strategy BlamconSerial uses for its guns (and Retro Shooter's own games
        // use for theirs). Filtering by VID/PID means we never write probe
        // commands at some other device's COM port.
        public static List<string> FindRetroShooterPorts() =>
            FindRetroShooterPortPairs().ConvertAll(p => p.Value);

        // (pid, port) pairs sorted by PID so P1 (5750) comes first.
        private static List<KeyValuePair<string, string>> FindRetroShooterPortPairs()
        {
            var results = new List<KeyValuePair<string, string>>();
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var livePorts = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            using (var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
            {
                if (enumKey == null) return results;
                foreach (string bus in enumKey.GetSubKeyNames())
                {
                    using (var busKey = enumKey.OpenSubKey(bus))
                    {
                        if (busKey == null) continue;
                        foreach (string deviceId in busKey.GetSubKeyNames())
                        {
                            var match = DeviceIdPattern.Match(deviceId);
                            if (!match.Success) continue;
                            using (var deviceKey = busKey.OpenSubKey(deviceId))
                            {
                                if (deviceKey == null) continue;
                                foreach (string instance in deviceKey.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (var paramsKey = deviceKey.OpenSubKey(instance + @"\Device Parameters"))
                                        {
                                            var name = paramsKey?.GetValue("PortName") as string;
                                            if (!string.IsNullOrEmpty(name) && livePorts.Contains(name) &&
                                                !results.Exists(r => string.Equals(r.Value, name, StringComparison.OrdinalIgnoreCase)))
                                                results.Add(new KeyValuePair<string, string>(match.Groups[1].Value, name));
                                        }
                                    }
                                    catch { /* orphaned registry entry — skip */ }
                                }
                            }
                        }
                    }
                }
            }
            results.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
#endif
            // Non-Windows: no safe way to identify RS3 ports without probing
            // unknown serial devices — set the port name manually there.
            return results;
        }
    }
}
