using System.Globalization;
using Osmium.Core.Pipeline;

namespace Osmium.Desktop.Settings;

/// <summary>
/// Settings-renderer descriptors for the parameters of a codec or filter (the Tune dialog), shown the way their
/// <see cref="ParamDef"/> describes them: <see cref="ParamType.Bool"/> as a checkbox, <see cref="ParamType.Choice"/>
/// as a list of the choice names, <see cref="ParamType.Size"/> as a size such as "64M", anything else as a number.
/// Every editor value maps back to exactly the parameter value it came from.
/// </summary>
public static class ParamDescriptors
{
    /// <param name="get">The stage's current parameter values.</param>
    /// <param name="set">Stores changed parameter values.</param>
    public static List<SettingDescriptor> For(IMethod method, Func<ParamValues> get, Action<ParamValues> set) =>
        method.Params.Select(d => For(method, d, get, set)).ToList();

    public static SettingDescriptor For(IMethod method, ParamDef d, Func<ParamValues> get, Action<ParamValues> set)
    {
        long Current() => get().Get(d.Name, d.Default);
        void Store(long v) => set(get().With(d.Name, v));
        string category = method.DisplayName, key = $"{method.Name}.{d.Name}";

        if (d.Type == ParamType.Bool)
            return new SettingDescriptor
            {
                Category = category, Key = key, Label = d.Label, Description = d.Description, Experimental = d.Experimental,
                ValueType = typeof(bool), Default = d.Default != 0,
                Get = () => Current() != 0,
                Set = v => Store(v is true ? 1 : 0),
            };
        if (d.Type == ParamType.Choice && d.Choices is { } choices && choices.Count == d.Max - d.Min + 1)
            return new SettingDescriptor
            {
                Category = category, Key = key, Label = d.Label, Description = d.Description, Experimental = d.Experimental,
                ValueType = typeof(string), Choices = choices, Default = ChoiceName(d, d.Default),
                Get = () => ChoiceName(d, Current()),
                Set = v => Store(ChoiceValue(d, v as string)),
            };
        if (d.Type == ParamType.Size)
            return new SettingDescriptor
            {
                Category = category, Key = key, Label = d.Label, Description = d.Description, Experimental = d.Experimental,
                ValueType = typeof(string), Editor = SettingEditor.ByteSize, Default = ParamValues.FormatSize(d.Default),
                Get = () => ParamValues.FormatSize(Current()),
                Set = v => Store(ParamValues.ParseNumber((v as string ?? "").Trim())),
                TextValidator = text => ValidateSize(d, text),
            };
        return new SettingDescriptor
        {
            Category = category, Key = key, Label = d.Label, Description = d.Description, Experimental = d.Experimental,
            ValueType = typeof(long), Min = d.Min, Max = d.Max, Default = d.Default,
            Get = () => Current(),
            Set = v => Store(Convert.ToInt64(v, CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>The name shown for a choice value (values outside the list show as their number).</summary>
    public static string ChoiceName(ParamDef d, long value)
    {
        long i = value - d.Min;
        return d.Choices is { } c && i >= 0 && i < c.Count ? c[(int)i] : value.ToString(CultureInfo.InvariantCulture);
    }

    public static long ChoiceValue(ParamDef d, string? name)
    {
        int i = d.Choices?.ToList().IndexOf(name ?? "") ?? -1;
        return i >= 0 ? d.Min + i : throw new FormatException($"'{name}' is not one of: {string.Join(", ", d.Choices ?? [])}.");
    }

    /// <summary>Null for a size within the parameter's range, else what is wrong with the text.</summary>
    public static string? ValidateSize(ParamDef d, string text)
    {
        long v;
        try { v = ParamValues.ParseNumber(text.Trim()); }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            return "Enter a size in bytes or with K, M or G, for example 64M.";
        }
        return v < d.Min || v > d.Max ? $"Must be between {ParamValues.FormatSize(d.Min)} and {ParamValues.FormatSize(d.Max)}." : null;
    }
}
