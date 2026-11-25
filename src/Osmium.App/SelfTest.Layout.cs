using System.Reflection;
using Osmium.Desktop.Theming;

namespace Osmium.App;

// Layout checks of the UI self-test: the bundled fonts, a specimen of every component in every state, the message
// dialogs, the main window at its smallest size, larger text and a small screen.
internal static partial class SelfTest
{
    static partial void LayoutSteps(SelfTestContext ctx)
    {
        ctx.Step("brand fonts", () =>
        {
            ctx.Detail($"Windows {Environment.OSVersion.Version}");
            ctx.Detail("bundled faces loaded: " + (BrandFonts.Loaded.Count == 0 ? "none" : string.Join(", ", BrandFonts.Loaded.Order(StringComparer.Ordinal))));
            foreach (var spec in TypeScale.All)
            {
                var font = Theme.Role(spec.Role);
                ctx.Detail($"{spec.Role}: {font.Name} {font.SizeInPoints:0.#} pt, drawn by Windows with {BrandFonts.ActualFace(font)}");
            }
            CheckBrandFace(ctx, TypeRole.Title, "Compress");
            CheckBrandFace(ctx, TypeRole.Spec, "COMPRESS");
            CheckBrandFace(ctx, TypeRole.CardHeading, "Compress");
            CheckBrandFace(ctx, TypeRole.Number, "604 KB");
        });

        ctx.Step("theme specimen", () =>
        {
            using var d = new SpecimenDialog();
            ctx.Host(d, null);
            d.Prepare();
            ctx.Pump();
            ctx.Shoot(d, "kit-specimen", "Every component of the app's look in each of its states, with the type scale");
            Fail(LayoutAudit.Run(d), "the specimen");
            d.Close();
        });

        ctx.Step("message dialogs", () =>
        {
            string path = Path.Combine(ctx.TempDir, "Osmium sample", "Reports for the second quarter", "measurements from the field station.csv");
            var cases = new (string Id, MessageDialog Dialog, string Caption, string? Default)[]
            {
                ("dialog-message-info", Ui.BuildMessage(MessageKind.Information, "The archive was tested: 6 files, 2.0 MB, every checksum matches."),
                    "Information message", null),
                ("dialog-message-error", Ui.BuildMessage(MessageKind.Error, $"Could not open {path}: the file is being used by another program."),
                    "Error message with a long path that wraps", null),
                ("dialog-message-confirm", Ui.BuildMessage(MessageKind.Question, "Remove the 3 selected files from the list? The files themselves stay where they are.", "Osmium", "Yes", "No"),
                    "Yes-or-no question, No by default", "No"),
                ("dialog-message-ask", Ui.BuildMessage(MessageKind.Question,
                    "photos.zip expands to more than 1,000 times its size (about 1.2 TB). Archives like this are sometimes made to fill the disk. Extract anyway?",
                    "Osmium", "Extract anyway", "Stop"), "Question with its own answers, Stop by default", "Stop"),
            };
            foreach (var (id, dialog, caption, expectedDefault) in cases)
            {
                using (dialog)
                {
                    ctx.Host(dialog, null);
                    ctx.Shoot(dialog, id, caption);
                    if (expectedDefault is not null && dialog.DefaultButton?.Text != expectedDefault)
                        throw new InvalidOperationException($"{id}: the default button is {dialog.DefaultButton?.Text ?? "none"}, not {expectedDefault}");
                    if (expectedDefault is not null && (dialog.CancelButton as Button)?.Text != expectedDefault)
                        throw new InvalidOperationException($"{id}: Esc does not answer {expectedDefault}");
                    Fail(LayoutAudit.Run(dialog), id);
                    dialog.Close();
                }
            }
        });

        ctx.Step("narrow window", () =>
        {
            var main = ctx.Main;
            Size minimum;
            using (var probe = new Form { FormBorderStyle = FormBorderStyle.Sizable, Size = main.MinimumSize })
                minimum = probe.ClientSize;
            ctx.Detail($"smallest window {main.MinimumSize.Width} × {main.MinimumSize.Height}: client area {minimum.Width} × {minimum.Height}");
            try
            {
                var strict = new List<string>();
                foreach (var size in new[] { minimum, MainClient })
                {
                    ctx.HostMain(size);
                    foreach (var name in MainForm.PageNames)
                    {
                        main.Navigate(name);
                        ctx.Pump();
                        if (size == minimum)
                            ctx.Shoot(main, $"page-{name.ToLowerInvariant()}-narrow", $"{name} page in the smallest main window ({size.Width} × {size.Height})");
                        var page = main.Page<Control>(name);
                        bool isStrict = page.GetType().GetCustomAttribute<LayoutStrictAttribute>() is not null;
                        foreach (var finding in LayoutAudit.Run(page))
                        {
                            string line = $"{name} page at {size.Width} × {size.Height}: {finding}";
                            if (isStrict) strict.Add(line); else ctx.Note(line);
                        }
                    }
                    foreach (var part in main.Chrome)
                        strict.AddRange(LayoutAudit.Run(part).Select(f => $"window at {size.Width} × {size.Height}: {f}"));
                }
                Fail(strict, "the main window");
            }
            finally { ctx.RestoreMain(); }
        });

        ctx.Step("large text", () =>
        {
            var tone = ctx.SampleFiles.FirstOrDefault(f => Path.GetFileName(f) == Osmium.Desktop.Samples.SampleFiles.Tone);
            Theme.ScaleText(1.25f);
            try
            {
                var findings = new List<string>();
                using (var about = new AboutDialog(ctx.App.AppIcon))
                {
                    ctx.Host(about, null);
                    findings.AddRange(LayoutAudit.Run(about).Select(f => "About: " + f));
                    about.Close();
                }
                if (tone is null) ctx.Note("large text: no sample file for the Tune dialog");
                else
                {
                    using var tune = new TuneDialog(ctx.App, tone, null, "max", startDeep: false);
                    ctx.Host(tune, tune.ClientSize);
                    ctx.Await(tune.Analysis, WorkTimeout, "The Tune dialog's analysis");
                    findings.AddRange(LayoutAudit.Run(tune).Select(f => "Tune: " + f));
                    tune.Close();
                }
                Fail(SettingsEditorAsNotes(ctx, findings), "text at 125 %");
            }
            finally { Theme.ScaleText(1f); }
        });

        ctx.Step("small screen", () =>
        {
            var tone = ctx.SampleFiles.FirstOrDefault(f => Path.GetFileName(f) == Osmium.Desktop.Samples.SampleFiles.Tone)
                       ?? throw new InvalidOperationException("no sample file");
            using var d = new TuneDialog(ctx.App, tone, null, "max", startDeep: false);
            var screen = new Rectangle(0, 0, 1366, 728); // a 1366 × 768 screen less the taskbar
            d.FitTo(screen);
            Size client;
            using (var probe = new Form { FormBorderStyle = FormBorderStyle.Sizable, Size = d.Size })
                client = probe.ClientSize;
            ctx.Detail($"window {d.Size.Width} × {d.Size.Height} on a {screen.Width} × {screen.Height} working area: client area {client.Width} × {client.Height}");
            ctx.Host(d, client);
            ctx.Await(d.Analysis, WorkTimeout, "The Tune dialog's analysis");
            ctx.Shoot(d, "dialog-tune-small", "Tune dialog sized for a 1366 × 768 screen: its buttons stay in view");
            foreach (var button in new[] { d.AcceptButton as Control, d.CancelButton as Control })
            {
                if (button is null) throw new InvalidOperationException("the dialog has no OK or Cancel button");
                var bounds = d.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                if (!d.ClientRectangle.Contains(bounds) || !button.Visible)
                    throw new InvalidOperationException($"\"{button.Text}\" at {bounds} is outside the client area {d.ClientRectangle}");
            }
            Fail(SettingsEditorAsNotes(ctx, LayoutAudit.Run(d)), "the Tune dialog on a small screen");
            d.Close();
        });
    }

    /// <summary>Proves Windows draws a role in its bundled face: GDI reports that face, and the text's width differs
    /// from the system stand-in's at the same size.</summary>
    private static void CheckBrandFace(SelfTestContext ctx, TypeRole role, string text)
    {
        var font = Theme.Role(role);
        var fallback = Theme.Fallback(role);
        if (!BrandFonts.IsBundled(font)) throw new InvalidOperationException($"{role} is shown in {font.Name}, not in its bundled face");
        string face = BrandFonts.ActualFace(font);
        if (!face.Equals(font.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Windows draws {role} with {face}, not with {font.Name}");
        int own = TextRenderer.MeasureText(text, font).Width, other = TextRenderer.MeasureText(text, fallback).Width;
        double apart = Math.Abs(own - other) / (double)Math.Max(1, Math.Max(own, other));
        ctx.Detail($"\"{text}\" is {own} px wide in {font.Name} and {other} px in {fallback.Name} at the same size ({apart:P0} apart)");
        if (role is TypeRole.Title or TypeRole.Spec && apart < 0.08)
            throw new InvalidOperationException($"\"{text}\" in {font.Name} is as wide as in {fallback.Name}: the bundled face is probably not used");
    }

    /// <summary>
    /// The Tune dialog shows the codec's parameters in the settings editor (SettingsRenderer), which the Settings page
    /// shares and which lays out its own rows: what the audit finds inside it is noted, the rest is returned.
    /// </summary>
    private static List<string> SettingsEditorAsNotes(SelfTestContext ctx, IEnumerable<string> findings)
    {
        var rest = new List<string>();
        foreach (var f in findings)
        {
            if (f.Contains($"/ {nameof(SettingsRenderer)}", StringComparison.Ordinal)) ctx.Note("settings editor: " + f);
            else rest.Add(f);
        }
        return rest;
    }

    private static void Fail(IReadOnlyCollection<string> findings, string what)
    {
        if (findings.Count > 0)
            throw new InvalidOperationException($"layout problems in {what}:{Environment.NewLine}      " + string.Join(Environment.NewLine + "      ", findings));
    }

    /// <summary>A dialog showing every component of the app's look in each state, the type scale, and button labels in
    /// two faces side by side.</summary>
    private sealed class SpecimenDialog : ThemedDialog
    {
        private readonly List<Action> _afterShown = [];

        public SpecimenDialog() : base("Specimen")
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var columns = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
            columns.Padding = Space.Pad(columns, Space.Xl - Space.Xs, 20, Space.Xl - Space.Xs, Space.Lg);
            columns.Controls.Add(LeftColumn(), 0, 0);
            columns.Controls.Add(RightColumn(), 1, 0);
            Controls.Add(columns);
            ButtonBar.Controls.Add(Ui.Primary("Close"));
            ButtonBar.Controls.Add(Ui.Button("Cancel"));
        }

        /// <summary>States that need a handle: focus looks, a selected row.</summary>
        public void Prepare()
        {
            foreach (var a in _afterShown) a();
        }

        private static FlowLayoutPanel Stack(int width)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            f.Margin = Space.Pad(f, Space.Xs, 0, Space.Lg, 0);
            f.MinimumSize = new Size(f.LogicalToDeviceUnits(width), 0);
            return f;
        }

        private static Label Caption(string text)
        {
            var l = Ui.Spec(text);
            l.Margin = Space.Pad(l, 0, Space.Md, 0, Space.Xs);
            return l;
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var r = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            r.Margin = Padding.Empty;
            r.Controls.AddRange(controls);
            return r;
        }

        private Control LeftColumn()
        {
            var s = Stack(560);
            var header = new PageHeader("Compress", "Drop files or folders here. Osmium plans a pipeline for each kind of data.", "Specimen · page header · 6 files · 2.0 MB → 604 KB");
            foreach (var line in header.Lines) s.Controls.Add(line);

            var cardText = Ui.Text("Content sits on the card colour, 16 pixels from the rounded one-pixel border.");
            var card = Ui.Card(cardText, "Card heading");
            card.AddHeadingControl(Ui.Tag("Experimental", TagKind.Warning));
            card.Width = card.LogicalToDeviceUnits(540);
            card.Height = card.GetPreferredSize(new Size(card.Width, 0)).Height;
            s.Controls.Add(card);

            s.Controls.Add(Caption("Buttons: normal · hover · pressed · focused · disabled · default"));
            foreach (bool primary in new[] { false, true })
            {
                var row = Row();
                foreach (var look in Enum.GetValues<ButtonLook>())
                {
                    var b = (OsmiumButton)(primary ? Ui.Primary(look.ToString()) : Ui.Button(look.ToString()));
                    b.ForcedLook = look;
                    if (look == ButtonLook.Disabled) b.Enabled = false;
                    row.Controls.Add(b);
                }
                s.Controls.Add(row);
            }

            s.Controls.Add(Caption("Segmented choice: selected · focused"));
            var seg = new SegmentedChoice("Ask", "Rename", "Replace", "Skip") { SelectedIndex = 1 };
            var segFocused = new SegmentedChoice("Ask", "Rename", "Replace", "Skip") { SelectedIndex = 0, ForceFocusLook = true };
            s.Controls.Add(Row(seg, segFocused));

            s.Controls.Add(Caption("Tags"));
            s.Controls.Add(Row(Ui.Tag("Neutral"), Ui.Tag("Encrypted", TagKind.Accent), Ui.Tag("Caution", TagKind.Warning),
                Ui.Tag("Failed", TagKind.Danger), Ui.Tag("Verified", TagKind.Success)));

            s.Controls.Add(Caption("Banners"));
            foreach (var (kind, text) in new[]
                     {
                         (BannerKind.Info, "Osmium keeps running in the notification area when this window is closed."),
                         (BannerKind.Warning, "Experimental codecs can change between versions. Keep the program that wrote an archive."),
                         (BannerKind.Danger, "The archive's file list is damaged. Recover files reads what can still be trusted."),
                         (BannerKind.Success, "Every block was read back and matches its checksum."),
                     })
            {
                var banner = new Banner(kind, text) { Width = s.MinimumSize.Width };
                if (kind == BannerKind.Danger) banner.SetAction("Recover files", () => { });
                s.Controls.Add(banner);
            }

            s.Controls.Add(Caption("Meters: 35 % · paused · failed · done · working"));
            foreach (var (state, value) in new (MeterState, double?)[]
                     {
                         (MeterState.Normal, 0.35), (MeterState.Paused, 0.35), (MeterState.Failed, 0.35), (MeterState.Done, 1), (MeterState.Normal, null),
                     })
            {
                var m = new Meter { Width = s.MinimumSize.Width, State = state, Value = value, AccessibleName = $"{state} meter" };
                m.Margin = Space.Pad(m, 0, Space.Xs, 0, Space.Sm);
                if (value is null) _afterShown.Add(() => m.Phase = 40); // one frame of the moving segment
                s.Controls.Add(m);
            }
            return s;
        }

        private Control RightColumn()
        {
            var s = Stack(600);
            int width = s.MinimumSize.Width;
            s.Controls.Add(Caption("Result tiles"));
            var tiles = new StatTiles { Width = width };
            tiles.SetTiles([
                new StatTile("604 KB", "Archive size", "from 2.0 MB", 0.29),
                new StatTile("29.2 %", "Of the original", "smaller is better"),
                new StatTile("6", "Files", "in 4 groups"),
                new StatTile("3.2 s", "Time", "on 4 threads"),
            ]);
            s.Controls.Add(tiles);

            s.Controls.Add(Caption("Empty states: normal · drop target"));
            var empty = new EmptyState("", "No files yet", "Drop files or folders here, or add them with the button.");
            empty.SetAction("Add files…", () => { });
            var target = new EmptyState("", "No files yet", "Drop files or folders here, or add them with the button.") { ReleaseTitle = "Release to add the files", Highlight = true };
            var emptyRow = Row();
            foreach (var e in new[] { empty, target })
            {
                var card = Ui.Card(e);
                card.Padding = Space.Pad(card, Space.Xs, Space.Xs, Space.Xs, Space.Xs);
                card.Size = new Size((width - card.LogicalToDeviceUnits(Space.Lg)) / 2, card.LogicalToDeviceUnits(190));
                card.Margin = ReferenceEquals(e, empty) ? Space.Pad(card, 0, 0, Space.Lg, 0) : Padding.Empty;
                emptyRow.Controls.Add(card);
            }
            s.Controls.Add(emptyRow);

            s.Controls.Add(Caption("List: icons, a right-aligned number column, a selected row"));
            var list = Ui.List(("Name", 220), ("Detected type", 200), ("Size", 90));
            list.RightAlign(2);
            var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(list.LogicalToDeviceUnits(16), list.LogicalToDeviceUnits(16)) };
            _ = icons.Handle; // Add copies the image into the native list, so the bitmap can be disposed
            using (var bmp = new Bitmap(icons.ImageSize.Width, icons.ImageSize.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Draw.RoundedBox(g, new Rectangle(1, 1, bmp.Width - 2, bmp.Height - 2), 3, Theme.P.Mark, null);
                }
                icons.Images.Add("file", bmp);
            }
            Disposed += (_, _) => icons.Dispose();
            list.SetIcons(icons);
            foreach (var (name, type, size) in new[]
                     {
                         ("chart.png", "PNG → Incompressible", "6.5 KB"), ("gradient.bmp", "BMP 400×300 24-bit → Image", "351.6 KB"),
                         ("measurements.csv", "text (UTF-8/ASCII) → Text", "300.0 KB"), ("tone.wav", "WAV PCM 16-bit ×2 → Audio", "344.6 KB"),
                     })
                list.Items.Add(new ListViewItem([name, type, size]) { ImageKey = "file" });
            var listCard = Ui.Card(list);
            listCard.Padding = Space.Pad(listCard, Space.Xs, Space.Xs, Space.Xs, Space.Xs);
            listCard.Size = new Size(width, list.LogicalToDeviceUnits(30 * 5 + 36));
            s.Controls.Add(listCard);
            _afterShown.Add(() => { list.Items[1].Selected = true; list.ItemsChanged(); });

            s.Controls.Add(Caption("Type scale"));
            foreach (var spec in TypeScale.All.Where(t => t.Role is not (TypeRole.Glyph or TypeRole.GlyphLarge)))
            {
                string sample = spec.Role switch
                {
                    TypeRole.Number or TypeRole.NumberLive => "604 KB · 29.2 %",
                    TypeRole.Mono => "delta(stride=4)+lzma(dict=64M)",
                    _ => "Compress archives",
                };
                var font = Theme.FontFor(spec.Role, sample);
                var l = new Label { Text = $"{TypeScale.Cased(spec.Role, sample)}", Font = font, AutoSize = true, UseMnemonic = false, Tag = "keep", ForeColor = Theme.ColorOf(spec.Role) };
                var name = new Label { Text = $"{spec.Role} · {font.Name} {font.SizeInPoints:0.#} pt", Font = Theme.Caption, AutoSize = true, UseMnemonic = false, Tag = "muted", Anchor = AnchorStyles.Left };
                name.MinimumSize = new Size(name.LogicalToDeviceUnits(190), 0);
                name.Margin = Space.Pad(name, 0, 6, Space.Sm, 0);
                l.Margin = Padding.Empty;
                s.Controls.Add(Row(name, l));
            }

            s.Controls.Add(Caption("Button labels: Segoe UI Variable · Instrument Sans"));
            var segoe = Row(Ui.Button("Compress"), Ui.Button("Extract here"), Ui.Primary("Use this pipeline"));
            var instrumentFont = BrandFonts.Create(TypeScale.InstrumentSansNumber, Theme.BodyStrong.SizeInPoints);
            var instrument = Row(Ui.Button("Compress"), Ui.Button("Extract here"), Ui.Primary("Use this pipeline"));
            if (instrumentFont is not null)
            {
                foreach (Control b in instrument.Controls) b.Font = instrumentFont;
                Disposed += (_, _) => instrumentFont.Dispose();
            }
            s.Controls.Add(segoe);
            s.Controls.Add(instrument);
            return s;
        }
    }
}
