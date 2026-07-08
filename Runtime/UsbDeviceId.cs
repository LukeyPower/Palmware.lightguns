using System.Text.RegularExpressions;
using UnityEngine.InputSystem;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  UsbDeviceId
    // ---------------------------------------------------------------------------
    //  Pulls the USB vendor/product id out of an InputDevice's description
    //  capabilities JSON (Unity reports them as decimal integers for HID and
    //  RawInput devices). This is what lets a module tell a GUN'S mouse/joystick
    //  interface apart from a real desktop mouse or some other joystick — an
    //  MnK-mode Blamcon is a Mouse device carrying VID 0x3673, an RS3 carries
    //  VID 0x0483 + PID 0x5750–0x5753.
    //
    //  Not every device exposes ids (varies by driver/backend) — callers must
    //  handle the false case with a sensible fallback.
    // ===========================================================================
    public static class UsbDeviceId
    {
        private static readonly Regex VendorPattern  = new Regex("\"vendorId\"\\s*:\\s*(\\d+)",  RegexOptions.IgnoreCase);
        private static readonly Regex ProductPattern = new Regex("\"productId\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);

        public static bool TryGetVendorId(InputDevice device, out int vendorId) =>
            TryParse(device, VendorPattern, out vendorId);

        public static bool TryGetProductId(InputDevice device, out int productId) =>
            TryParse(device, ProductPattern, out productId);

        private static bool TryParse(InputDevice device, Regex pattern, out int value)
        {
            value = 0;
            string caps = device?.description.capabilities;
            if (string.IsNullOrEmpty(caps)) return false;
            var match = pattern.Match(caps);
            return match.Success && int.TryParse(match.Groups[1].Value, out value);
        }
    }
}
