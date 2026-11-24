using System.Diagnostics;
using System.Globalization;
using Osmium.Cli;
using Osmium.Core.Analysis;
using Osmium.Core.Container;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;

return await Cli.RunAsync(args);

namespace Osmium.Cli
{
    /// <summary>Minimal argument parser: positional args + "--name value" / "--flag" options.</summary>
    internal sealed class Args
    {
        public List<string> Positional { get; } = [];
        private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
        {
            "overwrite", "skip", "rename", "no-verify", "explain", "experimental", "verbose", "v", "json", "force",
            "password-stdin", "dedup-chunks", "no-plan-log", "quiet", "q", "all", "allow-remote-encrypted", "no-discovery", "ship-symmetric",
        };

        public Args(IEnumerable<string> argv)
        {
            var list = argv.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.StartsWith("--") || (a.StartsWith('-') && a.Length == 2 && !char.IsDigit(a[1])))
                {
                    string name = a.TrimStart('-');
                    string? value = null;
                    int eq = name.IndexOf('=');
                    if (eq > 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                    else if (!Flags.Contains(name) && i + 1 < list.Count) value = list[++i];
                    _opts[name] = value ?? "true";
                }
                else Positional.Add(a);
            }
        }

        public bool Has(string n) => _opts.ContainsKey(n);
        public string? Get(string n) => _opts.GetValueOrDefault(n);
        public string Get(string n, string d) => _opts.GetValueOrDefault(n) ?? d;
        public long Size(string n, long d) => Has(n) ? ParamValues.ParseNumber(Get(n)!) : d;
        public int Int(string n, int d) => Has(n) ? int.Parse(Get(n)!, CultureInfo.InvariantCulture) : d;
    }

    internal static class Cli
    {
        public static async Task<int> RunAsync(string[] argv)
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { Usage(); return 0; }
            var cmd = argv[0].ToLowerInvariant();
            var a = new Args(argv.Skip(1));
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("\nCancelling…"); };
            try
            {
                return cmd switch
                {
                    "a" or "add" or "compress" => await Compress(a, cts.Token),
                    "x" or "extract" => Extract(a, cts.Token),
                    "l" or "list" => List(a),
                    "t" or "test" => Test(a, cts.Token),
                    "i" or "info" => Info(a),
                    "explain" => Explain(a),
                    "analyze" => Analyze(a),
                    "lab" or "bench" => await Lab(a, cts.Token),
                    "tune" => await Tune(a, cts.Token),
                    "methods" => Methods(),
                    "profiles" => Profiles(),
                    "rules" => RulesCmd(a),
                    "worker" => await WorkerCommand.RunAsync(a, cts.Token),
                    "keygen" => WorkerCommand.KeyGen(),
                    "version" or "--version" => Version(),
                    "license" or "licence" or "--license" => License(),
                    _ => Fail($"Unknown command '{cmd}'. Run 'osmium help'."),
                };
            }
            catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
            catch (PasswordRequiredException e) { return Fail(e.Message + " Use --password or --password-stdin."); }
            catch (WrongPasswordException e) { return Fail(e.Message); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException or UnauthorizedAccessException)
            {
                return Fail(e);
            }
        }

        private static int Fail(string msg) { Console.Error.WriteLine("error: " + msg); return 1; }

        private static int Fail(Exception e) =>
            Fail(Environment.GetEnvironmentVariable("OSMIUM_DEBUG") == "1" ? e.ToString() : e.Message);

        private static void Usage() => Console.WriteLine("""
            Osmium — maximum-ratio archiver

            Usage: osmium <command> [options]

              a <archive.osa> <files/dirs…>   Compress
                  --profile store|fast|balanced|max|ultra|insane|exhaustive   (default balanced)
                  --pipeline "x86+lzma(dict=64M,lc=0,lp=2,pb=2)"   force one pipeline for every file
                  --rules rules.json   per-file rules / patches      --exclude "*.tmp,build/**"
                  --dedup off|files|chunks   --block 256M   --threads N   --mem 8G
                  --decode-speed 0|1|2  (penalise slow-to-decode codecs)   --experimental
                  --password <pw> | --password-stdin   --kdf-iterations N
                  --workers host:port,…  --cluster-key OSM1-…   (distribute work)
                  --no-verify   --overwrite   --explain   --no-plan-log
              x <archive> [dest]     Extract   (--overwrite | --skip | --rename, --include glob)
              l <archive> [-v]       List contents        t <archive>    Test (full decode + hashes)
              info <archive>         Archive details      explain <archive>  Show planner decisions
              analyze <files…>       Detect type / class / statistics
              lab <file> [--profile p] [--sample 4M] [--candidates "spec;spec"]   Benchmark candidates
              tune <file> [--profile insane] [--export-rule rules.json]   Search the best pipeline for a file
              methods | profiles     List codecs/filters with parameters, or profiles
              rules check <rules.json>
              worker --key OSM1-… [--port 46510] [--slots N] [--mem 4G]   Lend this machine to a cluster
              keygen                 Create a cluster pairing key
              license                Show the license and third-party notices
              version                Show the version

            Osmium is free software under the MIT License and comes with no warranty.
            Keep backups, and test archives (osmium t) before deleting originals.
            """);

        private static int Version()
        {
            Console.WriteLine("osmium " + (typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "?"));
            Console.WriteLine("MIT License, provided as is without warranty. Run 'osmium license' for the license and third-party notices.");
            return 0;
        }

        private static int License()
        {
            foreach (var (name, title) in new[] { ("Osmium.LICENSE.txt", "Osmium license"), ("Osmium.THIRD-PARTY-NOTICES.txt", "Third-party notices") })
            {
                using var stream = typeof(Cli).Assembly.GetManifestResourceStream(name);
                if (stream is null) return Fail($"The {title.ToLowerInvariant()} are missing from this build; see https://github.com/windfastt/osmium.");
                using var reader = new StreamReader(stream);
                Console.WriteLine(reader.ReadToEnd().TrimEnd());
                Console.WriteLine();
            }
            return 0;
        }

        // ------------------------------------------------------------ compress

        private static string? Password(Args a)
        {
            if (a.Has("password-stdin")) return Console.In.ReadLine();
            return a.Get("password");
        }

        internal static PlanOptions PlanOptionsFrom(Args a, Profile profile, Action<string> log, Action<string, double>? progress = null)
        {
            var rules = RuleSet.BuiltIn();
            if (a.Get("rules") is { } rf) rules = RuleSet.Combine(rules, RuleSet.Load(rf, RuleSet.LayerJob));
            var dedup = a.Get("dedup")?.ToLowerInvariant() switch
            {
                "off" => DedupMode.Off, "files" => DedupMode.Files, "chunks" => DedupMode.Chunks, null => (DedupMode?)null,
                var v => throw new ArgumentException($"--dedup must be off, files or chunks (got {v})"),
            };
            return new PlanOptions
            {
                Profile = profile,
                Rules = rules,
                Threads = a.Int("threads", Environment.ProcessorCount),
                MemoryBudget = a.Size("mem", MemoryBudgets.Default()),
                Dedup = a.Has("dedup-chunks") ? DedupMode.Chunks : dedup,
                BlockCap = a.Has("block") ? a.Size("block", 0) : null,
                DecodeSpeedWeight = a.Int("decode-speed", 0),
                AllowExperimental = a.Has("experimental") || profile.Experimental,
                ForcedPipeline = a.Get("pipeline"),
                Log = log,
                Progress = progress,
            };
        }

        private static async Task<int> Compress(Args a, CancellationToken ct)
        {
            if (a.Positional.Count < 2) return Fail("usage: osmium a <archive.osa> <inputs…>");
            string output = a.Positional[0];
            if (!output.EndsWith(Osa.Extension, StringComparison.OrdinalIgnoreCase)) output += Osa.Extension;
            var profile = Profile.ByName(a.Get("profile", "balanced"));
            if (profile.Experimental && !a.Has("experimental"))
                Console.Error.WriteLine($"note: profile '{profile.Name}' is experimental.");
            bool quiet = a.Has("quiet") || a.Has("q");
            var sw = Stopwatch.StartNew();
            var progress = new ProgressLine(quiet);

            var scan = Scanner.Scan(a.Positional.Skip(1), new ScanOptions
            {
                Excludes = (a.Get("exclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            }, ct);
            if (scan.Files.Count == 0 && scan.Dirs.Count == 0) return Fail("nothing to compress");
            var log = new List<string>();
            var po = PlanOptionsFrom(a, profile, log.Add, (phase, f) => progress.Update($"{phase} {f:P0}"));

            await using var cluster = await ClusterSetup.CreateAsync(a, po, ct);
            if (cluster is not null) po = po with { Trials = cluster.Trials, Threads = po.Threads };

            var plan = await new Planner(po).BuildAsync(scan, ct);
            progress.Done();
            if (!quiet)
                Console.WriteLine($"Planned {plan.Files.Count:N0} files ({Fmt.Bytes(plan.TotalInput)}) into {plan.Groups.Count} group(s), {plan.Blocks.Count} block(s) — profile {profile.Name}");
            if (a.Has("explain"))
                foreach (var line in plan.Groups.SelectMany(g => g.Explain)) Console.WriteLine("  " + line);

            var result = await ArchiveBuilder.BuildAsync(plan, new BuildOptions
            {
                OutputPath = output,
                Password = Password(a),
                KdfIterations = a.Int("kdf-iterations", ArchiveKey.DefaultIterations),
                VerifyBlocks = !a.Has("no-verify"),
                Overwrite = a.Has("overwrite"),
                Threads = po.Threads + (cluster?.ExtraSlots ?? 0),
                MemoryBudget = po.MemoryBudget,
                StorePlanLog = !a.Has("no-plan-log"),
                Compressor = cluster?.Compressor ?? LocalBlockCompressor.Instance,
                Progress = p => progress.Update($"{p.Phase} {Fmt.Pct(p.BytesIn, p.BytesInTotal)}  {Fmt.Bytes(p.BytesIn)} → {Fmt.Bytes(p.BytesOut)}  [{p.BlocksDone}/{p.BlocksTotal}] {p.Detail}"),
            }, ct);
            progress.Done();
            foreach (var w in result.Warnings) Console.Error.WriteLine("warning: " + w);
            if (!quiet)
            {
                Console.WriteLine($"Created {result.Path}");
                Console.WriteLine($"  {Fmt.Bytes(result.InputBytes)} → {Fmt.Bytes(result.OutputBytes)}  ({result.Ratio:P2} of original, {1 - result.Ratio:P2} saved) in {sw.Elapsed.TotalSeconds:F1}s (planning {(sw.Elapsed - result.Elapsed).TotalSeconds:F1}s)");
                Console.WriteLine($"  To extract: ~{Fmt.Duration(result.EstimatedDecodeSeconds)} on one core, up to {Fmt.Bytes(result.MaxDecodeMemory)} RAM per block");
            }
            return 0;
        }

        // ------------------------------------------------------------ extract / test / list

        private static OsaReader Open(Args a, string path) => OsaReader.Open(path, () => Password(a) ?? PromptPassword());

        private static string? PromptPassword()
        {
            if (Console.IsInputRedirected) return null;
            Console.Error.Write("Password: ");
            var sb = new System.Text.StringBuilder();
            // Read Ctrl+C as a key so it can end the prompt (otherwise ReadKey keeps waiting after the cancel handler).
            bool treatCtrlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            try
            {
                while (true)
                {
                    var k = Console.ReadKey(intercept: true);
                    if (k.Key == ConsoleKey.Enter) break;
                    if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        Console.Error.WriteLine();
                        throw new OperationCanceledException();
                    }
                    if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                    // Arrows, Tab, Esc, function keys and dead keys produce '\0' or control characters: never part of a password.
                    if (char.IsControl(k.KeyChar) || k.KeyChar == '\0') continue;
                    sb.Append(k.KeyChar);
                }
            }
            finally { Console.TreatControlCAsInput = treatCtrlC; }
            Console.Error.WriteLine();
            return sb.ToString();
        }

        private static int Extract(Args a, CancellationToken ct)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium x <archive> [destination]");
            using var r = Open(a, a.Positional[0]);
            string dest = a.Positional.Count > 1 ? a.Positional[1] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(a.Positional[0]))!, Path.GetFileNameWithoutExtension(a.Positional[0]));
            var mode = a.Has("overwrite") ? OverwriteMode.Overwrite : a.Has("skip") ? OverwriteMode.Skip : OverwriteMode.Rename;
            if (r.ExpansionFactor > 200 && !a.Has("force"))
                return Fail($"archive expands {r.ExpansionFactor:F0}× (heavy dedup or a decompression bomb); re-run with --force to continue.");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))!);
            if (drive.IsReady && drive.AvailableFreeSpace < r.TotalUnpacked && !a.Has("force"))
                return Fail($"not enough free space: need {Fmt.Bytes(r.TotalUnpacked)}, have {Fmt.Bytes(drive.AvailableFreeSpace)}.");
            Func<ArchiveEntry, bool>? sel = null;
            if (a.Get("include") is { } inc)
            {
                var rx = inc.Split(',').Select(Glob.ToRegex).ToList();
                sel = e => rx.Any(x => x.IsMatch(e.Path));
            }
            var progress = new ProgressLine(a.Has("quiet"));
            var sw = Stopwatch.StartNew();
            var res = Extractor.Extract(r, dest, sel, new ExtractOptions { Overwrite = mode },
                new Progress<ExtractProgress>(p => progress.Update($"Extracting {Fmt.Pct(p.BytesDone, p.BytesTotal)}  {p.FilesDone}/{p.FilesTotal} files")), ct);
            progress.Done();
            foreach (var e in res.Errors) Console.Error.WriteLine("error: " + e);
            int newer = res.FilesNeedingNewerVersion, damaged = res.FilesFailed - newer;
            Console.WriteLine($"Extracted {res.FilesOk} file(s) to {dest} in {sw.Elapsed.TotalSeconds:F1}s{(res.FilesSkipped > 0 ? $", {res.FilesSkipped} skipped" : "")}" +
                              $"{(damaged > 0 ? $", {damaged} FAILED" : "")}{(newer > 0 ? $", {newer} need a newer version of Osmium" : "")}");
            return res.FilesFailed == 0 && res.Errors.Count == 0 ? 0 : 2;
        }

        /// <summary>"N file(s) damaged, M file(s) need a newer version of Osmium" for a test or extract that did not verify.</summary>
        private static string FailureSummary(ExtractResult res)
        {
            int newer = res.FilesNeedingNewerVersion, damaged = res.FilesFailed - newer;
            var parts = new List<string>();
            if (damaged > 0 || newer == 0) parts.Add($"{damaged} file(s) damaged");
            if (newer > 0) parts.Add($"{newer} file(s) need a newer version of Osmium (the rest verified)");
            return string.Join(", ", parts);
        }

        private static int Test(Args a, CancellationToken ct)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium t <archive>");
            using var r = Open(a, a.Positional[0]);
            var progress = new ProgressLine(a.Has("quiet"));
            var res = Extractor.Test(r, new Progress<ExtractProgress>(p => progress.Update($"Testing {Fmt.Pct(p.BytesDone, p.BytesTotal)}")), ct);
            progress.Done();
            foreach (var e in res.Errors) Console.Error.WriteLine("error: " + e);
            Console.WriteLine(res.FilesFailed == 0 && res.Errors.Count == 0
                ? $"OK — {res.FilesOk} file(s), {r.Blocks.Count} block(s) verified."
                : $"FAILED — {FailureSummary(res)}.");
            return res.FilesFailed == 0 && res.Errors.Count == 0 ? 0 : 2;
        }

        private static int List(Args a)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium l <archive>");
            using var r = Open(a, a.Positional[0]);
            bool v = a.Has("v") || a.Has("verbose");
            foreach (var e in r.Entries)
            {
                if (e.IsDirectory) { if (v) Console.WriteLine($"{"<dir>",14}  {e.ModifiedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Path}/"); continue; }
                string method = v && e.Segments.Count > 0 ? "  " + r.Blocks[e.Segments[0].Frame].Descriptor.Spec : "";
                Console.WriteLine($"{e.Size,14:N0}  {e.ModifiedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Path}{method}");
            }
            Console.WriteLine($"{r.TotalUnpacked,14:N0}  {r.Entries.Count(e => !e.IsDirectory)} file(s) in {Fmt.Bytes(r.FileLength)} ({(double)r.FileLength / Math.Max(1, r.TotalUnpacked):P2})");
            return 0;
        }

        private static int Info(Args a)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium info <archive>");
            using var r = Open(a, a.Positional[0]);
            Console.WriteLine($"Archive:      {r.Path}");
            Console.WriteLine($"Format:       .osa {Osa.Major}.{Osa.Minor}   created by {r.Header.Creator}");
            Console.WriteLine($"Encrypted:    {(r.Header.Encrypted ? $"yes (AES-256-GCM, PBKDF2-SHA512 ×{r.Header.Encryption!.Iterations:N0})" : "no")}");
            Console.WriteLine($"Experimental: {(r.Header.Experimental ? "yes" : "no")}");
            Console.WriteLine($"Profile:      {r.Index.MetaString("profile") ?? "?"}");
            Console.WriteLine($"Content:      {r.Entries.Count(e => !e.IsDirectory):N0} files, {r.Entries.Count(e => e.IsDirectory):N0} dirs, {Fmt.Bytes(r.TotalUnpacked)}");
            Console.WriteLine($"Archive size: {Fmt.Bytes(r.FileLength)} ({(double)r.FileLength / Math.Max(1, r.TotalUnpacked):P2})");
            Console.WriteLine($"Dedup:        {Fmt.Bytes(r.TotalUnpacked - r.TotalStoredRaw)} saved by deduplication");
            Console.WriteLine($"Extract cost: ~{Fmt.Duration(r.EstimatedDecodeSeconds)} single-core, ≤ {Fmt.Bytes(r.MaxBlockDecodeMemory)} RAM per block");
            Console.WriteLine($"Blocks ({r.Blocks.Count}):");
            foreach (var b in r.Blocks)
                Console.WriteLine($"  #{b.Frame,-4} {Fmt.Bytes(b.RawSize),10} → {Fmt.Bytes(b.PackedSize),10}  {b.Descriptor.Spec}");
            if (r.NewerVersion is { } newer)
                Console.WriteLine($"Note: {newer.Message} Blocks marked \"needs a newer Osmium\" cannot be extracted by this version; the others can.");
            return 0;
        }

        private static int Explain(Args a)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium explain <archive>");
            using var r = Open(a, a.Positional[0]);
            if (!r.Index.Metadata.TryGetValue("plan", out var json)) return Fail("this archive has no plan log (made with --no-plan-log)");
            foreach (var line in System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []) Console.WriteLine(line);
            return 0;
        }

        private static int Analyze(Args a)
        {
            var scan = Scanner.Scan(a.Positional);
            foreach (var f in scan.Files)
            {
                var an = Sniffer.Analyze(f.FullPath, 32);
                Console.WriteLine($"{f.RelPath}  [{Fmt.Bytes(f.Size)}]");
                Console.WriteLine($"  {an.Summary}");
                foreach (var ev in an.Evidence) Console.WriteLine($"    · {ev}");
            }
            return 0;
        }

        // ------------------------------------------------------------ lab / tune

        private static async Task<int> Lab(Args a, CancellationToken ct)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium lab <file> [--profile insane] [--sample 4M]");
            var path = a.Positional[0];
            var profile = Profile.ByName(a.Get("profile", "insane"));
            var an = Sniffer.Analyze(path, profile.SampleWindows);
            long sampleLen = a.Size("sample", 4L << 20);
            byte[] data;
            using (var h = File.OpenHandle(path))
                data = new FileInfo(path).Length <= sampleLen ? File.ReadAllBytes(path) : Sniffer.SampleBody(h, new FileInfo(path).Length, (int)(sampleLen / 65536));
            var reg = MethodRegistry.Default;
            var specs = a.Get("candidates") is { } c
                ? c.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => PipelineSpec.Parse(s.Trim(), reg)).ToList()
                : Candidates.For(an.Class, an, profile, reg);
            Console.WriteLine($"{path}: {an.Summary}");
            Console.WriteLine($"Sample: {Fmt.Bytes(data.Length)}, {specs.Count} candidate(s)\n");
            Console.WriteLine($"{"size",12} {"ratio",8} {"enc MB/s",9} {"dec MB/s",9}  pipeline");
            var rows = new List<(PipelineSpec s, long size, double enc, double dec)>();
            foreach (var s in specs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sw = Stopwatch.StartNew();
                    var (d, payload) = PipelineExecutor.Encode(s, data, new CodecContext { Token = ct });
                    double te = sw.Elapsed.TotalSeconds; sw.Restart();
                    var back = PipelineExecutor.Decode(d, payload, new CodecContext { Token = ct });
                    double td = sw.Elapsed.TotalSeconds;
                    if (!back.AsSpan().SequenceEqual(data)) throw new InvalidOperationException("round-trip mismatch");
                    rows.Add((s, payload.Length, data.Length / 1e6 / Math.Max(te, 1e-6), data.Length / 1e6 / Math.Max(td, 1e-6)));
                    var row = rows[^1];
                    Console.WriteLine($"{row.size,12:N0} {(double)row.size / data.Length,8:P2} {row.enc,9:F2} {row.dec,9:F2}  {s}");
                }
                catch (Exception e) when (e is not OperationCanceledException) { Console.WriteLine($"{"FAILED",12}  {s}: {e.Message}"); }
            }
            if (rows.Count > 0)
            {
                var best = rows.MinBy(r => r.size);
                Console.WriteLine($"\nBest: {best.s}  ({Fmt.Bytes(best.size)}, {(double)best.size / data.Length:P2})");
            }
            await Task.CompletedTask;
            return 0;
        }

        private static async Task<int> Tune(Args a, CancellationToken ct)
        {
            if (a.Positional.Count < 1) return Fail("usage: osmium tune <file> [--profile insane] [--export-rule rules.json]");
            var path = Path.GetFullPath(a.Positional[0]);
            var profile = Profile.ByName(a.Get("profile", "insane"));
            var scan = Scanner.Scan([path]);
            var po = PlanOptionsFrom(a, profile with { RaceTopTwo = false }, Console.WriteLine);
            var plan = await new Planner(po).BuildAsync(scan, ct);
            foreach (var line in plan.Groups.SelectMany(g => g.Explain)) Console.WriteLine(line);
            var g = plan.Groups.FirstOrDefault();
            if (g?.Spec is null) return Fail("nothing to tune");
            var raw = File.ReadAllBytes(path);
            var (_, payload) = PipelineExecutor.Encode(g.Spec, raw, new CodecContext { Token = ct });
            Console.WriteLine($"\nBest pipeline: {g.Spec}\nFull file: {Fmt.Bytes(raw.Length)} → {Fmt.Bytes(payload.Length)} ({(double)payload.Length / Math.Max(1, raw.Length):P2})");
            if (a.Get("export-rule") is { } rf)
            {
                var rs = File.Exists(rf) ? RuleSet.Load(rf) : new RuleSet();
                rs.Rules.Add(new Rule
                {
                    Id = "tuned-" + Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                    Description = $"Tuned by 'osmium tune' ({profile.Name})",
                    Match = new RuleMatch { Globs = ["**/" + Path.GetFileName(path)] },
                    Actions = new RuleActions { Pipeline = g.Spec.ToString(), Solid = "own" },
                    Origin = new RuleOrigin { Type = "tuned", At = DateTime.UtcNow, OriginalSize = raw.Length, MeasuredSize = payload.Length },
                });
                rs.Save(rf);
                Console.WriteLine($"Rule written to {rf}");
            }
            return 0;
        }

        // ------------------------------------------------------------ catalog

        private static int Methods()
        {
            foreach (var m in MethodRegistry.Default.All)
            {
                Console.WriteLine($"{m.Name,-10} {m.Kind,-6} {m.DisplayName}{(m.Experimental ? " [experimental]" : "")}");
                Console.WriteLine($"           {m.Description}");
                foreach (var p in m.Params)
                    Console.WriteLine($"           {p.Name,-9} {p.Min}..{p.Max} (default {(p.Type == ParamType.Size ? ParamValues.FormatSize(p.Default) : p.Default)})  {p.Label}");
            }
            return 0;
        }

        private static int Profiles()
        {
            foreach (var p in Profile.All) Console.WriteLine($"{p.Name,-11} {p.Description}");
            return 0;
        }

        private static int RulesCmd(Args a)
        {
            if (a.Positional.Count < 2 || a.Positional[0] != "check") return Fail("usage: osmium rules check <rules.json>");
            var rs = RuleSet.Load(a.Positional[1]);
            var problems = rs.Check(MethodRegistry.Default);
            foreach (var p in problems) Console.WriteLine("problem: " + p);
            Console.WriteLine(problems.Count == 0 ? $"OK — {rs.Rules.Count} rule(s)." : $"{problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 1;
        }
    }

    internal sealed class ProgressLine(bool quiet)
    {
        private DateTime _last;
        private int _len;
        public void Update(string text)
        {
            if (quiet || Console.IsOutputRedirected) return;
            var now = DateTime.UtcNow;
            if ((now - _last).TotalMilliseconds < 150) return;
            _last = now;
            lock (this)
            {
                if (text.Length > 110) text = text[..110];
                Console.Write("\r" + text.PadRight(_len));
                _len = text.Length;
            }
        }
        public void Done()
        {
            if (quiet || Console.IsOutputRedirected || _len == 0) return;
            Console.Write("\r" + new string(' ', _len) + "\r");
            _len = 0;
        }
    }

    internal static class Fmt
    {
        public static string Bytes(long b) => b switch
        {
            < 1024 => $"{b} B",
            < 1 << 20 => $"{b / 1024.0:F1} KiB",
            < 1 << 30 => $"{b / 1048576.0:F1} MiB",
            _ => $"{b / 1073741824.0:F2} GiB",
        };
        public static string Pct(long a, long b) => b <= 0 ? "" : $"{100.0 * a / b:F0}%";
        public static string Duration(double s) => s < 1 ? "<1 s" : s < 120 ? $"{s:F0} s" : s < 7200 ? $"{s / 60:F0} min" : $"{s / 3600:F1} h";
    }
}
