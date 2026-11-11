using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Automation;

namespace ParalivesAccessInstaller
{
    internal sealed class StatusBox : TextBox, IAutomationLiveRegion
    {
        public AutomationLiveSetting LiveSetting { get; set; } = AutomationLiveSetting.Polite;
        internal void AnnounceChange()
        {
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            if (!AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, Text))
                AccessibilityObject.RaiseLiveRegionChanged();
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly ComboBox folder = new ComboBox { Dock = DockStyle.Fill, AccessibleName = "Paralives game folder", TabIndex = 0 };
        private readonly Button browse = new Button { Text = "&Browse...", AutoSize = true, TabIndex = 1 };
        private readonly Button detect = new Button { Text = "&Detect Steam folders", AutoSize = true, TabIndex = 2 };
        private readonly Label summary = new Label { AutoSize = true, MaximumSize = new Size(760, 0), AccessibleName = "Version summary" };
        private readonly Button check = new Button { Text = "&Check for updates", AutoSize = true, TabIndex = 0 };
        private readonly Button install = new Button { Text = "&Install or update", AutoSize = true, TabIndex = 1 };
        private readonly Button reinstall = new Button { Text = "&Reinstall latest", AutoSize = true, TabIndex = 2 };
        private readonly StatusBox status = new StatusBox { ReadOnly = true, Multiline = true, Height = 52, Dock = DockStyle.Fill, AccessibleName = "Installation status", TabIndex = 3 };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, AccessibleName = "Download progress", Height = 22 };
        private readonly ListBox log = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Progress log", HorizontalScrollbar = true, TabIndex = 4 };
        private readonly Button releasePage = new Button { Text = "GitHub release &page", AutoSize = true, TabIndex = 0 };
        private readonly Button openLog = new Button { Text = "Open &log file", AutoSize = true, TabIndex = 1 };
        private readonly Button admin = new Button { Text = "Restart as &administrator", AutoSize = true, Visible = false, TabIndex = 2 };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, TabIndex = 3 };
        private readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParalivesAccess", "Installer");
        private string logPath;
        private Release latest;
        private bool busy;
        private CancellationTokenSource cancellation;

        internal InstallerForm(string initialFolder)
        {
            Text = "ParalivesAccess Installer";
            AccessibleName = Text;
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 570);
            MinimumSize = new Size(700, 560);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "Install or update ParalivesAccess", Font = new Font(Font, FontStyle.Bold), AutoSize = true }, 0, 0);
            layout.Controls.Add(new Label { Text = "Downloads the latest stable release from GitHub. Close Paralives first.\r\nExisting saves, mod settings and other plugins are kept.", AutoSize = true, Margin = new Padding(3, 8, 3, 12) }, 0, 1);
            var pathRow = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, TabIndex = 0 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(new Label { Text = "&Game folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            pathRow.Controls.Add(folder, 1, 0); pathRow.Controls.Add(browse, 2, 0); pathRow.Controls.Add(detect, 3, 0);
            layout.Controls.Add(pathRow, 0, 2);
            layout.Controls.Add(summary, 0, 3);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, TabIndex = 1 };
            actions.Controls.AddRange(new Control[] { check, install, reinstall });
            layout.Controls.Add(actions, 0, 4);
            layout.Controls.Add(status, 0, 5); layout.Controls.Add(progress, 0, 6); layout.Controls.Add(log, 0, 7);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, TabIndex = 5 };
            footer.Controls.AddRange(new Control[] { releasePage, openLog, admin, close }); layout.Controls.Add(footer, 0, 8);
            Controls.Add(layout);
            AcceptButton = check;
            CancelButton = close;
            folder.AccessibleDescription = "Choose a detected Steam installation or type the folder containing Paralives.exe. Alt+G focuses this field.";
            reinstall.AccessibleDescription = "Download and replace every included mod and loader file, even if its checksum already matches. Keeps configuration and saves.";
            install.AccessibleDescription = "Download the latest release and replace only missing or changed package files. Existing files are backed up.";
            status.AccessibleDescription = "Read-only current operation and result. Tab here to review it; Ctrl+A and Ctrl+C copy the text.";
            log.AccessibleDescription = "Use Up and Down to review installation steps.";
            check.Click += async (s, e) => await Run(CheckLatest);
            install.Click += async (s, e) => await Run(t => Install(false, t));
            reinstall.Click += async (s, e) => await Run(t => Install(true, t));
            browse.Click += (s, e) => Browse();
            detect.Click += (s, e) => Detect(true);
            folder.TextChanged += (s, e) => RefreshSummary();
            close.Click += (s, e) => { if (busy) cancellation.Cancel(); else Close(); };
            releasePage.Click += (s, e) => Open("https://github.com/" + ReleaseClient.Repository + "/releases/latest");
            openLog.Click += (s, e) => { if (logPath != null) Open(logPath); };
            admin.Click += (s, e) => RestartElevated();
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; cancellation.Cancel(); SetStatus("Canceling. Waiting for the current step and any file restoration to finish."); } };
            Shown += async (s, e) =>
            {
                Detect(false);
                if (!string.IsNullOrWhiteSpace(initialFolder)) folder.Text = initialFolder;
                await Run(CheckLatest);
            };
            RefreshSummary();
        }

        private string Folder => folder.Text.Trim().Trim('"');
        private void SetStatus(string message)
        {
            status.Text = message;
            status.AnnounceChange();
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + message;
            log.Items.Add(line);
            if (!log.Focused) log.TopIndex = Math.Max(0, log.Items.Count - 1);
            if (logPath != null) try { File.AppendAllText(logPath, line + Environment.NewLine); } catch (IOException) { }
            SetStatus(message);
        }

        private void RefreshSummary()
        {
            bool valid = false; string installed = "Choose the game folder";
            try { valid = GameLocator.IsGame(Folder); if (valid) installed = GameLocator.InstalledVersion(Folder); }
            catch (ArgumentException) { } catch (IOException) { } catch (UnauthorizedAccessException) { installed = "Access denied"; }
            summary.Text = "Installed: " + installed + ". Latest: " + (latest?.Tag ?? "Not checked") + ".";
            summary.AccessibleName = "Version summary: " + summary.Text;
            install.Enabled = reinstall.Enabled = !busy && valid && latest != null;
            check.Enabled = browse.Enabled = detect.Enabled = folder.Enabled = !busy;
            openLog.Enabled = !busy && logPath != null;
        }

        private void Detect(bool announce)
        {
            try
            {
                var paths = GameLocator.Find(); folder.Items.Clear(); folder.Items.AddRange(paths);
                if (paths.Length > 0 && (!GameLocator.IsGame(Folder) || announce)) folder.Text = paths[0];
                if (announce) Log(paths.Length == 0 ? "No Steam copy of Paralives found. Browse to the game folder or type its path." : "Found " + paths.Length + " Paralives installation(s).");
            }
            catch (Exception error) { Log("Automatic detection failed. Browse to the game folder. " + error.Message); }
            RefreshSummary();
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose the Paralives folder containing Paralives.exe", ShowNewFolderButton = false, SelectedPath = Directory.Exists(Folder) ? Folder : "" })
                if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            if (!GameLocator.IsGame(Folder)) Log("Choose the game folder itself, not BepInEx or the download folder.");
            folder.Focus();
        }

        private async Task Run(Func<CancellationToken, Task> action)
        {
            if (busy) return;
            busy = true; cancellation = new CancellationTokenSource(); close.Text = "Cancel"; admin.Visible = false;
            RefreshSummary();
            try
            {
                if (logPath == null)
                {
                    string directory = Path.Combine(dataRoot, "Logs"); Directory.CreateDirectory(directory);
                    logPath = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
                }
                await action(cancellation.Token);
            }
            catch (OperationCanceledException) { Log("Canceled. Any files replaced by this attempt were restored."); status.Focus(); }
            catch (Exception error)
            {
                Log(error.Message);
                admin.Visible = error is UnauthorizedAccessException || error is SecurityException;
                MessageBox.Show(this, error.Message, "ParalivesAccess Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Focus();
            }
            finally { busy = false; cancellation.Dispose(); cancellation = null; close.Text = "Close"; RefreshSummary(); }
        }

        private async Task CheckLatest(CancellationToken token)
        {
            Log("Checking the latest stable ParalivesAccess release on GitHub...");
            using (var client = new ReleaseClient()) latest = await client.Latest(token);
            Log("Latest release: " + latest.Tag + ". Choose Install or update to check and repair the included files, or Reinstall latest to replace them all.");
        }

        private async Task Install(bool force, CancellationToken token)
        {
            string target = Folder;
            if (!GameLocator.IsGame(target)) throw new IOException("Choose the Paralives game folder first.");
            if (GameLocator.Running()) throw new IOException("Close Paralives before installing or updating the mod.");
            await CheckLatest(token);
            var release = latest;
            string workRoot = Path.Combine(Path.GetTempPath(), "ParalivesAccess-Installer");
            Directory.CreateDirectory(workRoot);
            string work = Path.Combine(workRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            try
            {
                string archive = Path.Combine(work, "release.zip");
                int reported = -1;
                var download = new Progress<DownloadProgress>(p => { progress.Value = p.Percent; if (p.Percent / 10 != reported) { reported = p.Percent / 10; Log(p.Message); } });
                using (var client = new ReleaseClient()) await client.Download(release, archive, download, token);
                Log("Download checksum verified. Checking package files...");
                var package = await Task.Run(() => InstallEngine.Prepare(archive, Path.Combine(work, "files"), token), token);
                string backup = Path.Combine(dataRoot, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Log("Installing " + release.Tag + " into " + target + ".");
                var steps = new Progress<string>(Log);
                var result = await Task.Run(() => InstallEngine.Apply(package, target, backup, force, token, steps));
                string message = result.Changed == 0 ? release.Tag + " is installed and all included files match. No changes needed."
                    : "Installed " + release.Tag + ". Replaced " + result.Changed + " files; " + result.Unchanged + " already matched. Start your screen reader, then launch Paralives through Steam.";
                if (result.BackupFolder != null) Log("Previous files and installation record: " + result.BackupFolder);
                Log(message); progress.Value = 100;
                MessageBox.Show(this, message, "ParalivesAccess installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                status.Focus();
            }
            finally
            {
                // This directory was created by this operation; package entries cannot create links.
                string full = Path.GetFullPath(work);
                if (full.StartsWith(Path.GetFullPath(workRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void RestartElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--folder64 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Folder))) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (System.ComponentModel.Win32Exception error) { Log("Administrator restart canceled or unavailable. " + error.Message); }
        }

        private void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Cannot open", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
