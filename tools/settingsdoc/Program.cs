using System.Globalization;
using System.Text;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

var s = new OsmiumSettings();
var all = SettingReflector.Describe(s);
var sb = new StringBuilder();
sb.AppendLine("# Osmium — Settings reference");
sb.AppendLine();
sb.AppendLine("Generated from the `[Setting]` metadata in `src/Osmium.Desktop/Settings/OsmiumSettings.cs` and the engine's parameter");
sb.AppendLine("definitions — the Settings page and the Tune dialog render exactly these. Stored in `%APPDATA%\\Osmium\\settings.json`");
sb.AppendLine("(portable mode: `.\\data\\settings.json`).");
sb.AppendLine();
sb.AppendLine($"**{all.Count} settings in {SettingReflector.Categories(s).Count} categories**, plus {MethodRegistry.Default.All.Sum(m => m.Params.Count)} codec/filter parameters usable in pipelines, rules and the Tune dialog.");
sb.AppendLine();
string Fmt(object? v) => v switch
{
    null => "",
    bool b => b ? "on" : "off",
    List<string> l => l.Count == 0 ? "(empty)" : string.Join(", ", l.Select(x => $"`{x}`")),
    string str => str.Length == 0 ? "(empty)" : $"`{str}`",
    _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
};
foreach (var cat in SettingReflector.Categories(s))
{
    sb.AppendLine($"## {cat.Name}");
    sb.AppendLine();
    if (cat.Description is not null) { sb.AppendLine(cat.Description); sb.AppendLine(); }
    sb.AppendLine("| Setting | Default | Range / choices | Notes |");
    sb.AppendLine("|---|---|---|---|");
    foreach (var d in all.Where(d => d.Category == cat.Name))
    {
        string range = d.Choices is { } c ? string.Join(" · ", c)
            : !double.IsNaN(d.Min) ? $"{d.Min.ToString("N0", CultureInfo.InvariantCulture)} – {d.Max.ToString("N0", CultureInfo.InvariantCulture)}{d.Unit}" : "";
        var notes = new List<string>();
        if (d.Experimental) notes.Add("**experimental**");
        if (d.Dangerous) notes.Add("**caution**");
        if (d.Advanced) notes.Add("advanced");
        if (d.RequiresRestart) notes.Add("restart");
        if (d.VisibleWhen is not null) notes.Add($"shown when `{d.VisibleWhen}`");
        if (d.EnabledWhen is not null) notes.Add($"enabled when `{d.EnabledWhen}`");
        string desc = (d.Description ?? "").Replace("|", "\\|");
        sb.AppendLine($"| **{d.Label}**<br>{desc} | {Fmt(d.Default)} | {range} | {string.Join(", ", notes)} |");
    }
    sb.AppendLine();
}
sb.AppendLine("## Watch-folder entries");
sb.AppendLine();
sb.AppendLine("Each watched folder (Settings → Watch folders) has: path, include subfolders, include/exclude patterns");
sb.AppendLine($"(default exclude `{new WatchFolderConfig().Exclude}`), settle time ({new WatchFolderConfig().SettleSeconds} s), minimum size, profile and output subfolder (`{new WatchFolderConfig().OutputSubfolder}`). The originals are kept unless");
sb.AppendLine($"**{all.Single(d => d.Key == "Compression.RecycleOriginals").Label}** (Compression) is on; even then nothing is ever deleted for good.");
sb.AppendLine();
sb.AppendLine("## Profiles");
sb.AppendLine();
sb.AppendLine("| Profile | Sample windows | Trial rungs | Candidates | Search evals | Block cap | CM | Dedup | Description |");
sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
foreach (var p in Profile.All)
    sb.AppendLine($"| `{p.Name}` | {p.SampleWindows} | {(p.Rungs.Length == 0 ? "—" : string.Join(" → ", p.Rungs.Select(ParamValues.FormatSize)))} | {p.MaxCandidates} | {p.SearchEvals} | {ParamValues.FormatSize(p.BlockCap)} | {(p.AllowCm ? $"yes (mem {p.CmMem})" : "no")} | {p.Dedup} | {p.Description} |");
sb.AppendLine();
sb.AppendLine("## Codec and filter parameters");
sb.AppendLine();
sb.AppendLine("Use them in pipeline strings (`lzma(dict=128M,lc=0,lp=2,pb=2)`), in rules (`\"params\": { \"lzma.dict\": \"256M\" }`) or in the Tune dialog.");
sb.AppendLine("Sizes accept K/M/G suffixes. \"Searched\" parameters are explored by the hill-climbing optimizer.");
sb.AppendLine();
foreach (var m in MethodRegistry.Default.All)
{
    sb.AppendLine($"### `{m.Name}` — {m.DisplayName} ({m.Kind.ToString().ToLowerInvariant()}, id {m.Id}){(m.Deterministic ? "" : " · output may differ between library versions")}");
    sb.AppendLine();
    sb.AppendLine(m.Description);
    sb.AppendLine();
    if (m.Params.Count == 0) { sb.AppendLine("No parameters."); sb.AppendLine(); continue; }
    sb.AppendLine("| Parameter | Default | Range | Searched values | Meaning |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var p in m.Params)
    {
        string f(long v) => p.Type == ParamType.Size ? ParamValues.FormatSize(v) : v.ToString("N0", CultureInfo.InvariantCulture);
        string searched = !p.Tunable ? "— (resource/structure)" : p.Dimension.Count > 1 ? string.Join(", ", p.Dimension.Select(f)) : "—";
        sb.AppendLine($"| `{p.Name}` | {f(p.Default)} | {f(p.Min)} – {f(p.Max)} | {searched} | {p.Label}. {p.Description.Replace("|", "\\|")}{(p.Experimental ? " **(experimental)**" : "")} |");
    }
    sb.AppendLine();
}
// Output: docs/SETTINGS.md in the repository (found from this project's location), or the path given as the first argument.
// With --check, exit 1 instead of writing when the file is out of date.
static string RepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Osmium.sln"))) return d.FullName;
    throw new DirectoryNotFoundException("Run this from inside the Osmium repository.");
}
bool check = args.Contains("--check") || args.Contains("check");
string target = args.FirstOrDefault(a => !a.StartsWith("--") && a != "check") ?? Path.Combine(RepoRoot(), "docs", "SETTINGS.md");
if (check)
{
    bool same = File.Exists(target) && File.ReadAllText(target) == sb.ToString();
    Console.WriteLine(same ? "docs/SETTINGS.md is up to date" : "docs/SETTINGS.md is out of date: run dotnet run --project tools/settingsdoc");
    return same ? 0 : 1;
}
File.WriteAllText(target, sb.ToString());
Console.WriteLine($"wrote SETTINGS.md: {all.Count} settings, {MethodRegistry.Default.All.Sum(m => m.Params.Count)} parameters");
return 0;
