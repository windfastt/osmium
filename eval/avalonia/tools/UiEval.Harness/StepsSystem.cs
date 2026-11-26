using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Microsoft.Win32;

namespace UiEval;

internal static partial class Program
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static void SetSystemTheme(bool light)
    {
        using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        k.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        k.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        Win.BroadcastSetting("ImmersiveColorSet");
    }

    /// <summary>Theme: Windows switches between light and dark while the preview runs; the preview follows
    /// (platform colour values, window variant, dark title bar, page colour on screen, and the tray menu surface).</summary>
    private static JsonObject ThemeSwitch(Config cfg)
    {
        var res = new JsonObject();
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        object? apps = key?.GetValue("AppsUseLightTheme"), sys = key?.GetValue("SystemUsesLightTheme");
        res["original"] = $"AppsUseLightTheme={apps}, SystemUsesLightTheme={sys}";
        SetSystemTheme(light: true);
        Thread.Sleep(1500);
        bool ok = true;
        try
        {
            using var run = StartProto(cfg, "theme");
            var h = ReadyProto(run);
            // The tray host's native menu with uxtheme AllowDark: it must follow the system theme too.
            using var host = StartProto(cfg, "theme-host", "--tray-host");
            host.WaitEvent("tray-added", 35_000, e => e["ok"]?.GetValue<bool>() == true);
            res["initial"] = run.Command("state")?.DeepClone();
            foreach (var (light, label) in new[] { (false, "Dark"), (true, "Light") })
            {
                var r = new JsonObject();
                int n0 = run.EventCount;
                var sw = Stopwatch.StartNew();
                SetSystemTheme(light);
                var ev = run.WaitEvent("colors", 15_000, e => e["when"]?.GetValue<string>() == "changed" && e["platformTheme"]?.GetValue<string>() == label, n0);
                r["detectedMs"] = ev is null ? null : sw.ElapsedMilliseconds;
                r["colorsEvent"] = ev?.DeepClone();
                var win = run.WaitEvent("window-theme", 5000, e => e["actual"]?.GetValue<string>() == label, n0);
                r["windowThemeEvent"] = win?.DeepClone();
                Thread.Sleep(800);
                var st = run.Command("state");
                r["state"] = st?.DeepClone();
                var img = Img.Capture(h, Win.ClientScreenRect(h), false);
                img?.Save(Path.Combine(_shots, $"theme-follow-{label.ToLowerInvariant()}.png"));
                int expected = Tokens[(light ? "light" : "dark", "Page")];
                // The page area right of the rail, below the header (Page token).
                var probe = img?.Crop(300, 150, 400, 6).Top(1);
                r["pageColour"] = probe is { Count: > 0 } ? Img.Hex(probe[0].Rgb) : null;
                r["expectedPage"] = Img.Hex(expected);
                bool pageOk = probe is { Count: > 0 } && Img.Near(probe[0].Rgb, expected, 3);
                var menu = run.Command($"tray-menu-open {Path.Combine(_shots, "tray-system-" + label.ToLowerInvariant())}", 20_000);
                r["trayMenu"] = menu?.DeepClone();
                run.Command("tray-menu-close");
                r["hostNativeMenu"] = NativeMenuCheck(host, light ? "light" : "dark", "host-follow-" + label.ToLowerInvariant());
                bool stepOk = ev is not null && win is not null && pageOk && st?["appTheme"]?.GetValue<string>() == label
                              && menu?["check"]?["match"]?.GetValue<bool>() == true;
                r["pass"] = stepOk;
                ok &= stepOk;
                res["to" + label] = r;
            }
            res["end"] = run.Quit();
            res["hostEnd"] = host.Quit();
        }
        finally
        {
            using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            if (apps is int a) k.SetValue("AppsUseLightTheme", a, RegistryValueKind.DWord);
            if (sys is int s) k.SetValue("SystemUsesLightTheme", s, RegistryValueKind.DWord);
            Win.BroadcastSetting("ImmersiveColorSet");
        }
        res["pass"] = ok;
        return res;
    }

    /// <summary>High contrast: high contrast is detected (contrast preference logged) while running and at start.</summary>
    private static JsonObject HighContrast(Config cfg)
    {
        var res = new JsonObject { ["before"] = Win.GetHighContrast().On };
        bool live = false, atStart = false, off = false;
        using (var run = StartProto(cfg, "hc"))
        {
            var h = ReadyProto(run);
            int n0 = run.EventCount;
            var sw = Stopwatch.StartNew();
            var set = RunWithTimeout(() => Win.SetHighContrast(true), 60_000);
            res["setOn"] = set is { } s1 ? $"ok={s1.Ok} error={s1.Error} after {sw.ElapsedMilliseconds} ms" : "timed out";
            var ev = run.WaitEvent("colors", 20_000, e => e["contrast"]?.GetValue<string>() == "High", n0);
            if (ev is null)
            {
                // Some sessions do not broadcast the change; send the setting names Avalonia listens for, as the shell would.
                Win.BroadcastSetting("WindowsThemeElement");
                Win.BroadcastSetting("ImmersiveColorSet");
                ev = run.WaitEvent("colors", 10_000, e => e["contrast"]?.GetValue<string>() == "High", n0);
                res["neededExtraBroadcast"] = true;
            }
            live = ev is not null;
            res["liveEvent"] = ev?.DeepClone();
            res["liveDetectedMs"] = ev is null ? null : sw.ElapsedMilliseconds;
            res["spiAfterSet"] = Win.GetHighContrast().On + " " + Win.GetHighContrast().Scheme;
            Thread.Sleep(1500);
            Img.Capture(h, Win.ClientScreenRect(h), false)?.Save(Path.Combine(_shots, "high-contrast-live.png"));
            res["stateLive"] = run.Command("state")?.DeepClone();

            using (var second = StartProto(cfg, "hc-start"))
            {
                var h2 = ReadyProto(second, 80, 60);
                var startColors = second.WaitEvent("colors", 5000, e => e["when"]?.GetValue<string>() == "startup");
                res["startEvent"] = startColors?.DeepClone();
                atStart = startColors?["contrast"]?.GetValue<string>() == "High";
                Img.Capture(h2, Win.ClientScreenRect(h2), false)?.Save(Path.Combine(_shots, "high-contrast-at-start.png"));
                res["endSecond"] = second.Quit();
            }

            n0 = run.EventCount;
            var unset = RunWithTimeout(() => Win.SetHighContrast(false), 60_000);
            res["setOff"] = unset is { } s2 ? $"ok={s2.Ok} error={s2.Error}" : "timed out";
            var offEv = run.WaitEvent("colors", 20_000, e => e["contrast"]?.GetValue<string>() == "NoPreference", n0);
            if (offEv is null)
            {
                Win.BroadcastSetting("WindowsThemeElement");
                offEv = run.WaitEvent("colors", 10_000, e => e["contrast"]?.GetValue<string>() == "NoPreference", n0);
            }
            off = offEv is not null;
            res["offEvent"] = offEv?.DeepClone();
            res["end"] = run.Quit();
        }
        if (Win.GetHighContrast().On) Win.SetHighContrast(false);
        res["detectedLive"] = live;
        res["detectedAtStart"] = atStart;
        res["followedOff"] = off;
        res["pass"] = live || atStart;
        res["note"] = "detection only: the preview has no high-contrast token set yet; Fluent's own high-contrast resources apply.";
        return res;
    }

    private static T? RunWithTimeout<T>(Func<T> f, int ms) where T : struct
    {
        var t = Task.Run(f);
        return t.Wait(ms) ? t.Result : null;
    }

    /// <summary>The 20-line CF_HDROP drag: a list row dragged out of the preview onto an OLE drop target, then the desktop.</summary>
    private static JsonObject Drag(Config cfg)
    {
        var res = new JsonObject();
        using var run = StartProto(cfg, "drag", "--theme light");
        var h = ReadyProto(run, 20, 20);
        var root = Uia.Window(h)!;
        var row = Uia.All(root, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .FirstOrDefault(e => Uia.Name(e).StartsWith("写真-2025.zip", StringComparison.Ordinal));
        if (row is null) throw new InvalidOperationException("the CJK row was not found through UIA");
        var rr = row.Current.BoundingRectangle;
        int sx = (int)(rr.X + 60), sy = (int)(rr.Y + rr.Height / 2);
        Win.GetWindowRect(h, out var wr);
        using var target = new DropTarget(wr.Right + 40, 140, 380, 300);
        res["target"] = target.Bounds.ToString();
        res["source"] = $"{sx},{sy}";
        int tx = target.Bounds.Left + target.Bounds.Width / 2, ty = target.Bounds.Top + target.Bounds.Height / 2;
        int n0 = run.EventCount;
        DragMouse(sx, sy, tx, ty);
        bool dropped = target.Dropped.Wait(8000);
        res["dropped"] = dropped;
        res["formats"] = Arr(target.Formats);
        res["files"] = Arr(target.Files ?? []);
        res["targetEnters"] = target.Enters;
        string? file = target.Files?.FirstOrDefault();
        bool exists = file is not null && File.Exists(file);
        res["fileExists"] = exists;
        res["fileText"] = exists ? File.ReadAllText(file!) : null;
        res["dragEvents"] = Arr(run.Events().Skip(n0).Where(e => e["ev"]?.GetValue<string>()?.StartsWith("drag", StringComparison.Ordinal) == true).Select(e => e.ToJsonString()));
        bool hdrop = target.Formats.Contains("FileDrop") || target.Formats.Contains("FileNameW");
        res["cfHdrop"] = hdrop;
        target.Dispose();

        // The everyday case: onto the desktop (Explorer's drop target). The file must appear in the Desktop folder.
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = file is null ? null : Path.GetFileName(file);
        if (name is not null)
        {
            var onDesktop = Path.Combine(desktop, name);
            if (File.Exists(onDesktop)) File.Delete(onDesktop);
            Win.SetForegroundWindow(h);
            Thread.Sleep(300);
            int dx = Win.GetSystemMetrics(0) - 160, dy = Win.GetSystemMetrics(1) - 260;
            DragMouse(sx, sy, dx, dy);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && !File.Exists(onDesktop)) Thread.Sleep(100);
            res["desktopDrop"] = File.Exists(onDesktop) ? $"file appeared in the Desktop folder after {sw.ElapsedMilliseconds} ms" : "no file appeared in the Desktop folder";
            Img.Capture(IntPtr.Zero, new Win.RECT { Left = 0, Top = 0, Right = Win.GetSystemMetrics(0), Bottom = Win.GetSystemMetrics(1) }, false)?.Save(Path.Combine(_shots, "drag-desktop.png"));
            try { if (File.Exists(onDesktop)) File.Delete(onDesktop); } catch (Exception) { }
        }
        res["pass"] = dropped && hdrop && exists;
        res["end"] = run.Quit();
        return res;
    }

    private static void DragMouse(int sx, int sy, int tx, int ty)
    {
        Win.MouseMove(sx, sy);
        Thread.Sleep(250);
        Win.MouseButton(down: true);
        Thread.Sleep(150);
        for (int i = 1; i <= 6; i++) { Win.MouseMove(sx + i * 3, sy + i * 2); Thread.Sleep(30); }
        const int steps = 30;
        for (int i = 1; i <= steps; i++)
        {
            Win.MouseMove(sx + (tx - sx) * i / steps, sy + (ty - sy) * i / steps);
            Thread.Sleep(30);
        }
        Thread.Sleep(400);
        Win.MouseMove(tx + 2, ty + 2);
        Thread.Sleep(300);
        Win.MouseButton(down: false);
        Thread.Sleep(500);
    }

    /// <summary>Tab order through the window, read back through UIA's focused element (the keyboard path through the window).</summary>
    private static JsonObject Keyboard(Config cfg)
    {
        var res = new JsonObject();
        using var run = StartProto(cfg, "keyboard", "--theme light");
        var h = ReadyProto(run);
        // A click on the page title (no control there) gives the window the keyboard, as a user's click would.
        var cr = Win.ClientScreenRect(h);
        Win.MouseMove(cr.Left + 420, cr.Top + 62);
        Thread.Sleep(100);
        Win.MouseButton(down: true);
        Win.MouseButton(down: false);
        Thread.Sleep(300);
        res["foreground"] = Win.GetForegroundWindow() == h;
        var seq = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            Win.Key(0x09);
            Thread.Sleep(200);
            try
            {
                var f = AutomationElement.FocusedElement;
                seq.Add($"{Uia.TypeName(f)} '{Uia.Name(f)}'");
            }
            catch (Exception e) { seq.Add("error " + e.Message); }
        }
        res["tabOrder"] = Arr(seq);
        res["distinct"] = seq.Distinct().Count();
        Img.Capture(h, Win.ClientScreenRect(h), false)?.Save(Path.Combine(_shots, "keyboard-focus.png"));
        res["pass"] = seq.Distinct().Count() >= 8 && seq.Any(s => s.Contains("'Compress'", StringComparison.Ordinal));
        res["end"] = run.Quit();
        return res;
    }

    private static JsonObject Verdicts()
    {
        JsonNode? P(string step, string field = "pass") => Results[step]?[field]?.DeepClone();
        return new JsonObject
        {
            ["launch_no_crash"] = P("startup", "launchPass"),
            ["first_frame"] = P("startup", "firstFramePass"),
            ["first_frame_screen_pixels"] = P("startup_visual"),
            ["idle_memory"] = P("memory"),
            ["tray_shell"] = P("tray_shell"),
            ["tray_shell_dark"] = P("tray_shell", "darkModePass"),
            ["balloon"] = P("tray_shell", "balloonOk"),
            ["tray_avalonia"] = P("tray_avalonia"),
            ["uia_22_peers"] = P("uia_peers"),
            ["capture_vs_linux"] = P("capture"),
            ["fonts"] = P("fonts"),
            ["fonts_with_static_fallback"] = P("fonts", "passWithStaticFallback"),
            ["theme_follow"] = P("theme_follow"),
            ["high_contrast"] = P("high_contrast"),
            ["drag_cf_hdrop"] = P("drag"),
            ["keyboard"] = P("keyboard"),
            ["dpi_real"] = P("dpi"),
            ["archives"] = P("archives"),
            ["trimmed_startup"] = P("trimmed_startup"),
        };
    }
}
