using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapMeasure
{
    /// <summary>
    /// "Start with Windows" from the tray menu.
    /// Portable exe: the usual per-user Run entry in the registry (no admin rights).
    /// Store package: the StartupTask declared in the package manifest. Windows only lets the app ask to turn
    /// it on; if you switched it off in Settings, only Settings → Apps → Startup can switch it back on.
    /// </summary>
    internal static class Startup
    {
        public const string Arg = "--startup";             // started at sign-in: no "SnapMeasure is running" balloon
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "SnapMeasure";
        const string TaskId = "SnapMeasureStartup";        // must match store\AppxManifest.xml

        static string ExeCommand { get { return "\"" + Application.ExecutablePath + "\" " + Arg; } }

        public static bool IsOn
        {
            get
            {
                try
                {
                    if (AppInfo.IsStoreApp)
                    {
                        var st = WinRt.Run(() => WinRt.Wait(Windows.ApplicationModel.StartupTask.GetAsync(TaskId)).State);
                        return st == Windows.ApplicationModel.StartupTaskState.Enabled || st == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                    }
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        string v = k == null ? null : k.GetValue(RunName) as string;
                        return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch (Exception ex) { ErrorLog.Write("startup state", ex); return false; }
            }
        }

        /// <summary>Turns starting at sign-in on or off. Returns the state afterwards and a message for the user (or null).</summary>
        public static bool Set(bool on, out string message)
        {
            message = null;
            try
            {
                if (AppInfo.IsStoreApp) return SetStore(on, out message);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, ExeCommand);
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
                }
                return on;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("startup set", ex);
                message = "Windows did not allow the change: " + ex.Message;
                return IsOn;
            }
        }

        /// <summary>The portable exe was moved or renamed: point the Run entry at where it is now.</summary>
        public static void RefreshPath()
        {
            if (AppInfo.IsStoreApp) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    string v = k.GetValue(RunName) as string;
                    if (v != null && v != ExeCommand && v.IndexOf("SnapMeasure", StringComparison.OrdinalIgnoreCase) >= 0) k.SetValue(RunName, ExeCommand);
                }
            }
            catch (Exception) { }
        }

        static bool SetStore(bool on, out string message)
        {
            message = null;
            var task = WinRt.Run(() => WinRt.Wait(Windows.ApplicationModel.StartupTask.GetAsync(TaskId)));
            var st = task.State;
            if (!on)
            {
                if (st == Windows.ApplicationModel.StartupTaskState.Enabled) WinRt.Run(() => { task.Disable(); return 0; });
                else if (st == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                    message = "Your organisation starts SnapMeasure with Windows; it can't be switched off here.";
                return IsOn;
            }
            if (st == Windows.ApplicationModel.StartupTaskState.Disabled)
                st = WinRt.Run(() => WinRt.Wait(task.RequestEnableAsync()));
            if (st == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
            {
                message = "Starting with Windows was switched off in Windows Settings, so only Settings can switch it back on.\n\n" +
                          "Settings → Apps → Startup will open now: turn SnapMeasure on there.";
                OpenStartupSettings();
            }
            else if (st == Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
                message = "Your organisation does not allow apps to start with Windows on this PC.";
            return st == Windows.ApplicationModel.StartupTaskState.Enabled || st == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }

        public static void OpenStartupSettings()
        {
            try { Process.Start("ms-settings:startupapps"); } catch (Exception) { }
        }
    }
}
