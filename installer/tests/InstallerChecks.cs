using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParalivesAccessInstaller;

internal static class InstallerChecks
{
    private static string root;
    private static int count;
    private static readonly List<string> results = new List<string>();
    private static void Check(string name, bool passed)
    {
        if (!passed) throw new Exception("FAIL: " + name);
        count++; results.Add(name); Console.WriteLine("PASS: " + name);
    }
    private static void Reject(string name, Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; } catch (OperationCanceledException) { rejected = true; }
        Check(name, rejected);
    }
    private static string Fresh(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
    private static string Game(string name)
    {
        string game = Fresh(name); Directory.CreateDirectory(Path.Combine(game, "Paralives_Data", "Managed"));
        File.WriteAllText(Path.Combine(game, "Paralives.exe"), "sandbox marker, not executable");
        File.WriteAllText(Path.Combine(game, "Paralives_Data", "Managed", "Paralives.dll"), "sandbox game marker");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "config"));
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "config", "com.florianbeijers.paralivesaccess.cfg"), "user configuration");
        File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "OtherMod.dll"), "other plugin");
        return game;
    }
    private static string Snapshot(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => p.Substring(folder.Length) + ":" + InstallEngine.Hash(p)));
    private static InstallResult Apply(List<PackageFile> package, string game, bool force = false, Action<string> fault = null, Func<bool> running = null, CancellationToken token = default(CancellationToken))
        => InstallEngine.Apply(package, game, Fresh("backup"), force, token, null, fault, running ?? (() => false));
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static Dictionary<string, byte[]> Skeleton() => new Dictionary<string, byte[]>
    {
        ["BepInEx/plugins/ParalivesAccess.dll"] = Bytes("mod"),
        ["BepInEx/core/BepInEx.dll"] = Bytes("loader"),
        ["BepInEx/core/BepInEx.Preloader.dll"] = Bytes("preloader"),
        ["prism.dll"] = Bytes("speech"), ["winhttp.dll"] = Bytes("doorstop"), ["doorstop_config.ini"] = Bytes("settings")
    };
    private static string Archive(Dictionary<string, byte[]> files, string extra = null, bool duplicate = false, bool link = false, bool badManifest = false)
    {
        string zip = Fresh("fixture") + ".zip";
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var manifest = new List<string>();
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key);
                if (link && file.Key == "prism.dll") entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                using (var output = entry.Open()) output.Write(file.Value, 0, file.Value.Length);
                using (var sha = System.Security.Cryptography.SHA256.Create()) manifest.Add(BitConverter.ToString(sha.ComputeHash(file.Value)).Replace("-", "").ToLowerInvariant() + "  " + file.Key);
            }
            if (extra != null) using (var output = archive.CreateEntry(extra).Open()) output.WriteByte(1);
            if (duplicate) using (var output = archive.CreateEntry("PRISM.DLL").Open()) output.WriteByte(1);
            using (var writer = new StreamWriter(archive.CreateEntry("SHA256SUMS.txt").Open(), new UTF8Encoding(false))) writer.Write(string.Join("\n", badManifest ? manifest.Skip(1) : manifest));
        }
        return zip;
    }
    private static void RejectZip(string name, string path) => Reject(name, () => InstallEngine.Prepare(path, Fresh("unpack"), CancellationToken.None));

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "--junction")
            {
                root = Path.GetFullPath(args[3]);
                var package = InstallEngine.Prepare(args[2], Fresh("junction-package"), CancellationToken.None);
                Reject("existing game junction refused", () => Apply(package, args[1]));
                string outside = File.ReadAllLines(Path.Combine(root, "junction-request.txt"))[1];
                Check("junction destination untouched", !Directory.EnumerateFileSystemEntries(outside).Any());
                File.AppendAllLines(Path.Combine(root, "passed-checks.txt"), results);
                File.AppendAllText(Path.Combine(root, "result.txt"), "PASS 2 additional junction checks\r\n");
            }
            else Run(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run(string[] args)
    {
        root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Refusing to overwrite existing test evidence.");
        Directory.CreateDirectory(root);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.2", false);
        AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.3", false);
        using (var status = new StatusBox { Text = "Checking release", AccessibleName = "Installation status: Checking release" })
        {
            status.CreateControl();
            Check("polite screen-reader live region", status.LiveSetting == System.Windows.Forms.Automation.AutomationLiveSetting.Polite);
            status.AnnounceChange();
            Check("native live-region notification raises without error", status.IsHandleCreated);
        }
        // Creating a WinForms control installs its UI context. This console runner has
        // no message loop; keep later network continuations on the thread pool.
        SynchronizationContext.SetSynchronizationContext(null);
        string zip = Path.GetFullPath(args[1]);
        var package = InstallEngine.Prepare(zip, Fresh("verified-release"), CancellationToken.None);
        Check("real release full manifest validates", package.Count >= 40);
        Check("Steam game auto-detection", GameLocator.Find().Any(GameLocator.IsGame));
        string steam = Fresh("steam"); Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        string library = Fresh("second-library");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" } }");
        Check("additional Steam library paths", GameLocator.Libraries(steam).Contains(library));
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1118520.acf"), "\"installdir\" \"Custom Paralives\"");
        Check("manifest installation directory", GameLocator.ManifestFolder(library) == Path.Combine(library, "steamapps", "common", "Custom Paralives"));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1118520.acf"), "\"installdir\" \"..\\..\\outside\"");
        Check("manifest directory escape refused", GameLocator.ManifestFolder(library) == null);
        string game = Game("clean-game");
        var first = Apply(package, game);
        Check("first install writes full package", first.Changed == package.Count && first.Unchanged == 0 && package.All(f => InstallEngine.Hash(Path.Combine(game, f.Relative.Replace('/', '\\'))) == f.Hash));
        Check("user config and other plugin preserved", File.ReadAllText(Path.Combine(game, "BepInEx", "config", "com.florianbeijers.paralivesaccess.cfg")) == "user configuration" && File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "OtherMod.dll")) == "other plugin");
        Check("game binaries preserved", File.ReadAllText(Path.Combine(game, "Paralives.exe")) == "sandbox marker, not executable" && File.ReadAllText(Path.Combine(game, "Paralives_Data", "Managed", "Paralives.dll")) == "sandbox game marker");
        string before = Snapshot(game);
        var second = Apply(package, game);
        Check("unchanged update is a no-op", second.Changed == 0 && second.Unchanged == package.Count && Snapshot(game) == before);
        string dll = Path.Combine(game, "BepInEx", "plugins", "ParalivesAccess.dll");
        File.WriteAllText(dll, "damaged plugin"); File.Delete(Path.Combine(game, "prism.dll"));
        var repair = Apply(package, game);
        Check("repair missing and damaged files only", repair.Changed == 2 && Snapshot(game) == before);
        Check("repair retains previous bytes in backup", File.ReadAllText(Path.Combine(repair.BackupFolder, "BepInEx", "plugins", "ParalivesAccess.dll")) == "damaged plugin");
        var force = Apply(package, game, true);
        Check("reinstall replaces every included file", force.Changed == package.Count && Snapshot(game) == before);
        Reject("running game refused", () => Apply(package, game, false, null, () => true));
        Check("running-game refusal leaves target unchanged", Snapshot(game) == before);
        using (var held = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject("locked existing file refused", () => Apply(package, game, true));
        Check("lock failure leaves target unchanged", Snapshot(game) == before);
        int writes = 0;
        Reject("mid-update failure reported", () => Apply(package, game, true, p => { if (++writes == 6) throw new IOException("injected write failure"); }));
        Check("mid-update rollback restores every original file", Snapshot(game) == before);
        string fresh = Game("failed-first-install"); string freshBefore = Snapshot(fresh); writes = 0;
        Reject("mid-first-install failure reported", () => Apply(package, fresh, false, p => { if (++writes == 9) throw new IOException("injected write failure"); }));
        Check("first-install rollback removes new files", Snapshot(fresh) == freshBefore && !Directory.Exists(Path.Combine(fresh, "BepInEx", "core")));
        using (var cancel = new CancellationTokenSource())
        {
            writes = 0;
            Reject("cancellation reported", () => Apply(package, game, true, p => { if (++writes == 3) cancel.Cancel(); }, null, cancel.Token));
            Check("cancellation rolls back partial update", Snapshot(game) == before);
        }
        int probes = 0;
        Reject("game starting during install refused", () => Apply(package, game, true, null, () => ++probes >= 4));
        Check("game-start rollback restores original files", Snapshot(game) == before);
        var changed = package[0]; byte[] saved = File.ReadAllBytes(changed.Source); File.WriteAllText(changed.Source, "corrupt staging");
        Reject("staged tampering refused", () => Apply(package, game)); File.WriteAllBytes(changed.Source, saved);
        Check("tampering refusal leaves game unchanged", Snapshot(game) == before);
        foreach (string unsafePath in new[] { "../escape.dll", "/absolute.dll", "C:/escape.dll", "BepInEx/../escape.dll", "BepInEx\\escape.dll", "licenses/CON.txt", "licenses/file:stream", "licenses/file. ", "licenses//file" })
            Reject("unsafe archive path: " + unsafePath, () => InstallEngine.SafeRelative(unsafePath));
        RejectZip("ZIP traversal", Archive(Skeleton(), "../escape.dll"));
        RejectZip("ZIP duplicate case", Archive(Skeleton(), duplicate: true));
        RejectZip("ZIP symbolic link", Archive(Skeleton(), link: true));
        RejectZip("missing manifest entry", Archive(Skeleton(), badManifest: true));
        RejectZip("unlisted extra package file", Archive(Skeleton(), "licenses/extra.txt"));
        RejectZip("configuration overwrite in ZIP", Archive(Skeleton(), "BepInEx/config/user.cfg"));
        RejectZip("game executable overwrite in ZIP", Archive(Skeleton(), "Paralives.exe"));
        var missing = Skeleton(); missing.Remove("winhttp.dll"); RejectZip("incomplete loader bundle", Archive(missing));
        var badHashes = Skeleton(); string corruptedZip = Archive(badHashes);
        using (var archive = ZipFile.Open(corruptedZip, ZipArchiveMode.Update)) { archive.GetEntry("prism.dll").Delete(); using (var writer = new StreamWriter(archive.CreateEntry("prism.dll").Open())) writer.Write("tampered"); }
        RejectZip("per-file checksum mismatch", corruptedZip);
        foreach (string url in new[] { "http://github.com/zersiax/ParalivesAccess/releases/download/v1.0.0/a.zip", "https://github.com/evil/Other/releases/download/v1.0.0/a.zip", "https://example.com/a.zip", "https://github.com:444/zersiax/ParalivesAccess/releases/download/v1.0.0/a.zip" })
            Reject("untrusted download URL: " + url, () => ReleaseClient.CheckDownloadUrl(url));
        string linked = Game("junction-game");
        string outside = Fresh("junction-outside"); Directory.CreateDirectory(outside);
        // The launcher creates this junction using the native PowerShell cmdlet.
        string junctionRequest = Path.Combine(root, "junction-request.txt"); File.WriteAllLines(junctionRequest, new[] { linked, outside });
        if (args.Length > 2 && args[2] == "--live")
        {
            using (var client = new ReleaseClient())
            {
                var release = await client.Latest(CancellationToken.None);
                Check("live GitHub stable release metadata", release.Package != null && !release.Draft && !release.Prerelease);
                string downloaded = Path.Combine(root, "github-release.zip");
                await client.Download(release, downloaded, null, CancellationToken.None);
                Check("live download SHA-256 verification", InstallEngine.Hash(downloaded) == release.Package.Digest.Substring(7));
                var livePackage = InstallEngine.Prepare(downloaded, Fresh("github-package"), CancellationToken.None);
                string liveGame = Game("live-download-install");
                Check("live downloaded bundle installs into isolated folder", Apply(livePackage, liveGame).Changed == livePackage.Count);
                release.Package.Digest = "sha256:" + new string('0', 64);
                bool rejected = false;
                try { await client.Download(release, Path.Combine(root, "wrong-hash.zip"), null, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
                Check("download digest mismatch refused", rejected);
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool canceledResult = false;
                    try { await client.Latest(canceled.Token); } catch (OperationCanceledException) { canceledResult = true; }
                    Check("network cancellation", canceledResult);
                }
            }
        }
        File.WriteAllLines(Path.Combine(root, "passed-checks.txt"), results);
        File.WriteAllText(Path.Combine(root, "result.txt"), "PASS " + count + " checks\r\nSubject SHA256: " + InstallEngine.Hash(typeof(InstallEngine).Assembly.Location) + "\r\n");
        Console.WriteLine("PASS: " + count + " installer checks. Evidence: " + root);
    }
}
