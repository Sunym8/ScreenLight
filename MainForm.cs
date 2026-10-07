using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenLight;

internal sealed class KeyBox : TextBox
{
    internal uint KeyCode, Modifiers;
    internal KeyBox(HotkeyBinding binding)
    {
        ReadOnly = true; Width = 230; ShortcutsEnabled = false;
        KeyCode = binding.Key; Modifiers = binding.Modifiers; UpdateText();
        KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            KeyCode = (uint)e.KeyCode;
            Modifiers = (uint)((e.Alt ? 1 : 0) | (e.Control ? 2 : 0) | (e.Shift ? 4 : 0));
            UpdateText();
        };
    }
    private void UpdateText() => Text = new HotkeyBinding { Key = KeyCode, Modifiers = Modifiers }.Shortcut;
}

internal sealed class MainForm : Form
{
    private readonly DisplayService service = new();
    private readonly Settings settings;
    private readonly NotifyIcon tray;
    private readonly FlowLayoutPanel displayList = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
    private readonly RichTextBox diagnostics = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new("Microsoft YaHei UI", 10), DetectUrls = true };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 52, Padding = new(20, 8, 12, 8), ForeColor = Color.FromArgb(65, 79, 94) };
    private readonly Button refresh = Button("重新检测");
    private readonly NumericUpDown step = new() { Minimum = 1, Maximum = 25, Width = 70 };
    private readonly Dictionary<string, (TrackBar Slider, Label Value)> sliders = [];
    private readonly Dictionary<string, System.Windows.Forms.Timer> debounce = [];
    private readonly List<(CheckBox Enable, KeyBox Key)> keyEditors = [];
    private List<HotkeyBinding> registered = [];
    private bool scanning, quitting, busy, updating, resourcesDisposed;
    private readonly bool preview;
    private readonly System.Windows.Forms.Timer topologyTimer = new() { Interval = 1200 };
    private int activeOperations;
    private readonly string initialError;

    internal MainForm(bool preview = false)
    {
        this.preview = preview;
        settings = Settings.Load(out initialError);
        Text = "ScreenLight · 屏幕亮度";
        Font = new("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Color.FromArgb(30, 43, 58);
        ClientSize = new(800, 620); MinimumSize = new(720, 570);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        var header = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Color.FromArgb(27, 43, 66), Padding = new(22, 18, 20, 14) };
        header.Controls.Add(new Label { Text = "屏幕亮度", Font = new("Microsoft YaHei UI", 22, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new(22, 14) });
        header.Controls.Add(new Label { Text = "真实背光调节  /  独立控制每块屏幕  /  自定义全局快捷键", ForeColor = Color.FromArgb(187, 206, 229), AutoSize = true, Location = new(25, 66) });
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new(18, 8) };
        var screenTab = new TabPage("亮度") { BackColor = BackColor };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new(15, 8, 10, 4) };
        bar.Controls.Add(refresh); refresh.Click += async (_, _) => await Scan();
        var hide = Button("收起到托盘"); hide.Click += (_, _) => Hide(); bar.Controls.Add(hide);
        var exit = Button("退出程序"); exit.Click += (_, _) => Exit(); bar.Controls.Add(exit);
        screenTab.Controls.Add(displayList); screenTab.Controls.Add(bar);
        var keysTab = new TabPage("快捷键") { BackColor = BackColor, Padding = new(16) };
        BuildKeys(keysTab);
        var diagnosticsTab = new TabPage("检测与帮助") { BackColor = Color.White, Padding = new(16) };
        var diagBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48 };
        var export = Button("导出检测报告"); export.Click += (_, _) => ExportReport(); diagBar.Controls.Add(export);
        diagnosticsTab.Controls.Add(diagnostics); diagnosticsTab.Controls.Add(diagBar);
        tabs.TabPages.AddRange([screenTab, keysTab, diagnosticsTab]);
        Controls.Add(tabs); Controls.Add(header); Controls.Add(status);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开亮度面板", null, (_, _) => ShowPanel());
        menu.Items.Add("全部屏幕 +" + settings.Step + "%", null, async (_, _) => await Adjust("all", settings.Step));
        menu.Items.Add("全部屏幕 −" + settings.Step + "%", null, async (_, _) => await Adjust("all", -settings.Step));
        menu.Items.Add("重新检测", null, async (_, _) => await Scan());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ScreenLight · 屏幕亮度", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(); };
        FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2500, "ScreenLight 正在托盘运行", "快捷键仍然有效。右键托盘图标可退出。", ToolTipIcon.Info); } };
        topologyTimer.Tick += async (_, _) => { topologyTimer.Stop(); await Scan(); };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        Shown += async (_, _) =>
        {
            if (!preview)
            {
                var error = Register(settings.Hotkeys);
                if (error.Length > 0) { SetStatus(error); MessageBox.Show(this, error + "\n请在“快捷键”页更改并保存。", "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            await Scan();
            if (initialError.Length > 0) SetStatus(initialError);
        };
    }
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 32, Padding = new(9, 2, 9, 2), FlatStyle = FlatStyle.System, Margin = new(0, 0, 10, 0) };
    private void BuildKeys(TabPage page)
    {
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        content.Controls.Add(new Label { Text = "点选组合键框，再按下新的快捷键。勾选后点击保存即可生效。", AutoSize = true, Margin = new(0, 0, 0, 10) });
        foreach (var binding in settings.Hotkeys)
        {
            var row = new FlowLayoutPanel { Width = 680, Height = 30, Margin = new(0, 0, 0, 2) };
            var enable = new CheckBox { Checked = binding.Enabled, Text = binding.Caption + (binding.Delta > 0 ? "  提高" : "  降低"), Width = 210, Height = 30 };
            var key = new KeyBox(binding);
            keyEditors.Add((enable, key)); row.Controls.Add(enable); row.Controls.Add(key); content.Controls.Add(row);
        }
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 83 };
        var stepRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
        step.Value = settings.Step;
        stepRow.Controls.Add(new Label { Text = "每次调整", Width = 100, Height = 30, TextAlign = ContentAlignment.MiddleLeft }); stepRow.Controls.Add(step);
        stepRow.Controls.Add(new Label { Text = "%（1–25）", AutoSize = true, Padding = new(0, 5, 15, 0) });
        var save = Button("保存并启用快捷键"); save.Click += (_, _) => SaveKeys(); stepRow.Controls.Add(save);
        footer.Controls.Add(new Label { Text = "关闭窗口后仍可使用快捷键；退出程序后释放快捷键。\n若组合键被其他程序占用，会提示冲突。", Dock = DockStyle.Bottom, Height = 44, ForeColor = Color.FromArgb(92, 105, 120) });
        footer.Controls.Add(stepRow);
        page.Controls.Add(content); page.Controls.Add(footer);
    }
    private void SetStatus(string message) => status.Text = message;
    private void ShowPanel() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OnDisplayChanged(object? sender, EventArgs e) => ScheduleScan();
    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) ScheduleScan(); }
    private void ScheduleScan()
    {
        if (quitting || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (quitting) return; topologyTimer.Stop(); topologyTimer.Start(); }); } catch (InvalidOperationException) { }
    }
    private async Task Scan()
    {
        if (scanning || quitting) return;
        if (busy || activeOperations > 0) { topologyTimer.Start(); return; }
        scanning = true; activeOperations++; refresh.Enabled = false; displayList.Enabled = false;
        foreach (var timer in debounce.Values) timer.Stop();
        SetStatus("正在读取屏幕亮度接口…");
        try
        {
            await service.ScanAsync();
            BuildDisplays(); UpdateDiagnostics();
            SetStatus($"检测到 {service.Displays.Count} 块屏幕，{service.Displays.Count(d => d.Method != BrightnessMethod.None)} 块支持真实亮度调节。");
        }
        catch (Exception ex) { SetStatus("检测失败：" + ex.Message); }
        finally { scanning = false; activeOperations--; refresh.Enabled = true; displayList.Enabled = true; }
    }
    private void BuildDisplays()
    {
        foreach (var timer in debounce.Values) timer.Dispose(); debounce.Clear(); sliders.Clear();
        foreach (var control in displayList.Controls.Cast<Control>().ToArray()) control.Dispose();
        int number = 0;
        foreach (var display in service.Displays)
        {
            var card = new Panel { Width = 710, Height = 151, BackColor = Color.White, Margin = new(0, 0, 0, 12), Padding = new(16) };
            var supported = display.Method != BrightnessMethod.None;
            card.Controls.Add(new Label { Text = $"{++number:00}   {display.Name}", AutoSize = true, Font = new(Font, FontStyle.Bold), Location = new(17, 16) });
            card.Controls.Add(new Label { Text = display.Detail, AutoSize = false, Width = 610, Height = 38, Location = new(18, 45), ForeColor = supported ? Color.FromArgb(55, 105, 120) : Color.FromArgb(169, 79, 45) });
            var value = new Label { Text = supported ? display.Brightness + "%" : "不可用", Width = 75, Height = 35, Location = new(625, 89), Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = new(Font.FontFamily, 16), TextAlign = ContentAlignment.MiddleCenter };
            var slider = new TrackBar { Minimum = 0, Maximum = 100, Value = display.Brightness, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = settings.Step, Width = 580, Height = 40, Location = new(15, 89), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Enabled = supported && !preview };
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += async (_, _) => { timer.Stop(); await SetBrightness(display.Id, slider.Value); };
            slider.Scroll += (_, _) => { if (updating) return; value.Text = slider.Value + "%"; timer.Stop(); timer.Start(); };
            debounce.Add(display.Id, timer); sliders.Add(display.Id, (slider, value));
            card.Controls.Add(slider); card.Controls.Add(value); displayList.Controls.Add(card);
        }
        if (service.Displays.Count == 0) displayList.Controls.Add(new Label { Text = "未检测到桌面屏幕。请在本机桌面运行后重新检测。", AutoSize = true });
        displayList.Controls.Add(new Label { Text = "拖动滑块后自动应用。连接变化或休眠唤醒后会自动重新检测。", AutoSize = true, ForeColor = Color.FromArgb(96, 111, 127), Margin = new(3, 4, 0, 0) });
    }
    private void UpdateDiagnostics()
    {
        diagnostics.Text = "检测结果\n\n" + string.Join("\n\n", service.Displays.Select(d => $"{d.Name}\n{d.Id}\n{d.Device} · {d.Detail}\n当前亮度：{d.Brightness}%"))
            + (service.WmiError.Length > 0 ? "\n\nWMI 错误：" + service.WmiError : "")
            + "\n\n如果外接屏幕不可用：\n1. 在显示器实体菜单中开启 DDC/CI。\n2. 尽量使用 DP 或 HDMI 直连，检查线材及扩展坞。\n3. 退出其他亮度控制软件，再重新检测。\n4. 显示器断电重启；检查显卡驱动。\n\n本程序只调节真实背光，不使用屏幕遮罩。\n启动时读取当前亮度，不自动修改亮度。\n快捷键设置保存在：\n" + Settings.FilePath;
    }
    private async Task SetBrightness(string id, int percent, bool relative = false)
    {
        if (quitting || scanning || preview) return;
        activeOperations++;
        try
        {
            var actual = await service.SetAsync(id, percent, relative);
            UpdateSlider(id, actual);
            var display = service.Displays.First(d => d.Id == id);
            SetStatus(display.Name + "：" + actual + "%");
            tray.Text = "ScreenLight · " + actual + "%";
        }
        catch (Exception ex)
        {
            var display = service.Displays.FirstOrDefault(d => d.Id == id);
            if (display != null) UpdateSlider(id, display.Brightness);
            SetStatus(ex.Message); tray.ShowBalloonTip(2000, "亮度调整失败", ex.Message, ToolTipIcon.Warning);
        }
        finally { activeOperations--; }
    }
    private void UpdateSlider(string id, int value)
    {
        if (!sliders.TryGetValue(id, out var pair)) return;
        updating = true; pair.Slider.Value = Math.Clamp(value, 0, 100); pair.Value.Text = value + "%"; updating = false;
    }
    private async Task Adjust(string target, int delta)
    {
        if (busy || scanning || quitting || preview) return;
        busy = true;
        try
        {
            var point = Cursor.Position;
            var selected = service.Displays.Where(d => d.Method != BrightnessMethod.None && (target == "all" || (target == "internal" && d.Internal) || (target == "external" && !d.Internal) || (target == "cursor" && d.Bounds.Contains(point)))).ToArray();
            if (selected.Length == 0) { SetStatus("该目标没有可调节的屏幕，请重新检测。"); return; }
            foreach (var display in selected) await SetBrightness(display.Id, delta, true);
        }
        finally { busy = false; }
    }
    private string Register(List<HotkeyBinding> bindings)
    {
        Unregister();
        for (int i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (!binding.Enabled) continue;
            if (!Native.RegisterHotKey(Handle, 100 + i, binding.Modifiers | 0x4000, binding.Key))
            {
                var error = Marshal.GetLastWin32Error();
                for (int j = 0; j <= i; j++) Native.UnregisterHotKey(Handle, 100 + j);
                return "快捷键 " + binding.Shortcut + " 无法注册（错误 " + error + "），可能已被占用。";
            }
        }
        registered = bindings.Select(h => h.Copy()).ToList();
        return "";
    }
    private void Unregister() { if (IsHandleCreated) for (int i = 0; i < 8; i++) Native.UnregisterHotKey(Handle, 100 + i); registered = []; }
    private void SaveKeys()
    {
        var proposed = settings.Hotkeys.Select(h => h.Copy()).ToList();
        for (int i = 0; i < proposed.Count; i++)
        {
            proposed[i].Enabled = keyEditors[i].Enable.Checked;
            proposed[i].Key = keyEditors[i].Key.KeyCode;
            proposed[i].Modifiers = keyEditors[i].Key.Modifiers;
            proposed[i].Delta = Math.Sign(proposed[i].Delta) * (int)step.Value;
        }
        var enabled = proposed.Where(h => h.Enabled).ToArray();
        if (enabled.Any(h => (h.Modifiers & (1 | 2 | 8)) == 0)) { SetStatus("快捷键需要包含 Ctrl 或 Alt，避免影响正常打字。"); return; }
        if (enabled.GroupBy(h => (h.Modifiers, h.Key)).Any(g => g.Count() > 1)) { SetStatus("两个操作使用了相同的快捷键，请修改后保存。"); return; }
        if (preview) return;
        var previous = settings.Hotkeys.Select(h => h.Copy()).ToList();
        var oldStep = settings.Step;
        var error = Register(proposed);
        if (error.Length > 0) { var rollback = Register(previous); SetStatus(error + (rollback.Length > 0 ? " 恢复原快捷键失败：" + rollback : " 已恢复原快捷键。")); return; }
        try
        {
            settings.Hotkeys = proposed; settings.Step = (int)step.Value; settings.Save();
            foreach (var slider in sliders.Values) slider.Slider.LargeChange = settings.Step;
            tray.ContextMenuStrip!.Items[1].Text = "全部屏幕 +" + settings.Step + "%";
            tray.ContextMenuStrip.Items[2].Text = "全部屏幕 −" + settings.Step + "%";
            SetStatus("快捷键已保存并生效，每次调整 " + settings.Step + "%。");
        }
        catch (Exception ex) { settings.Hotkeys = previous; settings.Step = oldStep; var rollback = Register(previous); SetStatus("设置保存失败：" + ex.Message + (rollback.Length > 0 ? " " + rollback : " 已恢复原快捷键。")); }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && !preview)
        {
            var index = m.WParam.ToInt32() - 100;
            if (index >= 0 && index < registered.Count) { var binding = registered[index]; _ = Adjust(binding.Target, binding.Delta); }
        }
        if (m.Msg == 0x0011) quitting = true;
        base.WndProc(ref m);
    }
    private void ExportReport()
    {
        using var dialog = new SaveFileDialog { Filter = "检测报告 (*.json)|*.json", FileName = "ScreenLight-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, service.WmiError, Displays = service.Displays }, Program.Json)); SetStatus("检测报告已导出。"); }
        catch (Exception ex) { SetStatus("报告导出失败：" + ex.Message); }
    }
    internal async void Exit()
    {
        if (quitting) return;
        quitting = true; Unregister(); topologyTimer.Stop();
        foreach (var timer in debounce.Values) timer.Stop();
        Hide(); tray.Visible = false;
        // Finish outstanding native I/O before releasing physical monitor handles.
        while (activeOperations > 0) await Task.Delay(50);
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; SystemEvents.PowerModeChanged -= OnPowerChanged;
            Unregister(); topologyTimer.Dispose(); foreach (var timer in debounce.Values) timer.Dispose();
            tray.Dispose(); service.Dispose();
        }
        base.Dispose(disposing);
    }
}
