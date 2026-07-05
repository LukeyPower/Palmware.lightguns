using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
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
    //  What this component does, so the game "just works" whatever mode the gun
    //  was left in:
    //   1. DETECT  — find Blamcon COM ports via the Windows registry (VID 3673),
    //                confirm each with the ZD.1024 handshake (replies firmware
    //                version), and read the current emulation mode (ZD.3).
    //   2. SWITCH  — if a gun is in Mouse/Keyboard mode, write Gamepad mode
    //                (ZY.3.1, or ZY.3.3 for the Bluetooth variant). The gun saves
    //                the setting, reboots, and re-enumerates as a gamepad a couple
    //                of seconds later — at which point the normal BlamconModule
    //                sees it (InputSystem.onDeviceChange) and takes over.
    //   3. RESTORE — on application quit, put switched guns back into the mode
    //                they arrived in (courtesy: the setting persists on the gun,
    //                so a player's desktop/other-software setup is untouched).
    //   4. FALLBACK — serial recoil/rumble (F.0.1.n / F.1.1.n) work in ANY mode;
    //                exposed here for the rare case auto-switching is disabled
    //                and a MnK-mode gun still needs kick (Hook-of-the-Reaper
    //                style).
    //
    //  Place it next to LightgunManager. It is NOT an ILightgunModule — it owns
    //  no input; it only prepares the hardware so BlamconModule can do its job.
    //  Detection runs on a background thread (registry + 500 ms serial timeouts
    //  would hitch the main thread); events are re-raised on the main thread.
    // ===========================================================================
    public class BlamconSerial : MonoBehaviour
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

        // One entry per gun found on serial (any emulation mode).
        public class SerialGunInfo
        {
            public string PortName;            // e.g. "COM5" (can change after re-enumeration)
            public string ProductId;           // "0100".."0103" wired P1–P4, "0200".."0203" wireless
            public string FirmwareVersion;     // reply to ZD.1024
            public EmulationMode ModeAtScan = EmulationMode.Unknown;
            public bool SwitchedToGamepad;     // true if WE changed its mode this session
        }

        [Header("Auto mode switch")]
        [Tooltip("If a Blamcon is found in Mouse/Keyboard mode, switch it to Gamepad mode so the " +
                 "HID layer (BlamconModule: absolute aim, recoil, multiplayer) can claim it. The gun " +
                 "reboots and re-enumerates — expect it to reappear a couple of seconds later.")]
        [SerializeField] private bool autoSwitchToGamepadMode = true;

        [Tooltip("On quit, restore any gun WE switched back to the mode it arrived in. The mode is a " +
                 "persisted firmware setting, so without this the gun stays in Gamepad mode after the " +
                 "game closes.")]
        [SerializeField] private bool restoreModeOnExit = true;

        [Header("Detection")]
        [Tooltip("Seconds between automatic re-scans, for guns plugged in after startup while in MnK " +
                 "mode (a MnK gun never appears in the Input System, so nothing else can notice it). " +
                 "0 = scan once on enable; call Rescan() to scan manually.")]
        [SerializeField] private float rescanInterval = 0f;

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
        private bool _modesRestored;

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

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            Rescan();
            if (rescanInterval > 0f) _nextRescanTime = Time.unscaledTime + rescanInterval;
        }

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action)) action();

            if (rescanInterval > 0f && Time.unscaledTime >= _nextRescanTime)
            {
                _nextRescanTime = Time.unscaledTime + rescanInterval;
                Rescan();
            }
        }

        private void OnApplicationQuit() => RestoreModes();

        // ── Public API ─────────────────────────────────────────────────────────

        /// Detect serial Blamcons (and auto-switch MnK guns if enabled). Runs in
        /// the background; results arrive via GunDetected / DetectedGuns.
        public void Rescan()
        {
            if (IsScanning) return;
            _scanThread = new Thread(ScanWorker) { IsBackground = true };
            _scanThread.Start();
        }

        /// Serial recoil on every detected gun — works in ANY emulation mode
        /// (this is how Hook of the Reaper fires arcade-drive guns). Fallback for
        /// when auto-switch is off and a gun is stuck in MnK mode; in Gamepad
        /// mode prefer BlamconModule's HID recoil.
        public void SerialRecoilAll(int pulses = 1) => SendToAll(Commands.Recoil(pulses));

        /// Serial rumble on every detected gun — works in ANY emulation mode.
        public void SerialRumbleAll(int pulses = 1) => SendToAll(Commands.Rumble(pulses));

        // ── Scan worker (background thread) ────────────────────────────────────

        private void ScanWorker()
        {
            foreach (var match in FindBlamconPorts())
            {
                SerialGunInfo info = null;
                try
                {
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
                        if (autoSwitchToGamepadMode && inMnkMode)
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

        // Fire-and-forget feedback write to every known gun, off the main thread
        // (opening a COM port can take tens of ms — too slow for a fire event).
        private void SendToAll(string command)
        {
            var guns = DetectedGuns;
            if (guns.Count == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var gun in guns)
                {
                    try
                    {
                        using (var port = OpenPort(gun.PortName))
                            port.WriteLine(command);
                    }
                    catch (Exception e)
                    {
                        if (logDebugMessages)
                            Debug.LogWarning($"[BlamconSerial] '{command}' → {gun.PortName} failed: {e.Message}");
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
