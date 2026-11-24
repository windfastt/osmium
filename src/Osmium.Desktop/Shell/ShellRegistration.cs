namespace Osmium.Desktop.Shell;

[Flags]
public enum ShellFeatures { None = 0, ContextMenu = 1, Association = 2, Startup = 4, All = ContextMenu | Association | Startup }

/// <summary>A registry value relative to the hive root (HKCU or HKLM). Name null = default value.</summary>
public sealed record RegistryValue(string Key, string? Name, string Value);

/// <summary>
/// The Explorer integration as data (snapshot-testable on Linux). The Windows app applies it under HKCU (per-user
/// install) or HKLM (all-users install) and the uninstaller removes exactly <see cref="OwnedKeys"/>.
/// No COM shell extension: classic verbs (Windows 11 shows them under "Show more options").
/// </summary>
public static class ShellRegistrationPlan
{
    public const string ProgId = "Osmium.Archive";
    public const string Classes = @"Software\Classes";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "Osmium";

    public static IReadOnlyList<string> OwnedKeys { get; } =
    [
        $@"{Classes}\{ProgId}",
        $@"{Classes}\*\shell\Osmium",
        $@"{Classes}\Directory\shell\Osmium",
        $@"{Classes}\Directory\Background\shell\Osmium",
        $@"{Classes}\.osa\OpenWithProgids",
    ];

    public static List<RegistryValue> Build(string exe, string archiveIcon, ShellFeatures features, IEnumerable<string> quickProfiles)
    {
        var v = new List<RegistryValue>();
        string q(string s) => $"\"{s}\"";
        string cmd(string args) => $"{q(exe)} {args}";

        if (features.HasFlag(ShellFeatures.Association))
        {
            v.Add(new($@"{Classes}\.osa", null, ProgId));
            v.Add(new($@"{Classes}\.osa", "Content Type", "application/x-osmium-archive"));
            v.Add(new($@"{Classes}\.osa", "PerceivedType", "compressed"));
            v.Add(new($@"{Classes}\.osa\OpenWithProgids", ProgId, ""));
            v.Add(new($@"{Classes}\{ProgId}", null, "Osmium Archive"));
            v.Add(new($@"{Classes}\{ProgId}\DefaultIcon", null, q(archiveIcon)));
            v.Add(new($@"{Classes}\{ProgId}\shell", null, "open"));
            AddVerb(v, $@"{Classes}\{ProgId}\shell\open", "Open with Osmium", cmd("--shell open \"%1\""), exe);
            AddVerb(v, $@"{Classes}\{ProgId}\shell\extracthere", "Extract here", cmd("--shell extract-here \"%1\""), exe);
            AddVerb(v, $@"{Classes}\{ProgId}\shell\extractto", "Extract to folder", cmd("--shell extract-to \"%1\""), exe);
            AddVerb(v, $@"{Classes}\{ProgId}\shell\test", "Test archive", cmd("--shell test \"%1\""), exe);
        }
        if (features.HasFlag(ShellFeatures.ContextMenu))
        {
            foreach (var root in new[] { $@"{Classes}\*\shell\Osmium", $@"{Classes}\Directory\shell\Osmium" })
            {
                v.Add(new(root, "MUIVerb", "Osmium"));
                v.Add(new(root, "SubCommands", ""));
                v.Add(new(root, "Icon", $"{q(exe)},0"));
                v.Add(new(root, "MultiSelectModel", "Player"));
                if (root.Contains(@"\*\")) v.Add(new(root, "AppliesTo", "NOT System.FileExtension:=.osa"));
                AddVerb(v, $@"{root}\shell\10add", "Add to archive…", cmd("--shell compress-dialog \"%1\""), exe);
                int n = 20;
                foreach (var p in quickProfiles.Take(5))
                {
                    var safe = new string(p.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    AddVerb(v, $@"{root}\shell\{n}quick{safe}", $"Compress to .osa ({char.ToUpperInvariant(safe[0])}{safe[1..]})",
                        cmd($"--shell compress-quick --profile {safe} \"%1\""), exe);
                    n++;
                }
            }
            AddVerb(v, $@"{Classes}\Directory\Background\shell\Osmium", "Compress this folder with Osmium", cmd("--shell compress-dialog \"%V\""), exe);
        }
        if (features.HasFlag(ShellFeatures.Startup))
            v.Add(new(RunKey, RunValue, cmd("--tray")));
        return v;
    }

    private static void AddVerb(List<RegistryValue> v, string key, string text, string command, string exe)
    {
        v.Add(new(key, "MUIVerb", text));
        v.Add(new(key, "MultiSelectModel", "Player"));
        v.Add(new(key, "Icon", $"\"{exe}\",0"));
        v.Add(new($@"{key}\command", null, command));
    }
}

/// <summary>Parses the app's own command line (shell verbs, tray mode, registration, self-test).</summary>
public sealed record AppCommand(string Kind, string? Verb = null, string? Profile = null, string[]? Paths = null, string? Scope = null, ShellFeatures Features = ShellFeatures.None, string? Arg = null)
{
    public static AppCommand Parse(string[] args)
    {
        if (args.Length == 0) return new("show");
        switch (args[0].ToLowerInvariant())
        {
            case "--tray": return new("tray");
            case "--quit": return new("quit");
            case "--selftest-ui": return new("selftest", Arg: args.Length > 1 ? args[1] : "selftest");
            case "--register":
            case "--unregister":
            {
                string scope = "user";
                var feats = ShellFeatures.None;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--scope" && i + 1 < args.Length) scope = args[++i];
                    else if (args[i] == "--features" && i + 1 < args.Length)
                        foreach (var f in args[++i].Split(','))
                            feats |= f.Trim().ToLowerInvariant() switch
                            {
                                "contextmenu" => ShellFeatures.ContextMenu, "assoc" => ShellFeatures.Association,
                                "startup" => ShellFeatures.Startup, "all" => ShellFeatures.All, _ => ShellFeatures.None,
                            };
                    else if (args[i] == "--all") feats = ShellFeatures.All;
                }
                return new(args[0].TrimStart('-').ToLowerInvariant(), Scope: scope, Features: feats == ShellFeatures.None ? ShellFeatures.All : feats);
            }
            case "--shell":
            {
                string verb = args.Length > 1 ? args[1] : "open";
                string? profile = null;
                var paths = new List<string>();
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--profile" && i + 1 < args.Length) profile = args[++i];
                    else paths.Add(Path.GetFullPath(args[i]));
                }
                return new("invoke", verb, profile, [.. paths]);
            }
            default:
                // Files passed directly (drag onto exe, "Open with"): open archives, otherwise compress dialog.
                var all = args.Where(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToArray();
                if (all.Length == 0) return new("show");
                return all.All(a => a.EndsWith(".osa", StringComparison.OrdinalIgnoreCase))
                    ? new("invoke", "open", null, all)
                    : new("invoke", "compress-dialog", null, all);
        }
    }
}
