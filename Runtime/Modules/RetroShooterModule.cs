using System;
using System.IO.Ports;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  RetroShooterModule
    // ---------------------------------------------------------------------------
    //  Gun module for the Retro Shooter RS3 "Reaper" lightgun.
    //
    //  Input  : arrives through the Input System — in JOYSTICK mode (this module's
    //           default) the trigger is <Joystick>/trigger on a recognizable
    //           "3AGAME"/"Retro Shooter" HID device, and aim is the joystick stick
    //           (absolute, ~ -1..1), mapped + smoothed here.
    //  Output : recoil, ammo LEDs, slide-return and rumble go over a virtual serial
    //           COM port (default COM4 @ 115200) as short ASCII commands. If the
    //           port can't be opened, every command is a silent no-op and the game
    //           keeps running on mouse / Blamcon input.
    //
    //  Why joystick mode: recoil works over serial in either mode, but only in
    //  joystick mode is the trigger identifiable as the RS3 (so recoil routes to
    //  it and not to a real mouse). Trade-off: the gun no longer drives the OS
    //  cursor, so aim is read from the stick instead. The mode is forgotten on
    //  power-off, so it is re-sent on connect ('ZJ') and restored on exit ('ZM').
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

        [Header("Serial (feedback output)")]
        [SerializeField] private string portName = "COM4";
        [SerializeField] private int    baudRate = 115200;

        [Tooltip("Force the gun into JOYSTICK mode on connect (sends 'ZJ'). Required for recoil: in " +
                 "mouse+keyboard mode the trigger fires as a mouse click, so the game can't tell it's the " +
                 "RS3 and never sends recoil. Mouse+keyboard mode is restored on exit (sends 'ZM').")]
        [SerializeField] private bool forceJoystickMode = true;

        [Tooltip("Minimum seconds between recoil commands. The firmware can't recoil faster than " +
                 "~12/sec (≥83ms apart); this also swallows the occasional double-fire from a single " +
                 "trigger pull.")]
        [SerializeField] private float minRecoilInterval = 0.083f;

        [Header("Aim (joystick mode)")]
        [Tooltip("Map the RS3's joystick stick to the crosshair. Off = this module provides no aim.")]
        [SerializeField] private bool provideAim = true;
        [Tooltip("Flip the horizontal axis if aiming LEFT moves the crosshair RIGHT.")]
        [SerializeField] private bool invertX = false;
        [Tooltip("Flip the vertical axis if aiming UP moves the crosshair DOWN.")]
        [SerializeField] private bool invertY = false;
        [Tooltip("Low-pass smoothing (seconds) to damp the IR sensor's jitter. Higher = steadier " +
                 "but more lag; 0 = raw/instant. ~0.04–0.08 is a good starting range. Tune live.")]
        [SerializeField] private float aimSmoothTime = 0.05f;

        [SerializeField] private bool logDebugMessages = false;

        private SerialPort _serialPort;
        private Thread _readThread;
        private volatile bool _reading;
        private float _lastFireTime = -1f;

        // Aim state: movement detection + SmoothDamp working values.
        private Vector2 _lastStick;
        private bool _haveLastStick;
        private Vector2 _smoothedAim;
        private Vector2 _aimVelocity;
        private bool _haveSmoothedAim;

        public bool IsSerialConnected => _serialPort != null && _serialPort.IsOpen;

        public override string DisplayName => "Retro Shooter RS3";

        // "Usable" here means the feedback port is open — input alone still works
        // without it, but then the gun is indistinguishable from a generic joystick.
        public override bool IsConnected => IsSerialConnected;

        // Is this the RS3? Lets the manager route recoil to us instead of the Blamcon.
        public override bool Owns(InputDevice device) => IsRetroShooterDevice(device);

        // ── Lifecycle ──────────────────────────────────────────────────────────

        protected override void OnEnable()
        {
            base.OnEnable();

            Connect();
            if (IsSerialConnected)
            {
                Write(Commands.EnterExternalControlMode);

                // Put the gun into joystick mode so its trigger arrives as
                // <Joystick>/trigger. The gun re-enumerates on USB after this switch.
                if (forceJoystickMode) Write(Commands.JoystickMode);
            }

            if (logDebugMessages)
                Debug.Log($"[RS3] Enabled — port '{portName}' connected={IsSerialConnected}, joystickMode={forceJoystickMode}.");
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            Disconnect();
        }

        public override void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            // Hot-swap: if the RS3's HID side (re)appears and our port isn't open
            // (gun was plugged in after startup), try the serial side again.
            if (!IsRetroShooterDevice(device)) return;
            if (change != InputDeviceChange.Added && change != InputDeviceChange.Reconnected) return;
            if (IsSerialConnected) return;

            Connect();
            if (IsSerialConnected)
            {
                Write(Commands.EnterExternalControlMode);
                if (forceJoystickMode) Write(Commands.JoystickMode);
            }
        }

        // ── Serial plumbing ────────────────────────────────────────────────────

        private void Connect()
        {
            if (IsSerialConnected) return;

            try
            {
                _serialPort ??= new SerialPort(portName, baudRate);
                _serialPort.ReadTimeout = 50;
                _serialPort.Open();

                _reading = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();

                if (logDebugMessages) Debug.Log($"[RS3] Serial port {portName} opened.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RS3] Could not open {portName}: {e.Message}. " +
                                 "Recoil/reload output disabled — game still runs.");
            }
        }

        private void Disconnect()
        {
            _reading = false;
            try
            {
                if (IsSerialConnected)
                {
                    // Hand the gun back to mouse+keyboard mode so it works as a normal
                    // pointer on the desktop once the game stops.
                    if (forceJoystickMode) Write(Commands.MouseKeyboardMode);
                    Write(Commands.ExitExternalControlMode);
                    _serialPort.Close();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RS3] Error closing {portName}: {e.Message}");
            }

            if (_readThread != null && _readThread.IsAlive) _readThread.Join(200);
            if (logDebugMessages) Debug.Log("[RS3] Serial port closed.");
        }

        // Background drain of incoming bytes so the OS buffer doesn't fill. We don't
        // act on inbound data — inputs come through the Input System, not the port.
        private void ReadLoop()
        {
            while (_reading && _serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    string message = _serialPort.ReadLine();
                    if (logDebugMessages && !string.IsNullOrEmpty(message))
                        Debug.Log($"[RS3] Received: {message}");
                }
                catch (TimeoutException) { /* expected — keep polling */ }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void Write(string command)
        {
            if (!IsSerialConnected) return;
            try { _serialPort.Write(command); }
            catch (Exception e) { Debug.LogWarning($"[RS3] Write '{command}' failed: {e.Message}"); }
        }

        // ── Aim ────────────────────────────────────────────────────────────────

        // In joystick mode Unity surfaces the RS3 as a generic Joystick whose aim
        // X/Y form the stick control (absolute, ~ -1..1). Map that to the screen,
        // then smooth it to damp the IR sensor's jitter.
        public override bool TryGetAim(out Vector2 screenPosition, out bool movedThisFrame)
        {
            screenPosition = default;
            movedThisFrame = false;
            if (!provideAim) return false;

            var js = Joystick.current;
            if (js == null) return false;

            // Raw value, so the stick's default centre deadzone doesn't dead-spot the screen.
            Vector2 raw = js.stick.ReadUnprocessedValue();

            movedThisFrame = !_haveLastStick || (raw - _lastStick).sqrMagnitude > 1e-6f;
            _lastStick = raw;
            _haveLastStick = true;

            float nx = invertX ? -raw.x : raw.x;
            float ny = invertY ? -raw.y : raw.y;
            Vector2 target = new Vector2(
                (nx * 0.5f + 0.5f) * Screen.width,
                (ny * 0.5f + 0.5f) * Screen.height);

            if (!_haveSmoothedAim)
            {
                _smoothedAim = target;
                _haveSmoothedAim = true;
            }

            // Low-pass smoothing damps jitter; 0 = instant.
            _smoothedAim = aimSmoothTime > 0f
                ? Vector2.SmoothDamp(_smoothedAim, target, ref _aimVelocity, aimSmoothTime)
                : target;

            screenPosition = _smoothedAim;
            return true;
        }

        // ── Feedback ───────────────────────────────────────────────────────────

        // Recoil + set the ammo LEDs to the number of bullets left.
        public override void Recoil(InputDevice firingDevice, int remainingAmmo)
        {
            // Debounce: a single trigger pull can occasionally land twice, and the firmware
            // can't keep up with back-to-back pulses anyway. Drop anything too soon.
            if (_lastFireTime >= 0f && Time.unscaledTime - _lastFireTime < minRecoilInterval)
            {
                if (logDebugMessages) Debug.Log("[RS3] Recoil ignored — within cooldown.");
                return;
            }
            _lastFireTime = Time.unscaledTime;

            string cmd = Commands.Bullet(remainingAmmo);
            if (logDebugMessages) Debug.Log($"[RS3] Recoil → '{cmd}' (connected={IsSerialConnected})");
            Write(cmd);
        }

        // Return the slide to its forward position (reload).
        public override void ReloadFeedback() => Write(Commands.SlideReturn);

        public override void Rumble() => Write(Commands.Rumble);

        // SetAmmo: the RS3 has no standalone ammo command — its LEDs only update as
        // part of a recoil pulse ("Z0"-"Z5"), which Recoil() already sends.

        // True when the given input device is the RS3, so recoil can be routed to
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
    }
}
