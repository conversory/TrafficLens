using Microsoft.Win32;

namespace TrafficLens;

internal sealed record StartupState(bool Registered, bool Enabled, bool CurrentTarget, bool DisabledByWindows);

internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "TrafficLens";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal static event Action? Changed;

    internal static string BuildCommand(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidOperationException("自启程序需要有效的完整路径。");
        string command = "\"" + path + "\" --background --startup";
        if (command.Length > 260) throw new InvalidOperationException("程序路径过长，请将软件放在较短的固定目录后启用自启。");
        return command;
    }

    internal static StartupState Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        string command = key?.GetValue(ValueName) as string ?? "";
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
        bool disabled = approval?.GetValue(ValueName) is byte[] bytes && bytes.Length > 0 && bytes[0] is 3 or 7;
        bool registered = command.Length > 0;
        return new(registered, registered && !disabled,
            command.Equals(BuildCommand(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase), disabled && registered);
    }

    internal static StartupState SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(Environment.ProcessPath)) throw new InvalidOperationException("找不到当前程序文件。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            key.SetValue(ValueName, BuildCommand(Environment.ProcessPath!), RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(ValueName, false);
        }
        var state = Read();
        if (enabled != state.Registered) throw new InvalidOperationException("开机自启设置未能保存，请检查 Windows 权限。");
        Changed?.Invoke();
        return state;
    }

    internal static void Toggle(Form owner)
    {
        try
        {
            var state = Read();
            var next = SetEnabled(!state.Registered || state.DisabledByWindows || !state.CurrentTarget);
            if (next.DisabledByWindows)
                MessageBox.Show(owner, "启动项已登记，但 Windows 已将它禁用。请在“设置 → 应用 → 启动”中开启 TrafficLens。", "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(owner, "设置开机自启失败：" + ex.Message, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    internal static void Bind(CheckBox checkbox, Form owner)
    {
        var tip = new ToolTip();
        checkbox.AutoCheck = false;
        void Refresh()
        {
            if (owner.IsDisposed) return;
            try
            {
                var state = Read();
                checkbox.Checked = state.Enabled && state.CurrentTarget;
                checkbox.Text = state.DisabledByWindows ? "自启：系统已禁用" : state.Registered && !state.CurrentTarget ? "自启：点击更新版本" : "开机自启";
                tip.SetToolTip(checkbox, "登录 Windows 后自动在托盘监控；取消勾选即可关闭。请保留程序所在目录。" );
            }
            catch { checkbox.Enabled = false; tip.SetToolTip(checkbox, "无法读取 Windows 启动项设置。"); }
        }
        checkbox.Click += (_, _) => { Toggle(owner); Refresh(); };
        owner.VisibleChanged += (_, _) => { if (owner.Visible) Refresh(); };
        Changed += Refresh;
        owner.FormClosed += (_, _) => { Changed -= Refresh; tip.Dispose(); };
        Refresh();
    }

    internal static ToolStripMenuItem MenuItem(Form owner)
    {
        var item = new ToolStripMenuItem("开机自启（登录后在托盘监控）");
        try
        {
            var state = Read(); item.Checked = state.Enabled && state.CurrentTarget;
            if (state.DisabledByWindows) item.Text += " · 系统已禁用";
            else if (state.Registered && !state.CurrentTarget) item.Text += " · 点击更新版本";
        }
        catch { item.Enabled = false; }
        item.Click += (_, _) => Toggle(owner);
        return item;
    }

    // Exercise the real per-user entry, then restore its exact original value/kind.
    internal static object Verify()
    {
        using var originalKey = Registry.CurrentUser.OpenSubKey(RunKey);
        object? original = originalKey?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = original == null ? RegistryValueKind.String : originalKey!.GetValueKind(ValueName);
        try
        {
            string command = BuildCommand(@"C:\路径 有空格\TrafficLens.exe");
            if (command != "\"C:\\路径 有空格\\TrafficLens.exe\" --background --startup") throw new InvalidOperationException("自启路径引用测试失败。");
            var enabled = SetEnabled(true);
            if (!enabled.Registered || !enabled.CurrentTarget) throw new InvalidOperationException("启动项登记失败。");
            var disabled = SetEnabled(false);
            if (disabled.Registered || disabled.Enabled) throw new InvalidOperationException("启动项移除失败。");
            return new { Passed = true, EnabledReadBack = true, DisabledReadBack = true, QuotedUnicodePath = true, Scope = "HKCU Run / TrafficLens", OriginalEntryRestored = true };
        }
        finally
        {
            using var restore = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (original == null) restore.DeleteValue(ValueName, false);
            else restore.SetValue(ValueName, original, kind);
        }
    }
}
