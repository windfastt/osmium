using System.ComponentModel;

namespace Osmium.App;

/// <summary>
/// The head of a page: a short line of facts (<see cref="Spec"/>), the page's <see cref="Title"/> and one sentence
/// about it (<see cref="Lede"/>). Each can be changed while the page is shown. <see cref="Ui.Page(PageHeader, Control, Control[])"/>
/// lays out its three lines as the first rows of the page: the title as <see cref="Ui.Heading"/>, the lede as muted text
/// up to 900 pixels wide, and the spec line above the title, only while it is not empty.
/// </summary>
internal sealed class PageHeader : Control
{
    private readonly Label _spec, _title, _lede;

    public PageHeader(string title = "", string lede = "", string spec = "")
    {
        _spec = new Label { Text = spec, Font = Theme.Spec, AutoSize = true, Tag = "muted", UseMnemonic = false, Margin = new Padding(0, 0, 0, 2), Visible = spec.Length > 0 };
        _title = Ui.Heading(title);
        _lede = Ui.Text(lede, muted: true, maxWidth: 900);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Spec
    {
        get => _spec.Text;
        set
        {
            _spec.Text = value;
            _spec.Visible = value.Length > 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get => _title.Text; set => _title.Text = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Lede { get => _lede.Text; set => _lede.Text = value; }

    /// <summary>The lines in page order (spec, title, lede), for <see cref="Ui.Page(PageHeader, Control, Control[])"/>.</summary>
    internal IReadOnlyList<Control> Lines => [_spec, _title, _lede];
}
