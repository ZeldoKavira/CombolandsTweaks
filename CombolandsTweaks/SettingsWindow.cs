using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CombolandsTweaks
{
    // F8 window for the mod's options. Plain IMGUI (the game has legacy input enabled, so it gets mouse events).
    internal class SettingsWindow : MonoBehaviour
    {
        private const int WindowId = 0x434c5457; // "CLTW"

        private bool _open;
        private Rect _rect = new Rect(40f, 120f, 360f, 10f);

        // The game ignores map clicks while the pointer is over uGUI (UiUtils.IsPointerOverUIObject raycasts the
        // EventSystem), so invisible uGUI panels under the mod's windows stop clicks going through to the board.
        private GameObject _blockerCanvas;
        private readonly List<RectTransform> _blockers = new List<RectTransform>();

        private void Update()
        {
            if (Plugin.MenuKey.Value.IsDown())
                _open = !_open;
            UpdateBlockers(_open ? _rect : Rect.zero, PoolWindow.Open ? PoolWindow.ScreenRect : Rect.zero);
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, $"{Plugin.Name} {Plugin.Version}", GUILayout.Width(360f));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Placement scores");
            Plugin.ScoreOverlayEnabled.Value = GUILayout.Toggle(Plugin.ScoreOverlayEnabled.Value,
                $" Show score on every tile ({Plugin.ScoreOverlayKey.Value})");
            GUI.enabled = Plugin.ScoreOverlayEnabled.Value;
            Plugin.ScoreCountNearby.Value = GUILayout.Toggle(Plugin.ScoreCountNearby.Value,
                " Include the bonus to nearby buildings");
            Plugin.ScoreShowBreakdown.Value = GUILayout.Toggle(Plugin.ScoreShowBreakdown.Value,
                " Show own / nearby split at the cursor");
            Plugin.ScoreCardBadges.Value = GUILayout.Toggle(Plugin.ScoreCardBadges.Value,
                " Show each card's best score on the card");
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Frame best tiles: {Plugin.ScoreMarkBest.Value}", GUILayout.Width(150f));
            for (int i = 0; i <= 3; i++)
                if (GUILayout.Toggle(Plugin.ScoreMarkBest.Value == i, i == 0 ? "Off" : i.ToString(), GUI.skin.button, GUILayout.Width(40f)))
                    Plugin.ScoreMarkBest.Value = i;
            GUILayout.EndHorizontal();
            GUILayout.Label($"  {Plugin.CycleBestKey.Value} while placing jumps to the best tiles");
            GUI.enabled = true;

            GUILayout.Space(6f);
            Plugin.BuildingScoresEnabled.Value = GUILayout.Toggle(Plugin.BuildingScoresEnabled.Value,
                $" Show each building's points per activation ({Plugin.BuildingScoresKey.Value})");

            Plugin.UpgradePreviewEnabled.Value = GUILayout.Toggle(Plugin.UpgradePreviewEnabled.Value,
                " Building upgrades: show points per turn now / with it");
            Plugin.HeirloomCountsEnabled.Value = GUILayout.Toggle(Plugin.HeirloomCountsEnabled.Value,
                " Heirloom popups: show how many you have");

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Text size {Plugin.ScoreTextScale.Value:0.0}x", GUILayout.Width(110f));
            Plugin.ScoreTextScale.Value = Mathf.Round(GUILayout.HorizontalSlider(Plugin.ScoreTextScale.Value, 0.5f, 2f) * 10f) / 10f;
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Extra speed {Plugin.SpeedBoost.Value:0.0}x", GUILayout.Width(110f));
            Plugin.SpeedBoost.Value = Mathf.Round(GUILayout.HorizontalSlider(Plugin.SpeedBoost.Value, 1f, 5f) * 4f) / 4f;
            GUILayout.EndHorizontal();
            GUILayout.Label("  On top of the game's own Game Speed setting");

            GUILayout.Space(6f);
            if (GUILayout.Button(PoolWindow.Open ? $"Hide draw pool ({Plugin.PoolKey.Value})" : $"Show draw pool ({Plugin.PoolKey.Value})"))
                PoolWindow.Open = !PoolWindow.Open;

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Plugin.MenuKey.Value} toggles this window");
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                _open = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void UpdateBlockers(params Rect[] rects)
        {
            if (_blockerCanvas == null)
            {
                _blockerCanvas = new GameObject("CombolandsTweaks input blocker");
                DontDestroyOnLoad(_blockerCanvas);
                Canvas canvas = _blockerCanvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue;
                _blockerCanvas.AddComponent<GraphicRaycaster>();
                _blockers.Clear();
            }
            while (_blockers.Count < rects.Length)
            {
                var panel = new GameObject("Blocker", typeof(RectTransform));
                panel.transform.SetParent(_blockerCanvas.transform, false);
                Image image = panel.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0f);
                image.raycastTarget = true;
                var rt = (RectTransform)panel.transform;
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                _blockers.Add(rt);
            }

            // IMGUI rects are top-left based; the overlay canvas is bottom-left based, in screen pixels.
            float scale = _blockerCanvas.GetComponent<Canvas>().scaleFactor;
            for (int i = 0; i < rects.Length; i++)
            {
                Rect r = rects[i];
                bool on = r.width > 0f && r.height > 0f;
                _blockers[i].gameObject.SetActive(on);
                if (!on)
                    continue;
                _blockers[i].anchoredPosition = new Vector2(r.x, Screen.height - r.yMax) / scale;
                _blockers[i].sizeDelta = new Vector2(r.width, r.height) / scale;
            }
        }

        private void OnDestroy()
        {
            if (_blockerCanvas != null)
                Destroy(_blockerCanvas);
        }
    }
}
