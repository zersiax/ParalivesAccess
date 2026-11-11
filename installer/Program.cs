using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ParalivesAccessInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures", false);
            AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.2", false);
            AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.3", false);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var mutex = new Mutex(false, @"Local\ParalivesAccessInstaller"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { MessageBox.Show("The ParalivesAccess installer is already running.", "ParalivesAccess Installer"); return; }
                try
                {
                    string folder = null;
                    if (args.Length == 2 && args[0] == "--folder64")
                        try { folder = Encoding.UTF8.GetString(Convert.FromBase64String(args[1])); } catch (FormatException) { }
                    Application.Run(new InstallerForm(folder));
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
