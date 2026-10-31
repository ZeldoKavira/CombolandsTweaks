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
                    // Buildings without a countdown only score when something else triggers them, so a per-turn
                    // figure doesn't exist for them.
                    if (!row.PerTurn)
                        continue;
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
                DrawFitted(new Rect(box.x, box.y + bigH, box.width, smallH), $"{Format(r.Now)}→{Format(r.After)}", _smallStyle, smallH);
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
            top.RemoveAll(e => e.Key == null || e.Value.After - e.Value.Now <= 0);
            if (top.Count == 0)
                return;
            top.Sort((a, b) => (b.Value.After - b.Value.Now).CompareTo(a.Value.After - a.Value.Now));
            var sb = new System.Text.StringBuilder("Best buildings for this upgrade (points per turn)\n");
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
            _content.text = $"{NameOf(b)}\n{Format(r.Now)} → <color={Hex(ColourFor(gain))}>{Format(r.After)}</color> points per turn (activates every {b.Cooldown} turns)";
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
