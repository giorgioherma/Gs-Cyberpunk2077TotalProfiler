using System.Diagnostics;
using System.Text.Json;

namespace GsCyberpunkTotalProfiler;

internal sealed class MainForm : Form
{
    private readonly AppConfig cfg;
    private readonly TextBox gameBox = new();
    private readonly TextBox capExeBox = new();
    private readonly TextBox capResultsBox = new();
    private readonly TextBox resultsBox = new();
    private readonly TextBox scenarioBox = new();
    private readonly CheckBox coreOnly = new();
    private readonly Label gameStatus = new();
    private readonly Label grspStatus = new();
    private readonly Label cetStatus = new();
    private readonly Label zeroStatus = new();
    private readonly Label capStatus = new();
    private readonly Label installStatus = new();
    private readonly TextBox logBox = new();
    private readonly Button installButton = new();
    private readonly Button collectButton = new();
    private readonly Button compareButton = new();
    private bool busy;

    public MainForm()
    {
        cfg = AppConfig.Load();
        Text = ProfilerServices.AppName;
        Width = 1160;
        Height = 780;
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BuildUi();
        LoadConfigIntoUi();
        Shown += async (_, _) => await RefreshStatusAsync();
        FormClosing += (_, _) => SaveConfig();
    }

    private void BuildUi()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7, AutoScroll = true };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(outer);

        var titlePanel = new Panel { Dock = DockStyle.Fill, Height = 62 };
        var title = new Label { Text = ProfilerServices.AppName, Font = new Font("Segoe UI Semibold", 18F), AutoSize = true, Location = new Point(0, 0) };
        var sub = new Label { Text = $"GRSP {ProfilerServices.GrspVersion} + CET Runtime Profiler {ProfilerServices.CetVersion} + bundled/external CapFrameX + native correlator {ProfilerServices.CorrelatorVersion}", AutoSize = true, Location = new Point(2, 37) };
        titlePanel.Controls.Add(title); titlePanel.Controls.Add(sub); outer.Controls.Add(titlePanel);

        var setup = Group("Setup");
        var setupGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6, AutoSize = true };
        setupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        setupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        setupGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        AddPathRow(setupGrid, 0, "Cyberpunk 2077 directory", gameBox, BrowseGame);
        AddPathRow(setupGrid, 1, "CapFrameX.exe (bundled / existing)", capExeBox, BrowseCapExe, "Launch", LaunchCapX);
        AddPathRow(setupGrid, 2, "CapFrameX results", capResultsBox, BrowseCapResults);
        AddPathRow(setupGrid, 3, "TOTAL Profiler results", resultsBox, BrowseResults, "Open", () => ProfilerServices.OpenPath(resultsBox.Text));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 7, 0, 0) };
        settings.Controls.Add(new Label { Text = "Shared profiling key:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        settings.Controls.Add(new Label { Text = ProfilerServices.CaptureKey, AutoSize = true, Font = new Font("Segoe UI Semibold", 9F), Margin = new Padding(0, 6, 20, 0) });
        settings.Controls.Add(new Label { Text = "CET result export key:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        settings.Controls.Add(new Label { Text = ProfilerServices.CetExportKey, AutoSize = true, Font = new Font("Segoe UI Semibold", 9F), Margin = new Padding(0, 6, 20, 0) });
        settings.Controls.Add(new Label { Text = "Scenario:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        scenarioBox.Width = 155; settings.Controls.Add(scenarioBox);
        coreOnly.Text = "CET core profiler only — leave 0-Engine untouched"; coreOnly.AutoSize = true; coreOnly.Margin = new Padding(20, 3, 0, 0); settings.Controls.Add(coreOnly);

        var useBundledCapX = new Button { Text = $"Use bundled CapFrameX {ProfilerServices.BundledCapFrameXVersion}", AutoSize = true, Height = 27, Margin = new Padding(20, 0, 0, 0) };
        useBundledCapX.Click += (_, _) => UseBundledCapFrameX();
        settings.Controls.Add(useBundledCapX);

        setupGrid.Controls.Add(settings, 0, 4); setupGrid.SetColumnSpan(settings, 4);

        var capRuntimeNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1060, 0),
            Text = $"Bundled default: CapFrameX {ProfilerServices.BundledCapFrameXVersion} stable portable release — requires .NET 10 Desktop Runtime. Bundled captures: .\\Tools\\CapFrameX\\Portable\\Captures. Browse may link any compatible existing version."
        };
        setupGrid.Controls.Add(capRuntimeNote, 0, 5); setupGrid.SetColumnSpan(capRuntimeNote, 4);

        setup.Controls.Add(setupGrid); outer.Controls.Add(setup);

        var status = Group("Profiler status");
        var sg = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, AutoSize = true };
        sg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); sg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        AddStatusRow(sg, 0, "Game", gameStatus); AddStatusRow(sg, 1, "GRSP", grspStatus); AddStatusRow(sg, 2, "CET profiler", cetStatus); AddStatusRow(sg, 3, "0-Engine / Scheduler", zeroStatus); AddStatusRow(sg, 4, "CapFrameX", capStatus); AddStatusRow(sg, 5, "Install check", installStatus); AddStatusRow(sg, 6, "Capture keys", new Label { AutoSize = true, Text = "F11 shared capture · F12 CET export" });
        var refresh = new Button { Text = "Refresh status", Width = 105, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refresh.Click += async (_, _) => await RefreshStatusAsync(); sg.Controls.Add(refresh, 2, 0); sg.SetRowSpan(refresh, 2);
        status.Controls.Add(sg); outer.Controls.Add(status);

        var actions = Group("Actions");
        var af = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        installButton.Text = "INSTALL PROFILERS"; installButton.Width = 160; installButton.Height = 34; installButton.Click += async (_, _) => await InstallAsync();
        collectButton.Text = "COLLECT RESULTS"; collectButton.Width = 155; collectButton.Height = 34; collectButton.Click += async (_, _) => await CollectAsync();
        compareButton.Text = "COMPARE RESULTS"; compareButton.Width = 155; compareButton.Height = 34; compareButton.Click += async (_, _) => await CompareAsync();
        var openLatest = new Button { Text = "Open latest", Width = 115, Height = 34 }; openLatest.Click += (_, _) => OpenLatest();
        var resetResults = new Button { Text = "RESET RESULT PATHS", Width = 155, Height = 34 }; resetResults.Click += (_, _) => ResetResultPaths();
        var restoreAll = new Button { Text = "RESTORE ORIGINAL STATE", Width = 190, Height = 34 }; restoreAll.Click += async (_, _) => await RestoreAllAsync();
        af.Controls.AddRange([installButton, collectButton, compareButton, openLatest, resetResults, restoreAll]); actions.Controls.Add(af); outer.Controls.Add(actions);

        var workflow = Group("Capture workflow");
        var wf = new Label { AutoSize = true, MaximumSize = new Size(1060, 0), Text = $"1) INSTALL PROFILERS and confirm Install check = VERIFIED ✓.   2) Launch CapFrameX and Cyberpunk 2077.   3) F11 starts GRSP + CET + CapFrameX.   4) F11 stops all three.   5) F12 exports CET CSVs.   6) Close the game.   7) COLLECT RESULTS.   8) COMPARE RESULTS.\r\n\r\nCET note: after first profiler install, bind 'Profiler: START / PAUSE / RESUME' to F11 and 'Profiler: CREATE CSV' to F12 in CET > Bindings.\r\nCapFrameX note: while profiling, Cyberpunk 2077 should be the only app in CapFrameX 'Running processes'. If anything else is listed, move it to the CapFrameX ignore list before capture.\r\nBundled default: CapFrameX {ProfilerServices.BundledCapFrameXVersion}; Browse may link any compatible version." };
        workflow.Controls.Add(wf); outer.Controls.Add(workflow);

        var logGroup = Group("Log");
        logBox.Dock = DockStyle.Fill; logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical; logBox.Font = new Font("Consolas", 9F); logBox.BackColor = SystemColors.Window;
        logGroup.Controls.Add(logBox); outer.Controls.Add(logGroup);

        var footer = new Label { AutoSize = true, Text = "Native .NET self-contained Windows build. CapFrameX may be bundled or linked externally; TOTAL Profiler does not version-lock it.", ForeColor = SystemColors.GrayText };
        outer.Controls.Add(footer);
    }

    private static GroupBox Group(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 5, 0, 5) };

    private static void AddStatusRow(TableLayoutPanel p, int row, string name, Label value)
    {
        p.Controls.Add(new Label { Text = name + ":", AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 0, row);
        value.AutoSize = true; value.Margin = new Padding(0, 4, 0, 4); p.Controls.Add(value, 1, row);
    }

    private static void AddPathRow(TableLayoutPanel p, int row, string label, TextBox box, Action browse, string? extraText = null, Action? extra = null)
    {
        p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 0) }, 0, row);
        box.Dock = DockStyle.Fill; p.Controls.Add(box, 1, row);
        var b = new Button { Text = "Browse...", Dock = DockStyle.Fill, Height = 27 }; b.Click += (_, _) => browse(); p.Controls.Add(b, 2, row);
        if (extraText is not null && extra is not null) { var e = new Button { Text = extraText, Dock = DockStyle.Fill, Height = 27 }; e.Click += (_, _) => extra(); p.Controls.Add(e, 3, row); }
    }

    private void LoadConfigIntoUi()
    {
        // Bundled CapFrameX is the default. A user can explicitly choose an existing
        // installation with Browse; that preference then persists.
        if (cfg.UseBundledCapFrameX && File.Exists(ProfilerServices.BundledCapFrameXExe))
        {
            cfg.CapFrameXExe = ProfilerServices.BundledCapFrameXExe;
            cfg.CapFrameXResults = ProfilerServices.BundledCapFrameXResults;
            Directory.CreateDirectory(cfg.CapFrameXResults); // empty folder is valid before the first capture
            cfg.Save();
        }

        gameBox.Text = cfg.GameDirectory; capExeBox.Text = cfg.CapFrameXExe; capResultsBox.Text = cfg.CapFrameXResults; resultsBox.Text = cfg.ResultsDirectory; scenarioBox.Text = cfg.Scenario; coreOnly.Checked = cfg.CetCoreOnly;
    }
    private void SaveConfig()
    {
        cfg.GameDirectory = gameBox.Text.Trim(); cfg.CapFrameXExe = capExeBox.Text.Trim(); cfg.CapFrameXResults = capResultsBox.Text.Trim(); cfg.ResultsDirectory = resultsBox.Text.Trim(); cfg.Scenario = scenarioBox.Text.Trim(); cfg.CetCoreOnly = coreOnly.Checked; cfg.Save();
    }

    private void BrowseGame() { using var d = new FolderBrowserDialog { Description = "Select Cyberpunk 2077 game directory", SelectedPath = gameBox.Text }; if (d.ShowDialog(this) == DialogResult.OK) { gameBox.Text = d.SelectedPath; SaveConfig(); _ = RefreshStatusAsync(); } }
    private void BrowseCapExe() { using var d = new OpenFileDialog { Title = "Select an existing CapFrameX.exe (any compatible version)", Filter = "CapFrameX|CapFrameX.exe|Executable|*.exe|All files|*.*", FileName = "CapFrameX.exe" }; if (d.ShowDialog(this) == DialogResult.OK) { cfg.UseBundledCapFrameX = false; capExeBox.Text = d.FileName; var detected = ProfilerServices.DetectCapFrameXPath(d.FileName).Captures; if (!string.IsNullOrWhiteSpace(detected)) capResultsBox.Text = detected; SaveConfig(); _ = RefreshStatusAsync(); } }

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
        Directory.CreateDirectory(capResultsBox.Text); // valid even before the first capture exists
        SaveConfig();
        _ = RefreshStatusAsync();
    }
    private void ResetResultPaths()
    {
        string? capTarget = cfg.UseBundledCapFrameX
            ? ProfilerServices.BundledCapFrameXResults
            : ProfilerServices.DetectCapFrameXPath(capExeBox.Text).Captures;

        if (!string.IsNullOrWhiteSpace(capTarget))
        {
            capResultsBox.Text = capTarget;
            Directory.CreateDirectory(capTarget);
        }

        resultsBox.Text = ProfilerServices.DefaultResultsDirectory;
        Directory.CreateDirectory(resultsBox.Text);
        SaveConfig();
        _ = RefreshStatusAsync();

        MessageBox.Show(this,
            string.IsNullOrWhiteSpace(capTarget)
                ? $"TOTAL Profiler results reset to:\r\n{resultsBox.Text}\r\n\r\nCapFrameX results could not be auto-detected; use Browse for that path."
                : $"Result paths reset.\r\n\r\nCapFrameX:\r\n{capResultsBox.Text}\r\n\r\nTOTAL Profiler:\r\n{resultsBox.Text}",
            ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BrowseCapResults() { using var d = new FolderBrowserDialog { Description = "Select CapFrameX capture/results folder", SelectedPath = capResultsBox.Text }; if (d.ShowDialog(this) == DialogResult.OK) { capResultsBox.Text = d.SelectedPath; SaveConfig(); _ = RefreshStatusAsync(); } }
    private void BrowseResults() { using var d = new FolderBrowserDialog { Description = "Select TOTAL Profiler results folder", SelectedPath = resultsBox.Text }; if (d.ShowDialog(this) == DialogResult.OK) { resultsBox.Text = d.SelectedPath; SaveConfig(); } }
    private void LaunchCapX()
    {
        if (!File.Exists(capExeBox.Text))
        {
            MessageBox.Show(this, "Select CapFrameX.exe first.", ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var exe = Path.GetFullPath(capExeBox.Text);
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }
    private void SetBusy(bool value)
    {
        busy = value; installButton.Enabled = !value; collectButton.Enabled = !value; compareButton.Enabled = !value; Cursor = value ? Cursors.WaitCursor : Cursors.Default;
    }
    private async Task RunBusy(Func<Task> action)
    {
        if (busy) return; SetBusy(true);
        try { SaveConfig(); await action(); }
        catch (Exception ex) { Log("ERROR: " + ex); MessageBox.Show(this, ex.Message, ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false); }
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
                string grsp = "NOT INSTALLED", cet = "NOT AVAILABLE", zero = "-", install = "NOT READY · click INSTALL PROFILERS";
                bool grspOk = false, cetOk = false, controlsOk = false;

                if (v.Ok)
                {
                    var dll = Path.Combine(snap.GameDirectory, "red4ext", "plugins", "redscript_profiler_alpha.dll");
                    if (File.Exists(dll))
                    {
                        var h = ProfilerServices.Sha256(dll);
                        grspOk = string.Equals(h, ProfilerServices.GrspDllSha256, StringComparison.OrdinalIgnoreCase);
                        grsp = grspOk ? "INSTALLED ✓ · exact GRSP build verified" : $"OTHER BUILD ({h[..Math.Min(10,h.Length)]}…)";
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
                        cet = $"{cetState} · manager {J(r,"packageVersion")} · managed {managed} · controls {controls} · live CSVs {J(r,"liveResultCount")}";
                        zero = J(r,"zeroEnginePresent") == "True" ? $"{J(r,"zeroEngineInit")} · {J(r,"scheduler")}{(string.IsNullOrWhiteSpace(J(r,"managedMode"))?"":" · managed mode "+J(r,"managedMode"))}" : "Not found — core CET profiling only";
                    }
                    catch (Exception ex) { cet = "STATUS ERROR: " + ex.Message.Split('\n').Last(); }
                }

                string cap = "NOT FOUND";
                if (File.Exists(snap.CapFrameXExe))
                {
                    bool bundled = string.Equals(Path.GetFullPath(snap.CapFrameXExe), Path.GetFullPath(ProfilerServices.BundledCapFrameXExe), StringComparison.OrdinalIgnoreCase);
                    cap = (bundled ? $"BUNDLED {ProfilerServices.BundledCapFrameXVersion} ✓" : "LINKED EXISTING ✓") + (Directory.Exists(snap.CapFrameXResults) ? " · results FOUND ✓" : " · results NOT FOUND");
                }

                if (grspOk && cetOk && controlsOk)
                    install = "VERIFIED ✓ · GRSP + CET profiler + CET controls confirmed";

                return (v, grsp, cet, zero, cap, install);
            });

            gameStatus.Text = (data.v.Ok ? "FOUND ✓ · " : "NOT FOUND · ") + data.v.Message;
            grspStatus.Text = data.grsp;
            cetStatus.Text = data.cet;
            zeroStatus.Text = data.zero;
            capStatus.Text = data.cap;
            installStatus.Text = data.install;
        }
        catch (Exception ex) { Log("Status error: " + ex.Message); }
    }

    private async Task InstallAsync() => await RunBusy(async () =>
    {
        if (ProfilerServices.IsGameRunning()) throw new InvalidOperationException("Cyberpunk 2077 is running. Close it before installation.");
        Log("Installing GRSP 0.5.0...");
        var gr = await Task.Run(() => ProfilerServices.InstallGrsp(cfg.GameDirectory, cfg.Scenario));
        Log($"GRSP: {gr.Mode} -> {gr.Dll}");

        Log($"Installing CET Runtime Profiler {ProfilerServices.CetVersion}...");
        using var cet = await ProfilerServices.CallCetAsync("Install", cfg.GameDirectory, cfg.ResultsDirectory, cfg.CetCoreOnly);
        Log($"CET: {J(cet.RootElement,"cet")} · 0-Engine mode: {J(cet.RootElement,"managedMode")}");

        if (File.Exists(cfg.CapFrameXExe)) Log(await Task.Run(() => ProfilerServices.ConfigureCapFrameXF11BestEffort(cfg.CapFrameXExe)));
        else Log("CapFrameX was not found. The bundled copy may be missing; Browse may link an existing compatible version.");

        // Installation is not declared successful until we can read back the exact
        // GRSP DLL and CET manager state from the game directory.
        var liveGrsp = Path.Combine(cfg.GameDirectory, "red4ext", "plugins", "redscript_profiler_alpha.dll");
        if (!File.Exists(liveGrsp) || !string.Equals(ProfilerServices.Sha256(liveGrsp), ProfilerServices.GrspDllSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GRSP installation verification failed.");

        using var verify = await ProfilerServices.CallCetAsync("Status", cfg.GameDirectory);
        var vr = verify.RootElement;
        if (J(vr, "cet") != "PROFILER_ACTIVE" || J(vr, "managed") != "True" || J(vr, "controlsPresent") != "True")
            throw new InvalidOperationException("CET installation verification failed. TOTAL Profiler did not receive PROFILER_ACTIVE + managed + controlsPresent.");

        Log("Installation verification: GRSP ✓ · CET profiler ✓ · CET controls ✓");
        await RefreshStatusAsync();

        MessageBox.Show(this,
            "Profiler install VERIFIED.\r\n\r\n" +
            "GRSP: installed and exact DLL hash confirmed.\r\n" +
            "CET profiler: PROFILER_ACTIVE and managed state confirmed.\r\n" +
            "CET controls: present.\r\n\r\n" +
            "GRSP uses F11 automatically.\r\nCapFrameX should use F11.\r\n\r\n" +
            "CET requires one binding step in-game:\r\n  Profiler: START / PAUSE / RESUME -> F11\r\n  Profiler: CREATE CSV -> F12",
            ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private async Task CollectAsync() => await RunBusy(async () =>
    {
        var result = await CaptureCollector.CollectAsync(cfg, Log);
        cfg.LastCapture = result.CaptureDirectory; cfg.Save(); ProfilerServices.OpenPath(result.CaptureDirectory);
        var details = $"Collected successfully.\r\n\r\n{result.CaptureDirectory}\r\n\r\nPrecheck: {result.SyncPrecheck}";
        if (result.StartDeltaMs is not null) details += $"\r\nGRSP↔CET start delta: {result.StartDeltaMs:F3} ms";
        if (result.DurationDeltaMs is not null) details += $"\r\nDuration delta: {result.DurationDeltaMs:F3} ms";
        details += "\r\n\r\nClick COMPARE RESULTS to run the native correlator.";
        MessageBox.Show(this, details, ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshStatusAsync();
    });

    private async Task CompareAsync() => await RunBusy(async () =>
    {
        if (string.IsNullOrWhiteSpace(cfg.LastCapture) || !Directory.Exists(cfg.LastCapture)) throw new InvalidOperationException("No collected capture selected. Run COLLECT RESULTS first.");
        var raw = Path.Combine(cfg.LastCapture, "Raw"); if (!Directory.Exists(raw)) throw new InvalidOperationException("The selected capture has no Raw folder.");
        var combined = Path.Combine(cfg.LastCapture, "Combined"); if (Directory.Exists(combined)) Directory.Delete(combined, true); Directory.CreateDirectory(combined);
        Log("Running native .NET correlator...");
        var result = await Task.Run(() => CorrelatorEngine.Run(raw, combined));
        Log($"Combined report: {result.Report}");
        var manifestPath = Path.Combine(cfg.LastCapture, "CaptureManifest.json");
        Dictionary<string,object?> manifest;
        try { manifest = JsonSerializer.Deserialize<Dictionary<string,object?>>(File.ReadAllText(manifestPath), ProfilerServices.JsonOpts) ?? []; } catch { manifest = []; }
        manifest["correlated_local"] = DateTimeOffset.Now.ToString("O"); manifest["combined_report"] = result.Report; manifest["native_correlator_sync_quality"] = result.SyncQuality; manifest["native_correlator_frametime_correlation"] = result.Correlation; manifest["native_correlator_frame_offset"] = result.FrameOffset;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ProfilerServices.JsonOpts) + Environment.NewLine);
        var fullZip = Path.Combine(Path.GetDirectoryName(cfg.LastCapture)!, Path.GetFileName(cfg.LastCapture) + "_FULL.zip");
        await Task.Run(() => ProfilerServices.CreateDirectoryZip(cfg.LastCapture, fullZip));
        Log($"Portable full package: {fullZip}");
        ProfilerServices.OpenPath(result.Report); ProfilerServices.OpenPath(combined);
        MessageBox.Show(this, $"Correlation complete.\r\n\r\nReport:\r\n{result.Report}\r\n\r\nFull shareable capture package:\r\n{fullZip}", ProfilerServices.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    private async Task RestoreAllAsync()
    {
        if (MessageBox.Show(this,
            "Restore everything TOTAL Profiler changed back to its original state?\r\n\r\n" +
            "This restores CET / managed 0-Engine state, GRSP DLL state, and any CapFrameX settings file backed up by TOTAL Profiler.\r\n\r\n" +
            "Capture/result folders are NOT deleted. Cyberpunk 2077 must be closed.",
            ProfilerServices.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        await RunBusy(async () =>
        {
            if (ProfilerServices.IsGameRunning()) throw new InvalidOperationException("Cyberpunk 2077 is running. Close it before restoring.");

            var notes = new List<string>();
            bool failed = false;

            try
            {
                using var status = await ProfilerServices.CallCetAsync("Status", cfg.GameDirectory);
                if (J(status.RootElement, "managed") == "True")
                {
                    var outDir = Path.Combine(cfg.ResultsDirectory, "CET_Restore_Archive");
                    Directory.CreateDirectory(outDir);
                    using var r = await ProfilerServices.CallCetAsync("Restore", cfg.GameDirectory, outDir);
                    var archived = J(r.RootElement, "archived");
                    notes.Add("CET / 0-Engine: restored" + (string.IsNullOrWhiteSpace(archived) ? "." : $" · final CET results archived to {archived}"));
                }
                else notes.Add("CET / 0-Engine: no TOTAL Profiler managed state was present.");
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CET / 0-Engine: RESTORE FAILED · " + ex.Message.Split('\n').Last());
            }

            try
            {
                var msg = await Task.Run(() => ProfilerServices.RestoreGrsp(cfg.GameDirectory));
                notes.Add("GRSP: " + msg);
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("GRSP: RESTORE FAILED · " + ex.Message);
            }

            try
            {
                var msg = await Task.Run(() => ProfilerServices.RestoreCapFrameXConfigBestEffort(cfg.CapFrameXExe));
                notes.Add("CapFrameX: " + msg);
            }
            catch (Exception ex)
            {
                failed = true;
                notes.Add("CapFrameX: RESTORE FAILED · " + ex.Message);
            }

            foreach (var note in notes) Log(note);
            await RefreshStatusAsync();

            MessageBox.Show(this, string.Join("\r\n\r\n", notes), ProfilerServices.AppName,
                MessageBoxButtons.OK, failed ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        });
    }

    private void OpenLatest()
    {
        SaveConfig(); if (!string.IsNullOrWhiteSpace(cfg.LastCapture) && Directory.Exists(cfg.LastCapture)) ProfilerServices.OpenPath(cfg.LastCapture); else if (Directory.Exists(cfg.ResultsDirectory)) { var p = Directory.EnumerateDirectories(cfg.ResultsDirectory,"Capture_*",SearchOption.TopDirectoryOnly).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault(); if (p is not null) { cfg.LastCapture = p; cfg.Save(); ProfilerServices.OpenPath(p); } else ProfilerServices.OpenPath(cfg.ResultsDirectory); }
    }

    private static string J(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var x)) return "";
        return x.ValueKind switch { JsonValueKind.String => x.GetString() ?? "", JsonValueKind.True => "True", JsonValueKind.False => "False", JsonValueKind.Number => x.ToString(), _ => x.ToString() };
    }
}
