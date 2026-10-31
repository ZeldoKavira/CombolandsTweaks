using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameState;
using HarmonyLib;
using UnityEngine;

namespace CombolandsTweaks
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "zeldo.combolands.tweaks";
        public const string Name = "Combolands Tweaks";
        public const string Version = "0.3.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> MenuKey;

        internal static ConfigEntry<bool> ScoreOverlayEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScoreOverlayKey;
        internal static ConfigEntry<bool> ScoreCountNearby;
        internal static ConfigEntry<bool> ScoreShowBreakdown;
        internal static ConfigEntry<float> ScoreTextScale;

        private Harmony _harmony;
        private GameObject _host;

        private void Awake()
        {
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F8), "Opens the settings window.");

            ScoreOverlayEnabled = Config.Bind("Placement scores", "Enabled", true,
                "Show on every tile how many points the hovered or currently placing building would score there.");
            ScoreOverlayKey = Config.Bind("Placement scores", "ToggleKey", new KeyboardShortcut(KeyCode.F7),
                "Turns the placement score overlay on or off.");
            ScoreCountNearby = Config.Bind("Placement scores", "IncludeNearbyBonus", true,
                "Add what the placement adds to existing buildings' scores (e.g. Flowers next to an Apiary).");
            ScoreShowBreakdown = Config.Bind("Placement scores", "ShowBreakdown", true,
                "Show the hovered tile's own / nearby split next to the cursor.");
            ScoreTextScale = Config.Bind("Placement scores", "TextScale", 1f,
                new ConfigDescription("Size of the numbers on the tiles.", new AcceptableValueRange<float>(0.5f, 2f)));

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            _host = new GameObject("CombolandsTweaks");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<PlacementScores>();
            _host.AddComponent<SettingsWindow>();

            Log.LogInfo($"{Name} {Version} loaded");
        }

        // ScriptEngine destroys the plugin on hot reload; undo everything Awake did so the new copy starts clean.
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            if (_host != null)
                Destroy(_host);
        }
    }

    // Proves patching works: logs each new week of a run.
    [HarmonyPatch(typeof(GameController), "StartWeek")]
    internal static class WeekLog
    {
        private static void Postfix(GameController __instance)
        {
            Plugin.Log.LogInfo($"Week {__instance.WeeksSoFar + 1}/{__instance.WeeksAllowed}, target {__instance.ScoreRequired}");
        }
    }
}
