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
        public const string Version = "0.12.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> MenuKey;

        internal static ConfigEntry<bool> ScoreOverlayEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScoreOverlayKey;
        internal static ConfigEntry<bool> ScoreCountNearby;
        internal static ConfigEntry<bool> ScorePerTurn;
        internal static ConfigEntry<bool> ScorePlanMultiple;
        internal static ConfigEntry<bool> ScoreShowBreakdown;
        internal static ConfigEntry<bool> ScoreCardBadges;
        internal static ConfigEntry<int> ScoreMarkBest;
        internal static ConfigEntry<KeyboardShortcut> CycleBestKey;
        internal static ConfigEntry<float> ScoreTextScale;

        internal static ConfigEntry<bool> BuildingScoresEnabled;
        internal static ConfigEntry<KeyboardShortcut> BuildingScoresKey;

        internal static ConfigEntry<bool> UpgradePreviewEnabled;
        internal static ConfigEntry<bool> HeirloomCountsEnabled;

        internal static ConfigEntry<float> SpeedBoost;

        internal static ConfigEntry<KeyboardShortcut> PoolKey;

        private Harmony _harmony;
        private GameObject _host;

        private void Awake()
        {
            Log = Logger;

            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F8), "Opens the settings window.");

            ScoreOverlayEnabled = Config.Bind("Placement scores", "Enabled", true,
                "Show on every tile how many points the hovered or currently placing building would score there.");
            // Not F7: the game reserves it as a restart key (InputKeys.RestartKeyDown).
            ScoreOverlayKey = Config.Bind("Placement scores", "OverlayKey", new KeyboardShortcut(KeyCode.F9),
                "Turns the placement score overlay on or off.");
            ScoreCountNearby = Config.Bind("Placement scores", "IncludeNearbyBonus", true,
                "Add what the placement adds to existing buildings' scores (e.g. Flowers next to an Apiary).");
            ScorePerTurn = Config.Bind("Placement scores", "PerTurn", true,
                "Score tiles in average points per turn (counts cooldowns, e.g. a Lamppost speeding up its neighbours) instead of points per activation.");
            ScorePlanMultiple = Config.Bind("Placement scores", "PlanMultiple", true,
                "For cards that place several copies, plan the best set of tiles (each copy scored with the earlier ones in place).");
            ScoreShowBreakdown = Config.Bind("Placement scores", "ShowBreakdown", true,
                "Show the hovered tile's own / nearby split next to the cursor.");
            ScoreCardBadges = Config.Bind("Placement scores", "CardBadges", true,
                "Show each card's best possible score above it in the bottom bar.");
            ScoreMarkBest = Config.Bind("Placement scores", "MarkBestTiles", 3,
                new ConfigDescription("How many of the best tiles get a gold frame (0 = none).", new AcceptableValueRange<int>(0, 3)));
            CycleBestKey = Config.Bind("Placement scores", "JumpToBestKey", new KeyboardShortcut(KeyCode.Tab),
                "While placing, moves the cursor to the best tile, then the next best.");
            ScoreTextScale = Config.Bind("Placement scores", "TextScale", 1f,
                new ConfigDescription("Size of the mod's numbers.", new AcceptableValueRange<float>(0.5f, 2f)));

            BuildingScoresEnabled = Config.Bind("Building scores", "Enabled", false,
                "Show on each placed building the points it scores when it activates.");
            BuildingScoresKey = Config.Bind("Building scores", "ToggleKey", new KeyboardShortcut(KeyCode.F11),
                "Turns the building score labels on or off.");

            UpgradePreviewEnabled = Config.Bind("Building upgrades", "Preview", true,
                "While a building upgrade is hovered or picked, show points per turn now and with it on every building it can go on.");

            HeirloomCountsEnabled = Config.Bind("Heirlooms", "ShowCounts", true,
                "Add to heirloom popups how many you have and how many buildings of its categories are on the map.");

            SpeedBoost = Config.Bind("Game speed", "SpeedBoost", 1f,
                new ConfigDescription("Extra speed on top of the game's own Game Speed setting.", new AcceptableValueRange<float>(1f, 5f)));

            PoolKey = Config.Bind("Draw pool", "ToggleKey", new KeyboardShortcut(KeyCode.F10),
                "Opens the list of buildings that can still come up in the card draw.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            _host = new GameObject("CombolandsTweaks");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<PlacementScores>();
            _host.AddComponent<BuildingScores>();
            _host.AddComponent<UpgradePreview>();
            _host.AddComponent<GameSpeed>();
            _host.AddComponent<PoolWindow>();
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
