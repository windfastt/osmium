using System.Diagnostics;
using System.Security.Principal;
using Osmium.Desktop.Ipc;
using Osmium.Desktop.Settings;
using Osmium.Desktop.Shell;

namespace Osmium.App;

internal static class Program
{
    public const string AppUserModelId = "Osmium.Desktop";

    [STAThread]
    private static int Main(string[] args)
    {
        var cmd = AppCommand.Parse(args);
        try { Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch (Exception) { }

        switch (cmd.Kind)
        {
            case "register":
                return Registration.Apply(cmd.Scope, cmd.Features, new SettingsStore(AppPaths.SettingsFile).Load().Integration.QuickProfiles);
            case "unregister":
                return Registration.Remove(cmd.Scope);
            case "selftest":
                ApplicationConfiguration.Initialize();
                return SelfTest.Run(cmd.Arg ?? "selftest");
        }

        string user = UserKey();
        string pipe = IpcProtocol.PipeName(user, Process.GetCurrentProcess().SessionId);
        using var single = new Mutex(true, $@"Local\Osmium.App.{user}", out bool primary);
        if (!primary)
        {
            // Forward before touching WinForms: Explorer may start dozens of these processes at once.
            var msg = cmd.Kind switch
            {
                "quit" => new IpcMessage { Type = "quit" },
                "invoke" => new IpcMessage { Verb = cmd.Verb, Profile = cmd.Profile, Args = cmd.Paths ?? [], Cwd = Environment.CurrentDirectory },
                "tray" => new IpcMessage { Type = "ping" },
                _ => new IpcMessage { Type = "show" },
            };
            bool ok = IpcProtocol.ForwardAsync(pipe, msg, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return ok ? 0 : 2;
        }
        if (cmd.Kind == "quit") return 0;
        // Sentinel the installer looks for (CheckForMutexes) to close us gracefully before upgrading.
        using var running = new Mutex(false, "Osmium.App.Running");

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI error: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Osmium — unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("fatal: " + e.ExceptionObject);
            TrayApp.Current?.HideIcon();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Warn("background task: " + e.Exception.Message); e.SetObserved(); };

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var settings = settingsStore.Load();
        Theme.Initialize(settings.General.Theme);
        if (settingsStore.LastError is { } err) AppLog.Warn($"settings were unreadable and have been reset ({err}); a backup was kept.");

        using var app = new TrayApp(settingsStore, settings, cmd, pipe);
        Application.Run(app);
        return 0;
    }

    private static string UserKey()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { return Environment.UserName; }
    }
}
