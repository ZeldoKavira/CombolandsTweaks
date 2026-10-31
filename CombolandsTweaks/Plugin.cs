using BepInEx;
using BepInEx.Logging;
using GameState;
using HarmonyLib;

namespace CombolandsTweaks
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "zeldo.combolands.tweaks";
        public const string Name = "Combolands Tweaks";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        // ScriptEngine destroys the plugin on hot reload; undo everything Awake did so the new copy starts clean.
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
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
