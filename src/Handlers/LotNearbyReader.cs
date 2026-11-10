using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>On-demand nearby landmarks in the cursor's fixed lot axes.</summary>
    internal static class LotNearbyReader
    {
        internal static string Describe(ulong lot, int layer, Vector3 point)
        {
            if (!BuildModeRefreshManager.IsZoneStateRefreshed || ZoneManager.Instance?.DirtyWallZones.Count > 0)
                return Loc.Get("layout_updating");
            if (!PlacementGridReader.TryRead(lot, ItemPlacementHandler.GridSpacingMetres, out var grid))
                return Loc.Get("placement_grid_unavailable");
            var items = ItemManager.Instance.GetLotItems(lot).Where(i => i != null && i.gameObject.activeInHierarchy
                && i.LotPlacedOnGUID == lot && i.Layer == layer && i.CharacterHoldingGUID == 0
                && i != PlayerManager.Instance?.HybridPlayer1?.Player?.ItemInPlacement?.Item)
                .Select(i => new { Item = i, Distance = HorizontalDistance(point, i.CubeTransform.LocalPosition) })
                .Where(i => i.Distance <= 4f).OrderBy(i => i.Distance).ThenBy(i => i.Item.InstanceID).ToList();
            if (items.Count == 0) return Loc.Get("nearby_empty");
            var chosen = items.Take(8).ToList();
            var brief = chosen.Select(i => ItemDescription.GetBriefIdentity(i.Item) ?? Loc.Get("item_unknown")).ToList();
            var parts = new List<string> { items.Count == 1 ? Loc.Get("nearby_intro_one")
                : items.Count <= 8 ? Loc.Get("nearby_intro_all", items.Count) : Loc.Get("nearby_intro", items.Count, 8) };
            string area = null;
            for (int n = 0; n < chosen.Count; n++)
            {
                var item = chosen[n].Item;
                string nextArea = PlacementLocationReader.AreaName(lot, layer, item.CubeTransform.LocalPosition);
                if (nextArea != area) { parts.Add(nextArea); area = nextArea; }
                string name = brief.Count(b => b == brief[n]) > 1 ? ItemDescription.GetProductIdentity(item) : brief[n];
                if (item.GetComponent<ItemObjectDoor>() != null) name = Loc.Get("nearby_door") + ": " + name;
                parts.Add(Loc.Get("nearby_entry", name, Offset(grid, point, item.CubeTransform.LocalPosition)));
            }
            return string.Join(". ", parts);
        }

        internal static float HorizontalDistance(Vector3 a, Vector3 b)
            => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        internal static string Offset(PlacementGrid grid, Vector3 from, Vector3 to)
        {
            grid.Coordinates(from.x, from.z, out double a, out double b);
            grid.Coordinates(to.x, to.z, out double x, out double z);
            double dx = (x - a) * grid.Spacing, dz = (z - b) * grid.Spacing;
            string horizontal = Math.Abs(dx) < .05 ? "" : Loc.Get(dx > 0 ? "target_right" : "target_left", PlacementGridReader.Length((float)Math.Abs(dx)));
            string depth = Math.Abs(dz) < .05 ? "" : Loc.Get(dz > 0 ? "target_back" : "target_front", PlacementGridReader.Length((float)Math.Abs(dz)));
            return horizontal.Length + depth.Length == 0 ? Loc.Get("target_same_position")
                : string.Join(", ", new[] { horizontal, depth }.Where(s => s.Length > 0));
        }
    }
}
