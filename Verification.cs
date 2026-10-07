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
        Check("Settings JSON roundtrip", JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings(), Program.Json))?.Hotkeys.Count == 8);
        Check("Shortcut contains key modifiers", defaults[0].Shortcut == "Ctrl + Alt + ↑");
        using var window = new Form();
        var key = defaults[0];
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
