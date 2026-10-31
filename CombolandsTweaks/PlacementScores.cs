using System;
using System.Collections.Generic;
using Entities;
using Entities.Data;
using Framework;
using Environment;
using GameState;
using HarmonyLib;
using Interaction;
using Interaction.InteractionStates;
using Library;
using UI;
using UnityEngine;

namespace CombolandsTweaks
{
    // Shows, on every tile, the points the hovered (or currently placing) building card would score if placed there.
    // Uses the game's own placement preview (Behaviour.GetScorePreview x Multiplier, the green number shown at the
    // cursor while placing) evaluated with a hidden copy of the building moved across the map.
    internal class PlacementScores : MonoBehaviour
    {
        private const float RefreshInterval = 1f;

        private readonly Dictionary<Tile, long> _scores = new Dictionary<Tile, long>();
        private long _best;

        private Building _probe;
        private GameTag _probeTag = GameTag.None;

        private GameTag _shownTag = GameTag.None;
        private GameTag _shownConsumable = GameTag.None;
        private int _shownBoard;
        private float _nextRefresh;
        private bool _loggedError;

        private GUIStyle _style;

        private void Update()
        {
            if (Plugin.ScoreOverlayKey.Value.IsDown())
            {
                Plugin.ScoreOverlayEnabled.Value = !Plugin.ScoreOverlayEnabled.Value;
                Plugin.Log.LogInfo($"Placement score overlay {(Plugin.ScoreOverlayEnabled.Value ? "on" : "off")}");
            }

            GameTag tag = GameTag.None, consumable = GameTag.None;
            if (Plugin.ScoreOverlayEnabled.Value)
                FindTarget(out tag, out consumable);

            if (tag == GameTag.None)
            {
                Clear();
                return;
            }

            int board = BoardSignature();
            if (tag != _shownTag || consumable != _shownConsumable || board != _shownBoard || Time.unscaledTime >= _nextRefresh)
            {
                bool changed = tag != _shownTag || board != _shownBoard;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Compute(tag, consumable);
                if (changed)
                    Plugin.Log.LogInfo($"Placement scores for {tag}: {_scores.Count} tiles, best {_best} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
                _shownTag = tag;
                _shownConsumable = consumable;
                _shownBoard = board;
                _nextRefresh = Time.unscaledTime + RefreshInterval;
            }
        }

        // Hovering a card in the bottom bar wins; otherwise the building being placed.
        private static void FindTarget(out GameTag tag, out GameTag consumable)
        {
            tag = GameTag.None;
            consumable = GameTag.None;

            if (MonoSingleton<BuildingChoiceBar>.HasInstance)
            {
                List<BuildingChoiceButton> choices = MonoSingleton<BuildingChoiceBar>.Instance.Choices;
                if (choices != null)
                {
                    foreach (BuildingChoiceButton choice in choices)
                    {
                        if (choice == null || !choice.isActiveAndEnabled || !IsMouseOver((RectTransform)choice.transform))
                            continue;
                        tag = choice.GameTag;
                        consumable = choice.BuildingPlaceConsumableWhenCreated != null ? choice.BuildingPlaceConsumableWhenCreated.GetTag() : GameTag.None;
                        return;
                    }
                }
            }

            if (SerializedMonoSingleton<InteractionController>.HasInstance
                && SerializedMonoSingleton<InteractionController>.Instance.CurrentInteractionState is PlacingBuilding placing)
            {
                Building placed = Traverse.Create(placing).Field<Building>("_currentlyPlacing").Value;
                if (placed != null)
                {
                    tag = placed.Tag;
                    consumable = placing.ConsumableToApplyWhenCreated;
                }
            }
        }

        private static bool IsMouseOver(RectTransform rt)
        {
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
        }

        // Cheap fingerprint of the board so the overlay recomputes after a building is placed, moved or removed.
        private static int BoardSignature()
        {
            if (!MonoSingleton<BuildingController>.HasInstance)
                return 0;
            unchecked
            {
                int hash = 17;
                foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings)
                {
                    if (b == null)
                        continue;
                    hash = hash * 31 + (int)b.Tag;
                    hash = hash * 31 + b.X * 397 + b.Y;
                }
                return hash;
            }
        }

        private void Compute(GameTag tag, GameTag consumable)
        {
            _scores.Clear();
            _best = 0;
            if (!SerializedMonoSingleton<MapController>.HasInstance || !MonoSingleton<BuildingController>.HasInstance)
                return;

            Building probe = GetProbe(tag);
            if (probe == null)
                return;

            BuildingController controller = MonoSingleton<BuildingController>.Instance;
            double consumableFactor = consumable == GameTag.BsNegMult ? 0.5 : 1.0;
            foreach (Tile tile in SerializedMonoSingleton<MapController>.Instance.Grid)
            {
                if (tile == null)
                    continue;
                try
                {
                    probe.transform.position = new Vector3(tile.X, tile.Y);
                    BuildingExtensions.ResetCaches();
                    probe.Values.ResetCaches();

                    if (!controller.CanBuildBuildingAt(probe, tile.X, tile.Y, out CantPlaceBuildingReason reason)
                        || reason == CantPlaceBuildingReason.NestingDolls)
                        continue;

                    Dictionary<Tile, long> preview = probe.Behaviour.GetScorePreview(probe);
                    if (preview == null)
                        continue;
                    long sum = 0;
                    foreach (long v in preview.Values)
                        sum += v;
                    long total = (long)Math.Round(sum * probe.Multiplier * consumableFactor);
                    if (total == 0)
                        continue;
                    _scores[tile] = total;
                    if (total > _best)
                        _best = total;
                }
                catch (Exception e)
                {
                    if (!_loggedError)
                    {
                        _loggedError = true;
                        Plugin.Log.LogError($"Scoring {tag} at {tile.X},{tile.Y} failed: {e}");
                    }
                }
            }

            // Leave the shared range/adjacency caches as the game expects them: rebuilt on demand.
            BuildingExtensions.ResetCaches();
        }

        // A hidden, never-built copy of the building, the same kind of instance the game creates for its placement
        // ghost. It never occupies a tile, so moving it around doesn't affect the board.
        private Building GetProbe(GameTag tag)
        {
            if (_probe != null && _probeTag == tag)
                return _probe;
            DestroyProbe();

            BuildingData data = ScriptableObjectSingleton<GamePieceDataHolder>.Instance.GetBuildingDataFor(tag);
            if (data == null || data.BuildingPrefab == null)
                return null;
            _probe = Instantiate(data.BuildingPrefab, new Vector3(-1000f, -1000f), Quaternion.identity);
            _probe.name = "CombolandsTweaks score probe";
            _probe.Init(tag, playerInitiated: true);
            _probe.gameObject.SetActive(false);
            _probeTag = tag;
            return _probe;
        }

        private void DestroyProbe()
        {
            if (_probe != null)
                Destroy(_probe.gameObject);
            _probe = null;
            _probeTag = GameTag.None;
        }

        private void Clear()
        {
            _scores.Clear();
            _shownTag = GameTag.None;
        }

        private void OnGUI()
        {
            if (_scores.Count == 0 || Event.current.type != EventType.Repaint)
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;

            // Size text to the on-screen tile size so it follows the camera zoom.
            float tilePx = Mathf.Abs(cam.WorldToScreenPoint(Vector3.right).x - cam.WorldToScreenPoint(Vector3.zero).x);
            if (tilePx < 8f)
                return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            _style.fontSize = Mathf.Clamp(Mathf.RoundToInt(tilePx * 0.32f), 9, 40);

            foreach (KeyValuePair<Tile, long> entry in _scores)
            {
                // Lower part of the tile, below where the game draws its own per-tile preview numbers.
                Vector3 screen = cam.WorldToScreenPoint(new Vector3(entry.Key.X, entry.Key.Y - 0.28f));
                if (screen.z < 0f)
                    continue;
                Rect rect = new Rect(screen.x - tilePx / 2f, Screen.height - screen.y - tilePx / 4f, tilePx, tilePx / 2f);
                string text = entry.Value.ToString();

                _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, _style);
                _style.normal.textColor = ColorFor(entry.Value);
                GUI.Label(rect, text, _style);
            }
        }

        // White for low scores through green, gold for the best tile(s).
        private Color ColorFor(long score)
        {
            if (score < 0)
                return new Color(1f, 0.45f, 0.4f);
            if (_best <= 0)
                return Color.white;
            if (score == _best)
                return new Color(1f, 0.84f, 0.2f);
            float t = Mathf.Clamp01((float)score / _best);
            return Color.Lerp(new Color(0.95f, 0.95f, 0.95f), new Color(0.35f, 1f, 0.35f), t);
        }

        private void OnDestroy()
        {
            DestroyProbe();
        }
    }
}
