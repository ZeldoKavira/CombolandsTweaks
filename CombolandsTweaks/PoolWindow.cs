using System.Collections.Generic;
using System.Linq;
using Entities;
using Framework;
using GameState;
using GameState.Data;
using HarmonyLib;
using Interaction;
using Library;
using UnityEngine;

namespace CombolandsTweaks
{
    // F10 window: the buildings that can still come up in the card draw and each one's chance per card, worked out
    // the way BuildingPool.GetRoll picks (draftable minus dismissed, weighted by GetRollChanceFor).
    internal class PoolWindow : MonoBehaviour
    {
        private const int WindowId = 0x434c5450; // "CLTP"
        private const float RefreshInterval = 1f;

        private Rect _rect = new Rect(400f, 120f, 320f, 420f);
        private Vector2 _scroll;
        private float _nextRefresh;
        private readonly List<(string name, Rarity rarity, float chance, bool seen)> _rows = new List<(string, Rarity, float, bool)>();
        private readonly Dictionary<Rarity, (int count, float chance)> _byRarity = new Dictionary<Rarity, (int, float)>();

        private static readonly System.Reflection.MethodInfo RefreshPool = AccessTools.Method(typeof(BuildingPool), "RefreshPool");

        internal static bool Open;
        internal static Rect ScreenRect;

        private void Update()
        {
            if (Plugin.PoolKey.Value.IsDown())
                Open = !Open;
            if (Open && Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                Collect();
            }
        }

        private void Collect()
        {
            _rows.Clear();
            _byRarity.Clear();
            if (!SerializedMonoSingleton<BuildingPool>.HasInstance || Entities.BuildingBehaviours.BuildingBehaviours.Instance == null)
                return;
            BuildingPool pool = SerializedMonoSingleton<BuildingPool>.Instance;
            try
            {
                // The same refresh GetRoll does before drawing; it only rebuilds the draftable set.
                RefreshPool?.Invoke(pool, null);

                bool rainbow = MonoSingleton<ItemHeirloomController>.HasInstance && MonoSingleton<ItemHeirloomController>.Instance.IsItemWithTagEquipped(GameTag.RainbowFlag);
                IEnumerable<GameTag> source = rainbow ? pool.GetAllInPool : pool.GetCurrentlyDraftable;
                HashSet<GameTag> dismissed = pool.DismissedTags;
                HashSet<GameTag> seen = pool.GetAllSeen ?? new HashSet<GameTag>();
                var behaviours = Entities.BuildingBehaviours.BuildingBehaviours.Instance;

                var weights = new List<(GameTag tag, float weight)>();
                foreach (GameTag tag in source)
                {
                    if (dismissed.Contains(tag))
                        continue;
                    float w = behaviours.GetRollChanceFor(tag);
                    if (w > 0f)
                        weights.Add((tag, w));
                }
                float total = weights.Sum(w => w.weight);
                if (total <= 0f)
                    return;

                foreach ((GameTag tag, float weight) in weights.OrderByDescending(w => w.weight))
                {
                    string name = ScriptableObjectSingleton<StringLookup>.Instance.GetNameForTag(tag, out string n) ? n : tag.ToString();
                    Rarity rarity = behaviours.GetRarityFor(tag);
                    float chance = weight / total;
                    _rows.Add((name, rarity, chance, seen.Contains(tag)));
                    _byRarity.TryGetValue(rarity, out var agg);
                    _byRarity[rarity] = (agg.count + 1, agg.chance + chance);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Reading the draw pool failed: {e}");
                Open = false;
            }
        }

        private void OnGUI()
        {
            if (!Open)
            {
                ScreenRect = Rect.zero;
                return;
            }
            _rect = GUILayout.Window(WindowId, _rect, Draw, "Draw pool", GUILayout.Width(320f), GUILayout.Height(420f));
            ScreenRect = _rect;
        }

        private void Draw(int id)
        {
            if (_rows.Count == 0)
            {
                GUILayout.Label("Nothing to draw right now (not in a run?).");
            }
            else
            {
                GUILayout.Label($"{_rows.Count} buildings can come up. Chance per card:");
                foreach (var r in _byRarity.OrderBy(r => r.Key))
                    GUILayout.Label($"  {r.Key}: {r.Value.count} buildings, {r.Value.chance:P0}");
                GUILayout.Space(4f);
                _scroll = GUILayout.BeginScrollView(_scroll);
                foreach (var row in _rows)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(row.seen ? row.name : row.name + "  (new)", GUILayout.Width(150f));
                    GUILayout.Label(row.rarity.ToString(), GUILayout.Width(80f));
                    GUILayout.Label(row.chance.ToString("P1"));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Plugin.PoolKey.Value} toggles this window");
            if (GUILayout.Button("Close", GUILayout.Width(70f)))
                Open = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }
}
