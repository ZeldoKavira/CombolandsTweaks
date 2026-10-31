using System;
using System.Collections.Generic;
using Entities;
using Environment;
using HarmonyLib;
using Interaction;
using Interaction.InteractionStates;
using Library;
using Library.Grid;
using UI;
using UI.Shop;
using UI.CouncilRoomScreen;
using GameState;
using GameState.Data;
using UnityEngine;

namespace CombolandsTweaks
{
    // While a building upgrade (building supply: +mult, -cooldown, +range, +activation) is hovered in the shop or the
    // consumables bar, or picked and waiting for a target, shows on every building it can go on the average points per
    // turn now and with the upgrade. The upgrade's stat change is applied to the building for a moment, the game's own
    // preview re-run, and the change undone.
    internal class UpgradePreview : MonoBehaviour
    {
        private const float FindInterval = 0.5f;

        // Each candidate building costs a few previews (paints: of every building around it too), so the work is
        // spread over frames and only redone when the upgrade or the board changes.
        private const double FrameBudgetMs = 2.0;

        private struct Row
        {
            public double Now;
            public double After;
            public bool PerTurn;
            public int Affected; // paints: other buildings whose points change
        }

        private Dictionary<Building, Row> _rows = new Dictionary<Building, Row>();
        private double _bestGain;
        private GameTag _shownTag = GameTag.None;
        // Council votes: the member hovered, and whether it's their current votes (council room) or one more.
        private CouncilCharacter _shownVote = CouncilCharacter.None;
        private bool _shownVoteCurrent;
        private int _shownKey;
        private CouncilCharacter _jobVote = CouncilCharacter.None;
        private bool _jobVoteCurrent;
        private GamePieceChoice[] _choices = Array.Empty<GamePieceChoice>();
        private CouncilCharacterHover[] _councilHovers = Array.Empty<CouncilCharacterHover>();

        private static readonly AccessTools.FieldRef<CouncilFavour, Dictionary<CouncilCharacter, int>> CouncilVotes =
            AccessTools.FieldRefAccess<CouncilFavour, Dictionary<CouncilCharacter, int>>("_councilVotes");
        private int _shownBoard;
        private bool _loggedError;

        // The calculation in progress; its rows replace _rows when it finishes.
        private bool _jobActive;
        private GameTag _jobTag;
        private float _jobAmount;
        private readonly List<Building> _jobBuildings = new List<Building>();
        private int _jobIndex;
        private Dictionary<Building, Row> _jobRows = new Dictionary<Building, Row>();
        private double _jobBest;
        // Each building's points per turn before any change, worked out once per job (NaN = no figure).
        private readonly Dictionary<Building, double> _before = new Dictionary<Building, double>();
        // Paints: buildings whose range or adjacency covers each tile.
        private readonly Dictionary<Tile, List<Building>> _watchers = new Dictionary<Tile, List<Building>>();
        private readonly List<Building> _townHalls = new List<Building>();

        private Consumable[] _consumables = Array.Empty<Consumable>();
        private ShopSingle[] _shopItems = Array.Empty<ShopSingle>();
        private float _nextFind;


        internal static bool Showing;

        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalRange = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localRangeChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, float> LocalMult = AccessTools.FieldRefAccess<GamePieceLocalValues, float>("_localMultiplierChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalCooldown = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localCooldownChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalActivations = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localActivationCountChanges");

        private static bool IsStatUpgrade(GameTag tag) =>
            tag == GameTag.BsStatModMult || tag == GameTag.BsStatModCooldown || tag == GameTag.BsStatModRange || tag == GameTag.BsStatModActivationCount;

        // Paints whose effect is just being painted (Wild = rainbow changes the major category). Ghost paint asks for
        // a category first and black paint is a penalty, so neither is previewed.
        private static bool IsPaint(GameTag tag) =>
            tag == GameTag.BsRainbowPaint || tag == GameTag.BsRedPaint || tag == GameTag.BsGoldPaint || tag == GameTag.BsGreenPaint
            || tag == GameTag.BsBluePaint || tag == GameTag.BsWhitePaint || tag == GameTag.BsPurplePaint;

        private static bool IsSupported(GameTag tag) => IsStatUpgrade(tag) || IsPaint(tag);

        private static readonly AccessTools.FieldRef<BuildingPaint, GameTag> PaintTag = AccessTools.FieldRefAccess<BuildingPaint, GameTag>("_paintTag");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, GamePieceCategory> MajorOverride = AccessTools.FieldRefAccess<GamePieceLocalValues, GamePieceCategory>("_localOverrideMajorCategory");

        private void Update()
        {
            GameTag tag = GameTag.None;
            CouncilCharacter vote = CouncilCharacter.None;
            bool current = false;
            if (Plugin.UpgradePreviewEnabled.Value && MonoSingleton<BuildingController>.HasInstance)
            {
                tag = FindUpgrade();
                if (tag == GameTag.None && SerializedMonoSingleton<CouncilFavour>.HasInstance)
                    vote = FindVote(out current);
            }
            int key = tag != GameTag.None ? (int)tag : vote != CouncilCharacter.None ? -(1000 + (int)vote * 2 + (current ? 1 : 0)) : 0;
            if (key == 0)
            {
                _rows.Clear();
                _jobActive = false;
                _shownKey = 0;
                _shownTag = GameTag.None;
                _shownVote = CouncilCharacter.None;
                Showing = false;
                return;
            }

            int board = PlacementScores.BoardSignature();
            if (key != _shownKey)
            {
                // Never show one upgrade's numbers for another while the new ones are worked out.
                _rows.Clear();
                _bestGain = 0;
            }
            if (key != _shownKey || board != _shownBoard)
            {
                StartJob(tag);
                _jobVote = vote;
                _jobVoteCurrent = current;
                _shownKey = key;
                _shownTag = tag;
                _shownVote = vote;
                _shownVoteCurrent = current;
                _shownBoard = board;
            }
            if (_jobActive)
            {
                if (_jobVote != CouncilCharacter.None)
                    StepVote();
                else
                    StepJob();
            }
            Showing = _rows.Count > 0;
        }

        // A council vote under the mouse: one you could get (consumables bar, shop, pack pick) or a member in the
        // council room, whose current votes are shown instead.
        private CouncilCharacter FindVote(out bool current)
        {
            current = false;
            foreach (Consumable c in _consumables)
                if (c != null && c.isActiveAndEnabled && c.ConsumableType == GameItemType.CouncilVote
                    && c.GameTag.CharacterFromFavour() != CouncilCharacter.None && IsMouseOver(c.transform as RectTransform))
                    return c.GameTag.CharacterFromFavour();
            foreach (ShopSingle sh in _shopItems)
            {
                if (sh == null || !sh.isActiveAndEnabled || sh.ItemType != GameItemType.CouncilVote)
                    continue;
                CouncilCharacter c = Traverse.Create(sh).Field<GameTag>("_tag").Value.CharacterFromFavour();
                if (c != CouncilCharacter.None && IsMouseOver(sh.transform as RectTransform))
                    return c;
            }
            foreach (GamePieceChoice choice in _choices)
            {
                if (choice == null || !choice.isActiveAndEnabled)
                    continue;
                CouncilCharacter c = Traverse.Create(choice).Field<GameTag>("_type").Value.CharacterFromFavour();
                if (c != CouncilCharacter.None && IsMouseOver(choice.transform as RectTransform))
                    return c;
            }
            foreach (CouncilCharacterHover h in _councilHovers)
            {
                if (h == null || !h.isActiveAndEnabled || h.CouncilCharacter == CouncilCharacter.None || h.Votes <= 0)
                    continue;
                if (IsMouseOver(h.transform as RectTransform))
                {
                    current = true;
                    return h.CouncilCharacter;
                }
            }
            return CouncilCharacter.None;
        }

        // Points per turn of a batch of buildings with the member's votes set to `votes`, then restored.
        private void ValuesWithVotes(List<Building> batch, CouncilCharacter c, int votes, double[] into)
        {
            CouncilFavour council = SerializedMonoSingleton<CouncilFavour>.Instance;
            Dictionary<CouncilCharacter, int> dict = CouncilVotes(council);
            bool hadDict = dict != null;
            int old = council.GetVotesFor(c);
            bool hadKey = hadDict && dict.ContainsKey(c);
            bool change = votes != old;
            if (change)
            {
                if (!hadDict)
                    CouncilVotes(council) = dict = new Dictionary<CouncilCharacter, int>();
                dict[c] = votes;
                BuildingExtensions.ResetCaches();
            }
            try
            {
                for (int i = 0; i < batch.Count; i++)
                    into[i] = Attributed(batch[i], out double v, out _) ? v : double.NaN;
            }
            finally
            {
                if (change)
                {
                    if (!hadDict)
                        CouncilVotes(council) = null;
                    else if (hadKey)
                        dict[c] = old;
                    else
                        dict.Remove(c);
                    BuildingExtensions.ResetCaches();
                }
            }
        }

        // A vote changes every building in the member's categories at once, so buildings are done in small batches,
        // each with the votes changed and restored within the frame.
        private void StepVote()
        {
            const int BatchSize = 8;
            var frame = System.Diagnostics.Stopwatch.StartNew();
            int cur = SerializedMonoSingleton<CouncilFavour>.Instance.GetVotesFor(_jobVote);
            int from = _jobVoteCurrent ? 0 : cur;
            int to = _jobVoteCurrent ? cur : cur + 1;
            var batch = new List<Building>(BatchSize);
            var now = new double[BatchSize];
            var after = new double[BatchSize];
            while (_jobIndex < _jobBuildings.Count && frame.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                batch.Clear();
                while (batch.Count < BatchSize && _jobIndex < _jobBuildings.Count)
                {
                    Building b = _jobBuildings[_jobIndex++];
                    if (b != null)
                        batch.Add(b);
                }
                try
                {
                    ValuesWithVotes(batch, _jobVote, from, now);
                    ValuesWithVotes(batch, _jobVote, to, after);
                }
                catch (Exception e)
                {
                    LogOnce($"Vote preview for {_jobVote} failed: {e}");
                    continue;
                }
                for (int i = 0; i < batch.Count; i++)
                {
                    if (double.IsNaN(now[i]) || double.IsNaN(after[i]) || Math.Abs(after[i] - now[i]) < 0.05)
                        continue;
                    var row = new Row { Now = now[i], After = after[i], PerTurn = true };
                    _jobRows[batch[i]] = row;
                    if (row.After - row.Now > _jobBest)
                        _jobBest = row.After - row.Now;
                }
            }
            if (_jobIndex < _jobBuildings.Count)
                return;
            _jobActive = false;
            (_rows, _jobRows) = (_jobRows, _rows);
            _bestGain = _jobBest;
        }

        private static string NameOf(CouncilCharacter c) =>
            Framework.ScriptableObjectSingleton<StringLookup>.Instance.GetNameForCouncilCharacter(c, out string name) ? name : c.ToString();

        // The upgrade waiting for a target wins; otherwise one under the mouse in the shop or the consumables bar.
        private GameTag FindUpgrade()
        {
            if (SerializedMonoSingleton<InteractionController>.HasInstance
                && SerializedMonoSingleton<InteractionController>.Instance.CurrentInteractionState is ChangingBuildingStat changing)
            {
                GameTag stat = Traverse.Create(changing).Field<GameTag>("_stat").Value;
                if (IsStatUpgrade(stat))
                    return stat;
            }
            if (SerializedMonoSingleton<InteractionController>.HasInstance
                && SerializedMonoSingleton<InteractionController>.Instance.CurrentInteractionState is PaintingBuilding painting)
            {
                GameTag paint = Traverse.Create(painting).Field<GameTag>("_paintTag").Value;
                if (IsPaint(paint))
                    return paint;
            }

            if (Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + FindInterval;
                _consumables = FindObjectsByType<Consumable>(FindObjectsSortMode.None);
                _shopItems = FindObjectsByType<ShopSingle>(FindObjectsSortMode.None);
                _choices = FindObjectsByType<GamePieceChoice>(FindObjectsSortMode.None);
                _councilHovers = FindObjectsByType<CouncilCharacterHover>(FindObjectsSortMode.None);
            }
            foreach (Consumable c in _consumables)
                if (c != null && c.isActiveAndEnabled && IsSupported(c.GameTag) && IsMouseOver(c.transform as RectTransform))
                    return c.GameTag;
            foreach (ShopSingle s in _shopItems)
            {
                if (s == null || !s.isActiveAndEnabled || s.ItemType != GameItemType.BuildingUpgrade)
                    continue;
                GameTag t = Traverse.Create(s).Field<GameTag>("_tag").Value;
                if (IsSupported(t) && IsMouseOver(s.transform as RectTransform))
                    return t;
            }
            return GameTag.None;
        }

        private static bool IsMouseOver(RectTransform rt)
        {
            if (rt == null)
                return false;
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
        }

        private void StartJob(GameTag tag)
        {
            _jobActive = false;
            _jobBuildings.Clear();
            _jobRows = new Dictionary<Building, Row>();
            _jobBest = 0;
            _jobIndex = 0;
            _before.Clear();
            _watchers.Clear();
            _townHalls.Clear();
            try
            {
                BuildingExtensions.ResetCaches();
                BuildTriggerModel();
                foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings)
                {
                    if (b == null || b.Values == null || !b.isActiveAndEnabled)
                        continue;
                    _jobBuildings.Add(b);
                    if (IsPaint(tag))
                    {
                        if (b.Tag == GameTag.TownHall)
                            _townHalls.Add(b);
                        AddWatcher(b, b.GetTilesInRange());
                        AddWatcher(b, b.GetAdjacentTiles());
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce($"Upgrade preview setup for {tag} failed: {e}");
                return;
            }
            _jobTag = tag;
            _jobAmount = IsStatUpgrade(tag) ? ConsumableBehaviour.GetBuildingSupplyParam(tag) : 0f;
            _jobActive = true;
        }

        private void AddWatcher(Building w, HashSet<Tile> tiles)
        {
            foreach (Tile t in tiles)
            {
                if (t == null)
                    continue;
                if (!_watchers.TryGetValue(t, out List<Building> list))
                    _watchers[t] = list = new List<Building>();
                if (!list.Contains(w))
                    list.Add(w);
            }
        }

        private void StepJob()
        {
            var frame = System.Diagnostics.Stopwatch.StartNew();
            while (_jobIndex < _jobBuildings.Count && frame.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                Building b = _jobBuildings[_jobIndex++];
                if (b == null)
                    continue;
                try
                {
                    Row row;
                    bool ok = IsPaint(_jobTag) ? PaintRow(b, _jobTag, out row) : StatRow(b, _jobTag, _jobAmount, out row);
                    if (!ok)
                        continue;
                    _jobRows[b] = row;
                    if (row.After - row.Now > _jobBest)
                        _jobBest = row.After - row.Now;
                }
                catch (Exception e)
                {
                    LogOnce($"Upgrade preview for {b.Tag} at {b.X},{b.Y} failed: {e}");
                }
            }
            if (_jobIndex < _jobBuildings.Count)
                return;
            _jobActive = false;
            (_rows, _jobRows) = (_jobRows, _rows);
            _bestGain = _jobBest;
        }

        private void LogOnce(string message)
        {
            if (_loggedError)
                return;
            _loggedError = true;
            Plugin.Log.LogError(message);
        }

        // Points per turn before any change, memoised for the job.
        private double Before(Building x)
        {
            if (!_before.TryGetValue(x, out double v))
                _before[x] = v = Attributed(x, out double value, out _) ? value : double.NaN;
            return v;
        }

        private bool StatRow(Building b, GameTag tag, float amount, out Row row)
        {
            row = default;
            if (!ConsumableBehaviour.CanUseConsumableOn(tag, b.Tile))
                return false;
            if (tag == GameTag.BsStatModMult && !b.Tag.CanGetMult())
                return false;
            // Buildings that never activate on their own and that nothing nearby triggers have no meaningful figure.
            if (!Attributed(b, out row.Now, out row.PerTurn))
                return false;
            Apply(b, tag, amount);
            try
            {
                PlacementScores.ForgetCaches(b);
                Attributed(b, out row.After, out _);
            }
            finally
            {
                Apply(b, tag, -amount);
                PlacementScores.ForgetCaches(b);
            }
            return row.Now != 0 || row.After != 0;
        }

        // A paint changes how other buildings see the painted one (a Wild building counts as every category), so the
        // figure is the points per turn of the painted building plus every building whose range or adjacency covers it.
        private bool PaintRow(Building b, GameTag paint, out Row row)
        {
            row = default;
            if (b.BuildingPaint == null || !ConsumableBehaviour.CanUseConsumableOn(paint, b.Tile))
                return false;
            var affected = new List<Building> { b };
            if (_watchers.TryGetValue(b.Tile, out List<Building> seeing))
                foreach (Building w in seeing)
                    if (!affected.Contains(w))
                        affected.Add(w);
            foreach (Building w in _townHalls)
                if (!affected.Contains(w))
                    affected.Add(w);

            row.PerTurn = true;
            row.Affected = affected.Count - 1;
            foreach (Building x in affected)
            {
                double v = Before(x);
                if (!double.IsNaN(v))
                    row.Now += v;
            }

            GameTag oldPaint = PaintTag(b.BuildingPaint);
            GamePieceCategory oldMajor = MajorOverride(b.Values);
            PaintTag(b.BuildingPaint) = paint;
            if (paint == GameTag.BsRainbowPaint)
                MajorOverride(b.Values) = GamePieceCategory.Wild;
            try
            {
                // Only these buildings' cached ranges, adjacency counts and categories can change.
                foreach (Building x in affected)
                    PlacementScores.ForgetCaches(x);
                foreach (Building x in affected)
                    if (Attributed(x, out double v, out _))
                        row.After += v;
            }
            finally
            {
                PaintTag(b.BuildingPaint) = oldPaint;
                MajorOverride(b.Values) = oldMajor;
                foreach (Building x in affected)
                    PlacementScores.ForgetCaches(x);
            }
            return Math.Abs(row.After - row.Now) >= 0.05;
        }

        // Only the stored change: no notification text, effects or quest progress like AddLocalStatChange.
        private static void Apply(Building b, GameTag tag, float amount)
        {
            GamePieceLocalValues v = b.Values;
            switch (tag)
            {
                case GameTag.BsStatModRange:
                    LocalRange(v) += Mathf.RoundToInt(amount);
                    break;
                case GameTag.BsStatModMult:
                    LocalMult(v) += amount;
                    break;
                case GameTag.BsStatModCooldown:
                    LocalCooldown(v) += Mathf.RoundToInt(amount);
                    break;
                case GameTag.BsStatModActivationCount:
                    LocalActivations(v) += Mathf.RoundToInt(amount);
                    break;
            }
        }

        // How buildings trigger each other, modelled on the behaviours that call TriggerController.AddOnActivatedTrigger:
        // most pick (activation count) random buildings from their adjacent tiles or range each time they activate;
        // some trigger every match; Ballista shoots in one of 4 random directions per shot; Post Office triggers the
        // building above it; Outpost picks one adjacent building whose only neighbour is the Outpost.
        internal enum Area { Adjacent, Range, Up, Orthogonal }

        internal struct Triggerer
        {
            public Area Area;
            public bool All;           // triggers every candidate instead of picking at random
            public bool UseCategories; // only buildings in its target categories
            public bool Isolated;      // only buildings whose sole neighbour is the triggerer (Outpost)
        }

        internal static readonly Dictionary<GameTag, Triggerer> Triggerers = new Dictionary<GameTag, Triggerer>
        {
            { GameTag.Sawmill, new Triggerer { Area = Area.Range, UseCategories = true } },
            { GameTag.Conduit, new Triggerer { Area = Area.Range } },
            { GameTag.Trawler, new Triggerer { Area = Area.Range, UseCategories = true } },
            { GameTag.BanditCamp, new Triggerer { Area = Area.Adjacent } },
            { GameTag.FaerieRing, new Triggerer { Area = Area.Adjacent } },
            { GameTag.WayStation, new Triggerer { Area = Area.Adjacent } },
            { GameTag.Brewery, new Triggerer { Area = Area.Adjacent } },
            { GameTag.Treasury, new Triggerer { Area = Area.Adjacent } },
            { GameTag.Harvester, new Triggerer { Area = Area.Adjacent } },
            { GameTag.GuardTower, new Triggerer { Area = Area.Adjacent } },
            { GameTag.Outpost, new Triggerer { Area = Area.Adjacent, Isolated = true } },
            { GameTag.EffigyPyre, new Triggerer { Area = Area.Adjacent, All = true } },
            { GameTag.Marketplace, new Triggerer { Area = Area.Adjacent, All = true, UseCategories = true } },
            { GameTag.HerbGarden, new Triggerer { Area = Area.Adjacent, All = true, UseCategories = true } },
            { GameTag.FishTrap, new Triggerer { Area = Area.Range, All = true, UseCategories = true } },
            { GameTag.SparringYard, new Triggerer { Area = Area.Range, All = true, UseCategories = true } },
            { GameTag.PostOffice, new Triggerer { Area = Area.Up, All = true } },
            { GameTag.Ballista, new Triggerer { Area = Area.Orthogonal } },
        };

        // Per building, before any upgrade: points per activation, and activations per turn it gets from others.
        private readonly Dictionary<Building, double> _points = new Dictionary<Building, double>();
        private readonly Dictionary<Building, double> _incoming = new Dictionary<Building, double>();
        // Triggered only by buildings that react to events (no countdown): how often is unknown.
        private readonly Dictionary<Building, string> _eventTriggeredBy = new Dictionary<Building, string>();

        private void BuildTriggerModel()
        {
            _points.Clear();
            _incoming.Clear();
            _eventTriggeredBy.Clear();
            List<Building> all = MonoSingleton<BuildingController>.Instance.Buildings;
            foreach (Building b in all)
                if (b != null && b.Values != null && b.isActiveAndEnabled)
                    _points[b] = PointsPerActivation(b);

            foreach (Building t in all)
            {
                if (t == null || !_points.ContainsKey(t) || !Triggerers.ContainsKey(t.Tag))
                    continue;
                List<Building> targets = Candidates(t);
                double share = Share(t, targets);
                double rate = OwnRate(t);
                foreach (Building x in targets)
                {
                    if (rate > 0)
                    {
                        _incoming.TryGetValue(x, out double r);
                        _incoming[x] = r + rate * share;
                    }
                    else if (!_eventTriggeredBy.ContainsKey(x))
                    {
                        _eventTriggeredBy[x] = NameOf(t);
                    }
                }
            }
        }

        internal static List<Building> Candidates(Building t)
        {
            Triggerer cfg = Triggerers[t.Tag];
            IEnumerable<Tile> tiles;
            switch (cfg.Area)
            {
                case Area.Range:
                    tiles = t.GetTilesInRange();
                    break;
                case Area.Up:
                    tiles = new[] { t.Tile?.UpNeighbour() };
                    break;
                case Area.Orthogonal:
                    tiles = t.Tile == null ? new Tile[0] : new[] { t.Tile.UpNeighbour(), t.Tile.LeftNeighbour(), t.Tile.DownNeighbour(), t.Tile.RightNeighbour() };
                    break;
                default:
                    tiles = t.GetAdjacentTiles();
                    break;
            }
            GamePieceCategory[] cats = cfg.UseCategories ? t.Behaviour.GetTargetCategories(t) : null;
            var list = new List<Building>();
            foreach (Tile tile in tiles)
            {
                if (tile == null || tile.IsEmpty)
                    continue;
                Building x = tile.Building;
                if (x == null || x == t || x.Tag == t.Tag || !x.Behaviour.CanBeActivated(x))
                    continue;
                if (cats != null && cats.Length > 0 && !x.Categories.ContainsAnyOf(cats))
                    continue;
                if (cfg.Isolated && !OnlyNeighbourIs(x, t))
                    continue;
                if (!list.Contains(x))
                    list.Add(x);
            }
            return list;
        }

        private static bool OnlyNeighbourIs(Building x, Building t)
        {
            foreach (Tile n in x.GetAdjacentTiles())
                if (n != null && !n.IsEmpty && n.Building != t && n.Building != x)
                    return false;
            return true;
        }

        // Expected activations each candidate gets per activation of the triggerer, including its activation chance
        // (Brewery fires half the time).
        internal static double Share(Building t, List<Building> targets)
        {
            if (targets.Count == 0)
                return 0;
            double chance = Math.Min(1.0, t.Behaviour.GetActivationChance(t));
            if (chance <= 0)
                chance = 1.0;
            Triggerer cfg = Triggerers[t.Tag];
            if (cfg.All)
                return chance;
            if (t.Tag == GameTag.Ballista)
            {
                // One shot plus one per Arbalest, each in one of 4 directions.
                int shots = 1 + (MonoSingleton<ItemHeirloomController>.HasInstance ? MonoSingleton<ItemHeirloomController>.Instance.GetCountOfItemWithTagEquipped(GameTag.Arbalest) : 0);
                return chance * shots / 4.0;
            }
            return chance * Math.Min(1.0, Math.Max(1, t.Behaviour.GetActivationCount(t)) / (double)targets.Count);
        }

        private static bool HasCountdown(Building b) => b.GamePieceWithCount != null && b.Cooldown > 0;

        private static double OwnRate(Building b) => PlacementScores.OwnRate(b);

        // Points per activation (the game's preview) times activations.
        private static double PointsPerActivation(Building b) =>
            PlacementScores.PreviewTotal(b, 1.0) * Math.Max(1, b.Behaviour.GetActivationCount(b));

        // Points per turn the building is responsible for: its own (on its countdown plus triggers from neighbours)
        // and, for a building that triggers others, the points of the activations it hands out. Falls back to points
        // per activation for a building only event-driven neighbours trigger. False if neither applies.
        private bool Attributed(Building b, out double value, out bool perTurn)
        {
            double own = OwnRate(b);
            _incoming.TryGetValue(b, out double incoming);
            double rate = own + incoming;
            double points = PointsPerActivation(b);
            value = points * rate;
            double delivered = 0;
            if (Triggerers.ContainsKey(b.Tag) && own > 0)
                delivered = own * PlacementScores.Delivered(b, 1);
            value += delivered;
            perTurn = true;
            if (rate > 0 || delivered > 0)
                return true;
            if (_eventTriggeredBy.ContainsKey(b))
            {
                value = points;
                perTurn = false;
                return true;
            }
            return false;
        }

        private GUIStyle _bigStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _panelStyle;
        private readonly GUIContent _content = new GUIContent();

        private void OnGUI()
        {
            if (_rows.Count == 0 || Event.current.type != EventType.Repaint)
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;
            float tilePx = Mathf.Abs(cam.WorldToScreenPoint(Vector3.right).x - cam.WorldToScreenPoint(Vector3.zero).x);
            if (tilePx < 8f)
                return;
            if (_bigStyle == null)
            {
                _bigStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset() };
                _smallStyle = new GUIStyle(_bigStyle) { fontStyle = FontStyle.Normal };
                _smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                _panelStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = false, richText = true, fontStyle = FontStyle.Bold };
                _panelStyle.normal.textColor = Color.white;
            }

            // Each label stays inside its own tile (top part, clear of the game's count numbers), so labels of
            // neighbouring buildings can never overlap.
            float scale = Plugin.ScoreTextScale.Value;
            float width = tilePx * 0.94f;
            float bigH = tilePx * 0.3f * scale, smallH = tilePx * 0.21f * scale;
            Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Building hovered = null;
            Color old = GUI.color;
            foreach (KeyValuePair<Building, Row> entry in _rows)
            {
                Building b = entry.Key;
                if (b == null)
                    continue;
                Row r = entry.Value;
                Vector3 c = cam.WorldToScreenPoint(new Vector3(b.X, b.Y));
                if (c.z < 0f)
                    continue;
                var tile = new Rect(c.x - tilePx / 2f, Screen.height - c.y - tilePx / 2f, tilePx, tilePx);
                if (tile.Contains(mouse))
                    hovered = b;

                var box = new Rect(c.x - width / 2f, tile.y + tilePx * 0.03f, width, bigH + smallH + 2f);
                GUI.color = new Color(0.05f, 0.05f, 0.08f, 0.85f);
                GUI.DrawTexture(box, Texture2D.whiteTexture);
                GUI.color = old;

                double gain = r.After - r.Now;
                _bigStyle.normal.textColor = ColourFor(gain);
                DrawFitted(new Rect(box.x, box.y, box.width, bigH), $"{(gain > 0 ? "+" : "")}{Format(gain)}", _bigStyle, bigH);
                DrawFitted(new Rect(box.x, box.y + bigH, box.width, smallH), $"{Format(r.Now)}→{Format(r.After)}{(r.PerTurn ? "" : "/act")}", _smallStyle, smallH);
            }

            DrawTopList(scale);
            if (hovered != null)
                DrawTooltip(hovered, _rows[hovered], mouse, scale);
        }

        // Shrinks the font until the text fits the width.
        private void DrawFitted(Rect rect, string text, GUIStyle style, float height)
        {
            _content.text = text;
            int size = Mathf.Clamp(Mathf.RoundToInt(height * 0.9f), 6, 48);
            style.fontSize = size;
            while (size > 6 && style.CalcSize(_content).x > rect.width)
                style.fontSize = --size;
            GUI.Label(rect, _content, style);
        }

        // Fixed list on the left: the five buildings that gain most from the upgrade.
        private void DrawTopList(float scale)
        {
            var top = new List<KeyValuePair<Building, Row>>(_rows);
            top.RemoveAll(e => e.Key == null || !e.Value.PerTurn || e.Value.After - e.Value.Now <= 0);
            if (top.Count == 0)
                return;
            top.Sort((a, b) => (b.Value.After - b.Value.Now).CompareTo(a.Value.After - a.Value.Now));
            double total = 0;
            foreach (KeyValuePair<Building, Row> e in _rows)
                if (e.Key != null && e.Value.PerTurn)
                    total += e.Value.After - e.Value.Now;
            string title;
            if (_shownVote != CouncilCharacter.None)
            {
                int cur = SerializedMonoSingleton<CouncilFavour>.Instance.GetVotesFor(_shownVote);
                title = _shownVoteCurrent
                    ? $"{NameOf(_shownVote)}'s {cur} vote{(cur == 1 ? "" : "s")} add {Format(total)} points per turn"
                    : $"A vote for {NameOf(_shownVote)} ({cur} \u2192 {cur + 1}): {(total >= 0 ? "+" : "")}{Format(total)} points per turn";
            }
            else
            {
                title = $"Best buildings for this upgrade (points per turn)";
            }
            var sb = new System.Text.StringBuilder(title + "\n");
            for (int i = 0; i < top.Count && i < 5; i++)
            {
                Row r = top[i].Value;
                double gain = r.After - r.Now;
                sb.Append($"\n{i + 1}. {NameOf(top[i].Key)}  <color={Hex(ColourFor(gain))}>+{Format(gain)}</color>  ({Format(r.Now)} → {Format(r.After)})");
            }
            _panelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(15 * scale), 10, 30);
            _content.text = sb.ToString();
            Vector2 size = _panelStyle.CalcSize(_content);
            GUI.color = new Color(1f, 1f, 1f, 1f);
            Rect rect = new Rect(16f, Screen.height * 0.22f, size.x + 12f, size.y + 8f);
            Color old = GUI.color;
            GUI.color = new Color(0.05f, 0.05f, 0.08f, 0.9f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, size.x, size.y), _content, _panelStyle);
        }

        private void DrawTooltip(Building b, Row r, Vector2 mouse, float scale)
        {
            double gain = r.After - r.Now;
            string how;
            if (_shownVote != CouncilCharacter.None)
                how = _shownVoteCurrent ? $"points per turn without {NameOf(_shownVote)}'s votes \u2192 now" : $"points per turn now \u2192 with one more vote for {NameOf(_shownVote)}";
            else if (IsPaint(_shownTag))
                how = $"points per turn of it and the {r.Affected} building{(r.Affected == 1 ? "" : "s")} around it that it counts for";
            else if (!r.PerTurn)
                how = $"points per activation (triggered by {(_eventTriggeredBy.TryGetValue(b, out string by) ? by : "a neighbour")} when it reacts)";
            else
            {
                _incoming.TryGetValue(b, out double incoming);
                how = "points per turn (" + (PlacementScores.IsIrrigated(b) ? "irrigated: every turn" : HasCountdown(b) ? $"every {b.Cooldown} turns" : "no countdown")
                      + (incoming > 0 ? $", +{incoming:0.##} triggers/turn from neighbours" : "")
                      + (Triggerers.ContainsKey(b.Tag) && HasCountdown(b) ? ", incl. what it triggers" : "") + ")";
            }
            _content.text = $"{NameOf(b)}\n{Format(r.Now)} → <color={Hex(ColourFor(gain))}>{Format(r.After)}</color> {how}";
            _panelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(15 * scale), 10, 30);
            Vector2 size = _panelStyle.CalcSize(_content);
            var rect = new Rect(mouse.x + 20f, mouse.y + 20f, size.x + 12f, size.y + 8f);
            if (rect.xMax > Screen.width)
                rect.x = mouse.x - rect.width - 10f;
            if (rect.yMax > Screen.height)
                rect.y = mouse.y - rect.height - 10f;
            Color old = GUI.color;
            GUI.color = new Color(0.05f, 0.05f, 0.08f, 0.95f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, size.x, size.y), _content, _panelStyle);
        }

        private Color ColourFor(double gain)
        {
            if (gain > 0 && Math.Abs(gain - _bestGain) < 0.001)
                return new Color(1f, 0.84f, 0.2f);
            if (gain > 0)
                return new Color(0.4f, 1f, 0.4f);
            if (gain < 0)
                return new Color(1f, 0.45f, 0.4f);
            return new Color(0.85f, 0.85f, 0.85f);
        }

        private static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        private static string NameOf(Building b) =>
            Framework.ScriptableObjectSingleton<StringLookup>.Instance.GetNameForTag(b.Tag, out string name) ? name : b.Tag.ToString();

        // Whole numbers from 10 up keep the labels short.
        private static string Format(double v) => Math.Abs(v) >= 10 || Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.0");
    }
}
