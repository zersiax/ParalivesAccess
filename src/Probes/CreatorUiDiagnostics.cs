using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParalivesAccess.Handlers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ParalivesAccess.Probes
{
    /// <summary>Read-only diagnostics for intermittent creator exits and household-library focus.</summary>
    internal static class CreatorUiDiagnostics
    {
        private static readonly Dictionary<int, int> InventorySizes = new Dictionary<int, int>();
        private static bool Enabled => MenuNavigationProbe.Enabled;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                foreach (string method in new[] { "Cancel", "ValidateAndExitCharacterCreator", "CancelChanges", "LeaveCharacterCreator" })
                    Patch(harmony, typeof(UICharacterCreator), method, nameof(CreatorAction));
                Patch(harmony, typeof(UIPremadeHouseholdSelection), "RefreshList", nameof(LibraryRefresh), postfix: true);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Creator diagnostics hook failed: " + ex);
                return false;
            }
        }

        private static void Patch(Harmony harmony, Type type, string method, string callback, bool postfix = false)
        {
            MethodInfo target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.Name, method);
            var hook = new HarmonyMethod(typeof(CreatorUiDiagnostics), callback);
            if (postfix) harmony.Patch(target, postfix: hook);
            else harmony.Patch(target, prefix: hook);
            if (!new List<MethodBase>(harmony.GetPatchedMethods()).Contains(target))
                throw new InvalidOperationException("Unapplied diagnostic: " + method);
            Plugin.Log.LogInfo("[CreatorAudit] Patched " + type.Name + "." + method);
        }

        private static void CreatorAction(UICharacterCreator __instance, MethodBase __originalMethod)
        {
            if (!Enabled) return;
            try
            {
                AssetHousehold household = HouseholdManager.Instance?.CurrentHousehold;
                Plugin.Log.LogInfo($"[CreatorAudit] frame={Time.frameCount} native={__originalMethod.Name} "
                    + $"temporary={household?.IsTemporaryHousehold} savedOnce={household?.Data?.WasSavedOnce} "
                    + $"creatorVisible={__instance.IsVisible} {Context()}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Creator action diagnostic failed: " + ex); }
        }

        private static void LibraryRefresh(UIPremadeHouseholdSelection __instance)
        {
            if (!Enabled) return;
            try
            {
                InventorySizes.Remove(__instance.GetInstanceID());
                Plugin.Log.LogInfo($"[LibraryAudit] frame={Time.frameCount} native RefreshList; "
                    + $"rows={__instance.PremadeHouseholdList?.CurrentItems?.Count}");
                Inventory(__instance);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Library refresh diagnostic failed: " + ex); }
        }

        private static UIWindow Library(Component source)
        {
            if (source == null) return null;
            UIPremadeHouseholdSelection library = source.GetComponentInParent<UIPremadeHouseholdSelection>();
            return library != null ? (UIWindow)library : source.GetComponentInParent<UITrainSelectStartingHousehold>();
        }

        private static string Identify(Component source)
        {
            if (source == null) return "none";
            FocusableGroup group = source.GetComponentInParent<FocusableGroup>();
            return $"{source.GetType().Name}:{source.name}#{source.GetInstanceID()} "
                + $"group={group?.name}#{(group != null ? group.GetInstanceID() : 0)}";
        }

        private static string Context()
        {
            HybridPlayer player = PlayerManager.Instance?.HybridPlayer1;
            GameObject selected = EventSystem.current?.currentSelectedGameObject;
            return $"cursor=[{Identify(MenuNavigationHandler.Cursor)}] "
                + $"nativeFocused=[{Identify(MenuNavigationHandler.CursorGroup?.GetFocused(false))}] "
                + $"common={HybridReferences.Instance?.UIManagerCommon?.CurrentWindow?.GetType().Name} "
                + $"playerWindow={player?.UIManager?.CurrentWindow?.GetType().Name} "
                + $"textEntry={player?.HasInputFieldFocused} selected={selected?.name}";
        }

        internal static void Activation(Focusable target, string phase)
        {
            if (!Enabled || target == null) return;
            try
            {
                bool creator = target.GetComponentInParent<UICharacterCreator>() != null
                    || (target.GetComponentInParent<UIPrompt>() != null
                        && PlayerManager.Instance?.HybridPlayer1?.Player?.State == GameStates.CharacterCreator);
                UIWindow library = Library(target);
                if (!creator && library == null) return;
                var wiring = new List<string>();
                ParaButton button = target as ParaButton;
                if (button?.ButtonClick != null)
                    for (int i = 0; i < button.ButtonClick.GetPersistentEventCount(); i++)
                        wiring.Add(button.ButtonClick.GetPersistentTarget(i)?.GetType().Name + "."
                            + button.ButtonClick.GetPersistentMethodName(i));
                Plugin.Log.LogInfo($"[CreatorAudit] frame={Time.frameCount} {phase} "
                    + $"target=[{Identify(target)}] label='{UiLabelReader.GetLabelOrFallback(target)}' "
                    + $"wiring=[{string.Join(", ", wiring)}] canceled={target.IsClickCanceled} {Context()}");
                if (library != null) Inventory(library);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Creator activation diagnostic failed: " + ex); }
        }

        internal static void Focus(Focusable target, string reason)
        {
            if (!Enabled || target == null) return;
            try
            {
                UIWindow library = Library(target) ?? Library(MenuNavigationHandler.Cursor);
                if (library == null || !library.IsVisible) return;
                Plugin.Log.LogInfo($"[LibraryAudit] frame={Time.frameCount} {reason} "
                    + $"target=[{Identify(target)}] position={target.transform.position} "
                    + $"skipped={FocusNavigator.ShouldSkip(target)} mouseDelta={Mouse.current?.delta.ReadValue()} {Context()}");
                Inventory(library);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Library focus diagnostic failed: " + ex); }
        }

        private static void Inventory(UIWindow library)
        {
            Focusable[] controls = library.GetComponentsInChildren<Focusable>(false);
            int id = library.GetInstanceID();
            if (InventorySizes.TryGetValue(id, out int oldCount) && oldCount == controls.Length) return;
            InventorySizes[id] = controls.Length;
            Plugin.Log.LogInfo($"[LibraryAudit] inventory {library.GetType().Name} controls={controls.Length} "
                + $"blocksActions={library.BlockPlayerActionWhenShown} hidesOthers={library.HideOtherInterfacesWhenShown}");
            for (int i = 0; i < Math.Min(controls.Length, 80); i++)
            {
                Focusable control = controls[i];
                UIListItemBase item = control as UIListItemBase;
                Plugin.Log.LogInfo($"[LibraryAudit] item {i}: [{Identify(control)}] "
                    + $"position={control.transform.position} focused={control.IsFocused} "
                    + $"skipped={FocusNavigator.ShouldSkip(control)} parentList={item?.ParentUIList?.name} "
                    + $"label='{UiLabelReader.GetLabelOrFallback(control)}'");
            }
            if (controls.Length > 80) Plugin.Log.LogInfo("[LibraryAudit] Remaining inventory omitted after 80 controls.");
        }
    }
}
