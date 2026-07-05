using UnityEngine;
using UnityEngine.UI;

namespace Palmware.Lightguns
{
    // ===========================================================================
    //  SindenBorder
    // ---------------------------------------------------------------------------
    //  The white frame a Sinden lightgun needs around the screen edge. The gun is
    //  a camera: its driver software finds the display by edge-detecting this
    //  border, so it must be visible whenever a Sinden is in use — including over
    //  dark scenes, menus and videos. Games without a native border force players
    //  into overlay hacks (ReShade etc.); rendering it ourselves is the whole
    //  point of "Sinden compatibility".
    //
    //  Builds its own overlay canvas (four strips, no prefab wiring) on first
    //  Show(). Sorting order is near-max so nothing in the game draws over it.
    //  SindenModule drives Show/Hide; the settings menu can tune thickness/color
    //  live via the properties.
    // ===========================================================================
    public class SindenBorder : MonoBehaviour
    {
        [Tooltip("Border thickness as a percentage of screen HEIGHT. Sinden recommends ~1–3%; " +
                 "thicker = more reliable tracking, thinner = less screen lost. Tune live.")]
        [Range(0.5f, 10f)]
        [SerializeField] private float thicknessPercent = 2f;

        [Tooltip("Pure white is the most reliable for the camera. Other bright colors can work " +
                 "(the Sinden software has matching color settings) but change them together.")]
        [SerializeField] private Color color = Color.white;

        private Canvas _canvas;
        private Image[] _strips; // top, bottom, left, right
        private int _lastWidth, _lastHeight;
        private float _lastThickness;

        public bool IsVisible => _canvas != null && _canvas.enabled;

        public float ThicknessPercent
        {
            get => thicknessPercent;
            set { thicknessPercent = Mathf.Clamp(value, 0.5f, 10f); Layout(); }
        }

        public Color Color
        {
            get => color;
            set
            {
                color = value;
                if (_strips == null) return;
                foreach (var s in _strips) s.color = color;
            }
        }

        public void Show()
        {
            if (_canvas == null) Build();
            _canvas.enabled = true;
            Layout();
        }

        public void Hide()
        {
            if (_canvas != null) _canvas.enabled = false;
        }

        private void Update()
        {
            // Track resolution changes while visible (fullscreen toggles etc.).
            if (IsVisible && (Screen.width != _lastWidth || Screen.height != _lastHeight ||
                              !Mathf.Approximately(thicknessPercent, _lastThickness)))
                Layout();
        }

        private void OnDisable() => Hide();

        private void Build()
        {
            var go = new GameObject("SindenBorderCanvas");
            go.transform.SetParent(transform, false);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32760; // above everything, including fades and menus

            _strips = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                var strip = new GameObject(i switch { 0 => "Top", 1 => "Bottom", 2 => "Left", _ => "Right" });
                strip.transform.SetParent(go.transform, false);
                _strips[i] = strip.AddComponent<Image>();
                _strips[i].color = color;
                _strips[i].raycastTarget = false; // never block UI clicks
            }
        }

        // Anchor the four strips to the screen edges at the current thickness.
        private void Layout()
        {
            if (_strips == null) return;

            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            _lastThickness = thicknessPercent;
            float px = Screen.height * (thicknessPercent / 100f);

            SetStrip(_strips[0], new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), new Vector2(0, px)); // top
            SetStrip(_strips[1], new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0f), new Vector2(0, px)); // bottom
            SetStrip(_strips[2], new Vector2(0, 0), new Vector2(0, 1), new Vector2(0f, 0.5f), new Vector2(px, 0)); // left
            SetStrip(_strips[3], new Vector2(1, 0), new Vector2(1, 1), new Vector2(1f, 0.5f), new Vector2(px, 0)); // right
        }

        // Anchor the strip along one screen edge (pivot on that edge) and give it a
        // fixed thickness on the other axis.
        private static void SetStrip(Image strip, Vector2 anchorMin, Vector2 anchorMax,
                                     Vector2 pivot, Vector2 sizeDelta)
        {
            var rt = strip.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = Vector2.zero;
        }
    }
}
