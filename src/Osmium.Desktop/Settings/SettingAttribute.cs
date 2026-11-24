using System.Globalization;
using System.Reflection;

namespace Osmium.Desktop.Settings;

public enum SettingEditor { Auto, Slider, FolderPath, FilePath, Password, Multiline, ByteSize, Radio, StringList }

[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsCategoryAttribute(string name) : Attribute
{
    public string Name => name;
    public int Order { get; set; }
    /// <summary>Segoe Fluent Icons / MDL2 glyph code point.</summary>
    public string? Glyph { get; set; }
    public string? Description { get; set; }
}

/// <summary>Metadata that drives the generic settings renderer (no hand-built forms per setting).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label => label;
    public string? Description { get; set; }
    public string? Group { get; set; }
    public int Order { get; set; }
    public double Min { get; set; } = double.NaN;
    public double Max { get; set; } = double.NaN;
    public double Step { get; set; } = 1;
    public string? Unit { get; set; }
    public bool Experimental { get; set; }
    public bool RequiresRestart { get; set; }
    public bool Advanced { get; set; }
    public bool Dangerous { get; set; }
    public string? WarningText { get; set; }
    /// <summary>"Prop", "!Prop" or "Prop=Value" referring to a sibling property.</summary>
    public string? VisibleWhen { get; set; }
    public string? EnabledWhen { get; set; }
    public SettingEditor Editor { get; set; } = SettingEditor.Auto;
    public string? Keywords { get; set; }
}

/// <summary>A renderable setting: either a reflected property or an engine codec parameter.</summary>
public sealed class SettingDescriptor
{
    public required string Category { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public string? Group { get; init; }
    public required Type ValueType { get; init; }
    public double Min { get; init; } = double.NaN;
    public double Max { get; init; } = double.NaN;
    public double Step { get; init; } = 1;
    public string? Unit { get; init; }
    public bool Experimental { get; init; }
    public bool RequiresRestart { get; init; }
    public bool Advanced { get; init; }
    public bool Dangerous { get; init; }
    public string? WarningText { get; init; }
    public string? VisibleWhen { get; init; }
    public string? EnabledWhen { get; init; }
    public SettingEditor Editor { get; init; }
    public string? Keywords { get; init; }
    public object? Default { get; init; }
    public required Func<object?> Get { get; init; }
    public required Action<object?> Set { get; init; }
    public IReadOnlyList<string>? Choices { get; init; }

    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        Category.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a candidate value; returns an error message or null.</summary>
    public string? Validate(object? value)
    {
        if (value is null) return null;
        if (IsNumeric(ValueType))
        {
            double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsNaN(Min) && d < Min) return $"Must be at least {Min}{Unit}.";
            if (!double.IsNaN(Max) && d > Max) return $"Must be at most {Max}{Unit}.";
        }
        return null;
    }

    public static bool IsNumeric(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float);
}

public static class SettingReflector
{
    /// <summary>Builds descriptors for every [Setting] property of every [SettingsCategory] sub-object.</summary>
    public static List<SettingDescriptor> Describe(object root)
    {
        var list = new List<SettingDescriptor>();
        foreach (var catProp in root.GetType().GetProperties().Where(p => p.PropertyType.GetCustomAttribute<SettingsCategoryAttribute>() is not null))
        {
            var cat = catProp.PropertyType.GetCustomAttribute<SettingsCategoryAttribute>()!;
            var section = catProp.GetValue(root)!;
            var defaults = Activator.CreateInstance(catProp.PropertyType)!;
            foreach (var p in catProp.PropertyType.GetProperties())
            {
                var a = p.GetCustomAttribute<SettingAttribute>();
                if (a is null) continue;
                var pp = p;
                list.Add(new SettingDescriptor
                {
                    Category = cat.Name, Key = $"{catProp.Name}.{p.Name}", Label = a.Label, Description = a.Description,
                    Group = a.Group, ValueType = p.PropertyType, Min = a.Min, Max = a.Max, Step = a.Step, Unit = a.Unit,
                    Experimental = a.Experimental, RequiresRestart = a.RequiresRestart, Advanced = a.Advanced, Dangerous = a.Dangerous,
                    WarningText = a.WarningText, VisibleWhen = a.VisibleWhen, EnabledWhen = a.EnabledWhen, Editor = a.Editor,
                    Keywords = a.Keywords, Default = p.GetValue(defaults),
                    Get = () => pp.GetValue(section), Set = v => pp.SetValue(section, Coerce(v, pp.PropertyType)),
                    Choices = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null,
                });
            }
        }
        return list;
    }

    public static IReadOnlyList<(string Name, int Order, string? Glyph, string? Description)> Categories(object root) =>
        root.GetType().GetProperties()
            .Select(p => p.PropertyType.GetCustomAttribute<SettingsCategoryAttribute>())
            .Where(a => a is not null)
            .Select(a => (a!.Name, a.Order, a.Glyph, a.Description))
            .OrderBy(c => c.Order).ToList();

    public static object? Coerce(object? v, Type t)
    {
        if (v is null) return null;
        if (t.IsInstanceOfType(v)) return v;
        if (t.IsEnum) return v is string s ? Enum.Parse(t, s) : Enum.ToObject(t, v);
        if (t == typeof(List<string>)) return v is string str ? str.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : v;
        return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
    }

    /// <summary>Evaluates "Prop", "!Prop" or "Prop=Value" against sibling settings in the same section.</summary>
    public static bool Evaluate(string? condition, IReadOnlyList<SettingDescriptor> all, string category)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        bool negate = condition.StartsWith('!');
        string expr = negate ? condition[1..] : condition;
        string name = expr, expected = "True";
        int eq = expr.IndexOf('=');
        if (eq > 0) { name = expr[..eq]; expected = expr[(eq + 1)..]; }
        var d = all.FirstOrDefault(x => x.Category == category && x.Key.EndsWith("." + name, StringComparison.Ordinal))
                ?? all.FirstOrDefault(x => x.Key.EndsWith("." + name, StringComparison.Ordinal));
        if (d is null) return true;
        bool result = string.Equals(Convert.ToString(d.Get(), CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase);
        return negate ? !result : result;
    }
}
