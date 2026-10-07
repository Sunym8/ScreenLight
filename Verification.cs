using System.Text.Json;

namespace ScreenLight;

internal static class Verification
{
    internal static int SelfTest(string path)
    {
        var failures = new List<string>();
        int count = 0;
        void Check(string name, bool valid) { count++; if (!valid) failures.Add(name); }
        Check("WMI maps correct panel instance", DisplayService.SamePanel(@"\\?\DISPLAY#SAC2766#5&1820da98&0&UID4352#{GUID}", @"DISPLAY\SAC2766\5&1820DA98&0&UID4352_0"));
        Check("Same model different connector remains separate", !DisplayService.SamePanel(@"\\?\DISPLAY#SAC2766#UID1", @"DISPLAY\SAC2766\UID2_0"));
        Check("Unknown IDs do not match", !DisplayService.SamePanel("", ""));
        Check("Nonzero minimum scaled correctly", DisplayService.Raw(50, 20, 220) == 120 && DisplayService.Percent(120, 20, 220) == 50);
        Check("Hardware percentage clamped", DisplayService.Raw(-10, 5, 105) == 5 && DisplayService.Raw(110, 5, 105) == 105);
        Check("Degenerate hardware range handled", DisplayService.Percent(5, 5, 5) == 100);
        Check("Discrete laptop step down", DisplayService.Snap(95, [50, 100], 100) == 50);
        Check("Discrete laptop step up", DisplayService.Snap(55, [50, 100], 50) == 100);
        Check("Continuous laptop brightness", DisplayService.Snap(55, Enumerable.Range(0, 101).Select(v => (byte)v).ToArray(), 60) == 55);
        Check("No levels uses direct percentage", DisplayService.Snap(55, [], 60) == 55);
        var defaults = Settings.Defaults();
        Check("Default enabled keys unique", defaults.Where(h => h.Enabled).GroupBy(h => (h.Modifiers, h.Key)).All(g => g.Count() == 1));
        Check("Settings JSON roundtrip", JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings(), Program.Json), Program.Json)?.Hotkeys.Count == 8);
        Check("Startup opt-in by default", !new Settings().StartWithWindows);
        Check("Default close preserves tray behavior", new Settings().CloseAction == WindowCloseAction.HideToTray);
        var migrated = JsonSerializer.Deserialize<Settings>("{\"Step\":5}", Program.Json)!;
        Check("Old preferences retain safe defaults", !migrated.StartWithWindows && migrated.CloseAction == WindowCloseAction.HideToTray);
        var options = new Settings { StartWithWindows = true, CloseAction = WindowCloseAction.ExitApplication };
        var roundtrip = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(options, Program.Json), Program.Json)!;
        Check("New preferences JSON roundtrip", roundtrip.StartWithWindows && roundtrip.CloseAction == WindowCloseAction.ExitApplication);
        Check("Startup executable quoted for spaces", StartupRegistration.BuildCommand(@"C:\My Apps\ScreenLight.exe") == "\"C:\\My Apps\\ScreenLight.exe\" --tray");
        bool invalidCommandRejected = false;
        try { StartupRegistration.BuildCommand("C:\\Apps\\bad\"name.exe"); } catch (ArgumentException) { invalidCommandRejected = true; }
        Check("Invalid startup path rejected", invalidCommandRejected);
        using var icon = UiAssets.LoadIcon();
        Check("Embedded icon available", icon.Width == 32 && icon.Height == 32);
        Check("Shortcut contains key modifiers", defaults[0].Shortcut == "Ctrl + Alt + ↑");
        using var window = new Form();
        // An isolated test combination avoids colliding with a normally running ScreenLight instance.
        var key = new HotkeyBinding { Modifiers = 7, Key = (uint)Keys.F24 };
        var registered = Native.RegisterHotKey(window.Handle, 900, key.Modifiers | 0x4000, key.Key);
        Check("Real global shortcut registration", registered);
        if (registered)
        {
            Check("Duplicate shortcut rejected", !Native.RegisterHotKey(window.Handle, 901, key.Modifiers | 0x4000, key.Key));
            Check("Shortcut released", Native.UnregisterHotKey(window.Handle, 900));
            var availableAgain = Native.RegisterHotKey(window.Handle, 902, key.Modifiers | 0x4000, key.Key);
            Check("Released shortcut reusable", availableAgain);
            Native.UnregisterHotKey(window.Handle, 902);
        }
        File.WriteAllText(path, $"{count - failures.Count}/{count} checks passed\n" + string.Join("\n", failures));
        return failures.Count == 0 ? 0 : 1;
    }
    internal static int SettingsIntegration(string path)
    {
        int count = 0;
        var failures = new List<string>();
        void Check(string name, bool valid) { count++; if (!valid) failures.Add(name); }
        var folder = Path.Combine(Path.GetTempPath(), "ScreenLight-settings-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var preferenceFile = Path.Combine(folder, "settings.json");
        // Never toggle the production startup entry during verification.
        var registration = new StartupRegistration("ScreenLight.Verification." + Guid.NewGuid().ToString("N"));
        var before = registration.Capture();
        var executable = Environment.ProcessPath!;
        try
        {
            var settings = new Settings();
            GeneralOptions.Apply(settings, registration, true, WindowCloseAction.ExitApplication, executable, preferenceFile);
            Check("Current-user startup registration enabled", registration.IsEnabled(executable));
            Check("Exact startup command includes tray argument", (string?)registration.Capture().Run.Value == StartupRegistration.BuildCommand(executable));
            var loaded = Settings.Load(out var error, preferenceFile);
            Check("General options persist and reload", error == "" && loaded.StartWithWindows && loaded.CloseAction == WindowCloseAction.ExitApplication);
            GeneralOptions.Apply(settings, registration, false, WindowCloseAction.HideToTray, executable, preferenceFile);
            Check("Startup choice removes its registry value", !registration.IsEnabled(executable) && registration.Capture().Run.Value == null);
            var disabled = Settings.Load(out error, preferenceFile);
            Check("Disabled startup and tray choice persist", error == "" && !disabled.StartWithWindows && disabled.CloseAction == WindowCloseAction.HideToTray);
            var blockedParent = Path.Combine(folder, "parent-is-a-file"); File.WriteAllText(blockedParent, "test");
            bool failedSave = false;
            try { GeneralOptions.Apply(settings, registration, true, WindowCloseAction.ExitApplication, executable, Path.Combine(blockedParent, "settings.json")); }
            catch (IOException) { failedSave = true; }
            Check("Write failure is reported", failedSave);
            Check("Write failure rolls back startup entry", !registration.IsEnabled(executable));
            Check("Write failure rolls back in-memory choices", !settings.StartWithWindows && settings.CloseAction == WindowCloseAction.HideToTray);
            Check("Write failure preserves saved preferences", Settings.Load(out _, preferenceFile).CloseAction == WindowCloseAction.HideToTray);
            bool invalidOptionRejected = false;
            try { GeneralOptions.Apply(settings, registration, true, (WindowCloseAction)999, executable, preferenceFile); } catch (ArgumentException) { invalidOptionRejected = true; }
            Check("Invalid close action causes no startup change", invalidOptionRejected && !registration.IsEnabled(executable));
            using (var form = new MainForm(true, new Settings { CloseAction = WindowCloseAction.HideToTray }))
            {
                form.Show(); PumpUntil(() => form.Visible, 1000);
                var settingsWindow = form.OpenSettingsForVerification(0);
                settingsWindow.Close(); Application.DoEvents();
                Check("Settings close hides only settings window", !settingsWindow.Visible && !settingsWindow.IsDisposed && form.Visible);
                form.Close(); Application.DoEvents();
                Check("Main close to tray hides without disposing", !form.Visible && !form.IsDisposed);
                form.Show(); Application.DoEvents();
                Check("Hidden main window can reopen", form.Visible && !form.IsDisposed);
                form.Exit(); PumpUntil(() => form.IsDisposed, 7000);
                Check("Explicit exit disposes even with tray preference", form.IsDisposed);
            }
            using (var form = new MainForm(true, new Settings { CloseAction = WindowCloseAction.ExitApplication }))
            {
                form.Show(); Application.DoEvents(); form.Close(); PumpUntil(() => form.IsDisposed, 7000);
                Check("Main close exit preference disposes form", form.IsDisposed);
            }
            using (var form = new MainForm(true, new Settings(), startInTray: true))
            {
                form.Show(); Application.DoEvents();
                Check("Startup tray mode hides main window", !form.Visible && !form.IsDisposed);
                form.Show(); Application.DoEvents();
                Check("Startup tray mode can open afterwards", form.Visible);
                form.Exit(); PumpUntil(() => form.IsDisposed, 7000);
                Check("Startup tray mode exits cleanly", form.IsDisposed);
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            registration.Restore(before);
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        File.WriteAllText(path, $"{count - failures.Count}/{count} checks passed\n" + string.Join("\n", failures));
        return failures.Count == 0 ? 0 : 1;
    }
    private static void PumpUntil(Func<bool> condition, int timeout)
    {
        var limit = Environment.TickCount64 + timeout;
        while (!condition() && Environment.TickCount64 < limit) { Application.DoEvents(); Thread.Sleep(15); }
    }
    internal static int Hardware(string path)
    {
        using var service = new DisplayService();
        service.ScanAsync().GetAwaiter().GetResult();
        var reports = new List<object>();
        bool success = true;
        foreach (var display in service.Displays.Where(d => d.Method != BrightnessMethod.None).ToArray())
        {
            var original = display.Brightness;
            var requested = original >= 10 ? original - 5 : original + 5;
            int? actual = null, restored = null;
            string error = "", restorationError = "";
            try
            {
                actual = service.SetAsync(display.Id, requested).GetAwaiter().GetResult();
                Thread.Sleep(300);
                service.ScanAsync().GetAwaiter().GetResult();
                actual = service.Displays.First(d => d.Id == display.Id).Brightness;
                if (actual == original) throw new Exception("Readback did not change after test write");
            }
            catch (Exception ex) { success = false; error = ex.ToString(); }
            finally
            {
                try
                {
                    service.SetAsync(display.Id, original).GetAwaiter().GetResult();
                    Thread.Sleep(300);
                    service.ScanAsync().GetAwaiter().GetResult();
                    restored = service.Displays.First(d => d.Id == display.Id).Brightness;
                    if (restored != original) { success = false; restorationError = "Original percentage not restored"; }
                }
                catch (Exception ex) { success = false; restorationError = ex.Message; }
            }
            reports.Add(new { display.Name, display.Id, display.Method, Original = original, Requested = requested, Actual = actual, Restored = restored, Error = error, RestorationError = restorationError });
        }
        // Independent final scan validates the DDC write, not only its API return code.
        service.ScanAsync().GetAwaiter().GetResult();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Success = success && reports.Count > 0, Tests = reports, FinalReadback = service.Displays }, Program.Json));
        return success && reports.Count > 0 ? 0 : 1;
    }
}
