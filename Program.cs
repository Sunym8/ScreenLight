using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenLight;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--diagnose"))
        {
            using var service = new DisplayService();
            service.ScanAsync().GetAwaiter().GetResult();
            var path = args.SkipWhile(a => a != "--diagnose").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "diagnostics.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, service.WmiError, Displays = service.Displays }, Json));
            return 0;
        }
        if (args.Contains("--self-test")) return Verification.SelfTest(args.ElementAtOrDefault(1) ?? "self-test.txt");
        if (args.Contains("--settings-check")) return Verification.SettingsIntegration(args.ElementAtOrDefault(1) ?? "settings-verification.txt");
        if (args.Contains("--verify-hardware")) return Verification.Hardware(args.ElementAtOrDefault(1) ?? "hardware-verification.json");
        if (args.Contains("--ui-check"))
        {
            using var preview = new MainForm(true);
            var path = args.ElementAtOrDefault(1) ?? "ui-preview.png";
            preview.Shown += async (_, _) =>
            {
                await Task.Delay(3500);
                using var bitmap = new Bitmap(preview.Width, preview.Height);
                preview.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                for (int index = 0; index < 3; index++)
                {
                    var settingsWindow = preview.OpenSettingsForVerification(index);
                    using var settingsBitmap = new Bitmap(settingsWindow.Width, settingsWindow.Height);
                    settingsWindow.DrawToBitmap(settingsBitmap, new Rectangle(0, 0, settingsBitmap.Width, settingsBitmap.Height));
                    var name = new[] { "ui-settings.png", "ui-hotkeys.png", "ui-help.png" }[index];
                    settingsBitmap.Save(Path.Combine(Path.GetDirectoryName(path) ?? ".", name), System.Drawing.Imaging.ImageFormat.Png);
                }
                preview.Exit();
            };
            Application.Run(preview);
            return 0;
        }
        using var singleInstance = new Mutex(true, "Local\\ScreenLight.v1", out var created);
        if (!created)
        {
            if (args.Contains("--tray")) return 0;
            var window = Native.FindWindow(null, "ScreenLight · 屏幕亮度");
            if (window != IntPtr.Zero) { Native.ShowWindow(window, 9); Native.SetForegroundWindow(window); }
            return 0;
        }
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => MessageBox.Show("发生错误：" + e.Exception.Message, "ScreenLight");
            using var form = new MainForm(startInTray: args.Contains("--tray"));
            Application.Run(form);
            return 0;
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}
