using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Proto.Desktop;

/// <summary>
/// JSON-lines event log for the evaluation harness: one object per line with the event name, milliseconds since the
/// process was created (kernel creation time, the same clock the harness uses), Unix wall-clock milliseconds and the
/// event's fields. Written by hand (reflection over the fields object, no System.Text.Json), so logging adds next to no
/// JIT work to the start-up it measures; a trimmed or AOT build may drop the fields but keeps the times.
/// </summary>
internal static class EventLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _out;
    public static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void Open(string? path)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _out = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public static double SinceStartMs => (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds;

    public static void Write(string ev, object? fields = null)
    {
        var now = DateTime.UtcNow;
        var sb = new StringBuilder(256).Append('{');
        bool first = true;
        if (fields is not null)
            foreach (var p in fields.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (!first) sb.Append(',');
                first = false;
                Str(sb, p.Name).Append(':');
                object? v;
                try { v = p.GetValue(fields); } catch (Exception) { v = null; }
                Value(sb, v, 0);
            }
        if (!first) sb.Append(',');
        sb.Append("\"ev\":");
        Str(sb, ev);
        sb.Append(",\"ms\":").Append(Num((now - ProcessStartUtc).TotalMilliseconds, 1));
        // Wall clock in Unix milliseconds: lines from different processes (tray host and window) compare on it.
        sb.Append(",\"utc\":").Append(Num((now - DateTime.UnixEpoch).TotalMilliseconds, 1));
        sb.Append(",\"pid\":").Append(Environment.ProcessId).Append('}');
        var line = sb.ToString();
        lock (Gate)
        {
            _out?.WriteLine(line);
            if (_out is null) Trace.WriteLine(line);
        }
    }

    /// <summary>The same hand-written JSON for a reply object (dictionaries keep their keys in AOT builds).</summary>
    public static string Json(object? v)
    {
        var sb = new StringBuilder(256);
        Value(sb, v, 0);
        return sb.ToString();
    }

    private static string Num(double d, int digits = 3) =>
        double.IsFinite(d) ? Math.Round(d, digits).ToString("0.###", CultureInfo.InvariantCulture) : "null";

    private static void Value(StringBuilder sb, object? v, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Str(sb, s); break;
            case char c: Str(sb, c.ToString()); break;
            case double d: sb.Append(Num(d)); break;
            case float f: sb.Append(Num(f)); break;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
            case Enum e: Str(sb, e.ToString()); break;
            case IFormattable n when v.GetType().IsPrimitive: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
            case IDictionary dict when depth < 5:
                sb.Append('{');
                bool firstKey = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!firstKey) sb.Append(',');
                    firstKey = false;
                    Str(sb, kv.Key.ToString() ?? "").Append(':');
                    Value(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                break;
            case IEnumerable list when depth < 5:
                sb.Append('[');
                bool firstItem = true;
                foreach (var x in list)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Value(sb, x, depth + 1);
                }
                sb.Append(']');
                break;
            default:
                var props = depth < 5 ? v.GetType().GetProperties() : [];
                if (props.Length == 0) { Str(sb, v.ToString() ?? ""); break; }
                sb.Append('{');
                bool firstProp = true;
                foreach (var p in props)
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (!firstProp) sb.Append(',');
                    firstProp = false;
                    Str(sb, p.Name).Append(':');
                    object? pv;
                    try { pv = p.GetValue(v); } catch (Exception) { pv = null; }
                    Value(sb, pv, depth + 1);
                }
                sb.Append('}');
                break;
        }
    }

    private static StringBuilder Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"');
    }
}
