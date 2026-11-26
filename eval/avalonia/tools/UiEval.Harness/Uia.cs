using System.Diagnostics;
using System.Windows.Automation;

namespace UiEval;

/// <summary>UI Automation from outside the process (System.Windows.Automation, the client API screen readers use).</summary>
internal static class Uia
{
    public static string TypeName(AutomationElement e)
    {
        try { return e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
        catch (Exception) { return "?"; }
    }

    public static string Name(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; }
        catch (Exception) { return ""; }
    }

    public static AutomationElement? Window(IntPtr hwnd)
    {
        try { return AutomationElement.FromHandle(hwnd); }
        catch (Exception) { return null; }
    }

    public static List<AutomationElement> All(AutomationElement root, Condition? c = null)
    {
        try { return root.FindAll(TreeScope.Descendants, c ?? Condition.TrueCondition).Cast<AutomationElement>().ToList(); }
        catch (Exception) { return []; }
    }

    public static AutomationElement? Find(AutomationElement root, ControlType type, string? name = null, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        Condition c = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
        if (name is not null) c = new AndCondition(c, new PropertyCondition(AutomationElement.NameProperty, name));
        do
        {
            try
            {
                var e = root.FindFirst(TreeScope.Descendants, c);
                if (e is not null) return e;
            }
            catch (Exception) { }
            Thread.Sleep(50);
        } while (sw.ElapsedMilliseconds < timeoutMs);
        return null;
    }

    public static string[] Patterns(AutomationElement e)
    {
        try { return [.. e.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("Identifiers.Pattern", "").Replace("PatternIdentifiers.Pattern", ""))]; }
        catch (Exception) { return []; }
    }

    public static string Describe(AutomationElement e)
    {
        try
        {
            var c = e.Current;
            var r = c.BoundingRectangle;
            return $"{TypeName(e)} name='{c.Name}' class='{c.ClassName}' focusable={c.IsKeyboardFocusable} rect=[{r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0}] patterns=[{string.Join(",", Patterns(e))}]";
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    /// <summary>Top-level UIA windows of a process.</summary>
    public static List<AutomationElement> ProcessWindows(int pid)
    {
        try
        {
            return AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid))
                .Cast<AutomationElement>().ToList();
        }
        catch (Exception) { return []; }
    }
}
