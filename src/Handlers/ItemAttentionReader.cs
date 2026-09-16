using System.Collections.Generic;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads explicit native problem states without predicting failures or repairs.</summary>
    internal static class ItemAttentionReader
    {
        internal static string Describe(ItemObjectRoot root)
        {
            if (root == null) return null;
            var parts = new List<string>();
            ItemStates states = Settings.Get<ItemStates>();
            if (states != null)
            {
                Add(parts, root, states.ItemStateOnFire, "attention_fire");
                Add(parts, root, states.ItemStateBroken, "attention_broken");
                Add(parts, root, states.ItemStateBurnt, "attention_burnt");
                // UpdateItemMaterial renders partial dirt only when the native state rule has
                // enabled its overlay. Clean actions can appear before the state reaches 1.
                if (states.ItemStateDirty != 0UL && root.StateValues != null
                    && root.StateValues.TryGetValue(states.ItemStateDirty, out float dirt)
                    && (dirt >= 1f || (dirt > 0f && root.CanSetAlphaDirty)))
                    parts.Add(Loc.Get("attention_dirty"));
                Add(parts, root, states.ItemStateSpoiledFood, "attention_spoiled");
            }
            // The broken-appliance spawner uses this catalogue reference. Decorative puddles
            // and dirt decals are deliberately not inferred from their product names.
            ulong puddle = Settings.Get<ItemBrokenness>()?.WaterPuddleItemGUID ?? 0UL;
            if (puddle != 0UL && root.CatalogueGUID == puddle)
                parts.Add(Loc.Get("attention_puddle"));
            // FireManager spawns this item; its native catalogue group is Clean up soot.
            ulong fireSpot = Settings.Get<Fire>()?.FireItemGUID ?? 0UL;
            if (fireSpot != 0UL && root.CatalogueGUID == fireSpot && !root.IsOnFire)
                parts.Add(Loc.Get("attention_soot"));
            return parts.Count == 0 ? null : string.Join(Loc.Get("lot_detail_separator"), parts);
        }

        private static void Add(List<string> parts, ItemObjectRoot root, ulong state, string key)
        {
            // ItemObjectRoot.HasState uses >= 1, not the mere presence of a state entry.
            if (state != 0UL && root.StateValues != null
                && root.StateValues.TryGetValue(state, out float value) && value >= 1f)
                parts.Add(Loc.Get(key));
        }
    }
}
