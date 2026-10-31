using System.Collections.Generic;
using System.Linq;
using Entities;
using Entities.ItemBehaviours;
using Framework;
using GameState.Data;
using HarmonyLib;
using Interaction;
using Library;
using UI;
using UI.UiTextUtils;

namespace CombolandsTweaks
{
    // Adds to an heirloom's info popup (shop, pack pick or owned) how many of that heirloom you already have, and how
    // many buildings on the map are in the categories its description mentions.
    [HarmonyPatch(typeof(GameTagInfoPanel), "Populate")]
    internal static class HeirloomCounts
    {
        private static void Prefix(ref StringsWithContextHolder description, GameTag gameTag, GameItemType itemType)
        {
            if (!Plugin.HeirloomCountsEnabled.Value || description == null || gameTag == GameTag.None)
                return;
            // Owned heirlooms are shown with item type None, so also go by whether the tag is an item.
            if (itemType != GameItemType.Heirloom && (ItemBehaviours.Instance == null || ItemBehaviours.Instance.GetBehaviourFor(gameTag) == null))
                return;
            if (!MonoSingleton<ItemHeirloomController>.HasInstance)
                return;

            try
            {
                int owned = MonoSingleton<ItemHeirloomController>.Instance.GetCountOfItemWithTagEquippedIncludingExtraPanels(gameTag);

                // Work on a copy: the description can be data the game reuses.
                var copy = new StringsWithContextHolder();
                copy.AppendOtherStringHolder(description);
                copy.AppendString(default(Newline));
                AppendWords(copy, $"You have {owned}");

                HashSet<GamePieceCategory> categories = description.GetMetadataCategories();
                categories.Remove(GamePieceCategory.Wild);
                if (categories.Count > 0 && MonoSingleton<BuildingController>.HasInstance)
                {
                    foreach (GamePieceCategory category in categories)
                    {
                        int count = MonoSingleton<BuildingController>.Instance.Buildings.Count(b => b != null && b.Categories.ContainsCategory(category));
                        string name = ScriptableObjectSingleton<StringLookup>.Instance.GetNameForCategory(category, out string n) ? n : category.ToString();
                        copy.AppendString(default(Newline));
                        AppendWords(copy, count.ToString());
                        copy.AppendString(new StringWithCategory { String = name, Category = category });
                        AppendWords(copy, count == 1 ? "building on the map" : "buildings on the map");
                    }
                }
                description = copy;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Heirloom counts for {gameTag} failed: {e}");
            }
        }

        // The game lays descriptions out word by word, so add words the same way.
        private static void AppendWords(StringsWithContextHolder holder, string text)
        {
            foreach (string word in text.Split(' '))
                holder.AppendString(new StringNoAdditionalContext { String = word });
        }
    }
}
