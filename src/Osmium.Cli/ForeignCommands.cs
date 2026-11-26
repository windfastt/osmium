using System.Diagnostics;
using Osmium.Core.Archives;
using Osmium.Core.Container;
using Osmium.Core.Planning;
using Osmium.Core.Text;

namespace Osmium.Cli;

/// <summary>l, x, t and info for archives other than .osa (ZIP, 7z, TAR, single compressed files), through the common
/// archive interface; and `a name.zip`. Every name and comment from an archive is shown escaped.</summary>
internal static partial class Cli
{
    /// <summary>The kind of archive at <paramref name="path"/>, or null for .osa and for files that are no archive (both
    /// keep the .osa code path and its messages).</summary>
    private static ArchiveKind? ForeignKind(string path)
    {
        var kind = ArchiveSources.Detect(path);
        return kind is ArchiveKind.Osa ? null : kind;
    }

    private static string Esc(string s) => DisplayText.Escape(s);

    private static IArchiveSource OpenForeign(Args a, string path, CancellationToken ct) =>
        ArchiveSources.Open(path, new ArchiveOpenOptions
        {
            Password = () => Password(a) ?? PromptPassword(),
            Limits = ImportLimits.Default with { AllowLargeExpansion = a.Has("force") },
        }, ct);

    private static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
    }

    private static string Time(DateTime? utc) => $"{utc?.ToLocalTime():yyyy-MM-dd HH:mm}";

    private static int ListForeign(Args a, string path, CancellationToken ct)
    {
        using var s = OpenForeign(a, path, ct);
        PrintWarnings(s.Warnings);
        bool v = a.Has("v") || a.Has("verbose");
        int files = 0;
        foreach (var item in s.Items)
        {
            if (item.IsDirectory)
            {
                if (v) Console.WriteLine($"{"<dir>",14}  {Time(item.ModifiedUtc)}  {Esc(item.Path)}/");
                continue;
            }
            if (item.IsLink)
            {
                Console.WriteLine($"{"<link>",14}  {Time(item.ModifiedUtc)}  {Esc(item.Path)}{(item.LinkTarget is { } target ? " -> " + Esc(target) : "")}");
                continue;
            }
            if (item.IsSpecial)
            {
                Console.WriteLine($"{"<special>",14}  {Time(item.ModifiedUtc)}  {Esc(item.Path)}");
                continue;
            }
            files++;
            string size = item.Size is { } n ? n.ToString("N0") : "?";
            string extra = v ? $"  {item.Method}{(item.Encrypted ? "  encrypted" : "")}" : "";
            Console.WriteLine($"{size,14}  {Time(item.ModifiedUtc)}  {Esc(item.Path)}{extra}");
        }
        string total = s.TotalSize is { } t ? t.ToString("N0") : "?";
        string ratio = s.TotalSize is { } u ? $" ({(double)s.FileLength / Math.Max(1, u):P2})" : "";
        Console.WriteLine($"{total,14}  {files} file(s) in {Fmt.Bytes(s.FileLength)}{ratio}  — {s.FormatName}");
        return 0;
    }

    private static int InfoForeign(Args a, string path, CancellationToken ct)
    {
        using var s = OpenForeign(a, path, ct);
        PrintWarnings(s.Warnings);
        Console.WriteLine($"{"Archive:",-14}{s.Path}");
        foreach (var d in s.Details) Console.WriteLine($"{d.Label + ":",-14}{Esc(d.Value)}");
        return 0;
    }

    private static Func<ArchiveItem, bool>? Include(Args a)
    {
        if (a.Get("include") is not { } inc) return null;
        var rx = inc.Split(',').Select(Glob.ToRegex).ToList();
        return i => rx.Any(x => x.IsMatch(i.Path));
    }

    private static string ProgressText(string verb, ExtractProgress p) =>
        p.Current == "Checking the password" ? "Checking the password…"
        : p.BytesTotal > 0 ? $"{verb} {Fmt.Pct(p.BytesDone, p.BytesTotal)}  {p.FilesDone}/{p.FilesTotal} files"
        : $"{verb}… {Fmt.Bytes(p.BytesDone)} read";

    private static int TestForeign(Args a, string path, CancellationToken ct)
    {
        using var s = OpenForeign(a, path, ct);
        PrintWarnings(s.Warnings);
        var progress = new ProgressLine(a.Has("quiet"));
        var res = s.Test(Include(a), new Progress<ExtractProgress>(p => progress.Update(ProgressText("Testing", p))), ct);
        progress.Done();
        foreach (var e in res.Errors) Console.Error.WriteLine("error: " + e);
        PrintWarnings(res.Warnings);
        bool good = res.FilesFailed == 0 && res.LeftOut == 0;
        string notChecked = res.Unverified > 0 ? $"; {res.Unverified} of them have no stored checksum and were read but not checked" : "";
        Console.WriteLine(good
            ? $"OK — {res.FilesOk} file(s) read{notChecked}."
            : $"FAILED — {res.FilesFailed} file(s) damaged, {res.LeftOut - res.FilesFailed} not checked, {res.Errors.Count} error(s).");
        return good ? 0 : 2;
    }

    private static int ExtractForeign(Args a, string path, CancellationToken ct)
    {
        using var s = OpenForeign(a, path, ct);
        PrintWarnings(s.Warnings);
        string dest = a.Positional.Count > 1 ? a.Positional[1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ArchiveSources.StemOf(path));
        var mode = a.Has("overwrite") ? OverwriteMode.Overwrite : a.Has("skip") ? OverwriteMode.Skip : OverwriteMode.Rename;
        var progress = new ProgressLine(a.Has("quiet"));
        var sw = Stopwatch.StartNew();
        var res = s.Extract(dest, new ArchiveExtractRequest { Overwrite = mode, Select = Include(a) },
            new Progress<ExtractProgress>(p => progress.Update(ProgressText("Extracting", p))), ct);
        progress.Done();
        foreach (var e in res.Errors) Console.Error.WriteLine("error: " + e);
        PrintWarnings(res.Warnings);
        var parts = new List<string>();
        if (res.SkippedExisting > 0) parts.Add($"{res.SkippedExisting} skipped (already there)");
        if (res.Renamed > 0) parts.Add($"{res.Renamed} renamed");
        if (res.Unverified > 0) parts.Add($"{res.Unverified} without a stored checksum");
        if (res.FilesFailed > 0) parts.Add($"{res.FilesFailed} FAILED");
        if (res.SkippedUnsafe > 0) parts.Add($"{res.SkippedUnsafe} refused (unsafe names)");
        if (res.SkippedLinks > 0) parts.Add($"{res.SkippedLinks} links not created");
        if (res.SkippedUnsupported > 0) parts.Add($"{res.SkippedUnsupported} not supported");
        Console.WriteLine($"Extracted {res.FilesOk} file(s) to {dest} in {sw.Elapsed.TotalSeconds:F1}s{(parts.Count > 0 ? ", " + string.Join(", ", parts) : "")}");
        if (res.LeftOut > 0) Console.WriteLine($"{res.LeftOut} item(s) were not extracted; the errors above say why.");
        return res.FilesFailed == 0 && res.LeftOut == 0 ? 0 : 2;
    }

    // ------------------------------------------------------------ a name.zip, and names Osmium does not write

    /// <summary>Null for names that get an .osa archive; otherwise "zip", or the refusal for a type Osmium does not write.</summary>
    private static string? ArchiveTypeOf(string name, out string? refusal)
    {
        refusal = null;
        string n = name.ToLowerInvariant();
        if (n.EndsWith(".zip", StringComparison.Ordinal)) return "zip";
        if (n.EndsWith(".rar", StringComparison.Ordinal)) { refusal = "Osmium cannot create RAR archives."; return "refused"; }
        string? format =
            n.EndsWith(".7z", StringComparison.Ordinal) ? "7z"
            : n.EndsWith(".tar", StringComparison.Ordinal) || Path.GetExtension(Path.GetFileNameWithoutExtension(n)) == ".tar" && !n.EndsWith(Osa.Extension, StringComparison.Ordinal)
              || n.EndsWith(".tgz", StringComparison.Ordinal)
              || n.EndsWith(".tbz", StringComparison.Ordinal) || n.EndsWith(".tbz2", StringComparison.Ordinal) || n.EndsWith(".txz", StringComparison.Ordinal)
              || n.EndsWith(".tzst", StringComparison.Ordinal) ? "TAR"
            : n.EndsWith(".gz", StringComparison.Ordinal) ? "gzip"
            : n.EndsWith(".bz2", StringComparison.Ordinal) ? "bzip2"
            : n.EndsWith(".xz", StringComparison.Ordinal) ? "xz"
            : n.EndsWith(".zst", StringComparison.Ordinal) ? "Zstandard"
            : null;
        if (format is null) return null;
        refusal = $"Osmium cannot write {format} archives; use .osa or .zip.";
        return "refused";
    }

    private static async Task<int> CompressZip(Args a, string output, CancellationToken ct)
    {
        if (a.Has("password") || a.Has("password-stdin") || a.Has("encrypt"))
            return Fail("ZIP files made by Osmium are not encrypted; use an .osa archive with --password.");
        if (a.Has("profile")) return Fail("--profile does not apply to ZIP files, which Osmium writes with Deflate; use an .osa archive for profiles.");
        if (a.Has("pipeline")) return Fail("--pipeline does not apply to ZIP files; use an .osa archive to choose a pipeline.");
        bool quiet = a.Has("quiet") || a.Has("q");
        var progress = new ProgressLine(quiet);
        var sw = Stopwatch.StartNew();
        var scan = Scanner.Scan(a.Positional.Skip(1), new ScanOptions
        {
            Excludes = (a.Get("exclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        }, ct);
        if (scan.Files.Count == 0 && scan.Dirs.Count == 0) return Fail("nothing to compress");
        var result = await ZipCreator.CreateAsync(scan, new ZipOptions
        {
            OutputPath = output,
            Overwrite = a.Has("overwrite"),
            Verify = !a.Has("no-verify"),
            Progress = p => progress.Update($"Writing {Fmt.Pct(p.BytesDone, p.BytesTotal)}  [{p.FilesDone}/{p.FilesTotal}] {(p.Current is { } c ? Esc(c) : "")}"),
        }, ct);
        progress.Done();
        PrintWarnings(result.Warnings);
        if (!quiet)
        {
            Console.WriteLine($"Created {result.OutputPath}");
            Console.WriteLine($"  {result.FilesWritten} file(s): {result.FilesDeflated} compressed, {result.FilesStored} stored; {Fmt.Bytes(result.BytesIn)} → {Fmt.Bytes(result.BytesOut)} in {sw.Elapsed.TotalSeconds:F1}s");
        }
        if (result.LeftOut > 0)
        {
            Console.Error.WriteLine($"{result.LeftOut} file(s) were left out of the ZIP file: {result.LeftOutUnreadable} could not be read, {result.LeftOutChanged} changed while they were read.");
            return 2;
        }
        return 0;
    }
}
