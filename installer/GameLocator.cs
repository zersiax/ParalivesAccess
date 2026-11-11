using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ParalivesAccessInstaller
{
    internal static class GameLocator
    {
        internal static bool IsGame(string folder) => !string.IsNullOrWhiteSpace(folder)
            && File.Exists(Path.Combine(folder, "Paralives.exe"))
            && File.Exists(Path.Combine(folder, "Paralives_Data", "Managed", "Paralives.dll"));

        internal static bool Running() => Process.GetProcessesByName("Paralives").Any(p => { p.Dispose(); return true; });

        internal static string InstalledVersion(string folder)
        {
            string dll = Path.Combine(folder, "BepInEx", "plugins", "ParalivesAccess.dll");
            if (!File.Exists(dll)) return "Not installed";
            try { return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "Unknown version"; }
            catch (IOException) { return "Version unavailable"; }
        }

        internal static IEnumerable<string> Libraries(string steam)
        {
            yield return steam;
            string file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(file)) yield break;
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { yield break; } catch (UnauthorizedAccessException) { yield break; }
            foreach (Match match in Regex.Matches(text, "\"(?:path|[0-9]+)\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                string path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Path.IsPathRooted(path)) yield return path;
            }
        }

        internal static string ManifestFolder(string library)
        {
            string manifest = Path.Combine(library, "steamapps", "appmanifest_1118520.acf");
            string name = "Paralives";
            if (File.Exists(manifest))
            {
                var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (match.Success) name = match.Groups[1].Value;
            }
            string common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
            string target = Path.GetFullPath(Path.Combine(common, name));
            return target.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? target : null;
        }

        internal static string[] Find()
        {
            var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") };
            foreach (var location in new[] { new[] { "HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath" }, new[] { "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath" } })
            {
                string value = Registry.GetValue(location[0], location[1], null) as string;
                if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
            }
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string library in roots.Where(Directory.Exists).SelectMany(Libraries).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { string folder = ManifestFolder(library); if (IsGame(folder)) found.Add(folder); }
                catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            }
            return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
