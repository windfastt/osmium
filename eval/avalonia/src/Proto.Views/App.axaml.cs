using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Proto.Views;

public sealed class App : Application
{
    /// <summary>False when the host creates its windows itself (the Windows preview starts with or without one).</summary>
    public static bool CreateMainWindow { get; set; } = true;

    /// <summary>Startup stage marks for the host's log ("app-initialize-start", "window-ctor-done", ...).</summary>
    public static Action<string>? Trace { get; set; }

    public override void Initialize()
    {
        Trace?.Invoke("app-initialize-start");
        AvaloniaXamlLoader.Load(this);
        // Colours come from Osmium.Desktop.Theming.ColorTokens (plain C#, shared unchanged).
        Tokens.Install(Resources);
        Trace?.Invoke("app-initialize-done");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (CreateMainWindow && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
