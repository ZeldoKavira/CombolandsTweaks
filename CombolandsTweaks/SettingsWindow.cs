using UnityEngine;
using UnityEngine.UI;

namespace CombolandsTweaks
{
    // F8 window for the mod's options. Plain IMGUI (the game has legacy input enabled, so it gets mouse events).
    internal class SettingsWindow : MonoBehaviour
    {
        private const int WindowId = 0x434c5457; // "CLTW"

        private bool _open;
        private Rect _rect = new Rect(40f, 120f, 340f, 10f);

        // The game ignores map clicks while the pointer is over uGUI (UiUtils.IsPointerOverUIObject raycasts the
        // EventSystem), so an invisible uGUI panel under the window stops clicks going through to the board.
        private GameObject _blockerCanvas;
        private RectTransform _blocker;

        private void Update()
        {
            if (Plugin.MenuKey.Value.IsDown())
                _open = !_open;
            UpdateBlocker();
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, $"{Plugin.Name} {Plugin.Version}", GUILayout.Width(340f));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Placement scores");
            Plugin.ScoreOverlayEnabled.Value = GUILayout.Toggle(Plugin.ScoreOverlayEnabled.Value,
                $" Show score on every tile ({Plugin.ScoreOverlayKey.Value})");
            Plugin.ScoreCountNearby.Value = GUILayout.Toggle(Plugin.ScoreCountNearby.Value,
                " Include the bonus to nearby buildings");
            GUI.enabled = Plugin.ScoreCountNearby.Value;
            Plugin.ScoreShowBreakdown.Value = GUILayout.Toggle(Plugin.ScoreShowBreakdown.Value,
                " Show own / nearby split at the cursor");
            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Text size {Plugin.ScoreTextScale.Value:0.0}x", GUILayout.Width(110f));
            float scale = GUILayout.HorizontalSlider(Plugin.ScoreTextScale.Value, 0.5f, 2f);
            Plugin.ScoreTextScale.Value = Mathf.Round(scale * 10f) / 10f;
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Plugin.MenuKey.Value} toggles this window");
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                _open = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void UpdateBlocker()
        {
            if (_blockerCanvas == null)
            {
                _blockerCanvas = new GameObject("CombolandsTweaks input blocker");
                DontDestroyOnLoad(_blockerCanvas);
                Canvas canvas = _blockerCanvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue;
                _blockerCanvas.AddComponent<GraphicRaycaster>();

                var panel = new GameObject("Blocker", typeof(RectTransform));
                panel.transform.SetParent(_blockerCanvas.transform, false);
                Image image = panel.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0f);
                image.raycastTarget = true;
                _blocker = (RectTransform)panel.transform;
                _blocker.anchorMin = _blocker.anchorMax = Vector2.zero;
                _blocker.pivot = Vector2.zero;
            }

            _blockerCanvas.SetActive(_open);
            if (!_open)
                return;
            // IMGUI rects are top-left based; the overlay canvas is bottom-left based, in screen pixels.
            float scale = _blockerCanvas.GetComponent<Canvas>().scaleFactor;
            _blocker.anchoredPosition = new Vector2(_rect.x, Screen.height - _rect.yMax) / scale;
            _blocker.sizeDelta = new Vector2(_rect.width, _rect.height) / scale;
        }

        private void OnDestroy()
        {
            if (_blockerCanvas != null)
                Destroy(_blockerCanvas);
        }
    }
}
