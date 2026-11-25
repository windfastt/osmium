// Layout: logical units only
using System.ComponentModel;
using Osmium.Desktop.Theming;

namespace Osmium.App;

/// <summary>
/// The head of a page, like a section of the product page: a short line of facts (<see cref="Spec"/>, in capitals in
/// the monospaced face, muted), the page's <see cref="Title"/> (condensed and heavy) and one sentence about it
/// (<see cref="Lede"/>, wrapping at the page's width). Each can be changed while the page is shown.
/// <see cref="Ui.Page(PageHeader, Control, Control[])"/> lays out its three lines as the first rows of the page; the spec
/// line shows only while it is not empty. A title or spec with characters the bundled faces lack is shown in the system
/// font of the same size.
/// </summary>
internal sealed class PageHeader : Control
{
    private readonly Label _spec, _title;
    private readonly WrapLabel _lede;
    private string _specText;

    public PageHeader(string title = "", string lede = "", string spec = "")
    {
        _specText = spec;
        _spec = new Label { AutoSize = true, Tag = "muted", UseMnemonic = false, Visible = spec.Length > 0 };
        _spec.Margin = Space.Pad(_spec, 0, 0, 0, Space.Xs);
        _title = Ui.Heading(title);
        _title.Tag = "keep";
        _title.ForeColor = Theme.P.Text;
        _title.AccessibleRole = AccessibleRole.StaticText;
        _lede = new WrapLabel { Text = lede, Font = Theme.Lede, Tag = "secondary", Cap = Ui.ReadableWidth, Visible = lede.Length > 0 };
        _lede.Margin = Space.Pad(_lede, 0, 0, 0, Space.Lg);
        _title.Margin = Space.Pad(_title, 0, 0, 0, lede.Length > 0 ? 6 : Space.Lg);
        ShowSpec();
        AccessibleName = title;
        AccessibleDescription = lede;
        _title.AccessibleDescription = lede;
    }

    /// <summary>The line of facts above the title; shown in capitals. Empty: no line.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Spec
    {
        get => _specText;
        set
        {
            _specText = value ?? "";
            ShowSpec();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title.Text;
        set
        {
            _title.Text = value;
            _title.Font = Theme.FontFor(TypeRole.Title, value);
            AccessibleName = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Lede
    {
        get => _lede.Text;
        set
        {
            _lede.Text = value;
            _lede.Visible = value.Length > 0;
            _title.Margin = Space.Pad(_title, 0, 0, 0, value.Length > 0 ? 6 : Space.Lg);
            AccessibleDescription = value;
            _title.AccessibleDescription = value;
        }
    }

    /// <summary>The lines in page order (spec, title, lede), for <see cref="Ui.Page(PageHeader, Control, Control[])"/>.</summary>
    internal IReadOnlyList<Control> Lines => [_spec, _title, _lede];

    private void ShowSpec()
    {
        _spec.Text = TypeScale.Cased(TypeRole.Spec, _specText);
        _spec.Font = Theme.FontFor(TypeRole.Spec, _specText);
        _spec.Visible = _specText.Length > 0;
    }
}
