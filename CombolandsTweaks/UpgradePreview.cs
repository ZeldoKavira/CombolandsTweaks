using System;
using System.Collections.Generic;
using Entities;
using HarmonyLib;
using Interaction;
using Interaction.InteractionStates;
using Library;
using UI;
using UI.Shop;
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
        private const float RefreshInterval = 0.5f;
        private const float FindInterval = 0.5f;

        private struct Row
        {
            public double Now;
            public double After;
            public bool PerTurn;
        }

        private readonly Dictionary<Building, Row> _rows = new Dictionary<Building, Row>();
        private double _bestGain;
        private GameTag _shownTag = GameTag.None;
        private float _nextRefresh;
        private bool _loggedError;

        private Consumable[] _consumables = Array.Empty<Consumable>();
        private ShopSingle[] _shopItems = Array.Empty<ShopSingle>();
        private float _nextFind;

        private GUIStyle _style;

        internal static bool Showing;

        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalRange = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localRangeChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, float> LocalMult = AccessTools.FieldRefAccess<GamePieceLocalValues, float>("_localMultiplierChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalCooldown = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localCooldownChanges");
        private static readonly AccessTools.FieldRef<GamePieceLocalValues, int> LocalActivations = AccessTools.FieldRefAccess<GamePieceLocalValues, int>("_localActivationCountChanges");

        private static bool IsStatUpgrade(GameTag tag) =>
            tag == GameTag.BsStatModMult || tag == GameTag.BsStatModCooldown || tag == GameTag.BsStatModRange || tag == GameTag.BsStatModActivationCount;

        private void Update()
        {
            GameTag tag = Plugin.UpgradePreviewEnabled.Value ? FindUpgrade() : GameTag.None;
            Showing = tag != GameTag.None && _rows.Count > 0;
            if (tag == GameTag.None)
            {
                _rows.Clear();
                _shownTag = GameTag.None;
                return;
            }
            if (tag == _shownTag && Time.unscaledTime < _nextRefresh)
                return;
            _shownTag = tag;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Compute(tag);
            Showing = _rows.Count > 0;
        }

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

            if (Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + FindInterval;
                _consumables = FindObjectsByType<Consumable>(FindObjectsSortMode.None);
                _shopItems = FindObjectsByType<ShopSingle>(FindObjectsSortMode.None);
            }
            foreach (Consumable c in _consumables)
                if (c != null && c.isActiveAndEnabled && IsStatUpgrade(c.GameTag) && IsMouseOver(c.transform as RectTransform))
                    return c.GameTag;
            foreach (ShopSingle s in _shopItems)
            {
                if (s == null || !s.isActiveAndEnabled || s.ItemType != GameItemType.BuildingUpgrade)
                    continue;
                GameTag t = Traverse.Create(s).Field<GameTag>("_tag").Value;
                if (IsStatUpgrade(t) && IsMouseOver(s.transform as RectTransform))
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

        private void Compute(GameTag tag)
        {
            _rows.Clear();
            _bestGain = 0;
            if (!MonoSingleton<BuildingController>.HasInstance)
                return;
            float amount = ConsumableBehaviour.GetBuildingSupplyParam(tag);
            BuildingExtensions.ResetCaches();
            foreach (Building b in MonoSingleton<BuildingController>.Instance.Buildings)
            {
                if (b == null || b.Values == null || !b.isActiveAndEnabled)
                    continue;
                try
                {
                    if (!ConsumableBehaviour.CanUseConsumableOn(tag, b.Tile))
                        continue;
                    if (tag == GameTag.BsStatModMult && !b.Tag.CanGetMult())
                        continue;
                    Row row = default;
                    row.PerTurn = b.GamePieceWithCount != null && b.Cooldown > 0;
                    row.Now = PointsPerTurn(b, row.PerTurn);
                    Apply(b, tag, amount);
                    try
                    {
                        PlacementScores.ForgetCaches(b);
                        row.After = PointsPerTurn(b, row.PerTurn);
                    }
                    finally
                    {
                        Apply(b, tag, -amount);
                        PlacementScores.ForgetCaches(b);
                    }
                    if (row.Now == 0 && row.After == 0)
                        continue;
                    _rows[b] = row;
                    if (row.After - row.Now > _bestGain)
                        _bestGain = row.After - row.Now;
                }
                catch (Exception e)
                {
                    if (!_loggedError)
                    {
                        _loggedError = true;
                        Plugin.Log.LogError($"Upgrade preview for {b.Tag} at {b.X},{b.Y} failed: {e}");
                    }
                }
            }
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

        // Points per activation (the game's preview) times activations, spread over the cooldown when it has one.
        private static double PointsPerTurn(Building b, bool perTurn)
        {
            double points = PlacementScores.PreviewTotal(b, 1.0) * Math.Max(1, b.Behaviour.GetActivationCount(b));
            return perTurn ? points / Math.Max(1, b.Cooldown) : points;
        }

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
            if (_style == null)
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, richText = true };
            _style.fontSize = Mathf.Clamp(Mathf.RoundToInt(tilePx * 0.22f * Plugin.ScoreTextScale.Value), 9, 40);

            foreach (KeyValuePair<Building, Row> entry in _rows)
            {
                Building b = entry.Key;
                if (b == null)
                    continue;
                Row r = entry.Value;
                double gain = r.After - r.Now;
                string colour = gain > 0 && Math.Abs(gain - _bestGain) < 0.001 ? "#FFD633" : gain > 0 ? "#66FF66" : gain < 0 ? "#FF7366" : "#DDDDDD";
                string text = $"{Format(r.Now)} → <color={colour}>{Format(r.After)}</color>{(r.PerTurn ? "/turn" : "/act")}";
                Vector2 size = _style.CalcSize(new GUIContent(text));
                Vector3 screen = cam.WorldToScreenPoint(new Vector3(b.X, b.Y + 0.55f));
                if (screen.z < 0f)
                    continue;
                GUI.Box(new Rect(screen.x - size.x / 2f - 3f, Screen.height - screen.y - size.y, size.x + 6f, size.y + 2f), text, _style);
            }
        }

        private static string Format(double v) => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.0");
    }
}
