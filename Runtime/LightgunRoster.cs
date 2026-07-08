using System;
using System.Collections.Generic;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  LightgunRoster
    // ---------------------------------------------------------------------------
    //  Shared count of PHYSICAL guns, reported by each family's serial/registry
    //  scanner (which sees a gun whatever input mode it's in — every Blamcon and
    //  RS3 exposes its USB serial side in every mode).
    //
    //  This is what drives the package's MODE POLICY:
    //    • 1 gun  → leave it in whatever mode it arrived in. MnK-mode guns play
    //      through the manager's mouse path with serial feedback; gamepad/joystick
    //      guns play through their device module. No switching.
    //    • 2+ guns → every gun must be individually identifiable (two mice can't
    //      share one OS cursor), so modules switch their MnK guns to the family's
    //      per-device mode: Blamcon → Gamepad (ZY.3.1), RS3 → Joystick (ZJ).
    //
    //  Report() and Changed are main-thread only (background scanners marshal to
    //  the main thread before reporting).
    // ===========================================================================
    public static class LightgunRoster
    {
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        // Raised when any family's count changes — modules re-run their mode
        // policy on this (e.g. a second gun hot-plugged mid-session).
        public static event Action Changed;

        public static int TotalGuns
        {
            get
            {
                int total = 0;
                foreach (var kv in _counts) total += kv.Value;
                return total;
            }
        }

        // 2+ physical guns present → per-device modes required.
        public static bool Multiplayer => TotalGuns >= 2;

        public static int CountOf(string family) =>
            _counts.TryGetValue(family, out int count) ? count : 0;

        public static int CountExcluding(string family) => TotalGuns - CountOf(family);

        public static void Report(string family, int count)
        {
            _counts.TryGetValue(family, out int old);
            if (old == count) return;
            _counts[family] = count;
            Changed?.Invoke();
        }
    }
}
