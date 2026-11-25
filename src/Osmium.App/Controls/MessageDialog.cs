// Layout: logical units only
using System.Media;

namespace Osmium.App;

/// <summary>What a message dialog says: which glyph, colour and sound it has.</summary>
internal enum MessageKind { Information, Error, Warning, Question }

/// <summary>
/// Osmium's message box: a <see cref="ThemedDialog"/> in the current theme with the kind's glyph, the text wrapped at
/// 480 logical pixels, and one or two buttons with the caller's words. The default button is the safe answer (No, or
/// the caller's <c>no</c>), and Esc gives it. Ctrl+C copies the dialog's text as Windows' message boxes do. Built by
/// <see cref="Ui.BuildMessage"/>; shown by <see cref="Ui.Info"/>, <see cref="Ui.Error"/>, <see cref="Ui.Confirm"/> and
/// <see cref="Ui.Ask"/>.
/// </summary>
internal sealed class MessageDialog : ThemedDialog
{
    private readonly MessageKind _kind;
    private readonly string _message;
    private readonly List<Button> _buttons = [];

    public MessageDialog(MessageKind kind, string text, string title, string? yes, string? no, bool yesIsDefault) : base(title)
    {
        _kind = kind;
        _message = text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(LogicalToDeviceUnits(320), 0);
        KeyPreview = true;

        var body = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
        body.Padding = Space.Pad(body, Space.Xl, Space.Xl, Space.Xl, Space.Md);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var glyph = new Label
        {
            Text = Glyph, Font = Theme.GlyphLarge, AutoSize = true, UseMnemonic = false, Tag = "keep", ForeColor = GlyphColor,
            AccessibleRole = AccessibleRole.Graphic, AccessibleName = KindName,
        };
        glyph.Margin = Space.Pad(glyph, 0, 0, Space.Lg, 0);
        var label = new WrapLabel { Text = text, Cap = 480, Font = Theme.Body };
        label.Margin = Space.Pad(label, 0, 6, 0, 0); // the first line beside the glyph's middle
        body.Controls.Add(glyph, 0, 0);
        body.Controls.Add(label, 1, 0);
        Controls.Add(body);

        if (yes is null)
        {
            var ok = Add(no ?? "OK", DialogResult.OK);
            AcceptButton = ok;
            CancelButton = ok;
            ActiveControl = ok;
        }
        else
        {
            // Added right to left: the answer that goes ahead on the right, as in Windows' own dialogs.
            var yesButton = Add(yes, DialogResult.Yes);
            var noButton = Add(no ?? "No", DialogResult.No);
            var preferred = yesIsDefault ? yesButton : noButton;
            AcceptButton = preferred;
            CancelButton = noButton;
            ActiveControl = preferred;
        }
        AccessibleName = title;
        AccessibleDescription = text;
    }

    /// <summary>The button that Enter presses.</summary>
    internal Button? DefaultButton => AcceptButton as Button;

    /// <summary>The dialog's buttons, from right to left.</summary>
    internal IReadOnlyList<Button> Buttons => _buttons;

    private Button Add(string text, DialogResult result)
    {
        var b = Ui.Button(text);
        b.DialogResult = result;
        ButtonBar.Controls.Add(b);
        _buttons.Add(b);
        return b;
    }

    private string Glyph => _kind switch
    {
        MessageKind.Error => "",
        MessageKind.Warning => "",
        MessageKind.Question => "",
        _ => "",
    };

    private string KindName => _kind switch
    {
        MessageKind.Error => "Error",
        MessageKind.Warning => "Warning",
        MessageKind.Question => "Question",
        _ => "Information",
    };

    private Color GlyphColor => _kind switch
    {
        MessageKind.Error => Theme.P.Danger,
        MessageKind.Warning or MessageKind.Question => Theme.P.WarningText,
        _ => Theme.P.AccentInk,
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        (_kind switch
        {
            MessageKind.Error => SystemSounds.Hand,
            MessageKind.Warning or MessageKind.Question => SystemSounds.Exclamation,
            _ => SystemSounds.Asterisk,
        }).Play();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C))
        {
            // The layout Windows' message boxes copy: caption, text and buttons between dashed lines.
            const string rule = "---------------------------";
            string buttons = string.Join("   ", _buttons.AsEnumerable().Reverse().Select(b => b.Text));
            Ui.CopyToClipboard(string.Join(Environment.NewLine, rule, Text, rule, _message, rule, buttons, rule, ""));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
