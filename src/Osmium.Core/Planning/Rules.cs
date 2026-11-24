using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Osmium.Core.Analysis;
using Osmium.Core.Pipeline;

namespace Osmium.Core.Planning;

/// <summary>gitignore-style globs over archive-relative paths ("/" separated, case-insensitive).</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        bool anchored = glob.Contains('/');
        if (!anchored) sb.Append("(?:.*/)?");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}

public sealed class RuleMatch
{
    public string? Path { get; set; }
    public string[]? Globs { get; set; }
    public string? Regex { get; set; }
    public string[]? Extensions { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    /// <summary>FileKind or CompatClass names (e.g. "Text", "CodeX86", "Compressed").</summary>
    public string[]? Kinds { get; set; }
}

public sealed class RuleActions
{
    /// <summary>Forced pipeline in text form, e.g. <c>delta(stride=4)+lzma(dict=128M)</c>.</summary>
    public string? Pipeline { get; set; }
    public bool? Store { get; set; }
    public bool? Exclude { get; set; }
    public string[]? ForbidCodecs { get; set; }
    public string[]? ForbidFilters { get; set; }
    /// <summary>"auto", "own" (file gets its own block) or "group:&lt;name&gt;".</summary>
    public string? Solid { get; set; }
    /// <summary>Parameter overrides applied to whatever pipeline is chosen, e.g. "lzma.dict": "256M".</summary>
    public Dictionary<string, string>? Params { get; set; }
}

public sealed class RuleOrigin
{
    public string? Type { get; set; }          // "tuned", "manual", "imported"
    public DateTime? At { get; set; }
    public long? OriginalSize { get; set; }
    public long? MeasuredSize { get; set; }
    public string? Note { get; set; }
}

public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public RuleMatch Match { get; set; } = new();
    public RuleActions Actions { get; set; } = new();
    public RuleOrigin? Origin { get; set; }

    [JsonIgnore] internal int Layer { get; set; }
    [JsonIgnore] internal int Order { get; set; }
    [JsonIgnore] private Regex[]? _globs;
    [JsonIgnore] private Regex? _regex;

    public bool NeedsAnalysis => Match.Kinds is { Length: > 0 };

    public bool Matches(string relPath, long size, FileAnalysis? a)
    {
        if (!Enabled) return false;
        var m = Match;
        if (m.Path is not null && !string.Equals(m.Path, relPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Globs is { Length: > 0 })
        {
            _globs ??= m.Globs.Select(Glob.ToRegex).ToArray();
            if (!_globs.Any(g => g.IsMatch(relPath))) return false;
        }
        if (m.Regex is not null)
        {
            _regex ??= new Regex(m.Regex, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            if (!_regex.IsMatch(relPath)) return false;
        }
        if (m.Extensions is { Length: > 0 })
        {
            string ext = System.IO.Path.GetExtension(relPath).TrimStart('.');
            if (!m.Extensions.Any(e => string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (m.MinSize is { } mn && size < mn) return false;
        if (m.MaxSize is { } mx && size > mx) return false;
        if (m.Kinds is { Length: > 0 })
        {
            if (a is null) return false;
            if (!m.Kinds.Any(k => string.Equals(k, a.Kind.ToString(), StringComparison.OrdinalIgnoreCase)
                                || string.Equals(k, a.Class.ToString(), StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }
}

/// <summary>The resolved per-file outcome of all matching rules, with provenance for "explain".</summary>
public sealed class EffectiveRules
{
    public PipelineSpec? Pipeline { get; set; }
    public bool Exclude { get; set; }
    public string Solid { get; set; } = "auto";
    public HashSet<string> ForbidCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ForbidFilters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> Params { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Provenance { get; } = [];
    public bool Any => Pipeline is not null || Exclude || Solid != "auto" || ForbidCodecs.Count > 0 || ForbidFilters.Count > 0 || Params.Count > 0;

    public string Key => $"{Pipeline}|{Solid}|{string.Join(',', ForbidCodecs.Order())}|{string.Join(',', ForbidFilters.Order())}|{string.Join(',', Params.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value))}";

    public bool Allows(PipelineSpec spec) =>
        !spec.Stages.Any(s => s.Method.Kind == MethodKind.Codec ? ForbidCodecs.Contains(s.Method.Name) : ForbidFilters.Contains(s.Method.Name));

    /// <summary>Applies "codec.param" overrides to a spec (unknown names are ignored).</summary>
    public PipelineSpec Apply(PipelineSpec spec)
    {
        if (Params.Count == 0) return spec;
        var stages = spec.Stages.Select(s =>
        {
            var p = s.Params;
            foreach (var kv in Params)
            {
                int dot = kv.Key.IndexOf('.');
                if (dot <= 0) continue;
                if (!kv.Key[..dot].Equals(s.Method.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string name = kv.Key[(dot + 1)..].ToLowerInvariant();
                if (s.Method.Params.Any(d => d.Name == name)) p = p.With(name, kv.Value);
            }
            return new StageSpec(s.Method, p);
        });
        return new PipelineSpec(stages);
    }
}

/// <summary>Rule set with precedence layer → priority → file order (later wins); forbid lists are unioned and
/// Exclude from any rule wins.</summary>
public sealed class RuleSet
{
    public const int LayerBuiltIn = 0, LayerGlobal = 1, LayerJob = 2;

    public int Version { get; set; } = 1;
    public List<Rule> Rules { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // rules.json is a file people edit: keep '&', '<', '+', quotes in globs and non-ASCII names readable instead of
        // writing &-style escapes (it is never embedded in HTML).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RuleSet Load(string path, int layer = LayerGlobal)
    {
        var rs = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(path), Json) ?? new RuleSet();
        for (int i = 0; i < rs.Rules.Count; i++) { rs.Rules[i].Layer = layer; rs.Rules[i].Order = i; }
        return rs;
    }

    public static RuleSet Parse(string json, int layer = LayerJob)
    {
        var rs = JsonSerializer.Deserialize<RuleSet>(json, Json) ?? new RuleSet();
        for (int i = 0; i < rs.Rules.Count; i++) { rs.Rules[i].Layer = layer; rs.Rules[i].Order = i; }
        return rs;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Assigns a precedence layer to every rule (file order breaks ties within the layer).</summary>
    public RuleSet AtLayer(int layer)
    {
        for (int i = 0; i < Rules.Count; i++) { Rules[i].Layer = layer; Rules[i].Order = i; }
        return this;
    }

    public static RuleSet Combine(params RuleSet?[] sets)
    {
        var all = new RuleSet();
        foreach (var s in sets) if (s is not null) all.Rules.AddRange(s.Rules);
        return all;
    }

    /// <summary>Built-in rules: never recompress archives/media; keep Osmium archives as-is.</summary>
    public static RuleSet BuiltIn()
    {
        var rs = new RuleSet
        {
            Rules =
            [
                new Rule { Id = "builtin-compressed", Description = "Already-compressed formats are stored.",
                    Match = new RuleMatch { Kinds = ["Compressed"] }, Actions = new RuleActions { Store = true } },
            ],
        };
        foreach (var r in rs.Rules) r.Layer = LayerBuiltIn;
        return rs;
    }

    /// <summary>Validates every rule (pipelines parse, regexes compile, globs compile).</summary>
    public List<string> Check(MethodRegistry registry)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>();
        foreach (var r in Rules)
        {
            if (!ids.Add(r.Id)) problems.Add($"{r.Id}: duplicate rule id");
            try { if (r.Actions.Pipeline is { } p) PipelineSpec.Parse(p, registry); }
            catch (Exception e) { problems.Add($"{r.Id}: pipeline: {e.Message}"); }
            try { if (r.Match.Regex is { } rx) _ = new Regex(rx, RegexOptions.NonBacktracking); }
            catch (Exception e) { problems.Add($"{r.Id}: regex: {e.Message}"); }
            try { foreach (var g in r.Match.Globs ?? []) Glob.ToRegex(g); }
            catch (Exception e) { problems.Add($"{r.Id}: glob: {e.Message}"); }
            foreach (var kv in r.Actions.Params ?? [])
            {
                try { ParamValues.ParseNumber(kv.Value); }
                catch (Exception) { problems.Add($"{r.Id}: param {kv.Key}: '{kv.Value}' is not a number/size"); }
                if (!kv.Key.Contains('.')) problems.Add($"{r.Id}: param '{kv.Key}' must be method.param (e.g. lzma.dict)");
            }
            if (r.Match is { Path: null, Globs: null or [], Regex: null, Extensions: null or [], MinSize: null, MaxSize: null, Kinds: null or [] })
                problems.Add($"{r.Id}: matches every file");
        }
        return problems;
    }

    public EffectiveRules Resolve(string relPath, long size, FileAnalysis? analysis, MethodRegistry registry)
    {
        var eff = new EffectiveRules();
        var matching = Rules.Where(r => r.Matches(relPath, size, analysis))
            .OrderBy(r => r.Layer).ThenBy(r => r.Priority).ThenBy(r => r.Order).ToList();
        foreach (var r in matching)
        {
            var a = r.Actions;
            if (a.Exclude == true) { eff.Exclude = true; eff.Provenance.Add($"exclude ← {r.Id}"); }
            if (a.Store == true) { eff.Pipeline = PipelineSpec.Of(registry.Get("store")); eff.Provenance.Add($"store ← {r.Id}"); }
            if (a.Pipeline is not null) { eff.Pipeline = PipelineSpec.Parse(a.Pipeline, registry); eff.Provenance.Add($"pipeline ← {r.Id}"); }
            if (a.Solid is not null) { eff.Solid = a.Solid; eff.Provenance.Add($"solid={a.Solid} ← {r.Id}"); }
            foreach (var c in a.ForbidCodecs ?? []) eff.ForbidCodecs.Add(c);
            foreach (var f in a.ForbidFilters ?? []) eff.ForbidFilters.Add(f);
            foreach (var kv in a.Params ?? []) { eff.Params[kv.Key] = ParamValues.ParseNumber(kv.Value); eff.Provenance.Add($"{kv.Key}={kv.Value} ← {r.Id}"); }
        }
        if (eff.Pipeline is not null && !eff.Allows(eff.Pipeline))
        {
            eff.Provenance.Add($"forced pipeline {eff.Pipeline} conflicts with a forbid rule; forbid wins");
            eff.Pipeline = null;
        }
        return eff;
    }
}
