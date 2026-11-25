using Osmium.Desktop;

namespace Osmium.App;

/// <summary>
/// Asks for an archive password (twice for a new archive). It comes to the front when shown, also when an Explorer
/// verb opened it (the forwarding instance allows that, see Program.Main), so typing goes here and not to Explorer.
/// </summary>
internal sealed class PasswordDialog : Form
{
    private readonly EditBox _first = new() { UseSystemPasswordChar = true, Width = 280 };
    private readonly EditBox? _confirm;

    /// <summary>The password after OK, without control characters.</summary>
    public string? Password { get; private set; }

    public PasswordDialog(string prompt, bool confirm)
    {
        Text = "Osmium";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Padding = new Padding(12);
        TopMost = true;
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        t.Controls.Add(Ui.Text(prompt));
        t.Controls.Add(_first);
        if (confirm)
        {
            t.Controls.Add(Ui.Text("Confirm password"));
            _confirm = new EditBox { UseSystemPasswordChar = true, Width = 280 };
            t.Controls.Add(_confirm);
        }
        var ok = Ui.Button("OK", (_, _) => Check(), accent: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        t.Controls.Add(Ui.Bar(ok, cancel));
        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _first;
        Theme.Apply(this);
    }

    /// <summary>Shows the dialog; null when it was cancelled.</summary>
    public static string? Ask(string prompt, bool confirm)
    {
        using var f = new PasswordDialog(prompt, confirm);
        return f.ShowDialog() == DialogResult.OK ? f.Password : null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Native.SetDarkTitleBar(Handle, Theme.IsDark); } catch (Exception) { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _first.Focus();
    }

    /// <summary>OK: keeps the dialog open on an empty or mismatched password, with the confirm box cleared.</summary>
    private void Check()
    {
        // A new password loses invisible control characters (a pasted tab or line break); one for an existing archive is used as typed.
        bool isNew = _confirm is not null;
        string pw = TextInput.PasswordFromText(_first.Text, isNew);
        string? error = pw.Length == 0 ? "The password cannot be empty."
            : _confirm is not null && TextInput.PasswordFromText(_confirm.Text, isNew) != pw ? "The passwords do not match."
            : null;
        if (error is null)
        {
            Password = pw;
            return;
        }
        DialogResult = DialogResult.None; // the OK button had already set OK
        Ui.Error(error);
        if (_confirm is not null) _confirm.Text = "";
        _first.SelectAll();
        _first.Focus();
    }
}
