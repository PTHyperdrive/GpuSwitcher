using System.Diagnostics;
using Microsoft.Win32;

namespace GpuSwitcher.App;

sealed class MainForm : Form
{
    readonly RoutingEngine _engine;
    readonly bool _startHidden;
    bool _exiting, _hintShown;
    int _refreshQueued;

    // chrome
    readonly NotifyIcon _tray = new();
    readonly Label _status = new() { AutoSize = true };
    readonly CheckBox _autoToggle = new() { Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.System };
    readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Top, Height = 186, Padding = new Padding(16, 10, 16, 8), WrapContents = false, AutoScroll = true };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    readonly ToolStripStatusLabel _footer = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    // apps tab
    readonly SmoothListView _apps = new();
    readonly CheckBox _showIdle = new() { Text = "Show idle && system processes", AutoSize = true };
    readonly TextBox _filter = new() { PlaceholderText = "Filter…", Width = 200 };
    readonly Dictionary<string, ListViewItem> _appItems = new(StringComparer.OrdinalIgnoreCase);
    int _sortColumn = -1;
    bool _sortDesc;

    // rules tab
    readonly SmoothListView _rules = new();

    // libraries tab
    readonly SmoothListView _roots = new();
    readonly TextBox _mediaExes = MultiLine(), _gameExes = MultiLine(), _ignoreExes = MultiLine(), _noInjectExes = MultiLine();

    // settings tab
    readonly Dictionary<Role, ComboBox> _roleCombos = new();
    readonly ComboBox _appHook = Combo(), _mediaHook = Combo();
    readonly CheckBox _routeUnknown = new() { Text = "Route new apps the moment they launch (before they pick a GPU)", AutoSize = true };
    readonly CheckBox _notify = new() { Text = "Tell me when running apps need a restart to move", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start with Windows (in the tray)", AutoSize = true };
    bool _loadingSettings;

    // log tab
    readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = new Font("Cascadia Mono", 9f) };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainForm(RoutingEngine engine, bool startHidden)
    {
        _engine = engine;
        _startHidden = startHidden;
        Text = $"GPU Switcher {AppInfo.Version}";
        Icon = Theme.MakeIcon(32);
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(980, 640);
        Size = new Size(1240, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;

        Controls.Add(_tabs);
        Controls.Add(_cards);
        Controls.Add(BuildHeader());
        var strip = new StatusStrip { SizingGrip = true };
        strip.Items.Add(_footer);
        Controls.Add(strip);

        _tabs.TabPages.Add(BuildAppsTab());
        _tabs.TabPages.Add(BuildRulesTab());
        _tabs.TabPages.Add(BuildLibrariesTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        _tabs.TabPages.Add(BuildLogTab());
        _tabs.SelectedIndexChanged += (_, _) => RefreshCurrentTab();

        BuildTray();

        _engine.Updated += QueueRefresh;
        _engine.Logged += e => { if (IsHandleCreated) BeginInvoke(() => AppendLog(e)); };
        _engine.Notify += (title, msg) => { if (IsHandleCreated) BeginInvoke(() => _tray.ShowBalloonTip(6000, title, msg, ToolTipIcon.Info)); };

        foreach (var e in _engine.LogEntries) _log.Items.Add(e.ToString());
        VisibleChanged += (_, _) => _engine.SetUiVisible(Visible);
        _engine.SetUiVisible(!startHidden);   // started in the tray: VisibleChanged never fires for that
        LoadSettings();
        RefreshAll();
    }

    // ================================================================= layout

    Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(16, 10, 16, 0) };
        var title = new Label { Text = "GPU Switcher", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Location = new Point(16, 8) };
        _status.Location = new Point(18, 38);
        _status.ForeColor = Theme.Muted;
        _autoToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _autoToggle.CheckedChanged += (_, _) =>
        {
            if (_loadingSettings) return;
            _engine.UpdateConfig(c => c.AutoRoute = _autoToggle.Checked);
            UpdateAutoToggle();
        };
        header.Controls.Add(title);
        header.Controls.Add(_status);
        header.Controls.Add(_autoToggle);
        header.Resize += (_, _) => _autoToggle.Location = new Point(header.Width - _autoToggle.Width - 18, 18);
        return header;
    }

    TabPage BuildAppsTab()
    {
        var page = new TabPage("Apps") { Padding = new Padding(10), UseVisualStyleBackColor = false, BackColor = Theme.Surface };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        var restart = new Button { Text = "Restart selected", AutoSize = true };
        var restartAll = new Button { Text = "Restart all that need it", AutoSize = true };
        restart.Click += (_, _) => RestartSelected(force: false);
        restartAll.Click += (_, _) => RestartAllNeeded();
        _showIdle.CheckedChanged += (_, _) => RefreshApps();
        _filter.TextChanged += (_, _) => RefreshApps();
        foreach (Control c in new Control[] { _filter, _showIdle, restart, restartAll }) { c.Margin = new Padding(0, 4, 10, 0); bar.Controls.Add(c); }

        _apps.Dock = DockStyle.Fill;
        _apps.Columns.Add("App", 190);
        _apps.Columns.Add("Role", 70);
        _apps.Columns.Add("Assigned GPU", 140);
        _apps.Columns.Add("Running on", 140);
        _apps.Columns.Add("GPU", 55, HorizontalAlignment.Right);
        _apps.Columns.Add("Video", 75, HorizontalAlignment.Right);
        _apps.Columns.Add("Memory", 75, HorizontalAlignment.Right);
        _apps.Columns.Add("Status", 220);
        _apps.Columns.Add("Hook", 90);
        _apps.Columns.Add("Why", 120);
        _apps.Columns.Add("Path", 400);
        _apps.ColumnClick += (_, e) => { _sortDesc = _sortColumn == e.Column && !_sortDesc; _sortColumn = e.Column; SortApps(); };
        _apps.ContextMenuStrip = AppMenu();
        _apps.DoubleClick += (_, _) => { if (Selected() is { } a && a.ExePath != null) Reveal(a.ExePath); };

        page.Controls.Add(_apps);
        page.Controls.Add(bar);
        return page;
    }

    ContextMenuStrip AppMenu()
    {
        var m = new ContextMenuStrip();
        void Add(string text, Action<AppView> act) => m.Items.Add(text, null, (_, _) => { if (Selected() is { } a) act(a); });
        Add("Use the Games GPU", a => Rule(a.Target, Role.Game));
        Add("Use the Apps GPU", a => Rule(a.Target, Role.App));
        Add("Use the Media GPU (encode/decode)", a => Rule(a.Target, Role.Media));
        Add("Let Windows decide (ignore)", a => Rule(a.Target, Role.Ignore));
        Add("Remove my rule (auto-classify)", a => Rule(a.Target, null));
        m.Items.Add(new ToolStripSeparator());
        Add("Restart to apply", _ => RestartSelected(false));
        Add("Force restart (kill)", _ => RestartSelected(true));
        m.Items.Add(new ToolStripSeparator());
        Add("Never inject the hook into this app", a =>
        {
            var name = Path.GetFileName(a.ExePath ?? a.Target);
            _engine.UpdateConfig(c => { if (!c.NoInjectExes.Contains(name, StringComparer.OrdinalIgnoreCase)) c.NoInjectExes.Add(name); });
            LoadSettings();
            Footer($"{name} will keep registry routing but never get the hook.");
        });
        Add("Open file location", a => { if (a.ExePath != null) Reveal(a.ExePath); });
        Add("Copy path", a => Clipboard.SetText(a.ExePath ?? a.Target));
        m.Opening += (_, e) => e.Cancel = Selected() == null;
        return m;
    }

    TabPage BuildRulesTab()
    {
        var page = new TabPage("Rules") { Padding = new Padding(10), UseVisualStyleBackColor = false, BackColor = Theme.Surface };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true, Margin = new Padding(0, 4, 10, 0) }; b.Click += (_, _) => a(); bar.Controls.Add(b); return b; }
        B("Take over selected", () => { foreach (var t in SelectedRules()) Footer(_engine.TakeOver(t)); RefreshRules(); });
        B("Remove routing", () =>
        {
            var sel = SelectedRules();
            if (sel.Count == 0 || !Confirm($"Remove GPU routing for {sel.Count} entr{(sel.Count == 1 ? "y" : "ies")}?")) return;
            foreach (var t in sel) _engine.RemoveEntry(t);
            RefreshRules();
        });
        B("Fix stale entries", () =>
        {
            var stale = _engine.GetRules().Where(r => r.Stale).Select(r => r.Target).ToList();
            foreach (var t in stale) _engine.TakeOver(t);
            Footer($"Re-routed {stale.Count} entries that pointed at a GPU that is no longer installed.");
            RefreshRules();
        });
        B("Remove everything GpuSwitcher wrote", () =>
        {
            if (!Confirm("Remove every GPU preference GpuSwitcher created? Entries you made in Windows Settings are kept.")) return;
            Footer($"Removed {_engine.ReleaseAll()} entries.");
            RefreshRules();
        });
        B("Back up now", () => Footer("Saved " + _engine.Backup()));
        B("Open backups", () => { Directory.CreateDirectory(ConfigStore.BackupDir); Process.Start("explorer.exe", ConfigStore.BackupDir); });

        _rules.Dock = DockStyle.Fill;
        _rules.Columns.Add("App", 330);
        _rules.Columns.Add("Routes to", 150);
        _rules.Columns.Add("Made by", 150);
        _rules.Columns.Add("Role", 70);
        _rules.Columns.Add("State", 110);
        _rules.Columns.Add("Raw value", 420);
        page.Controls.Add(_rules);
        page.Controls.Add(bar);
        return page;
    }

    TabPage BuildLibrariesTab()
    {
        var page = new TabPage("Libraries & lists") { Padding = new Padding(10), UseVisualStyleBackColor = false, BackColor = Theme.Surface };
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0) };
        var leftBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        var addFolder = new Button { Text = "Add game folder…", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        var removeFolder = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        var preGames = new Button { Text = "Pre-route all games now", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        var preStart = new Button { Text = "Pre-route Start-menu apps", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        addFolder.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Everything launched from this folder is treated as a game" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _engine.UpdateConfig(c => { if (!c.ExtraGameFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) c.ExtraGameFolders.Add(dlg.SelectedPath); });
            RefreshRoots();
        };
        removeFolder.Click += (_, _) =>
        {
            foreach (ListViewItem it in _roots.SelectedItems)
                if (it.Tag is GameRoot { Source: "user" } r) _engine.UpdateConfig(c => c.ExtraGameFolders.RemoveAll(f => f.Equals(r.Path, StringComparison.OrdinalIgnoreCase)));
            RefreshRoots();
        };
        preGames.Click += async (_, _) =>
        {
            Footer("Scanning game libraries…");
            var (n, s) = await Task.Run(() => _engine.PreRouteGames(false));
            Footer($"Pre-routed {n} game executables to {_engine.Roles[Role.Game].Adapter?.ShortName} ({s} already set or skipped).");
        };
        preStart.Click += async (_, _) =>
        {
            if (!Confirm("Add a GPU rule for every app in your Start menu and on your desktop now (instead of as they launch)?")) return;
            Footer("Reading shortcuts…");
            var (n, s) = await Task.Run(() => _engine.PreRouteStartMenu(false));
            Footer($"Pre-routed {n} apps ({s} skipped).");
        };
        leftBar.Controls.AddRange(new Control[] { addFolder, removeFolder, preGames, preStart });
        _roots.Dock = DockStyle.Fill;
        _roots.Columns.Add("Game library", 380);
        _roots.Columns.Add("Found via", 100);
        left.Controls.Add(_roots);
        left.Controls.Add(leftBar);
        left.Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = "Anything started from these folders is a game: Games GPU, never injected.", ForeColor = Theme.Muted });

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        lists.Controls.Add(Group("Media apps → Media GPU (decode/encode)", _mediaExes), 0, 0);
        lists.Controls.Add(Group("Also games (overlays, capture)", _gameExes), 1, 0);
        lists.Controls.Add(Group("Ignore (Windows decides)", _ignoreExes), 0, 1);
        lists.Controls.Add(Group("Route, but never inject the hook", _noInjectExes), 1, 1);
        var save = new Button { Text = "Save lists", AutoSize = true, Anchor = AnchorStyles.Right };
        save.Click += (_, _) =>
        {
            static List<string> L(TextBox t) => t.Lines.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _engine.UpdateConfig(c => { c.MediaExes = L(_mediaExes); c.GameExes = L(_gameExes); c.IgnoreExes = L(_ignoreExes); c.NoInjectExes = L(_noInjectExes); });
            Footer("Lists saved; existing GpuSwitcher entries were re-checked.");
        };
        lists.Controls.Add(save, 1, 2);

        split.Controls.Add(left, 0, 0);
        split.Controls.Add(lists, 1, 0);
        page.Controls.Add(split);
        return page;
    }

    TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings") { Padding = new Padding(16), UseVisualStyleBackColor = false, BackColor = Theme.Surface, AutoScroll = true };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c, string hint)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 4), Font = new Font(Font, FontStyle.Bold) });
            c.Margin = new Padding(0, 4, 12, 4);
            grid.Controls.Add(c);
            grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 8, 0, 4), MaximumSize = new Size(560, 0) });
        }

        foreach (var role in new[] { Role.Game, Role.App, Role.Media })
        {
            var cb = Combo();
            cb.Width = 300;
            cb.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings || cb.SelectedItem is not Choice ch) return;
                _engine.SetRoleGpu(role, ch.Value);
                RefreshAll();
            };
            _roleCombos[role] = cb;
            Row($"{Theme.RoleLabel(role)} GPU", cb, role switch
            {
                Role.Game => "Games, launchers and capture tools (OBS must share the game's GPU).",
                Role.App => "Browsers, chat, Electron apps, everything else that draws.",
                _ => "Video players and transcoders. Pick the Intel iGPU to use Quick Sync for decode/encode.",
            });
        }

        foreach (var (cb, role) in new[] { (_appHook, Role.App), (_mediaHook, Role.Media) })
        {
            cb.Width = 300;
            cb.Items.AddRange(new object[] { new Choice("Off — registry routing only", "Off"), new Choice("Reorder — assigned GPU first", "Reorder"), new Choice("Exclusive — hide other GPUs", "Exclusive") });
            cb.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings || cb.SelectedItem is not Choice ch) return;
                var mode = Enum.Parse<InjectMode>(ch.Value);
                _engine.UpdateConfig(c => { if (role == Role.App) c.AppInjection = mode; else c.MediaInjection = mode; });
            };
            Row($"Hook for {Theme.RoleLabel(role)}", cb, "Injected at launch into apps that use a GPU, so they can't pick a different one themselves. Never injected into games or anything in a game library.");
        }

        var checks = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 14, 0, 0) };
        _routeUnknown.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.UpdateConfig(c => c.RouteUnknownAtLaunch = _routeUnknown.Checked); };
        _notify.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.UpdateConfig(c => c.NotifyRestart = _notify.Checked); };
        _startup.CheckedChanged += (_, _) => { if (!_loadingSettings) SetStartup(_startup.Checked); };
        checks.Controls.AddRange(new Control[] { _routeUnknown, _notify, _startup });

        var notes = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 16, 0, 0), MaximumSize = new Size(1000, 0),
            Text = "How it works: GpuSwitcher writes the same per-app GPU preference Windows Settings ▸ Graphics uses, " +
                   "so D3D10/11/12 and Vulkan apps pick the assigned GPU when they start. Apps that were already running move after a restart. " +
                   "OpenGL and D3D9 apps always render on the GPU that drives their monitor; to move one, put its window on a monitor plugged into the GPU you want. " +
                   "A GPU with no monitor still renders fine; frames are copied to the display GPU, just as on a laptop.\n\n" +
                   $"Config: {ConfigStore.ConfigPath}",
        };
        var openFolder = new Button { Text = "Open config folder", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        openFolder.Click += (_, _) => { Directory.CreateDirectory(ConfigStore.Home); Process.Start("explorer.exe", ConfigStore.Home); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(openFolder);

        // Boot guard: the SYSTEM service that routes from boot, before anything else starts.
        var guard = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 16, 0, 0), WrapContents = false };
        _serviceButton.Click += (_, _) => ToggleService();
        guard.Controls.Add(new Label { Text = "Boot guard", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 12, 0) });
        guard.Controls.Add(_serviceState);
        guard.Controls.Add(_serviceButton);

        page.Controls.Add(buttons);
        page.Controls.Add(notes);
        page.Controls.Add(guard);
        page.Controls.Add(checks);
        page.Controls.Add(grid);
        return page;
    }

    readonly Label _serviceState = new() { AutoSize = true, Margin = new Padding(0, 6, 12, 0), MaximumSize = new Size(640, 0) };
    readonly Button _serviceButton = new() { AutoSize = true };

    void RefreshServiceState()
    {
        var st = BootGuardService.Query();
        _serviceState.Text = st switch
        {
            BootGuardService.State.Running => "Running: starts at boot, routes and hooks every launch the instant it happens, opens this app at logon.",
            BootGuardService.State.NotInstalled => "Not installed: routing only works while this app runs, and launches are found by polling.",
            _ => $"{st}.",
        };
        _serviceState.ForeColor = st == BootGuardService.State.Running ? Theme.Good : Theme.Warn;
        _serviceButton.Text = st == BootGuardService.State.NotInstalled ? "Install (admin)…" : st == BootGuardService.State.Running ? "Remove…" : "Start / repair (admin)…";
    }

    /// <summary>Install/remove goes through gpusw.exe elevated: one UAC prompt, this app stays non-elevated.</summary>
    async void ToggleService()
    {
        var st = BootGuardService.Query();
        string verb = st == BootGuardService.State.Running ? "uninstall" : "install";
        if (verb == "uninstall" && !Confirm("Remove the boot-guard service? Launches will only be routed while this app is running.")) return;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "gpusw.exe"), $"service {verb}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            Footer(p.ExitCode == 0 ? $"Boot guard {(verb == "install" ? "installed and running" : "removed")}." : $"gpusw service {verb} failed (exit {p.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception) { Footer("Cancelled at the administrator prompt."); }
        RefreshServiceState();
        if (_startup.Checked) SetStartup(true);   // move the autostart to whichever mechanism is now available
    }

    TabPage BuildLogTab()
    {
        var page = new TabPage("Log") { Padding = new Padding(10), UseVisualStyleBackColor = false, BackColor = Theme.Surface };
        page.Controls.Add(_log);
        return page;
    }

    void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "GPU Switcher";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open GPU Switcher", null, (_, _) => ShowFromTray());
        var auto = new ToolStripMenuItem("Auto-routing") { CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { if (_autoToggle.Checked != auto.Checked) _autoToggle.Checked = auto.Checked; };
        menu.Opening += (_, _) => auto.Checked = _engine.Config.AutoRoute;
        menu.Items.Add(auto);
        menu.Items.Add("Pre-route all games", null, async (_, _) =>
        {
            var (n, _) = await Task.Run(() => _engine.PreRouteGames(false));
            _tray.ShowBalloonTip(4000, "Games routed", $"{n} game executables now use {_engine.Roles[Role.Game].Adapter?.ShortName}.", ToolTipIcon.Info);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ================================================================= window behavior

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X / Alt+F4 arrive as UserClosing, a plain WM_CLOSE from another program as None: both just hide.
        // Exit from the tray menu, log-off and shutdown really close.
        if (!_exiting && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray.ShowBalloonTip(3000, "Still routing", "GPU Switcher keeps running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
            }
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        RefreshAll();
    }

    // ================================================================= refresh

    void QueueRefresh()
    {
        if (!IsHandleCreated || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        BeginInvoke(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            if (Visible) { RefreshCards(); RefreshCurrentTab(); }
            UpdateTrayText();
        });
    }

    void RefreshAll()
    {
        RefreshCards();
        RefreshApps();
        RefreshRules();
        RefreshRoots();
        UpdateAutoToggle();
    }

    void RefreshCurrentTab()
    {
        switch (_tabs.SelectedIndex)
        {
            case 0: RefreshApps(); break;
            case 1: RefreshRules(); break;
            case 2: RefreshRoots(); break;
            case 3: RefreshServiceState(); break;
        }
    }

    void RefreshCards()
    {
        var adapters = _engine.Adapters.Where(a => a.IsRoutable).ToList();
        var roles = _engine.Roles;
        var snap = _engine.Snapshot;
        var apps = _engine.GetApps(false);
        var owned = _engine.Config.Owned;

        while (_cards.Controls.Count > adapters.Count) _cards.Controls[^1].Dispose();
        while (_cards.Controls.Count < adapters.Count) _cards.Controls.Add(new GpuCard());
        for (int i = 0; i < adapters.Count; i++)
        {
            var a = adapters[i];
            var card = (GpuCard)_cards.Controls[i];
            card.Size = new Size((int)(300 * DeviceDpi / 96f), (int)(166 * DeviceDpi / 96f));
            AdapterUsage? usage = null;
            snap?.ByAdapter.TryGetValue(a.Luid, out usage);
            var myRoles = roles.Values.Where(r => r.Adapter?.Luid == a.Luid).Select(r => r.Role);
            int on = apps.Count(x => x.ActualAdapter?.Luid == a.Luid && x.Role != Role.Ignore);
            int routed = owned.Values.Count(o => o.AdapterKey.Equals(a.Key, StringComparison.OrdinalIgnoreCase));
            card.Bind(a, usage, myRoles, on, routed);
        }
        _cards.Height = (int)(186 * DeviceDpi / 96f);

        var r = roles;
        string Name(Role role) => r.TryGetValue(role, out var b) ? b.Adapter?.ShortName ?? "Windows" : "?";
        _status.Text = $"Games → {Name(Role.Game)}    Apps → {Name(Role.App)}    Media → {Name(Role.Media)}    ·    hook {(Injector.Available ? "ready" : "missing")}";
    }

    void RefreshApps()
    {
        var apps = _engine.GetApps(_showIdle.Checked);
        if (!_showIdle.Checked) apps = apps.Where(a => !(a.Role == Role.Ignore && a.Reason == "Windows component")).ToList();
        var filter = _filter.Text.Trim();
        if (filter.Length > 0)
            apps = apps.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (a.ExePath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        _apps.BeginUpdate();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
        {
            seen.Add(a.Target);
            if (!_appItems.TryGetValue(a.Target, out var item))
            {
                item = new ListViewItem(new string[11]) { UseItemStyleForSubItems = false };
                _appItems[a.Target] = item;
                _apps.Items.Add(item);
            }
            item.Tag = a;
            Set(item, 0, Path.GetFileNameWithoutExtension(a.Name) + (a.Pids.Count > 1 ? $" ({a.Pids.Count})" : ""));
            Set(item, 1, a.Role == Role.Ignore ? "—" : a.Role.ToString() + (a.HasOverride ? " *" : ""));
            Set(item, 2, a.Routing);
            Set(item, 3, a.ActualAdapter?.ShortName ?? (a.UsesGpu ? "?" : "—"));
            Set(item, 4, a.UsesGpu ? $"{a.Busy:0}%" : "");
            Set(item, 5, a.Decode + a.Encode >= 0.5 ? $"{a.Decode:0}/{a.Encode:0}%" : "");
            Set(item, 6, a.Memory > 0 ? Theme.Mb(a.Memory) : "");
            Set(item, 7, StatusText(a));
            Set(item, 8, a.Hook);
            Set(item, 9, a.Reason);
            Set(item, 10, a.ExePath ?? a.Target);
            item.SubItems[1].ForeColor = Theme.RoleColor(a.Role);
            item.SubItems[7].ForeColor = a.Status switch
            {
                RouteStatus.OnTarget => Theme.Good,
                RouteStatus.RestartNeeded => Theme.Warn,
                RouteStatus.IgnoresPreference => Theme.Bad,
                _ => Theme.Muted,
            };
            item.ForeColor = a.UsesGpu ? _apps.ForeColor : Theme.Muted;
        }
        foreach (var gone in _appItems.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _apps.Items.Remove(_appItems[gone]);
            _appItems.Remove(gone);
        }
        SortApps();
        _apps.EndUpdate();

        int restart = apps.Count(a => a.Status == RouteStatus.RestartNeeded);
        int off = apps.Count(a => a.Status == RouteStatus.IgnoresPreference);
        int on = apps.Count(a => a.Status == RouteStatus.OnTarget);
        _tabs.TabPages[0].Text = restart > 0 ? $"Apps ({restart} to restart)" : "Apps";
        if (_tabs.SelectedIndex == 0) Footer($"{on} on their GPU · {restart} waiting for a restart · {off} choosing their own GPU");
    }

    static string StatusText(AppView a) => a.Status switch
    {
        RouteStatus.OnTarget => "✔ " + a.StatusText,
        RouteStatus.RestartNeeded => "⟳ " + a.StatusText,
        RouteStatus.IgnoresPreference => "✖ " + a.StatusText,
        _ => a.StatusText,
    };

    static void Set(ListViewItem item, int col, string text)
    {
        if (item.SubItems[col].Text != text) item.SubItems[col].Text = text;
    }

    void SortApps()
    {
        int col = _sortColumn, dir = _sortDesc ? -1 : 1;
        _apps.ListViewItemSorter = Comparer<ListViewItem>.Create((x, y) =>
        {
            if (x.Tag is not AppView a || y.Tag is not AppView b) return 0;
            int c = col switch
            {
                -1 => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : b.Memory.CompareTo(a.Memory),
                4 => a.Busy.CompareTo(b.Busy),
                5 => (a.Decode + a.Encode).CompareTo(b.Decode + b.Encode),
                6 => a.Memory.CompareTo(b.Memory),
                _ => string.Compare(x.SubItems[col].Text, y.SubItems[col].Text, StringComparison.OrdinalIgnoreCase),
            };
            return col == -1 ? c : c * dir;
        }) as System.Collections.IComparer;
        static int Rank(AppView a) => a.Status switch
        {
            RouteStatus.RestartNeeded => 0, RouteStatus.IgnoresPreference => 1, RouteStatus.OnTarget => 2, RouteStatus.Ignored => 3, _ => 4,
        };
    }

    void RefreshRules()
    {
        var rules = _engine.GetRules();
        _rules.BeginUpdate();
        _rules.Items.Clear();
        foreach (var r in rules)
        {
            var state = r.Stale ? "⚠ GPU missing" : r.Running ? "running" : "";
            var item = new ListViewItem(new[] { r.Target, r.Routing, r.Owner, r.Role?.ToString() ?? "", state, r.Raw }) { Tag = r, UseItemStyleForSubItems = false };
            if (r.Stale) item.SubItems[4].ForeColor = Theme.Warn;
            if (!r.IsRouting) item.ForeColor = Theme.Muted;
            _rules.Items.Add(item);
        }
        _rules.EndUpdate();
        if (_tabs.SelectedIndex == 1)
            Footer($"{rules.Count} entries · {rules.Count(r => r.Owner == "GpuSwitcher")} by GpuSwitcher · {rules.Count(r => r.Stale)} stale");
    }

    void RefreshRoots()
    {
        _roots.BeginUpdate();
        _roots.Items.Clear();
        foreach (var r in _engine.GameRoots)
            _roots.Items.Add(new ListViewItem(new[] { r.Path, r.Source }) { Tag = r });
        _roots.EndUpdate();
    }

    void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var cfg = _engine.Config;
            var adapters = _engine.Adapters.Where(a => a.IsRoutable).ToList();   // identical cards listed separately (by slot)
            foreach (var (role, cb) in _roleCombos)
            {
                cb.Items.Clear();
                cb.Items.Add(new Choice("Automatic", "auto"));
                foreach (var a in adapters) cb.Items.Add(new Choice($"{a.ShortName}  ({a.Key})", a.Id));
                cb.Items.Add(new Choice("Let Windows decide (no routing)", "windows"));
                cfg.RoleGpu.TryGetValue(role, out var v);
                v ??= "auto";
                cb.SelectedItem = cb.Items.Cast<Choice>().FirstOrDefault(c => c.Value.Equals(v, StringComparison.OrdinalIgnoreCase))
                    ?? cb.Items.Cast<Choice>().FirstOrDefault(c => c.Value.StartsWith(v + "@", StringComparison.OrdinalIgnoreCase))   // older configs store the bare key
                    ?? cb.Items[0];
            }
            Select(_appHook, cfg.AppInjection.ToString());
            Select(_mediaHook, cfg.MediaInjection.ToString());
            _routeUnknown.Checked = cfg.RouteUnknownAtLaunch;
            _notify.Checked = cfg.NotifyRestart;
            _startup.Checked = cfg.TrayAtLogon || Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("GpuSwitcher") != null;
            RefreshServiceState();
            _autoToggle.Checked = cfg.AutoRoute;
            _mediaExes.Lines = cfg.MediaExes.ToArray();
            _gameExes.Lines = cfg.GameExes.ToArray();
            _ignoreExes.Lines = cfg.IgnoreExes.ToArray();
            _noInjectExes.Lines = cfg.NoInjectExes.ToArray();
            UpdateAutoToggle();
        }
        finally { _loadingSettings = false; }

        static void Select(ComboBox cb, string value) =>
            cb.SelectedItem = cb.Items.Cast<Choice>().FirstOrDefault(c => c.Value == value) ?? cb.Items[0];
    }

    void UpdateAutoToggle()
    {
        _autoToggle.Text = _autoToggle.Checked ? "  Auto-routing ON  " : "  Auto-routing OFF  ";
    }

    void UpdateTrayText()
    {
        var r = _engine.Roles;
        var t = $"GPU Switcher — games: {r[Role.Game].Adapter?.ShortName ?? "Windows"}, apps: {r[Role.App].Adapter?.ShortName ?? "Windows"}";
        _tray.Text = t.Length > 63 ? t[..63] : t;
    }

    void AppendLog(LogEntry e)
    {
        _log.Items.Add(e.ToString());
        while (_log.Items.Count > 1000) _log.Items.RemoveAt(0);
        if (_tabs.SelectedIndex == 4) _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
    }

    // ================================================================= actions

    AppView? Selected() => _apps.SelectedItems.Count > 0 ? _apps.SelectedItems[0].Tag as AppView : null;
    List<string> SelectedRules() => _rules.SelectedItems.Cast<ListViewItem>().Select(i => ((RuleView)i.Tag!).Target).ToList();

    void Rule(string target, Role? role)
    {
        var msg = _engine.SetOverride(target, role);
        var running = _engine.ProcessesOf(target).Count > 0;
        Footer(msg + (running && role != Role.Ignore ? " Restart the app to move it." : ""));
        RefreshApps();
    }

    async void RestartSelected(bool force)
    {
        var sel = _apps.SelectedItems.Cast<ListViewItem>().Select(i => (AppView)i.Tag!).ToList();
        if (sel.Count == 0) return;
        if (sel.Any(a => a.Role == Role.Game || a.IsGameLike) &&
            !Confirm("Restarting a game from here skips its launcher. Restart it from Steam/Epic instead?\n\nPress OK to restart it here anyway."))
            return;
        if (force && !Confirm($"Kill {string.Join(", ", sel.Select(a => a.Name))}? Unsaved work will be lost.")) return;
        foreach (var a in sel)
        {
            Footer($"Restarting {a.Name}…");
            var r = await Task.Run(() => _engine.Restart(a.Target, force));
            Footer(r.Message);
            if (!r.Closed && !force && Confirm($"{a.Name}: {r.Message}\n\nForce it closed and relaunch?"))
                Footer((await Task.Run(() => _engine.Restart(a.Target, true))).Message);
        }
    }

    async void RestartAllNeeded()
    {
        var list = _engine.GetApps(false).Where(a => a.Status == RouteStatus.RestartNeeded && a.Role != Role.Game && !a.IsGameLike).ToList();
        if (list.Count == 0) { Footer("Nothing needs a restart."); return; }
        if (!Confirm($"Close and relaunch {list.Count} apps so they move to their GPU?\n\n{string.Join("\n", list.Select(a => "• " + a.Name))}\n\nApps are asked to close normally; any that refuse are left running.")) return;
        int ok = 0;
        foreach (var a in list)
        {
            Footer($"Restarting {a.Name}…");
            if ((await Task.Run(() => _engine.Restart(a.Target, false))).Relaunched) ok++;
        }
        Footer($"Restarted {ok} of {list.Count} apps. Check the Log tab for the rest.");
    }

    /// <summary>With the boot guard installed, the service opens this app the instant you log on (ahead of every
    /// Run-key app); without it, fall back to the Run key.</summary>
    void SetStartup(bool on)
    {
        bool service = BootGuardService.Query() != BootGuardService.State.NotInstalled;
        _engine.UpdateConfig(c => c.TrayAtLogon = on);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (on && !service) key.SetValue("GpuSwitcher", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("GpuSwitcher", false);
        Footer(!on ? "Removed from startup."
            : service ? "The boot guard will open GPU Switcher the moment you log on, before other startup apps."
            : "GPU Switcher will start with Windows (Run key). Install the boot guard to start before everything else.");
    }

    bool Confirm(string text) => MessageBox.Show(this, text, "GPU Switcher", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
    void Footer(string text) => _footer.Text = text;
    static void Reveal(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    static TextBox MultiLine() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Cascadia Mono", 9f), WordWrap = false };
    static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
    static GroupBox Group(string text, Control inner)
    {
        var g = new GroupBox { Text = text, Dock = DockStyle.Fill, Padding = new Padding(8) };
        g.Controls.Add(inner);
        return g;
    }

    sealed record Choice(string Text, string Value) { public override string ToString() => Text; }

    // ================================================================= test hook

    /// <summary>Renders the window to a PNG after the engine has sampled for a while, then exits (used for automated checks).</summary>
    public void CaptureAndExit(string png, string? tab, TimeSpan after)
    {
        Shown += async (_, _) =>
        {
            if (tab != null)
                for (int i = 0; i < _tabs.TabPages.Count; i++)
                    if (_tabs.TabPages[i].Text.StartsWith(tab, StringComparison.OrdinalIgnoreCase)) _tabs.SelectedIndex = i;
            await Task.Delay(after);
            RefreshAll();
            await Task.Delay(300);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            _exiting = true;
            Close();
        };
    }
}
