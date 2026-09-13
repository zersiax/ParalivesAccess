using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Exposes the native creator portrait Delete buttons and their keyboard row.</summary>
    internal static class CreatorHouseholdHandler
    {
        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                bool applied = true;
                foreach (string name in new[] { "UpdateItem", "OnHoverEnd" })
                {
                    MethodInfo method = AccessTools.DeclaredMethod(typeof(UICharacterItem), name);
                    if (method == null) throw new MissingMethodException("UICharacterItem." + name);
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(CreatorHouseholdHandler), nameof(RefreshDeleteButton)));
                    bool found = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(method);
                    Plugin.Log.LogInfo($"[CreatorHousehold] Patched UICharacterItem.{name}, applied={found}.");
                    applied &= found;
                }
                MethodInfo click = AccessTools.DeclaredMethod(typeof(UICharacterItem), "DeleteButtonClick");
                if (click == null) throw new MissingMethodException("UICharacterItem.DeleteButtonClick");
                harmony.Patch(click, prefix: new HarmonyMethod(typeof(CreatorHouseholdHandler), nameof(BeforeDelete)));
                bool clickApplied = new List<MethodBase>(harmony.GetPatchedMethods()).Contains(click);
                Plugin.Log.LogInfo($"[CreatorHousehold] Patched UICharacterItem.DeleteButtonClick, applied={clickApplied}.");
                applied &= clickApplied;
                return applied;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Creator household hooks failed: " + ex);
                return false;
            }
        }

        internal static bool CanDelete(UICharacterItem item)
        {
            if (item?.Character?.Data == null || item.CharacterGUID != item.Character.GUID
                || item.DeleteButton == null || !item.gameObject.activeInHierarchy)
                return false;
            Player player = PlayerManager.Instance?.GetHybridPlayer(item.PlayerIndex)?.Player;
            UICharacters bar = item.GetComponentInParent<UICharacters>();
            if (player == null || player.State != GameStates.CharacterCreator
                || player.LimitedCharacterCreatorAccess != 0L || UI.Instance == null
                || bar == null || !bar.IsVisible || bar.IsInTargetCharacterSelectionMode
                || bar.List?.CurrentItems == null || !bar.List.CurrentItems.Contains(item))
                return false;
            // UICharacterItem.OnHoverStart owns these eligibility rules. Only the hover
            // prerequisite is removed; DeleteButtonClick still owns the native Yes/No prompt.
            return CharacterManager.Instance != null
                && CharacterManager.Instance.GetNumberOfCharactersInHousehold() > 1
                && HouseholdManager.Instance?.CurrentHousehold?.Data?.Characters?.Contains(item.CharacterGUID) == true
                && UI.GetOrNull<UICreateCharacterFromGenetics>(item.PlayerIndex)?.IsVisible != true;
        }

        private static void RefreshDeleteButton(UICharacterItem __instance)
        {
            try
            {
                if (__instance?.DeleteButton != null)
                    __instance.DeleteButton.SetActive(CanDelete(__instance));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Refreshing creator Delete button failed: " + ex);
            }
        }

        private static bool BeforeDelete(UICharacterItem __instance)
        {
            try
            {
                bool allowed = CanDelete(__instance);
                Plugin.Log.LogInfo($"[CreatorHousehold] Delete prompt character={__instance?.CharacterGUID}, allowed={allowed}.");
                if (!allowed) ScreenReader.Speak(Loc.Get("explorer_action_unavailable"));
                return allowed;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Checking creator Delete action failed: " + ex);
                ScreenReader.Speak(Loc.Get("explorer_action_unavailable"));
                return false;
            }
        }

        internal static Focusable GetEntry(FocusableGroup group, IList<Focusable> registered)
        {
            UICharacters bar = group as UICharacters;
            Player player = bar != null ? PlayerManager.Instance?.GetHybridPlayer(bar.PlayerOwnerIndex)?.Player : null;
            if (bar == null || !bar.IsVisible || bar.List?.CurrentItems == null
                || player?.State != GameStates.CharacterCreator) return null;
            Focusable first = null;
            foreach (UIListItemBase row in bar.List.CurrentItems)
            {
                if (!(row is UICharacterItem item) || !Available(item, registered)) continue;
                if (player.SelectedCharactersGUID.Contains(item.CharacterGUID)) return item;
                if (first == null) first = item;
            }
            return first ?? Button(bar.AddCharacterButton, registered);
        }

        internal static string GetDeleteLabel(Component source)
        {
            UICharacterItem item = source?.GetComponentInParent<UICharacterItem>();
            if (item?.DeleteButton == null || !source.transform.IsChildOf(item.DeleteButton.transform))
                return null;
            string name = ScreenReader.NormalizeForOutput(item.Character?.Data?.FullName);
            return Loc.Get("creator_delete_para", string.IsNullOrWhiteSpace(name)
                ? Loc.Get("state_unknown_para") : name);
        }

        private static bool Available(Focusable control, IList<Focusable> registered)
        {
            return control != null && registered != null && registered.Contains(control)
                && control.gameObject.activeInHierarchy && !FocusNavigator.ShouldSkip(control);
        }

        private static Focusable Button(GameObject root, IList<Focusable> registered)
        {
            if (root == null || registered == null) return null;
            foreach (Focusable control in registered)
                if (Available(control, registered) && control.transform.IsChildOf(root.transform))
                    return control;
            return null;
        }

        internal static bool TryNavigate(FocusableGroup group, IList<Focusable> registered,
            Focusable source, Vector2 direction, out Focusable target)
        {
            target = null;
            UICharacters bar = group as UICharacters;
            if (bar == null || !bar.IsVisible || bar.List?.CurrentItems == null || source == null
                || PlayerManager.Instance?.GetHybridPlayer(bar.PlayerOwnerIndex)?.Player?.State != GameStates.CharacterCreator)
                return false;

            var portraits = new List<Focusable>();
            foreach (UIListItemBase row in bar.List.CurrentItems)
                if (row is UICharacterItem && Available(row, registered)) portraits.Add(row);
            Focusable add = Button(bar.AddCharacterButton, registered);
            if (add != null) portraits.Add(add);

            UICharacterItem owner = source.GetComponentInParent<UICharacterItem>();
            int index = portraits.IndexOf(owner != null ? owner : source);
            if (index < 0) return false;
            target = source;
            if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y))
            {
                int next = index + (direction.x > 0 ? 1 : -1);
                if (next >= 0 && next < portraits.Count) target = portraits[next];
            }
            else if (direction.y < 0 && owner != null && CanDelete(owner))
            {
                target = Button(owner.DeleteButton, registered) ?? source;
            }
            else if (direction.y > 0 && owner != null)
            {
                target = owner;
            }
            return true;
        }
    }
}
