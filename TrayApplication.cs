namespace TrafficLens;

// All monitor windows share one tray icon. Hiding never stops their collectors.
internal static class TrayApplication
{
    private static NotifyIcon? icon;
    private static ContextMenuStrip? menu;
    private static Form? root;
    private static bool notified;
    internal static bool IconVisible => icon?.Visible == true;
    internal static bool Disposed => icon == null;

    public static void Initialize(Form main)
    {
        root = main;
        menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            menu.Items.Clear();
            foreach (Form window in Application.OpenForms)
            {
                var target = window;
                menu.Items.Add(window is ClashForm ? "打开 Clash 流量监控" : "打开系统流量监控", null, (_, _) => ShowWindow(target));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(StartupRegistration.MenuItem(main));
            menu.Items.Add("退出流量侦探（停止监控）", null, (_, _) => Exit());
        };
        icon = new NotifyIcon { Icon = main.Icon, Text = "流量侦探 1.6 · 后台监控", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => { if (root != null) ShowWindow(root); };
        icon.BalloonTipClicked += (_, _) => { if (root != null) ShowWindow(root); };
    }

    public static void HideWindow(Form window, bool notify = true)
    {
        window.Hide();
        if (notify && !notified && icon != null)
        {
            notified = true;
            icon.ShowBalloonTip(3000, "流量侦探仍在后台监控", "双击托盘图标打开窗口；右键选择“退出”才会停止监控。", ToolTipIcon.Info);
        }
    }

    internal static void ShowWindow(Form window)
    {
        if (window.IsDisposed) return;
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.BringToFront(); window.Activate();
    }

    internal static void OpenWindow<T>(Form owner) where T : Form, new()
    {
        var existing = Application.OpenForms.OfType<T>().FirstOrDefault();
        if (existing != null) ShowWindow(existing);
        else new T().Show(owner);
    }

    public static void Update(string text) { if (icon != null) icon.Text = text.Length > 63 ? text[..63] : text; }
    internal static void Exit() => Application.Exit();
    public static void Dispose()
    {
        if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
        menu?.Dispose(); menu = null; root = null;
    }
}

internal class TrayForm : Form
{
    private bool closeForReplacement;
    protected void CloseForReplacement() { closeForReplacement = true; Close(); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !closeForReplacement && TrayApplication.IconVisible)
        {
            e.Cancel = true;
            TrayApplication.HideWindow(this);
            return; // Do not invoke collector shutdown handlers on a tray hide.
        }
        base.OnFormClosing(e);
    }
}
