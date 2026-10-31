using System.Collections.Generic;
using Entities;
using Library;
using UnityEngine;

namespace CombolandsTweaks
{
    // Puts on each placed building the points it scores when it activates: its own placement preview (target tiles
    // and buildings in range, times its multiplier), the same number the game shows while placing it.
    internal class BuildingScores : MonoBehaviour
    {
        private const float RefreshInterval = 0.5f;

        private readonly Dictionary<Building, long> _scores = new Dictionary<Building, long>();
        private float _nextRefresh;
        private bool _loggedError;
        private GUIStyle _style;

        private void Update()
        {
            if (Plugin.BuildingScoresKey.Value.IsDown())
                Plugin.BuildingScoresEnabled.Value = !Plugin.BuildingScoresEnabled.Value;

            if (!Plugin.BuildingScoresEnabled.Value || !MonoSingleton<BuildingController>.HasInstance)
            {
                _scores.Clear();
                return;
            }
            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;

            _scores.Clear();
            foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings)
            {
                if (b == null || !b.isActiveAndEnabled)
                    continue;
                try
                {
                    long points = PlacementScores.PreviewTotal(b, 1.0);
                    if (points != 0)
                        _scores[b] = points;
                }
                catch (System.Exception e)
                {
                    if (!_loggedError)
                    {
                        _loggedError = true;
                        Plugin.Log.LogError($"Scoring {b.Tag} at {b.X},{b.Y} failed: {e}");
                    }
                }
            }
        }

        private void OnGUI()
        {
            // While placing, the game draws its own preview numbers on these buildings.
            if (_scores.Count == 0 || PlacementScores.ShowingPlacement || UpgradePreview.Showing || Event.current.type != EventType.Repaint)
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;
            float tilePx = Mathf.Abs(cam.WorldToScreenPoint(Vector3.right).x - cam.WorldToScreenPoint(Vector3.zero).x);
            if (tilePx < 8f)
                return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            _style.fontSize = Mathf.Clamp(Mathf.RoundToInt(tilePx * 0.3f * Plugin.ScoreTextScale.Value), 8, 56);

            foreach (KeyValuePair<Building, long> entry in _scores)
            {
                Building b = entry.Key;
                if (b == null)
                    continue;
                // Top of the tile, clear of the placement overlay's numbers at the bottom.
                Vector3 screen = cam.WorldToScreenPoint(new Vector3(b.X, b.Y + 0.3f));
                if (screen.z < 0f)
                    continue;
                var rect = new Rect(screen.x - tilePx / 2f, Screen.height - screen.y - tilePx / 4f, tilePx, tilePx / 2f);
                string text = entry.Value.ToString();
                _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, _style);
                _style.normal.textColor = entry.Value < 0 ? new Color(1f, 0.45f, 0.4f) : new Color(0.45f, 0.9f, 1f);
                GUI.Label(rect, text, _style);
            }
        }
    }
}
