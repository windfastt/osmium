using System.Globalization;
using System.Text;
using Osmium.Core.IO;

namespace Osmium.Core.Pipeline;

/// <summary>
/// Immutable, ordered set of named integer parameters for one pipeline stage.
/// Every parameter in Osmium is an integer (booleans are 0/1, enums are indices, sizes are bytes),
/// which keeps serialization canonical and the tuner's search space simple.
/// </summary>
public sealed class ParamValues : IEquatable<ParamValues>
{
    public static readonly ParamValues Empty = new(new SortedDictionary<string, long>(StringComparer.Ordinal));

    private readonly SortedDictionary<string, long> _values;

    private ParamValues(SortedDictionary<string, long> values) => _values = values;

    public static ParamValues From(IEnumerable<KeyValuePair<string, long>> values)
    {
        var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var kv in values) d[kv.Key] = kv.Value;
        return new ParamValues(d);
    }

    public static ParamValues Of(params (string Name, long Value)[] values) =>
        From(values.Select(v => new KeyValuePair<string, long>(v.Name, v.Value)));

    public IEnumerable<KeyValuePair<string, long>> All => _values;
    public int Count => _values.Count;
    public bool Contains(string name) => _values.ContainsKey(name);
    public long Get(string name, long fallback) => _values.TryGetValue(name, out var v) ? v : fallback;
    public int GetInt(string name, int fallback) => checked((int)Get(name, fallback));
    public bool GetBool(string name, bool fallback) => Get(name, fallback ? 1 : 0) != 0;

    public ParamValues With(string name, long value)
    {
        var d = new SortedDictionary<string, long>(_values, StringComparer.Ordinal) { [name] = value };
        return new ParamValues(d);
    }

    public ParamValues Without(string name)
    {
        if (!_values.ContainsKey(name)) return this;
        var d = new SortedDictionary<string, long>(_values, StringComparer.Ordinal);
        d.Remove(name);
        return new ParamValues(d);
    }

    /// <summary>Overlay: values from <paramref name="other"/> win.</summary>
    public ParamValues Merge(ParamValues other)
    {
        if (other.Count == 0) return this;
        var d = new SortedDictionary<string, long>(_values, StringComparer.Ordinal);
        foreach (var kv in other._values) d[kv.Key] = kv.Value;
        return new ParamValues(d);
    }

    public byte[] Serialize()
    {
        var w = new BlobWriter().U((ulong)_values.Count);
        foreach (var kv in _values) w.Str(kv.Key).I(kv.Value);
        return w.ToArray();
    }

    public static ParamValues Deserialize(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return Empty;
        var r = new BlobReader(blob);
        int n = r.Int(256);
        var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            string name = r.Str(64);
            d[name] = r.I();
        }
        if (!r.End) throw new InvalidDataException("Trailing bytes in parameter blob.");
        return new ParamValues(d);
    }

    /// <summary>Formats as <c>a=1,b=64M</c>. Byte sizes that are exact multiples print with K/M/G suffixes.</summary>
    public string Format(Func<string, bool>? isSize = null)
    {
        var sb = new StringBuilder();
        foreach (var kv in _values)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key).Append('=');
            sb.Append(isSize?.Invoke(kv.Key) == true ? FormatSize(kv.Value) : kv.Value.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static ParamValues Parse(string text)
    {
        var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return new ParamValues(d);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Expected name=value but got '{part}'.");
            string name = part[..eq].Trim().ToLowerInvariant();
            d[name] = ParseNumber(part[(eq + 1)..].Trim());
        }
        return new ParamValues(d);
    }

    public static long ParseNumber(string s)
    {
        if (s.Length == 0) throw new FormatException("Empty value.");
        string lower = s.ToLowerInvariant();
        if (lower is "true" or "on" or "yes") return 1;
        if (lower is "false" or "off" or "no") return 0;
        long mul = 1;
        string num = lower;
        if (num.EndsWith("ib")) num = num[..^2];
        else if (num.EndsWith('b')) num = num[..^1];
        char last = num.Length > 0 ? num[^1] : ' ';
        switch (last)
        {
            case 'k': mul = 1L << 10; num = num[..^1]; break;
            case 'm': mul = 1L << 20; num = num[..^1]; break;
            case 'g': mul = 1L << 30; num = num[..^1]; break;
        }
        if (num.Contains('.'))
            return checked((long)Math.Round(double.Parse(num, CultureInfo.InvariantCulture) * mul));
        return checked(long.Parse(num, NumberStyles.Integer, CultureInfo.InvariantCulture) * mul);
    }

    public static string FormatSize(long v)
    {
        if (v != 0 && v % (1L << 30) == 0) return (v >> 30).ToString(CultureInfo.InvariantCulture) + "G";
        if (v != 0 && v % (1L << 20) == 0) return (v >> 20).ToString(CultureInfo.InvariantCulture) + "M";
        if (v != 0 && v % (1L << 10) == 0) return (v >> 10).ToString(CultureInfo.InvariantCulture) + "K";
        return v.ToString(CultureInfo.InvariantCulture);
    }

    public bool Equals(ParamValues? other)
    {
        if (other is null || other.Count != Count) return false;
        foreach (var kv in _values)
            if (!other._values.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ParamValues);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var kv in _values) { h.Add(kv.Key); h.Add(kv.Value); }
        return h.ToHashCode();
    }

    public override string ToString() => Format();
}
