param([string]$GameRoot = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$CoreScript = Join-Path $PackageRoot "CET_Manager_Core.ps1"
$Manifest = Get-Content -LiteralPath (Join-Path $PackageRoot "manifest.json") -Raw | ConvertFrom-Json
$ResultsRoot = Join-Path $PackageRoot "RESULTS"
$NL = [Environment]::NewLine

function Test-GameRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    return Test-Path -LiteralPath (Join-Path $Root "bin\x64\plugins") -PathType Container
}

function Find-InitialRoot {
    $candidates = @()
    if ($GameRoot) { $candidates += $GameRoot }
    $candidates += @(
        "D:\Games\PC\Cyberpunk 2077",
        "C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077",
        "C:\Program Files\Steam\steamapps\common\Cyberpunk 2077",
        "C:\GOG Games\Cyberpunk 2077"
    )
    foreach ($candidate in $candidates) {
        if (Test-GameRoot $candidate) { return $candidate }
    }
    return ""
}

function Invoke-Core([string]$Action, [string]$Root, [bool]$CoreOnly = $false) {
    if (!(Test-Path -LiteralPath $CoreScript -PathType Leaf)) { throw "CET_Manager_Core.ps1 is missing." }

    $parts = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"' + $CoreScript + '"'),
        "-Action", $Action,
        "-GameRoot", ('"' + $Root + '"')
    )
    if ($CoreOnly) { $parts += "-CoreProfilerOnly" }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = ($parts -join " ")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    if ($proc.ExitCode -ne 0) {
        $clean = @($stderr -split "[\r\n]+" | ForEach-Object { $_.Trim() } | Where-Object {
            $_ -and !($_ -like "At *") -and !($_ -like "+ CategoryInfo*") -and
            !($_ -like "+ FullyQualifiedErrorId*") -and !($_ -like "+ *")
        })
        if ($clean.Count -gt 0) { throw $clean[0] }
        throw (($stderr + $NL + $stdout).Trim())
    }

    $jsonLine = @($stdout -split "[\r\n]+" | Where-Object { $_.TrimStart().StartsWith("{") }) | Select-Object -Last 1
    if (!$jsonLine) { throw "Standalone CET core returned no JSON status." }
    return ($jsonLine | ConvertFrom-Json)
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "CET Runtime Profiler Manager - v$($Manifest.packageVersion)"
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(820, 625)
$form.MinimumSize = New-Object System.Drawing.Size(820, 625)
$form.Font = New-Object System.Drawing.Font("Segoe UI", 9)

$lblPath = New-Object System.Windows.Forms.Label
$lblPath.Text = "Cyberpunk 2077 folder"
$lblPath.Location = New-Object System.Drawing.Point(20, 18)
$lblPath.AutoSize = $true
$form.Controls.Add($lblPath)

$txtPath = New-Object System.Windows.Forms.TextBox
$txtPath.Location = New-Object System.Drawing.Point(20, 42)
$txtPath.Size = New-Object System.Drawing.Size(665, 24)
$txtPath.Text = Find-InitialRoot
$form.Controls.Add($txtPath)

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = "Browse..."
$btnBrowse.Location = New-Object System.Drawing.Point(695, 40)
$btnBrowse.Size = New-Object System.Drawing.Size(85, 28)
$form.Controls.Add($btnBrowse)

$group = New-Object System.Windows.Forms.GroupBox
$group.Text = "Status"
$group.Location = New-Object System.Drawing.Point(20, 82)
$group.Size = New-Object System.Drawing.Size(760, 230)
$form.Controls.Add($group)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = New-Object System.Drawing.Point(18, 28)
$lblStatus.Size = New-Object System.Drawing.Size(720, 145)
$lblStatus.Font = New-Object System.Drawing.Font("Consolas", 10)
$group.Controls.Add($lblStatus)

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Text = "REFRESH STATUS"
$btnRefresh.Location = New-Object System.Drawing.Point(625, 184)
$btnRefresh.Size = New-Object System.Drawing.Size(112, 28)
$group.Controls.Add($btnRefresh)

$compatGroup = New-Object System.Windows.Forms.GroupBox
$compatGroup.Text = "0-Engine / Scheduler compatibility"
$compatGroup.Location = New-Object System.Drawing.Point(20, 322)
$compatGroup.Size = New-Object System.Drawing.Size(760, 72)
$form.Controls.Add($compatGroup)

$chkCoreOnly = New-Object System.Windows.Forms.CheckBox
$chkCoreOnly.Text = "Core profiler only - leave 0-Engine untouched"
$chkCoreOnly.Location = New-Object System.Drawing.Point(18, 22)
$chkCoreOnly.Size = New-Object System.Drawing.Size(315, 24)
$compatGroup.Controls.Add($chkCoreOnly)

$lblCompat = New-Object System.Windows.Forms.Label
$lblCompat.Text = "Use this only if Scheduler integration cannot safely patch the installed 0-Engine. Core profiling still works and 0-Engine remains exactly as installed."
$lblCompat.Location = New-Object System.Drawing.Point(335, 14)
$lblCompat.Size = New-Object System.Drawing.Size(400, 50)
$compatGroup.Controls.Add($lblCompat)

$btnInstall = New-Object System.Windows.Forms.Button
$btnInstall.Text = "INSTALL PROFILER"
$btnInstall.Location = New-Object System.Drawing.Point(20, 409)
$btnInstall.Size = New-Object System.Drawing.Size(230, 44)
$form.Controls.Add($btnInstall)

$btnCollect = New-Object System.Windows.Forms.Button
$btnCollect.Text = "COLLECT RESULTS / CLEAR LIVE"
$btnCollect.Location = New-Object System.Drawing.Point(265, 409)
$btnCollect.Size = New-Object System.Drawing.Size(275, 44)
$form.Controls.Add($btnCollect)

$btnRestore = New-Object System.Windows.Forms.Button
$btnRestore.Text = "RESTORE ORIGINAL STATE"
$btnRestore.Location = New-Object System.Drawing.Point(555, 409)
$btnRestore.Size = New-Object System.Drawing.Size(225, 44)
$form.Controls.Add($btnRestore)

$btnOpen = New-Object System.Windows.Forms.Button
$btnOpen.Text = "Open Results Folder"
$btnOpen.Location = New-Object System.Drawing.Point(20, 468)
$btnOpen.Size = New-Object System.Drawing.Size(230, 36)
$form.Controls.Add($btnOpen)

$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Location = New-Object System.Drawing.Point(20, 520)
$lblInfo.Size = New-Object System.Drawing.Size(760, 62)
$lblInfo.Text = "One capture key: F11 starts a fresh measurement; F11 again stops it and exports CSVs. Game-side install/restore state survives closing this Manager and is the same state TOTAL Profiler consumes."
$form.Controls.Add($lblInfo)

function Show-Error([string]$Message) { [System.Windows.Forms.MessageBox]::Show($Message, "CET Runtime Profiler", "OK", "Error") | Out-Null }
function Show-Info([string]$Message) { [System.Windows.Forms.MessageBox]::Show($Message, "CET Runtime Profiler", "OK", "Information") | Out-Null }

function Refresh-Status {
    $root = $txtPath.Text.Trim()
    if (!(Test-GameRoot $root)) {
        $lblStatus.Text = "Game:       NOT FOUND" + $NL + "CET:        -" + $NL + "0-Engine:   -" + $NL + "Init:       -" + $NL + "Scheduler:  -" + $NL + "Controls:   -" + $NL + "F11:        -" + $NL + "Live CSVs:  -"
        $btnInstall.Enabled = $false
        $btnCollect.Enabled = $false
        $btnRestore.Enabled = $false
        $chkCoreOnly.Enabled = $false
        return
    }

    try {
        $status = Invoke-Core "Status" $root
        $zeroText = if ($status.zeroEnginePresent) { [string]$status.zeroEngineInit } else { "NOT FOUND - CORE PROFILER ONLY" }
        $f11Text = if ($status.f11Binding) { "F11 START / STOP + AUTO EXPORT" } else { "NOT CONFIGURED" }
        $controlsText = if ($status.controlsPresent) { "PRESENT" } else { "NOT INSTALLED" }
        $managedText = if ($status.managed) { "YES" } else { "NO" }

        $lblStatus.Text =
            "Game:       FOUND" + $NL +
            "CET:        $($status.cet)" + $NL +
            "0-Engine:   $zeroText" + $NL +
            "Init:       $($status.zeroEngineInit)" + $NL +
            "Scheduler:  $($status.scheduler)" + $NL +
            "Controls:   $controlsText" + $NL +
            "F11:        $f11Text" + $NL +
            "Live CSVs:  $($status.liveResultCount)    Managed install: $managedText"

        $cetAllowed = ($status.cet -eq "OFFICIAL" -or $status.cet -eq "PROFILER_ACTIVE")
        $btnInstall.Enabled = ($cetAllowed -and !$status.managed -and [int]$status.liveResultCount -eq 0)
        $btnCollect.Enabled = ([int]$status.liveResultCount -gt 0)
        $btnRestore.Enabled = [bool]$status.managed
        $chkCoreOnly.Enabled = ([bool]$status.zeroEnginePresent -and !$status.managed)
    }
    catch {
        $lblStatus.Text = "STATUS ERROR:" + $NL + $_.Exception.Message
        $btnInstall.Enabled = $false
        $btnCollect.Enabled = $false
        $btnRestore.Enabled = $false
    }
}

$btnBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select the Cyberpunk 2077 game folder"
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtPath.Text = $dialog.SelectedPath
        Refresh-Status
    }
})
$txtPath.Add_TextChanged({ Refresh-Status })
$btnRefresh.Add_Click({ Refresh-Status })
$chkCoreOnly.Add_CheckedChanged({ Refresh-Status })

$btnInstall.Add_Click({
    try {
        $status = Invoke-Core "Install" $txtPath.Text.Trim() ([bool]$chkCoreOnly.Checked)
        Refresh-Status
        Show-Info ("Profiler installed." + $NL + $NL + "F11 #1 = START" + $NL + "F11 #2 = STOP + AUTO EXPORT" + $NL + $NL + "0-Engine mode: " + $status.managedMode)
    }
    catch { Show-Error $_.Exception.Message; Refresh-Status }
})

$btnCollect.Add_Click({
    try {
        $result = Invoke-Core "Collect" $txtPath.Text.Trim()
        Refresh-Status
        Show-Info ("Results archived successfully." + $NL + $NL + "Archive folder:" + $NL + $result.destination + $NL + $NL + "Live CET profiler CSVs were cleared.")
    }
    catch { Show-Error $_.Exception.Message; Refresh-Status }
})

$btnRestore.Add_Click({
    try {
        $result = Invoke-Core "Restore" $txtPath.Text.Trim()
        Refresh-Status
        $extra = if ($result.archived) { $NL + $NL + "Final live results were archived to:" + $NL + $result.archived } else { "" }
        Show-Info ("Original CET / 0-Engine files and the previous CET binding state were restored." + $extra)
    }
    catch { Show-Error $_.Exception.Message; Refresh-Status }
})

$btnOpen.Add_Click({
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    Start-Process explorer.exe $ResultsRoot
})

$form.Add_Shown({ Refresh-Status })
$form.Add_Activated({ Refresh-Status })
[void]$form.ShowDialog()
