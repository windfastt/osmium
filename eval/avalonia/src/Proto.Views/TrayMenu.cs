using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Proto.Views;

/// <summary>One entry of the notification-area menu.</summary>
public sealed record TrayMenuEntry(string Header, Action? Click, bool Enabled = true, bool Bold = false)
{
    public static TrayMenuEntry Separator { get; } = new("-", null, false);
}

/// <summary>
/// The notification-area menu drawn by Avalonia, styled with the tokens (MenuFlyoutPresenter, as Avalonia's own
/// TrayIcon does on Windows) and following the app's light or dark variant. Closes when it loses activation.
/// </summary>
public sealed class TrayMenuWindow : Window
{
    private readonly PixelPoint _anchor;

    public TrayMenuWindow(IReadOnlyList<TrayMenuEntry> entries, PixelPoint anchor)
    {
        _anchor = anchor;
        Title = "Osmium notification menu";
        WindowDecorations = WindowDecorations.None;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = null;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        CanResize = false;
        var items = new List<Control>();
        foreach (var e in entries)
        {
            if (ReferenceEquals(e, TrayMenuEntry.Separator) || e.Header == "-")
            {
                items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = e.Header, IsEnabled = e.Enabled };
            if (e.Bold) item.FontWeight = FontWeight.SemiBold;
            if (e.Click is { } click) item.Click += (_, _) => { Close(); click(); };
            items.Add(item);
        }
        var presenter = new TrayMenuPresenter { ItemsSource = items };
        Avalonia.Automation.AutomationProperties.SetName(presenter, "Osmium notification menu");
        Content = presenter;
        Deactivated += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += (_, _) => Place();
    }

    /// <summary>Above and to the left of the anchor (the cursor on the taskbar), kept on the anchor's screen.</summary>
    private void Place()
    {
        var screen = Screens.ScreenFromPoint(_anchor) ?? Screens.Primary;
        double s = screen?.Scaling ?? 1;
        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * s), (int)Math.Ceiling(Bounds.Height * s));
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int x = Math.Clamp(_anchor.X - size.Width, area.X, Math.Max(area.X, area.Right - size.Width));
        int y = _anchor.Y - size.Height;
        if (y < area.Y) y = Math.Min(_anchor.Y, area.Bottom - size.Height);
        Position = new PixelPoint(x, y);
        Activate();
        if (Content is TrayMenuPresenter p && p.ItemCount > 0) p.Focus(NavigationMethod.Tab);
    }

    private sealed class TrayMenuPresenter : MenuFlyoutPresenter
    {
        protected override Type StyleKeyOverride => typeof(MenuFlyoutPresenter);

        public override void Close() => this.FindLogicalAncestorOfType<TrayMenuWindow>()?.Close();
    }
}
