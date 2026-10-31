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
        public const string Version = "0.2.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ScoreOverlayEnabled;
        internal static ConfigEntry<KeyboardShortcut> ScoreOverlayKey;

        private Harmony _harmony;
        private GameObject _host;

        private void Awake()
        {
            Log = Logger;

            ScoreOverlayEnabled = Config.Bind("Placement scores", "Enabled", true,
                "Show on every tile how many points the hovered or currently placing building would score there.");
            ScoreOverlayKey = Config.Bind("Placement scores", "ToggleKey", new KeyboardShortcut(KeyCode.F7),
                "Turns the placement score overlay on or off.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            _host = new GameObject("CombolandsTweaks");
            DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<PlacementScores>();

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
