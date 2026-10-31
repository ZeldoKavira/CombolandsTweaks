using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    // Shows, on every tile, the points the hovered (or currently placing) building card would score if placed there,
    // marks the best tiles, and puts each card's best score on the card in the bottom bar.
    // Uses the game's own placement preview (Behaviour.GetScorePreview x Multiplier, the green number shown at the
    // cursor while placing) evaluated with a hidden copy of the building moved across the map. Optionally adds what
    // the placement adds to existing buildings' own previews (e.g. Flowers next to an Apiary).
    internal class PlacementScores : MonoBehaviour
    {
        private const float RefreshInterval = 1f;
        private const int TopCount = 3;

        // A full pass over the map takes 10-100 ms (more with neighbours), so passes run in the background, a slice
        // per frame, one at a time: the hovered/placing building first, then the other cards in the bar.
        private const double FrameBudgetMs = 2.0;

        private struct TileScore
        {
            public double Own;
            public double Nearby;
            public string Note; // placement bonus that applied (Scaffolding, Nesting Doll, ...)
            public double Total => Own + Nearby;
        }

        private class Result
        {
            public Dictionary<Tile, TileScore> Scores;
            public double Best;
            public bool PerTurn;
            public List<Tile> Top;
            public int Board;
            public bool Nearby;
            public float Time;
        }

        private struct Key : IEquatable<Key>
        {
            public GameTag Tag;
            public GameTag Consumable;
            public bool Equals(Key other) => Tag == other.Tag && Consumable == other.Consumable;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => ((int)Tag * 397) ^ (int)Consumable;
        }

        private readonly Dictionary<Key, Result> _results = new Dictionary<Key, Result>();

        // True while a building is hovered or being placed, so other overlays can stay out of the way.
        internal static bool ShowingPlacement;

        // What is on screen this frame.
        private Key? _target;
        private bool _placing;
        private readonly List<(BuildingChoiceButton button, Key key)> _cards = new List<(BuildingChoiceButton, Key)>();
        private int _board;
        private bool _nearby;
        private int _cycleIndex = -1;

        private Building _probe;
        private GameTag _probeTag = GameTag.None;
        private readonly HashSet<GameTag> _failedProbes = new HashSet<GameTag>();
        private bool _loggedError;

        // The pass in progress.
        private bool _passActive;
        private Key _passKey;
        private int _passBoard;
        private bool _passNearby;
        private bool _passPerTurn;
        private bool _perTurn;
        private readonly List<Tile> _passTiles = new List<Tile>();
        private int _passIndex;
        private Dictionary<Tile, TileScore> _passScores = new Dictionary<Tile, TileScore>();
        private double _passFactor;
        private readonly Stopwatch _passTime = new Stopwatch();

        // Existing buildings' own preview totals before the placement, and which of them can see each tile.
        private readonly Dictionary<Building, double> _baseline = new Dictionary<Building, double>();
        // What each existing triggering building hands out per activation before the placement.
        private readonly Dictionary<Building, double> _deliveredBaseline = new Dictionary<Building, double>();
        // Set by NearbyGain: which existing triggering buildings would also trigger the new one.
        private readonly List<string> _triggeredBy = new List<string>();
        private readonly Dictionary<Tile, List<Building>> _watchers = new Dictionary<Tile, List<Building>>();
        private readonly List<Building> _alwaysWatch = new List<Building>();
        private readonly List<Building> _candidates = new List<Building>();

        private GUIStyle _style;
        private GUIStyle _tipStyle;
        private GUIStyle _badgeStyle;
        private Texture2D _white;

        private void Update()
        {
            if (Plugin.ScoreOverlayKey.Value.IsDown())
            {
                Plugin.ScoreOverlayEnabled.Value = !Plugin.ScoreOverlayEnabled.Value;
                Plugin.Log.LogInfo($"Placement score overlay {(Plugin.ScoreOverlayEnabled.Value ? "on" : "off")}");
            }

            FindTargets();
            ShowingPlacement = _target != null;
            _board = BoardSignature();
            _nearby = Plugin.ScoreCountNearby.Value;
            _perTurn = Plugin.ScorePerTurn.Value;
            if (_target == null && _cards.Count == 0)
            {
                _passActive = false;
                return;
            }

            // A pass for an old board is useless; one for a background card gives way to a target with nothing to show.
            if (_passActive && (_passBoard != _board || _passNearby != _nearby || _passPerTurn != _perTurn
                                || (_target is Key t && !_passKey.Equals(t) && Usable(t) == null)))
                _passActive = false;

            if (!_passActive)
            {
                Key? next = NextToCompute();
                if (next is Key k)
                    StartPass(k);
            }
            if (_passActive)
                StepPass();

            if (_placing && Plugin.CycleBestKey.Value.IsDown())
                JumpToNextBest();
        }

        // Hovering a card in the bottom bar wins; otherwise the building being placed. The visible cards are queued
        // for their badges.
        private void FindTargets()
        {
            _target = null;
            _placing = false;
            _cards.Clear();
            if (!Plugin.ScoreOverlayEnabled.Value)
                return;

            if (MonoSingleton<BuildingChoiceBar>.HasInstance)
            {
                List<BuildingChoiceButton> choices = MonoSingleton<BuildingChoiceBar>.Instance.Choices;
                if (choices != null)
                {
                    foreach (BuildingChoiceButton choice in choices)
                    {
                        if (choice == null || !choice.isActiveAndEnabled || choice.GameTag == GameTag.None)
                            continue;
                        var key = new Key
                        {
                            Tag = choice.GameTag,
                            Consumable = choice.BuildingPlaceConsumableWhenCreated != null ? choice.BuildingPlaceConsumableWhenCreated.GetTag() : GameTag.None,
                        };
                        if (Plugin.ScoreCardBadges.Value)
                            _cards.Add((choice, key));
                        if (_target == null && IsMouseOver((RectTransform)choice.transform))
                            _target = key;
                    }
                }
            }
            if (_target != null)
                return;

            if (SerializedMonoSingleton<InteractionController>.HasInstance
                && SerializedMonoSingleton<InteractionController>.Instance.CurrentInteractionState is PlacingBuilding placing)
            {
                Building placed = Traverse.Create(placing).Field<Building>("_currentlyPlacing").Value;
                if (placed != null)
                {
                    _target = new Key { Tag = placed.Tag, Consumable = placing.ConsumableToApplyWhenCreated };
                    _placing = true;
                }
            }
            if (_target == null)
                _cycleIndex = -1;
        }

        // A result computed for the current board, however old.
        private Result Usable(Key key)
        {
            return _results.TryGetValue(key, out Result r) && r.Board == _board && r.Nearby == _nearby && r.PerTurn == _perTurn ? r : null;
        }

        private Key? NextToCompute()
        {
            if (_target is Key t)
            {
                Result r = Usable(t);
                // The shown building is refreshed now and then: multipliers can change without the board changing.
                if (r == null || Time.unscaledTime - r.Time >= RefreshInterval)
                    return t;
            }
            foreach ((BuildingChoiceButton _, Key key) in _cards)
                if (Usable(key) == null)
                    return key;
            return null;
        }

        private static bool IsMouseOver(RectTransform rt)
        {
            Camera cam = CanvasCamera(rt);
            return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
        }

        private static Camera CanvasCamera(RectTransform rt)
        {
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            return canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
        }

        // Cheap fingerprint of the board so the overlay recomputes after a building is placed, moved or removed.
        internal static int BoardSignature()
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

        private void StartPass(Key key)
        {
            _passActive = false;
            _passTiles.Clear();
            _passScores = new Dictionary<Tile, TileScore>();
            _passIndex = 0;
            _passTime.Reset();
            if (!SerializedMonoSingleton<MapController>.HasInstance || !MonoSingleton<BuildingController>.HasInstance)
                return;
            if (GetProbe(key.Tag) == null)
            {
                // Remember the failure so the queue moves on instead of retrying every frame.
                _results[key] = new Result { Scores = new Dictionary<Tile, TileScore>(), Top = new List<Tile>(), Board = _board, Nearby = _nearby, Time = float.MaxValue };
                return;
            }

            foreach (Tile tile in SerializedMonoSingleton<MapController>.Instance.Grid)
                if (tile != null)
                    _passTiles.Add(tile);
            _passKey = key;
            _passBoard = _board;
            _passNearby = _nearby;
            _passPerTurn = _perTurn;
            _passFactor = key.Consumable == GameTag.BsNegMult ? 0.5 : 1.0;
            _passActive = true;
            _passTime.Start();
            BuildingExtensions.ResetCaches();
            if (_passNearby)
                CollectWatchers();
            _passTime.Stop();
        }

        // Baseline preview of every existing building, and for each tile the buildings whose range or adjacency covers
        // it (those are the only ones a new neighbour there can change).
        private void CollectWatchers()
        {
            _baseline.Clear();
            _deliveredBaseline.Clear();
            _watchers.Clear();
            _alwaysWatch.Clear();
            foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings)
            {
                if (b == null || b == _probe)
                    continue;
                try
                {
                    _baseline[b] = Value(b);
                    if (UpgradePreview.Triggerers.ContainsKey(b.Tag))
                        _deliveredBaseline[b] = Delivered(b, 1);
                    if (b.Tag == GameTag.TownHall)
                    {
                        // Scores a contiguous residential block, which can reach beyond its adjacent tiles.
                        _alwaysWatch.Add(b);
                        continue;
                    }
                    AddWatcher(b, b.GetTilesInRange());
                    AddWatcher(b, b.GetAdjacentTiles());
                }
                catch (Exception e)
                {
                    _baseline.Remove(b);
                    LogOnce($"Baseline for {b.Tag} at {b.X},{b.Y} failed: {e}");
                }
            }
        }

        private void AddWatcher(Building b, HashSet<Tile> tiles)
        {
            if (tiles == null)
                return;
            foreach (Tile t in tiles)
            {
                if (t == null)
                    continue;
                if (!_watchers.TryGetValue(t, out List<Building> list))
                    _watchers[t] = list = new List<Building>();
                if (!list.Contains(b))
                    list.Add(b);
            }
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
                if (ScoreAt(_probe, tile, controller, out TileScore score) && Math.Abs(score.Total) >= 0.05)
                    _passScores[tile] = score;
            }
            ForgetCaches(_probe);
            _passTime.Stop();

            if (_passIndex < _passTiles.Count)
                return;

            _passActive = false;
            List<Tile> top = _passScores.Where(e => e.Value.Total > 0).OrderByDescending(e => e.Value.Total).Take(TopCount).Select(e => e.Key).ToList();
            bool firstForBoard = Usable(_passKey) == null;
            _results[_passKey] = new Result
            {
                Scores = _passScores,
                Best = top.Count > 0 ? _passScores[top[0]].Total : 0,
                Top = top,
                Board = _passBoard,
                Nearby = _passNearby,
                PerTurn = _passPerTurn,
                Time = Time.unscaledTime,
            };
            if (_results.Count > 32)
                PruneResults();
            if (firstForBoard)
                Plugin.Log.LogInfo($"Placement scores for {_passKey.Tag}: {_passScores.Count} tiles, best {Fmt(_results[_passKey].Best)}{(_passPerTurn ? "/turn" : "")} ({_passTime.Elapsed.TotalMilliseconds:F1} ms{(_passNearby ? ", with nearby" : "")})");
        }

        private void PruneResults()
        {
            foreach (Key k in _results.Keys.ToList())
                if (_results[k].Board != _board)
                    _results.Remove(k);
        }

        private bool ScoreAt(Building probe, Tile tile, BuildingController controller, out TileScore score)
        {
            score = default;
            try
            {
                probe.transform.position = new Vector3(tile.X, tile.Y);
                ForgetCaches(probe);

                if (!controller.CanBuildBuildingAt(probe, tile.X, tile.Y, out CantPlaceBuildingReason reason))
                    return false;

                // Nesting Doll: nothing is built; the existing copy gets extra mult instead (BuildBuildingAt).
                if (reason == CantPlaceBuildingReason.NestingDolls)
                {
                    float dollMult = MonoSingleton<ItemHeirloomController>.Instance.GetItemWithTagEquipped(GameTag.NestingDoll).Behaviour.GetMultParam(null);
                    score.Own = MultGain(tile.Building, dollMult);
                    score.Note = $"Nesting Doll: +{dollMult:0.##} mult to the {NameOf(tile.Building.Tag)} here";
                    return true;
                }

                // Mult the new building gets on being built (BuildBuildingAt), which the game's preview leaves out.
                float bonusMult = PlacementMultBonus(tile, out score.Note);
                if (bonusMult != 0f && probe.Tag.CanGetMult())
                {
                    LocalMult(probe.Values) += bonusMult;
                    ForgetCaches(probe);
                    try
                    {
                        score.Own = OwnValue(probe);
                    }
                    finally
                    {
                        LocalMult(probe.Values) -= bonusMult;
                        ForgetCaches(probe);
                    }
                }
                else
                {
                    score.Own = OwnValue(probe);
                }
                if (_passPerTurn && OwnRate(probe) <= 0)
                {
                    long once = PreviewTotal(probe, _passFactor);
                    if (once != 0)
                        score.Note = Join(score.Note, $"no countdown: {once} per activation when something triggers it");
                }

                // A building that triggers others (Marketplace -> Stalls, Sawmill, ...) earns their points when it
                // activates; the game's preview only shows what it scores itself.
                long triggered = TriggeredPoints(probe, out int count);
                if (triggered != 0)
                {
                    // Per turn, OwnValue already includes it.
                    if (!_passPerTurn)
                        score.Own += triggered;
                    score.Note = Join(score.Note, $"triggers {count} building{(count == 1 ? "" : "s")}: +{triggered} per activation");
                }

                if (_passNearby)
                {
                    score.Nearby = NearbyGain(probe, tile);
                    foreach (string by in _triggeredBy)
                        score.Note = Join(score.Note, by);
                    score.Nearby += InventorGain(probe, tile, ref score.Note);
                    // A scoring building that gets built over is lost.
                    if (tile.Building != null && _baseline.TryGetValue(tile.Building, out double lost) && Math.Abs(lost) >= 0.05)
                    {
                        score.Nearby -= lost;
                        score.Note = Join(score.Note, $"replaces a {NameOf(tile.Building.Tag)} (-{Fmt(lost)})");
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                LogOnce($"Scoring {probe.Tag} at {tile.X},{tile.Y} failed: {e}");
                return false;
            }
        }

        internal static readonly AccessTools.FieldRef<GamePieceLocalValues, float> LocalMult =
            AccessTools.FieldRefAccess<GamePieceLocalValues, float>("_localMultiplierChanges");

        // Scaffolding under the tile, and Checkered Flag on the run's first building.
        private static float PlacementMultBonus(Tile tile, out string note)
        {
            float mult = 0f;
            note = null;
            if (tile.Building != null && tile.Building.Tag == GameTag.Scaffolding)
            {
                float m = tile.Building.Behaviour.GetMultParam(tile.Building);
                mult += m;
                note = Join(note, $"Scaffolding +{m:0.##} mult");
            }
            if (MonoSingleton<GameController>.HasInstance && !MonoSingleton<GameController>.Instance.FirstBuildingBuilt
                && MonoSingleton<ItemHeirloomController>.HasInstance
                && MonoSingleton<ItemHeirloomController>.Instance.IsItemWithTagEquipped(GameTag.CheckeredFlag))
            {
                float m = MonoSingleton<ItemHeirloomController>.Instance.GetCountOfItemWithTagEquipped(GameTag.CheckeredFlag)
                          * Entities.ItemBehaviours.ItemBehaviours.Instance.GetBehaviourFor(GameTag.CheckeredFlag).GetMultParam(null);
                mult += m;
                note = Join(note, $"Checkered Flag +{m:0.##} mult");
            }
            return mult;
        }

        // Inventor with 4+ council votes: an Engineering building gives every adjacent building extra mult on build.
        private double InventorGain(Building probe, Tile tile, ref string note)
        {
            if (!SerializedMonoSingleton<CouncilFavour>.HasInstance || !probe.Categories.ContainsCategory(GamePieceCategory.Engineering))
                return 0;
            CouncilFavour council = SerializedMonoSingleton<CouncilFavour>.Instance;
            if (council.GetVotesFor(CouncilCharacter.Inventor) < 4)
                return 0;
            float mult = council.GetMultParamFor(CouncilCharacter.Inventor);
            double gain = 0;
            foreach (Tile t in probe.GetAdjacentTiles())
                if (t != null && t != tile && !t.IsEmpty && t.Building != null)
                    gain += MultGain(t.Building, mult);
            if (gain != 0)
                note = Join(note, $"Inventor +{mult:0.##} mult to neighbours");
            return gain;
        }

        // Points of the activations a triggering building hands out each time it activates: every candidate's
        // preview points times the chance it gets picked (see UpgradePreview's trigger table).
        private static long TriggeredPoints(Building probe, out int count)
        {
            count = 0;
            if (!UpgradePreview.Triggerers.ContainsKey(probe.Tag))
                return 0;
            List<Building> targets = UpgradePreview.Candidates(probe);
            count = targets.Count;
            return (long)Math.Round(Delivered(probe, 1));
        }

        // Points a triggering building hands out per activation: each target's activation value times the chance it
        // gets picked. A target that triggers others passes its own deliveries on, down to `depth` more steps.
        internal static double Delivered(Building t, int depth)
        {
            if (!UpgradePreview.Triggerers.ContainsKey(t.Tag))
                return 0;
            List<Building> targets = UpgradePreview.Candidates(t);
            double share = UpgradePreview.Share(t, targets);
            double points = 0;
            foreach (Building x in targets)
                points += share * ActivationValue(x, depth);
            return points;
        }

        // Points one activation of a building is worth: its own preview times activations, plus what it triggers.
        internal static double ActivationValue(Building x, int depth)
        {
            double value = PreviewTotal(x, 1.0) * Math.Max(1, x.Behaviour.GetActivationCount(x));
            if (depth > 0)
                value += Delivered(x, depth - 1);
            return value;
        }

        // What extra mult adds to an existing building's preview points.
        private double MultGain(Building b, float mult)
        {
            if (b == null || b.Values == null || !b.Tag.CanGetMult())
                return 0;
            double before = Value(b);
            LocalMult(b.Values) += mult;
            ForgetCaches(b);
            try
            {
                return Value(b) - before;
            }
            finally
            {
                LocalMult(b.Values) -= mult;
                ForgetCaches(b);
            }
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + ", " + b;

        private static string NameOf(GameTag tag) =>
            ScriptableObjectSingleton<StringLookup>.Instance.GetNameForTag(tag, out string name) ? name : tag.ToString();

        // Activations per turn from the building's own countdown (Lamppost-style cooldown cuts show up here). Irrigation
        // and Reservoir zero the countdown of adjacent crops (not Sunflower Farm) every end of turn, so those activate
        // every turn whatever their cooldown.
        internal static double OwnRate(Building b)
        {
            if (b.GamePieceWithCount == null || b.Cooldown <= 0)
                return 0;
            if (IsIrrigated(b))
                return 1.0;
            return 1.0 / b.Cooldown;
        }

        internal static bool IsIrrigated(Building b)
        {
            if (b.Tag == GameTag.SunflowerFarm || !b.Categories.ContainsCategory(GamePieceCategory.Crop))
                return false;
            foreach (Tile t in b.GetAdjacentTiles())
                if (t != null && !t.IsEmpty && t.Building != null && (t.Building.Tag == GameTag.Irrigation || t.Building.Tag == GameTag.Reservoir))
                    return true;
            return false;
        }

        // Average points per turn: one activation's worth (own points and what it triggers) times activations per turn.
        internal static double TurnValue(Building b, double factor = 1.0) =>
            (PreviewTotal(b, factor) * Math.Max(1, b.Behaviour.GetActivationCount(b)) + Delivered(b, 1)) * OwnRate(b);

        // An existing building's worth in this pass's units.
        private double Value(Building b) => _passPerTurn ? TurnValue(b) : PreviewTotal(b, 1.0);

        private double OwnValue(Building probe) => _passPerTurn ? TurnValue(probe, _passFactor) : PreviewTotal(probe, _passFactor);

        // Whole numbers from 10 up, one decimal below.
        internal static string Fmt(double v) => Math.Abs(v) >= 10 ? Math.Round(v).ToString("0") : v.ToString("0.#");

        // The game's green placement number: the building's preview points times its multiplier.
        internal static long PreviewTotal(Building b, double factor)
        {
            Dictionary<Tile, long> preview = b.Behaviour?.GetScorePreview(b);
            if (preview == null)
                return 0;
            long sum = 0;
            foreach (long v in preview.Values)
                sum += v;
            return (long)Math.Round(sum * b.Multiplier * factor);
        }

        // Puts the probe on the tile for a moment and re-runs the preview of every existing building that can see it.
        private double NearbyGain(Building probe, Tile tile)
        {
            _candidates.Clear();
            _triggeredBy.Clear();
            if (_watchers.TryGetValue(tile, out List<Building> watching))
                _candidates.AddRange(watching);
            foreach (Building b in _alwaysWatch)
                if (!_candidates.Contains(b))
                    _candidates.Add(b);
            if (_candidates.Count == 0)
                return 0;

            Building previous = tile.Building;
            double gain = 0;
            tile.AssignBuilding(probe);
            try
            {
                foreach (Building b in _candidates)
                {
                    // Something built over is replaced, not boosted.
                    if (b == previous || b == null || !_baseline.TryGetValue(b, out double before))
                        continue;
                    ForgetCaches(b);
                    gain += Value(b) - before;
                    // An existing triggering building that would now also pick the new one: what it hands out per
                    // activation changes (the new building's value, minus what random-pickers stop giving others).
                    if (_deliveredBaseline.TryGetValue(b, out double deliveredBefore) && UpgradePreview.Candidates(b).Contains(probe))
                    {
                        double delta = Delivered(b, 1) - deliveredBefore;
                        // Per turn, Value(b) already counts what it hands out, scaled by how often it activates.
                        if (_passPerTurn)
                            delta *= OwnRate(b);
                        else
                            gain += delta;
                        if (Math.Abs(delta) >= 0.05)
                            _triggeredBy.Add($"{NameOf(b.Tag)} triggers it ({(delta > 0 ? "+" : "")}{Fmt(delta)})");
                    }
                }
            }
            finally
            {
                if (previous != null)
                    tile.AssignBuilding(previous);
                else
                    tile.RemoveCurrentBuilding();
                foreach (Building b in _candidates)
                    if (b != null)
                        ForgetCaches(b);
            }
            return gain;
        }

        private void LogOnce(string message)
        {
            if (_loggedError)
                return;
            _loggedError = true;
            Plugin.Log.LogError(message);
        }

        private static readonly AccessTools.FieldRef<Dictionary<Building, HashSet<Tile>>> AdjacencyCache =
            AccessTools.StaticFieldRefAccess<Dictionary<Building, HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_adjacencyCache"));
        private static readonly AccessTools.FieldRef<Dictionary<Building, HashSet<Tile>>> RangeCache =
            AccessTools.StaticFieldRefAccess<Dictionary<Building, HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_rangeCache"));
        private static readonly AccessTools.FieldRef<Dictionary<(Building, Tile), HashSet<Tile>>> OverrideTileRangeCache =
            AccessTools.StaticFieldRefAccess<Dictionary<(Building, Tile), HashSet<Tile>>>(AccessTools.Field(typeof(BuildingExtensions), "_overrideTileRangeCache"));
        private static readonly AccessTools.FieldRef<Dictionary<(Building, GamePieceCategory), int>> CategoryAdjacencyCache =
            AccessTools.StaticFieldRefAccess<Dictionary<(Building, GamePieceCategory), int>>(AccessTools.Field(typeof(BuildingExtensions), "_countsOfCategoryAdjacentTo"));

        // The game's range/adjacency caches are keyed by building. Dropping just the entries of the buildings whose
        // surroundings changed keeps everyone else's cached results valid.
        internal static void ForgetCaches(Building building)
        {
            AdjacencyCache()?.Remove(building);
            RangeCache()?.Remove(building);
            RemoveKeys(OverrideTileRangeCache(), k => k.Item1 == building);
            RemoveKeys(CategoryAdjacencyCache(), k => k.Item1 == building);
            if (building.Values != null)
                building.Values.ResetCaches();
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
        // ghost. It only occupies a tile for the moment NearbyGain needs it to.
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

        private void DestroyProbe()
        {
            if (_probe != null)
                Destroy(_probe.gameObject);
            _probe = null;
            _probeTag = GameTag.None;
        }

        // Tab while placing: move the cursor to the best tile, then the 2nd and 3rd best, then round again.
        private void JumpToNextBest()
        {
            Result r = _target is Key t ? Usable(t) : null;
            Camera cam = Camera.main;
            if (r == null || r.Top.Count == 0 || cam == null || UnityEngine.InputSystem.Mouse.current == null)
                return;
            _cycleIndex = (_cycleIndex + 1) % r.Top.Count;
            Tile tile = r.Top[_cycleIndex];
            Vector3 screen = cam.WorldToScreenPoint(new Vector3(tile.X, tile.Y));
            UnityEngine.InputSystem.Mouse.current.WarpCursorPosition(new Vector2(screen.x, screen.y));
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint)
                return;
            EnsureStyles();
            DrawCardBadges();

            Result r = _target is Key t ? Usable(t) : null;
            if (r == null || r.Scores.Count == 0)
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;

            // Size text to the on-screen tile size so it follows the camera zoom.
            float tilePx = Mathf.Abs(cam.WorldToScreenPoint(Vector3.right).x - cam.WorldToScreenPoint(Vector3.zero).x);
            if (tilePx < 8f)
                return;
            _style.fontSize = Mathf.Clamp(Mathf.RoundToInt(tilePx * 0.32f * Plugin.ScoreTextScale.Value), 8, 60);

            int marks = Mathf.Clamp(Plugin.ScoreMarkBest.Value, 0, TopCount);
            for (int i = 0; i < marks && i < r.Top.Count; i++)
                DrawTileFrame(cam, r.Top[i], tilePx, i == 0 ? new Color(1f, 0.84f, 0.2f, 0.95f) : new Color(1f, 0.84f, 0.2f, 0.5f), i == 0 ? 3f : 2f);

            foreach (KeyValuePair<Tile, TileScore> entry in r.Scores)
            {
                // Lower part of the tile, below where the game draws its own per-tile preview numbers.
                Vector3 screen = cam.WorldToScreenPoint(new Vector3(entry.Key.X, entry.Key.Y - 0.28f));
                if (screen.z < 0f)
                    continue;
                Rect rect = new Rect(screen.x - tilePx / 2f, Screen.height - screen.y - tilePx / 4f, tilePx, tilePx / 2f);
                double total = entry.Value.Total;
                string text = Fmt(total);

                _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, _style);
                _style.normal.textColor = ColorFor(total, r.Best);
                GUI.Label(rect, text, _style);
            }

            if (Plugin.ScoreShowBreakdown.Value && Plugin.ScoreCountNearby.Value)
                DrawBreakdown(cam, r);
        }

        private void EnsureStyles()
        {
            if (_style != null)
                return;
            _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            _tipStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, wordWrap = false };
            _tipStyle.normal.textColor = Color.white;
            _badgeStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            _white = Texture2D.whiteTexture;
        }

        private void DrawTileFrame(Camera cam, Tile tile, float tilePx, Color color, float thickness)
        {
            Vector3 c = cam.WorldToScreenPoint(new Vector3(tile.X, tile.Y));
            float half = tilePx * 0.48f;
            var rect = new Rect(c.x - half, Screen.height - c.y - half, half * 2f, half * 2f);
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), _white);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), _white);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), _white);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), _white);
            GUI.color = old;
        }

        // "Best 225" above each card in the bottom bar; the strongest card's badge is gold.
        private void DrawCardBadges()
        {
            if (_cards.Count == 0)
                return;
            double top = double.MinValue;
            foreach ((BuildingChoiceButton _, Key key) in _cards)
                if (Usable(key) is Result r && r.Best > top)
                    top = r.Best;

            var corners = new Vector3[4];
            _badgeStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(15 * Plugin.ScoreTextScale.Value), 10, 30);
            foreach ((BuildingChoiceButton button, Key key) in _cards)
            {
                if (button == null)
                    continue;
                var rt = (RectTransform)button.transform;
                rt.GetWorldCorners(corners);
                Camera cam = CanvasCamera(rt);
                Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

                Result r = Usable(key);
                string text = r == null ? "..." : r.Best > 0 ? $"Best {Fmt(r.Best)}{(r.PerTurn ? "/turn" : "")}" : "No points";
                Vector2 size = _badgeStyle.CalcSize(new GUIContent(text));
                float x = (bottomLeft.x + topRight.x) / 2f - size.x / 2f - 4f;
                float y = Screen.height - topRight.y - size.y - 8f;
                _badgeStyle.normal.textColor = r != null && r.Best > 0 && r.Best == top && _cards.Count > 1 ? new Color(1f, 0.84f, 0.2f) : Color.white;
                GUI.Box(new Rect(x, y, size.x + 8f, size.y + 4f), text, _badgeStyle);
            }
        }

        // Next to the cursor: how the hovered tile's number splits into the building's own points and the bonus to
        // the buildings around it.
        private void DrawBreakdown(Camera cam, Result r)
        {
            Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);
            int x = Mathf.RoundToInt(world.x), y = Mathf.RoundToInt(world.y);
            Tile hovered = null;
            foreach (Tile t in r.Scores.Keys)
            {
                if (t.X == x && t.Y == y)
                {
                    hovered = t;
                    break;
                }
            }
            if (hovered == null || (r.Scores[hovered].Nearby == 0 && r.Scores[hovered].Note == null))
                return;

            TileScore s = r.Scores[hovered];
            string text = s.Nearby != 0 ? $"{Fmt(s.Own)} own  {(s.Nearby > 0 ? "+" : "")}{Fmt(s.Nearby)} nearby" : Fmt(s.Own);
            if (r.PerTurn)
                text += " (per turn)";
            if (s.Note != null)
                text += "\n" + s.Note;
            _tipStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(14 * Plugin.ScoreTextScale.Value), 10, 32);
            Vector2 size = _tipStyle.CalcSize(new GUIContent(text));
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            GUI.Box(new Rect(mouse.x + 18f, mouse.y + 18f, size.x + 8f, size.y + 4f), text, _tipStyle);
        }

        // White for low scores through green, gold for the best tile(s).
        private static Color ColorFor(double score, double best)
        {
            if (score < 0)
                return new Color(1f, 0.45f, 0.4f);
            if (best <= 0)
                return Color.white;
            if (Math.Abs(score - best) < 1e-6)
                return new Color(1f, 0.84f, 0.2f);
            float t = Mathf.Clamp01((float)(score / best));
            return Color.Lerp(new Color(0.95f, 0.95f, 0.95f), new Color(0.35f, 1f, 0.35f), t);
        }

        private void OnDestroy()
        {
            DestroyProbe();
        }
    }
}
