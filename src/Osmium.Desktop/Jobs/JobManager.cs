using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Osmium.Core.Container;
using Osmium.Core.Distributed;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Jobs;

public enum JobKind { Compress, Extract, Test }
public enum JobState { Queued, Running, Paused, Completed, Failed, Canceled }

public sealed class JobRequest
{
    public JobKind Kind { get; set; }
    public List<string> Inputs { get; set; } = [];
    /// <summary>Archive path (compress) or destination folder (extract).</summary>
    public string Output { get; set; } = "";
    public string? Profile { get; set; }
    public string? Password { get; set; }
    public string? Pipeline { get; set; }
    /// <summary>Per-file patches from the Compress page / TuneDialog (job-layer rules).</summary>
    public List<Rule> Patches { get; set; } = [];
    public bool Overwrite { get; set; }
    public string? Name { get; set; }
    public string? Origin { get; set; }
}

public sealed record JobSnapshot(
    Guid Id, string Name, JobKind Kind, JobState State, string Phase, double Fraction,
    long BytesIn, long BytesOut, long BytesTotal, TimeSpan Elapsed, TimeSpan? Eta, string? Detail,
    string? Error, string? Output, DateTime Created, IReadOnlyList<string> Log)
{
    public double Ratio => BytesIn == 0 ? 0 : (double)BytesOut / BytesIn;
    public bool IsActive => State is JobState.Queued or JobState.Running or JobState.Paused;
    /// <summary>
    /// For a compress job that ran with "move the originals to the Recycle Bin": how many inputs moved and how many
    /// stayed where they were. Null when the setting was off or the job did not get that far.
    /// </summary>
    public RecycleSummary? Recycle { get; init; }
}

/// <summary>
/// How many inputs of a compress job moved to the Recycle Bin, how many stayed where they were, and how many are no
/// longer in place although the Recycle Bin did not confirm holding them (the job log names them).
/// </summary>
public sealed record RecycleSummary(int Moved, int Kept, int Gone = 0)
{
    public override string ToString() =>
        $"{Moved} moved to the Recycle Bin, {Kept} kept" + (Gone > 0 ? $", {Gone} no longer in place and not confirmed in the Recycle Bin" : "");
}

/// <summary>Sequential job queue used by the tray app. Thread-safe; the UI polls <see cref="Snapshot"/>.</summary>
public sealed class JobManager : IDisposable
{
    private sealed class Job(JobRequest r)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public JobRequest Request { get; } = r;
        public JobState State = JobState.Queued;
        public string Phase = "Queued";
        public double Fraction;
        public long In, Out, Total;
        public string? Detail, Error, Output;
        public DateTime Created = DateTime.Now;
        public Stopwatch Clock = new();
        public PauseGate Gate { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
        public List<string> Log { get; } = [];
        public RecycleSummary? Recycle;
    }

    private readonly Func<OsmiumSettings> _settings;
    private readonly Func<RuleSet> _globalRules;
    private readonly IRecycler? _recycler;
    private readonly List<Job> _jobs = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly string? _historyFile;
    private bool _pausedAll;

    public event Action<JobSnapshot>? JobFinished;
    public event Action<bool>? BusyChanged;
    public Func<string, string?>? PasswordPrompt { get; set; }

    /// <summary>Reads the index of a written archive (path, password): what it holds, for moving the originals.</summary>
    internal Func<string, Func<string?>, IReadOnlyList<ArchiveEntry>> ReadIndex { get; set; } = static (path, password) =>
    {
        using var archive = OsaReader.Open(path, password);
        return archive.Entries;
    };

    /// <param name="recycler">Moves the originals of compress jobs to the Recycle Bin when the setting asks for it;
    /// without one, originals always stay.</param>
    public JobManager(Func<OsmiumSettings> settings, Func<RuleSet> globalRules, string? historyFile = null, IRecycler? recycler = null)
    {
        _settings = settings;
        _globalRules = globalRules;
        _historyFile = historyFile;
        _recycler = recycler;
        LoadHistory();
        _loop = Task.Run(LoopAsync);
    }

    public bool AnyActive { get { lock (_lock) return _jobs.Any(j => j.State is JobState.Running or JobState.Paused or JobState.Queued); } }
    public bool PausedAll => _pausedAll;

    public Guid Enqueue(JobRequest r)
    {
        var j = new Job(r);
        lock (_lock) _jobs.Add(j);
        if (_pausedAll) j.Gate.Pause();
        _signal.Release();
        return j.Id;
    }

    /// <summary>Cheap snapshot for polling; job logs are omitted (use <see cref="GetLog"/>).</summary>
    public IReadOnlyList<JobSnapshot> Snapshot()
    {
        lock (_lock) return _jobs.Select(j => Snap(j, withLog: false)).ToList();
    }

    public IReadOnlyList<string> GetLog(Guid id)
    {
        var j = Find(id);
        if (j is null) return [];
        lock (j.Log) return j.Log.ToList();
    }

    private static JobSnapshot Snap(Job j) => Snap(j, withLog: true);

    private static JobSnapshot Snap(Job j, bool withLog)
    {
        TimeSpan? eta = null;
        if (j.State == JobState.Running && j.Fraction > 0.02 && j.Clock.Elapsed.TotalSeconds > 5)
            eta = TimeSpan.FromSeconds(j.Clock.Elapsed.TotalSeconds * (1 - j.Fraction) / j.Fraction);
        string name = j.Request.Name ?? (j.Request.Kind == JobKind.Compress ? Path.GetFileName(j.Request.Output) : Path.GetFileName(j.Request.Inputs.FirstOrDefault() ?? "?"));
        return new JobSnapshot(j.Id, name, j.Request.Kind, j.State, j.Phase, j.Fraction, j.In, j.Out, j.Total, j.Clock.Elapsed, eta,
            j.Detail, j.Error, j.Output ?? j.Request.Output, j.Created, withLog ? LogCopy(j) : []) { Recycle = j.Recycle };
    }

    private static List<string> LogCopy(Job j)
    {
        lock (j.Log) return j.Log.ToList();
    }

    private Job? Find(Guid id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    public void Pause(Guid id) { if (Find(id) is { } j && j.State is JobState.Running or JobState.Queued) { j.Gate.Pause(); if (j.State == JobState.Running) j.State = JobState.Paused; } }
    public void Resume(Guid id) { if (Find(id) is { } j) { j.Gate.Resume(); if (j.State == JobState.Paused) j.State = JobState.Running; } }
    public void Cancel(Guid id) { if (Find(id) is { } j) { j.Cts.Cancel(); j.Gate.Resume(); if (j.State == JobState.Queued) j.State = JobState.Canceled; } }

    public void PauseAll()
    {
        _pausedAll = true;
        lock (_lock) foreach (var j in _jobs.Where(j => j.State is JobState.Running or JobState.Queued)) { j.Gate.Pause(); if (j.State == JobState.Running) j.State = JobState.Paused; }
    }

    public void ResumeAll()
    {
        _pausedAll = false;
        lock (_lock) foreach (var j in _jobs.Where(j => j.State is JobState.Paused or JobState.Queued)) { j.Gate.Resume(); if (j.State == JobState.Paused) j.State = JobState.Running; }
    }

    public void ClearFinished()
    {
        lock (_lock) _jobs.RemoveAll(j => !(j.State is JobState.Queued or JobState.Running or JobState.Paused));
        SaveHistory();
    }

    private async Task LoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_shutdown.Token); } catch (OperationCanceledException) { return; }
            Job? next;
            lock (_lock) next = _jobs.FirstOrDefault(j => j.State == JobState.Queued);
            if (next is null) continue;
            BusyChanged?.Invoke(true);
            await RunAsync(next);
            BusyChanged?.Invoke(AnyActive);
            if (AnyActive) _signal.Release();
            SaveHistory();
            JobFinished?.Invoke(Snap(next));
        }
    }

    private async Task RunAsync(Job j)
    {
        j.State = j.Gate.IsPaused ? JobState.Paused : JobState.Running;
        j.Clock.Start();
        var ct = CancellationTokenSource.CreateLinkedTokenSource(j.Cts.Token, _shutdown.Token).Token;
        try
        {
            switch (j.Request.Kind)
            {
                case JobKind.Compress: await CompressAsync(j, ct); break;
                case JobKind.Extract: Extract(j, ct, test: false); break;
                case JobKind.Test: Extract(j, ct, test: true); break;
            }
            j.State = JobState.Completed;
            j.Fraction = 1;
            j.Phase = "Done";
        }
        catch (OperationCanceledException) { j.State = JobState.Canceled; j.Phase = "Canceled"; }
        catch (Exception e)
        {
            j.State = JobState.Failed;
            j.Phase = "Failed";
            j.Error = e.Message;
            j.Log.Add("error: " + e);
        }
        finally { j.Clock.Stop(); }
    }

    /// <summary>Translates settings + request into planner options (shared with the CLI semantics).</summary>
    public static PlanOptions BuildPlanOptions(OsmiumSettings s, JobRequest r, RuleSet globalRules, Action<string>? log)
    {
        var profile = Profile.ByName(r.Profile ?? s.Compression.Profile.ToString().ToLowerInvariant());
        if (s.Codecs.CmMemoryBits > 0) profile = profile with { CmMem = Math.Min(s.Codecs.CmMemoryBits, s.Experimental is { Enabled: true, HugeTables: true } ? 26 : 24) };
        if (s.Experimental is { Enabled: true, CmAllModels: true }) profile = profile with { CmAllModels = true };
        if (s.Codecs.LzmaDictMiB > 0) profile = profile with { LzmaDict = (long)s.Codecs.LzmaDictMiB << 20 };
        if (s.Codecs.ZstdLevel > 0) profile = profile with { ZstdLevel = s.Codecs.ZstdLevel };
        if (s.Analysis.SampleWindows > 0) profile = profile with { SampleWindows = s.Analysis.SampleWindows };
        if (s.Analysis.MaxCandidates > 0) profile = profile with { MaxCandidates = s.Analysis.MaxCandidates };
        if (s.Analysis.SearchEvals >= 0) profile = profile with { SearchEvals = s.Analysis.SearchEvals };
        if (s.Analysis.RaceTopTwo) profile = profile with { RaceTopTwo = true };
        if (!s.Codecs.AllowCm) profile = profile with { AllowCm = false };

        var forbidCodecs = new List<string>();
        if (!s.Codecs.AllowPpmd) forbidCodecs.Add("ppmd");
        if (!s.Codecs.AllowLzma) forbidCodecs.Add("lzma");
        if (!s.Codecs.AllowZstd) forbidCodecs.Add("zstd");
        if (!s.Codecs.AllowBrotli) forbidCodecs.Add("brotli");
        if (!s.Codecs.AllowBzip2) forbidCodecs.Add("bzip2");
        var forbidFilters = new List<string>();
        if (!s.Filters.X86) forbidFilters.Add("x86");
        if (!s.Filters.Arm64) forbidFilters.Add("arm64");
        if (!s.Filters.Delta) forbidFilters.Add("delta");
        if (!s.Filters.Transpose) forbidFilters.Add("transpose");
        if (!s.Filters.Pcm) forbidFilters.Add("pcm16");
        if (!s.Filters.Image) forbidFilters.Add("image");
        if (!s.Filters.Text) forbidFilters.Add("text");

        var settingsRules = new RuleSet();
        if (forbidCodecs.Count + forbidFilters.Count > 0)
            settingsRules.Rules.Add(new Rule
            {
                Id = "settings-allowed-methods", Description = "Codec/filter switches from Settings",
                Match = new RuleMatch { Globs = ["**"] },
                Actions = new RuleActions { ForbidCodecs = [.. forbidCodecs], ForbidFilters = [.. forbidFilters] },
            });
        var job = new RuleSet { Rules = r.Patches }.AtLayer(RuleSet.LayerJob);
        settingsRules.AtLayer(RuleSet.LayerGlobal);
        var rules = RuleSet.Combine(RuleSet.BuiltIn(), globalRules, settingsRules, job);

        return new PlanOptions
        {
            Profile = profile,
            Rules = rules,
            Threads = s.Performance.Threads > 0 ? s.Performance.Threads : Environment.ProcessorCount,
            MemoryBudget = s.Performance.MemoryLimitMiB > 0 ? (long)s.Performance.MemoryLimitMiB << 20 : MemoryBudgets.Default(),
            Dedup = s.Compression.Dedup switch { DedupSetting.Off => DedupMode.Off, DedupSetting.Files => DedupMode.Files, DedupSetting.Chunks => DedupMode.Chunks, _ => null },
            BlockCap = s.Compression.BlockSizeMiB > 0 ? (long)s.Compression.BlockSizeMiB << 20 : null,
            DecodeSpeedWeight = (int)s.Compression.DecodeSpeedWeight,
            AllowExperimental = s.Experimental.Enabled || profile.Experimental,
            ForcedPipeline = r.Pipeline,
            Log = log,
        };
    }

    private async Task CompressAsync(Job j, CancellationToken ct)
    {
        var s = _settings();
        var r = j.Request;
        // Read once: the whole job uses the settings in effect when it started, and the originals move only if the
        // blocks were verified with exactly this value.
        bool verify = s.Compression.VerifyBlocks, recycle = s.Compression.RecycleOriginals;
        j.Phase = "Scanning";
        var scan = Scanner.Scan(r.Inputs, new ScanOptions { Excludes = s.Compression.Excludes, IncludeHidden = s.Compression.IncludeHidden }, ct);
        var po = BuildPlanOptions(s, r, _globalRules(), m => { lock (j.Log) j.Log.Add(m); }) with
        {
            Progress = (phase, f) => { j.Phase = phase; j.Fraction = 0.1 * f; },
        };

        Cluster? cluster = null;
        bool encrypted = !string.IsNullOrEmpty(r.Password);
        if (s.Cluster.UseWorkers && !string.IsNullOrWhiteSpace(s.Cluster.ClusterKey) && (!encrypted || s.Cluster.AllowEncryptedJobs))
        {
            try
            {
                j.Phase = "Connecting to workers";
                var key = ClusterKey.Parse(s.Cluster.ClusterKey);
                var endpoints = new List<string>(s.Cluster.Workers);
                if (s.Cluster.Discovery) endpoints.AddRange((await Discovery.ProbeAsync(key, TimeSpan.FromSeconds(1.5), ct)).Select(w => w.Endpoint));
                cluster = await Cluster.ConnectAsync(endpoints, key, po.Threads, m => { lock (j.Log) j.Log.Add("cluster: " + m); }, ct);
                if (cluster.Nodes.Count > 0) po = po with { Trials = cluster.TrialRunner(new MemoryGate(po.MemoryBudget)) };
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                lock (j.Log) j.Log.Add("cluster unavailable: " + e.Message);
            }
        }
        ArchivePlan plan;
        BuildResult result;
        try
        {
            plan = await new Planner(po).BuildAsync(scan, ct);
            j.Total = plan.TotalInput;
            lock (j.Log) j.Log.AddRange(plan.Groups.SelectMany(g => g.Explain));
            result = await ArchiveBuilder.BuildAsync(plan, new BuildOptions
            {
                OutputPath = r.Output,
                Password = r.Password,
                KdfIterations = s.Security.KdfIterations,
                VerifyBlocks = verify,
                Overwrite = r.Overwrite,
                Threads = po.Threads + (cluster?.RemoteSlots ?? 0),
                MemoryBudget = po.MemoryBudget,
                StorePlanLog = s.Compression.StorePlanLog,
                Gate = j.Gate,
                Compressor = cluster is { Nodes.Count: > 0 }
                    ? cluster.BlockCompressor(shipSymmetric: s.Experimental is { Enabled: true, DistributeSymmetric: true })
                    : LocalBlockCompressor.Instance,
                Progress = p =>
                {
                    j.Phase = p.Phase;
                    j.Detail = p.Detail;
                    j.In = p.BytesIn;
                    j.Out = p.BytesOut;
                    j.Fraction = 0.1 + 0.9 * (p.BytesInTotal == 0 ? 1 : (double)p.BytesIn / p.BytesInTotal);
                },
            }, ct);
            j.In = result.InputBytes;
            j.Out = result.OutputBytes;
            j.Output = result.Path;
            lock (j.Log) j.Log.AddRange(result.Warnings.Select(w => "warning: " + w));
        }
        finally
        {
            if (cluster is not null) await cluster.DisposeAsync();
        }
        if (recycle) MoveOriginals(j, verify, plan, result, ct);
    }

    /// <summary>
    /// Once the archive is complete: moves the inputs that every rule of <see cref="RecycleCandidates"/> allows to the
    /// Recycle Bin, in one operation, and logs for each input "moved to the Recycle Bin: path" or "kept path: reason".
    /// Never throws: the archive is good whatever happens here, so the job completes, with a warning when moving
    /// failed. Whatever could not be checked or moved stays where it is.
    /// </summary>
    private void MoveOriginals(Job j, bool verified, ArchivePlan plan, BuildResult result, CancellationToken ct)
    {
        j.Phase = "Moving the originals to the Recycle Bin";
        var r = j.Request;
        void Log(string line) { lock (j.Log) j.Log.Add(line); }
        RecycleDecision decision;
        Func<string, string?> recheck = _ => "it could not be checked again";
        try
        {
            var facts = new RecycleFacts
            {
                Inputs = r.Inputs, Outcome = ct.IsCancellationRequested ? JobState.Canceled : JobState.Completed,
                VerifiedBlocks = verified, Build = result,
            };
            if (_recycler is null) decision = RecycleDecision.KeepAll(r.Inputs, "Osmium has no Recycle Bin to use on this system");
            else if (RecycleCandidates.JobBlocker(facts) is { } blocker) decision = RecycleDecision.KeepAll(r.Inputs, blocker);
            else
            {
                // In the archive means listed by the archive's own index: the planner leaves files out (rules, unreadable
                // files), so the scan alone does not say what the archive holds.
                var (files, dirs) = RecycleCandidates.InArchive(plan.Files.Select(f => f.Src), plan.Dirs, ReadIndex(result.Path, () => r.Password));
                var archived = facts with { ArchivedFiles = files, ArchivedDirs = dirs };
                decision = RecycleCandidates.Decide(archived, _recycler.DriveTypeOf);
                // The same rules once more for each input just before it moves: it may have changed since.
                recheck = RecycleCandidates.Recheck(archived, _recycler.DriveTypeOf);
            }
        }
        catch (Exception e)
        {
            Log("warning: the originals could not be checked, so none were moved: " + e.Message);
            decision = RecycleDecision.KeepAll(r.Inputs, "it could not be checked");
        }

        var move = decision.Move;
        // The inputs that are to stay and are in place now: afterwards, each must still be there.
        var staying = decision.Keep.Select(k => k.Path).Where(InPlace).ToHashSet(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.Ordinal);
        if (move.Count > 0)
        {
            try
            {
                var handedOver = move.ToHashSet(StringComparer.Ordinal);
                foreach (var o in _recycler!.Recycle(move, recheck, ct))
                {
                    if (handedOver.Contains(o.Path)) outcomes[o.Path] = o;
                    else Log($"warning: the Recycle Bin operation also affected {o.Path}, which was not to move: {o.Reason}");
                }
            }
            catch (Exception e) { Log("warning: moving the originals to the Recycle Bin failed: " + e.Message); }
        }

        int moved = 0, kept = 0, gone = 0, failed = 0;
        foreach (var input in decision.Inputs)
        {
            bool inPlace = InPlace(input.Path);
            if (!input.Moves)
            {
                if (inPlace || !staying.Contains(input.Path))
                {
                    kept++;
                    Log($"kept {input.Path}: {input.Reason}");
                }
                else
                {
                    gone++;
                    Log($"warning: {input.Path} was to stay ({input.Reason}), but it is no longer in place");
                }
                continue;
            }
            outcomes.TryGetValue(input.Path, out var o);
            if (o is { Recycled: true } && !inPlace)
            {
                moved++;
                Log("moved to the Recycle Bin: " + input.Path);
            }
            else if (inPlace)
            {
                kept++;
                failed++;
                Log($"kept {input.Path}: {(o is { Recycled: true } ? "it is still in place, although the Recycle Bin reported it as moved" : o?.Reason ?? "the Recycle Bin did not take it")}");
            }
            else
            {
                gone++;
                failed++;
                Log($"warning: {input.Path} is no longer in place, but the Recycle Bin did not confirm that it holds it{(o?.Reason is { } why ? $" ({why})" : "")}");
            }
        }
        if (failed > 0) Log($"warning: {failed} of {move.Count} item{(move.Count == 1 ? "" : "s")} could not be moved to the Recycle Bin");
        j.Recycle = new RecycleSummary(moved, kept, gone);
        Log("originals: " + j.Recycle);

        static bool InPlace(string path) => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path));
    }

    private void Extract(Job j, CancellationToken ct, bool test)
    {
        var r = j.Request;
        string archive = r.Inputs[0];
        j.Phase = "Opening";
        using var reader = OsaReader.Open(archive, () => r.Password ?? PasswordPrompt?.Invoke(archive));
        j.Total = reader.TotalUnpacked;
        var progress = new Progress<ExtractProgress>(p =>
        {
            j.Phase = test ? "Testing" : "Extracting";
            j.In = p.BytesDone;
            j.Fraction = p.BytesTotal == 0 ? 1 : (double)p.BytesDone / p.BytesTotal;
            j.Detail = $"{p.FilesDone}/{p.FilesTotal} files";
        });
        var res = test
            ? Extractor.Test(reader, progress, ct)
            : Extractor.Extract(reader, r.Output, null, new ExtractOptions { Overwrite = r.Overwrite ? OverwriteMode.Overwrite : OverwriteMode.Rename }, progress, ct);
        j.In = reader.TotalUnpacked;
        j.Out = reader.FileLength;
        j.Output = test ? archive : r.Output;
        lock (j.Log) j.Log.AddRange(res.Errors.Select(e => "error: " + e));
        if (res.FilesFailed > 0 || res.Errors.Count > 0)
        {
            int newer = res.FilesNeedingNewerVersion, damaged = res.FilesFailed - newer;
            if (newer > 0 && damaged == 0)
                throw new InvalidDataException($"{newer} file(s) need a newer version of Osmium; every other file verified.");
            throw new InvalidDataException($"{damaged} file(s) failed verification{(newer > 0 ? $", {newer} need a newer version of Osmium" : "")}; {res.Errors.Count} error(s).");
        }
    }

    private sealed record HistoryEntry(string Name, JobKind Kind, JobState State, long In, long Out, string? Output, DateTime Created, double Seconds, string? Error,
        RecycleSummary? Recycle = null);

    private void SaveHistory()
    {
        if (_historyFile is null) return;
        try
        {
            List<HistoryEntry> h;
            lock (_lock)
                h = _jobs.Where(j => !(j.State is JobState.Queued or JobState.Running or JobState.Paused)).TakeLast(50)
                    .Select(j => new HistoryEntry(Snap(j).Name, j.Request.Kind, j.State, j.In, j.Out, j.Output, j.Created, j.Clock.Elapsed.TotalSeconds, j.Error, j.Recycle)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
            File.WriteAllText(_historyFile, JsonSerializer.Serialize(h));
        }
        catch (IOException) { }
    }

    private void LoadHistory()
    {
        if (_historyFile is null || !File.Exists(_historyFile)) return;
        try
        {
            foreach (var h in JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_historyFile)) ?? [])
            {
                var j = new Job(new JobRequest { Kind = h.Kind, Name = h.Name, Output = h.Output ?? "" })
                {
                    State = h.State, In = h.In, Out = h.Out, Output = h.Output, Created = h.Created, Error = h.Error, Phase = h.State.ToString(), Fraction = 1,
                    Recycle = h.Recycle,
                };
                lock (_lock) _jobs.Add(j);
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_lock) foreach (var j in _jobs) { j.Gate.Resume(); j.Cts.Cancel(); }
        try { _loop.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        SaveHistory();
    }
}
