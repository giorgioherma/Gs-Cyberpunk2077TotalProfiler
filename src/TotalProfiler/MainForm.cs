using System.Diagnostics;
using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal sealed class MainForm : Form
{
    private const long CetF11BindCode = 34339947158700032L;

    private readonly AppConfig cfg;

    private readonly TextBox gameBox = new();
    private readonly TextBox capExeBox = new();
    private readonly TextBox capResultsBox = new();
    private readonly TextBox resultsBox = new();
    private readonly TextBox scenarioBox = new();
    private readonly TextBox captureNameBox = new();

    private readonly CheckBox coreOnly = new();

    private readonly Label stepLabel = new();
    private readonly Label setupGameStatus = new();
    private readonly Label gameStatus = new();
    private readonly Label grspStatus = new();
    private readonly Label cetStatus = new();
    private readonly Label zeroStatus = new();
    private readonly Label capStatus = new();
    private readonly Label cetBindingStatus = new();
    private readonly Label capBindingStatus = new();
    private readonly Label installStatus = new();
    private readonly Label captureReadiness = new();
    private readonly Label lastCaptureStatus = new();
    private readonly Label resultsLocationStatus = new();
    private readonly LinkLabel checkResultsLink = new();

    private readonly TextBox logBox = new();

    private readonly Button setupNext = new();
    private readonly Button installButton = new();
    private readonly Button installNext = new();
    private readonly Button collectButton = new();
    private readonly Button compareButton = new();
    private readonly Button restoreButton = new();

    private readonly Panel setupPage = new();
    private readonly Panel installPage = new();
    private readonly Panel capturePage = new();
    private readonly Panel resultsPage = new();
    private readonly GroupBox advancedPaths = new();
    private readonly GroupBox technicalLog = new();

    private bool busy;
    private bool gameValid;
    private bool installVerified;
    private bool cetBindingVerified;
    private bool capSettingsVerified;
    private bool syncingScenarioNames;
    private int currentPage;

    public MainForm()
    {
        cfg = AppConfig.Load();
        Text = ProfilerServices.AppName;
        Width = 900;
        Height = 690;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        BuildUi();
        scenarioBox.TextChanged += (_, _) => SyncScenarioName(scenarioBox, captureNameBox);
        captureNameBox.TextChanged += (_, _) => SyncScenarioName(captureNameBox, scenarioBox);
        LoadConfigIntoUi();

        Shown += async (_, _) =>
        {
            await RefreshStatusAsync();
            ShowPage(installVerified ? 2 : 0);
        };
        FormClosing += (_, _) => SaveConfig();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = ProfilerServices.AppName,
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var sub = new Label
        {
            Text = $"GRSP {ProfilerServices.GrspVersion}  ·  CET {ProfilerServices.CetVersion}  ·  CapFrameX {ProfilerServices.BundledCapFrameXVersion}  ·  native correlator",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(2, 38)
        };
        header.Controls.Add(title);
        header.Controls.Add(sub);
        root.Controls.Add(header, 0, 0);

        var steps = new Panel { Dock = DockStyle.Fill };
        stepLabel.AutoSize = true;
        stepLabel.Font = new Font("Segoe UI Semibold", 10F);
        stepLabel.Location = new Point(2, 8);
        steps.Controls.Add(stepLabel);
        root.Controls.Add(steps, 0, 1);

        var pageHost = new Panel { Dock = DockStyle.Fill };
        setupPage.Dock = DockStyle.Fill;
        installPage.Dock = DockStyle.Fill;
        capturePage.Dock = DockStyle.Fill;
        resultsPage.Dock = DockStyle.Fill;
        pageHost.Controls.Add(resultsPage);
        pageHost.Controls.Add(capturePage);
        pageHost.Controls.Add(installPage);
        pageHost.Controls.Add(setupPage);
        root.Controls.Add(pageHost, 0, 2);

        BuildSetupPage();
        BuildInstallPage();
        BuildCapturePage();
        BuildResultsPage();
        ShowPage(0);
    }

    private TableLayoutPanel PageBody(Panel page)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(8),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            MinimumSize = new Size(680, 0)
        };
        scroll.Controls.Add(body);
        page.Controls.Add(scroll);

        void Fit()
        {
            var usable = Math.Max(680, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            if (body.Width != usable) body.Width = usable;
        }

        scroll.ClientSizeChanged += (_, _) => Fit();
        Fit();
        return body;
    }

    private void BuildSetupPage()
    {
        var body = PageBody(setupPage);

        body.Controls.Add(PageHeading("STEP 1 — SETUP", "Point TOTAL Profiler at Cyberpunk 2077. Everything else uses the bundled defaults unless you choose custom paths."));

        var gameGroup = Group("Cyberpunk 2077");
        var gg = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2 };
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        gg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        gameBox.Dock = DockStyle.Fill;
        var browseGame = new Button { Text = "Browse...", Dock = DockStyle.Fill, Height = 30 };
        browseGame.Click += (_, _) => BrowseGame();
        gg.Controls.Add(gameBox, 0, 0);
        gg.Controls.Add(browseGame, 1, 0);
        setupGameStatus.AutoSize = true;
        setupGameStatus.Margin = new Padding(0, 8, 0, 2);
        gg.Controls.Add(setupGameStatus, 0, 1);
        gg.SetColumnSpan(setupGameStatus, 2);
        gameGroup.Controls.Add(gg);
        body.Controls.Add(gameGroup);

        body.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(800, 0),
            ForeColor = SystemColors.GrayText,
            Text = $"Default setup: bundled CapFrameX {ProfilerServices.BundledCapFrameXVersion} + local Results folder. Most users do not need to change anything below."
        });

        var advancedToggle = new Button { Text = "Custom paths / advanced setup ▼", AutoSize = true, Height = 30, Margin = new Padding(0, 10, 0, 3) };
        advancedToggle.Click += (_, _) =>
        {
            advancedPaths.Visible = !advancedPaths.Visible;
            advancedToggle.Text = advancedPaths.Visible ? "Custom paths / advanced setup ▲" : "Custom paths / advanced setup ▼";
        };
        body.Controls.Add(advancedToggle);

        advancedPaths.Text = "Custom paths";
        advancedPaths.Dock = DockStyle.Fill;
        advancedPaths.AutoSize = true;
        advancedPaths.Padding = new Padding(10);
        advancedPaths.Visible = false;

        var ag = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 4 };
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        ag.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        AddPathRow(ag, 0, "CapFrameX.exe", capExeBox, BrowseCapExe, "Use bundled", UseBundledCapFrameX);
        AddPathRow(ag, 1, "CapFrameX captures", capResultsBox, BrowseCapResults);
        AddPathRow(ag, 2, "Profiler results", resultsBox, BrowseResults, "Open", () => ProfilerServices.OpenPath(resultsBox.Text));

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(750, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Custom CapFrameX versions are allowed when their capture schema is compatible. Bundled CapFrameX uses Tools\\CapFrameX\\Portable\\Captures."
        };
        ag.Controls.Add(note, 0, 3);
        ag.SetColumnSpan(note, 4);
        advancedPaths.Controls.Add(ag);
        body.Controls.Add(advancedPaths);

        var nav = NavPanel();
        setupNext.Text = "CONTINUE →";
        setupNext.Width = 130;
        setupNext.Height = 34;
        setupNext.Click += async (_, _) =>
        {
            SaveConfig();
            await RefreshStatusAsync();
            if (gameValid) ShowPage(1);
        };
        nav.Controls.Add(setupNext);
        body.Controls.Add(nav);
    }

    private void BuildInstallPage()
    {
        var body = PageBody(installPage);

        body.Controls.Add(PageHeading("STEP 2 — INSTALL & VERIFY", "Install the profilers once, verify every component, then continue."));

        var options = Group("Installation options");
        var og = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        og.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        og.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        coreOnly.Text = "CET core profiler only — leave existing 0-Engine untouched";
        coreOnly.AutoSize = true;
        og.Controls.Add(coreOnly, 0, 0);
        og.SetColumnSpan(coreOnly, 2);

        og.Controls.Add(new Label { Text = "Scenario / capture name:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 1);
        scenarioBox.Width = 180;
        scenarioBox.Anchor = AnchorStyles.Left;
        og.Controls.Add(scenarioBox, 1, 1);
        options.Controls.Add(og);
        body.Controls.Add(options);

        var status = Group("Verification");
        var sg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, AutoSize = true };
        sg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        sg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatusRow(sg, 0, "Game", gameStatus);
        AddStatusRow(sg, 1, "GRSP", grspStatus);
        AddStatusRow(sg, 2, "CET profiler", cetStatus);
        AddStatusRow(sg, 3, "0-Engine", zeroStatus);
        AddStatusRow(sg, 4, "CapFrameX", capStatus);
        AddStatusRow(sg, 5, "CET F11", cetBindingStatus);
        AddStatusRow(sg, 6, "CapFrameX F11", capBindingStatus);
        AddStatusRow(sg, 7, "Install check", installStatus);
        status.Controls.Add(sg);
        body.Controls.Add(status);

        var installActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        installButton.Text = "INSTALL / VERIFY";
        installButton.Width = 165;
        installButton.Height = 36;
        installButton.Click += async (_, _) => await InstallAsync();

        var refresh = new Button { Text = "Refresh checks", Width = 125, Height = 36 };
        refresh.Click += async (_, _) => await RefreshStatusAsync();

        installActions.Controls.Add(installButton);
        installActions.Controls.Add(refresh);
        body.Controls.Add(installActions);

        var logToggle = new Button { Text = "Technical log ▼", AutoSize = true, Height = 30, Margin = new Padding(0, 5, 0, 3) };
        logToggle.Click += (_, _) =>
        {
            technicalLog.Visible = !technicalLog.Visible;
            logToggle.Text = technicalLog.Visible ? "Technical log ▲" : "Technical log ▼";
        };
        body.Controls.Add(logToggle);

        technicalLog.Text = "Log";
        technicalLog.Dock = DockStyle.Fill;
        technicalLog.Height = 160;
        technicalLog.MinimumSize = new Size(0, 160);
        technicalLog.Visible = false;
        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 9F);
        technicalLog.Controls.Add(logBox);
        body.Controls.Add(technicalLog);

        var nav = NavPanel();
        var back = new Button { Text = "← SETUP", Width = 110, Height = 34 };
        back.Click += (_, _) => ShowPage(0);
        installNext.Text = "CONTINUE →";
        installNext.Width = 130;
        installNext.Height = 34;
        installNext.Click += async (_, _) =>
        {
            await RefreshStatusAsync();
            if (installVerified) ShowPage(2);
        };
        nav.Controls.Add(back);
        nav.Controls.Add(installNext);
        body.Controls.Add(nav);
    }

    private void BuildCapturePage()
    {
        var body = PageBody(capturePage);

        body.Controls.Add(PageHeading("STEP 3 — MEASUREMENT", "Run one synchronized F11 measurement, then continue to Results."));

        var nameGroup = Group("Measurement");
        var ng = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        ng.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        ng.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ng.Controls.Add(new Label { Text = "Scenario / capture name:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 0);
        captureNameBox.Dock = DockStyle.Fill;
        captureNameBox.PlaceholderText = "e.g. Drive-Battle-Chase";
        ng.Controls.Add(captureNameBox, 1, 0);
        captureReadiness.AutoSize = true;
        captureReadiness.MaximumSize = new Size(760, 0);
        captureReadiness.Margin = new Padding(0, 10, 0, 0);
        ng.Controls.Add(captureReadiness, 0, 1);
        ng.SetColumnSpan(captureReadiness, 2);
        nameGroup.Controls.Add(ng);
        body.Controls.Add(nameGroup);

        var workflow = Group("Measurement");
        var wg = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 6 };

        wg.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(770, 0),
            Font = new Font("Segoe UI Semibold", 13F),
            Text = "Make sure all 3 profilers have their start/stop key bound to F11.",
            Margin = new Padding(0, 2, 0, 8)
        });

        wg.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F),
            Text = "RUN THE GAME AND PROFILERS!",
            Margin = new Padding(0, 2, 0, 8)
        });

        wg.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(770, 0),
            Text =
                "1. Load a save.\r\n" +
                "2. Press F11 to start capture (temporarily unbind any other bindings from F11).\r\n" +
                "3. Press F11 when finished.\r\n" +
                "4. Return to this page."
        });

        var launchFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 5) };
        var launchCap = new Button { Text = "LAUNCH CAPFRAMEX", Width = 165, Height = 36 };
        launchCap.Click += (_, _) => LaunchCapX();
        var launchGame = new Button { Text = "LAUNCH CYBERPUNK", Width = 170, Height = 36 };
        launchGame.Click += (_, _) => LaunchGame();
        var refresh = new Button { Text = "Refresh readiness", Width = 140, Height = 36 };
        refresh.Click += async (_, _) => await RefreshStatusAsync();
        launchFlow.Controls.Add(launchCap);
        launchFlow.Controls.Add(launchGame);
        launchFlow.Controls.Add(refresh);
        wg.Controls.Add(launchFlow);

        var resetFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        resetFlow.Controls.Add(new Label
        {
            Text = "If all 3 profilers did not run, RESET CAPTURE STATE here and repeat the measurement.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 8, 10, 0)
        });
        var resetCapture = new Button { Text = "RESET CAPTURE STATE", Width = 170, Height = 32 };
        resetCapture.Click += async (_, _) => await ResetCaptureStateAsync();
        resetFlow.Controls.Add(resetCapture);
        wg.Controls.Add(resetFlow);

        workflow.Controls.Add(wg);
        body.Controls.Add(workflow);

        var nav = NavPanel();
        var back = new Button { Text = "← INSTALL", Width = 110, Height = 34 };
        back.Click += (_, _) => ShowPage(1);
        var results = new Button { Text = "RESULTS →", Width = 120, Height = 34 };
        results.Click += async (_, _) =>
        {
            await RefreshStatusAsync();
            ShowPage(3);
        };
        nav.Controls.Add(back);
        nav.Controls.Add(results);
        body.Controls.Add(nav);
    }

    private void BuildResultsPage()
    {
        var body = PageBody(resultsPage);

        body.Controls.Add(PageHeading("STEP 4 — RESULTS", "Collect the three profiler outputs, compare them, or open the latest result."));

        var results = Group("Results");
        var rg = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 5 };

        var resultActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        collectButton.Text = "COLLECT RESULTS";
        collectButton.Width = 160;
        collectButton.Height = 38;
        collectButton.Click += async (_, _) => await CollectAsync();

        compareButton.Text = "COMPARE RESULTS";
        compareButton.Width = 165;
        compareButton.Height = 38;
        compareButton.Click += async (_, _) => await CompareAsync();

        var openLatest = new Button { Text = "OPEN LAST", Width = 115, Height = 38 };
        openLatest.Click += (_, _) => OpenLatest();

        resultActions.Controls.Add(collectButton);
        resultActions.Controls.Add(compareButton);
        resultActions.Controls.Add(openLatest);
        rg.Controls.Add(resultActions);

        lastCaptureStatus.AutoSize = true;
        lastCaptureStatus.MaximumSize = new Size(760, 0);
        lastCaptureStatus.Margin = new Padding(0, 10, 0, 2);
        rg.Controls.Add(lastCaptureStatus);

        resultsLocationStatus.AutoSize = true;
        resultsLocationStatus.MaximumSize = new Size(760, 0);
        resultsLocationStatus.ForeColor = SystemColors.GrayText;
        resultsLocationStatus.Margin = new Padding(0, 4, 0, 4);
        rg.Controls.Add(resultsLocationStatus);

        checkResultsLink.Text = "CHECK RESULTS HERE!";
        checkResultsLink.AutoSize = true;
        checkResultsLink.Font = new Font("Segoe UI Semibold", 10F);
        checkResultsLink.Margin = new Padding(0, 8, 0, 8);
        checkResultsLink.LinkClicked += (_, _) => OpenLatestReportOrFolder();
        rg.Controls.Add(checkResultsLink);

        rg.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = SystemColors.GrayText,
            Text = "COLLECT RESULTS copies the profiler outputs into the TOTAL Profiler Results folder and opens that capture folder so the files are visible immediately."
        });

        results.Controls.Add(rg);
        body.Controls.Add(results);

        var restoreInfo = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(780, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 14, 0, 4),
            Text = "RESTORE GAME FILES returns every TOTAL Profiler-managed game file and configuration to its original state. Any uncollected profiler results will be lost. Already-collected Results stay."
        };
        body.Controls.Add(restoreInfo);

        var nav = NavPanel();
        var back = new Button { Text = "← MEASUREMENT", Width = 145, Height = 34 };
        back.Click += (_, _) => ShowPage(2);

        restoreButton.Text = "RESTORE GAME FILES";
        restoreButton.Width = 175;
        restoreButton.Height = 34;
        restoreButton.Click += async (_, _) => await RestoreAllAsync();

        nav.Controls.Add(back);
        nav.Controls.Add(restoreButton);
        body.Controls.Add(nav);
    }

    private static Control PageHeading(string title, string description)
    {
        var p = new Panel { Dock = DockStyle.Fill, Height = 68, Margin = new Padding(0, 0, 0, 8) };
        var h = new Label { Text = title, Font = new Font("Segoe UI Semibold", 15F), AutoSize = true, Location = new Point(0, 0) };
        var d = new Label { Text = description, AutoSize = true, ForeColor = SystemColors.GrayText, Location = new Point(2, 34) };
        p.Controls.Add(h);
        p.Controls.Add(d);
        return p;
    }

    private static FlowLayoutPanel NavPanel() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Margin = new Padding(0, 14, 0, 0)
    };

    private static GroupBox Group(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        Padding = new Padding(10),
        Margin = new Padding(0, 5, 0, 5)
    };

    private static void AddStatusRow(TableLayoutPanel p, int row, string name, Label value)
    {
        p.Controls.Add(new Label { Text = name + ":", AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 0, row);
        value.AutoSize = true;
        value.MaximumSize = new Size(610, 0);
        value.Margin = new Padding(0, 4, 0, 4);
        p.Controls.Add(value, 1, row);
    }

    private static void AddPathRow(TableLayoutPanel p, int row, string label, TextBox box, Action browse, string? extraText = null, Action? extra = null)
    {
        p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 0) }, 0, row);
        box.Dock = DockStyle.Fill;
        p.Controls.Add(box, 1, row);
        var b = new Button { Text = "Browse...", Dock = DockStyle.Fill, Height = 27 };
        b.Click += (_, _) => browse();
        p.Controls.Add(b, 2, row);
        if (extraText is not null && extra is not null)
        {
            var e = new Button { Text = extraText, Dock = DockStyle.Fill, Height = 27 };
            e.Click += (_, _) => extra();
            p.Controls.Add(e, 3, row);
        }
    }

    private void ShowPage(int page)
    {
        currentPage = Math.Clamp(page, 0, 3);
        setupPage.Visible = currentPage == 0;
        installPage.Visible = currentPage == 1;
        capturePage.Visible = currentPage == 2;
        resultsPage.Visible = currentPage == 3;

        if (setupPage.Visible) setupPage.BringToFront();
        if (installPage.Visible) installPage.BringToFront();
        if (capturePage.Visible) capturePage.BringToFront();
        if (resultsPage.Visible) resultsPage.BringToFront();

        stepLabel.Text = currentPage switch
        {
            0 => "1  SETUP   ›   2  INSTALL & VERIFY   ›   3  MEASUREMENT   ›   4  RESULTS",
            1 => "1  Setup   ›   2  INSTALL & VERIFY   ›   3  Measurement   ›   4  Results",
            2 => "1  Setup   ›   2  Install & Verify   ›   3  MEASUREMENT   ›   4  Results",
            _ => "1  Setup   ›   2  Install & Verify   ›   3  Measurement   ›   4  RESULTS"
        };
    }

    private void SyncScenarioName(TextBox source, TextBox target)
    {
        if (syncingScenarioNames) return;
        try
        {
            syncingScenarioNames = true;
            if (!string.Equals(target.Text, source.Text, StringComparison.Ordinal))
                target.Text = source.Text;
        }
        finally
        {
            syncingScenarioNames = false;
        }
    }

    private void LoadConfigIntoUi()
    {
        if (cfg.UseBundledCapFrameX && File.Exists(ProfilerServices.BundledCapFrameXExe))
        {
            cfg.CapFrameXExe = ProfilerServices.BundledCapFrameXExe;
            cfg.CapFrameXResults = ProfilerServices.BundledCapFrameXResults;
            Directory.CreateDirectory(cfg.CapFrameXResults);
            cfg.Save();
        }

        gameBox.Text = cfg.GameDirectory;
        capExeBox.Text = cfg.CapFrameXExe;
        capResultsBox.Text = cfg.CapFrameXResults;
        resultsBox.Text = cfg.ResultsDirectory;

        var sharedScenario = !string.IsNullOrWhiteSpace(cfg.CaptureName) &&
                             !string.Equals(cfg.CaptureName, "TEST", StringComparison.OrdinalIgnoreCase)
            ? cfg.CaptureName
            : cfg.Scenario;
        if (string.IsNullOrWhiteSpace(sharedScenario)) sharedScenario = "TEST";
        scenarioBox.Text = sharedScenario;
        captureNameBox.Text = sharedScenario;
        cfg.Scenario = sharedScenario;
        cfg.CaptureName = sharedScenario;
        cfg.Save();

        coreOnly.Checked = cfg.CetCoreOnly;
        UpdateActionState();
    }

    private void SaveConfig()
    {
        cfg.GameDirectory = gameBox.Text.Trim();
        cfg.CapFrameXExe = capExeBox.Text.Trim();
        cfg.CapFrameXResults = capResultsBox.Text.Trim();
        cfg.ResultsDirectory = resultsBox.Text.Trim();
        var sharedScenario = string.IsNullOrWhiteSpace(scenarioBox.Text) ? "TEST" : scenarioBox.Text.Trim();
        if (!string.Equals(captureNameBox.Text, sharedScenario, StringComparison.Ordinal))
            captureNameBox.Text = sharedScenario;
        cfg.Scenario = sharedScenario;
        cfg.CaptureName = sharedScenario;
        cfg.CetCoreOnly = coreOnly.Checked;
        cfg.Save();
    }

    private void BrowseGame()
    {
        using var d = new FolderBrowserDialog { Description = "Select Cyberpunk 2077 game directory", SelectedPath = gameBox.Text };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            gameBox.Text = d.SelectedPath;
            SaveConfig();
            _ = RefreshStatusAsync();
        }
    }

    private void BrowseCapExe()
    {
        using var d = new OpenFileDialog
        {
            Title = "Select an existing CapFrameX.exe (any compatible version)",
            Filter = "CapFrameX|CapFrameX.exe|Executable|*.exe|All files|*.*",
            FileName = "CapFrameX.exe"
        };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            cfg.UseBundledCapFrameX = false;
            capExeBox.Text = d.FileName;
            var detected = ProfilerServices.DetectCapFrameXPath(d.FileName).Captures;
            if (!string.IsNullOrWhiteSpace(detected)) capResultsBox.Text = detected;
            SaveConfig();
            _ = RefreshStatusAsync();
        }
    }

    private void UseBundledCapFrameX()
    {
        if (!File.Exists(ProfilerServices.BundledCapFrameXExe))
        {
            MessageBox.Show(this, "Bundled CapFrameX was not found in this TOTAL Profiler package.", ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        cfg.UseBundledCapFrameX = true;
        capExeBox.Text = ProfilerServices.BundledCapFrameXExe;
        capResultsBox.Text = ProfilerServices.BundledCapFrameXResults;
        Directory.CreateDirectory(capResultsBox.Text);
        SaveConfig();
        _ = RefreshStatusAsync();
    }

    private void BrowseCapResults()
    {
        using var d = new FolderBrowserDialog { Description = "Select CapFrameX capture/results folder", SelectedPath = capResultsBox.Text };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            capResultsBox.Text = d.SelectedPath;
            SaveConfig();
            _ = RefreshStatusAsync();
        }
    }

    private void BrowseResults()
    {
        using var d = new FolderBrowserDialog { Description = "Select TOTAL Profiler results folder", SelectedPath = resultsBox.Text };
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            resultsBox.Text = d.SelectedPath;
            SaveConfig();
        }
    }

    private void LaunchCapX()
    {
        if (!File.Exists(capExeBox.Text))
        {
            MessageBox.Show(this, "CapFrameX.exe was not found. Check Custom paths / advanced setup.", ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var exe = Path.GetFullPath(capExeBox.Text);
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
    }

    private void LaunchGame()
    {
        var exe = Path.Combine(gameBox.Text.Trim(), "bin", "x64", "Cyberpunk2077.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, "Cyberpunk2077.exe was not found at bin\\x64\\Cyberpunk2077.exe.", ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
    }

    private void Log(string msg)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(msg));
            return;
        }

        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Cursor = value ? Cursors.WaitCursor : Cursors.Default;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        setupNext.Enabled = !busy && gameValid;
        installButton.Enabled = !busy && gameValid;
        installNext.Enabled = !busy && installVerified;
        collectButton.Enabled = !busy && installVerified;
        compareButton.Enabled = !busy && !string.IsNullOrWhiteSpace(cfg.LastCapture) && Directory.Exists(cfg.LastCapture);
        restoreButton.Enabled = !busy && gameValid;
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            SaveConfig();
            await action();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshStatusAsync()
    {
        SaveConfig();
        try
        {
            var snap = AppConfig.Load();
            var data = await Task.Run(async () =>
            {
                var v = ProfilerServices.ValidateGameRoot(snap.GameDirectory);
                string grsp = "NOT INSTALLED";
                string cet = "NOT AVAILABLE";
                string zero = "-";
                bool grspOk = false;
                bool cetOk = false;
                bool controlsOk = false;

                if (v.Ok)
                {
                    var dll = Path.Combine(snap.GameDirectory, "red4ext", "plugins", "redscript_profiler_alpha.dll");
                    if (File.Exists(dll))
                    {
                        var h = ProfilerServices.Sha256(dll);
                        grspOk = string.Equals(h, ProfilerServices.GrspDllSha256, StringComparison.OrdinalIgnoreCase);
                        grsp = grspOk ? "INSTALLED ✓ · exact build" : $"OTHER BUILD ({h[..Math.Min(10, h.Length)]}…)";
                    }

                    try
                    {
                        using var s = await ProfilerServices.CallCetAsync("Status", snap.GameDirectory);
                        var r = s.RootElement;
                        var cetState = J(r, "cet");
                        var managed = J(r, "managed");
                        var controls = J(r, "controlsPresent");
                        cetOk = cetState == "PROFILER_ACTIVE" && managed == "True";
                        controlsOk = controls == "True";
                        cet = $"{cetState} · managed {managed} · controls {controls} · live CSVs {J(r, "liveResultCount")}";
                        zero = J(r, "zeroEnginePresent") == "True"
                            ? $"{J(r, "zeroEngineInit")} · {J(r, "scheduler")}{(string.IsNullOrWhiteSpace(J(r, "managedMode")) ? "" : " · managed mode " + J(r, "managedMode"))}"
                            : "Not found — core CET profiling only";
                    }
                    catch (Exception ex)
                    {
                        cet = "STATUS ERROR: " + ex.Message.Split('\n').Last();
                    }
                }

                bool cetBind = v.Ok && IsCetF11Configured(snap.GameDirectory);

                string cap = "NOT FOUND";
                bool capFound = File.Exists(snap.CapFrameXExe);
                if (capFound)
                {
                    bool bundled = string.Equals(Path.GetFullPath(snap.CapFrameXExe), Path.GetFullPath(ProfilerServices.BundledCapFrameXExe), StringComparison.OrdinalIgnoreCase);
                    cap = (bundled ? $"BUNDLED {ProfilerServices.BundledCapFrameXVersion} ✓" : "LINKED EXISTING ✓") +
                          (Directory.Exists(snap.CapFrameXResults) ? " · captures folder ✓" : " · captures folder NOT FOUND");
                }

                bool capConfigured = capFound && IsCapFrameXCaptureConfigured(snap.CapFrameXExe);
                bool verified = v.Ok && grspOk && cetOk && controlsOk && cetBind && capFound && capConfigured;

                return (v, grsp, cet, zero, cap, cetBind, capConfigured, verified);
            });

            gameValid = data.v.Ok;
            cetBindingVerified = data.cetBind;
            capSettingsVerified = data.capConfigured;
            installVerified = data.verified;

            setupGameStatus.Text = (gameValid ? "✓ " : "✗ ") + data.v.Message;
            setupGameStatus.ForeColor = gameValid ? Color.DarkGreen : Color.DarkRed;

            gameStatus.Text = (gameValid ? "FOUND ✓ · " : "NOT FOUND · ") + data.v.Message;
            grspStatus.Text = data.grsp;
            cetStatus.Text = data.cet;
            zeroStatus.Text = data.zero;
            capStatus.Text = data.cap;
            cetBindingStatus.Text = data.cetBind ? "F11 ✓ · TOTAL Profiler managed" : "NOT VERIFIED";
            capBindingStatus.Text = data.capConfigured ? "F11 + unlimited capture ✓" : "NOT VERIFIED";
            installStatus.Text = installVerified ? "VERIFIED ✓ · ready for capture" : "NOT READY · run INSTALL / VERIFY";
            installStatus.ForeColor = installVerified ? Color.DarkGreen : Color.DarkRed;

            captureReadiness.Text =
                $"Automatic checks: {(installVerified ? "READY ✓" : "NOT READY")}   ·   CET F11 {(cetBindingVerified ? "✓" : "✗")}   ·   CapFrameX F11/unlimited {(capSettingsVerified ? "✓" : "✗")}\r\n" +
                "First-run manual check: make sure CapFrameX (Capture tab), REDscript profiler (currently hard-coded F11), and CET profiler (CET binding) all use the same F11 start/stop key. If CapFrameX is not recording, make sure Cyberpunk2077.exe is the only active capture process.";
            captureReadiness.ForeColor = installVerified ? Color.DarkGreen : Color.DarkRed;

            resultsLocationStatus.Text = $"Results folder: {cfg.ResultsDirectory}";
            if (!string.IsNullOrWhiteSpace(cfg.LastCapture) && Directory.Exists(cfg.LastCapture))
                lastCaptureStatus.Text = $"Latest collected capture: {Path.GetFileName(cfg.LastCapture)}\r\n{cfg.LastCapture}";
            else
                lastCaptureStatus.Text = "No collected capture selected yet.";

            UpdateActionState();
        }
        catch (Exception ex)
        {
            Log("Status error: " + ex.Message);
        }
    }

    private static bool IsCetF11Configured(string gameRoot)
    {
        try
        {
            var path = Path.Combine(gameRoot, "bin", "x64", "plugins", "cyber_engine_tweaks", "bindings.json");
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("CETProfilerControls", out var mod)) return false;
            if (!mod.TryGetProperty("CETProfiler_Toggle", out var bind) || bind.ValueKind != JsonValueKind.Number) return false;
            return bind.TryGetInt64(out var code) && code == CetF11BindCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCapFrameXCaptureConfigured(string exePath)
    {
        try
        {
            var detected = ProfilerServices.DetectCapFrameXPath(exePath);
            if (string.IsNullOrWhiteSpace(detected.Config)) return false;
            var path = Path.Combine(detected.Config, "AppSettings.json");
            if (!File.Exists(path)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("CaptureHotKey", out var hotkey) || !string.Equals(hotkey.GetString(), "F11", StringComparison.OrdinalIgnoreCase)) return false;
            if (!root.TryGetProperty("CaptureTime", out var time) || time.ValueKind != JsonValueKind.Number || Math.Abs(time.GetDouble()) > 0.0001) return false;
            if (!root.TryGetProperty("CaptureDelay", out var delay) || delay.ValueKind != JsonValueKind.Number || Math.Abs(delay.GetDouble()) > 0.0001) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallAsync() => await RunBusy(async () =>
    {
        if (ProfilerServices.IsGameRunning())
            throw new InvalidOperationException("Cyberpunk 2077 is running. Close it before installation.");

        Log("Installing/verifying GRSP 0.5.0...");
        var gr = await Task.Run(() => ProfilerServices.InstallGrsp(cfg.GameDirectory, cfg.Scenario));
        Log($"GRSP: {gr.Mode} -> {gr.Dll}");

        using (var before = await ProfilerServices.CallCetAsync("Status", cfg.GameDirectory))
        {
            var br = before.RootElement;
            if (J(br, "managed") == "True" && J(br, "cet") == "PROFILER_ACTIVE")
            {
                Log("CET profiler already managed; keeping profiler/0-Engine state and updating TOTAL Profiler controls.");
            }
            else
            {
                Log($"Installing CET Runtime Profiler {ProfilerServices.CetVersion}...");
                using var cet = await ProfilerServices.CallCetAsync("Install", cfg.GameDirectory, cfg.ResultsDirectory, cfg.CetCoreOnly);
                Log($"CET: {J(cet.RootElement, "cet")} · 0-Engine mode: {J(cet.RootElement, "managedMode")}");
            }
        }

        Log(await Task.Run(() => ProfilerServices.SyncCetProfilerControls(cfg.GameDirectory)));
        Log(await Task.Run(() => ProfilerServices.ConfigureCetProfilerBinding(cfg.GameDirectory)));

        if (File.Exists(cfg.CapFrameXExe))
            Log(await Task.Run(() => ProfilerServices.ConfigureCapFrameXF11BestEffort(cfg.CapFrameXExe)));
        else
            throw new FileNotFoundException("CapFrameX was not found. Use the bundled copy or select a custom CapFrameX.exe.");

        var liveGrsp = Path.Combine(cfg.GameDirectory, "red4ext", "plugins", "redscript_profiler_alpha.dll");
        if (!File.Exists(liveGrsp) || !string.Equals(ProfilerServices.Sha256(liveGrsp), ProfilerServices.GrspDllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GRSP installation verification failed.");

        using var verify = await ProfilerServices.CallCetAsync("Status", cfg.GameDirectory);
        var vr = verify.RootElement;
        if (J(vr, "cet") != "PROFILER_ACTIVE" || J(vr, "managed") != "True" || J(vr, "controlsPresent") != "True")
            throw new InvalidOperationException("CET installation verification failed.");

        await RefreshStatusAsync();
        if (!installVerified)
            throw new InvalidOperationException("Installation completed, but one or more verification checks are still failing. Expand Technical log for details.");

        Log("Installation VERIFIED. Continue to Measurement.");
    });

    private async Task CollectAsync() => await RunBusy(async () =>
    {
        if (!installVerified)
            throw new InvalidOperationException("Profiler installation is not verified. Return to INSTALL & VERIFY first.");

        var result = await CaptureCollector.CollectAsync(cfg, Log);
        cfg.LastCapture = result.CaptureDirectory;
        cfg.CaptureResetUtc = DateTimeOffset.UtcNow;
        cfg.Save();

        lastCaptureStatus.Text =
            $"Collected ✓  {Path.GetFileName(result.CaptureDirectory)}\r\n" +
            $"Sync precheck: {result.SyncPrecheck}" +
            (result.StartDeltaMs is null ? "" : $" · start Δ {result.StartDeltaMs:F3} ms") +
            (result.DurationDeltaMs is null ? "" : $" · duration Δ {result.DurationDeltaMs:F3} ms");

        await RefreshStatusAsync();
        ProfilerServices.OpenPath(result.CaptureDirectory);

        MessageBox.Show(this,
            $"Collected successfully.\r\n\r\n{Path.GetFileName(result.CaptureDirectory)}\r\n\r\nSync precheck: {result.SyncPrecheck}\r\n\r\nThe capture folder has been opened. Next: click COMPARE RESULTS.",
            ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private async Task CompareAsync() => await RunBusy(async () =>
    {
        if (string.IsNullOrWhiteSpace(cfg.LastCapture) || !Directory.Exists(cfg.LastCapture))
            throw new InvalidOperationException("No collected capture selected. Run COLLECT RESULTS first.");

        var raw = Path.Combine(cfg.LastCapture, "Raw");
        if (!Directory.Exists(raw)) throw new InvalidOperationException("The selected capture has no Raw folder.");

        var combined = Path.Combine(cfg.LastCapture, "Combined");
        if (Directory.Exists(combined)) Directory.Delete(combined, true);
        Directory.CreateDirectory(combined);

        Log("Running native .NET correlator...");
        var result = await Task.Run(() => CorrelatorEngine.Run(raw, combined));
        Log($"Combined report: {result.Report}");

        var manifestPath = Path.Combine(cfg.LastCapture, "CaptureManifest.json");
        Dictionary<string, object?> manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(manifestPath), ProfilerServices.JsonOpts) ?? new();
        }
        catch
        {
            manifest = new();
        }

        manifest["correlated_local"] = DateTimeOffset.Now.ToString("O");
        manifest["combined_report"] = result.Report;
        manifest["native_correlator_sync_quality"] = result.SyncQuality;
        manifest["native_correlator_frametime_correlation"] = result.Correlation;
        manifest["native_correlator_frame_offset"] = result.FrameOffset;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ProfilerServices.JsonOpts) + Environment.NewLine);

        var fullZip = Path.Combine(cfg.LastCapture, Path.GetFileName(cfg.LastCapture) + "_FULL.zip");
        if (File.Exists(fullZip)) File.Delete(fullZip);
        var tempZip = Path.Combine(Path.GetTempPath(), $"GCTP_{Guid.NewGuid():N}.zip");
        await Task.Run(() => ProfilerServices.CreateDirectoryZip(cfg.LastCapture, tempZip));
        File.Move(tempZip, fullZip, true);

        Log($"Portable full package stored inside capture folder: {fullZip}");
        ProfilerServices.OpenPath(result.Report);

        MessageBox.Show(this,
            $"Correlation complete.\r\n\r\nReport:\r\n{result.Report}\r\n\r\nFull capture ZIP:\r\n{fullZip}",
            ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private async Task ResetCaptureStateAsync()
    {
        if (MessageBox.Show(this,
            "Reset the CURRENT capture state?\r\n\r\n" +
            "Use this after an incomplete/failed F11 run.\r\n\r\n" +
            "Current/latest raw GRSP, CET and CapFrameX data is moved to Results\\Discarded. Already-collected Results are untouched.\r\n\r\n" +
            "Cyberpunk 2077 must be closed.",
            ProfilerServices.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await RunBusy(async () =>
        {
            if (ProfilerServices.IsGameRunning())
                throw new InvalidOperationException("Cyberpunk 2077 is running. Close it before resetting capture state.");

            var resetStarted = DateTimeOffset.UtcNow;
            var discardRoot = Path.Combine(cfg.ResultsDirectory, "Discarded", $"Reset_{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(discardRoot);
            var notes = new List<string>();

            var grsp = ProfilerServices.LatestGrspCapture(cfg.GameDirectory, cfg.CaptureResetUtc?.UtcDateTime);
            if (grsp is not null && Directory.Exists(grsp))
            {
                var dstRoot = Path.Combine(discardRoot, "GRSP");
                Directory.CreateDirectory(dstRoot);
                var dst = Path.Combine(dstRoot, Path.GetFileName(grsp));
                Directory.Move(grsp, dst);
                var latestTxt = Path.Combine(cfg.GameDirectory, "red4ext", "plugins", "redscript_profiler_alpha", "RESULTS", "LATEST.txt");
                if (File.Exists(latestTxt)) File.Delete(latestTxt);
                notes.Add($"GRSP: archived {Path.GetFileName(grsp)}.");
            }
            else notes.Add("GRSP: no current capture to reset.");

            try
            {
                var cetRoot = Path.Combine(discardRoot, "CET");
                Directory.CreateDirectory(cetRoot);
                using var r = await ProfilerServices.CallCetAsync("ResetLive", cfg.GameDirectory, cetRoot);
                var archived = J(r.RootElement, "archived");
                notes.Add(string.IsNullOrWhiteSpace(archived) ? "CET: no live CSV results to reset." : $"CET: live CSVs archived to {archived}.");
            }
            catch (Exception ex)
            {
                notes.Add("CET: reset failed · " + ex.Message.Split('\n').Last());
            }

            var cap = ProfilerServices.LatestValidCapXCapture(cfg.CapFrameXResults, cfg.CaptureResetUtc?.UtcDateTime);
            if (cap is not null && File.Exists(cap))
            {
                var dstRoot = Path.Combine(discardRoot, "CapFrameX");
                Directory.CreateDirectory(dstRoot);
                var dst = Path.Combine(dstRoot, Path.GetFileName(cap));
                if (File.Exists(dst))
                    dst = Path.Combine(dstRoot, $"{Path.GetFileNameWithoutExtension(cap)}_{DateTime.Now:HHmmss}{Path.GetExtension(cap)}");
                File.Move(cap, dst);
                notes.Add($"CapFrameX: archived {Path.GetFileName(cap)}.");
            }
            else notes.Add("CapFrameX: no current valid capture to reset.");

            cfg.CaptureResetUtc = resetStarted;
            cfg.Save();

            foreach (var note in notes) Log(note);

            MessageBox.Show(this,
                string.Join("\r\n", notes) + "\r\n\r\nCapture state is clean for the next F11 run.",
                ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private async Task RestoreAllAsync()
    {
        if (MessageBox.Show(this,
            "Restore every TOTAL Profiler change in the Cyberpunk 2077 game folder?\r\n\r\n" +
            "All TOTAL Profiler-managed game files and configuration will be returned to their original state. CapFrameX settings changed by TOTAL Profiler will also be restored.\r\n\r\n" +
            "Any uncollected GRSP, CET, or current CapFrameX capture data will be discarded. Already-collected Results stay.\r\n\r\n" +
            "Cyberpunk 2077 must be closed.",
            ProfilerServices.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        await RunBusy(async () =>
        {
            if (ProfilerServices.IsGameRunning())
                throw new InvalidOperationException("Cyberpunk 2077 is running. Close it before restoring.");

            var notes = new List<string>();
            bool failed = false;

            try
            {
                using var status = await ProfilerServices.CallCetAsync("Status", cfg.GameDirectory);
                if (J(status.RootElement, "managed") == "True")
                {
                    using var r = await ProfilerServices.CallCetAsync("Restore", cfg.GameDirectory, discardLiveOnRestore: true);
                    var discarded = J(r.RootElement, "discardedLiveResultCount");
                    notes.Add("CET / 0-Engine: original files restored; live uncollected CET CSVs discarded" +
                              (string.IsNullOrWhiteSpace(discarded) ? "." : $" ({discarded})."));
                }
                else
                {
                    notes.Add("CET / 0-Engine: no TOTAL Profiler managed state was present.");
                }
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CET / 0-Engine: RESTORE FAILED · " + ex.Message.Split('\n').Last());
            }

            try
            {
                notes.Add("CET binding: " + await Task.Run(() => ProfilerServices.RestoreCetProfilerBinding(cfg.GameDirectory)));
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CET binding: RESTORE FAILED · " + ex.Message);
            }

            try
            {
                notes.Add("GRSP: " + await Task.Run(() => ProfilerServices.RestoreGrsp(cfg.GameDirectory)));
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("GRSP: RESTORE FAILED · " + ex.Message);
            }

            try
            {
                var cap = ProfilerServices.LatestValidCapXCapture(cfg.CapFrameXResults, cfg.CaptureResetUtc?.UtcDateTime);
                if (cap is not null && File.Exists(cap))
                {
                    File.Delete(cap);
                    notes.Add($"CapFrameX: discarded uncollected capture {Path.GetFileName(cap)}.");
                }
                else
                {
                    notes.Add("CapFrameX: no uncollected capture to discard.");
                }
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CapFrameX capture cleanup: FAILED · " + ex.Message);
            }

            try
            {
                notes.Add("CapFrameX settings: " + await Task.Run(() => ProfilerServices.RestoreCapFrameXConfigBestEffort(cfg.CapFrameXExe)));
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CapFrameX settings: RESTORE FAILED · " + ex.Message);
            }

            cfg.CaptureResetUtc = DateTimeOffset.UtcNow;
            cfg.Save();

            foreach (var note in notes) Log(note);
            await RefreshStatusAsync();

            MessageBox.Show(this,
                string.Join("\r\n\r\n", notes) +
                (failed ? "\r\n\r\nOne or more restore steps failed. The app kept the remaining restore state so the operation can be retried safely." :
                          "\r\n\r\nGame files are back to the managed pre-profiler state."),
                ProfilerServices.AppName,
                MessageBoxButtons.OK,
                failed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        });
    }

    private void OpenLatestReportOrFolder()
    {
        SaveConfig();

        if (!string.IsNullOrWhiteSpace(cfg.LastCapture) && Directory.Exists(cfg.LastCapture))
        {
            var combined = Path.Combine(cfg.LastCapture, "Combined", "GRSP_Combined_Report.html");
            if (File.Exists(combined))
            {
                ProfilerServices.OpenPath(combined);
                return;
            }

            var grsp = Path.Combine(cfg.LastCapture, "Raw", "GRSP", "GRSP_Report.html");
            if (File.Exists(grsp))
            {
                ProfilerServices.OpenPath(grsp);
                return;
            }

            ProfilerServices.OpenPath(cfg.LastCapture);
            return;
        }

        ProfilerServices.OpenPath(cfg.ResultsDirectory);
    }

    private void OpenLatest()
    {
        SaveConfig();

        if (!string.IsNullOrWhiteSpace(cfg.LastCapture) && Directory.Exists(cfg.LastCapture))
        {
            ProfilerServices.OpenPath(cfg.LastCapture);
            return;
        }

        if (!Directory.Exists(cfg.ResultsDirectory)) return;

        var p = Directory.EnumerateDirectories(cfg.ResultsDirectory, "Capture_*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (p is null)
        {
            ProfilerServices.OpenPath(cfg.ResultsDirectory);
            return;
        }

        cfg.LastCapture = p;
        cfg.Save();
        ProfilerServices.OpenPath(p);
        UpdateActionState();
    }

    private static string J(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var x)) return "";
        return x.ValueKind switch
        {
            JsonValueKind.String => x.GetString() ?? "",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => x.ToString(),
            _ => x.ToString()
        };
    }
}
