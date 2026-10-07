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
                var tabs = preview.Controls.OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                preview.Refresh();
                using var keysBitmap = new Bitmap(preview.Width, preview.Height);
                preview.DrawToBitmap(keysBitmap, new Rectangle(0, 0, keysBitmap.Width, keysBitmap.Height));
                keysBitmap.Save(Path.Combine(Path.GetDirectoryName(path) ?? ".", "ui-hotkeys.png"), System.Drawing.Imaging.ImageFormat.Png);
                preview.Exit();
            };
            Application.Run(preview);
            return 0;
        }
        using var singleInstance = new Mutex(true, "Local\\ScreenLight.v1", out var created);
        if (!created)
        {
            var window = Native.FindWindow(null, "ScreenLight · 屏幕亮度");
            if (window != IntPtr.Zero) { Native.ShowWindow(window, 9); Native.SetForegroundWindow(window); }
            return 0;
        }
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => MessageBox.Show("发生错误：" + e.Exception.Message, "ScreenLight");
            using var form = new MainForm();
            Application.Run(form);
            return 0;
        }
        finally { singleInstance.ReleaseMutex(); }
    }
}
