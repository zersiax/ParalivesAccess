using System.Reflection;
using ParalivesAccess.Speech;
using Setting;

namespace ParalivesAccess.Handlers
{
    /// <summary>Reads the native discriminant before interpreting pooled catalogue row data.</summary>
    internal static class CatalogEntryReader
    {
        private static readonly FieldInfo TypeField = typeof(UIBuildModeItem).GetField("_type", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SurfaceField = typeof(UIBuildModeItem).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static UIBuildModeItemTypes TypeOf(UIBuildModeItem item)
        {
            if (TypeField == null || TypeField.FieldType != typeof(UIBuildModeItemTypes))
                throw new System.MissingFieldException(typeof(UIBuildModeItem).FullName, "_type");
            return item == null ? UIBuildModeItemTypes.None : (UIBuildModeItemTypes)TypeField.GetValue(item);
        }

        internal static bool IsOrdinaryItem(UIBuildModeItem item) => item != null
            && TypeOf(item) == UIBuildModeItemTypes.Items && item.ItemData != null;

        internal static Surface SurfaceOf(UIBuildModeItem item)
        {
            if (TypeOf(item) != UIBuildModeItemTypes.Surfaces) return null;
            if (SurfaceField == null || SurfaceField.FieldType != typeof(Surface))
                throw new System.MissingFieldException(typeof(UIBuildModeItem).FullName, "_surface");
            return (Surface)SurfaceField.GetValue(item);
        }

        internal static string SurfaceName(ulong guid)
        {
            var surface = Settings.Get<Surfaces>()?.GetSurfaceByGUID(guid);
            if (surface == null) return Loc.Get("surface_unknown");
            string translated = TranslationManager.Get(surface.TranslationKey);
            return ScreenReader.NormalizeForOutput(string.IsNullOrEmpty(translated) || translated == surface.TranslationKey
                || translated == surface.DisplayName ? ReadableSurfaceName(surface.DisplayName) : translated);
        }

        internal static string ReadableSurfaceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Loc.Get("surface_unknown");
            // Prefer native localization above. The fallback uses the game's existing word
            // splitter and removes asset prefixes from missing English surface labels.
            foreach (string prefix in new[] { "FloorWoodHard", "FloorWoodSoft" })
                if (name.StartsWith(prefix, System.StringComparison.Ordinal) && name.Length > prefix.Length)
                    return Loc.Get("surface_flooring", FormatService.AddSpacesBetweenUpperCamelCaseWords(name.Substring(prefix.Length), false));
            bool floor = name.StartsWith("Floor", System.StringComparison.Ordinal) && name.Length > 5;
            foreach (string prefix in new[] { "Floor", "Ceiling", "Wall", "Roof" })
                if (name.StartsWith(prefix, System.StringComparison.Ordinal) && name.Length > prefix.Length)
                { name = name.Substring(prefix.Length); break; }
            string words = FormatService.AddSpacesBetweenUpperCamelCaseWords(name, false);
            return floor ? Loc.Get("surface_flooring", words) : words;
        }
    }
}
