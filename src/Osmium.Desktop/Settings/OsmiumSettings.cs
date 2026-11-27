using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osmium.Core.Crypto;

namespace Osmium.Desktop.Settings;

public enum ThemeMode { System, Light, Dark }
public enum NotificationMode { All, FailuresOnly, None }
public enum OutputLocation { NextToSource, Folder, Ask }
public enum DecodeSpeed { None, Light, Strong }
public enum DedupSetting { Auto, Off, Files, Chunks }
public enum PriorityLevel { Idle, BelowNormal, Normal }
public enum CloseAction { MinimizeToTray, Exit }
public enum ProfileName { Store, Fast, Balanced, Max, Ultra, Insane }
/// <summary>How new encrypted archives derive their key (<see cref="SecuritySettings.Kdf"/>).</summary>
public enum PasswordProtection { Standard, Strong, Maximum, Compatible }

[SettingsCategory("General", Order = 0, Glyph = "", Description = "Appearance, startup and notifications.")]
public sealed class GeneralSettings
{
    [Setting("Theme", Description = "Follow Windows, or force light/dark.", Order = 0, RequiresRestart = true)]
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    [Setting("Start with Windows", Description = "Start Osmium in the notification area when you sign in.", Order = 1)]
    public bool StartWithWindows { get; set; } = true;
    [Setting("Close button", Description = "What the window's close button does.", Order = 2)]
    public CloseAction CloseButton { get; set; } = CloseAction.MinimizeToTray;
    [Setting("Notifications", Description = "Balloon notifications when jobs finish.", Order = 3)]
    public NotificationMode Notifications { get; set; } = NotificationMode.All;
    [Setting("Confirm exit while jobs run", Order = 4)]
    public bool ConfirmExit { get; set; } = true;
    [Setting("Default output", Description = "Where new archives go.", Order = 5)]
    public OutputLocation Output { get; set; } = OutputLocation.NextToSource;
    [Setting("Output folder", Editor = SettingEditor.FolderPath, VisibleWhen = "Output=Folder", Order = 6)]
    public string OutputFolder { get; set; } = "";
    [Setting("Open folder when finished", Order = 7)]
    public bool OpenFolderWhenDone { get; set; }
}

[SettingsCategory("Compression", Order = 1, Glyph = "", Description = "Defaults for new archives.")]
public sealed class CompressionSettings
{
    [Setting("Default profile", Description = "Store → Insane: higher profiles try more algorithms and take longer.", Order = 0, Keywords = "level preset")]
    public ProfileName Profile { get; set; } = ProfileName.Balanced;
    [Setting("Decode-speed weight", Description = "Penalise pipelines that are slow to extract (context mixing decompresses at about 0.3 MB/s).", Order = 1)]
    public DecodeSpeed DecodeSpeedWeight { get; set; } = DecodeSpeed.None;
    [Setting("Deduplication", Description = "Auto = profile default. Chunks finds repeats inside and across files (slower to plan).", Order = 2)]
    public DedupSetting Dedup { get; set; } = DedupSetting.Auto;
    [Setting("Solid block size", Description = "0 = profile default. Larger blocks compress better but need more memory and limit parallelism.", Min = 0, Max = 1024, Unit = " MiB", Order = 3)]
    public int BlockSizeMiB { get; set; }
    [Setting("Verify every block", Description = "Decode each block and compare before writing it. Strongly recommended.", Order = 4, Dangerous = true,
        WarningText = "Turning verification off means a codec bug could produce an archive that does not extract.", WarnWhenOff = true)]
    public bool VerifyBlocks { get; set; } = true;
    [Setting("Store planner log in archive", Description = "Lets 'Explain' show why each pipeline was chosen (archive-relative paths only).", Order = 5)]
    public bool StorePlanLog { get; set; } = true;
    [Setting("Exclude patterns", Description = "One glob per line, e.g. *.tmp or node_modules/**", Editor = SettingEditor.StringList, Order = 6)]
    public List<string> Excludes { get; set; } = ["Thumbs.db", "desktop.ini", "*.osmium-part-*", "*.osa.partial-*"];
    [Setting("Include hidden files", Order = 7)]
    public bool IncludeHidden { get; set; } = true;
    [Setting("After a verified compression, move the originals to the Recycle Bin",
        Description = "Needs Verify every block. Only files and folders the archive holds completely and unchanged move, and only on drives with a Recycle Bin; the job's log says why anything stays. "
            + "When the Recycle Bin is full, Windows makes room by removing its oldest items, as it does for every delete.",
        Order = 8, Dangerous = true, Keywords = "delete remove trash recycle bin originals sources",
        WarningText = "Osmium moves the files it compressed to the Recycle Bin after the archive is written and every block is verified. Files that were not compressed, and drives without a Recycle Bin, are never touched.")]
    public bool RecycleOriginals { get; set; }
}

[SettingsCategory("Codecs", Order = 2, Glyph = "", Description = "Which algorithms the planner may use, and their resource limits.")]
public sealed class CodecSettings
{
    [Setting("Allow context mixing (CM)", Description = "Osmium CM (cm2; cm in older archives and explicit pipelines): smallest output, but compresses AND extracts slowly.", Order = 0)]
    public bool AllowCm { get; set; } = true;
    [Setting("Allow PPMd", Order = 1)] public bool AllowPpmd { get; set; } = true;
    [Setting("Allow LZMA", Order = 2)] public bool AllowLzma { get; set; } = true;
    [Setting("Allow Zstandard", Order = 3)] public bool AllowZstd { get; set; } = true;
    [Setting("Allow Brotli", Order = 4)] public bool AllowBrotli { get; set; } = true;
    [Setting("Allow BZip2", Order = 5)] public bool AllowBzip2 { get; set; } = true;
    [Setting("CM model memory", Description = "log2 of 64-byte table lines: 20 = 64 MiB, 22 = 256 MiB, 24 = 1 GiB. 0 = profile default. Extraction needs the same memory.", Min = 0, Max = 24, Order = 6, Advanced = true)]
    public int CmMemoryBits { get; set; }
    [Setting("LZMA dictionary", Description = "0 = profile default.", Min = 0, Max = 512, Unit = " MiB", Order = 7, Advanced = true)]
    public int LzmaDictMiB { get; set; }
    [Setting("Zstandard level", Description = "0 = profile default.", Min = 0, Max = 22, Order = 8, Advanced = true)]
    public int ZstdLevel { get; set; }
}

[SettingsCategory("Filters", Order = 3, Glyph = "", Description = "Reversible pre-processing that makes data more compressible.")]
public sealed class FilterSettings
{
    [Setting("x86/x64 branch converter", Description = "For .exe/.dll/.so: CALL/JMP targets (x86), and for x64 code RIP-relative addresses too (x86v2).", Order = 0)] public bool X86 { get; set; } = true;
    [Setting("ARM64 branch converter", Order = 1)] public bool Arm64 { get; set; } = true;
    [Setting("Delta (records, tables)", Order = 2)] public bool Delta { get; set; } = true;
    [Setting("Byte-plane transpose", Order = 3)] public bool Transpose { get; set; } = true;
    [Setting("PCM audio predictor", Description = "Uncompressed .wav.", Order = 4)] public bool Pcm { get; set; } = true;
    [Setting("Image predictor", Description = "Uncompressed .bmp.", Order = 5)] public bool Image { get; set; } = true;
    [Setting("Text dictionary + capitalization", Order = 6)] public bool Text { get; set; } = true;
}

[SettingsCategory("Analysis", Order = 4, Glyph = "", Description = "How hard the planner searches for the best pipeline.")]
public sealed class AnalysisSettings
{
    [Setting("Sample windows per file", Description = "0 = profile default. More windows = better detection, slower start.", Min = 0, Max = 512, Order = 0)]
    public int SampleWindows { get; set; }
    [Setting("Candidates per group", Description = "0 = profile default. Upper bound of pipelines raced by successive halving.", Min = 0, Max = 200, Order = 1)]
    public int MaxCandidates { get; set; }
    [Setting("Parameter-search evaluations", Description = "-1 = profile default, 0 = off. Hill-climbing steps per group.", Min = -1, Max = 1000, Order = 2)]
    public int SearchEvals { get; set; } = -1;
    [Setting("Race top-2 on full blocks", Description = "Compress every block twice with the two best pipelines and keep the smaller (Insane does this).", Order = 3)]
    public bool RaceTopTwo { get; set; }
}

[SettingsCategory("Performance", Order = 5, Glyph = "", Description = "CPU, memory and power.")]
public sealed class PerformanceSettings
{
    [Setting("Threads", Description = "0 = all logical processors.", Min = 0, Max = 512, Order = 0)]
    public int Threads { get; set; }
    [Setting("Memory limit", Description = "0 = automatic (70 % of free RAM).", Min = 0, Max = 1_048_576, Unit = " MiB", Order = 1)]
    public int MemoryLimitMiB { get; set; }
    [Setting("Process priority", Description = "Lower priority keeps your PC responsive during long jobs.", Order = 2)]
    public PriorityLevel Priority { get; set; } = PriorityLevel.BelowNormal;
    [Setting("Keep the PC awake while working", Order = 3)]
    public bool KeepAwake { get; set; } = true;
    [Setting("Pause jobs on battery power", Order = 4)]
    public bool PauseOnBattery { get; set; } = true;
}

[SettingsCategory("Security", Order = 6, Glyph = "", Description = "Encryption defaults.")]
public sealed class SecuritySettings
{
    [Setting("Encrypt new archives by default", Description = "Ticks Encrypt on the Compress page. Explorer quick-compress and watch folders do not encrypt.", Order = 0)]
    public bool EncryptByDefault { get; set; }
    [Setting("Password protection", Description = "How much work every password guess costs, for new encrypted archives. Standard: Argon2id with 1 GiB of memory, about a second to open. Strong: 2 GiB, about three times as long. Maximum: 4 GiB, about six times as long; PCs with less than 8 GB of memory may not open it. Compatible: PBKDF2, which Osmium 0.1 can open, but guessing is far cheaper.", Order = 1)]
    public PasswordProtection Protection { get; set; } = PasswordProtection.Standard;
    [Setting("PBKDF2 iterations", Description = "For Compatible protection only. Higher = slower guessing and slower to open.", Min = 100_000, Max = 20_000_000, Order = 2, Advanced = true, VisibleWhen = "Protection=Compatible")]
    public int KdfIterations { get; set; } = KdfPresets.CompatibleIterations;
    [Setting("Remember passwords until Osmium exits", Order = 3)]
    public bool RememberForSession { get; set; }
    [Setting("Hide exact sizes", Description = "Pads encrypted blocks so their exact sizes are hidden, at a cost of up to 12 % in size. Osmium 0.1 cannot open such archives. The file list of an encrypted archive is always padded, except with Compatible protection.", Order = 4, VisibleWhen = "!Protection=Compatible")]
    public bool HideExactSizes { get; set; }

    /// <summary>Whether new encrypted archives hide exact sizes: only with Argon2id protection, so never with Compatible.</summary>
    public bool PadBlocks() => HideExactSizes && Protection != PasswordProtection.Compatible;

    /// <summary>
    /// The key-derivation settings new encrypted archives get. A settings file edited by hand may hold anything: an
    /// unknown protection is Standard, and with Compatible, iterations outside the setting's range are refused with
    /// <see cref="ArgumentException"/> before any work starts.
    /// </summary>
    public KdfParameters Kdf()
    {
        switch (Protection)
        {
            case PasswordProtection.Strong: return KdfPresets.Strong;
            case PasswordProtection.Maximum: return KdfPresets.Maximum;
            case PasswordProtection.Compatible:
                var range = typeof(SecuritySettings).GetProperty(nameof(KdfIterations))!.GetCustomAttribute<SettingAttribute>()!;
                if (KdfIterations < range.Min || KdfIterations > range.Max)
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"The PBKDF2 iterations in the settings must be between {range.Min:N0} and {range.Max:N0}."));
                return KdfPresets.Compatible(KdfIterations);
            default:
                return KdfPresets.Standard;
        }
    }
}

[SettingsCategory("Integration", Order = 7, Glyph = "", Description = "Explorer and Windows integration.")]
public sealed class IntegrationSettings
{
    [Setting("Explorer context menu", Description = "\"Osmium\" submenu on files and folders (under \"Show more options\" on Windows 11).", Order = 0)]
    public bool ContextMenu { get; set; } = true;
    [Setting("Open .osa archives with Osmium", Order = 1)]
    public bool FileAssociation { get; set; } = true;
    [Setting("Quick-compress profiles in menu", Description = "Profiles offered directly in the context menu (one per line).", Editor = SettingEditor.StringList, Order = 2)]
    public List<string> QuickProfiles { get; set; } = ["balanced", "ultra"];
    [Setting("Invocation batching window", Description = "Explorer starts one process per selected file; invocations within this window become one job.", Min = 100, Max = 5000, Unit = " ms", Order = 3, Advanced = true, RequiresRestart = true)]
    public int CoalesceMs { get; set; } = 350;
}

public sealed class WatchFolderConfig
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;
    public string Include { get; set; } = "*";
    public string Exclude { get; set; } = "*.osa;*.tmp;~$*;*.part;*.crdownload;*.partial-*";
    public int SettleSeconds { get; set; } = 10;
    public long MinSize { get; set; }
    public ProfileName Profile { get; set; } = ProfileName.Balanced;
    /// <summary>Archives are written to this subfolder of the watched folder.</summary>
    public string OutputSubfolder { get; set; } = "_osmium";
}

[SettingsCategory("Watch folders", Order = 8, Glyph = "", Description = "Automatically compress files that appear in folders.")]
public sealed class WatchSettings
{
    [Setting("Enable watch folders", Order = 0)]
    public bool Enabled { get; set; }
    public List<WatchFolderConfig> Folders { get; set; } = [];
}

[SettingsCategory("Cluster", Order = 9, Glyph = "", Description = "Share compression work between PCs on your network.")]
public sealed class ClusterSettings
{
    [Setting("Use cluster workers for my jobs", Order = 0)]
    public bool UseWorkers { get; set; }
    [Setting("Find workers automatically (LAN)", VisibleWhen = "UseWorkers", Order = 1)]
    public bool Discovery { get; set; } = true;
    [Setting("Worker addresses", Description = "host:port, one per line (for routed networks/VPNs).", Editor = SettingEditor.StringList, VisibleWhen = "UseWorkers", Order = 2)]
    public List<string> Workers { get; set; } = [];
    [Setting("Lend this PC as a worker", Description = "Other Osmium PCs with the same cluster key can send work here. They see file contents (never names).", Order = 3)]
    public bool LendThisPc { get; set; }
    [Setting("Worker port", Min = 1024, Max = 65535, Order = 4, Advanced = true)]
    public int WorkerPort { get; set; } = 46510;
    [Setting("Worker slots", Description = "0 = all logical processors.", Min = 0, Max = 512, Order = 5)]
    public int WorkerSlots { get; set; }
    [Setting("Worker memory limit", Description = "0 = automatic.", Min = 0, Max = 1_048_576, Unit = " MiB", Order = 6)]
    public int WorkerMemoryMiB { get; set; }
    [Setting("Cluster key", Description = "Shared secret (OSM1-…). Generate one on the Cluster page.", Editor = SettingEditor.Password, Order = 7,
        Validator = nameof(ValidateClusterKey))]
    public string ClusterKey { get; set; } = "";
    [Setting("Use workers for encrypted archives", Description = "Workers would see the plaintext of encrypted jobs.", Dangerous = true, Order = 8)]
    public bool AllowEncryptedJobs { get; set; }

    /// <summary>Null for an empty value (no key) or a key that ClusterKey.Parse accepts, else the parse error.</summary>
    public static string? ValidateClusterKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            global::Osmium.Core.Distributed.ClusterKey.Parse(value);
            return null;
        }
        catch (FormatException e) { return e.Message; }
    }

    /// <summary>
    /// The canonical text (OSM1-XXXXX-…) of a valid key and "" for a blank one. An invalid key is returned
    /// unchanged, so the error is reported where the key is used instead of the key silently disappearing.
    /// </summary>
    public static string NormalizeClusterKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return global::Osmium.Core.Distributed.ClusterKey.Parse(value).ToString(); }
        catch (FormatException) { return value; }
    }
}

[SettingsCategory("Experimental", Order = 10, Glyph = "", Description = "Features that may be very slow, use huge memory, or change in future versions. Archives made with them are flagged.")]
public sealed class ExperimentalSettings
{
    [Setting("Enable experimental features", Description = "Master switch for everything on this page.", Order = 0,
        WarningText = "Experimental features can take days, use many GB of RAM, and produce archives flagged as experimental.")]
    public bool Enabled { get; set; }
    [Setting("Offer the Exhaustive profile", Description = "Every candidate compressed on the full data, then hill-climbed.", EnabledWhen = "Enabled", Experimental = true, Order = 1)]
    public bool Exhaustive { get; set; }
    [Setting("CM: all context models", Description = "Adds order-5/8, sparse and x86 models to every CM candidate (slower, sometimes smaller).", EnabledWhen = "Enabled", Experimental = true, Order = 2)]
    public bool CmAllModels { get; set; }
    [Setting("Distribute symmetric codecs", Description = "Send CM/PPMd blocks to workers too (verification then costs as much as compressing).", EnabledWhen = "Enabled", Experimental = true, Order = 4)]
    public bool DistributeSymmetric { get; set; }
}

/// <summary>All persisted settings (one JSON file).</summary>
public sealed class OsmiumSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public CompressionSettings Compression { get; set; } = new();
    public CodecSettings Codecs { get; set; } = new();
    public FilterSettings Filters { get; set; } = new();
    public AnalysisSettings Analysis { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public IntegrationSettings Integration { get; set; } = new();
    public WatchSettings Watch { get; set; } = new();
    public ClusterSettings Cluster { get; set; } = new();
    public ExperimentalSettings Experimental { get; set; } = new();

    /// <summary>Unknown properties from newer versions survive a round-trip.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public OsmiumSettings Clone() => JsonSerializer.Deserialize<OsmiumSettings>(JsonSerializer.Serialize(this, SettingsStore.Json), SettingsStore.Json)!;
}

/// <summary>Atomic JSON persistence with corrupt-file recovery.</summary>
public sealed class SettingsStore(string path)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Path => path;
    public string? LastError { get; private set; }

    public OsmiumSettings Load()
    {
        if (!File.Exists(path)) return new OsmiumSettings();
        try
        {
            return JsonSerializer.Deserialize<OsmiumSettings>(File.ReadAllText(path), Json) ?? new OsmiumSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            LastError = e.Message;
            try { File.Copy(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true); } catch (IOException) { }
            return new OsmiumSettings();
        }
    }

    public void Save(OsmiumSettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Per-user data locations; a file named "osmium.portable" next to the executable switches to .\data.</summary>
public static class AppPaths
{
    public static bool Portable { get; } = File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "osmium.portable"));

    public static string Roaming => Portable
        ? System.IO.Path.Combine(AppContext.BaseDirectory, "data")
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Osmium");

    public static string Local => Portable
        ? System.IO.Path.Combine(AppContext.BaseDirectory, "data", "local")
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Osmium");

    public static string SettingsFile => System.IO.Path.Combine(Roaming, "settings.json");
    public static string RulesFile => System.IO.Path.Combine(Roaming, "rules.json");
    public static string HistoryFile => System.IO.Path.Combine(Local, "history.json");
    public static string LogDir => System.IO.Path.Combine(Local, "logs");
}
