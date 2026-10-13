using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using PerpetuaSync.Core;

namespace PerpetuaSync;

/// <summary>
/// The whole app. The first time, two steps: find World of Warcraft, connect to the site. After that one screen:
/// the crest and a Sync button, with Activity (the log) and Settings (folder, disconnect) as tabs. While the window
/// is open it also syncs by itself whenever WoW saves (logout, /reload, exit). Closing the window stops it; nothing
/// runs in the background or at startup.
/// </summary>
sealed class MainForm : Form
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ResyncEvery = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PairPoll = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PairFor = TimeSpan.FromMinutes(10);

    readonly bool _dev;
    readonly Settings _settings;
    readonly SiteClient _site;
    readonly Syncer _syncer;
    readonly string _version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";

    List<Flavor> _flavors = [];
    Dictionary<string, DateTime> _seen = [];
    DateTime _lastUpload = DateTime.MinValue, _lastDownload = DateTime.MinValue, _retryAt = DateTime.MinValue;
    bool _busy, _forceNext;
    string? _connectedAs;
    CancellationTokenSource? _pairing;
    DateTime _pairUntil;

    // Screens, swapped inside _host.
    readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    readonly Control _stepFolder, _stepConnect, _home, _activity, _settingsView;
    readonly TabStrip _tabs = new(["Home", "Activity", "Settings"]) { Anchor = AnchorStyles.None };

    // Step 1 and Settings share these: there's one folder.
    readonly Field _folderStep = new(), _folderSettings = new();
    readonly Label _folderInfoStep = Words(Theme.Muted, 11f), _folderInfoSettings = Words(Theme.Muted, 11f);
    readonly GoldButton _next = new("Next", GoldButton.Kinds.Solid);

    // Step 2.
    readonly Label _connectText = Words(Theme.Text, 12.5f);
    readonly GoldButton _getCode = new("Get a code", GoldButton.Kinds.Solid);
    readonly FlowLayoutPanel _codeBox = Column();
    readonly SpacedLabel _code = new("XXXX-XXXX", Theme.Display(26f, bold: true), Theme.GoldPale, .18f);
    readonly GoldButton _copy = new("Copy code");
    readonly GoldButton _open = new("Open " + "perpetua.gg", GoldButton.Kinds.Solid);
    readonly Label _waiting = Words(Theme.Muted, 11.5f, italic: true);
    readonly GoldButton _cancelPair = new("Cancel");
    readonly System.Windows.Forms.Timer _countdown = new() { Interval = 1000 };

    // Home.
    readonly Label _who = Words(Theme.Ok, 13f);
    readonly GoldButton _sync = new("Sync now", GoldButton.Kinds.Solid);
    readonly Label _status = Words(Theme.Muted, 12f, italic: true);

    // Activity.
    readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Theme.Midnight, ForeColor = Theme.Text, Margin = Padding.Empty,
    };

    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)Poll.TotalMilliseconds };

    string AdminUrl => new Uri(_site.Site, "admin/sync").ToString();

    public MainForm(Uri site, bool dev)
    {
        _dev = dev;
        _settings = Settings.Load(dev);
        _site = new SiteClient(site, _version) { Key = _settings.Key };
        _syncer = new Syncer(_site, Path.Combine(Settings.StateFolder, dev ? "state-dev.json" : "state.json"), Log);

        Text = "Perpetua Sync" + (dev ? " (dev)" : "");
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("perpetua.ico")) if (s != null) Icon = new Icon(s);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Serif(11.5f);
        BackColor = Theme.Navy;
        ForeColor = Theme.Text;
        ClientSize = new Size(480, 560);
        MinimumSize = new Size(440, 520);
        StartPosition = FormStartPosition.CenterScreen;

        _stepFolder = BuildStepFolder();
        _stepConnect = BuildStepConnect();
        _home = BuildHome();
        _activity = BuildActivity();
        _settingsView = BuildSettings();

        var root = new Backdrop { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 14) };
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(_host, 0, 0);
        page.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(page);
        Controls.Add(root);

        HandleCreated += (_, _) => { Theme.DarkTitleBar(this); DarkScrollbars(_log); };
        _tabs.Selected += i => Show(i switch { 1 => _activity, 2 => _settingsView, _ => _home });
        _timer.Tick += async (_, _) => await Tick();
        _countdown.Tick += (_, _) => ShowCountdown();
        FormClosing += (_, _) => { _timer.Stop(); _pairing?.Cancel(); };

        _settings.WowFolder ??= WowFolder.Guess();
        SetFolder(_settings.WowFolder, save: false);
        if (_settings.ProtectedKey != null && _site.Key == null)
            Log("The saved connection can't be read on this Windows account. Connect again.");
        Log($"Perpetua Sync {_version} started.");
        ShowStart();
        _timer.Start();
    }

    // ---------- screens ----------

    /// <summary>Where to begin: the first step that isn't done yet, else Home.</summary>
    void ShowStart()
    {
        if (!_flavors.Any(f => f.HasAddon)) Show(_stepFolder);
        else if (_site.Key == null) Show(_stepConnect);
        else { Show(_home); _tabs.Select(0); }
    }

    void Show(Control view)
    {
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(view);
        _host.ResumeLayout();
        _tabs.Visible = view == _home || view == _activity || view == _settingsView;
        UpdateHome();
    }

    Control BuildStepFolder()
    {
        var col = Screen("Step 1 of 2", "Find World of Warcraft");
        var text = Words(Theme.Text, 12.5f);
        text.Text = "Pick the folder WoW Forever is installed in: the \"World of Warcraft\" folder with the _classic_ folders inside. "
                  + "The Perpetua addon needs to be installed there.";
        col.Controls.Add(text);
        col.Controls.Add(FolderRow(_folderStep, _folderInfoStep));
        _next.Margin = new Padding(0, 22, 0, 0);
        _next.Click += (_, _) => ShowStart();
        col.Controls.Add(_next);
        return Centered(col);
    }

    Control BuildStepConnect()
    {
        var col = Screen("Step 2 of 2", "Connect to " + _site.Site.Host);
        _connectText.Text = "An officer approves this PC once, on the site. Get a code, then open the site signed in with your "
                          + "officer Discord account and paste the code there.";
        col.Controls.Add(_connectText);
        _getCode.Margin = new Padding(0, 20, 0, 0);
        _getCode.Click += async (_, _) => await Pair();
        col.Controls.Add(_getCode);

        _code.Margin = new Padding(0, 18, 0, 10);
        _codeBox.Controls.Add(_code);
        var buttons = Row();
        _copy.Margin = new Padding(0, 0, 10, 0);
        _copy.Click += (_, _) => CopyCode();
        _open.Click += (_, _) => OpenAdminPage();
        buttons.Controls.AddRange([_copy, _open]);
        _codeBox.Controls.Add(buttons);
        _waiting.Margin = new Padding(0, 14, 0, 6);
        _codeBox.Controls.Add(_waiting);
        _cancelPair.Click += (_, _) => _pairing?.Cancel();
        _codeBox.Controls.Add(_cancelPair);
        _codeBox.Visible = false;
        col.Controls.Add(_codeBox);

        var back = new LinkLabel
        {
            Text = "Back", AutoSize = true, BackColor = Color.Transparent, LinkColor = Theme.Muted, ActiveLinkColor = Theme.GoldPale,
            Font = Theme.Serif(11.5f, italic: true), UseCompatibleTextRendering = true, Margin = new Padding(0, 18, 0, 0),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        back.LinkClicked += (_, _) => { _pairing?.Cancel(); Show(_stepFolder); };
        col.Controls.Add(back);
        return Centered(col);
    }

    Control BuildHome()
    {
        var col = Column();
        var crest = new Crest { Size = new Size(118, 118), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 16) };
        var title = new SpacedLabel("Perpetua", Theme.Display(24f, bold: true), Theme.Gold, .2f) { Anchor = AnchorStyles.None };
        _who.Anchor = AnchorStyles.None;
        _who.Margin = new Padding(0, 4, 0, 30);
        _sync.Font = Theme.Display(12f);
        _sync.Padding = new Padding(40, 16, 40, 16);
        _sync.Anchor = AnchorStyles.None;
        _sync.Click += (_, _) => { _forceNext = true; _ = Tick(); };
        _status.Anchor = AnchorStyles.None;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Margin = new Padding(0, 14, 0, 0);
        col.Controls.AddRange([crest, title, _who, _sync, _status]);
        if (_dev)
        {
            var dev = Words(Theme.GoldWarm, 11f, italic: true);
            dev.Text = "Test copy: syncs with dev.perpetua.gg";
            dev.Anchor = AnchorStyles.None;
            dev.Margin = new Padding(0, 10, 0, 0);
            col.Controls.Add(dev);
        }
        return Centered(col, center: true);
    }

    Control BuildActivity()
    {
        var box = new Section("Activity") { AutoSize = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        box.Body.AutoSize = false;
        box.Body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _log.Font = Theme.Serif(11.5f);
        box.Body.Controls.Add(_log, 0, 0);
        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 12) };
        wrap.Controls.Add(box);
        return wrap;
    }

    Control BuildSettings()
    {
        var col = Column();
        var wow = new Section("World of Warcraft") { Dock = DockStyle.None, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        wow.Body.Controls.Add(FolderRow(_folderSettings, _folderInfoSettings), 0, 0);
        col.Controls.Add(wow);

        var site = new Section(_site.Site.Host) { Dock = DockStyle.None, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        var text = Words(Theme.Muted, 11.5f);
        text.Text = "Disconnecting revokes this PC's key on the site. Connecting again needs a new approval.";
        var disconnect = new GoldButton("Disconnect", GoldButton.Kinds.Danger) { Margin = new Padding(0, 12, 0, 0) };
        disconnect.Click += async (_, _) => await Disconnect();
        var inner = Column();
        inner.Controls.AddRange([text, disconnect]);
        site.Body.Controls.Add(inner, 0, 0);
        col.Controls.Add(site);

        var about = Words(Theme.Muted, 10.5f, italic: true);
        about.Text = $"Version {_version}. Runs as you, never as administrator. Only connects to {_site.Site.Host}. "
                   + "Reads the addon's Perpetua.lua and writes only its SiteData.lua.";
        col.Controls.Add(about);
        var wrap = Centered(col);
        // Sections fill the width here.
        wrap.Resize += (_, _) => { foreach (Control c in col.Controls) if (c is Section) c.Width = col.ClientSize.Width - 2; };
        return wrap;
    }

    /// <summary>"STEP 1 OF 2" over a heading, as the top of a setup screen.</summary>
    static FlowLayoutPanel Screen(string step, string heading)
    {
        var col = Column();
        var crest = new Crest { Size = new Size(54, 54), Margin = new Padding(0, 0, 0, 14) };
        col.Controls.Add(crest);
        col.Controls.Add(new SpacedLabel(step, Theme.Display(8.5f), Theme.Gold, .16f) { Margin = new Padding(0, 0, 0, 6) });
        var h = Words(Theme.GoldPale, 19f);
        h.Text = heading;
        h.Margin = new Padding(0, 0, 0, 12);
        col.Controls.Add(h);
        return col;
    }

    Control FolderRow(Field field, Label info)
    {
        var col = Column();
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 16, 0, 0), Dock = DockStyle.Top };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Dock = DockStyle.None;
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var choose = new GoldButton("Choose…");
        choose.Click += (_, _) => ChooseFolder();
        row.Controls.Add(field, 0, 0);
        row.Controls.Add(choose, 1, 0);
        info.Margin = new Padding(0, 8, 0, 0);
        col.Controls.Add(row);
        col.Controls.Add(info);
        col.Resize += (_, _) => row.Width = col.ClientSize.Width;
        col.Dock = DockStyle.Top;
        return col;
    }

    // ---------- small layout helpers ----------

    static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Color.Transparent, Margin = Padding.Empty,
    };

    static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Color.Transparent, Margin = Padding.Empty,
    };

    /// <summary>A screen's column, full width (text wraps to it), and vertically centred when center is true.</summary>
    static Control Centered(FlowLayoutPanel col, bool center = false)
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, center ? 50 : 0));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, center ? 50 : 100));
        col.Anchor = center ? AnchorStyles.None : AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        if (!center) col.AutoSize = true;
        host.Controls.Add(col, 0, 1);
        host.Resize += (_, _) =>
        {
            var w = host.ClientSize.Width - 4;
            foreach (Control c in col.Controls)
                if (c is Label { AutoSize: true } l) l.MaximumSize = new Size(Math.Max(100, w), 0);
            if (center) foreach (Control c in col.Controls) c.Margin = new Padding(Math.Max(0, (w - c.Width) / 2), c.Margin.Top, 0, c.Margin.Bottom);
            else col.MinimumSize = new Size(w, 0);
        };
        return host;
    }

    /// <summary>A plain text label in Cormorant (drawn with GDI+, which sees the built-in fonts).</summary>
    static Label Words(Color color, float size, bool italic = false) => new WrapLabel
    {
        ForeColor = color, BackColor = Color.Transparent, Font = Theme.Serif(size, italic), Margin = Padding.Empty,
    };

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    // Windows 10/11 draw a control's scroll bars dark with this theme; elsewhere it's ignored.
    static void DarkScrollbars(Control c)
    {
        try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    void Log(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        var line = $"{DateTime.Now:HH:mm:ss}  {message}\r\n";
        if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..];
        _log.AppendText(line);
    }

    void UpdateHome()
    {
        _who.Text = _site.Key == null ? "Not connected" : _connectedAs != null ? $"Connected as {_connectedAs}" : "Connected";
        _sync.Enabled = _site.Key != null && _flavors.Count > 0 && !_busy;
        _sync.Text = _busy ? "Syncing…" : "Sync now";
        _home.PerformLayout();
        _home.Width++; _home.Width--; // re-centre after text changes
    }

    void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
        UpdateHome();
    }

    // ---------- folder ----------

    void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick your \"World of Warcraft\" folder (the one with the _classic_ folders in it).",
            UseDescriptionForTitle = true,
            InitialDirectory = _settings.WowFolder ?? WowFolder.Guess() ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetFolder(dlg.SelectedPath, save: true);
    }

    void SetFolder(string? path, bool save)
    {
        _flavors = WowFolder.Flavors(path);
        var ok = _flavors.Any(f => f.HasAddon);
        var info = string.IsNullOrEmpty(path) ? "Not found yet: press Choose." : WowFolder.Describe(_flavors);
        foreach (var f in new[] { _folderStep, _folderSettings }) f.Box.Text = path ?? "";
        foreach (var l in new[] { _folderInfoStep, _folderInfoSettings }) { l.Text = info; l.ForeColor = ok ? Theme.Muted : Theme.GoldWarm; }
        _next.Enabled = ok;
        _seen = [];
        if (save)
        {
            _settings.WowFolder = path;
            _settings.Save(_dev);
            if (_flavors.Count > 0) _forceNext = true;
        }
        UpdateHome();
    }

    List<string> SavedFiles() => _flavors.SelectMany(f => f.SavedVariables()).ToList();

    // ---------- connecting ----------

    async Task Disconnect()
    {
        if (MessageBox.Show(this, "Disconnect this PC from the site? Its key is revoked; an officer would need to approve it again.",
                "Perpetua Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { await _site.Send(HttpMethod.Delete, "/api/sync/me"); }
        catch (Exception e) when (e is SiteException or HttpRequestException or TaskCanceledException) { Log($"Couldn't tell the site ({e.Message}); revoke this PC on {AdminUrl} to be sure."); }
        Forget("Disconnected.");
    }

    void Forget(string why)
    {
        _site.Key = null;
        _settings.Key = null;
        _settings.Save(_dev);
        _connectedAs = null;
        Log(why);
        ShowStart();
    }

    // The app makes its key here and sends the site only its hash with the pairing request. An officer pastes the code
    // on the site's /admin/sync page; once they approve, the key works. The key itself is never shown or copied.
    async Task Pair()
    {
        var key = SiteClient.NewKey();
        _pairing = new CancellationTokenSource(PairFor);
        var ct = _pairing.Token;
        _getCode.Visible = false;
        try
        {
            var start = await _site.Send(HttpMethod.Post, "/api/sync/pair",
                new JsonObject { ["hash"] = SiteClient.Hash(key), ["device"] = "Windows · " + Environment.MachineName }, auth: false, ct);
            _code.Text = (string?)start["code"] ?? throw new InvalidDataException("no code");
            _pairUntil = DateTime.UtcNow + PairFor;
            _codeBox.Visible = true;
            ShowCountdown();
            _countdown.Start();
            Log($"Waiting for approval of code {_code.Text} on {AdminUrl}.");

            using var probe = new SiteClient(_site.Site, _version) { Key = key };
            while (true)
            {
                await Task.Delay(PairPoll, ct);
                var st = await probe.Get("/api/sync/pair/status", ct);
                var status = (string?)st["status"];
                if (status == "approved")
                {
                    _site.Key = key;
                    _settings.Key = key;
                    _settings.Save(_dev);
                    _connectedAs = (string?)st["by"];
                    Log($"Connected{(_connectedAs != null ? " as " + _connectedAs : "")}. Syncing now.");
                    _forceNext = true;
                    ShowStart();
                    break;
                }
                if (status != "pending") { Log("The code expired. Get a new one."); break; }
            }
        }
        catch (OperationCanceledException) { Log(DateTime.UtcNow >= _pairUntil && _pairUntil != default ? "The code expired. Get a new one." : "Connecting cancelled."); }
        catch (SiteException e) when (e.Code == "slow_down") { Log("Too many connection attempts from this network. Try again in an hour."); }
        catch (Exception e) when (e is SiteException or HttpRequestException or InvalidDataException)
        {
            Log($"Couldn't connect: {e.Message}");
            MessageBox.Show(this, $"Couldn't reach {_site.Site.Host}: {e.Message}", "Perpetua Sync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _countdown.Stop();
            _codeBox.Visible = false;
            _getCode.Visible = true;
            _getCode.Text = "Get a new code";
            _pairing.Dispose();
            _pairing = null;
        }
    }

    void ShowCountdown()
    {
        var left = _pairUntil - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        _waiting.Text = $"Waiting for approval…  the code works for {left:m\\:ss} more.";
    }

    void CopyCode()
    {
        try
        {
            Clipboard.SetText(_code.Text);
            _copy.Text = "Copied";
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_, _) => { _copy.Text = "Copy code"; t.Dispose(); };
            t.Start();
        }
        catch (System.Runtime.InteropServices.ExternalException) { Log("Couldn't copy (another app is using the clipboard). Type the code instead."); }
    }

    void OpenAdminPage()
    {
        // A fixed address on the guild site, opened in the default browser.
        Process.Start(new ProcessStartInfo(AdminUrl) { UseShellExecute = true });
    }

    // ---------- syncing ----------

    async Task Tick()
    {
        if (_busy || _site.Key == null || _flavors.Count == 0) return;
        var now = DateTime.UtcNow;
        var files = SavedFiles();
        var mtimes = new Dictionary<string, DateTime>();
        foreach (var f in files)
        {
            try { mtimes[f] = File.GetLastWriteTimeUtc(f); } catch (IOException) { }
        }
        var changed = mtimes.Count != _seen.Count || mtimes.Any(p => !_seen.TryGetValue(p.Key, out var t) || t != p.Value);
        var force = _forceNext;
        var uploadDue = force || changed || now - _lastUpload > ResyncEvery || (_retryAt != DateTime.MinValue && now >= _retryAt);
        var downloadDue = uploadDue || now - _lastDownload > Syncer.CalendarEvery;
        if (!uploadDue && !downloadDue) return;
        if (_retryAt != DateTime.MinValue && now < _retryAt && !force) return;

        _busy = true;
        _forceNext = false;
        UpdateHome();
        try
        {
            if (_connectedAs == null)
            {
                try { _connectedAs = (string?)(await _site.Get("/api/sync/me"))["name"]; }
                catch (SiteException e) when (e.Status == HttpStatusCode.NotFound) { }
            }
            if (uploadDue)
            {
                if (changed && !force) await Task.Delay(1000); // let WoW finish writing
                if (files.Count == 0 && force) Log("No Perpetua data yet: it appears after you log out or /reload in game with the addon.");
                await Task.Run(() => _syncer.Upload(files));
                _seen = mtimes;
                _lastUpload = DateTime.UtcNow;
            }
            await Task.Run(() => _syncer.Download(_flavors));
            _lastDownload = DateTime.UtcNow;
            _retryAt = DateTime.MinValue;
            SetStatus($"Last synced at {DateTime.Now:h:mm tt}. Syncs by itself whenever WoW saves.", Theme.Muted);
            if (force) Log("Synced.");
        }
        catch (SiteException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            Forget("The site no longer accepts this PC's key (it was revoked). Connect again.");
        }
        catch (SiteException e) when (e.Status == HttpStatusCode.Forbidden && e.Code == "not_officer")
        {
            Log("The site says your Discord account isn't an officer any more, so syncing is paused. It tries again in 30 seconds.");
            SetStatus("Paused: your Discord account isn't an officer.", Theme.Danger);
            _retryAt = DateTime.UtcNow + RetryAfterError;
        }
        catch (Exception e) when (e is SiteException or HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log($"Sync failed ({e.Message}); trying again in 30 seconds.");
            SetStatus("Sync failed; trying again shortly. See Activity.", Theme.Danger);
            _retryAt = DateTime.UtcNow + RetryAfterError;
        }
        finally
        {
            _busy = false;
            UpdateHome();
        }
    }
}
