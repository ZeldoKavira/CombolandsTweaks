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
using Library.Grid;
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
            // Points scored straight away when it's built (its own on-build activation and neighbours that react to
            // construction); not part of the per-turn figure.
            public double Once;
            // For ranking tiles: per-turn points, plus one-off points spread over the weeks left in the milestone.
            public double Rank => Total + Once / Horizon();
        }

        private class Result
        {
            public Dictionary<Tile, TileScore> Scores;
            public double Best;     // per-turn points of the best tile
            public double BestOnce; // one-off points of the best tile
            public double BestRank;
            public bool PerTurn;
            public List<Tile> Top;
            // Cards that place several copies: the greedy best set of tiles, each copy's gain given the ones before.
            public List<Tile> Plan = new List<Tile>();
            public List<double> PlanGains = new List<double>();
            public double PlanTotal;
            public double PlanOnce;
            public int Board;
            public bool Nearby;
            public float Time;
        }

        private struct Key : IEquatable<Key>
        {
            public GameTag Tag;
            public GameTag Consumable;
            public int Count; // copies to place
            public bool Equals(Key other) => Tag == other.Tag && Consumable == other.Consumable && Count == other.Count;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => (((int)Tag * 397) ^ (int)Consumable) * 31 + Count;
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
        // For cards placed as another building (crops: the Empty form), the building it grows into.
        private Building _grown;
        private GameTag _grownTag = GameTag.None;
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

        // Multi-placement: the copies already planned sit on their tiles as hidden buildings during each slice (and
        // only then), so later copies are scored with them in place.
        private int _passStage;
        private bool _needWatchers;
        private Dictionary<Tile, TileScore> _stage0Scores;
        private readonly List<Building> _ghosts = new List<Building>();
        private readonly List<Tile> _ghostTiles = new List<Tile>();
        private readonly List<Building> _ghostPrevious = new List<Building>();
        private readonly List<double> _planGains = new List<double>();
        private double _planOnce;

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
                CancelPass();
                return;
            }

            // A pass for an old board is useless; one for a background card gives way to a target with nothing to show.
            if (_passActive && (_passBoard != _board || _passNearby != _nearby || _passPerTurn != _perTurn
                                || (_target is Key t && !_passKey.Equals(t) && Usable(t) == null)))
                CancelPass();

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
                        BuildingPlacementCount count = Traverse.Create(choice).Field<BuildingPlacementCount>("_placementCount").Value;
                        var key = new Key
                        {
                            Tag = choice.GameTag,
                            Consumable = choice.BuildingPlaceConsumableWhenCreated != null ? choice.BuildingPlaceConsumableWhenCreated.GetTag() : GameTag.None,
                            Count = Copies(count != null ? count.PlacementCount : 1),
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
                    int remaining = Traverse.Create(placing).Field<int>("_countRemainingToPlace").Value;
                    _target = new Key { Tag = placed.Tag, Consumable = placing.ConsumableToApplyWhenCreated, Count = Copies(remaining) };
                    _placing = true;
                }
            }
            if (_target == null)
                _cycleIndex = -1;
        }

        // Copies worth planning for; 1 when multi-placement planning is off.
        private static int Copies(int count) => Plugin.ScorePlanMultiple.Value ? Mathf.Clamp(count, 1, 6) : 1;

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

        private void CancelPass()
        {
            _passActive = false;
            ClearGhosts();
        }

        private void StartPass(Key key)
        {
            CancelPass();
            _passStage = 0;
            _needWatchers = false;
            _stage0Scores = null;
            _planGains.Clear();
            _planOnce = 0;
            _passTiles.Clear();
            _passScores = new Dictionary<Tile, TileScore>();
            _passIndex = 0;
            _passTime.Reset();
            if (!SerializedMonoSingleton<MapController>.HasInstance || !MonoSingleton<BuildingController>.HasInstance)
                return;
            if (GetProbe(PlacedTag(key.Tag)) == null)
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
            // Card drawbacks are applied as stat changes in ScoreAt (the game's own preview just halves for -mult).
            _passFactor = 1.0;
            _passActive = true;
            _passTime.Start();
            BuildingExtensions.ResetCaches();
            ClearSpawnCache();
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
            foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings.Concat(_ghosts))
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
                CancelPass();
                return;
            }

            BuildingController controller = MonoSingleton<BuildingController>.Instance;
            var frame = Stopwatch.StartNew();
            _passTime.Start();
            PlaceGhosts();
            try
            {
                if (_needWatchers)
                {
                    _needWatchers = false;
                    if (_passNearby)
                        CollectWatchers();
                }
                while (_passIndex < _passTiles.Count && frame.Elapsed.TotalMilliseconds < FrameBudgetMs)
                {
                    Tile tile = _passTiles[_passIndex++];
                    if (_ghostTiles.Contains(tile))
                        continue;
                    if (ScoreAt(_probe, tile, controller, out TileScore score) && (Math.Abs(score.Total) >= 0.05 || score.Once >= 0.5))
                        _passScores[tile] = score;
                }
                ForgetCaches(_probe);
            }
            finally
            {
                LiftGhosts();
                _passTime.Stop();
            }

            if (_passIndex < _passTiles.Count)
                return;

            // One stage done: note this copy's best tile; plan the next copy with this one in place.
            if (_passStage == 0)
                _stage0Scores = _passScores;
            KeyValuePair<Tile, TileScore> best = _passScores.Where(e => e.Value.Rank > 0).OrderByDescending(e => e.Value.Rank).FirstOrDefault();
            if (best.Key != null)
            {
                _planGains.Add(best.Value.Total);
                _planOnce += best.Value.Once;
                if (_passStage + 1 < _passKey.Count && AddGhost(best.Key))
                {
                    _passStage++;
                    _passIndex = 0;
                    _passScores = new Dictionary<Tile, TileScore>();
                    _needWatchers = true;
                    return;
                }
                _ghostTiles.Add(best.Key); // the last copy's tile, for the plan (no ghost needed)
            }

            _passActive = false;
            Dictionary<Tile, TileScore> scores = _stage0Scores ?? _passScores;
            List<Tile> top = scores.Where(e => e.Value.Rank > 0).OrderByDescending(e => e.Value.Rank).Take(TopCount).Select(e => e.Key).ToList();
            bool firstForBoard = Usable(_passKey) == null;
            var result = new Result
            {
                Scores = scores,
                Best = top.Count > 0 ? scores[top[0]].Total : 0,
                BestOnce = top.Count > 0 ? scores[top[0]].Once : 0,
                BestRank = top.Count > 0 ? scores[top[0]].Rank : 0,
                Top = top,
                Board = _passBoard,
                Nearby = _passNearby,
                PerTurn = _passPerTurn,
                Time = Time.unscaledTime,
            };
            if (_passKey.Count > 1)
            {
                result.Plan.AddRange(_ghostTiles);
                result.PlanGains.AddRange(_planGains);
                result.PlanTotal = _planGains.Sum();
                result.PlanOnce = _planOnce;
            }
            _results[_passKey] = result;
            ClearGhosts();
            if (_results.Count > 32)
                PruneResults();
            if (firstForBoard)
                Plugin.Log.LogInfo($"Placement scores for {_passKey.Tag}: {scores.Count} tiles, best {Fmt(result.Best)}{(_passPerTurn ? "/turn" : "")}"
                                   + (_passKey.Count > 1 ? $", {result.Plan.Count} placements {Fmt(result.PlanTotal)}" : "")
                                   + $" ({_passTime.Elapsed.TotalMilliseconds:F1} ms{(_passNearby ? ", with nearby" : "")})");
        }

        private bool AddGhost(Tile tile)
        {
            Building ghost = CreateCopy(PlacedTag(_passKey.Tag));
            if (ghost == null)
                return false;
            ghost.name = "CombolandsTweaks planned copy";
            ghost.transform.position = new Vector3(tile.X, tile.Y);
            _ghosts.Add(ghost);
            _ghostTiles.Add(tile);
            _ghostPrevious.Add(null);
            return true;
        }

        private void PlaceGhosts()
        {
            if (_ghosts.Count == 0)
                return;
            for (int i = 0; i < _ghosts.Count; i++)
            {
                _ghostPrevious[i] = _ghostTiles[i].Building;
                _ghostTiles[i].AssignBuilding(_ghosts[i]);
            }
            BuildingExtensions.ResetCaches();
        }

        private void LiftGhosts()
        {
            if (_ghosts.Count == 0)
                return;
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                if (_ghostPrevious[i] != null)
                    _ghostTiles[i].AssignBuilding(_ghostPrevious[i]);
                else
                    _ghostTiles[i].RemoveCurrentBuilding();
                _ghostPrevious[i] = null;
            }
            BuildingExtensions.ResetCaches();
        }

        private void ClearGhosts()
        {
            foreach (Building g in _ghosts)
                if (g != null)
                    Destroy(g.gameObject);
            _ghosts.Clear();
            _ghostTiles.Clear();
            _ghostPrevious.Clear();
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

                // Obelisk on Obelisk: nothing is built; the existing one stores +1 and gives its neighbours more mult.
                if (reason == CantPlaceBuildingReason.Obelisk && tile.Building != null)
                {
                    float step = tile.Building.Behaviour.GetMultParam(tile.Building);
                    foreach (Tile t in tile.Building.GetAdjacentTiles())
                        if (t != null && t != tile && !t.IsEmpty && t.Building != null && t.Building != tile.Building)
                            score.Own += MultGain(t.Building, step);
                    score.Note = $"stacks on the Obelisk: +{step:0.##} mult to its neighbours";
                    return true;
                }

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
                if (!probe.Tag.CanGetMult())
                    bonusMult = 0f;
                // The card's drawback (PlacingBuilding.ApplyConsumableWhenCreated): a local stat change on the new
                // building, applied the same way here.
                CardPenalty(_passKey.Consumable, out float penaltyMult, out int penaltyCooldown, out int penaltyRange);
                if (!probe.Tag.CanGetMult())
                    penaltyMult = 0f;
                float mult = bonusMult + penaltyMult;
                LocalMult(probe.Values) += mult;
                LocalCooldown(probe.Values) += penaltyCooldown;
                LocalRange(probe.Values) += penaltyRange;
                ForgetCaches(probe);
                try
                {
                    score.Own = OwnValue(probe);
                    score.Once = OnceValue(probe);
                    if (probe.Tag != _passKey.Tag && _passPerTurn)
                        GrowthValue(probe, tile, mult, penaltyCooldown, penaltyRange, ref score);
                }
                finally
                {
                    LocalMult(probe.Values) -= mult;
                    LocalCooldown(probe.Values) -= penaltyCooldown;
                    LocalRange(probe.Values) -= penaltyRange;
                    ForgetCaches(probe);
                }
                if (penaltyMult != 0f || penaltyCooldown != 0 || penaltyRange != 0)
                    score.Note = Join(score.Note, "card drawback: " + string.Join(", ", new[]
                    {
                        penaltyMult != 0f ? $"{penaltyMult:0.##} mult" : null,
                        penaltyCooldown != 0 ? $"+{penaltyCooldown} cooldown" : null,
                        penaltyRange != 0 ? $"{penaltyRange} range" : null,
                    }.Where(x => x != null)));
                if (_passKey.Consumable == GameTag.BsNegBlackPaint)
                    score.Note = Join(score.Note, $"black paint: removed after {ConsumableBehaviour.GetPaintCooldownParam(GameTag.BsNegBlackPaint)} turns");
                if (_passPerTurn && OwnRate(probe) <= 0 && score.Once < 0.5)
                {
                    long per = PreviewTotal(probe, _passFactor);
                    if (per != 0)
                        score.Note = Join(score.Note, $"no countdown: {per} per activation when something triggers it");
                }
                if (_passPerTurn && _passNearby)
                    score.Once += ConstructionReactions(probe, tile, ref score.Note);
                if (score.Once >= 0.5)
                    score.Note = Join(score.Note, $"+{Fmt(score.Once)} now when built");

                // A building that triggers others (Marketplace -> Stalls, Sawmill, ...) earns their points when it
                // activates; the game's preview only shows what it scores itself.
                if (IsGranter(probe))
                {
                    int targets = GrantTargets(probe).Count;
                    double perTurn = GrantPerTurn(probe);
                    string how = "";
                    if (probe.Tag == GameTag.Composter)
                    {
                        int manure = probe.GetAdjacentTiles().Count(t => t != null && !t.IsEmpty && t.Building != null && t.Building.Tag == GameTag.Manure);
                        double fromSheep = OwnRate(probe) - manure / Horizon();
                        how = (manure > 0 ? $", clears {manure} Manure next turn (+{Fmt(manure * perTurn)}/turn)" : "")
                              + (fromSheep > 0.005 ? $", ~{fromSheep:0.##} more activations/turn from Sheep Farm Manure" : "");
                    }
                    if (probe.Tag == GameTag.Banner)
                        score.Note = Join(score.Note, $"when you remove it: +{probe.Behaviour.GetMultParam(probe):0.##} mult to {targets} adjacent building{(targets == 1 ? "" : "s")} (+{Fmt(perTurn)}/turn for good)");
                    else
                        score.Note = Join(score.Note, $"each activation: +{probe.Behaviour.GetMultParam(probe):0.##} mult to {targets} building{(targets == 1 ? "" : "s")} (+{Fmt(perTurn)}/turn, {Horizon():0} weeks left){how}");
                }
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
                    // A Campfire's +1 mult to its neighbours lasts only until it burns out.
                    if (probe.Tag == GameTag.Campfire && probe.GamePieceWithCount != null && _passPerTurn)
                    {
                        double lit = Math.Min(probe.Cooldown + 1, Horizon());
                        score.Nearby *= lit / Horizon();
                        score.Note = Join(score.Note, $"burns out after {probe.Cooldown} turns");
                    }
                    // Sewer removes every adjacent non-Infrastructure building when built.
                    if (probe.Tag == GameTag.Sewer)
                    {
                        int gone = 0;
                        foreach (Tile t in probe.GetAdjacentTiles())
                            if (t != null && t != tile && !t.IsEmpty && t.Building != null && !t.Building.Categories.ContainsCategory(GamePieceCategory.Infrastructure)
                                && _baseline.TryGetValue(t.Building, out double v))
                            {
                                score.Nearby -= v;
                                gone++;
                            }
                        if (gone > 0)
                            score.Note = Join(score.Note, $"destroys {gone} adjacent building{(gone == 1 ? "" : "s")}");
                    }
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

        internal static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalCooldown =
            AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localCooldownChanges");
        internal static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalRange =
            AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localRangeChanges");

        // A card can come with a drawback applied when built: BsNegMult (-0.5 mult), BsNegCooldown (+1 cooldown),
        // BsNegRange (-1 range), each a local stat change (ConsumableBehaviour.GetBuildingSupplyParam).
        private static void CardPenalty(GameTag consumable, out float mult, out int cooldown, out int range)
        {
            mult = 0f;
            cooldown = 0;
            range = 0;
            switch (consumable)
            {
                case GameTag.BsNegMult:
                    mult = ConsumableBehaviour.GetBuildingSupplyParam(consumable);
                    break;
                case GameTag.BsNegCooldown:
                    cooldown = Mathf.RoundToInt(ConsumableBehaviour.GetBuildingSupplyParam(consumable));
                    break;
                case GameTag.BsNegRange:
                    range = Mathf.RoundToInt(ConsumableBehaviour.GetBuildingSupplyParam(consumable));
                    break;
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
        internal static double Delivered(Building t, int depth, bool grants = true)
        {
            if (!UpgradePreview.Triggerers.ContainsKey(t.Tag))
                return 0;
            List<Building> targets = UpgradePreview.Candidates(t);
            double share = UpgradePreview.Share(t, targets);
            double points = 0;
            foreach (Building x in targets)
                points += share * ActivationValue(x, depth, grants);
            return points;
        }

        // Points one activation of a building is worth: its own preview times activations, plus what it triggers, plus
        // (for buildings that hand out permanent mult) what that mult earns by the end of the milestone.
        internal static double ActivationValue(Building x, int depth, bool grants = true)
        {
            double value = PreviewTotal(x, 1.0) * Math.Max(1, x.Behaviour.GetActivationCount(x));
            if (depth > 0)
                value += Delivered(x, depth - 1, grants);
            if (grants)
                value += GrantValue(x);
            return value;
        }

        // Buildings that permanently add mult to others each time they activate: Composter (+0.1 to Farms in range),
        // Field Hospital (+0.2 to Healing/Training in range), Cotton Field (+0.2 to every adjacent building), and Banner
        // (+0.3 to every adjacent building, once, when it is removed).
        private static readonly Dictionary<GameTag, (UpgradePreview.Area area, bool useCategories)> Granters =
            new Dictionary<GameTag, (UpgradePreview.Area, bool)>
            {
                { GameTag.Composter, (UpgradePreview.Area.Range, true) },
                { GameTag.FieldHospital, (UpgradePreview.Area.Range, true) },
                { GameTag.CottonField, (UpgradePreview.Area.Adjacent, false) },
                { GameTag.Banner, (UpgradePreview.Area.Adjacent, false) },
            };

        internal static bool IsGranter(Building b) => Granters.ContainsKey(b.Tag);

        // Weeks left in the current milestone: how long a permanent mult grant made now gets to pay out here.
        internal static double Horizon() =>
            MonoSingleton<GameController>.HasInstance ? Math.Max(1, MonoSingleton<GameController>.Instance.WeeksRemaining) : 1;

        internal static List<Building> GrantTargets(Building g)
        {
            var list = new List<Building>();
            if (!Granters.TryGetValue(g.Tag, out var cfg))
                return list;
            HashSet<Tile> tiles = cfg.area == UpgradePreview.Area.Range ? g.GetTilesInRange() : g.GetAdjacentTiles();
            GamePieceCategory[] cats = cfg.useCategories ? g.Behaviour.GetTargetCategories(g) : null;
            foreach (Tile t in tiles)
            {
                if (t == null || t.IsEmpty || t.Building == null || t.Building == g || t.Building.Values == null || !t.Building.Tag.CanGetMult())
                    continue;
                if (cats != null && cats.Length > 0 && !t.Building.Categories.ContainsAnyOf(cats))
                    continue;
                if (!list.Contains(t.Building))
                    list.Add(t.Building);
            }
            return list;
        }

        // Extra points per turn one activation's mult grant adds to its targets, for good.
        internal static double GrantPerTurn(Building g)
        {
            if (!Granters.ContainsKey(g.Tag))
                return 0;
            float mult = g.Behaviour.GetMultParam(g);
            double gain = 0;
            foreach (Building x in GrantTargets(g))
            {
                double before = TurnValue(x, 1.0, grants: false);
                LocalMult(x.Values) += mult;
                ForgetCaches(x);
                try
                {
                    gain += TurnValue(x, 1.0, grants: false) - before;
                }
                finally
                {
                    LocalMult(x.Values) -= mult;
                    ForgetCaches(x);
                }
            }
            return gain;
        }

        internal static double GrantValue(Building g) => Granters.ContainsKey(g.Tag) ? GrantPerTurn(g) * Horizon() : 0;

        // Composter activations per turn: Manure that lands next to it is cleared (and so activates it) at end of turn.
        // Sheep Farms drop Manure on a random empty grass tile next to them each time they activate; Manure already
        // adjacent counts once, spread over the milestone.
        internal static double ManureRate(Building composter)
        {
            HashSet<Tile> adjacent = composter.GetAdjacentTiles();
            double rate = 0;
            int manure = 0;
            var sheep = new HashSet<Building>();
            foreach (Tile a in adjacent)
            {
                if (a == null)
                    continue;
                if (!a.IsEmpty && a.Building != null && a.Building.Tag == GameTag.Manure)
                    manure++;
                foreach (Tile n in a.Get8NeighboursEnumerable())
                    if (n != null && !n.IsEmpty && n.Building != null && n.Building.Tag == GameTag.SheepFarm)
                        sheep.Add(n.Building);
            }
            foreach (Building s in sheep)
            {
                int spots = 0, near = 0;
                foreach (Tile t in s.GetAdjacentTiles())
                {
                    if (t == null || t.Type != TileType.Grass || !(t.IsEmpty || (t.Building != null && t.Building.Tag == GameTag.Plateau)))
                        continue;
                    spots++;
                    if (adjacent.Contains(t))
                        near++;
                }
                if (spots > 0 && near > 0)
                    rate += TriggerRate(s) * near / spots;
            }
            return rate + manure / Horizon();
        }

        // Activations per turn a building gets from neighbours with a countdown that trigger it.
        internal static double TriggerRate(Building x)
        {
            if (!MonoSingleton<BuildingController>.HasInstance)
                return 0;
            double rate = 0;
            foreach (Building t in MonoSingleton<BuildingController>.Instance.Buildings)
            {
                if (t == null || t == x || !UpgradePreview.Triggerers.ContainsKey(t.Tag))
                    continue;
                double own = OwnRate(t);
                if (own <= 0)
                    continue;
                List<Building> targets = UpgradePreview.Candidates(t);
                if (targets.Contains(x))
                    rate += own * UpgradePreview.Share(t, targets);
            }
            return rate;
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
            if (b.Tag == GameTag.Composter)
                return ManureRate(b);
            // Picks ripe bushes every end of turn, no countdown.
            if (b.Tag == GameTag.BerryPicker)
                return 1.0;
            double events = EventRate(b);
            if (events >= 0)
                return events;
            // A countdown alone isn't enough: the building must act on end of turn (ripe crops keep a cooldown param
            // but only score when they grow or are triggered).
            if (b.Behaviour?.ValidTriggers == null || !b.Behaviour.ValidTriggers.Contains(TriggerType.OnEndOfTurn))
                return 0;
            if (b.GamePieceWithCount == null || b.Cooldown <= 0)
                return 0;
            if (IsIrrigated(b))
                return 1.0;
            return 1.0 / b.Cooldown;
        }

        // Buildings that activate on events rather than a countdown: activations per turn from the buildings around
        // them that cause those events. -1 when the building isn't one of these.
        private static readonly HashSet<GameTag> MoneyMakers = new HashSet<GameTag>
        {
            GameTag.Bank, GameTag.Clipper, GameTag.GoldPan, GameTag.TreasureChest, GameTag.Workshop, GameTag.JoustingArena,
        };
        private static readonly HashSet<GameTag> RerollMakers = new HashSet<GameTag> { GameTag.Grove, GameTag.HerbGarden };

        private static double EventRate(Building b)
        {
            switch (b.Tag)
            {
                case GameTag.Brewery: // a building in range 2 transforming, half the time
                {
                    double transforms = 0;
                    foreach (Building x in InRange(b))
                        transforms += TransformRate(x);
                    return Math.Min(1.0, b.Behaviour.GetActivationChance(b)) * transforms;
                }
                case GameTag.Circus:   // money earned in range 4
                case GameTag.Treasury: // money earned in range 7
                    return InRange(b).Where(x => MoneyMakers.Contains(x.Tag)).Sum(x => OwnRateSafe(x));
                case GameTag.Shaman: // rerolls gained in range 4
                    return InRange(b).Where(x => RerollMakers.Contains(x.Tag)).Sum(x => OwnRateSafe(x));
                case GameTag.PostOffice: // fires when the building below it activates
                {
                    Tile below = b.Tile?.DownNeighbour();
                    return below != null && !below.IsEmpty && below.Building != null && below.Building != b ? OwnRateSafe(below.Building) : 0;
                }
                case GameTag.Windmill: // an adjacent crop growing (each Empty crop grows once)
                {
                    int growing = 0;
                    foreach (Tile t in b.GetAdjacentTiles())
                        if (t != null && !t.IsEmpty && t.Building != null && EmptyCrops.Contains(t.Building.Tag))
                            growing++;
                    return growing / Horizon();
                }
            }
            return -1;
        }

        // Guards the event-rate lookups against loops (a Post Office below a Post Office ...).
        private static int _rateDepth;

        private static double OwnRateSafe(Building b)
        {
            if (_rateDepth > 4)
                return 0;
            _rateDepth++;
            try
            {
                return OwnRate(b);
            }
            finally
            {
                _rateDepth--;
            }
        }

        // How often a building transforms: Berry Bushes are picked and regrow, Empty crops grow once, houses step up a
        // tier, a lit Campfire burns out.
        private static double TransformRate(Building x)
        {
            if (x.Tag == GameTag.BerryBush || x.Tag == GameTag.BerryBushEmpty)
                return 2.0 / BushCycle(x);
            if (EmptyCrops.Contains(x.Tag))
                return 1.0 / Horizon();
            if (x.Behaviour is Entities.BuildingBehaviours._House)
                return HouseSteps(x) / Horizon();
            if (x.Tag == GameTag.Campfire && x.GamePieceWithCount != null)
                return 1.0 / (x.Cooldown + 1);
            return 0;
        }

        private static IEnumerable<Building> InRange(Building b)
        {
            var seen = new HashSet<Building>();
            foreach (Tile t in b.GetTilesInRange())
                if (t != null && !t.IsEmpty && t.Building != null && t.Building != b && seen.Add(t.Building))
                    yield return t.Building;
        }

        // ---- Houses: score their tier and step up while the next tier's neighbour category is in reach ----------------
        private static readonly AccessTools.FieldRef<Entities.BuildingBehaviours._House, GamePieceCategory[]> HouseNeeds =
            AccessTools.FieldRefAccess<Entities.BuildingBehaviours._House, GamePieceCategory[]>("_transformInRangeOf");
        private static readonly AccessTools.FieldRef<Entities.BuildingBehaviours._House, GameTag> HouseNext =
            AccessTools.FieldRefAccess<Entities.BuildingBehaviours._House, GameTag>("_transformInto");

        // Points the house earns climbing tiers within the weeks left, and how many steps that is.
        private static double HouseChain(Building house, out int steps)
        {
            steps = 0;
            double total = 0;
            var tier = house.Behaviour as Entities.BuildingBehaviours._House;
            int horizon = (int)Horizon();
            var seen = new HashSet<Entities.BuildingBehaviours._House>();
            while (tier != null && steps < horizon && seen.Add(tier))
            {
                GamePieceCategory[] needs = HouseNeeds(tier);
                if (needs == null || needs.Length == 0)
                    break;
                bool met = needs.Any(c => house.IsBuildingInRangeOfAny(c) || house.GetCountOfBuildingsOfCategoryAdjacent(c) > 0);
                if (!met)
                    break;
                total += tier.GetBehaviourScoreParam(house) * house.Multiplier;
                steps++;
                tier = Entities.BuildingBehaviours.BuildingBehaviours.Instance.GetBehaviourFor(HouseNext(tier)) as Entities.BuildingBehaviours._House;
            }
            return total;
        }

        private static int HouseSteps(Building house)
        {
            HouseChain(house, out int steps);
            return steps;
        }

        // ---- Spawners: each new resource tile feeds the buildings around it, and they pile up --------------------------
        private static readonly Dictionary<GameTag, (GameTag resource, bool adjacent)> Spawners = new Dictionary<GameTag, (GameTag, bool)>
        {
            { GameTag.Forester, (GameTag.Trees, false) },
            { GameTag.FishFarm, (GameTag.Fish, false) },
            { GameTag.Quarry, (GameTag.Rocks, false) },
            { GameTag.Mine, (GameTag.Ore, true) },
            { GameTag.Lumberyard, (GameTag.Lumber, true) },
            { GameTag.Apothecary, (GameTag.Herbs, true) },
        };

        private static readonly Dictionary<GameTag, Building> SpawnCopies = new Dictionary<GameTag, Building>();
        private static readonly Dictionary<Building, double> SpawnCache = new Dictionary<Building, double>();
        private static bool _inSpawn;

        internal static void ClearSpawnCache() => SpawnCache.Clear();

        internal static void DestroySpawnCopies()
        {
            foreach (Building c in SpawnCopies.Values)
                if (c != null)
                    Destroy(c.gameObject);
            SpawnCopies.Clear();
            SpawnCache.Clear();
        }

        // Average extra points per turn over the milestone from what the spawner produces: each new resource adds the
        // gain it gives the buildings around its tile (sampled), and resources accumulate until the free tiles run out.
        private static double SpawnValue(Building spawner)
        {
            if (_inSpawn || !Spawners.TryGetValue(spawner.Tag, out var cfg) || !MonoSingleton<BuildingController>.HasInstance)
                return 0;
            if (SpawnCache.TryGetValue(spawner, out double cached))
                return cached;
            double value = 0;
            _inSpawn = true;
            try
            {
                double rate = OwnRate(spawner) * Math.Max(1, spawner.Behaviour.GetActivationCount(spawner));
                Building res = SpawnCopy(cfg.resource);
                if (rate > 0 && res != null)
                {
                    HashSet<TileType> types = res.Behaviour.GetTileTypesCanBePlacedOn(cfg.resource);
                    var free = (cfg.adjacent ? spawner.GetAdjacentTiles() : spawner.GetTilesInRange())
                        .Where(t => t != null && t != spawner.Tile && t.IsEmpty && (types == null || types.Contains(t.Type))).ToList();
                    if (free.Count > 0)
                    {
                        int samples = Math.Min(3, free.Count);
                        double gain = 0;
                        for (int i = 0; i < samples; i++)
                            gain += ResourceGain(res, free[i * free.Count / samples]);
                        gain /= samples;
                        double spawns = Math.Min(rate * Horizon(), free.Count);
                        value = gain * spawns / 2.0;
                    }
                }
            }
            catch (Exception)
            {
                value = 0;
            }
            finally
            {
                _inSpawn = false;
            }
            SpawnCache[spawner] = value;
            return value;
        }

        private static double ResourceGain(Building res, Tile t)
        {
            var seeing = new List<Building>();
            foreach (Building w in MonoSingleton<BuildingController>.Instance.Buildings)
                if (w != null && w.Values != null && (w.GetTilesInRange().Contains(t) || w.GetAdjacentTiles().Contains(t)))
                    seeing.Add(w);
            double before = seeing.Sum(w => TurnValue(w));
            res.transform.position = new Vector3(t.X, t.Y);
            t.AssignBuilding(res);
            try
            {
                foreach (Building w in seeing)
                    ForgetCaches(w);
                ForgetCaches(res);
                return seeing.Sum(w => TurnValue(w)) - before;
            }
            finally
            {
                t.RemoveCurrentBuilding();
                foreach (Building w in seeing)
                    ForgetCaches(w);
            }
        }

        private static Building SpawnCopy(GameTag tag)
        {
            if (SpawnCopies.TryGetValue(tag, out Building c) && c != null)
                return c;
            c = CreateCopy(tag);
            if (c != null)
                SpawnCopies[tag] = c;
            return c;
        }

        // Warehouse: stores what buildings in range score and pays it all out (times its own mult) when triggered.
        private static double WarehouseValue(Building w)
        {
            if (TriggerRate(w) <= 0)
                return 0;
            double stored = 0;
            foreach (Building x in InRange(w))
                if (x.Tag != GameTag.Warehouse)
                    stored += TurnValue(x);
            return stored * w.Multiplier;
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
        internal static double TurnValue(Building b, double factor = 1.0, bool grants = true)
        {
            // A Banner never activates; its worth is the permanent mult its neighbours get once it's removed.
            if (b.Tag == GameTag.Banner)
                return grants ? GrantPerTurn(b) : 0;
            // Berry Picker: every bush in range (ripe or regrowing) is picked once per regrow cycle.
            if (b.Tag == GameTag.BerryPicker)
            {
                double perPick = b.Behaviour.GetScoreForTag(b, GameTag.BerryBush) * b.Multiplier * factor;
                double total = 0;
                var seen = new HashSet<Building>();
                foreach (Tile t in b.GetTilesInRange())
                    if (t != null && !t.IsEmpty && t.Building != null && (t.Building.Tag == GameTag.BerryBush || t.Building.Tag == GameTag.BerryBushEmpty) && seen.Add(t.Building))
                        total += perPick / BushCycle(t.Building);
                return total;
            }
            // Hideout: -100 (Fawkes Mask: +100) each time an adjacent building scores, at most 10 times a week.
            if (b.Tag == GameTag.Hideout)
            {
                double scores = 0;
                foreach (Tile t in b.GetAdjacentTiles())
                    if (t != null && !t.IsEmpty && t.Building != null && t.Building != b && t.Building.Tag != GameTag.Hideout && PreviewTotal(t.Building, 1.0) > 0)
                        scores += OwnRate(t.Building);
                return b.Behaviour.GetScoreParam(b) * b.Multiplier * factor * Math.Min(10, scores);
            }
            // Houses climb tiers (one-off points, spread over the milestone).
            if (b.Behaviour is Entities.BuildingBehaviours._House && b.Tag != GameTag.HouseResidence)
                return HouseChain(b, out _) * factor / Horizon();
            if (b.Tag == GameTag.Warehouse)
                return WarehouseValue(b) * factor;
            // Woodcutter and Nitrary run out of trees / food: at most what's in range, over the milestone.
            if (b.Tag == GameTag.Woodcutter || b.Tag == GameTag.Nitrary)
            {
                double eatRate = OwnRate(b) * Math.Max(1, b.Behaviour.GetActivationCount(b));
                int food = b.Tag == GameTag.Woodcutter
                    ? CountInRange(b, x => x.Tag == GameTag.Trees)
                    : CountInRange(b, x => x.Categories.ContainsCategory(GamePieceCategory.Nature) || x.Categories.ContainsCategory(GamePieceCategory.Resource));
                double each = (b.Tag == GameTag.Woodcutter ? b.Behaviour.GetScoreForTag(b, GameTag.Trees) : b.Behaviour.GetScoreParam(b)) * b.Multiplier * factor;
                return each * Math.Min(eatRate, food / Horizon()) + SpawnValue(b);
            }
            // Effigy Pyre fires once (triggering every neighbour) and burns itself down: spread over the milestone.
            if (b.Tag == GameTag.EffigyPyre)
            {
                if (b.GamePieceWithCount == null || Horizon() < b.Cooldown)
                    return 0;
                return Delivered(b, 1, grants) / Horizon();
            }
            double rate = OwnRate(b);
            if (rate <= 0)
                return 0;
            return (PreviewTotal(b, factor) * Math.Max(1, b.Behaviour.GetActivationCount(b)) + Delivered(b, 1, grants)
                    + (grants ? GrantValue(b) : 0)) * rate + SpawnValue(b);
        }

        // A crop placed as its Empty form grows into the card's building after its cooldown (1 next to an Irrigator):
        // that scores once on growing (OnTransformedInto) and, if the grown building acts every few turns, per turn.
        private void GrowthValue(Building empty, Tile tile, float mult, int cooldown, int range, ref TileScore score)
        {
            Building grown = GetGrown(_passKey.Tag);
            if (grown == null)
                return;
            int delay = empty.GamePieceWithCount != null && empty.Cooldown > 0 ? (IsIrrigated(empty) ? 1 : empty.Cooldown) : 1;
            grown.transform.position = new Vector3(tile.X, tile.Y);
            LocalMult(grown.Values) += mult;
            LocalCooldown(grown.Values) += cooldown;
            LocalRange(grown.Values) += range;
            ForgetCaches(grown);
            try
            {
                double once = 0;
                if (grown.Behaviour?.ValidTriggers != null && grown.Behaviour.ValidTriggers.Contains(TriggerType.OnTransformedInto) && delay <= Horizon())
                    once = ActivationValue(grown, 1);
                score.Once += once;
                score.Own += TurnValue(grown);
                score.Note = Join(score.Note, $"grows into {NameOf(grown.Tag)} in {delay} turn{(delay == 1 ? "" : "s")}" + (once >= 0.5 ? $" (+{Fmt(once)})" : ""));
            }
            finally
            {
                LocalMult(grown.Values) -= mult;
                LocalCooldown(grown.Values) -= cooldown;
                LocalRange(grown.Values) -= range;
                ForgetCaches(grown);
            }
        }

        // Hunting Bow repeats on-build triggers (TriggerController.AddOnBuiltTrigger).
        private static int BuildTriggers() =>
            1 + (MonoSingleton<ItemHeirloomController>.HasInstance && MonoSingleton<ItemHeirloomController>.Instance.IsItemWithTagEquipped(GameTag.HuntingBow) ? 1 : 0);

        // List OnBuild as a valid trigger but their Trigger() never acts on it.
        private static readonly HashSet<GameTag> NoBuildActivation = new HashSet<GameTag> { GameTag.Vault, GameTag.Windmill };

        // Per-turn mode: what the building scores the moment it's built, if it activates on build.
        private double OnceValue(Building probe)
        {
            if (!_passPerTurn || probe.Behaviour?.ValidTriggers == null || !probe.Behaviour.ValidTriggers.Contains(TriggerType.OnBuild)
                || NoBuildActivation.Contains(probe.Tag))
                return 0;
            double regrow = 0;
            if (probe.Tag == GameTag.Granary)
                foreach (Building crop in InRange(probe))
                {
                    BuildingData d = ScriptableObjectSingleton<GamePieceDataHolder>.Instance.GetBuildingDataFor(crop.Tag);
                    if (crop.Categories.ContainsCategory(GamePieceCategory.Crop) && d != null && d.AlternatePlacedType != GameTag.None
                        && crop.Behaviour.ValidTriggers != null && crop.Behaviour.ValidTriggers.Contains(TriggerType.OnTransformedInto))
                        regrow += ActivationValue(crop, 1);
                }
            return (PreviewTotal(probe, _passFactor) * Math.Max(1, probe.Behaviour.GetActivationCount(probe)) + Delivered(probe, 1, grants: false) + regrow)
                   * BuildTriggers();
        }

        // Existing buildings that activate when something is built near them (BuildingConstructionOccurred): where
        // they look (0 = anywhere, 1 = range, 2 = adjacent) and which of their target categories the new building
        // must be in (0 = any).
        private static readonly (GameTag tag, int area, int category)[] Reactors =
        {
            (GameTag.Depot, 0, 1),
            (GameTag.Harvester, 1, 0),
            (GameTag.WitchHut, 1, 0),
            (GameTag.ToolShed, 1, 3),
            (GameTag.Lantern, 2, 1),
            (GameTag.WayStation, 2, 1),
        };

        private static double ConstructionReactions(Building probe, Tile tile, ref string note)
        {
            if (!MonoSingleton<BuildingController>.HasInstance)
                return 0;
            double total = 0;
            foreach (Building r in MonoSingleton<BuildingController>.Instance.Buildings)
            {
                if (r == null || r.Behaviour == null)
                    continue;
                foreach ((GameTag tag, int area, int category) in Reactors)
                {
                    if (r.Tag != tag)
                        continue;
                    if (area == 1 && !r.GetTilesInRange().Contains(tile))
                        break;
                    if (area == 2 && !r.GetAdjacentTiles().Contains(tile))
                        break;
                    if (category > 0 && !probe.Categories.ContainsCategory(r.Behaviour.GetTargetCategoryByNumber(r, category)))
                        break;
                    double v = ActivationValue(r, 1, grants: false) * BuildTriggers();
                    // Way Station doesn't activate on construction; its countdown just comes one turn closer.
                    if (r.Tag == GameTag.WayStation)
                        v /= Math.Max(1, r.Cooldown);
                    if (v >= 0.5)
                    {
                        total += v;
                        note = Join(note, $"{NameOf(r.Tag)} reacts (+{Fmt(v)} now)");
                    }
                    break;
                }
            }
            return total;
        }

        // An existing building's worth in this pass's units.
        private double Value(Building b) => _passPerTurn ? TurnValue(b) : PreviewTotal(b, 1.0);

        private double OwnValue(Building probe) => _passPerTurn ? TurnValue(probe, _passFactor) : PreviewTotal(probe, _passFactor);

        // Whole numbers from 10 up, one decimal below.
        internal static string Fmt(double v) => Math.Abs(v) >= 10 ? Math.Round(v).ToString("0") : v.ToString("0.#");

        // The game's green placement number: the building's preview points times its multiplier.
        internal static long PreviewTotal(Building b, double factor)
        {
            if (CustomPoints(b, out double custom))
                return (long)Math.Round(custom * factor);
            Dictionary<Tile, long> preview = b.Behaviour?.GetScorePreview(b);
            if (preview == null)
                return 0;
            long sum = 0;
            foreach (long v in preview.Values)
                sum += v;
            return (long)Math.Round(sum * b.Multiplier * factor);
        }

        // Crops are placed as their Empty form, which scores nothing itself and grows into the ripe crop.
        private static readonly HashSet<GameTag> EmptyCrops = new HashSet<GameTag>
        {
            GameTag.AppleOrchardEmpty, GameTag.BerryBushEmpty, GameTag.CottonFieldEmpty, GameTag.SunflowerFarmEmpty,
            GameTag.VineyardEmpty, GameTag.WheatFieldEmpty,
        };

        // Points per activation (per pick, for buildings that repeat activation-count times) for buildings whose real
        // scoring isn't what GetScorePreview shows. Callers multiply by the activation count.
        private static bool CustomPoints(Building b, out double points)
        {
            points = 0;
            if (b.Behaviour == null)
                return false;
            double mult = b.Multiplier;
            int picks = Math.Max(1, b.Behaviour.GetActivationCount(b));
            switch (b.Tag)
            {
                case GameTag.Jeweller: // preview shows Metalworking points it never scores
                case GameTag.Hideout:  // scores when neighbours score; see TurnValue
                    return true;
                case GameTag.Smelter: // no preview mode; scores its flat score param
                    points = b.Behaviour.GetScoreParam(b) * mult;
                    return true;
                case GameTag.Woodcutter: // cuts activation-count Trees in range, 80 each
                {
                    int trees = CountInRange(b, x => x.Tag == GameTag.Trees);
                    points = b.Behaviour.GetScoreForTag(b, GameTag.Trees) * mult * Math.Min(picks, trees) / (double)picks;
                    return true;
                }
                case GameTag.Nitrary: // eats activation-count Nature/Resource buildings in range, 50 each
                {
                    int food = CountInRange(b, x => x.Categories.ContainsCategory(GamePieceCategory.Nature) || x.Categories.ContainsCategory(GamePieceCategory.Resource));
                    points = b.Behaviour.GetScoreParam(b) * mult * Math.Min(picks, food) / (double)picks;
                    return true;
                }
                case GameTag.PirateShip: // -100 (Fawkes Mask: +100) per land tile it can drop a chest on
                {
                    bool spot = false;
                    foreach (Tile t in b.GetTilesInRange())
                        if (t != null && t != b.Tile && !t.Type.IsSeaType() && (t.IsEmpty || (t.Building != null && t.Building.Data.CanBeBuiltOver)))
                        {
                            spot = true;
                            break;
                        }
                    points = spot ? b.Behaviour.GetScoreParam(b) * mult : 0;
                    return true;
                }
                case GameTag.BanditCamp: // -150 (Fawkes Mask: +150) for every building it triggers
                    points = UpgradePreview.Candidates(b).Count > 0 ? b.Behaviour.GetScoreParam(b) * mult : 0;
                    return true;
                case GameTag.BerryPicker: // only ripe bushes are picked
                {
                    int ripe = CountInRange(b, x => x.Tag == GameTag.BerryBush);
                    points = b.Behaviour.GetScoreForTag(b, GameTag.BerryBush) * mult * ripe;
                    return true;
                }
                case GameTag.Stockpile: // the preview counts Plateaus, Activate doesn't
                {
                    Dictionary<Tile, long> preview = b.Behaviour.GetScorePreview(b);
                    long sum = preview?.Values.Sum() ?? 0;
                    int plateaus = CountInRange(b, x => x.Tag == GameTag.Plateau);
                    points = (sum - b.Behaviour.GetScoreForTag(b, GameTag.Plateau) * plateaus) * mult;
                    return true;
                }
            }
            if (EmptyCrops.Contains(b.Tag))
                return true;
            return false;
        }

        private static int CountInRange(Building b, Func<Building, bool> match)
        {
            var seen = new HashSet<Building>();
            foreach (Tile t in b.GetTilesInRange())
                if (t != null && !t.IsEmpty && t.Building != null && t.Building != b && match(t.Building))
                    seen.Add(t.Building);
            return seen.Count;
        }

        // Ripe Berry Bushes regrow after the Empty bush's cooldown (4, or 1 next to an Irrigator).
        private static double BushCycle(Building bush)
        {
            if (bush.Tag == GameTag.BerryBushEmpty && bush.GamePieceWithCount != null && bush.Cooldown > 0)
                return bush.Cooldown + 0.5;
            foreach (Tile t in bush.GetAdjacentTiles())
                if (t != null && !t.IsEmpty && t.Building != null && t.Building.Categories.ContainsCategory(GamePieceCategory.Irrigator))
                    return 1.5;
            return 4.5;
        }

        // Puts the probe on the tile for a moment and re-runs the preview of every existing building that can see it.
        private double NearbyGain(Building probe, Tile tile)
        {
            _candidates.Clear();
            _triggeredBy.Clear();
            AddCandidatesSeeing(tile);
            foreach (Building b in _alwaysWatch)
                if (!_candidates.Contains(b))
                    _candidates.Add(b);

            Building previous = tile.Building;
            double gain = 0;
            tile.AssignBuilding(probe);
            // The game reads auras (Lighthouse, Balloon, Apothecary, Ocean Temple, Fisher, ...) through its building
            // list and its tile -> buildings-in-range index, which only real builds update. Put the probe in both for
            // the moment, and redo the Wall / Palisade enclosure with it in place.
            var restore = EnterBoard(probe);
            try
            {
                if (AuraTags.Contains(probe.Tag))
                {
                    // Buildings the aura reaches, and anything that scores them.
                    foreach (Tile t in probe.GetTilesInRange().Concat(probe.GetAdjacentTiles()))
                        AddReached(t);
                }
                // Houses the new building's range or adjacency reaches can climb a tier.
                foreach (Tile t in probe.GetTilesInRange().Concat(probe.GetAdjacentTiles()))
                    if (t != null && !t.IsEmpty && t.Building != null && t.Building.Behaviour is Entities.BuildingBehaviours._House && !_candidates.Contains(t.Building))
                        _candidates.Add(t.Building);
                if (probe.Tag == GameTag.Wall || probe.Tag == GameTag.Palisade)
                    foreach (Tile t in Reenclose(probe.Tag, restore))
                        AddReached(t);
                // A Fisher passes its extra mult on to the Fishing buildings in its range.
                foreach (Building b in _candidates.ToList())
                    if (b != null && b.Tag == GameTag.Fisher)
                        foreach (Tile t in b.GetTilesInRange())
                            AddReached(t);
                if (_candidates.Count == 0)
                    return 0;

                foreach (Building b in _candidates)
                    if (b != null)
                        ForgetCaches(b);
                foreach (Building b in _candidates)
                {
                    // Something built over is replaced, not boosted.
                    if (b == previous || b == null || !_baseline.TryGetValue(b, out double before))
                        continue;
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
                LeaveBoard(probe, restore);
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

        private void AddCandidatesSeeing(Tile t)
        {
            if (t != null && _watchers.TryGetValue(t, out List<Building> watching))
                foreach (Building b in watching)
                    if (!_candidates.Contains(b))
                        _candidates.Add(b);
        }

        // The building on a tile the aura changes, plus the buildings that can see that tile.
        private void AddReached(Tile t)
        {
            if (t == null)
                return;
            if (!t.IsEmpty && t.Building != null && !_candidates.Contains(t.Building))
                _candidates.Add(t.Building);
            AddCandidatesSeeing(t);
        }

        // Buildings whose effect on others goes through the game's range index or enclosure sets rather than the
        // tile they stand on: category / rarity / cooldown / range auras.
        private static readonly HashSet<GameTag> AuraTags = new HashSet<GameTag>
        {
            GameTag.Lighthouse, GameTag.Balloon, GameTag.Apothecary, GameTag.OceanTemple, GameTag.Fisher, GameTag.Trapper,
            GameTag.Park, GameTag.Quay, GameTag.Irrigation, GameTag.Stable, GameTag.TownBell, GameTag.Crane, GameTag.Plaza,
            GameTag.MagicPortal, GameTag.Campfire, GameTag.Obelisk, GameTag.Lamppost, GameTag.Warehouse,
        };

        private class BoardRestore
        {
            public readonly List<Tile> RangeTiles = new List<Tile>();
            public readonly List<Tile> TagTiles = new List<Tile>();
            public bool InList;
            public HashSet<Tile> Walls, Palisades;
            public bool SwappedWalls, SwappedPalisades;
        }

        private static readonly AccessTools.FieldRef<BuildingController, List<Building>> BuildingList =
            AccessTools.FieldRefAccess<BuildingController, List<Building>>("_buildings");
        private static readonly AccessTools.FieldRef<BuildingController, Dictionary<Tile, HashSet<Building>>> RangeIndex =
            AccessTools.FieldRefAccess<BuildingController, Dictionary<Tile, HashSet<Building>>>("_buildingsTilesRangeIndex");
        private static readonly AccessTools.FieldRef<BuildingController, Dictionary<Tile, HashSet<GameTag>>> RangeTagIndex =
            AccessTools.FieldRefAccess<BuildingController, Dictionary<Tile, HashSet<GameTag>>>("_buildingsTilesRangeTagIndex");
        private static readonly AccessTools.FieldRef<BuildingController, HashSet<Tile>> InsideWalls =
            AccessTools.FieldRefAccess<BuildingController, HashSet<Tile>>("_insideWalls");
        private static readonly AccessTools.FieldRef<BuildingController, HashSet<Tile>> InsidePalisades =
            AccessTools.FieldRefAccess<BuildingController, HashSet<Tile>>("_insidePalisades");

        private static BoardRestore EnterBoard(Building probe)
        {
            var r = new BoardRestore();
            BuildingController bc = MonoSingleton<BuildingController>.Instance;
            List<Building> list = BuildingList(bc);
            if (list != null && !list.Contains(probe))
            {
                list.Add(probe);
                r.InList = true;
            }
            Dictionary<Tile, HashSet<Building>> index = RangeIndex(bc);
            Dictionary<Tile, HashSet<GameTag>> tagIndex = RangeTagIndex(bc);
            if (index != null && tagIndex != null)
            {
                foreach (Tile t in probe.GetTilesInRange())
                {
                    if (t == null)
                        continue;
                    if (!index.TryGetValue(t, out HashSet<Building> set))
                        index[t] = set = new HashSet<Building>();
                    if (set.Add(probe))
                        r.RangeTiles.Add(t);
                    if (!tagIndex.TryGetValue(t, out HashSet<GameTag> tags))
                        tagIndex[t] = tags = new HashSet<GameTag>();
                    if (tags.Add(probe.Tag))
                        r.TagTiles.Add(t);
                }
            }
            return r;
        }

        private static void LeaveBoard(Building probe, BoardRestore r)
        {
            BuildingController bc = MonoSingleton<BuildingController>.Instance;
            if (r.InList)
                BuildingList(bc)?.Remove(probe);
            Dictionary<Tile, HashSet<Building>> index = RangeIndex(bc);
            Dictionary<Tile, HashSet<GameTag>> tagIndex = RangeTagIndex(bc);
            foreach (Tile t in r.RangeTiles)
                if (index != null && index.TryGetValue(t, out HashSet<Building> set))
                    set.Remove(probe);
            foreach (Tile t in r.TagTiles)
                if (tagIndex != null && tagIndex.TryGetValue(t, out HashSet<GameTag> tags))
                    tags.Remove(probe.Tag);
            if (r.SwappedWalls)
                InsideWalls(bc) = r.Walls;
            if (r.SwappedPalisades)
                InsidePalisades(bc) = r.Palisades;
        }

        // BuildingController.RefreshBuildings' enclosure flood fill, with the probe (already on its tile) counted.
        // Returns the tiles whose inside/outside status changes.
        private static IEnumerable<Tile> Reenclose(GameTag wallTag, BoardRestore r)
        {
            BuildingController bc = MonoSingleton<BuildingController>.Instance;
            MapController map = SerializedMonoSingleton<MapController>.Instance;
            var outside = new HashSet<Tile>(map.Grid.GetFloodFillAreaWithDiagonals(map.Grid.GetTile(0, 0),
                (Tile t) => t.IsEmpty || t.Building.Tag != wallTag));
            var inside = new HashSet<Tile>();
            foreach (Tile t in map.Grid)
                if ((t.IsEmpty || t.Building.Tag != wallTag) && !outside.Contains(t))
                    inside.Add(t);
            HashSet<Tile> old;
            if (wallTag == GameTag.Wall)
            {
                old = InsideWalls(bc);
                r.Walls = old;
                r.SwappedWalls = true;
                InsideWalls(bc) = inside;
            }
            else
            {
                old = InsidePalisades(bc);
                r.Palisades = old;
                r.SwappedPalisades = true;
                InsidePalisades(bc) = inside;
            }
            old = old ?? new HashSet<Tile>();
            return inside.Where(t => !old.Contains(t)).Concat(old.Where(t => !inside.Contains(t))).ToList();
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

            Building probe = CreateCopy(tag);
            if (probe == null)
            {
                _failedProbes.Add(tag);
                return null;
            }
            _probe = probe;
            _probeTag = tag;
            return _probe;
        }

        // What a card actually builds (PlacingBuilding: BuildingData.AlternatePlacedType, e.g. a crop's Empty form).
        private static GameTag PlacedTag(GameTag card)
        {
            BuildingData data = ScriptableObjectSingleton<GamePieceDataHolder>.Instance.GetBuildingDataFor(card);
            return data != null && data.AlternatePlacedType != GameTag.None ? data.AlternatePlacedType : card;
        }

        private Building GetGrown(GameTag card)
        {
            if (_grown != null && _grownTag == card)
                return _grown;
            if (_grown != null)
                Destroy(_grown.gameObject);
            _grown = CreateCopy(card);
            _grownTag = _grown != null ? card : GameTag.None;
            return _grown;
        }

        // A hidden, inactive, never-built instance of a building.
        private static Building CreateCopy(GameTag tag)
        {
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
                Plugin.Log.LogError($"Couldn't create a scoring copy of {tag}: {e}");
                return null;
            }
            probe.gameObject.SetActive(false);
            return probe;
        }

        private void DestroyProbe()
        {
            if (_grown != null)
                Destroy(_grown.gameObject);
            _grown = null;
            _grownTag = GameTag.None;
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
            List<Tile> spots = r != null && r.Plan.Count > 1 ? r.Plan : r?.Top;
            if (spots == null || spots.Count == 0 || cam == null || UnityEngine.InputSystem.Mouse.current == null)
                return;
            _cycleIndex = (_cycleIndex + 1) % spots.Count;
            Tile tile = spots[_cycleIndex];
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

            if (r.Plan.Count > 1)
                DrawPlan(cam, r, tilePx);
            int marks = r.Plan.Count > 1 ? 0 : Mathf.Clamp(Plugin.ScoreMarkBest.Value, 0, TopCount);
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
                // Tiles worth something only once show that in cyan, with a "+".
                bool onceOnly = Math.Abs(total) < 0.05;
                string text = onceOnly ? "+" + Fmt(entry.Value.Once) : Fmt(total);

                _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), text, _style);
                _style.normal.textColor = onceOnly
                    ? (Math.Abs(entry.Value.Rank - r.BestRank) < 1e-6 ? new Color(1f, 0.84f, 0.2f) : new Color(0.45f, 0.9f, 1f))
                    : ColorFor(entry.Value.Rank, r.BestRank);
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

        // Multi-placement plan: purple frames numbered in placing order, and the total at the top left.
        private void DrawPlan(Camera cam, Result r, float tilePx)
        {
            var purple = new Color(0.8f, 0.5f, 1f, 0.95f);
            _badgeStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(tilePx * 0.28f * Plugin.ScoreTextScale.Value), 9, 40);
            for (int i = 0; i < r.Plan.Count; i++)
            {
                Tile t = r.Plan[i];
                DrawTileFrame(cam, t, tilePx, purple, 3f);
                Vector3 c = cam.WorldToScreenPoint(new Vector3(t.X - 0.32f, t.Y + 0.32f));
                string n = (i + 1).ToString();
                Vector2 size = _badgeStyle.CalcSize(new GUIContent(n));
                _badgeStyle.normal.textColor = purple;
                GUI.Box(new Rect(c.x - size.x / 2f - 3f, Screen.height - c.y - size.y / 2f, size.x + 6f, size.y + 2f), n, _badgeStyle);
            }
            var sb = new System.Text.StringBuilder($"Best {r.Plan.Count} placements: {Fmt(r.PlanTotal)}{(r.PerTurn ? "/turn" : "")}"
                                                   + (r.PlanOnce >= 0.5 ? $" +{Fmt(r.PlanOnce)} now" : ""));
            for (int i = 0; i < r.PlanGains.Count; i++)
                sb.Append($"\n  {i + 1}. +{Fmt(r.PlanGains[i])}");
            sb.Append($"\n{Plugin.CycleBestKey.Value} jumps between them while placing");
            _tipStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(15 * Plugin.ScoreTextScale.Value), 10, 30);
            var content = new GUIContent(sb.ToString());
            Vector2 box = _tipStyle.CalcSize(content);
            GUI.Box(new Rect(16f, Screen.height * 0.22f, box.x + 10f, box.y + 6f), content, _tipStyle);
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
                if (Usable(key) is Result r && r.BestRank > top)
                    top = r.BestRank;

            var corners = new Vector3[4];
            var content = new GUIContent();
            int baseSize = Mathf.Clamp(Mathf.RoundToInt(15 * Plugin.ScoreTextScale.Value), 10, 30);
            foreach ((BuildingChoiceButton button, Key key) in _cards)
            {
                if (button == null)
                    continue;
                var rt = (RectTransform)button.transform;
                rt.GetWorldCorners(corners);
                Camera cam = CanvasCamera(rt);
                Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

                // One fact per line, shrunk to the card's width so neighbouring badges never overlap.
                Result r = Usable(key);
                var lines = new List<string>();
                if (r == null)
                    lines.Add("...");
                else if (r.BestRank <= 0)
                    lines.Add("No points");
                else
                {
                    if (Math.Abs(r.Best) >= 0.05)
                        lines.Add($"{Fmt(r.Best)}{(r.PerTurn ? "/turn" : "")}");
                    if (r.BestOnce >= 0.5)
                        lines.Add($"+{Fmt(r.BestOnce)} now");
                    if (r.Plan.Count > 1)
                        lines.Add($"\u00d7{r.Plan.Count}: {Fmt(r.PlanTotal)}{(r.PerTurn ? "/turn" : "")}");
                }
                content.text = string.Join("\n", lines);
                float maxWidth = Mathf.Max(30f, (topRight.x - bottomLeft.x) * 0.92f) - 8f;
                int fontSize = baseSize;
                _badgeStyle.fontSize = fontSize;
                while (fontSize > 8 && _badgeStyle.CalcSize(content).x > maxWidth)
                    _badgeStyle.fontSize = --fontSize;
                string text = content.text;
                Vector2 size = _badgeStyle.CalcSize(content);
                float x = (bottomLeft.x + topRight.x) / 2f - size.x / 2f - 4f;
                float y = Screen.height - topRight.y - size.y - 8f;
                _badgeStyle.normal.textColor = r != null && r.BestRank > 0 && r.BestRank == top && _cards.Count > 1 ? new Color(1f, 0.84f, 0.2f) : Color.white;
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
            if (hovered == null || (r.Scores[hovered].Nearby == 0 && r.Scores[hovered].Note == null && r.Scores[hovered].Once < 0.5))
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
            DestroySpawnCopies();
            ClearGhosts();
            DestroyProbe();
        }
    }
}
