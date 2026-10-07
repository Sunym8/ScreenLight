using Microsoft.Win32;

namespace ScreenLight;

// Only the current user's named application entry is changed. No administrator rights required.
internal sealed class StartupRegistration(string valueName = "ScreenLight")
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal sealed record Entry(object? Value, RegistryValueKind Kind);
    internal sealed record Snapshot(Entry Run, Entry Approval);

    internal static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['"', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("开机启动需要有效的程序完整路径。");
        var command = "\"" + executable + "\" --tray";
        if (command.Length > 260) throw new ArgumentException("程序路径过长，请将程序放到路径较短的固定文件夹中。");
        return command;
    }
    private Entry Read(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var value = key?.GetValue(valueName);
        return new Entry(value, value == null ? RegistryValueKind.None : key!.GetValueKind(valueName));
    }
    internal Snapshot Capture() => new(Read(RunKey), Read(ApprovalKey));
    internal bool IsEnabled(string executable)
    {
        var state = Capture();
        var disabled = state.Approval.Value is byte[] data && data.Length > 0 && data[0] is 3 or 7;
        return !disabled && string.Equals(state.Run.Value as string, BuildCommand(executable), StringComparison.OrdinalIgnoreCase);
    }
    private void Write(string keyPath, Entry entry)
    {
        if (entry.Value == null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
            key?.DeleteValue(valueName, false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
            key.SetValue(valueName, entry.Value, entry.Kind);
        }
    }
    internal void SetEnabled(bool enabled, string executable)
    {
        var command = BuildCommand(executable);
        if (enabled)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到开机启动程序，请重新选择程序的固定存放位置。", executable);
            Write(RunKey, new Entry(command, RegistryValueKind.String));
        }
        else Write(RunKey, new Entry(null, RegistryValueKind.None));
        // Reset only this app's previous Task Manager approval entry when the user saves a choice.
        Write(ApprovalKey, new Entry(null, RegistryValueKind.None));
        if (IsEnabled(executable) != enabled) throw new IOException("开机启动设置未能生效。");
    }
    internal void Restore(Snapshot snapshot) { Write(RunKey, snapshot.Run); Write(ApprovalKey, snapshot.Approval); }
}

internal static class GeneralOptions
{
    internal static void Apply(Settings settings, StartupRegistration startup, bool enabled, WindowCloseAction closeAction, string executable, string? settingsPath = null)
    {
        if (!Enum.IsDefined(closeAction)) throw new ArgumentException("无效的关闭窗口行为。");
        var before = startup.Capture();
        var oldStartup = settings.StartWithWindows;
        var oldClose = settings.CloseAction;
        try
        {
            startup.SetEnabled(enabled, executable);
            settings.StartWithWindows = enabled; settings.CloseAction = closeAction;
            settings.Save(settingsPath);
        }
        catch (Exception ex)
        {
            settings.StartWithWindows = oldStartup; settings.CloseAction = oldClose;
            try { startup.Restore(before); }
            catch (Exception restoreError) { throw new AggregateException("设置保存失败，开机启动恢复失败：" + restoreError.Message, ex, restoreError); }
            throw;
        }
    }
}
