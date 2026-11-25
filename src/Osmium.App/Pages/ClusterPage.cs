using Osmium.Core.Distributed;
using Osmium.Desktop.Settings;

namespace Osmium.App;

internal sealed class ClusterPage : UserControl, IPage
{
    private readonly TrayApp _app;
    private readonly EditBox _key = new() { UseSystemPasswordChar = true };
    private readonly Button _reveal;
    private readonly FastList _list = Ui.List(("Name", 180), ("Address", 200), ("Slots", 60), ("Status", 400));
    private readonly Label _local = Ui.Text("", muted: true, maxWidth: 900);
    private readonly Label _found = Ui.Text("", muted: true, maxWidth: 900);
    /// <summary>Shows and changes the "Lend this PC as a worker" setting.</summary>
    private readonly CheckBox _lend = new() { Text = "Lend this PC to the cluster", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    /// <summary>The key box holds text the user typed, pasted or generated and has not saved yet.</summary>
    private bool _keyEdited;
    private bool _loadingKey, _syncingLend;

    public ClusterPage(TrayApp app)
    {
        _app = app;
        // A whole key (OSM1- and 11 groups: 70 characters) fits; monospaced so the groups line up when read aloud.
        _key.Font = Theme.Mono;
        _key.Width = _key.LogicalToDeviceUnits(560);
        _key.TextChanged += (_, _) => { if (!_loadingKey) _keyEdited = true; };
        _key.EnterAction = SaveKey;
        LoadKey();
        _reveal = Ui.RevealButton(_key);
        var keyRow = Ui.Bar(new Label { Text = "Cluster key:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, _key,
            _reveal,
            Ui.Button("Generate", (_, _) => GenerateKey()),
            Ui.Button("Copy", (_, _) => Ui.CopyToClipboard(ClusterSettings.NormalizeClusterKey(_key.Text))),
            Ui.Button("Save", (_, _) => SaveKey(), accent: true));
        _lend.CheckedChanged += (_, _) =>
        {
            if (_syncingLend) return;
            var s = _app.Settings.Clone();
            s.Cluster.LendThisPc = _lend.Checked;
            _app.SaveSettings(s); // SettingsChanged updates the status line
        };
        var tools = Ui.Bar(Ui.Button("Find workers on this network", (_, _) => Discover()), _lend);
        Controls.Add(Ui.Page("Cluster",
            "PCs that share the same key can split compression work. Workers receive file contents (never names or passwords). Workers are refused if they run a different engine version; every remote result is re-verified here.",
            _list, keyRow, tools, _local, _found));
        UpdateLocal();
        app.SettingsChanged += OnSettingsChanged;
        app.WorkerChanged += UpdateLocalIfAlive;
        Disposed += (_, _) => { app.SettingsChanged -= OnSettingsChanged; app.WorkerChanged -= UpdateLocalIfAlive; };
    }

    private void UpdateLocalIfAlive()
    {
        if (!IsDisposed) UpdateLocal();
    }

    public bool HasUnsavedChanges => _keyEdited && _key.Text != _app.Settings.Cluster.ClusterKey;

    public void Activated()
    {
        LoadKey();
        UpdateLocal();
    }

    private void OnSettingsChanged()
    {
        if (IsDisposed) return;
        LoadKey();
        UpdateLocal();
    }

    /// <summary>Shows the saved key, unless the box holds an unsaved one.</summary>
    private void LoadKey()
    {
        if (_keyEdited) return;
        _loadingKey = true;
        try { _key.Text = _app.Settings.Cluster.ClusterKey; }
        finally { _loadingKey = false; }
    }

    /// <summary>Puts a new random key in the box, unmasked so it can be read and copied (it is saved with Save).</summary>
    internal void GenerateKey()
    {
        if (_key.Text.Length > 0 && !Ui.Confirm("Replace the current key? Other PCs will need the new key.")) return;
        _key.Text = ClusterKey.Generate().ToString();
        Ui.SetMasked(_key, _reveal, masked: false);
    }

    /// <summary>Saves the key in its canonical form (OSM1-XXXXX-…), however it was pasted; an empty box removes it.</summary>
    private void SaveKey()
    {
        string key;
        if (string.IsNullOrWhiteSpace(_key.Text)) key = "";
        else
        {
            try { key = ClusterKey.Parse(_key.Text).ToString(); }
            catch (FormatException e) { Ui.Error(e.Message, "Cluster key not saved"); return; }
        }
        var s = _app.Settings.Clone();
        s.Cluster.ClusterKey = key;
        _app.SaveSettings(s);
        _keyEdited = false;
        LoadKey();
        Ui.Info(key.Length == 0 ? "Cluster key removed." : "Cluster key saved.");
    }

    /// <summary>The "Lend this PC" check and the status line: the worker's state, or why it does not run.</summary>
    private void UpdateLocal()
    {
        var c = _app.Settings.Cluster;
        _syncingLend = true;
        try
        {
            _lend.Checked = c.LendThisPc;
            _lend.Enabled = c.LendThisPc || !string.IsNullOrWhiteSpace(c.ClusterKey);
        }
        finally { _syncingLend = false; }
        var w = _app.LocalWorker;
        string? problem = _app.WorkerError;
        _local.Text = problem
            ?? (w is not null
                ? $"This PC is a worker on port {c.WorkerPort}: {w.Stats.Connections} coordinator(s), {w.Stats.Running} running, {w.Stats.TasksDone} task(s) done, {Ui.Bytes(w.Stats.BytesIn)} received."
                : string.IsNullOrWhiteSpace(c.ClusterKey)
                    ? "This PC is not lending its CPU. Save a cluster key first."
                    : "This PC is not lending its CPU. Turn on \"Lend this PC to the cluster\" to start the worker.");
        _local.ForeColor = problem is null ? Theme.P.Muted : Theme.P.Danger;
    }

    private void Discover()
    {
        if (string.IsNullOrWhiteSpace(_key.Text))
        {
            Ui.Info("Enter, paste or generate a cluster key first: workers only answer PCs that use the same key.");
            return;
        }
        ClusterKey key;
        try { key = ClusterKey.Parse(_key.Text); }
        catch (FormatException e) { Ui.Error(e.Message, "Cluster key"); return; }
        Ui.RunSafe(this, async () =>
        {
            _list.Items.Clear();
            _found.Text = "Looking for workers…";
            var found = await Discovery.ProbeAsync(key, TimeSpan.FromSeconds(2), CancellationToken.None);
            foreach (var f in found) _list.Items.Add(new ListViewItem([f.Name, f.Endpoint, f.Slots.ToString(), "found"]));
            foreach (var manual in _app.Settings.Cluster.Workers) _list.Items.Add(new ListViewItem(["(manual)", manual, "", "configured"]));
            int authenticated = 0;
            foreach (ListViewItem item in _list.Items)
            {
                try
                {
                    await using var n = await RemoteNode.ConnectAsync(item.SubItems[1].Text, key, null, CancellationToken.None);
                    item.SubItems[0].Text = n.Name;
                    item.SubItems[2].Text = n.Slots.ToString();
                    item.SubItems[3].Text = $"OK — authenticated, {Ui.Bytes(n.Memory)} memory";
                    authenticated++;
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException or OperationCanceledException)
                {
                    item.SubItems[3].Text = "error: " + e.Message;
                }
            }
            // Discovery answers are authenticated with the key, so a PC with a different key stays silent.
            _found.Text = _list.Items.Count == 0
                ? "No workers answered. Every PC must use the same cluster key as this one and have \"Lend this PC\" turned on, and its firewall must allow TCP 46510 and UDP 46511."
                : $"{_list.Items.Count} worker(s) listed, {authenticated} authenticated." +
                  (authenticated < _list.Items.Count ? " The others show why they failed, for example a different cluster key or Osmium version." : "");
        });
    }
}
