using System;
using System.Collections.Concurrent;
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
    //  BlamconSerial
    // ---------------------------------------------------------------------------
    //  Companion service for the Blamcon's SERIAL side. Every Blamcon exposes a
    //  USB CDC virtual COM port in EVERY emulation mode (MnK, Gamepad, Bluetooth
    //  variants) — separate from its mouse/gamepad input interface — and the
    //  firmware settings live behind it. This is exactly how Blamcon's own tools
    //  detect and configure guns; the commands below are Blamcon's publicly
    //  documented serial protocol (https://blamcon.com/get-started-with-blamcon/
    //  serial-commands/ — see Documentation~/integration-guide.md §2).
    //
    //  MnK-FIRST MODE POLICY (mirrors RetroShooterModule):
    //   • 1 gun — leave it in whatever mode it arrived in. A Gamepad-mode gun is
    //     claimed by BlamconModule (HID aim + FFB). An MnK-mode gun plays through
    //     the manager's mouse path, and THIS component routes its recoil/rumble
    //     over serial (F.0.1.n / F.1.1.n — the Hook-of-the-Reaper trick, works
    //     in any mode): it registers as a module, claims mouse shots while an
    //     MnK gun is confirmed on serial, and kicks it on Recoil(). Same
    //     gameplay result in either mode.
    //   • 2+ guns (any mix of families, via LightgunRoster) — per-device
    //     identity is required, so MnK guns are switched to Gamepad mode
    //     (ZY.3.1; Bluetooth variant ZY.3.3). The gun saves the setting, reboots
    //     and re-enumerates a couple of seconds later, at which point
    //     BlamconModule sees it (InputSystem.onDeviceChange) and takes over.
    //     Guns WE switched are restored to their arrival mode on quit.
    //
    //  Place it next to LightgunManager. Detection runs on a background thread
    //  (registry + 500 ms serial timeouts would hitch the main thread); events
    //  are re-raised on the main thread.
    // ===========================================================================
    public class BlamconSerial : MonoBehaviour, ILightgunModule
    {
        // Firmware setting index 3 ("emulation mode") — values from ARC's
        // DeviceEmulation enum.
        public enum EmulationMode
        {
            Unknown = -1,
            MouseKeyboard = 0,
            Gamepad = 1,
            MouseKeyboardBluetooth = 2,
            GamepadBluetooth = 3,
        }

        // When to move an MnK-mode gun into Gamepad mode.
        public enum ModeSwitchPolicy
        {
            KeepMode = 0,             // never switch — MnK guns use serial feedback only
            SwitchWhenMultiplayer = 1, // switch only once 2+ physical guns exist (default)
            AlwaysSwitch = 2,          // old behavior: every MnK gun becomes a gamepad
        }

        // One entry per gun found on serial (any emulation mode).
        public class SerialGunInfo
        {
            public string PortName;            // e.g. "COM5" (can change after re-enumeration)
            public string ProductId;           // "0100".."0103" wired P1–P4, "0200".."0203" wireless
            public string FirmwareVersion;     // reply to ZD.1024
            public EmulationMode ModeAtScan = EmulationMode.Unknown;
            public bool SwitchedToGamepad;     // true if WE changed its mode this session

            public bool IsMnk => !SwitchedToGamepad &&
                                 (ModeAtScan == EmulationMode.MouseKeyboard ||
                                  ModeAtScan == EmulationMode.MouseKeyboardBluetooth);
        }

        public const string RosterFamily = "Blamcon";

        [Header("Mode policy")]
        [Tooltip("When to switch an MnK-mode gun to Gamepad mode. Default: only when 2+ physical guns " +
                 "are present (any mix of families) — a single gun stays in the mode it arrived in and " +
                 "plays through the mouse path with serial recoil. 'Always' is the old auto-switch " +
                 "behavior; 'Keep Mode' never switches.")]
        [SerializeField] private ModeSwitchPolicy modeSwitchPolicy = ModeSwitchPolicy.SwitchWhenMultiplayer;

        [Tooltip("On quit, restore any gun WE switched back to the mode it arrived in. The mode is a " +
                 "persisted firmware setting, so without this the gun stays in Gamepad mode after the " +
                 "game closes.")]
        [SerializeField] private bool restoreModeOnExit = true;

        [Header("MnK-mode feedback (single gun)")]
        [Tooltip("While an MnK-mode Blamcon is confirmed on serial (and it's the only gun), treat " +
                 "mouse shots as Blamcon shots so recoil routes to it. (A desktop mouse click will " +
                 "also kick the gun. Don't combine with an active SindenModule, which claims the " +
                 "mouse the same way.)")]
        [SerializeField] private bool claimMouseWhileMnk = true;

        [Tooltip("Send serial recoil (F.0.1.n) to MnK-mode guns on Recoil(). NOTE: unless FFB control " +
                 "is taken away from the firmware, the gun may ALSO auto-recoil on every trigger pull " +
                 "by itself — if you feel a double kick per shot, disable trigger recoil on the gun " +
                 "(in Blamcon ARC) or turn this off and let the firmware do it.")]
        [SerializeField] private bool serialRecoilInMnk = true;

        [SerializeField] private int recoilPulses = 1;
        [SerializeField] private int rumblePulses = 1;

        [Tooltip("Minimum seconds between serial recoil commands (each one opens the COM port).")]
        [SerializeField] private float minRecoilInterval = 0.083f;

        [Header("Detection")]
        [Tooltip("Seconds between automatic re-scans, for guns plugged in after startup while in MnK " +
                 "mode (a MnK gun never appears in the Input System as a Blamcon, so nothing else can " +
                 "notice it). 0 = scan on enable + on device changes; call Rescan() to scan manually.")]
        [SerializeField] private float rescanInterval = 0f;

        [Tooltip("Re-scan whenever the application regains focus. Guns plugged in (or power-cycled) " +
                 "while the player was alt-tabbed are picked up the moment they come back.")]
        [SerializeField] private bool rescanOnFocusRegained = true;

        [SerializeField] private bool logDebugMessages = false;

        // Blamcon USB identity — the same board IDs ARC scans for.
        private const string VendorId = "3673";
        private const int BaudRate = 9600;
        private static readonly Regex DeviceIdPattern =
            new Regex("^VID_" + VendorId + ".PID_(010[0-3]|020[0-3])", RegexOptions.IgnoreCase);

        private static class Commands
        {
            public const string Handshake = "ZD.1024";       // → firmware version string
            public const string GetEmulationMode = "ZD.3";   // → int (EmulationMode)
            public static string SetEmulationMode(EmulationMode mode) => $"ZY.3.{(int)mode}"; // saves + reboots

            // Serial FFB control — the serial twin of the HID EnableFFBControl
            // handshake BlamconModule sends in Gamepad mode. Taking control
            // BOTH arms the F.* commands AND stops the firmware kicking on
            // every trigger pull by itself (otherwise, once the solenoid
            // subsystem is armed, CM.8 "recoil mode" fires it on each pull —
            // kicks in menus, double-kicks in play).
            public const string TakeSolenoidControl = "SM.0.1";
            public const string TakeRumbleControl   = "SM.1.1";
            public const string ReleaseSerialControl = "ES";

            public static string Recoil(int pulses) => $"F.0.1.{pulses}";
            public static string Rumble(int pulses) => $"F.1.1.{pulses}";
        }

        private struct PortMatch
        {
            public string Port;
            public string Pid;
        }

        private readonly List<SerialGunInfo> _guns = new List<SerialGunInfo>();
        private readonly object _gunsLock = new object();
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private Thread _scanThread;
        private float _nextRescanTime;
        private bool _rescanQueued;
        private float _rescanNotBefore;
        private bool _modesRestored;
        private float _lastFireTime = -1f;

        /// Snapshot of every Blamcon found on serial (any mode). Thread-safe copy.
        public List<SerialGunInfo> DetectedGuns
        {
            get { lock (_gunsLock) return new List<SerialGunInfo>(_guns); }
        }

        public bool IsScanning => _scanThread != null && _scanThread.IsAlive;

        /// A gun was confirmed on serial (fires on the main thread).
        public event Action<SerialGunInfo> GunDetected;

        /// A MnK-mode gun was told to become a gamepad; expect USB re-enumeration.
        public event Action<SerialGunInfo> ModeSwitchRequested;

        private bool HasMnkGun
        {
            get
            {
                lock (_gunsLock)
                    foreach (var g in _guns)
                        if (g.IsMnk) return true;
                return false;
            }
        }

        // Firmware 2.1.0+ exposes a vendor HID collection on a mouse-mode gun, and
        // BlamconModule drives it. HID is the better transport (survives replug, no
        // COM-port reassignment, and it's where Blamcon is taking the platform), so
        // whenever it's available this serial path stands down completely rather
        // than kicking the same gun twice. It stands down only while an enabled
        // BlamconModule actually claims the gun (claimMouseWhileMouseModeHid on);
        // otherwise nothing would drive it. On older firmware the collection doesn't
        // exist, this stays false, and serial carries the feedback as before.
        private static bool HidOwnsMouseModeFeedback =>
            BlamconModule.ClaimsMouseModeHid && BlamconModule.HasMouseModeGun;

        // ── ILightgunModule (the MnK-mode serial feedback path) ────────────────

        public string DisplayName => "Blamcon (serial/MnK)";

        // "Connected" = at least one gun is playing through the serial/MnK path.
        // (Gamepad-mode guns are BlamconModule's to report.)
        public bool IsConnected => HasMnkGun && !HidOwnsMouseModeFeedback;

        // MnK mode: the gun's mouse interface IS the gun — claim its shots so
        // Recoil() routes here. Precise when Unity exposes USB ids: the gun's
        // mouse carries Blamcon's VID 0x3673, a real desktop mouse doesn't (so
        // desktop clicks don't kick the gun). Only when no ids are available do
        // we fall back to the single-gun any-mouse assumption.
        public bool Owns(InputDevice device)
        {
            if (HidOwnsMouseModeFeedback) return false; // BlamconModule has this gun over HID
            if (!claimMouseWhileMnk || !(device is Mouse) || !HasMnkGun) return false;
            if (UsbDeviceId.TryGetVendorId(device, out int vid))
                return vid == 0x3673;
            return LightgunRoster.TotalGuns <= 1;
        }

        public bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            return false; // MnK aim is the OS cursor — the manager's mouse path has it
        }

        public bool TryGetAim(InputDevice device, out Vector2 screenPosition)
        {
            screenPosition = default;
            return false;
        }

        public void Recoil(InputDevice firingDevice, int remainingAmmo)
        {
            if (!serialRecoilInMnk || HidOwnsMouseModeFeedback) return;
            if (_lastFireTime >= 0f && Time.unscaledTime - _lastFireTime < minRecoilInterval) return;
            _lastFireTime = Time.unscaledTime;
            SendToMnkGuns(Commands.Recoil(recoilPulses));
        }

        public void Rumble()
        {
            if (HidOwnsMouseModeFeedback) return;
            SendToMnkGuns(Commands.Rumble(rumblePulses));
        }

        public void ReloadFeedback() { } // no slide, no serial ammo command
        public void SetAmmo(int remainingAmmo) { }

        public void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            // Any device (dis)appearing can be a Blamcon in MnK mode (it looks
            // like a plain mouse/keyboard) — queue a debounced re-scan so a gun
            // plugged in mid-session is found, and multiplayer escalation runs.
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed ||
                change == InputDeviceChange.Reconnected || change == InputDeviceChange.Disconnected)
                QueueRescan();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LightgunManager.Register(this);
            LightgunRoster.Changed += OnRosterChanged;
            Application.focusChanged += OnFocusChanged;
            Rescan();
            if (rescanInterval > 0f) _nextRescanTime = Time.unscaledTime + rescanInterval;
        }

        private void OnDisable()
        {
            LightgunManager.Unregister(this);
            LightgunRoster.Changed -= OnRosterChanged;
            Application.focusChanged -= OnFocusChanged;
            LightgunRoster.Report(RosterFamily, 0);
        }

        private void OnFocusChanged(bool focused)
        {
            if (focused && rescanOnFocusRegained) QueueRescan();
        }

        private void OnRosterChanged()
        {
            // A gun appeared/vanished somewhere (maybe another family). If that
            // tips us into multiplayer, the next scan switches our MnK guns.
            if (modeSwitchPolicy == ModeSwitchPolicy.SwitchWhenMultiplayer && HasMnkGun &&
                LightgunRoster.TotalGuns >= 2)
                QueueRescan();
        }

        private void QueueRescan() => _rescanQueued = true;

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action)) action();

            if (rescanInterval > 0f && Time.unscaledTime >= _nextRescanTime)
            {
                _nextRescanTime = Time.unscaledTime + rescanInterval;
                QueueRescan();
            }

            // Debounced: device-change bursts (one gun = several devices) and
            // roster ripples collapse into a single scan.
            if (_rescanQueued && !IsScanning && Time.unscaledTime >= _rescanNotBefore)
            {
                _rescanQueued = false;
                _rescanNotBefore = Time.unscaledTime + 1f;
                Rescan();
            }
        }

        private void OnApplicationQuit()
        {
            ReleaseSerialControl();
            RestoreModes();
        }

        // Courtesy on quit: hand solenoid/rumble control back to the firmware so
        // the gun behaves normally on the desktop / in other software.
        // Synchronous — it runs during shutdown. Worst case ~100 ms per gun.
        private void ReleaseSerialControl()
        {
            List<SerialGunInfo> mnkGuns;
            lock (_gunsLock) mnkGuns = _guns.FindAll(g => g.IsMnk);
            foreach (var gun in mnkGuns)
            {
                try
                {
                    using (var port = OpenPort(gun.PortName))
                        port.WriteLine(Commands.ReleaseSerialControl);
                }
                catch { /* gun already unplugged — nothing to clean up */ }
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// Detect serial Blamcons (and apply the mode policy). Runs in the
        /// background; results arrive via GunDetected / DetectedGuns.
        public void Rescan()
        {
            if (IsScanning) return;
            // Captured on the main thread — the worker must not touch the roster.
            int gunsElsewhere = LightgunRoster.CountExcluding(RosterFamily);
            _scanThread = new Thread(() => ScanWorker(gunsElsewhere)) { IsBackground = true };
            _scanThread.Start();
        }

        /// Serial recoil on every detected gun — works in ANY emulation mode
        /// (this is how Hook of the Reaper fires arcade-drive guns). Routed
        /// automatically for MnK guns; public for manual use.
        public void SerialRecoilAll(int pulses = 1) => SendToAll(Commands.Recoil(pulses));

        /// Serial rumble on every detected gun — works in ANY emulation mode.
        public void SerialRumbleAll(int pulses = 1) => SendToAll(Commands.Rumble(pulses));

        // ── Scan worker (background thread) ────────────────────────────────────

        private void ScanWorker(int gunsElsewhere)
        {
            var found = FindBlamconPorts();

            // The mode policy decision. Total physical guns = Blamcons found on
            // this scan + everything other families reported (RS3 ports etc.).
            bool switchMnkGuns =
                modeSwitchPolicy == ModeSwitchPolicy.AlwaysSwitch ||
                (modeSwitchPolicy == ModeSwitchPolicy.SwitchWhenMultiplayer &&
                 found.Count + gunsElsewhere >= 2);

            foreach (var match in found)
            {
                SerialGunInfo info = null;
                try
                {
                    // Shares _sendLock with the feedback writers: a rescan (focus
                    // regained, hot-plug) must not collide with an in-flight
                    // recoil write on the same COM port ("Access is denied").
                    lock (_sendLock)
                    using (var port = OpenPort(match.Port))
                    {
                        string version = Transact(port, Commands.Handshake);
                        if (string.IsNullOrEmpty(version)) continue; // not answering — not a live Blamcon

                        info = new SerialGunInfo
                        {
                            PortName = match.Port,
                            ProductId = match.Pid,
                            FirmwareVersion = version,
                            ModeAtScan = ParseMode(Transact(port, Commands.GetEmulationMode)),
                        };

                        bool inMnkMode = info.ModeAtScan == EmulationMode.MouseKeyboard ||
                                         info.ModeAtScan == EmulationMode.MouseKeyboardBluetooth;
                        if (switchMnkGuns && inMnkMode)
                        {
                            var target = info.ModeAtScan == EmulationMode.MouseKeyboard
                                ? EmulationMode.Gamepad
                                : EmulationMode.GamepadBluetooth;
                            port.WriteLine(Commands.SetEmulationMode(target));
                            info.SwitchedToGamepad = true;
                            // The gun now saves the setting and reboots; this COM port is
                            // about to vanish and the HID gamepad appears in a couple of
                            // seconds. BlamconModule.OnDeviceChange handles the rest.
                        }
                        else if (inMnkMode)
                        {
                            // Gun stays in MnK mode → WE drive its feedback over serial.
                            // Take solenoid + rumble control now (BlamconModule does the
                            // same over HID in Gamepad mode): firmware stops kicking on
                            // trigger pulls by itself — from this moment, splash screen
                            // included, the gun only kicks when the game says so.
                            // Idempotent; re-sent on every rescan. Released on quit (ES).
                            port.WriteLine(Commands.TakeSolenoidControl);
                            port.WriteLine(Commands.TakeRumbleControl);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (logDebugMessages)
                        Debug.LogWarning($"[BlamconSerial] {match.Port}: {e.Message}");
                    continue;
                }

                lock (_gunsLock)
                {
                    // Carry the switch record across re-scans: a gun WE turned into a
                    // gamepad now scans as Gamepad mode, but must still be restored
                    // to its arrival mode on exit.
                    var prior = _guns.Find(g => g.ProductId == info.ProductId);
                    if (prior != null && prior.SwitchedToGamepad && !info.SwitchedToGamepad)
                    {
                        info.SwitchedToGamepad = true;
                        info.ModeAtScan = prior.ModeAtScan;
                    }
                    _guns.RemoveAll(g => g.ProductId == info.ProductId);
                    _guns.Add(info);
                }

                if (logDebugMessages)
                    Debug.Log($"[BlamconSerial] {match.Port}: Blamcon PID {info.ProductId}, fw {info.FirmwareVersion}, " +
                              $"mode {info.ModeAtScan}{(info.SwitchedToGamepad ? " → switching to Gamepad (re-enumerating)" : "")}.");

                var captured = info;
                _mainThreadQueue.Enqueue(() =>
                {
                    GunDetected?.Invoke(captured);
                    if (captured.SwitchedToGamepad) ModeSwitchRequested?.Invoke(captured);
                });
            }

            // Drop guns that vanished — but never one WE switched: its port
            // disappears briefly while it reboots into Gamepad mode, and losing
            // the record would lose the mode-restore on exit. Then report the
            // physical count (roster is main-thread only).
            lock (_gunsLock)
                _guns.RemoveAll(g => !g.SwitchedToGamepad &&
                                     !found.Exists(f => string.Equals(f.Pid, g.ProductId, StringComparison.OrdinalIgnoreCase)));
            int count = found.Count;
            _mainThreadQueue.Enqueue(() => LightgunRoster.Report(RosterFamily, count));
        }

        // ── Restore on exit ────────────────────────────────────────────────────

        // Synchronous on purpose: it runs during shutdown, after which no thread
        // would get to finish. Worst case a few hundred ms per switched gun.
        private void RestoreModes()
        {
            if (_modesRestored || !restoreModeOnExit) return;
            _modesRestored = true;

            List<SerialGunInfo> switched;
            lock (_gunsLock) switched = _guns.FindAll(g => g.SwitchedToGamepad);
            if (switched.Count == 0) return;

            // Port names may have changed when the guns re-enumerated — re-find
            // each gun by its product ID (stable per player slot).
            var ports = FindBlamconPorts();
            foreach (var gun in switched)
            {
                var match = ports.Find(p => string.Equals(p.Pid, gun.ProductId, StringComparison.OrdinalIgnoreCase));
                if (match.Port == null) continue;
                try
                {
                    using (var port = OpenPort(match.Port))
                        port.WriteLine(Commands.SetEmulationMode(gun.ModeAtScan));
                    if (logDebugMessages)
                        Debug.Log($"[BlamconSerial] Restored PID {gun.ProductId} to {gun.ModeAtScan}.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BlamconSerial] Could not restore mode on {match.Port}: {e.Message}");
                }
            }
        }

        // ── Serial plumbing ────────────────────────────────────────────────────

        // Same parameters ARC uses: 9600 8-N-1, no handshake, DTR+RTS on,
        // newline-terminated ASCII, 500 ms timeouts.
        private static SerialPort OpenPort(string portName)
        {
            var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true,
                NewLine = "\n",
            };
            port.Open();
            return port;
        }

        private static string Transact(SerialPort port, string command)
        {
            try
            {
                port.WriteLine(command);
                return port.ReadLine()?.Trim();
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        private static EmulationMode ParseMode(string reply)
        {
            if (int.TryParse(reply, out int value) &&
                value >= (int)EmulationMode.MouseKeyboard && value <= (int)EmulationMode.GamepadBluetooth)
                return (EmulationMode)value;
            return EmulationMode.Unknown;
        }

        private void SendToMnkGuns(string command)
        {
            var targets = new List<SerialGunInfo>();
            lock (_gunsLock)
                foreach (var g in _guns)
                    if (g.IsMnk) targets.Add(g);
            SendTo(targets, command);
        }

        // Fire-and-forget feedback write, off the main thread (opening a COM
        // port can take tens of ms — too slow for a fire event).
        private void SendToAll(string command) => SendTo(DetectedGuns, command);

        private readonly object _sendLock = new object();

        private void SendTo(List<SerialGunInfo> guns, string command)
        {
            if (guns.Count == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Serialized: two near-simultaneous commands (recoil + rumble) must
                // not race for the same COM port — the loser would be dropped.
                lock (_sendLock)
                {
                    foreach (var gun in guns)
                    {
                        try
                        {
                            using (var port = OpenPort(gun.PortName))
                            {
                                // Belt-and-braces: serial control was already taken at
                                // scan time (SM.0.1/SM.1.1) — without it, F.* commands
                                // are ignored (verified on fw 1.0.16). Re-assert here in
                                // case the gun was power-cycled since the last scan.
                                port.WriteLine(Commands.TakeSolenoidControl);
                                port.WriteLine(Commands.TakeRumbleControl);
                                port.WriteLine(command);
                            }
                        }
                        catch (Exception e)
                        {
                            if (logDebugMessages)
                                Debug.LogWarning($"[BlamconSerial] '{command}' → {gun.PortName} failed: {e.Message}");
                        }
                    }
                }
            });
        }

        // ── Port discovery ─────────────────────────────────────────────────────

        // Windows: walk HKLM\SYSTEM\CurrentControlSet\Enum for device IDs matching
        // VID_3673&PID_01xx/02xx and read Device Parameters\PortName — the exact
        // strategy Blamcon ARC uses. Filtering by VID means we never write probe
        // commands at some other device's COM port (e.g. the RS3's).
        private static List<PortMatch> FindBlamconPorts()
        {
            var results = new List<PortMatch>();
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
                                            var portName = paramsKey?.GetValue("PortName") as string;
                                            if (!string.IsNullOrEmpty(portName) && livePorts.Contains(portName) &&
                                                !results.Exists(r => string.Equals(r.Port, portName, StringComparison.OrdinalIgnoreCase)))
                                                results.Add(new PortMatch { Port = portName, Pid = match.Groups[1].Value });
                                        }
                                    }
                                    catch { /* orphaned registry entry — skip */ }
                                }
                            }
                        }
                    }
                }
            }
#endif
            // Non-Windows: no safe way to identify Blamcon ports without probing
            // unknown serial devices, so detection is Windows-only (like ARC's
            // registry strategy; Linux would be the /dev/ttyACM* ports).
            return results;
        }
    }
}
