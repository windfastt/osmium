using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace UiEval;

internal static partial class Program
{
    /// <summary>The tray host's native menu (two-process layout): icon, items through UIA, and its light and dark look
    /// (uxtheme ForceLight / ForceDark here; AllowDark, which follows Windows, is checked in the theme step).</summary>
    private static JsonObject TrayHostMenu(Config cfg)
    {
        var res = new JsonObject();
        bool ok = true;
        foreach (var theme in new[] { "light", "dark" })
        {
            Apps.KillAll();
            Thread.Sleep(1000);
            using var host = StartProto(cfg, "tray-host-" + theme, $"--tray-host --theme {theme}");
            var r = new JsonObject();
            r["trayAdded"] = host.WaitEvent("tray-added", 35_000, e => e["ok"]?.GetValue<bool>() == true)?.DeepClone();
            r["darkMenus"] = host.WaitEvent("dark-menus", 2000)?.DeepClone();
            var check = NativeMenuCheck(host, theme, $"host-{theme}");
            r["menu"] = check;
            ok &= check["surfaceOk"]?.GetValue<bool>() == true && check["itemsOk"]?.GetValue<bool>() == true;
            r["end"] = host.Quit();
            res[theme] = r;
        }
        res["pass"] = ok;
        return res;
    }

    /// <summary>Opens the host's native menu, reads it through UIA, captures it and checks its surface brightness.</summary>
    internal static JsonObject NativeMenuCheck(AppRun host, string theme, string tag)
    {
        var r = new JsonObject();
        r["open"] = host.Command("tray-menu-open")?.DeepClone();
        IntPtr menu = IntPtr.Zero;
        for (int i = 0; i < 60 && menu == IntPtr.Zero; i++)
        {
            Thread.Sleep(50);
            menu = Win.TopWindows(host.Pid, true).FirstOrDefault(h => Win.ClassOf(h) == "#32768");
        }
        r["menuWindow"] = menu.ToInt64();
        if (menu == IntPtr.Zero) { r["surfaceOk"] = false; r["itemsOk"] = false; return r; }
        Thread.Sleep(400);
        var names = new List<string>();
        if (Uia.Window(menu) is { } el)
            names.AddRange(Uia.All(el, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)).Select(Uia.Name));
        r["uiaItems"] = Arr(names);
        r["itemsOk"] = names.Contains("Open Osmium") && names.Contains("Exit");
        Win.GetWindowRect(menu, out var wr);
        var img = Img.Capture(menu, wr, printWindow: false);
        img?.Save(Path.Combine(_shots, $"traymenu-{tag}.png"));
        if (img is not null)
        {
            var top = img.Crop(4, 4, img.W - 8, img.H - 8).Top(3);
            r["topColours"] = Arr(top.Select(t => $"{Img.Hex(t.Rgb)} {t.Share:P0}"));
            int c = top.Count > 0 ? top[0].Rgb : 0;
            double lum = 0.2126 * ((c >> 16) & 255) + 0.7152 * ((c >> 8) & 255) + 0.0722 * (c & 255);
            r["surfaceLuminance"] = Math.Round(lum);
            r["surfaceOk"] = theme == "dark" ? lum < 80 : lum > 200;
        }
        host.Command("tray-menu-close");
        Thread.Sleep(300);
        return r;
    }
}
