param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("Status","Install","Collect","ResetLive","Restore")]
    [string]$Action,

    [Parameter(Mandatory=$true)]
    [string]$GameRoot,

    [string]$ResultsRoot = "",
    [switch]$CoreProfilerOnly
)

$ErrorActionPreference = "Stop"
$PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ManifestPath = Join-Path $PackageRoot "manifest.json"
$Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$PayloadRoot = Join-Path $PackageRoot "PAYLOAD"
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $ResultsRoot = Join-Path $PackageRoot "RESULTS"
}
$F11BindCode = 34339947158700032

function Get-Sha256([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-GameClosed {
    $p = Get-Process -Name "Cyberpunk2077" -ErrorAction SilentlyContinue
    if ($p) { throw "Cyberpunk 2077 is running. Close the game before installing, collecting, or restoring." }
}

function Get-Paths([string]$Root) {
    $plugins = Join-Path $Root "bin\x64\plugins"
    $cetRoot = Join-Path $plugins "cyber_engine_tweaks"
    $mods = Join-Path $cetRoot "mods"
    $stateRoot = Join-Path $plugins ".cet_runtime_profiler"
    $zeroRoot = Join-Path $mods "0-Engine"
    return [pscustomobject]@{
        Root = $Root
        Plugins = $plugins
        CETRoot = $cetRoot
        Mods = $mods
        LiveAsi = Join-Path $plugins "cyber_engine_tweaks.asi"
        ZeroRoot = $zeroRoot
        ZeroInit = Join-Path $zeroRoot "init.lua"
        ZeroScheduler = Join-Path $zeroRoot "modules\Scheduler.lua"
        ZeroAdaptiveScheduler = Join-Path $zeroRoot "modules\CETProfilerScheduler.lua"
        Controls = Join-Path $mods "CETProfilerControls"
        Bindings = Join-Path $cetRoot "bindings.json"
        LegacyTotalBindingState = Join-Path $cetRoot ".gctp_cet_profiler_binding_state.json"
        StateRoot = $stateRoot
        StateFile = Join-Path $stateRoot "state.json"
        BackupAsi = Join-Path $stateRoot "cyber_engine_tweaks.ORIGINAL.asi"
        BackupZeroRoot = Join-Path $stateRoot "0-Engine.FULL.ORIGINAL"
        BackupZeroInit = Join-Path $stateRoot "0-Engine.init.ORIGINAL.lua"
        BackupZeroScheduler = Join-Path $stateRoot "0-Engine.Scheduler.ORIGINAL.lua"
        BackupZeroAdaptiveScheduler = Join-Path $stateRoot "0-Engine.CETProfilerScheduler.ORIGINAL.lua"
        # Compatibility only: TOTAL Profiler 0.2.19 could snapshot a pre-existing
        # CETProfilerControls folder here. New standalone installs own this namespace.
        LegacyBackupControlsRoot = Join-Path $stateRoot "CETProfilerControls.ORIGINAL"
    }
}

function Get-StringSha256([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Get-DirectoryFingerprint([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { return $null }
    $base = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]"\/")
    $records = @()
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Sort-Object FullName)
    foreach ($f in $files) {
        $full = [IO.Path]::GetFullPath($f.FullName)
        $rel = $full.Substring($base.Length).TrimStart([char[]]"\/").Replace("\", "/")
        $records += ($rel + "`t" + (Get-Sha256 $f.FullName))
    }
    return Get-StringSha256 ($records -join "`n")
}

function Get-State($Paths) {
    if (!(Test-Path -LiteralPath $Paths.StateFile)) { return $null }
    try { return Get-Content -LiteralPath $Paths.StateFile -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Save-State($Paths, $State) {
    New-Item -ItemType Directory -Path $Paths.StateRoot -Force | Out-Null
    $State | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Paths.StateFile -Encoding UTF8
}

function Read-BindingsObject($Paths) {
    if (!(Test-Path -LiteralPath $Paths.Bindings -PathType Leaf)) { return [pscustomobject]@{} }
    try {
        $root = Get-Content -LiteralPath $Paths.Bindings -Raw | ConvertFrom-Json
        if ($null -eq $root) { return [pscustomobject]@{} }
        return $root
    }
    catch { throw "CET bindings.json is invalid JSON. No binding changes were made." }
}

function Get-ProfilerBindingSnapshot($Paths) {
    $fileExisted = Test-Path -LiteralPath $Paths.Bindings -PathType Leaf
    $root = Read-BindingsObject $Paths
    $prop = $root.PSObject.Properties["CETProfilerControls"]
    $hadNode = $null -ne $prop
    $nodeJson = if ($hadNode) { $prop.Value | ConvertTo-Json -Depth 20 -Compress } else { "" }
    return [ordered]@{
        fileExistedBefore = [bool]$fileExisted
        hadNode = [bool]$hadNode
        originalNodeJson = [string]$nodeJson
        installedToggle = [Int64]$F11BindCode
    }
}

function Set-ProfilerDefaultBinding($Paths) {
    $root = Read-BindingsObject $Paths
    $prop = $root.PSObject.Properties["CETProfilerControls"]
    $node = if ($null -ne $prop -and $null -ne $prop.Value) { $prop.Value } else { [pscustomobject]@{} }
    $node | Add-Member -NotePropertyName "CETProfiler_Toggle" -NotePropertyValue ([Int64]$F11BindCode) -Force
    $dump = $node.PSObject.Properties["CETProfiler_Dump"]
    if ($null -ne $dump) { $node.PSObject.Properties.Remove("CETProfiler_Dump") }
    $root | Add-Member -NotePropertyName "CETProfilerControls" -NotePropertyValue $node -Force
    $root | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Paths.Bindings -Encoding UTF8
}

function Test-ProfilerF11Binding($Paths) {
    if (!(Test-Path -LiteralPath $Paths.Bindings -PathType Leaf)) { return $false }
    try {
        $root = Read-BindingsObject $Paths
        $prop = $root.PSObject.Properties["CETProfilerControls"]
        if ($null -eq $prop -or $null -eq $prop.Value) { return $false }
        $toggle = $prop.Value.PSObject.Properties["CETProfiler_Toggle"]
        if ($null -eq $toggle) { return $false }
        return ([Int64]$toggle.Value -eq [Int64]$F11BindCode)
    }
    catch { return $false }
}

function Restore-ProfilerBinding($Paths, $BindingState) {
    # alpha6c stores binding rollback data inside the persistent CET profiler state.
    # TOTAL Profiler <=0.2.19 used a separate state file; consume that once so an
    # existing managed install can still be restored correctly after upgrading.
    if ($null -eq $BindingState -and (Test-Path -LiteralPath $Paths.LegacyTotalBindingState -PathType Leaf)) {
        try {
            $legacy = Get-Content -LiteralPath $Paths.LegacyTotalBindingState -Raw | ConvertFrom-Json
            $legacyNodeJson = if ([bool]$legacy.hadNode -and $null -ne $legacy.node) {
                $legacy.node | ConvertTo-Json -Depth 20 -Compress
            } else { "" }
            $BindingState = [pscustomobject]@{
                fileExistedBefore = $(if ($null -ne $legacy.bindingsFileExisted) { [bool]$legacy.bindingsFileExisted } else { $true })
                hadNode = [bool]$legacy.hadNode
                originalNodeJson = [string]$legacyNodeJson
            }
        }
        catch { throw "Legacy TOTAL Profiler CET binding state is invalid; binding restore was not attempted." }
    }

    $root = Read-BindingsObject $Paths
    $existing = $root.PSObject.Properties["CETProfilerControls"]
    if ($null -ne $existing) { $root.PSObject.Properties.Remove("CETProfilerControls") }

    if ($null -ne $BindingState -and [bool]$BindingState.hadNode) {
        $node = ([string]$BindingState.originalNodeJson | ConvertFrom-Json)
        $root | Add-Member -NotePropertyName "CETProfilerControls" -NotePropertyValue $node -Force
    }

    $fileExistedBefore = if ($null -ne $BindingState) { [bool]$BindingState.fileExistedBefore } else { $true }
    $props = @($root.PSObject.Properties).Count
    if (!$fileExistedBefore -and $props -eq 0) {
        if (Test-Path -LiteralPath $Paths.Bindings -PathType Leaf) { Remove-Item -LiteralPath $Paths.Bindings -Force }
    }
    else {
        $root | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Paths.Bindings -Encoding UTF8
    }

    if (Test-Path -LiteralPath $Paths.LegacyTotalBindingState -PathType Leaf) {
        Remove-Item -LiteralPath $Paths.LegacyTotalBindingState -Force
    }
}

function Test-GameRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $p = Get-Paths $Root
    return (Test-Path -LiteralPath $p.Plugins -PathType Container)
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
    foreach ($c in $candidates) { if (Test-GameRoot $c) { return $c } }
    return ""
}

function Get-LiveResults($Paths) {
    $found = @()
    foreach ($name in $Manifest.liveResultFiles) {
        $p = Join-Path $Paths.CETRoot ([string]$name)
        if (Test-Path -LiteralPath $p -PathType Leaf) { $found += $p }
    }
    return @($found)
}

function Remove-LiveResults($Paths) {
    $found = @(Get-LiveResults $Paths)
    $removed = 0
    foreach ($src in $found) {
        if (Test-Path -LiteralPath $src -PathType Leaf) {
            Remove-Item -LiteralPath $src -Force
            $removed++
        }
    }
    return $removed
}

function Copy-DirectoryExact([string]$Source, [string]$Destination) {
    if (!(Test-Path -LiteralPath $Source -PathType Container)) { throw "Source directory not found: $Source" }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Collect-ResultsInternal($Paths, [bool]$AllowEmpty = $false) {
    $found = @(Get-LiveResults $Paths)
    if ($found.Count -eq 0) {
        if ($AllowEmpty) { return $null }
        throw "No live profiler CSV files were found in the CET folder."
    }

    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dest = Join-Path $ResultsRoot $stamp
    $suffix = 1
    while (Test-Path -LiteralPath $dest) {
        $dest = Join-Path $ResultsRoot ($stamp + "-" + $suffix)
        $suffix++
    }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    foreach ($src in $found) {
        $dst = Join-Path $dest (Split-Path -Leaf $src)
        Copy-Item -LiteralPath $src -Destination $dst -Force
        $a = Get-Sha256 $src
        $b = Get-Sha256 $dst
        if (!$a -or $a -ne $b) { throw "Result verification failed for $(Split-Path -Leaf $src). Live files were NOT cleared." }
    }

    foreach ($src in $found) { Remove-Item -LiteralPath $src -Force }
    return $dest
}

function Test-ZeroEngineSchedulerIntegrated([string]$InitPath) {
    if (!(Test-Path -LiteralPath $InitPath -PathType Leaf)) { return $false }
    $text = Get-Content -LiteralPath $InitPath -Raw
    $marker = [string]$Manifest.zeroEngine.profilerBridgeMarker
    if ($marker -and $text.Contains($marker)) { return $false }

    $hasRequire = $text -match 'require\s*\(\s*[''"]modules/Scheduler[''"]\s*\)'
    $hasInit = $text -match "Scheduler\.Init\s*\("
    $hasUpdate = $text -match "Scheduler\.Update\s*[,\(]"
    $hasEngineApi = $text -match "Engine\.Schedule"
    $hasModApi = $text -match "Mod\.Schedule"
    return ($hasRequire -and $hasInit -and $hasUpdate -and $hasEngineApi -and $hasModApi)
}

function Test-ZeroEngineAdaptiveCompatible([string]$InitPath) {
    if (!(Test-Path -LiteralPath $InitPath -PathType Leaf)) { return $false }
    $text = Get-Content -LiteralPath $InitPath -Raw
    if ($text.Contains([string]$Manifest.zeroEngine.profilerBridgeMarker)) { return $true }

    # Adaptive injection deliberately requires only a stable Engine export anchor.
    # We patch the user's own file immediately before the final `return Engine`.
    $hasReturn = [regex]::Matches($text, '(?m)^[ \t]*return[ \t]+Engine[ \t]*$').Count -gt 0
    $hasEngineTable = ($text -match '(?m)^\s*(local\s+)?Engine\s*=\s*\{')
    return ($hasReturn -and $hasEngineTable)
}

function Get-ZeroInitState($Paths) {
    if (!(Test-Path -LiteralPath $Paths.ZeroInit -PathType Leaf)) {
        return [pscustomobject]@{ Kind = "absent"; Text = "0-ENGINE NOT FOUND" }
    }
    $text = Get-Content -LiteralPath $Paths.ZeroInit -Raw
    if ($text.Contains([string]$Manifest.zeroEngine.profilerBridgeMarker)) {
        return [pscustomobject]@{ Kind = "profiler-bridge"; Text = "ADAPTIVE PROFILER BRIDGE PRESENT" }
    }
    if (Test-ZeroEngineSchedulerIntegrated $Paths.ZeroInit) {
        return [pscustomobject]@{ Kind = "integrated"; Text = "EXISTING SCHEDULER INTEGRATION" }
    }
    if (Test-ZeroEngineAdaptiveCompatible $Paths.ZeroInit) {
        return [pscustomobject]@{ Kind = "adaptive"; Text = "ADAPTIVE BRIDGE AVAILABLE" }
    }
    return [pscustomobject]@{ Kind = "unsafe"; Text = "STRUCTURE NOT RECOGNIZED" }
}

function Get-ZeroSchedulerState($Paths) {
    $aware = ([string]$Manifest.zeroEngine.profilerSchedulerSha256).ToLowerInvariant()
    if (!(Test-Path -LiteralPath $Paths.ZeroScheduler -PathType Leaf)) {
        return [pscustomobject]@{ Kind = "absent"; Text = "ABSENT - WILL ADD"; Hash = $null }
    }
    $h = Get-Sha256 $Paths.ZeroScheduler
    if ($h -eq $aware) { return [pscustomobject]@{ Kind = "aware"; Text = "PROFILER-AWARE"; Hash = $h } }
    return [pscustomobject]@{ Kind = "other"; Text = "PRESENT - WILL BACKUP + REPLACE"; Hash = $h }
}

function Get-ZeroAdaptiveSchedulerState($Paths) {
    $aware = ([string]$Manifest.zeroEngine.profilerAdaptiveSchedulerSha256).ToLowerInvariant()
    if (!(Test-Path -LiteralPath $Paths.ZeroAdaptiveScheduler -PathType Leaf)) {
        return [pscustomobject]@{ Kind = "absent"; Text = "WILL ADD CETProfilerScheduler"; Hash = $null }
    }
    $h = Get-Sha256 $Paths.ZeroAdaptiveScheduler
    if ($h -eq $aware) { return [pscustomobject]@{ Kind = "aware"; Text = "CETProfilerScheduler READY"; Hash = $h } }
    return [pscustomobject]@{ Kind = "other"; Text = "CUSTOM CETProfilerScheduler - WILL BACKUP"; Hash = $h }
}

function Add-AdaptiveProfilerSchedulerBridge([string]$InitPath) {
    $text = Get-Content -LiteralPath $InitPath -Raw
    $marker = [string]$Manifest.zeroEngine.profilerBridgeMarker
    if ($text.Contains($marker)) { return }

    if (!(Test-ZeroEngineAdaptiveCompatible $InitPath)) {
        throw "0-Engine init.lua structure is not recognized as safe for adaptive Scheduler injection. No 0-Engine files were changed. Use 'Core profiler only - leave 0-Engine untouched' instead."
    }

    $matches = [regex]::Matches($text, '(?m)^[ \t]*return[ \t]+Engine[ \t]*$')
    $m = $matches[$matches.Count - 1]

    $bridge = @'

-- CET_RUNTIME_PROFILER_ADAPTIVE_SCHEDULER_BEGIN v2
-- Temporary profiler integration. Profiler Manager restores this exact init.lua from backup.
-- The profiler module has a unique filename so it cannot collide with 0-Engine's own Scheduler.lua.
local __CETRP_Scheduler = require('modules/CETProfilerScheduler')
__CETRP_Scheduler.Init(nil, nil)

local function __CETRP_NormalizeOptions(owner, options, fn)
    if type(options) == "function" then
        fn = options
        options = {}
    end
    local copy = {}
    for k, v in pairs(options or {}) do copy[k] = v end
    copy.owner = copy.owner or owner or "unscoped"
    return copy, fn
end

if type(Engine.Schedule) ~= "table" then
    Engine.Schedule = {}
    function Engine.Schedule.EveryFrame(options, fn)
        options, fn = __CETRP_NormalizeOptions("unscoped", options, fn)
        return __CETRP_Scheduler.EveryFrame(options, fn)
    end
    function Engine.Schedule.EveryFrames(interval, options, fn)
        options, fn = __CETRP_NormalizeOptions("unscoped", options, fn)
        return __CETRP_Scheduler.EveryFrames(interval, options, fn)
    end
    function Engine.Schedule.Every(seconds, options, fn)
        options, fn = __CETRP_NormalizeOptions("unscoped", options, fn)
        return __CETRP_Scheduler.Every(seconds, options, fn)
    end
    function Engine.Schedule.After(seconds, options, fn)
        options, fn = __CETRP_NormalizeOptions("unscoped", options, fn)
        return __CETRP_Scheduler.After(seconds, options, fn)
    end
    function Engine.Schedule.NextTick(options, fn)
        options, fn = __CETRP_NormalizeOptions("unscoped", options, fn)
        return __CETRP_Scheduler.NextTick(options, fn)
    end
    Engine.Schedule.GetInfo = __CETRP_Scheduler.GetInfo
end

if type(Engine.Register) == "function" then
    local __CETRP_OriginalRegister = Engine.Register
    Engine.Register = function(name)
        local Mod = __CETRP_OriginalRegister(name)
        if type(Mod) == "table" and type(Mod.Schedule) ~= "table" then
            Mod.Schedule = {}

            local function scopedArgs(options, fn)
                return __CETRP_NormalizeOptions(name, options, fn)
            end

            local function scopedCallback(fn)
                return function(...)
                    if type(Engine.IsModEnabled) == "function" then
                        local okEnabled, enabled = pcall(Engine.IsModEnabled, name)
                        if okEnabled and enabled == false then return end
                    end
                    return fn(...)
                end
            end

            function Mod.Schedule.EveryFrame(options, fn)
                options, fn = scopedArgs(options, fn)
                return __CETRP_Scheduler.EveryFrame(options, scopedCallback(fn))
            end
            function Mod.Schedule.EveryFrames(interval, options, fn)
                options, fn = scopedArgs(options, fn)
                return __CETRP_Scheduler.EveryFrames(interval, options, scopedCallback(fn))
            end
            function Mod.Schedule.Every(seconds, options, fn)
                options, fn = scopedArgs(options, fn)
                return __CETRP_Scheduler.Every(seconds, options, scopedCallback(fn))
            end
            function Mod.Schedule.After(seconds, options, fn)
                options, fn = scopedArgs(options, fn)
                return __CETRP_Scheduler.After(seconds, options, scopedCallback(fn))
            end
            function Mod.Schedule.NextTick(options, fn)
                options, fn = scopedArgs(options, fn)
                return __CETRP_Scheduler.NextTick(options, scopedCallback(fn))
            end
        end
        return Mod
    end
end

local __CETRP_Frame = 0
local __CETRP_GameClock = 0
local __CETRP_LastRealClock = os.clock()
registerForEvent("onUpdate", function(delta)
    delta = tonumber(delta) or 0
    __CETRP_Frame = __CETRP_Frame + 1
    __CETRP_GameClock = __CETRP_GameClock + delta

    local realClock = os.clock()
    local realDelta = realClock - __CETRP_LastRealClock
    __CETRP_LastRealClock = realClock

    local playing = true
    if type(Engine.IsPlaying) == "function" then
        local okPlaying, value = pcall(Engine.IsPlaying)
        if okPlaying then playing = value == true end
    end

    local state = nil
    if type(Engine.GetState) == "function" then
        local okState, value = pcall(Engine.GetState)
        if okState then state = value end
    end

    local player = nil
    if type(Engine.GetPlayer) == "function" then
        local okPlayer, value = pcall(Engine.GetPlayer)
        if okPlayer then player = value end
    end

    pcall(__CETRP_Scheduler.Update, {
        frame = __CETRP_Frame,
        dt = delta,
        realDt = realDelta,
        elapsed = delta,
        now = __CETRP_GameClock,
        generation = 0,
        playing = playing,
        inMenu = state and state.inMenu or false,
        player = player,
        state = state
    })
end)
-- CET_RUNTIME_PROFILER_ADAPTIVE_SCHEDULER_END v2

'@

    $patched = $text.Substring(0, $m.Index) + $bridge + $text.Substring($m.Index)
    Set-Content -LiteralPath $InitPath -Value $patched -Encoding UTF8 -NoNewline
}

function Validate-Core($Paths) {
    if (!(Test-Path -LiteralPath $Paths.LiveAsi -PathType Leaf)) { throw "CET ASI not found: $($Paths.LiveAsi)" }
}

function Install-Profiler($Paths, [bool]$CoreProfilerOnly) {
    Assert-GameClosed
    Validate-Core $Paths

    if (Test-Path -LiteralPath $Paths.StateRoot) { throw "Profiler manager state already exists. Restore/clean the previous managed install first." }
    $existingResults = @(Get-LiveResults $Paths)
    if ($existingResults.Count -gt 0) { throw "Live profiler CSVs already exist in the CET folder. Use COLLECT RESULTS / CLEAR LIVE first." }

    $officialHash = ([string]$Manifest.targetCET.officialSha256).ToLowerInvariant()
    $profilerHash = ([string]$Manifest.targetCET.profilerSha256).ToLowerInvariant()
    $liveHash = Get-Sha256 $Paths.LiveAsi
    if ($liveHash -ne $officialHash -and $liveHash -ne $profilerHash) {
        throw "Installed CET ASI is neither the exact supported official CET $($Manifest.targetCET.version) binary nor this profiler binary. No files were changed."
    }

    $profilerSource = Join-Path $PayloadRoot "cyber_engine_tweaks.PROFILER.asi"
    $schedulerSource = Join-Path $PayloadRoot "0-Engine\modules\Scheduler.lua"
    $adaptiveSchedulerSource = Join-Path $PayloadRoot "0-Engine\modules\CETProfilerScheduler.lua"
    $controlsSource = Join-Path $PayloadRoot "CETProfilerControls"

    if ((Get-Sha256 $profilerSource) -ne $profilerHash) { throw "Bundled profiler ASI failed its manifest hash check." }
    $schedulerProfilerHash = ([string]$Manifest.zeroEngine.profilerSchedulerSha256).ToLowerInvariant()
    if ((Get-Sha256 $schedulerSource) -ne $schedulerProfilerHash) { throw "Bundled profiler-aware Scheduler.lua failed its manifest hash check." }
    $adaptiveProfilerHash = ([string]$Manifest.zeroEngine.profilerAdaptiveSchedulerSha256).ToLowerInvariant()
    if ((Get-Sha256 $adaptiveSchedulerSource) -ne $adaptiveProfilerHash) { throw "Bundled CETProfilerScheduler.lua failed its manifest hash check." }
    if (!(Test-Path -LiteralPath $controlsSource -PathType Container)) { throw "Bundled CETProfilerControls is missing." }

    $zeroPresentBefore = Test-Path -LiteralPath $Paths.ZeroInit -PathType Leaf
    if ($zeroPresentBefore -and !$CoreProfilerOnly) {
        $preInitState = Get-ZeroInitState $Paths
        if ($preInitState.Kind -eq "unsafe") {
            throw "0-Engine was found, but its init.lua structure is not recognized as safe for adaptive Scheduler injection. Check 'Core profiler only - leave 0-Engine untouched' and install again."
        }
    }

    New-Item -ItemType Directory -Path $Paths.StateRoot -Force | Out-Null

    $state = [ordered]@{
        packageVersion = [string]$Manifest.packageVersion
        installedUtc = [DateTime]::UtcNow.ToString("o")
        coreProfilerOnly = [bool]$CoreProfilerOnly
        asi = [ordered]@{ mode = ""; originalHash = $liveHash; installedHash = $profilerHash }
        zeroEngine = [ordered]@{
            mode = $(if ($zeroPresentBefore) { "pending" } else { "absent" })
            presentBefore = $zeroPresentBefore
            bypass = [ordered]@{ originalFingerprint = $null }
            init = [ordered]@{ mode = "not-applicable"; originalHash = $null; installedHash = $null }
            scheduler = [ordered]@{ mode = "not-applicable"; originalHash = $null; installedHash = $null }
            adaptiveScheduler = [ordered]@{ mode = "not-applicable"; originalHash = $null; installedHash = $null }
        }
        controls = [ordered]@{ mode = "profiler-owned" }
        binding = Get-ProfilerBindingSnapshot $Paths
    }

    try {
        if ($liveHash -eq $profilerHash) {
            $state.asi.mode = "preexisting-profiler"
        }
        else {
            $state.asi.mode = "replaced"
            Copy-Item -LiteralPath $Paths.LiveAsi -Destination $Paths.BackupAsi -Force
            if ((Get-Sha256 $Paths.BackupAsi) -ne $liveHash) { throw "CET ASI backup verification failed." }
            Copy-Item -LiteralPath $profilerSource -Destination $Paths.LiveAsi -Force
            if ((Get-Sha256 $Paths.LiveAsi) -ne $profilerHash) { throw "Profiler ASI deployment verification failed." }
        }

        if ($zeroPresentBefore -and $CoreProfilerOnly) {
            # Core-profiler fallback deliberately does not touch 0-Engine at all.
            # It remains installed and loaded normally; we only skip Scheduler integration.
            $state.zeroEngine.mode = "core-only"
            $state.zeroEngine.init.mode = "left-untouched"
            $state.zeroEngine.init.originalHash = Get-Sha256 $Paths.ZeroInit
            $state.zeroEngine.init.installedHash = $state.zeroEngine.init.originalHash
            $state.zeroEngine.scheduler.mode = "left-untouched"
            if (Test-Path -LiteralPath $Paths.ZeroScheduler -PathType Leaf) {
                $state.zeroEngine.scheduler.originalHash = Get-Sha256 $Paths.ZeroScheduler
                $state.zeroEngine.scheduler.installedHash = $state.zeroEngine.scheduler.originalHash
            }
        }
        elseif ($zeroPresentBefore) {
            $initState = Get-ZeroInitState $Paths
            $state.zeroEngine.init.originalHash = Get-Sha256 $Paths.ZeroInit

            if ($initState.Kind -eq "integrated") {
                $state.zeroEngine.mode = "integrated"
                $state.zeroEngine.init.mode = "preexisting-compatible"
                $state.zeroEngine.init.installedHash = Get-Sha256 $Paths.ZeroInit

                $schedState = Get-ZeroSchedulerState $Paths
                if ($schedState.Kind -eq "aware") {
                    $state.zeroEngine.scheduler.mode = "preexisting-profiler"
                    $state.zeroEngine.scheduler.originalHash = $schedState.Hash
                    $state.zeroEngine.scheduler.installedHash = $schedulerProfilerHash
                }
                elseif ($schedState.Kind -eq "other") {
                    $state.zeroEngine.scheduler.mode = "replaced"
                    $state.zeroEngine.scheduler.originalHash = $schedState.Hash
                    New-Item -ItemType Directory -Path (Split-Path -Parent $Paths.ZeroScheduler) -Force | Out-Null
                    Copy-Item -LiteralPath $Paths.ZeroScheduler -Destination $Paths.BackupZeroScheduler -Force
                    if ((Get-Sha256 $Paths.BackupZeroScheduler) -ne $schedState.Hash) { throw "Existing Scheduler.lua backup verification failed." }
                    Copy-Item -LiteralPath $schedulerSource -Destination $Paths.ZeroScheduler -Force
                    if ((Get-Sha256 $Paths.ZeroScheduler) -ne $schedulerProfilerHash) { throw "Profiler-aware Scheduler.lua deployment verification failed." }
                    $state.zeroEngine.scheduler.installedHash = $schedulerProfilerHash
                }
                else {
                    $state.zeroEngine.scheduler.mode = "added"
                    New-Item -ItemType Directory -Path (Split-Path -Parent $Paths.ZeroScheduler) -Force | Out-Null
                    Copy-Item -LiteralPath $schedulerSource -Destination $Paths.ZeroScheduler -Force
                    if ((Get-Sha256 $Paths.ZeroScheduler) -ne $schedulerProfilerHash) { throw "Profiler-aware Scheduler.lua deployment verification failed." }
                    $state.zeroEngine.scheduler.installedHash = $schedulerProfilerHash
                }
            }
            elseif ($initState.Kind -eq "adaptive" -or $initState.Kind -eq "profiler-bridge") {
                $state.zeroEngine.mode = "adaptive"

                if ($initState.Kind -eq "profiler-bridge") {
                    $state.zeroEngine.init.mode = "preexisting-profiler-bridge"
                    $state.zeroEngine.init.installedHash = Get-Sha256 $Paths.ZeroInit
                }
                else {
                    $state.zeroEngine.init.mode = "patched-adaptive"
                    Copy-Item -LiteralPath $Paths.ZeroInit -Destination $Paths.BackupZeroInit -Force
                    if ((Get-Sha256 $Paths.BackupZeroInit) -ne $state.zeroEngine.init.originalHash) { throw "0-Engine init.lua backup verification failed." }
                    Add-AdaptiveProfilerSchedulerBridge $Paths.ZeroInit
                    $state.zeroEngine.init.installedHash = Get-Sha256 $Paths.ZeroInit
                    if (!(Get-Content -LiteralPath $Paths.ZeroInit -Raw).Contains([string]$Manifest.zeroEngine.profilerBridgeMarker)) {
                        throw "Adaptive 0-Engine profiler bridge verification failed."
                    }
                }

                $adaptiveState = Get-ZeroAdaptiveSchedulerState $Paths
                if ($adaptiveState.Kind -eq "aware") {
                    $state.zeroEngine.adaptiveScheduler.mode = "preexisting-profiler"
                    $state.zeroEngine.adaptiveScheduler.originalHash = $adaptiveState.Hash
                    $state.zeroEngine.adaptiveScheduler.installedHash = $adaptiveProfilerHash
                }
                elseif ($adaptiveState.Kind -eq "other") {
                    $state.zeroEngine.adaptiveScheduler.mode = "replaced"
                    $state.zeroEngine.adaptiveScheduler.originalHash = $adaptiveState.Hash
                    New-Item -ItemType Directory -Path (Split-Path -Parent $Paths.ZeroAdaptiveScheduler) -Force | Out-Null
                    Copy-Item -LiteralPath $Paths.ZeroAdaptiveScheduler -Destination $Paths.BackupZeroAdaptiveScheduler -Force
                    if ((Get-Sha256 $Paths.BackupZeroAdaptiveScheduler) -ne $adaptiveState.Hash) { throw "Existing CETProfilerScheduler.lua backup verification failed." }
                    Copy-Item -LiteralPath $adaptiveSchedulerSource -Destination $Paths.ZeroAdaptiveScheduler -Force
                    if ((Get-Sha256 $Paths.ZeroAdaptiveScheduler) -ne $adaptiveProfilerHash) { throw "CETProfilerScheduler.lua deployment verification failed." }
                    $state.zeroEngine.adaptiveScheduler.installedHash = $adaptiveProfilerHash
                }
                else {
                    $state.zeroEngine.adaptiveScheduler.mode = "added"
                    New-Item -ItemType Directory -Path (Split-Path -Parent $Paths.ZeroAdaptiveScheduler) -Force | Out-Null
                    Copy-Item -LiteralPath $adaptiveSchedulerSource -Destination $Paths.ZeroAdaptiveScheduler -Force
                    if ((Get-Sha256 $Paths.ZeroAdaptiveScheduler) -ne $adaptiveProfilerHash) { throw "CETProfilerScheduler.lua deployment verification failed." }
                    $state.zeroEngine.adaptiveScheduler.installedHash = $adaptiveProfilerHash
                }
            }
            else {
                throw "0-Engine init.lua structure is not recognized. Use Core profiler mode to leave 0-Engine untouched and skip Scheduler integration."
            }
        }

        Copy-DirectoryExact $controlsSource $Paths.Controls
        if (!(Test-Path -LiteralPath (Join-Path $Paths.Controls "init.lua") -PathType Leaf)) {
            throw "CETProfilerControls deployment verification failed."
        }

        Set-ProfilerDefaultBinding $Paths
        if (!(Test-ProfilerF11Binding $Paths)) { throw "CETProfilerControls F11 binding verification failed." }

        Save-State $Paths $state
    }
    catch {
        try { if (Test-Path -LiteralPath $Paths.Controls) { Remove-Item -LiteralPath $Paths.Controls -Recurse -Force } } catch {}
        try { Restore-ProfilerBinding $Paths $state.binding } catch {}

        try {
            if ($state.zeroEngine.adaptiveScheduler.mode -eq "replaced" -and (Test-Path -LiteralPath $Paths.BackupZeroAdaptiveScheduler)) {
                Copy-Item -LiteralPath $Paths.BackupZeroAdaptiveScheduler -Destination $Paths.ZeroAdaptiveScheduler -Force
            }
            elseif ($state.zeroEngine.adaptiveScheduler.mode -eq "added" -and (Test-Path -LiteralPath $Paths.ZeroAdaptiveScheduler)) {
                Remove-Item -LiteralPath $Paths.ZeroAdaptiveScheduler -Force
            }
        } catch {}

        try {
            if ($state.zeroEngine.scheduler.mode -eq "replaced" -and (Test-Path -LiteralPath $Paths.BackupZeroScheduler)) {
                Copy-Item -LiteralPath $Paths.BackupZeroScheduler -Destination $Paths.ZeroScheduler -Force
            }
            elseif ($state.zeroEngine.scheduler.mode -eq "added" -and (Test-Path -LiteralPath $Paths.ZeroScheduler)) {
                Remove-Item -LiteralPath $Paths.ZeroScheduler -Force
            }
        } catch {}

        try {
            if ($state.zeroEngine.init.mode -eq "patched-adaptive" -and (Test-Path -LiteralPath $Paths.BackupZeroInit)) {
                Copy-Item -LiteralPath $Paths.BackupZeroInit -Destination $Paths.ZeroInit -Force
            }
        } catch {}

        try {
            if ($state.zeroEngine.mode -eq "bypassed" -and (Test-Path -LiteralPath $Paths.BackupZeroRoot -PathType Container) -and !(Test-Path -LiteralPath $Paths.ZeroRoot)) {
                Copy-DirectoryExact $Paths.BackupZeroRoot $Paths.ZeroRoot
            }
        } catch {}

        try {
            if ($state.asi.mode -eq "replaced" -and (Test-Path -LiteralPath $Paths.BackupAsi)) {
                Copy-Item -LiteralPath $Paths.BackupAsi -Destination $Paths.LiveAsi -Force
            }
        } catch {}

        if (Test-Path -LiteralPath $Paths.StateRoot) { try { Remove-Item -LiteralPath $Paths.StateRoot -Recurse -Force } catch {} }
        throw
    }
}

function Restore-Profiler($Paths) {
    Assert-GameClosed
    $state = Get-State $Paths
    if ($null -eq $state) { throw "No managed profiler installation state was found." }

    $profilerHash = ([string]$state.asi.installedHash).ToLowerInvariant()

    if ([string]$state.asi.mode -eq "replaced") {
        if (!(Test-Path -LiteralPath $Paths.BackupAsi -PathType Leaf)) { throw "Original CET ASI backup is missing. Restore aborted before changing anything." }
        if ((Get-Sha256 $Paths.BackupAsi) -ne ([string]$state.asi.originalHash).ToLowerInvariant()) { throw "Original CET ASI backup hash is wrong. Restore aborted before changing anything." }
        $cur = Get-Sha256 $Paths.LiveAsi
        if ($cur -ne $profilerHash -and $cur -ne ([string]$state.asi.originalHash).ToLowerInvariant()) { throw "Live CET ASI changed after profiler installation. Restore aborted to avoid overwriting user changes." }
    }

    $zeroMode = [string]$state.zeroEngine.mode
    if ($zeroMode -eq "bypassed") {
        if (!(Test-Path -LiteralPath $Paths.BackupZeroRoot -PathType Container)) { throw "Full 0-Engine backup is missing. Restore aborted before changing anything." }
        $expected = [string]$state.zeroEngine.bypass.originalFingerprint
        if ((Get-DirectoryFingerprint $Paths.BackupZeroRoot) -ne $expected) { throw "Full 0-Engine backup fingerprint is wrong. Restore aborted before changing anything." }
        if (Test-Path -LiteralPath $Paths.ZeroRoot -PathType Container) {
            $liveFingerprint = Get-DirectoryFingerprint $Paths.ZeroRoot
            if ($liveFingerprint -ne $expected) { throw "0-Engine reappeared or changed while compatibility mode was active. Restore aborted to avoid overwriting user files." }
        }
    }

    if ([string]$state.zeroEngine.init.mode -eq "patched-adaptive") {
        if (!(Test-Path -LiteralPath $Paths.BackupZeroInit -PathType Leaf)) { throw "Original 0-Engine init.lua backup is missing. Restore aborted before changing anything." }
        if ((Get-Sha256 $Paths.BackupZeroInit) -ne ([string]$state.zeroEngine.init.originalHash).ToLowerInvariant()) { throw "Original 0-Engine init.lua backup hash is wrong. Restore aborted before changing anything." }
        $cur = Get-Sha256 $Paths.ZeroInit
        if ($cur -ne ([string]$state.zeroEngine.init.installedHash).ToLowerInvariant() -and $cur -ne ([string]$state.zeroEngine.init.originalHash).ToLowerInvariant()) {
            throw "0-Engine init.lua changed after profiler installation. Restore aborted to avoid overwriting user changes."
        }
    }

    $schedMode = [string]$state.zeroEngine.scheduler.mode
    if ($schedMode -eq "replaced") {
        if (!(Test-Path -LiteralPath $Paths.BackupZeroScheduler -PathType Leaf)) { throw "Original Scheduler.lua backup is missing. Restore aborted before changing anything." }
        if ((Get-Sha256 $Paths.BackupZeroScheduler) -ne ([string]$state.zeroEngine.scheduler.originalHash).ToLowerInvariant()) { throw "Original Scheduler.lua backup hash is wrong. Restore aborted before changing anything." }
        $cur = Get-Sha256 $Paths.ZeroScheduler
        if ($cur -ne ([string]$state.zeroEngine.scheduler.installedHash).ToLowerInvariant() -and $cur -ne ([string]$state.zeroEngine.scheduler.originalHash).ToLowerInvariant()) {
            throw "0-Engine Scheduler.lua changed after profiler installation. Restore aborted to avoid overwriting user changes."
        }
    }
    elseif ($schedMode -eq "added" -and (Test-Path -LiteralPath $Paths.ZeroScheduler -PathType Leaf)) {
        if ((Get-Sha256 $Paths.ZeroScheduler) -ne ([string]$state.zeroEngine.scheduler.installedHash).ToLowerInvariant()) {
            throw "Profiler-added Scheduler.lua changed after installation. Restore aborted to avoid deleting user changes."
        }
    }

    $adaptiveMode = [string]$state.zeroEngine.adaptiveScheduler.mode
    if ($adaptiveMode -eq "replaced") {
        if (!(Test-Path -LiteralPath $Paths.BackupZeroAdaptiveScheduler -PathType Leaf)) { throw "Original CETProfilerScheduler.lua backup is missing. Restore aborted before changing anything." }
        if ((Get-Sha256 $Paths.BackupZeroAdaptiveScheduler) -ne ([string]$state.zeroEngine.adaptiveScheduler.originalHash).ToLowerInvariant()) { throw "Original CETProfilerScheduler.lua backup hash is wrong. Restore aborted before changing anything." }
        $cur = Get-Sha256 $Paths.ZeroAdaptiveScheduler
        if ($cur -ne ([string]$state.zeroEngine.adaptiveScheduler.installedHash).ToLowerInvariant() -and $cur -ne ([string]$state.zeroEngine.adaptiveScheduler.originalHash).ToLowerInvariant()) {
            throw "CETProfilerScheduler.lua changed after profiler installation. Restore aborted to avoid overwriting user changes."
        }
    }
    elseif ($adaptiveMode -eq "added" -and (Test-Path -LiteralPath $Paths.ZeroAdaptiveScheduler -PathType Leaf)) {
        if ((Get-Sha256 $Paths.ZeroAdaptiveScheduler) -ne ([string]$state.zeroEngine.adaptiveScheduler.installedHash).ToLowerInvariant()) {
            throw "Profiler-added CETProfilerScheduler.lua changed after installation. Restore aborted to avoid deleting user changes."
        }
    }

    $archived = Collect-ResultsInternal $Paths $true

    if ([string]$state.asi.mode -eq "replaced") {
        if ((Get-Sha256 $Paths.LiveAsi) -eq $profilerHash) {
            Copy-Item -LiteralPath $Paths.BackupAsi -Destination $Paths.LiveAsi -Force
        }
        if ((Get-Sha256 $Paths.LiveAsi) -ne ([string]$state.asi.originalHash).ToLowerInvariant()) { throw "CET ASI restoration failed verification." }
    }

    if ($zeroMode -eq "bypassed") {
        $expected = [string]$state.zeroEngine.bypass.originalFingerprint
        if (!(Test-Path -LiteralPath $Paths.ZeroRoot -PathType Container)) {
            Copy-DirectoryExact $Paths.BackupZeroRoot $Paths.ZeroRoot
        }
        if ((Get-DirectoryFingerprint $Paths.ZeroRoot) -ne $expected) { throw "0-Engine full-folder restoration failed verification." }
    }

    if ([string]$state.zeroEngine.init.mode -eq "patched-adaptive") {
        if ((Get-Sha256 $Paths.ZeroInit) -eq ([string]$state.zeroEngine.init.installedHash).ToLowerInvariant()) {
            Copy-Item -LiteralPath $Paths.BackupZeroInit -Destination $Paths.ZeroInit -Force
        }
        if ((Get-Sha256 $Paths.ZeroInit) -ne ([string]$state.zeroEngine.init.originalHash).ToLowerInvariant()) { throw "0-Engine init.lua restoration failed verification." }
    }

    if ($schedMode -eq "replaced") {
        if ((Get-Sha256 $Paths.ZeroScheduler) -eq ([string]$state.zeroEngine.scheduler.installedHash).ToLowerInvariant()) {
            Copy-Item -LiteralPath $Paths.BackupZeroScheduler -Destination $Paths.ZeroScheduler -Force
        }
        if ((Get-Sha256 $Paths.ZeroScheduler) -ne ([string]$state.zeroEngine.scheduler.originalHash).ToLowerInvariant()) { throw "Scheduler.lua restoration failed verification." }
    }
    elseif ($schedMode -eq "added") {
        if (Test-Path -LiteralPath $Paths.ZeroScheduler -PathType Leaf) { Remove-Item -LiteralPath $Paths.ZeroScheduler -Force }
    }

    if ($adaptiveMode -eq "replaced") {
        if ((Get-Sha256 $Paths.ZeroAdaptiveScheduler) -eq ([string]$state.zeroEngine.adaptiveScheduler.installedHash).ToLowerInvariant()) {
            Copy-Item -LiteralPath $Paths.BackupZeroAdaptiveScheduler -Destination $Paths.ZeroAdaptiveScheduler -Force
        }
        if ((Get-Sha256 $Paths.ZeroAdaptiveScheduler) -ne ([string]$state.zeroEngine.adaptiveScheduler.originalHash).ToLowerInvariant()) { throw "CETProfilerScheduler.lua restoration failed verification." }
    }
    elseif ($adaptiveMode -eq "added") {
        if (Test-Path -LiteralPath $Paths.ZeroAdaptiveScheduler -PathType Leaf) { Remove-Item -LiteralPath $Paths.ZeroAdaptiveScheduler -Force }
    }

    # New standalone state treats CETProfilerControls as profiler-owned. A
    # TOTAL Profiler 0.2.19 state may instead say "replaced" and carry an exact
    # backup of a pre-existing controls directory. Honor that legacy transaction
    # so upgrading the manager cannot destroy the user's prior folder.
    $legacyControlsMode = ""
    if ($null -ne $state.controls -and $null -ne $state.controls.mode) {
        $legacyControlsMode = [string]$state.controls.mode
    }

    if ($legacyControlsMode -eq "replaced") {
        if (!(Test-Path -LiteralPath $Paths.LegacyBackupControlsRoot -PathType Container)) {
            throw "Legacy CETProfilerControls backup is missing. Restore aborted before removing the live controls folder."
        }

        $expectedControlsFingerprint = ""
        if ($null -ne $state.controls.originalFingerprint) {
            $expectedControlsFingerprint = [string]$state.controls.originalFingerprint
        }
        if ($expectedControlsFingerprint -and
            (Get-DirectoryFingerprint $Paths.LegacyBackupControlsRoot) -ne $expectedControlsFingerprint) {
            throw "Legacy CETProfilerControls backup fingerprint is wrong. Restore aborted."
        }

        Copy-DirectoryExact $Paths.LegacyBackupControlsRoot $Paths.Controls

        if ($expectedControlsFingerprint -and
            (Get-DirectoryFingerprint $Paths.Controls) -ne $expectedControlsFingerprint) {
            throw "Legacy CETProfilerControls restoration failed verification."
        }
    }
    else {
        if (Test-Path -LiteralPath $Paths.Controls) {
            Remove-Item -LiteralPath $Paths.Controls -Recurse -Force
        }
    }

    Restore-ProfilerBinding $Paths $state.binding

    Remove-Item -LiteralPath $Paths.StateRoot -Recurse -Force
    return $archived
}

function Get-ProfilerStatus($Paths) {
    $officialHash = ([string]$Manifest.targetCET.officialSha256).ToLowerInvariant()
    $profilerHash = ([string]$Manifest.targetCET.profilerSha256).ToLowerInvariant()
    $liveHash = Get-Sha256 $Paths.LiveAsi
    $cet = if ($liveHash -eq $officialHash) { "OFFICIAL" } elseif ($liveHash -eq $profilerHash) { "PROFILER_ACTIVE" } elseif ($liveHash) { "UNKNOWN" } else { "MISSING" }
    $state = Get-State $Paths
    $zeroPresent = Test-Path -LiteralPath $Paths.ZeroInit -PathType Leaf
    $initKind = "absent"
    $initText = "0-ENGINE NOT FOUND"
    $schedulerText = "-"
    if ($zeroPresent) {
        $i = Get-ZeroInitState $Paths
        $initKind = [string]$i.Kind
        $initText = [string]$i.Text
        if ($i.Kind -eq "integrated") {
            $schedulerText = [string](Get-ZeroSchedulerState $Paths).Text
        }
        elseif ($i.Kind -eq "adaptive" -or $i.Kind -eq "profiler-bridge") {
            $schedulerText = [string](Get-ZeroAdaptiveSchedulerState $Paths).Text
        }
        else {
            $schedulerText = "CHECK CORE PROFILER MODE"
        }
    }
    return [ordered]@{
        ok = $true
        packageVersion = [string]$Manifest.packageVersion
        targetCETVersion = [string]$Manifest.targetCET.version
        gameRoot = [string]$Paths.Root
        cet = $cet
        cetHash = $liveHash
        zeroEnginePresent = [bool]$zeroPresent
        zeroEngineInitKind = $initKind
        zeroEngineInit = $initText
        scheduler = $schedulerText
        managed = ($null -ne $state)
        managedMode = $(if ($null -ne $state) { [string]$state.zeroEngine.mode } else { "" })
        controlsPresent = (Test-Path -LiteralPath $Paths.Controls -PathType Container)
        f11Binding = (Test-ProfilerF11Binding $Paths)
        liveResultCount = @(Get-LiveResults $Paths).Count
        resultsRoot = [string]$ResultsRoot
        state = $state
    }
}

try {
    if (!(Test-GameRoot $GameRoot)) { throw "Cyberpunk 2077 folder is invalid or CET plugins folder was not found: $GameRoot" }
    $p = Get-Paths $GameRoot

    switch ($Action) {
        "Status" {
            Get-ProfilerStatus $p | ConvertTo-Json -Depth 12 -Compress
        }
        "Install" {
            Install-Profiler $p ([bool]$CoreProfilerOnly)
            Get-ProfilerStatus $p | ConvertTo-Json -Depth 12 -Compress
        }
        "Collect" {
            Assert-GameClosed
            $dest = Collect-ResultsInternal $p $false
            [ordered]@{ ok=$true; destination=[string]$dest; status=(Get-ProfilerStatus $p) } | ConvertTo-Json -Depth 12 -Compress
        }
        "ResetLive" {
            Assert-GameClosed
            $dest = Collect-ResultsInternal $p $true
            [ordered]@{ ok=$true; archived=$(if ($dest) { [string]$dest } else { "" }); status=(Get-ProfilerStatus $p) } | ConvertTo-Json -Depth 12 -Compress
        }
        "Restore" {
            $archived = Restore-Profiler $p
            [ordered]@{
                ok = $true
                archived = $(if ($archived) { [string]$archived } else { "" })
                status = (Get-ProfilerStatus $p)
            } | ConvertTo-Json -Depth 12 -Compress
        }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
