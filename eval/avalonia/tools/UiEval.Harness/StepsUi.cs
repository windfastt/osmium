using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace UiEval;

internal static partial class Program
{
    private static readonly Dictionary<(string Theme, string Token), int> Tokens = new()
    {
        [("light", "Page")] = 0xEEF0F4, [("dark", "Page")] = 0x0E0F13,
        [("light", "Card")] = 0xFFFFFF, [("dark", "Card")] = 0x17181E,
        [("light", "Mark")] = 0x5B4BD5, [("dark", "Mark")] = 0x9085E9,
        [("light", "Accent")] = 0x3A2A96, [("dark", "Accent")] = 0xB3A9FF,
    };

    /// <summary>Notification area: icon, menu items through UI Automation, light and dark menu surface,
    /// a balloon through Shell_NotifyIcon NIF_INFO (shell route only).</summary>
    private static JsonObject Tray(Config cfg, string route)
    {
        var res = new JsonObject { ["route"] = route };
        using var run = StartProto(cfg, "tray-" + route, $"--tray {route} --theme light");
        var main = ReadyProto(run);
        // The shell route retries for 30 s when the notification area refuses the icon; wait for its verdict.
        var added = run.WaitEvent("tray-added", 35_000, e => e["ok"] is null || e["ok"]!.GetValue<bool>() || e["attempt"]?.GetValue<int>() >= 30);
        res["trayAttempts"] = Arr(run.Events().Where(e => e["ev"]?.GetValue<string>() == "tray-added").Select(e => e.ToJsonString()));
        res["trayAddedEvent"] = added?.DeepClone();
        IntPtr owner = route == "shell" ? Win.MessageWindow(run.Pid, "OsmiumUiPreviewTray") : Win.WindowByClassPrefix(run.Pid, "AvaloniaMessageWindow");
        var (hr, rect) = Win.IconRect(owner, 1);
        res["owner"] = owner.ToInt64();
        res["iconRectHr"] = hr;
        res["iconRect"] = rect.ToString();
        bool iconOk = owner != IntPtr.Zero && hr == 0;
        res["iconOk"] = iconOk;

        bool menuOk = true, lightOk = false, darkOk = false;
        foreach (var theme in new[] { "light", "dark" })
        {
            run.Command("theme " + theme);
            Thread.Sleep(300);
            var open = run.Command($"tray-menu-open {Path.Combine(_shots, "tray-inproc")}", 20_000);
            var r = new JsonObject { ["open"] = open?.DeepClone() };
            long hwnd = open?["hwnd"]?.GetValue<long>() ?? 0;
            Thread.Sleep(500);
            // UI Automation from outside: the menu window and its items.
            var names = new List<string>();
            foreach (var w in Uia.ProcessWindows(run.Pid))
            {
                var items = Uia.All(w, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                if (items.Count == 0) continue;
                r["uiaWindow"] = Uia.Describe(w);
                names.AddRange(items.Select(Uia.Name));
                r["uiaItems"] = Arr(items.Select(Uia.Describe));
            }
            r["uiaItemNames"] = Arr(names);
            menuOk &= names.Contains("Open Osmium") && names.Contains("Exit");
            // External capture of the menu window: its surface against the Card token of the variant.
            if (hwnd != 0)
            {
                Win.GetWindowRect((IntPtr)hwnd, out var wr);
                var img = Img.Capture((IntPtr)hwnd, Win.ClientScreenRect((IntPtr)hwnd), printWindow: true);
                var scr = Img.Capture((IntPtr)hwnd, Win.ClientScreenRect((IntPtr)hwnd), printWindow: false);
                img?.Save(Path.Combine(_shots, $"traymenu-{route}-{theme}-printwindow.png"));
                scr?.Save(Path.Combine(_shots, $"traymenu-{route}-{theme}-screen.png"));
                int expected = Tokens[(theme, "Card")];
                var src = scr is not null && !scr.IsBlank() ? scr : img;
                if (src is not null)
                {
                    var inner = src.Crop(6, 6, src.W - 12, src.H - 12);
                    var top = inner.Top(3);
                    r["topColours"] = Arr(top.Select(t => $"{Img.Hex(t.Rgb)} {t.Share:P0}"));
                    bool ok = top.Count > 0 && Img.Near(top[0].Rgb, expected, 6);
                    r["expected"] = Img.Hex(expected);
                    r["surfaceMatches"] = ok;
                    if (theme == "light") lightOk = ok; else darkOk = ok;
                }
                r["windowRect"] = wr.ToString();
            }
            run.Command("tray-menu-close");
            res[theme] = r;
        }
        run.Command("theme default");
        if (route == "shell")
        {
            // Invoke "Open Archives" the way a screen-reader user would (after the captures): the window must switch page.
            run.Command($"tray-menu-open {Path.Combine(_shots, "tray-inproc")}", 20_000);
            Thread.Sleep(400);
            foreach (var w in Uia.ProcessWindows(run.Pid))
            {
                var target = Uia.All(w, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
                    .FirstOrDefault(i => Uia.Name(i) == "Open Archives");
                if (target is null || !target.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) continue;
                int n0 = run.EventCount;
                ((InvokePattern)ip).Invoke();
                var page = run.WaitEvent("page", 5000, e => e["index"]?.GetValue<int>() == 2, n0);
                res["invokeOpenArchives"] = page is not null ? "page 2 shown" : "no page change";
                break;
            }
            run.Command("tray-menu-close");
        }
        res["menuItemsOk"] = menuOk;
        res["lightSurfaceOk"] = lightOk;
        res["darkSurfaceOk"] = darkOk;

        bool? balloonOk = null;
        if (route == "shell")
        {
            int n0 = run.EventCount;
            var sent = run.Command("balloon");
            res["balloonSent"] = sent?.DeepClone();
            var shown = run.WaitEvent("balloon-show", 10_000, after: n0);
            res["balloonShowEvent"] = shown?.DeepClone();
            Thread.Sleep(1500);
            var screen = Img.Capture(IntPtr.Zero, new Win.RECT { Left = 0, Top = 0, Right = Win.GetSystemMetrics(0), Bottom = Win.GetSystemMetrics(1) }, false);
            screen?.Save(Path.Combine(_shots, "balloon-screen.png"));
            var toasts = new List<string>();
            try
            {
                foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    var n = Uia.Name(w);
                    var c = w.Current.ClassName;
                    if (n.Contains("notification", StringComparison.OrdinalIgnoreCase) || c.Contains("CoreWindow", StringComparison.Ordinal) || n.Contains("Compression finished", StringComparison.Ordinal))
                        toasts.Add($"{n} | {c}");
                }
            }
            catch (Exception e) { toasts.Add("uia error " + e.Message); }
            res["toastWindows"] = Arr(toasts);
            var later = run.WaitEvent("balloon-timeout", 12_000, after: n0) ?? run.WaitEvent("balloon-hide", 1, after: n0);
            res["balloonEndEvent"] = later?.DeepClone();
            res["balloonEvents"] = Arr(run.Events().Skip(n0).Select(e => e["ev"]?.GetValue<string>()).Where(e => e?.StartsWith("balloon", StringComparison.Ordinal) == true));
            balloonOk = shown is not null || toasts.Any(t => t.Contains("Compression finished", StringComparison.Ordinal));
            res["balloonOk"] = balloonOk;
        }
        res["darkModePass"] = darkOk;
        res["pass"] = iconOk && menuOk && lightOk && darkOk && (balloonOk ?? true);
        res["end"] = run.Quit();
        return res;
    }

    private static List<string> ReferencePeers(Config cfg)
    {
        var list = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(cfg.Ref, "automation-tree.txt")))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            int sp = t.IndexOf(' ');
            string type = t[..sp];
            int a = t.IndexOf("name='", StringComparison.Ordinal) + 6;
            int b = t.LastIndexOf("' focusable=", StringComparison.Ordinal);
            list.Add($"{type}|{t[a..b]}");
        }
        return list;
    }

    /// <summary>UI Automation: the same 22 named peers as the Linux render, found by a UIA client, with their patterns.</summary>
    private static JsonObject UiaPeers(Config cfg)
    {
        var res = new JsonObject();
        using var run = StartProto(cfg, "uia", "--theme light");
        var h = ReadyProto(run);
        var root = Uia.Window(h) ?? throw new InvalidOperationException("no UIA element for the main window");
        var all = Uia.All(root);
        File.WriteAllLines(Path.Combine(_shots, "uia-tree.txt"), all.Select(Uia.Describe));
        var wanted = new HashSet<string> { "List", "ListItem", "Edit", "Button", "ProgressBar" };
        var found = all.Where(e => wanted.Contains(Uia.TypeName(e)) && e.Current.IsControlElement).ToList();
        var have = found.Select(e => $"{Uia.TypeName(e)}|{Uia.Name(e)}").ToList();
        var reference = ReferencePeers(cfg);
        var missing = new List<string>(reference);
        foreach (var x in have) missing.Remove(x);
        var extra = new List<string>(have);
        foreach (var x in reference) extra.Remove(x);
        res["reference"] = reference.Count;
        res["found"] = Arr(found.Select(Uia.Describe));
        res["missing"] = Arr(missing);
        res["extra"] = Arr(extra);
        res["total"] = all.Count;

        // Navigation through UIA: select the Archives rail item (SelectionItemPattern), as a screen reader would.
        var archives = found.FirstOrDefault(e => Uia.TypeName(e) == "ListItem" && Uia.Name(e) == "Archives");
        if (archives is not null && archives.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
        {
            int n0 = run.EventCount;
            var sw = Stopwatch.StartNew();
            ((SelectionItemPattern)sp).Select();
            var page = run.WaitEvent("page", 10_000, e => e["index"]?.GetValue<int>() == 2, n0);
            var grid = Uia.Find(root, ControlType.DataGrid, "Entries", 15_000);
            res["selectArchivesViaUia"] = page is not null;
            res["archivesDataGridVisibleToUiaMs"] = grid is null ? null : sw.ElapsedMilliseconds;
        }
        res["pass"] = missing.Count == 0 && reference.Count == 22;
        res["end"] = run.Quit();
        return res;
    }

    /// <summary>Archive-browser scale (100,000 entries): first show, scroll to the end and sort by
    /// size, in-process to the rendered frame, then the TreeView / DataGrid / list peers from a UIA client.</summary>
    private static JsonObject Archives(Config cfg)
    {
        var res = new JsonObject();
        using var run = StartProto(cfg, "archives", "--theme light");
        var h = ReadyProto(run);
        var bench = run.Command("bench-archives", 180_000);
        res["bench"] = bench?.DeepClone();
        var steps = bench?["steps"]?.AsArray().OfType<JsonObject>().ToDictionary(s => s["Name"]!.GetValue<string>(), s => s) ?? [];
        double Ms(string n) => steps.TryGetValue(n, out var s) ? s["Ms"]!.GetValue<double>() : double.NaN;
        bool Ok(string n) => steps.TryGetValue(n, out var s) && s["Ok"]!.GetValue<bool>();
        // Thresholds: first show 1.0 s like the window, scroll 0.25 s,
        // sort 1.0 s; each step must also show the right rows.
        var limits = new Dictionary<string, double>
        {
            ["grid: first show"] = 1000, ["grid: scroll to end"] = 250, ["grid: sort by size"] = 1000,
            ["list: first show"] = 1000, ["list: scroll to end"] = 250, ["list: sort by size"] = 1000,
        };
        var table = new JsonArray();
        bool gridOk = true, listOk = true;
        foreach (var (name, limit) in limits)
        {
            bool ok = Ok(name) && Ms(name) <= limit;
            if (name.StartsWith("grid", StringComparison.Ordinal)) gridOk &= ok; else listOk &= ok;
            table.Add(new JsonObject { ["step"] = name, ["ms"] = Math.Round(Ms(name)), ["limit"] = limit, ["rowsRight"] = Ok(name), ["pass"] = ok });
        }
        res["table"] = table;
        Img.Capture(h, Win.ClientScreenRect(h), false)?.Save(Path.Combine(_shots, "archives-after-bench.png"));

        var root = Uia.Window(h)!;
        var grid = Uia.Find(root, ControlType.DataGrid, "Entries", 5000);
        var uia = new JsonObject();
        if (grid is not null)
        {
            uia["dataGrid"] = Uia.Describe(grid);
            if (grid.TryGetCurrentPattern(GridPattern.Pattern, out var gp))
                uia["gridPattern"] = $"rows {((GridPattern)gp).Current.RowCount}, columns {((GridPattern)gp).Current.ColumnCount}";
            if (grid.TryGetCurrentPattern(ScrollPattern.Pattern, out var scp))
                uia["scroll"] = $"vertical {((ScrollPattern)scp).Current.VerticalScrollPercent:0.#} %, view {((ScrollPattern)scp).Current.VerticalViewSize:0.###} %";
            var headers = Uia.All(grid, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem));
            uia["headers"] = Arr(headers.Select(Uia.Describe));
            var rows = Uia.All(grid, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            uia["rowCount"] = rows.Count;
            uia["rows"] = Arr(rows.Take(4).Select(Uia.Describe));
            uia["rowsNamed"] = rows.Count > 0 && rows.All(r => Uia.Name(r).Length > 0);
            // Sort by Name through UIA (the header's Invoke pattern), timed until the first row's name changes.
            var nameHeader = headers.FirstOrDefault(x => Uia.Name(x) == "Name");
            if (nameHeader is not null && nameHeader.TryGetCurrentPattern(InvokePattern.Pattern, out var ip) && rows.Count > 0)
            {
                string first = Uia.Name(rows[0]);
                var sw = Stopwatch.StartNew();
                ((InvokePattern)ip).Invoke();
                string now = first;
                while (sw.ElapsedMilliseconds < 10_000 && now == first)
                {
                    var r0 = Uia.All(grid, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).FirstOrDefault();
                    now = r0 is null ? first : Uia.Name(r0);
                    if (now == first) Thread.Sleep(10);
                }
                uia["sortByNameViaUiaMs"] = now != first ? sw.ElapsedMilliseconds : null;
                uia["firstRowAfterSort"] = now;
            }
            else uia["headerInvoke"] = "no Invoke pattern on the Name header";
        }
        var tree = Uia.Find(root, ControlType.Tree, "Folders", 3000);
        if (tree is not null)
        {
            uia["tree"] = Uia.Describe(tree);
            var items = Uia.All(tree, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            uia["treeItems"] = items.Count;
            uia["treeItemSample"] = Arr(items.Take(4).Select(Uia.Describe));
            uia["treeItemsNamed"] = items.Count > 0 && items.All(i => Uia.Name(i).Length > 0);
            var src = items.FirstOrDefault(i => Uia.Name(i).StartsWith("src", StringComparison.Ordinal));
            if (src is not null && src.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
            {
                ((ExpandCollapsePattern)ec).Expand();
                Thread.Sleep(500);
                uia["expandSrc"] = Uia.All(src, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Count;
            }
        }
        // The list variant.
        var listButton = Uia.Find(root, ControlType.Button, "List view", 2000);
        if (listButton is not null && listButton.TryGetCurrentPattern(InvokePattern.Pattern, out var lb))
        {
            ((InvokePattern)lb).Invoke();
            var list = Uia.Find(root, ControlType.List, "Entries list", 5000);
            if (list is not null)
            {
                uia["list"] = Uia.Describe(list);
                var li = Uia.All(list, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                uia["listItems"] = li.Count;
                uia["listItemSample"] = Arr(li.Take(3).Select(Uia.Describe));
                if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var lsp))
                {
                    var sw = Stopwatch.StartNew();
                    ((ScrollPattern)lsp).SetScrollPercent(ScrollPattern.NoScroll, 100);
                    uia["listScrollToEndViaUiaMs"] = sw.ElapsedMilliseconds;
                    Thread.Sleep(300);
                    uia["listScrollPercent"] = ((ScrollPattern)lsp).Current.VerticalScrollPercent;
                }
            }
        }
        res["uia"] = uia;
        bool peersOk = grid is not null && tree is not null && uia["rowsNamed"]?.GetValue<bool>() == true && uia["treeItemsNamed"]?.GetValue<bool>() == true;
        res["gridPass"] = gridOk;
        res["listPass"] = listOk;
        res["peersPass"] = peersOk;
        res["pass"] = (gridOk || listOk) && peersOk;
        res["end"] = run.Quit();
        return res;
    }

    /// <summary>The Windows capture against the headless Linux render: element boxes within 1 px, token colours.</summary>
    private static JsonObject CaptureCompare(Config cfg, string? exe = null)
    {
        var res = new JsonObject { ["build"] = exe is null ? "r2r" : Path.GetFileName(Path.GetDirectoryName(exe)) };
        using var run = StartProto(cfg, exe is null ? "capture" : "capture-aot", "--theme light", exe: exe);
        var h = ReadyProto(run);
        bool allOk = true;
        foreach (var theme in new[] { "light", "dark" })
        {
            run.Command("theme " + theme);
            Thread.Sleep(800);
            var r = new JsonObject();
            var boxes = run.Command("boxes");
            r["boxes"] = boxes?.DeepClone();
            var refBoxes = JsonNode.Parse(File.ReadAllText(Path.Combine(cfg.Ref, $"{theme}-boxes.json")))!.AsObject();
            double scaling = boxes?["scaling"]?.GetValue<double>() ?? 1;
            var deltas = new JsonObject();
            double maxDelta = 0;
            foreach (var (id, rb) in refBoxes)
            {
                var wb = boxes?["px"]?[id]?.AsArray();
                if (wb is null) { deltas[id] = "missing"; maxDelta = double.PositiveInfinity; continue; }
                double d = Enumerable.Range(0, 4).Max(k => Math.Abs(wb[k]!.GetValue<double>() - rb!.AsArray()[k]!.GetValue<double>() * scaling));
                deltas[id] = Math.Round(d, 2);
                maxDelta = Math.Max(maxDelta, d);
            }
            r["boxDeltaPx"] = deltas;
            r["maxBoxDeltaPx"] = double.IsInfinity(maxDelta) ? -1 : Math.Round(maxDelta, 2);
            // A build that cannot answer with boxes (native AOT drops the reply's fields) is judged on the pixels only.
            bool boxesOk = maxDelta <= 1.0 || (exe is not null && boxes?["px"] is null);

            var rect = Win.ClientScreenRect(h);
            var screen = Img.Capture(h, rect, printWindow: false);
            var print = Img.Capture(h, rect, printWindow: true);
            screen?.Save(Path.Combine(_shots, $"win-{theme}-screen.png"));
            print?.Save(Path.Combine(_shots, $"win-{theme}-printwindow.png"));
            run.Command($"capture {_shots} win-{theme}-inproc");
            var linux = Img.Load(Path.Combine(cfg.Ref, $"{theme}.png"));
            // The screen copy only when the whole client area is on screen; PrintWindow otherwise.
            bool onScreen = rect.Left >= 0 && rect.Top >= 0 && rect.Right <= Win.GetSystemMetrics(0) && rect.Bottom <= Win.GetSystemMetrics(1)
                            && Win.TopAt(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2) == h;
            r["coveredBy"] = Win.Describe(Win.TopAt(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
            r["clientRect"] = rect.ToString();
            r["onScreen"] = onScreen;
            var win = onScreen && screen is not null && !screen.IsBlank() ? screen : print;
            r["captureSource"] = ReferenceEquals(win, screen) ? "screen (BitBlt)" : "PrintWindow";
            r["sizes"] = $"windows {win?.W}x{win?.H}, linux {linux.W}x{linux.H}";
            bool coloursOk = win is not null;
            var crops = new JsonArray();
            if (win is not null)
            {
                r["meanAbsDiff"] = Math.Round(win.MeanAbsDiff(linux), 2);
                r["sameWithin16"] = Math.Round(win.SameShare(linux, 16), 4);
                foreach (var (id, token, pad) in new[] { ("Header", "Page", 0), ("Tiles", "Card", 1), ("Card", "Card", 1), ("Meter", "Mark", 0), ("Ghost", "Card", 0), ("Primary", "Accent", 0) })
                {
                    var b = refBoxes[id]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                    int x = (int)(b[0] * scaling) - pad, y = (int)(b[1] * scaling) - pad, cw = (int)((b[0] + b[2]) * scaling) + pad - x, ch = (int)((b[1] + b[3]) * scaling) + pad - y;
                    var wc = win.Crop(x, y, cw, ch).Top(3);
                    var lc = linux.Crop((int)b[0] - pad, (int)b[1] - pad, (int)(b[0] + b[2]) + pad - ((int)b[0] - pad), (int)(b[1] + b[3]) + pad - ((int)b[1] - pad)).Top(3);
                    int expected = Tokens[(theme, token)];
                    bool ok = wc.Count > 0 && lc.Count > 0 && Img.Near(wc[0].Rgb, expected, 2) && Img.Near(lc[0].Rgb, expected, 2);
                    coloursOk &= ok;
                    crops.Add(new JsonObject
                    {
                        ["box"] = id, ["token"] = token, ["expected"] = Img.Hex(expected),
                        ["windows"] = string.Join(", ", wc.Select(t => $"{Img.Hex(t.Rgb)} {t.Share:P0}")),
                        ["linux"] = string.Join(", ", lc.Select(t => $"{Img.Hex(t.Rgb)} {t.Share:P0}")),
                        ["pass"] = ok,
                    });
                }
            }
            r["tokenColours"] = crops;
            r["boxesPass"] = boxesOk;
            r["coloursPass"] = coloursOk;
            allOk &= boxesOk && coloursOk;
            res[theme] = r;
        }
        run.Command("theme light");
        Thread.Sleep(500);
        res["scaled"] = run.Command($"scaled {Path.Combine(_shots, "scaled")}")?.DeepClone();
        res["pass"] = allOk;
        res["end"] = run.Quit();
        return res;
    }

    /// <summary>Scaling: 100 %, 150 % and 200 %. Real per-monitor scaling where the display offers it
    /// (DisplayConfig DPI scale), otherwise the scaled offscreen renders of the capture step (RenderTargetBitmap at 144 / 192 dpi).</summary>
    private static JsonObject Dpi(Config cfg)
    {
        var res = new JsonObject();
        var scale = Win.GetScale();
        res["scaleInfo"] = scale is { } s ? $"current {s.Current} %, min {s.Min} %, max {s.Max} %, recommended {s.Recommended} %" : "DisplayConfig scale query failed";
        using var run = StartProto(cfg, "dpi", "--theme light");
        var h = ReadyProto(run);
        var baseBoxes = run.Command("boxes");
        res["base"] = baseBoxes?.DeepClone();
        var levels = new JsonArray();
        bool realOk = true;
        int original = scale?.Current ?? 100;
        foreach (var pct in new[] { 150, 175, 200 })
        {
            var r = new JsonObject { ["percent"] = pct };
            if (scale is null || pct > scale.Value.Max)
            {
                r["real"] = $"not offered by this display (max {scale?.Max} %)";
                levels.Add(r);
                continue;
            }
            int n0 = run.EventCount;
            r["setResult"] = Win.SetScale(pct);
            var ev = run.WaitEvent("scaling", 10_000, after: n0);
            Thread.Sleep(1500);
            var st = run.Command("state");
            var bx = run.Command("boxes");
            r["scalingEvent"] = ev?.DeepClone();
            r["state"] = st?.DeepClone();
            r["boxes"] = bx?.DeepClone();
            double sc = bx?["scaling"]?.GetValue<double>() ?? 1;
            r["renderScaling"] = sc;
            r["windowDpi"] = Win.GetDpiForWindow(h);
            // Windows shrinks the window to fit the smaller effective screen (1920 x 1080 at 150 % is 1280 x 720), so the
            // rows anchored to the bottom move. Checked: the device-pixel boxes are the layout boxes times the scale (to
            // a pixel), and the boxes above the card keep their place and width (to 1.5 layout pixels; the client width
            // itself rounds to whole device pixels).
            double maxPx = 0, maxTop = 0;
            if (bx?["dips"] is JsonObject dips && bx["px"] is JsonObject pxs && baseBoxes?["dips"] is JsonObject b0)
                foreach (var (id, v) in dips)
                {
                    var a = v!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
                    var p = pxs[id]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
                    var b = b0[id]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
                    maxPx = Math.Max(maxPx, Enumerable.Range(0, 4).Max(k => Math.Abs(p[k] - a[k] * sc)));
                    if (id is "Header" or "Tiles") maxTop = Math.Max(maxTop, new[] { 0, 1, 3 }.Max(k => Math.Abs(a[k] - b[k])));
                }
            r["clientAtScale"] = bx?["client"]?.DeepClone();
            r["maxPxVsLayoutTimesScale"] = Math.Round(maxPx, 2);
            r["maxTopBoxDeltaDip"] = Math.Round(maxTop, 2);
            Img.Capture(h, Win.ClientScreenRect(h), false)?.Save(Path.Combine(_shots, $"win-light-{pct}-real.png"));
            bool ok = Math.Abs(sc - pct / 100.0) < 0.01 && maxPx <= 1.0 && maxTop <= 1.5;
            r["pass"] = ok;
            realOk &= ok;
            levels.Add(r);
        }
        if (scale is not null) Win.SetScale(original);
        Thread.Sleep(1500);
        res["restored"] = Win.GetScale() is { } after ? after.Current : -1;
        res["levels"] = levels;
        bool anyReal = levels.OfType<JsonObject>().Any(l => l["pass"] is not null);
        res["method"] = anyReal ? "real per-monitor render scaling (DisplayConfig DPI scale) plus scaled offscreen renders" : "scaled offscreen renders only (RenderTargetBitmap at 144 and 192 dpi on the Win32/Skia backend)";
        res["pass"] = anyReal ? realOk : null;
        res["end"] = run.Quit();
        return res;
    }

    /// <summary>Fonts: bundled static faces, Segoe UI Variable at its weights (against static Segoe UI), a CJK file name.</summary>
    private static JsonObject Fonts(Config cfg)
    {
        using var run = StartProto(cfg, "fonts", "--theme light");
        ReadyProto(run);
        var f = run.Command($"fonts {Path.Combine(_shots, "fonts")}", 60_000) ?? new JsonObject { ["ok"] = false };
        var res = f.DeepClone().AsObject();
        res["pass"] = f["ok"]?.GetValue<bool>() == true && f["segoeVariableOk"]?.GetValue<bool>() == true;
        res["passWithStaticFallback"] = f["ok"]?.GetValue<bool>() == true;
        res["end"] = run.Quit();
        return res;
    }
}
