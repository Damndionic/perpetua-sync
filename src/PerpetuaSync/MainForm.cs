using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using PerpetuaSync.Core;

namespace PerpetuaSync;

/// <summary>
/// The whole app. The first time, two steps: find World of Warcraft, connect to the site. After that one screen:
/// the crest and a Sync button, with Activity (the log) and Settings (folder, disconnect) as tabs. While it runs it
/// also syncs by itself whenever WoW saves (logout, /reload, exit). The window's X hides it to the system tray, where
/// it keeps syncing (asked once; Settings changes it); Exit, in the tray menu or Settings, stops it. It starts with
/// Windows, in the tray, unless that's turned off in Settings.
/// </summary>
sealed class MainForm : Form
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ResyncEvery = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RetryAfterError = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PairPoll = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PairFor = TimeSpan.FromMinutes(10);
    const string DiscordInvite = "https://discord.gg/B57dYAKaZ2"; // the guild's invite, as on the site (DISCORD_INVITE)

    readonly bool _dev;
    readonly Settings _settings;
    readonly SiteClient _site;
    readonly Syncer _syncer;
    readonly string _version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";

    List<Flavor> _found = [];   // every game in the WoW folder
    Flavor? _pick;              // the one picked on the setup screen (saved on Next)
    List<Flavor> _flavors = []; // what syncs: the saved WoW Forever game, if it has the addon
    Dictionary<string, DateTime> _seen = [];
    DateTime _lastUpload = DateTime.MinValue, _lastDownload = DateTime.MinValue, _retryAt = DateTime.MinValue;
    bool _busy, _forceNext;
    string? _connectedAs;
    CancellationTokenSource? _pairing;
    DateTime _pairUntil;

    // Screens, swapped inside _host.
    readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    readonly Control _stepFolder, _stepConnect, _home, _activity, _settingsView;
    readonly TabStrip _tabs = new(["Home", "Activity", "Settings"]) { Anchor = AnchorStyles.Left };
    readonly Control _bottomBar = new Panel();

    // Step 1 and Settings share these: there's one folder.
    readonly Field _folderStep = new(), _folderSettings = new();
    readonly Label _folderInfoStep = Words(Theme.Muted, 11f), _folderInfoSettings = Words(Theme.Muted, 11f);
    readonly GoldButton _next = new("Next", GoldButton.Kinds.Solid);
    readonly FlowLayoutPanel _pickerStep = Column(), _pickerSettings = Column();

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

    readonly NotifyIcon _tray = new() { Text = "Perpetua Sync" };
    readonly GoldCheck _trayBox = new("Keep syncing in the tray when the window is closed");
    readonly GoldCheck _startBox = new("Start with Windows (in the tray)") { Margin = new Padding(0, 8, 0, 0) };
    bool _exiting, _toldAboutTray, _startHidden;

    static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(6);
    Release? _ready;                       // an update downloaded and checked, waiting to be installed
    DateTime _nextUpdateCheck = DateTime.UtcNow.AddSeconds(15);
    bool _checkingUpdate;
    readonly GoldButton _restart = new("Restart to update", GoldButton.Kinds.Solid) { Visible = false, Margin = new Padding(0, 16, 0, 0) };

    public MainForm(Uri site, bool dev, EventWaitHandle showMe, bool startHidden = false, bool updated = false)
    {
        _dev = dev;
        _settings = Settings.Load(dev);
        _settings.Save(dev); // rewrites the file in the current format (drops the plain-text key 1.0.0 also saved)
        _site = new SiteClient(site, _version) { Key = _settings.Key };
        _syncer = new Syncer(_site, Path.Combine(Settings.StateFolder, dev ? "state-dev.json" : "state.json"), Log);

        Text = "Perpetua Sync" + (dev ? " (dev)" : "");
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("perpetua.ico")) if (s != null) Icon = new Icon(s);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Serif(11.5f);
        BackColor = Theme.Navy;
        ForeColor = Theme.Text;
        // A small utility window, always the size it was designed at (Windows scales it with the display).
        ClientSize = new Size(880, 540);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
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
        page.Controls.Add(BuildBottomBar(), 0, 1);
        root.Controls.Add(page);
        Controls.Add(root);

        HandleCreated += (_, _) => { Theme.DarkTitleBar(this); DarkScrollbars(_log); };
        _tabs.Selected += i => Show(i switch { 1 => _activity, 2 => _settingsView, _ => _home });
        _timer.Tick += async (_, _) => { await Tick(); await CheckForUpdate(); };
        _countdown.Tick += (_, _) => ShowCountdown();
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _tray.Visible = false; _tray.Dispose(); };
        SetUpTray();
        Startup.Apply(_settings.StartWithWindows, _dev);
        // Another launch asks this one to come forward.
        ThreadPool.RegisterWaitForSingleObject(showMe, (_, _) => { if (!IsDisposed) BeginInvoke(ShowWindow); }, null, -1, false);

        _settings.WowFolder ??= WowFolder.Guess();
        SetFolder(_settings.WowFolder, save: false);
        if (_settings.ProtectedKey != null && _site.Key == null)
            Log("The saved connection can't be read on this Windows account. Connect again.");
        Log(updated ? $"Updated to Perpetua Sync {_version}." : $"Perpetua Sync {_version} started.");
        Load += (_, _) =>
        {
            foreach (var v in new[] { _home, _activity, _settingsView, _stepFolder, _stepConnect })
            {
                v.Bounds = _host.ClientRectangle;
                v.PerformLayout();
            }
        };
        ShowStart();
        // Started with Windows: straight to the tray, unless setup isn't finished (then the window is what's needed).
        _startHidden = startHidden && _site.Key != null && _flavors.Count > 0;
        if (_startHidden) _toldAboutTray = true;
        _timer.Start();
    }

    // ---------- screens ----------

    /// <summary>Where to begin: the first step that isn't done yet, else Home.</summary>
    void ShowStart()
    {
        if (_flavors.Count == 0) Show(_stepFolder);
        else if (_site.Key == null) Show(_stepConnect);
        else { Show(_home); _tabs.Select(0); }
    }

    void Show(Control view)
    {
        // Swap screens without showing the in-between: drawing off, the new screen laid out at its final size first,
        // then one repaint.
        Redraw(_host, false);
        try
        {
            view.Bounds = _host.ClientRectangle;
            view.PerformLayout();
            _host.SuspendLayout();
            _host.Controls.Clear();
            _host.Controls.Add(view);
            _host.ResumeLayout(true);
        }
        finally
        {
            Redraw(_host, true);
            _host.Invalidate(true);
        }
        _bottomBar.Visible = view == _home || view == _activity || view == _settingsView;
        UpdateHome();
    }

    Control BuildStepFolder()
    {
        var col = Screen("Step 1 of 2", "Find WoW Forever");
        var text = Words(Theme.Text, 12.5f);
        text.Text = "Pick the \"World of Warcraft\" folder Battle.net installed the game into (usually "
                  + "C:\\Program Files (x86)\\World of Warcraft), then choose which game in it is WoW Forever.";
        col.Controls.Add(text);
        col.Controls.Add(FolderRow(_folderStep, _folderInfoStep));
        _pickerStep.Margin = new Padding(0, 10, 0, 0);
        col.Controls.Add(_pickerStep);
        _next.Margin = new Padding(0, 18, 0, 0);
        _next.Click += (_, _) =>
        {
            if (_pick is not { HasAddon: true }) return;
            SaveGame(_pick);
            ShowStart();
        };
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
        // Side by side, the same size: copy, then open the site and paste.
        _copy.Margin = new Padding(0, 0, 10, 0);
        _open.Margin = Padding.Empty;
        _copy.Click += (_, _) => CopyCode();
        _open.Click += (_, _) => OpenAdminPage();
        PairUp(_copy, _open);
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
        // One centred column: each item in its own row, anchored to nothing, so the table centres it both ways.
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var crest = new Crest { Size = new Size(118, 118), Margin = new Padding(0, 0, 0, 16) };
        var title = new SpacedLabel("Perpetua", Theme.Display(24f, bold: true), Theme.Gold, .2f);
        _who.Margin = new Padding(0, 4, 0, 30);
        _sync.Font = Theme.Display(12f);
        _sync.Padding = new Padding(40, 16, 40, 16);
        _sync.Click += (_, _) => { _forceNext = true; _ = Tick(); };
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Margin = new Padding(0, 14, 0, 0);
        _restart.Click += (_, _) => InstallUpdate();
        var items = new List<Control> { crest, title, _who, _sync, _status, _restart };
        if (_dev)
        {
            var dev = Words(Theme.GoldWarm, 11f, italic: true);
            dev.Text = "Test copy: syncs with dev.perpetua.gg";
            dev.Margin = new Padding(0, 10, 0, 0);
            items.Add(dev);
        }
        t.RowCount = items.Count + 2;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        t.Controls.Add(new Panel { Size = Size.Empty, BackColor = Color.Transparent }, 0, 0);
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Anchor = AnchorStyles.None;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(items[i], 0, i + 1);
        }
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        t.Controls.Add(new Panel { Size = Size.Empty, BackColor = Color.Transparent }, 0, items.Count + 1);
        // Long status lines wrap within the window instead of running off it.
        t.Resize += (_, _) => { foreach (var c in items) if (c is Label l) l.MaximumSize = new Size(Math.Max(100, t.ClientSize.Width - 20), 0); };
        return t;
    }

    /// <summary>Tabs on the left; the site and the guild's Discord in the corner.</summary>
    Control BuildBottomBar()
    {
        var bar = (Panel)_bottomBar;
        bar.Dock = DockStyle.Fill;
        bar.BackColor = Color.Transparent;
        bar.Margin = Padding.Empty;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var links = Row();
        links.Anchor = AnchorStyles.Right;
        var site = new WordLink("perpetua.gg") { Margin = new Padding(0, 6, 12, 0) }; // the dev build opens dev.perpetua.gg
        site.Click += (_, _) => OpenUrl(_site.Site.ToString());
        var discord = new IconLink("discord-gold.png", "discord-pale.png", "Perpetua on Discord") { Size = new Size(24, 24), Margin = new Padding(0, 8, 2, 0) };
        discord.Click += (_, _) => OpenUrl(DiscordInvite);
        links.Controls.AddRange([site, discord]);
        _tabs.Margin = new Padding(0, 6, 0, 0);
        t.Controls.Add(_tabs, 0, 0);
        t.Controls.Add(links, 2, 0);
        bar.Height = Math.Max(_tabs.Height, 34) + 6;
        bar.Controls.Add(t);
        return bar;
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
        // Two columns: the game on the left (its list can be long), the site and background settings stacked on the
        // right, the version note under both. Scrolls if a WoW folder has a lot of games in it.
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 6), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var wow = new Section("World of Warcraft") { Margin = new Padding(0, 0, 7, 14) };
        var wowCol = Column();
        wowCol.Controls.Add(FolderRow(_folderSettings, _folderInfoSettings));
        _pickerSettings.Margin = new Padding(0, 8, 0, 0);
        wowCol.Controls.Add(_pickerSettings);
        wow.Body.Controls.Add(wowCol, 0, 0);
        grid.Controls.Add(wow, 0, 0);
        grid.SetRowSpan(wow, 2);

        var site = new Section(_site.Site.Host) { Margin = new Padding(7, 0, 0, 14) };
        var text = Words(Theme.Muted, 11.5f);
        text.Text = "Disconnecting revokes this PC's key on the site. Connecting again needs a new approval.";
        var disconnect = new GoldButton("Disconnect", GoldButton.Kinds.Danger) { Margin = new Padding(0, 12, 0, 0) };
        disconnect.Click += async (_, _) => await Disconnect();
        var inner = Column();
        inner.Controls.AddRange([text, disconnect]);
        site.Body.Controls.Add(inner, 0, 0);
        grid.Controls.Add(site, 1, 0);

        var background = new Section("While it runs") { Margin = new Padding(7, 0, 0, 14) };
        _trayBox.CheckedChanged += (_, _) => { _settings.CloseToTray = _trayBox.Checked; _settings.Save(_dev); };
        _startBox.CheckedChanged += (_, _) =>
        {
            _settings.StartWithWindows = _startBox.Checked;
            _settings.Save(_dev);
            Startup.Apply(_startBox.Checked, _dev);
        };
        _trayBox.Checked = _settings.CloseToTray != false;
        _startBox.Checked = _settings.StartWithWindows;
        var exit = new GoldButton("Exit Perpetua Sync") { Margin = new Padding(0, 12, 0, 0) };
        exit.Click += (_, _) => ExitApp();
        var bg = Column();
        bg.Controls.AddRange([_trayBox, _startBox, exit]);
        background.Body.Controls.Add(bg, 0, 0);
        grid.Controls.Add(background, 1, 1);

        var about = Words(Theme.Muted, 10.5f, italic: true);
        about.Text = $"Version {_version}. Runs as you, never as administrator. Only connects to {_site.Site.Host}. "
                   + "Reads the addon's Perpetua.lua and writes only its SiteData.lua.";
        about.Margin = new Padding(2, 0, 0, 0);
        grid.Controls.Add(about, 0, 2);
        grid.SetColumnSpan(about, 2);
        grid.Resize += (_, _) => about.MaximumSize = new Size(Math.Max(100, grid.ClientSize.Width - 4), 0);
        return grid;
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
        // Not AutoSize: the section (or the step screen) sets its width, and the field takes what the button leaves.
        var row = new TableLayoutPanel { ColumnCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 16, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Dock = DockStyle.None;
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var choose = new GoldButton("Choose…");
        choose.Click += (_, _) => ChooseFolder();
        row.Controls.Add(field, 0, 0);
        row.Controls.Add(choose, 1, 0);
        row.Height = Math.Max(field.Height, choose.Height) + 6;
        row.Width = 400;
        info.Margin = new Padding(0, 8, 0, 0);
        col.Controls.Add(row);
        col.Controls.Add(info);
        col.Dock = DockStyle.Top;
        return col;
    }

    // ---------- tray and closing ----------

    void SetUpTray()
    {
        _tray.Icon = Icon;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new TrayColors()), ShowImageMargin = false };
        menu.Font = Theme.Serif(11.5f);
        var open = new ToolStripMenuItem("Open Perpetua Sync") { ForeColor = Theme.Text, Font = Theme.Serif(11.5f) };
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => ShowWindow();
        var sync = new ToolStripMenuItem("Sync now") { ForeColor = Theme.Text };
        sync.Click += (_, _) => { _forceNext = true; _ = Tick(); };
        var exit = new ToolStripMenuItem("Exit") { ForeColor = Theme.Text };
        exit.Click += (_, _) => ExitApp();
        menu.Items.AddRange([open, sync, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => sync.Enabled = _site.Key != null && _flavors.Count > 0 && !_busy;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        _tray.Visible = true;
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            // The first Show() from Application.Run: make the window (so the timer and tray work) but keep it hidden.
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        // Windows shutting down, Task Manager, or Exit: really close.
        if (_exiting || e.CloseReason != CloseReason.UserClosing)
        {
            _timer.Stop();
            _pairing?.Cancel();
            return;
        }
        if (_settings.CloseToTray == null)
        {
            using var ask = new ClosePrompt(Icon);
            var choice = ask.ShowDialog(this);
            if (choice == DialogResult.Cancel) { e.Cancel = true; return; }
            _settings.CloseToTray = choice == DialogResult.Yes;
            _settings.Save(_dev);
            _trayBox.Checked = _settings.CloseToTray.Value;
        }
        if (_settings.CloseToTray == false)
        {
            _exiting = true;
            _timer.Stop();
            _pairing?.Cancel();
            return;
        }
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray.ShowBalloonTip(4000, "Perpetua Sync is still syncing", "It's in the system tray. Right-click the icon to sync or exit.", ToolTipIcon.None);
        }
    }

    /// <summary>A tray notice, only while the window is hidden (in the window the Activity tab says it).</summary>
    void Notify(string title, string text)
    {
        if (!Visible) _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);
    }

    sealed class TrayColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Midnight;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Line;
        public override Color MenuItemSelected => Theme.Royal;
        public override Color MenuItemSelectedGradientBegin => Theme.Royal;
        public override Color MenuItemSelectedGradientEnd => Theme.Royal;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Midnight;
        public override Color ImageMarginGradientBegin => Theme.Midnight;
        public override Color ImageMarginGradientMiddle => Theme.Midnight;
        public override Color ImageMarginGradientEnd => Theme.Midnight;
    }

    // The whole window is composed off-screen, so see-through controls over the gradient appear in one piece.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    static void Redraw(Control c, bool on)
    {
        if (c.IsHandleCreated) SendMessage(c.Handle, 0x000B /* WM_SETREDRAW */, on ? 1 : 0, IntPtr.Zero);
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
            Description = "Pick the \"World of Warcraft\" folder that Battle.net installed WoW Forever into.",
            UseDescriptionForTitle = true,
            InitialDirectory = _settings.WowFolder ?? WowFolder.Guess() ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetFolder(dlg.SelectedPath, save: true);
    }

    void SetFolder(string? path, bool save)
    {
        _found = WowFolder.Flavors(path);
        if (save)
        {
            _settings.WowFolder = path;
            _settings.Save(_dev);
        }
        // The saved game if it's in this folder; otherwise a suggestion (only saved when the person presses Next).
        _pick = _found.FirstOrDefault(f => f.Name == _settings.Game) ?? WowFolder.Suggest(_found);
        string info;
        if (string.IsNullOrEmpty(path)) info = "Not found yet: press Choose.";
        else if (_found.Count == 0) info = "No WoW games in this folder. Pick the \"World of Warcraft\" folder itself.";
        else info = "Which one is WoW Forever?";
        foreach (var f in new[] { _folderStep, _folderSettings }) f.Box.Text = path ?? "";
        foreach (var l in new[] { _folderInfoStep, _folderInfoSettings })
        {
            l.Text = info;
            l.ForeColor = _found.Count > 0 ? Theme.GoldPale : Theme.GoldWarm;
        }
        ApplyGame();
        if (save && _flavors.Count > 0) _forceNext = true;
    }

    void SaveGame(Flavor f)
    {
        _settings.Game = f.Name;
        _settings.Save(_dev);
        _pick = f;
        ApplyGame();
        _forceNext = true;
        Log($"Syncing {WowFolder.Friendly(f.Name)} ({f.Name}) as WoW Forever.");
    }

    /// <summary>What syncs follows the saved game; both pickers show the choices.</summary>
    void ApplyGame()
    {
        var saved = _found.FirstOrDefault(f => f.Name == _settings.Game);
        _flavors = saved is { HasAddon: true } ? [saved] : [];
        _seen = [];
        FillPicker(_pickerStep, inSettings: false);
        FillPicker(_pickerSettings, inSettings: true);
        _next.Enabled = _pick is { HasAddon: true };
        UpdateHome();
    }

    // One round button per game in the folder. Games without the Perpetua addon are shown but can't be picked.
    void FillPicker(FlowLayoutPanel host, bool inSettings)
    {
        host.SuspendLayout();
        foreach (Control c in host.Controls) c.Dispose();
        host.Controls.Clear();
        var selected = inSettings ? _found.FirstOrDefault(f => f.Name == _settings.Game) : _pick;
        foreach (var f in _found)
        {
            var option = new GoldCheck($"{WowFolder.Friendly(f.Name)}  ·  {WowFolder.Detail(f)}")
            {
                Radio = true, Enabled = f.HasAddon, Margin = new Padding(0, 6, 0, 0), Tag = f,
            };
            option.Checked = f == selected;
            option.CheckedChanged += (_, _) =>
            {
                if (!option.Checked) return;
                foreach (Control other in host.Controls) if (other != option && other is GoldCheck g) g.Checked = false;
                if (inSettings) BeginInvoke(() => SaveGame(f)); // after this click: SaveGame rebuilds the list
                else { _pick = f; _next.Enabled = f.HasAddon; }
            };
            host.Controls.Add(option);
        }
        if (inSettings && _found.Count > 0 && _flavors.Count == 0)
        {
            var warn = Words(Theme.GoldWarm, 11.5f, italic: true);
            warn.Text = "Pick which game is WoW Forever: nothing syncs until you do.";
            warn.Margin = new Padding(0, 6, 0, 0);
            host.Controls.Add(warn);
        }
        host.ResumeLayout(true);
        for (var c = host.Parent; c != null; c = c.Parent) if (c is Section sec) { sec.Rewrap(); break; }
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

    /// <summary>Makes two buttons the width of the wider one (Fit() sizes each to its own text).</summary>
    static void PairUp(GoldButton a, GoldButton b)
    {
        var w = Math.Max(a.Width, b.Width);
        a.Width = b.Width = w;
    }

    void CopyCode()
    {
        try
        {
            Clipboard.SetText(_code.Text);
            _copy.Text = "Copied";
            PairUp(_copy, _open);
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_, _) => { _copy.Text = "Copy code"; PairUp(_copy, _open); t.Dispose(); };
            t.Start();
        }
        catch (System.Runtime.InteropServices.ExternalException) { Log("Couldn't copy (another app is using the clipboard). Type the code instead."); }
    }

    void OpenAdminPage() => OpenUrl(AdminUrl);

    // Only ever fixed addresses (the guild site and its Discord invite), opened in the default browser.
    static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ---------- updates ----------

    Version Current => typeof(MainForm).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>Every few hours: is there a newer version? If so, download and check it (Core/Updates.cs). In the tray
    /// it installs straight away; with the window open it waits for "Restart to update".</summary>
    async Task CheckForUpdate()
    {
        if (_ready != null && !Visible) { InstallUpdate(); return; } // waiting since the window was open; it's in the tray now
        if (_checkingUpdate || DateTime.UtcNow < _nextUpdateCheck || !Installer.RunningInstalled) return;
        _checkingUpdate = true;
        _nextUpdateCheck = DateTime.UtcNow + UpdateEvery;
        try
        {
            var release = await Updates.Check(_site, Current);
            if (release == null) return;
            if (_ready?.Version != release.Version)
            {
                Log($"Downloading Perpetua Sync {release.Version}…");
                await Updates.Download(release, Installer.Pending, $"PerpetuaSync-Windows/{_version}");
                _ready = release;
                Log($"Perpetua Sync {release.Version} is downloaded and checked.");
            }
            _restart.Text = $"Restart to update to {release.Version}";
            _restart.Visible = true;
            UpdateHome();
            if (!Visible) InstallUpdate(); // in the tray: nobody to interrupt
        }
        catch (Exception e) when (e is SiteException or HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log($"Update check failed ({e.Message}); trying again later.");
            _nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        }
        finally { _checkingUpdate = false; }
    }

    void InstallUpdate()
    {
        if (_ready == null || _busy || _pairing != null) return; // not mid-sync or mid-connect; the next tick tries again
        try
        {
            var args = new List<string>();
            if (!Visible) args.Add("--tray");
            if (_dev) args.Add("--dev");
            Installer.ApplyAndRestart(args);
            _exiting = true;
            Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log($"Couldn't install the update ({e.Message}). It will try again later.");
            _ready = null;
            _restart.Visible = false;
        }
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
            _tray.Text = $"Perpetua Sync: last synced {DateTime.Now:h:mm tt}";
            if (force) Log("Synced.");
        }
        catch (SiteException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            Forget("The site no longer accepts this PC's key (it was revoked). Connect again.");
            Notify("Perpetua Sync is disconnected", "The site no longer accepts this PC. Open the app to connect again.");
        }
        catch (SiteException e) when (e.Status == HttpStatusCode.Forbidden && e.Code == "not_officer")
        {
            Log("The site says your Discord account isn't an officer any more, so syncing is paused. It tries again in 30 seconds.");
            SetStatus("Paused: your Discord account isn't an officer.", Theme.Danger);
            _tray.Text = "Perpetua Sync: paused (not an officer)";
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
