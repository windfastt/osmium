using System.Globalization;
using Avalonia.Media;

namespace Proto.Views;

/// <summary>A navigation rail entry: its name and a 16x16 stroke icon.</summary>
public sealed record NavItem(string Name, Geometry Icon)
{
    public const int ArchivesIndex = 2;

    public static IReadOnlyList<NavItem> All { get; } =
    [
        new("Compress", G("M8 2v8M4.5 6.5 8 10l3.5-3.5M2.5 10.5v3h11v-3")),
        new("Jobs", G("M5.5 4h8.5M5.5 8h8.5M5.5 12h8.5M2.2 4h.1M2.2 8h.1M2.2 12h.1")),
        new("Archives", G("M2.5 6h11v7.5h-11zM1.5 2.5h13V6h-13zM6.2 9h3.6")),
        new("Lab", G("M5.5 1.8h5M6.5 1.8v4.4L2.8 13.6h10.4L9.5 6.2V1.8M4.6 10h6.8")),
        new("Rules", G("M2 4h7M12.5 4H14M10.8 2.4v3.2M2 8h1.5M6.8 8H14M5.2 6.4v3.2M2 12h8M13 12h1M11.5 10.4v3.2")),
        new("Cluster", G("M6.5 3a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0M1.8 12.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0M11.2 12.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0M8 4.5V8M4 11.3 8 8l4 3.3")),
        new("Settings", G("M5 8a3 3 0 1 0 6 0a3 3 0 1 0-6 0M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4")),
        new("Log", G("M3.5 1.5h6l3 3v10h-9zM9.5 1.5v3h3M5.5 8h5M5.5 10.5h5")),
    ];

    private static Geometry G(string data) => StreamGeometry.Parse(data);

    /// <summary>What UI Automation (screen readers) announces for the list item.</summary>
    public override string ToString() => Name;
}

/// <summary>A row of the Compress page's file list.</summary>
public sealed record FileRow(string Name, string Kind, string Size, string Plan, string Estimate)
{
    public override string ToString() => $"{Name}, {Kind}, {Size}, plan {Plan}, estimate {Estimate}";
}

/// <summary>A result tile (site .st: value, label, caption).</summary>
public sealed record Stat(string Value, string Label, string Caption);

/// <summary>One entry of the archive browser (a file inside an .osa). Sizes are bytes; texts are pre-formatted once.</summary>
public sealed class ArchiveEntry
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required string Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required long PackedBytes { get; init; }
    public required DateTime Modified { get; init; }
    public required string Method { get; init; }

    public string Size => Bytes(SizeBytes);
    public string Packed => Bytes(PackedBytes);
    public string Ratio => SizeBytes == 0 ? "–" : (100.0 * PackedBytes / SizeBytes).ToString("0.0", CultureInfo.InvariantCulture) + " %";
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string Path => Folder + "/" + Name;

    public static string Bytes(long b) => b switch
    {
        < 1024 => b.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>What UI Automation announces for a list row.</summary>
    public override string ToString() => $"{Name}, {Kind}, {Size}, packed {Packed}, {Folder}";
}

/// <summary>A folder node of the archive browser's tree.</summary>
public sealed class FolderNode
{
    public required string Name { get; init; }
    public List<FolderNode> Children { get; } = [];
    public int Files { get; set; }

    public override string ToString() => $"{Name}, {Files} files";
}

/// <summary>A deterministic sample archive: 100,000 entries in a two-level folder tree (no real archive is read).</summary>
public static class SampleArchive
{
    private static readonly (string Ext, string Kind, string Method, double Ratio)[] Kinds =
    [
        (".cs", "C# source", "dedup → text → cm2", 0.14), (".h", "C/C++ header", "dedup → text → cm2", 0.12),
        (".dll", "Library, x86-64", "x86 → lzma2:d64m", 0.38), (".png", "PNG image", "store", 1.0),
        (".wav", "Audio, PCM 16-bit", "audio → cm", 0.47), (".json", "JSON", "text → ppmd", 0.09),
        (".zip", "ZIP archive", "store", 1.0), (".exe", "Program, x86-64", "x86 → lzma2:d64m", 0.41),
        (".txt", "Text", "text → ppmd", 0.21), (".bmp", "Bitmap", "image → cm", 0.29),
    ];

    private static readonly string[] Top =
    [
        "src", "include", "assets", "docs", "tests", "tools", "build", "vendor", "samples", "data", "packages",
        "lib", "bin", "scripts", "locales", "images", "audio", "fixtures", "templates", "logs",
    ];

    public static (IReadOnlyList<ArchiveEntry> Entries, FolderNode Root) Build(int count = 100_000)
    {
        var rng = new Random(20261125);
        var root = new FolderNode { Name = "project-2026-11.osa" };
        var folders = new List<(string Path, FolderNode Node)>();
        foreach (var t in Top)
        {
            var top = new FolderNode { Name = t };
            root.Children.Add(top);
            for (int i = 0; i < 25; i++)
            {
                var sub = new FolderNode { Name = $"{t}-{i:00}" };
                top.Children.Add(sub);
                folders.Add(($"{t}/{sub.Name}", sub));
            }
        }
        var entries = new ArchiveEntry[count];
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var (path, node) = folders[i % folders.Count];
            var k = Kinds[rng.Next(Kinds.Length)];
            long size = (long)Math.Exp(rng.NextDouble() * 16.5) + rng.Next(64);
            long packed = Math.Max(1, (long)(size * k.Ratio * (0.7 + 0.6 * rng.NextDouble())));
            if (packed > size) packed = size;
            node.Files++;
            entries[i] = new ArchiveEntry
            {
                Index = i,
                Name = $"file-{i:000000}{k.Ext}",
                Folder = path,
                Kind = k.Kind,
                SizeBytes = size,
                PackedBytes = packed,
                Modified = epoch.AddMinutes(rng.Next(0, 480_000)),
                Method = k.Method,
            };
        }
        foreach (var top in root.Children) top.Files = top.Children.Sum(c => c.Files);
        root.Files = count;
        return (entries, root);
    }
}
