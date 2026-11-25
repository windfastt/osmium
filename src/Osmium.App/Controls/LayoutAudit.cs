// Layout: logical units only
namespace Osmium.App;

/// <summary>
/// Checks a control tree the way a reviewer looks at a screenshot, and reports one line per problem with the control's
/// path: (a) a visible control that runs past its parent's client area (content scrolled inside lists and scrolling
/// panels is not counted), (b) a label whose text needs more room than it has, (c) a custom-drawn control without an
/// accessible name, (d) text in a bundled face that lacks one of its characters, (e) a control smaller than its
/// minimum size or a button smaller than its text needs.
/// </summary>
internal static class LayoutAudit
{
    /// <summary>The problems found under <paramref name="root"/> (empty: none).</summary>
    public static IReadOnlyList<string> Run(Control root)
    {
        var findings = new List<string>();
        Check(root, Describe(root), findings, checkBounds: false);
        Walk(root, Describe(root), findings);
        return findings;
    }

    private static void Walk(Control parent, string path, List<string> findings)
    {
        // Native controls draw and scroll their own content; panels that scroll show what passes their edge.
        if (parent is ListView or ListBox or TreeView or TextBoxBase or ComboBox or NumericUpDown or PropertyGrid or DataGridView) return;
        bool scrolls = parent is ScrollableControl { AutoScroll: true };
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            string childPath = $"{path} / {Describe(child)}";
            if (child is EmptyState && parent is FastList list && (list.VirtualMode ? list.VirtualListSize : list.Items.Count) > 0)
                findings.Add($"{childPath}: shown over a list that has rows");
            Check(child, childPath, findings, checkBounds: !scrolls);
            Walk(child, childPath, findings);
        }
    }

    private static void Check(Control c, string path, List<string> findings, bool checkBounds)
    {
        // (a) Past the parent's edge (a pixel of rounding is allowed).
        if (checkBounds && c.Parent is { } parent && c.Width > 0 && c.Height > 0)
        {
            var client = parent.ClientRectangle;
            int right = c.Right - client.Right, bottom = c.Bottom - client.Bottom;
            if (right > 1 || bottom > 1 || c.Left < -1 || c.Top < -1)
                findings.Add($"{path}: runs past its parent ({c.Bounds} in {client.Size})");
        }

        // (b) Text that does not fit: cut off at the bottom or, on one line, at the side.
        // The label's own measure: on one line when it sizes itself freely, wrapped at its width otherwise.
        if (c is Label { Text.Length: > 0 } label && !label.AutoEllipsis)
        {
            bool oneLine = label.AutoSize && label.MaximumSize.Width == 0;
            int width = Math.Max(2, label.Width); // 1 would mean "no limit" to the label
            var needed = oneLine ? label.GetPreferredSize(Size.Empty) : label.GetPreferredSize(new Size(width, 0));
            if (needed.Height > label.Height + 1) findings.Add($"{path}: text needs {needed.Height} px of height, has {label.Height}");
            else if (oneLine && needed.Width > label.Width + 1) findings.Add($"{path}: text needs {needed.Width} px of width, has {label.Width}");
        }
        if (c is CheckBox or RadioButton && c.Text.Length > 0)
        {
            var preferred = c.GetPreferredSize(Size.Empty);
            if (preferred.Width > c.Width + 1 && c is ButtonBase { AutoSize: false }) findings.Add($"{path}: text needs {preferred.Width} px of width, has {c.Width}");
        }

        // (c) Custom-drawn controls need a name for screen readers.
        bool custom = c is OsmiumButton or SegmentedChoice or TagLabel or Banner or Meter or StatTiles or EmptyState or NavButton
                      || c is CardPanel { Heading: not null };
        if (custom && string.IsNullOrWhiteSpace(c.AccessibilityObject.Name) && string.IsNullOrWhiteSpace(c.AccessibleName))
            findings.Add($"{path}: no accessible name");

        // (d) Text in a bundled face with characters the face lacks.
        if (c.Text.Length > 0 && BrandFonts.IsBundled(c.Font) && !BrandFonts.Covers(c.Font, c.Text))
            findings.Add($"{path}: \"{Short(c.Text)}\" has characters {c.Font.Name} lacks");

        // (e) Smaller than its minimum, or a button smaller than its text.
        if ((!c.MinimumSize.IsEmpty && (c.Width < c.MinimumSize.Width - 1 || c.Height < c.MinimumSize.Height - 1)))
            findings.Add($"{path}: {c.Size} is below its minimum {c.MinimumSize}");
        if (c is OsmiumButton b)
        {
            var preferred = b.GetPreferredSize(Size.Empty);
            if (b.Width + 1 < preferred.Width || b.Height + 1 < preferred.Height) findings.Add($"{path}: {b.Size} cuts its text ({preferred} needed)");
        }
    }

    /// <summary>"Label \"Archive:\"", "FastList", "CardPanel \"Pipeline\"": enough to find the control.</summary>
    private static string Describe(Control c)
    {
        string name = c.GetType().Name;
        string text = c switch
        {
            CardPanel card => card.Heading ?? "",
            ListView or TextBoxBase or ComboBox => "",
            _ => c.Text,
        };
        if (c.Name.Length > 0) name += "#" + c.Name;
        return text.Length > 0 ? $"{name} \"{Short(text)}\"" : name;
    }

    private static string Short(string text)
    {
        string line = text.ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..39] + "…" : line;
    }
}
