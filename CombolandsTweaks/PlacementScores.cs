using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // A full pass over the map takes ~10-15 ms, so it is spread over frames to avoid hitches.
        private const double FrameBudgetMs = 2.0;

        private Dictionary<Tile, long> _scores = new Dictionary<Tile, long>();
        private long _best;

        private Building _probe;
        private GameTag _probeTag = GameTag.None;

        private GameTag _shownTag = GameTag.None;
        private GameTag _shownConsumable = GameTag.None;
        private int _shownBoard;
        private float _nextRefresh;
        private bool _loggedError;

        // The pass in progress; its results replace _scores when it finishes.
        private bool _passActive;
        private bool _passLog;
        private readonly List<Tile> _passTiles = new List<Tile>();
        private int _passIndex;
        private Dictionary<Tile, long> _passScores = new Dictionary<Tile, long>();
        private long _passBest;
        private double _passFactor;
        private readonly Stopwatch _passTime = new Stopwatch();

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
            bool targetChanged = tag != _shownTag || consumable != _shownConsumable;
            if (targetChanged)
            {
                // Never show one card's numbers for another while the new pass runs.
                _scores.Clear();
                _best = 0;
            }
            if (targetChanged || board != _shownBoard || (!_passActive && Time.unscaledTime >= _nextRefresh))
            {
                StartPass(tag, consumable, log: targetChanged || board != _shownBoard);
                _shownTag = tag;
                _shownConsumable = consumable;
                _shownBoard = board;
            }
            if (_passActive)
                StepPass();
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

        private void StartPass(GameTag tag, GameTag consumable, bool log)
        {
            _passActive = false;
            _passTiles.Clear();
            _passScores.Clear();
            _passBest = 0;
            _passIndex = 0;
            _passTime.Reset();
            if (!SerializedMonoSingleton<MapController>.HasInstance || !MonoSingleton<BuildingController>.HasInstance)
                return;
            if (GetProbe(tag) == null)
                return;

            foreach (Tile tile in SerializedMonoSingleton<MapController>.Instance.Grid)
                if (tile != null)
                    _passTiles.Add(tile);
            _passFactor = consumable == GameTag.BsNegMult ? 0.5 : 1.0;
            _passLog = log;
            _passActive = true;
            BuildingExtensions.ResetCaches();
        }

        private void StepPass()
        {
            if (_probe == null || !MonoSingleton<BuildingController>.HasInstance)
            {
                _passActive = false;
                return;
            }

            BuildingController controller = MonoSingleton<BuildingController>.Instance;
            var frame = Stopwatch.StartNew();
            _passTime.Start();
            while (_passIndex < _passTiles.Count && frame.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                Tile tile = _passTiles[_passIndex++];
                long total = ScoreAt(_probe, tile, controller, _passFactor);
                if (total == 0)
                    continue;
                _passScores[tile] = total;
                if (total > _passBest)
                    _passBest = total;
            }
            ForgetProbeCaches(_probe);
            _passTime.Stop();

            if (_passIndex < _passTiles.Count)
                return;

            _passActive = false;
            (_scores, _passScores) = (_passScores, _scores);
            _best = _passBest;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            if (_passLog)
                Plugin.Log.LogInfo($"Placement scores for {_probeTag}: {_scores.Count} tiles, best {_best} ({_passTime.Elapsed.TotalMilliseconds:F1} ms)");
        }

        private long ScoreAt(Building probe, Tile tile, BuildingController controller, double factor)
        {
            try
            {
                probe.transform.position = new Vector3(tile.X, tile.Y);
                ForgetProbeCaches(probe);

                if (!controller.CanBuildBuildingAt(probe, tile.X, tile.Y, out CantPlaceBuildingReason reason)
                    || reason == CantPlaceBuildingReason.NestingDolls)
                    return 0;

                Dictionary<Tile, long> preview = probe.Behaviour.GetScorePreview(probe);
                if (preview == null)
                    return 0;
                long sum = 0;
                foreach (long v in preview.Values)
                    sum += v;
                return (long)Math.Round(sum * probe.Multiplier * factor);
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.Log.LogError($"Scoring {probe.Tag} at {tile.X},{tile.Y} failed: {e}");
                }
                return 0;
            }
        }

        private static readonly AccessTools.FieldRef<Dictionary<Building, HashSet<Tile>>> AdjacencyCache =
            AccessTools.StaticFieldRefAccess<Dictionary<Building, HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_adjacencyCache"));
        private static readonly AccessTools.FieldRef<Dictionary<Building, HashSet<Tile>>> RangeCache =
            AccessTools.StaticFieldRefAccess<Dictionary<Building, HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_rangeCache"));
        private static readonly AccessTools.FieldRef<Dictionary<(Building, Tile), HashSet<Tile>>> OverrideTileRangeCache =
            AccessTools.StaticFieldRefAccess<Dictionary<(Building, Tile), HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_overrideTileRangeCache"));
        private static readonly AccessTools.FieldRef<Dictionary<(Building, GamePieceCategory), int>> CategoryAdjacencyCache =
            AccessTools.StaticFieldRefAccess<Dictionary<(Building, GamePieceCategory), int>>(AccessTools.Field(typeof(BuildingExtensions), "_countsOfCategoryAdjacentTo"));

        // The game's range/adjacency caches are keyed by building. Only the probe moves, so dropping just its entries
        // keeps every other building's cached results valid (BuildingExtensions.ResetCaches per tile was ~20-50 ms).
        private static void ForgetProbeCaches(Building probe)
        {
            AdjacencyCache()?.Remove(probe);
            RangeCache()?.Remove(probe);
            RemoveKeys(OverrideTileRangeCache(), k => k.Item1 == probe);
            RemoveKeys(CategoryAdjacencyCache(), k => k.Item1 == probe);
            probe.Values.ResetCaches();
        }

        private static readonly List<object> KeyScratch = new List<object>();

        private static void RemoveKeys<TKey, TValue>(Dictionary<TKey, TValue> dict, Func<TKey, bool> match)
        {
            if (dict == null || dict.Count == 0)
                return;
            KeyScratch.Clear();
            foreach (TKey key in dict.Keys)
                if (match(key))
                    KeyScratch.Add(key);
            foreach (object key in KeyScratch)
                dict.Remove((TKey)key);
        }

        // A hidden, never-built copy of the building, the same kind of instance the game creates for its placement
        // ghost. It never occupies a tile, so moving it around doesn't affect the board.
        private Building GetProbe(GameTag tag)
        {
            if (_probe != null && _probeTag == tag)
                return _probe;
            DestroyProbe();
            if (_failedProbes.Contains(tag))
                return null;

            BuildingData data = ScriptableObjectSingleton<GamePieceDataHolder>.Instance.GetBuildingDataFor(tag);
            if (data == null || data.BuildingPrefab == null)
                return null;
            // Init reads the cooldown, which for some buildings (e.g. Apiary) looks at neighbouring tiles, so the probe
            // must start on the map: the middle, like the game's own placement ghost.
            MapController map = SerializedMonoSingleton<MapController>.Instance;
            Building probe = Instantiate(data.BuildingPrefab, new Vector3(map.Width / 2, map.Height / 2), Quaternion.identity);
            probe.name = "CombolandsTweaks score probe";
            try
            {
                probe.Init(tag, playerInitiated: true);
            }
            catch (Exception e)
            {
                Destroy(probe.gameObject);
                _failedProbes.Add(tag);
                Plugin.Log.LogError($"Couldn't create a scoring copy of {tag}: {e}");
                return null;
            }
            probe.gameObject.SetActive(false);
            _probe = probe;
            _probeTag = tag;
            return _probe;
        }

        private readonly HashSet<GameTag> _failedProbes = new HashSet<GameTag>();

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
            _passActive = false;
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
