using System.Text.Json;

namespace ScreenLight;

internal enum WindowCloseAction { HideToTray, ExitApplication }

internal sealed class HotkeyBinding
{
    public string Target { get; set; } = "all";
    public int Delta { get; set; } = 5;
    public uint Modifiers { get; set; } = 3;
    public uint Key { get; set; }
    public bool Enabled { get; set; } = true;
    internal HotkeyBinding Copy() => new() { Target = Target, Delta = Delta, Modifiers = Modifiers, Key = Key, Enabled = Enabled };
    internal string Caption => Target switch { "internal" => "笔记本", "external" => "外接屏幕", "cursor" => "鼠标所在屏幕", _ => "全部屏幕" };
    internal string Shortcut => string.Join(" + ", new[] { (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null, (Modifiers & 4) != 0 ? "Shift" : null, (Modifiers & 8) != 0 ? "Win" : null, ((Keys)Key) switch { Keys.PageUp => "Page Up", Keys.PageDown => "Page Down", Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→", _ => ((Keys)Key).ToString() } }.Where(s => s != null));
}

internal sealed class Settings
{
    public int Step { get; set; } = 5;
    public bool StartWithWindows { get; set; }
    public WindowCloseAction CloseAction { get; set; } = WindowCloseAction.HideToTray;
    public List<HotkeyBinding> Hotkeys { get; set; } = Defaults();
    internal static List<HotkeyBinding> Defaults() =>
    [
        new() { Target = "all", Key = (uint)Keys.Up, Delta = 5 },
        new() { Target = "all", Key = (uint)Keys.Down, Delta = -5 },
        new() { Target = "external", Key = (uint)Keys.PageUp, Delta = 5 },
        new() { Target = "external", Key = (uint)Keys.PageDown, Delta = -5 },
        new() { Target = "internal", Modifiers = 7, Key = (uint)Keys.Up, Delta = 5 },
        new() { Target = "internal", Modifiers = 7, Key = (uint)Keys.Down, Delta = -5 },
        new() { Target = "cursor", Modifiers = 7, Key = (uint)Keys.PageUp, Delta = 5, Enabled = false },
        new() { Target = "cursor", Modifiers = 7, Key = (uint)Keys.PageDown, Delta = -5, Enabled = false }
    ];
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenLight");
    internal static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    internal static Settings Load(out string error, string? filePath = null)
    {
        error = "";
        var path = filePath ?? FilePath;
        if (!File.Exists(path)) return new();
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Program.Json) ?? throw new Exception("设置为空");
            if (loaded.Step < 1 || loaded.Step > 25 || !Enum.IsDefined(loaded.CloseAction) || loaded.Hotkeys == null || loaded.Hotkeys.Count != 8 || loaded.Hotkeys.Any(h => h == null || h.Key > 255 || h.Key == 0 || h.Modifiers > 15 || !new[] { "all", "internal", "external", "cursor" }.Contains(h.Target)))
                throw new Exception("设置内容无效");
            return loaded;
        }
        catch (Exception ex) { error = "设置读取失败，已使用默认值：" + ex.Message; return new(); }
    }
    internal void Save(string? filePath = null)
    {
        var path = filePath ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Program.Json));
        File.Move(temporary, path, true);
    }
}
