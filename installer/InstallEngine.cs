using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace ParalivesAccessInstaller
{
    internal sealed class PackageFile
    {
        internal string Relative, Source, Hash;
    }
    internal sealed class InstallResult
    {
        internal int Changed, Unchanged;
        internal string BackupFolder;
    }
    internal static class InstallEngine
    {
        internal static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string SafeRelative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains("\\") || value.Contains(":") || value.StartsWith("/") || value.EndsWith("/"))
                throw new InvalidDataException("Unsafe installation path in package: " + value);
            var parts = value.Split('/');
            if (parts.Any(p => p == "" || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("Unsafe installation path in package: " + value);
            return value;
        }

        internal static bool Allowed(string path)
        {
            if (path.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)) return true;
            return new[] { ".doorstop_version", "BepInEx/plugins/ParalivesAccess.dll", "prism.dll", "winhttp.dll", "doorstop_config.ini", "LICENSE", "LICENSE-SOURCES.json", "THIRD-PARTY-NOTICES.txt", "README-ParalivesAccess.md", "README-ParalivesAccess.txt", "SHA256SUMS.txt" }.Contains(path, StringComparer.OrdinalIgnoreCase);
        }

        internal static List<PackageFile> Prepare(string zip, string staging, CancellationToken token)
        {
            if (Directory.Exists(staging)) throw new IOException("The staging folder already exists.");
            Directory.CreateDirectory(staging);
            var files = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > 2048) throw new InvalidDataException("Unexpected ZIP contents.");
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = SafeRelative(entry.FullName);
                    if (!Allowed(relative) || files.ContainsKey(relative) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                        throw new InvalidDataException("Unexpected, duplicate or linked package file: " + relative);
                    size += entry.Length;
                    if (entry.Length > 128L * 1024 * 1024 || size > 512L * 1024 * 1024) throw new InvalidDataException("Unpacked package is too large.");
                    string destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[65536]; long total = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            token.ThrowIfCancellationRequested(); total += count;
                            if (total > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
                            output.Write(buffer, 0, count);
                        }
                        if (total != entry.Length) throw new InvalidDataException("ZIP entry is incomplete.");
                    }
                    files.Add(relative, new PackageFile { Relative = relative, Source = destination, Hash = Hash(destination) });
                }
            }
            if (!files.TryGetValue("SHA256SUMS.txt", out var manifest)) throw new InvalidDataException("The release has no file checksum manifest.");
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(manifest.Source).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var match = Regex.Match(line, @"^([a-fA-F0-9]{64})  (.+)$");
                if (!match.Success) throw new InvalidDataException("Invalid checksum manifest.");
                string path = SafeRelative(match.Groups[2].Value);
                if (!declared.Add(path) || path.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                    || !files.TryGetValue(path, out var file) || !file.Hash.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A packaged file failed checksum verification: " + path);
            }
            if (declared.Count != files.Count - 1) throw new InvalidDataException("Some packaged files are missing from the checksum manifest.");
            foreach (string required in new[] { "BepInEx/plugins/ParalivesAccess.dll", "BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll", "prism.dll", "winhttp.dll", "doorstop_config.ini" })
                if (!files.ContainsKey(required)) throw new InvalidDataException("The full installation package is missing " + required);
            return files.Values.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Target(string root, string relative)
        {
            SafeRelative(relative);
            if (!Allowed(relative)) throw new InvalidDataException("Unsupported installation file: " + relative);
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File escapes the game folder.");
            string current = target;
            while (current != null && current.Length >= root.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An installation path is a symbolic link or junction. Choose a game folder with ordinary mod directories: " + current);
                if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
            return target;
        }

        private sealed class Change
        {
            internal string Destination, Backup;
            internal DateTime LastWrite;
            internal FileAttributes Attributes;
        }

        internal static InstallResult Apply(List<PackageFile> package, string folder, string backupRoot, bool reinstall,
            CancellationToken token, IProgress<string> progress, Action<string> beforeWrite = null, Func<bool> gameRunning = null)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (!GameLocator.IsGame(root)) throw new IOException("Choose the Paralives folder containing Paralives.exe and Paralives_Data.");
            if ((gameRunning ?? GameLocator.Running)()) throw new IOException("Close Paralives before installing or updating the mod.");
            var changes = new List<Change>();
            var directories = new List<string>();
            var planned = new List<Tuple<PackageFile, string>>();
            int unchanged = 0;
            foreach (var file in package)
            {
                token.ThrowIfCancellationRequested();
                string target = Target(root, file.Relative);
                if (!File.Exists(file.Source) || Hash(file.Source) != file.Hash) throw new InvalidDataException("Staged file changed: " + file.Relative);
                if (!reinstall && File.Exists(target) && Hash(target) == file.Hash) { unchanged++; continue; }
                if (File.Exists(target)) using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
                planned.Add(Tuple.Create(file, target));
            }
            if (planned.Count == 0) return new InstallResult { Unchanged = unchanged };
            if (Directory.Exists(backupRoot)) throw new IOException("A backup folder with this name already exists.");
            Directory.CreateDirectory(backupRoot);
            try
            {
                foreach (var item in planned)
                {
                    token.ThrowIfCancellationRequested();
                    if ((gameRunning ?? GameLocator.Running)()) throw new IOException("Paralives started during installation. Close it and try again.");
                    string target = Target(root, item.Item1.Relative);
                    string backup = null;
                    var change = new Change { Destination = target };
                    if (File.Exists(target))
                    {
                        change.LastWrite = File.GetLastWriteTimeUtc(target); change.Attributes = File.GetAttributes(target);
                        backup = Path.Combine(backupRoot, item.Item1.Relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(target, backup, false);
                    }
                    change.Backup = backup;
                    string parent = Path.GetDirectoryName(target);
                    var missing = new Stack<string>();
                    for (string current = parent; !Directory.Exists(current); current = Path.GetDirectoryName(current)) missing.Push(current);
                    while (missing.Count > 0) { string dir = missing.Pop(); Directory.CreateDirectory(dir); directories.Add(dir); }
                    changes.Add(change);
                    beforeWrite?.Invoke(target);
                    File.Copy(item.Item1.Source, target, true);
                    if (Hash(target) != item.Item1.Hash) throw new IOException("Installed file verification failed: " + item.Item1.Relative);
                    progress?.Report("Installed " + item.Item1.Relative);
                }
                File.WriteAllLines(Path.Combine(backupRoot, "installation.txt"), new[] { "Game folder: " + root, "Completed: " + DateTime.UtcNow.ToString("o") }.Concat(planned.Select(p => p.Item1.Hash + "  " + p.Item1.Relative)));
                return new InstallResult { Changed = changes.Count, Unchanged = unchanged, BackupFolder = backupRoot };
            }
            catch (Exception original)
            {
                var errors = new List<string>();
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (change.Backup == null) { if (File.Exists(change.Destination)) File.Delete(change.Destination); }
                        else { File.Copy(change.Backup, change.Destination, true); File.SetLastWriteTimeUtc(change.Destination, change.LastWrite); File.SetAttributes(change.Destination, change.Attributes); }
                    }
                    catch (Exception error) { errors.Add(error.Message); }
                }
                foreach (string dir in directories.AsEnumerable().Reverse())
                    try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
                if (errors.Count != 0) throw new IOException("Installation failed and some files could not be restored. Keep the backup at " + backupRoot + ". " + string.Join("; ", errors), original);
                if (original is OperationCanceledException) throw;
                if (original is UnauthorizedAccessException) throw new UnauthorizedAccessException("Windows denied access. Previous files were restored. Run the installer as administrator and try again.", original);
                throw new IOException("Installation failed; previous files were restored. " + original.Message, original);
            }
        }
    }
}
