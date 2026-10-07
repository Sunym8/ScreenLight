using System.Collections;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScreenLight;

internal enum BrightnessMethod { None, Wmi, DdcHigh, DdcVcp }

internal sealed class Display
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Device { get; set; } = "";
    public Rectangle Bounds { get; set; }
    public bool Internal { get; set; }
    public BrightnessMethod Method { get; set; }
    public int Brightness { get; set; } = 100;
    public string Detail { get; set; } = "";
    public uint Minimum { get; set; }
    public uint Maximum { get; set; } = 100;
    public byte[] Levels { get; set; } = [];
    internal string? WmiInstance;
    internal IntPtr Physical;
}

internal sealed class DisplayService : IDisposable
{
    // Every scan/read/write runs through this gate: no DDC handle can be freed mid-write.
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<Display> displays = [];
    public string WmiError { get; private set; } = "";
    public IReadOnlyList<Display> Displays => displays;
    public async Task<IReadOnlyList<Display>> ScanAsync()
    {
        await gate.WaitAsync();
        try { return await Task.Run(Scan); }
        finally { gate.Release(); }
    }
    private List<Display> Scan()
    {
        ReleaseHandles();
        displays = [];
        var internalPanels = ReadWmi();
        var friendlyNames = ReadFriendlyNames();
        Native.MonitorEnum callback = (IntPtr monitor, IntPtr dc, ref Native.Rect bounds, IntPtr data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(monitor, ref info)) return true;
            var device = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
            Native.EnumDisplayDevices(info.Device, 0, ref device, 1);
            var panel = internalPanels.FirstOrDefault(p => SamePanel(device.Id ?? "", p.Instance));
            var id = NormalizeId(device.Id ?? "");
            if (id.Length == 0) id = info.Device;
            var display = new Display { Id = id, Device = info.Device, Name = friendlyNames.GetValueOrDefault(id, device.Description ?? info.Device), Bounds = info.Monitor.Bounds };
            if (panel != null)
            {
                display.Internal = true;
                display.Name = "笔记本内置屏幕";
                display.WmiInstance = panel.Instance;
                display.Method = BrightnessMethod.Wmi;
                display.Brightness = panel.Current;
                display.Levels = panel.Levels;
                display.Detail = "WMI · 真实背光";
                if (panel.Levels.Length > 0 && panel.Levels.Length < 10)
                    display.Detail += " · 驱动亮度档位：" + string.Join(" / ", panel.Levels);
                displays.Add(display);
                return true;
            }
            if (Native.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) && count is > 0 and < 32)
            {
                var physical = new Native.PhysicalMonitor[count];
                if (Native.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                {
                    // A cloned logical display may represent multiple physical monitors.
                    for (var index = 0; index < physical.Length; index++)
                    {
                        var item = index == 0 ? display : new Display { Id = id + "#physical" + index, Device = info.Device, Name = physical[index].Description, Bounds = display.Bounds };
                        item.Physical = physical[index].Handle;
                        if (count > 1) item.Name += " · " + (index + 1);
                        ReadDdc(item);
                        displays.Add(item);
                    }
                    return true;
                }
            }
            display.Detail = "未取得 DDC/CI 句柄（Windows 错误 " + Marshal.GetLastWin32Error() + "）";
            displays.Add(display);
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        // Never associate WMI with a screen merely because it is the primary display.
        foreach (var panel in internalPanels.Where(p => !displays.Any(d => d.WmiInstance == p.Instance)))
            displays.Add(new Display { Id = NormalizeId(panel.Instance), Name = "笔记本内置屏幕（未映射到桌面）", Internal = true, WmiInstance = panel.Instance, Method = BrightnessMethod.Wmi, Brightness = panel.Current, Levels = panel.Levels, Detail = "WMI · 真实背光 · 当前桌面位置未知" });
        return displays;
    }
    private sealed record WmiPanel(string Instance, int Current, byte[] Levels);
    internal static string NormalizeId(string value)
    {
        var match = Regex.Match(value.Replace('#', '\\'), @"DISPLAY\\[^\\]+\\[^\\]+", RegexOptions.IgnoreCase);
        return Regex.Replace(match.Value, @"_\d+$", "").ToUpperInvariant();
    }
    internal static bool SamePanel(string device, string instance) => NormalizeId(device) != "" && NormalizeId(device) == NormalizeId(instance);
    private List<WmiPanel> ReadWmi()
    {
        var result = new List<WmiPanel>();
        object? locator = null, service = null, collection = null;
        WmiError = "";
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\wmi");
            collection = ((dynamic)service).ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE");
            foreach (var obj in (IEnumerable)collection)
            {
                try
                {
                    dynamic item = obj;
                    var values = new List<byte>();
                    var levels = item.Properties_.Item("Level").Value;
                    if (levels is IEnumerable enumerable) foreach (var value in enumerable) values.Add(Convert.ToByte(value));
                    result.Add(new WmiPanel((string)item.Properties_.Item("InstanceName").Value, Convert.ToInt32(item.Properties_.Item("CurrentBrightness").Value), values.ToArray()));
                }
                finally { ReleaseCom(obj); }
            }
        }
        catch (Exception ex) { WmiError = ex.Message; }
        finally { ReleaseCom(collection); ReleaseCom(service); ReleaseCom(locator); }
        return result;
    }
    private static void ReleaseCom(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static Dictionary<string, string> ReadFriendlyNames()
    {
        var names = new Dictionary<string, string>();
        object? locator = null, service = null, collection = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\wmi");
            collection = ((dynamic)service).ExecQuery("SELECT * FROM WmiMonitorID WHERE Active = TRUE");
            foreach (var obj in (IEnumerable)collection)
            {
                try
                {
                    dynamic item = obj;
                    var raw = item.Properties_.Item("UserFriendlyName").Value;
                    if (raw is not IEnumerable chars) continue;
                    var name = new string(chars.Cast<object>().Select(Convert.ToInt32).Where(c => c > 0).Select(c => (char)c).ToArray());
                    if (!string.IsNullOrWhiteSpace(name)) names[NormalizeId((string)item.Properties_.Item("InstanceName").Value)] = name;
                }
                finally { ReleaseCom(obj); }
            }
        }
        catch { /* Friendly names are optional; detection still works without them. */ }
        finally { ReleaseCom(collection); ReleaseCom(service); ReleaseCom(locator); }
        return names;
    }
    private static bool ReadDdc(Display display)
    {
        if (Native.GetMonitorBrightness(display.Physical, out var min, out var current, out var max) && max > min && current >= min && current <= max)
        {
            display.Minimum = min; display.Maximum = max; display.Method = BrightnessMethod.DdcHigh;
            display.Brightness = Percent(current, min, max); display.Detail = "DDC/CI · 真实背光"; return true;
        }
        var highError = Marshal.GetLastWin32Error();
        if (Native.GetVCPFeatureAndVCPFeatureReply(display.Physical, 0x10, out _, out current, out max) && max > 0 && current <= max)
        {
            display.Minimum = 0; display.Maximum = max; display.Method = BrightnessMethod.DdcVcp;
            display.Brightness = Percent(current, 0, max); display.Detail = "DDC/CI VCP 0x10 · 真实背光"; return true;
        }
        display.Method = BrightnessMethod.None;
        display.Detail = "DDC/CI 无响应 · 高层错误 " + highError + " / VCP 错误 " + Marshal.GetLastWin32Error();
        return false;
    }
    internal static int Percent(uint value, uint min, uint max) => max <= min ? 100 : Math.Clamp((int)Math.Round((value - (double)min) * 100 / (max - min)), 0, 100);
    internal static uint Raw(int percent, uint min, uint max) => min + (uint)Math.Round(Math.Clamp(percent, 0, 100) * (max - min) / 100.0);
    // Respect drivers that expose only a small discrete set, including this laptop's 50/100 levels.
    internal static int Snap(int requested, byte[] levels, int previous)
    {
        requested = Math.Clamp(requested, 0, 100);
        if (levels.Length == 0) return requested;
        var sorted = levels.Select(v => (int)v).Distinct().Order().ToArray();
        if (requested > previous) return sorted.FirstOrDefault(v => v >= requested, sorted[^1]);
        if (requested < previous) return sorted.LastOrDefault(v => v <= requested, sorted[0]);
        return sorted.MinBy(v => Math.Abs(v - requested));
    }
    public async Task<int> SetAsync(string id, int requested, bool relative = false)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var display = displays.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("显示器已断开，请重新检测。");
                if (display.Method == BrightnessMethod.None) throw new InvalidOperationException("没有可用的真实背光接口，请检查 DDC/CI 或启用软件变暗。");
                // Read current hardware value so external changes (Fn keys/OSD) are not overwritten by stale state.
                if (display.Method == BrightnessMethod.Wmi)
                {
                    var currentPanel = ReadWmi().FirstOrDefault(p => p.Instance == display.WmiInstance);
                    if (currentPanel != null) display.Brightness = currentPanel.Current;
                }
                else if (!ReadDdc(display)) throw new InvalidOperationException(display.Detail);
                var percent = Math.Clamp(relative ? display.Brightness + requested : requested, 0, 100);
                if (display.Method == BrightnessMethod.Wmi)
                {
                    percent = Snap(percent, display.Levels, display.Brightness);
                    SetWmi(display.WmiInstance!, percent);
                    Thread.Sleep(150);
                    var actual = ReadWmi().FirstOrDefault(p => p.Instance == display.WmiInstance);
                    if (actual != null)
                    {
                        if (actual.Current != percent) throw new InvalidOperationException("笔记本驱动未应用目标亮度，当前为 " + actual.Current + "%。");
                        percent = actual.Current;
                    }
                }
                else
                {
                    var value = Raw(percent, display.Minimum, display.Maximum);
                    var ok = display.Method == BrightnessMethod.DdcHigh ? Native.SetMonitorBrightness(display.Physical, value) : Native.SetVCPFeature(display.Physical, 0x10, value);
                    if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "亮度写入失败，请重新检测或检查显示器 DDC/CI。");
                    percent = Percent(value, display.Minimum, display.Maximum);
                }
                display.Brightness = percent;
                return percent;
            });
        }
        finally { gate.Release(); }
    }
    private static void SetWmi(string instance, int brightness)
    {
        object? locator = null, service = null, collection = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\wmi");
            collection = ((dynamic)service).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            bool found = false;
            foreach (var obj in (IEnumerable)collection)
            {
                object? input = null, output = null;
                try
                {
                    dynamic item = obj;
                    if ((string)item.Properties_.Item("InstanceName").Value != instance) continue;
                    found = true;
                    input = item.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
                    ((dynamic)input).Properties_.Item("Timeout").Value = 0;
                    ((dynamic)input).Properties_.Item("Brightness").Value = (byte)brightness;
                    output = item.ExecMethod_("WmiSetBrightness", input);
                    // Some Windows providers expose no ReturnValue (including this machine).
                    // Actual brightness is read back by the caller after the write.
                    if (output != null)
                    {
                        foreach (var property in (IEnumerable)((dynamic)output).Properties_)
                        {
                            try
                            {
                                dynamic prop = property;
                                if ((string)prop.Name == "ReturnValue")
                                {
                                    var code = Convert.ToUInt32(prop.Value);
                                    if (code != 0) throw new InvalidOperationException("WMI 设置失败，返回码 " + code);
                                }
                            }
                            finally { ReleaseCom(property); }
                        }
                    }
                    return;
                }
                finally { ReleaseCom(output); ReleaseCom(input); ReleaseCom(obj); }
            }
            if (!found) throw new InvalidOperationException("没有找到内置屏幕亮度接口，请重新检测。");
        }
        finally { ReleaseCom(collection); ReleaseCom(service); ReleaseCom(locator); }
    }
    private void ReleaseHandles()
    {
        foreach (var display in displays) if (display.Physical != IntPtr.Zero) { Native.DestroyPhysicalMonitor(display.Physical); display.Physical = IntPtr.Zero; }
    }
    public void Dispose() { ReleaseHandles(); gate.Dispose(); }
}
