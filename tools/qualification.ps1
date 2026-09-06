param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Run','Cleanup')][string]$Action,
    [Parameter(Mandatory=$true)][string]$SandboxRoot,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Vanguard Galaxy',
    [string]$OriginalSaveDir = "$env:USERPROFILE\AppData\LocalLow\Bat Roost Games\VanguardGalaxy\Saves",
    [string]$SaveA,
    [string]$SaveB,
    [string]$BuildRoot,
    [string]$MissionJournalBin,
    [string]$StockpileBin,
    [switch]$AssemblyOverlay,
    [switch]$VanillaLoadControl,
    [switch]$PersistenceProbe,
    [switch]$JournalCoordinated,
    [switch]$StockpileCoordinated,
    [switch]$ContentReferenceProbe,
    [switch]$MissionTransitionsProbe,
    [switch]$MissionIdentityProbe,
    [switch]$JournalMissionEventsProbe,
    [string]$BuildRevision = 'unknown',
    [switch]$Diagnostics,
    [ValidateSet('Full','MissingApi','UnavailableApi')][string]$Scenario = 'Full',
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'qualification-profile.ps1')
. (Join-Path $PSScriptRoot 'qualification-inputs.ps1')
$Scenario = switch ($Scenario) { 'Full' { 'Full' }; 'MissingApi' { 'MissingApi' }; 'UnavailableApi' { 'UnavailableApi' } }
$root = [IO.Path]::GetFullPath($SandboxRoot).TrimEnd('\')
$game = Join-Path $root 'game'
$marker = Join-Path $root 'qualification.marker'
$markerText = 'vgmodapi-disposable-sandbox-v1'
$junctions = @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')
function SamePath($a, $b) { return [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') }
function SaveHashes($directories) {
    $result = @{}
    foreach ($directory in $directories) {
        Get-ChildItem -LiteralPath $directory -File -Force | ForEach-Object {
            $result[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $result
}
if (Get-Process VanguardGalaxy -ErrorAction SilentlyContinue) { throw 'A game process is already running. Refusing concurrent qualification.' }
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $root) { throw 'Sandbox root already exists; use a fresh directory.' }
    if (!$SaveA -or !$SaveB -or !$BuildRoot) { throw 'Prepare requires SaveA, SaveB and BuildRoot.' }
    if (!(Test-Path -LiteralPath $OriginalSaveDir -PathType Container)) { throw 'OriginalSaveDir must identify the existing real save directory.' }
    $sources = @((Get-Item -LiteralPath $SaveA), (Get-Item -LiteralPath $SaveB))
    foreach ($source in $sources) { if ($source.Extension -ne '.save') { throw 'Fixtures must be existing .save files.' } }
    $directories = @(@([IO.Path]::GetFullPath($OriginalSaveDir)) + @($sources | ForEach-Object { $_.DirectoryName }) | Select-Object -Unique)
    foreach ($protected in @($GameDir) + $directories) {
        $prefix = [IO.Path]::GetFullPath($protected).TrimEnd('\')
        if ((SamePath $root $prefix) -or $root.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Sandbox cannot be inside the installation or original save directory.' }
    }
    if ($AssemblyOverlay -and $Scenario -ne 'UnavailableApi') { throw 'Assembly overlay requires UnavailableApi.' }
    if ($VanillaLoadControl -and $Scenario -ne 'MissingApi') { throw 'Vanilla load control requires MissingApi.' }
    if ($PersistenceProbe -and $Scenario -ne 'Full') { throw 'Persistence probe requires Full.' }
    if ($JournalMissionEventsProbe -and (!$JournalCoordinated -or !$MissionIdentityProbe)) { throw 'Journal mission events require API-managed journal and mission identity probes.' }
    if ($MissionIdentityProbe -and (!$MissionTransitionsProbe -or !$PersistenceProbe)) { throw 'Mission identity probe requires mission transitions and persistence probes.' }
    if ($MissionTransitionsProbe -and $Scenario -ne 'Full') { throw 'Mission transitions probe requires Full.' }
    if ($ContentReferenceProbe -and $Scenario -ne 'Full') { throw 'Content reference probe requires Full.' }
    if ($JournalCoordinated -and (!$PersistenceProbe -or !$MissionJournalBin)) { throw 'Coordinated journal requires persistence probe and journal binary.' }
    if ($StockpileCoordinated -and (!$JournalCoordinated -or !$StockpileBin)) { throw 'Coordinated Stockpile requires coordinated journal and Stockpile binary.' }
    New-Item -ItemType Directory -Path $game | Out-Null
    [IO.File]::WriteAllText($marker, $markerText)
    [IO.File]::WriteAllText((Join-Path $root 'original-save-directory.txt'), [IO.Path]::GetFullPath($OriginalSaveDir))
    @{ directories=$directories; files=(SaveHashes $directories) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'original-save-hashes.json')
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','UnityCrashHandler64.exe','dstorage.dll','dstoragecore.dll')) {
        $source = Join-Path $GameDir $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $game }
        elseif ($name -in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll')) { throw "Required runtime file missing: $name" }
    }
    # Doorstop 4 format, verified against the installed loader's configuration.
    # Never inherit an absolute preloader path or Mono search override.
    [IO.File]::WriteAllText((Join-Path $game 'doorstop_config.ini'), "[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Preloader.dll`nredirect_output_log=false`nboot_config_override=`nignore_disable_switch=false`n[UnityMono]`ndll_search_path_override=`ndebug_enabled=false`ndebug_suspend=false`n")
    foreach ($name in $junctions) {
        $source = Join-Path $GameDir $name
        if (Test-Path -LiteralPath $source) { New-Item -ItemType Junction -Path (Join-Path $game $name) -Target $source | Out-Null }
    }
    $overlay = if ($AssemblyOverlay) { Initialize-QualificationAssemblyOverlay $root $GameDir } else { $null }
    $bep = Join-Path $game 'BepInEx'
    New-Item -ItemType Directory -Path $bep | Out-Null
    Copy-Item -LiteralPath (Join-Path $GameDir 'BepInEx\core') -Destination $bep -Recurse
    $plugins = Join-Path $bep 'plugins'
    New-Item -ItemType Directory -Path $plugins | Out-Null
    Copy-Item -LiteralPath (Join-Path $BuildRoot 'tools\QualificationGuard\bin\Release\netstandard2.1\QualificationGuard.dll') -Destination $plugins
    if ($Scenario -ne 'MissingApi') {
        foreach ($name in @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll')) {
            Copy-Item -LiteralPath (Join-Path $BuildRoot "artifacts\VGModAPI\$name") -Destination $plugins
        }
    }
    if ($Scenario -eq 'Full') {
        Copy-Item -LiteralPath (Join-Path $BuildRoot 'tools\QualificationRunner\bin\Release\netstandard2.1\QualificationRunner.dll') -Destination $plugins
        Copy-Item -LiteralPath (Join-Path $BuildRoot 'examples\LifecycleObserver\bin\Release\netstandard2.1\LifecycleObserver.dll') -Destination $plugins
    }
    if ($MissionJournalBin) {
        # Refuse legacy binaries whose startup sweeper can run before the guard.
        $candidate = Join-Path $MissionJournalBin 'VGMissionJournal.dll'
        $null = [Reflection.AssemblyName]::GetAssemblyName($candidate)
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($candidate)
        try {
            if ($assembly.Name.Name -ne 'VGMissionJournal' -or $assembly.Name.Version.Major -ne 0 -or $assembly.Name.Version.Minor -notin @(2,3,4) -or ($JournalCoordinated -and $assembly.Name.Version.Minor -notin @(3,4)) -or ($JournalMissionEventsProbe -and $assembly.Name.Version.Minor -ne 4)) { throw 'Only reviewed 0.2/0.3/0.4 MissionJournal pilot shapes are accepted; API mission events require 0.4.' }
            $minimumApi = if ($assembly.Name.Version.Minor -eq 4) { '0.1.8' } elseif ($assembly.Name.Version.Minor -eq 3) { '0.1.2' } else { '0.1.0' }
            $plugin = $assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGMissionJournal.Plugin' }
            $dependency = @($plugin.CustomAttributes | Where-Object {
                $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and
                $_.ConstructorArguments[0].Value -eq 'vgmodapi' -and $_.ConstructorArguments[1].Value -eq $minimumApi
            })
            if ($dependency.Count -ne 1) { throw 'MissionJournal must require the API before its Awake.' }
        } finally { $assembly.Dispose() }
        foreach ($name in @('VGMissionJournal.dll','Newtonsoft.Json.dll')) {
            Copy-Item -LiteralPath (Join-Path $MissionJournalBin $name) -Destination $plugins
        }
        [IO.File]::WriteAllText((Join-Path $root 'missionjournal.enabled'), 'pilot-v1')
    }
    if ($StockpileBin) {
        $candidate = Join-Path $StockpileBin 'VGStockpile.dll'
        $null = [Reflection.AssemblyName]::GetAssemblyName($candidate)
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($candidate)
        try {
            if ($assembly.Name.Name -ne 'VGStockpile' -or $assembly.Name.Version.Major -ne 0 -or $assembly.Name.Version.Minor -notin @(6,7) -or ($StockpileCoordinated -and $assembly.Name.Version.Minor -ne 7)) { throw 'Only Stockpile 0.6/0.7 pilot inputs accepted; coordinated mode requires 0.7.' }
            $minimumApi = if ($assembly.Name.Version.Minor -eq 7) { '0.1.2' } else { '0.1.1' }
            $plugin = $assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGStockpile.Plugin' }
            $dependency = @($plugin.CustomAttributes | Where-Object {
                $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and
                $_.ConstructorArguments[0].Value -eq 'vgmodapi' -and $_.ConstructorArguments[1].Value -eq $minimumApi
            })
            if ($dependency.Count -ne 1) { throw 'Stockpile must hard-require its expected API version.' }
        } finally { $assembly.Dispose() }
        foreach ($name in @('VGStockpile.dll','Newtonsoft.Json.dll')) {
            $source = Join-Path $StockpileBin $name
            $destination = Join-Path $plugins $name
            if ((Test-Path -LiteralPath $destination) -and (Get-FileHash $source).Hash -ne (Get-FileHash $destination).Hash) { throw 'Consumer dependency bytes disagree.' }
            Copy-Item -LiteralPath $source -Destination $destination
        }
        [IO.File]::WriteAllText((Join-Path $root 'stockpile.enabled'), 'pilot-v1')
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgstockpile.cfg'), "[Transfers]`r`nEnabled = true`r`n")
    }
    # Unselected pilots explicitly use legacy mode; selected pilots exercise the new defaults.
    New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
    if ($StockpileBin -and !$StockpileCoordinated) { [IO.File]::AppendAllText((Join-Path $bep 'config\vgstockpile.cfg'), "[Persistence]`r`nUseApiSaveData = false`r`n") }
    if ($MissionJournalBin -and !$JournalCoordinated) { [IO.File]::WriteAllText((Join-Path $bep 'config\vgmissionjournal.cfg'), "[Persistence]`r`nUseApiSaveData = false`r`n") }
    if (!$PersistenceProbe) { [IO.File]::WriteAllText((Join-Path $bep 'config\vgmodapi.cfg'), "[Persistence]`r`nEnabled = false`r`n") }
    if ($StockpileCoordinated) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgstockpile.cfg'), "[Persistence]`r`nImportLegacySidecars = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'stockpile-coordinated.enabled'), 'stockpile-v1')
    }
    if ($JournalCoordinated) {
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgmissionjournal.cfg'), "[Persistence]`r`nImportLegacySidecars = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'journal-coordinated.enabled'), 'journal-v1')
    }
    if ($PersistenceProbe) {
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgmodapi.cfg'), "[Persistence]`r`nRoot = $(Join-Path $root 'state')`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'persistence-probe.enabled'), 'probe-v1')
    }
    if ($VanillaLoadControl) { [IO.File]::WriteAllText((Join-Path $root 'vanilla-load.enabled'), 'control-v1') }
    [IO.File]::WriteAllText((Join-Path $root 'scenario.txt'), $Scenario)
    $hashes = @{}
    Get-ChildItem -LiteralPath $plugins -File | ForEach-Object { $hashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    if ($MissionTransitionsProbe) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "`r`n[Missions]`r`nEnabled = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'mission-transitions.enabled'), 'missions-v1')
    }
    if ($MissionIdentityProbe) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "IdentityContinuity = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'mission-identity.enabled'), 'identity-v1')
    }
    if ($ContentReferenceProbe) { [IO.File]::WriteAllText((Join-Path $root 'content-reference.enabled'), 'refs-v1') }
    if ($JournalMissionEventsProbe) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmissionjournal.cfg'), "`r`n[Missions]`r`nUseApiMissionEvents = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'journal-mission-events.enabled'), 'journal-events-v1')
    }
    @{ journalMissionEventsProbe=[bool]$JournalMissionEventsProbe; missionIdentityProbe=[bool]$MissionIdentityProbe; missionTransitionsProbe=[bool]$MissionTransitionsProbe; contentReferenceProbe=[bool]$ContentReferenceProbe; stockpileCoordinated=[bool]$StockpileCoordinated; journalCoordinated=[bool]$JournalCoordinated; persistenceProbe=[bool]$PersistenceProbe; vanillaLoadControl=[bool]$VanillaLoadControl; assemblyOverlay=$overlay; stockpile=[bool]$StockpileBin; missionJournal=[bool]$MissionJournalBin; scenario=$Scenario; revision=$BuildRevision; preparedUtc=[DateTime]::UtcNow.ToString('o'); plugins=$hashes } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'build-provenance.json')
    # Prevent Steam's restart path; the runner disables SteamManager before arming checks.
    [IO.File]::WriteAllText((Join-Path $game 'steam_appid.txt'), '3471800')
    $saves = Join-Path $root 'Saves'
    New-Item -ItemType Directory -Path $saves | Out-Null
    Copy-Item -LiteralPath $sources[0].FullName -Destination (Join-Path $saves 'fixture-a.save')
    Copy-Item -LiteralPath $sources[1].FullName -Destination (Join-Path $saves 'fixture-b.save')
    if ($MissionJournalBin) { Copy-QualificationJournalHistory $sources $saves }
    if ($StockpileBin) {
        for ($i = 0; $i -lt 2; $i++) {
            $source = [IO.Path]::ChangeExtension($sources[$i].FullName, '.vgstockpile-transfers.json')
            $name = if ($i -eq 0) { 'fixture-a' } else { 'fixture-b' }
            $destination = Join-Path $saves ($name + '.vgstockpile-transfers.json')
            if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $destination }
            else { [IO.File]::WriteAllText($destination, '{"Version":1,"Items":[]}') }
        }
    }
    [IO.File]::WriteAllText((Join-Path $saves 'fixture-future.save'), '{"Version":"99.0.0.0","Player":{}}', (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $saves 'fixture-corrupt.save'), 'not a save', (New-Object Text.UTF8Encoding($false)))
    Write-Output "Prepared sandbox: $root"
    exit 0
}
if (!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $markerText) { throw 'Not a marked qualification sandbox.' }
if ($Action -eq 'Cleanup') {
    # Directory.Delete(non-recursive) unlinks a junction, never its target contents.
    foreach ($name in $junctions) {
        $path = Join-Path $game $name
        if (Test-Path -LiteralPath $path) {
            if ($name -eq 'VanguardGalaxy_Data' -and (Test-Path -LiteralPath (Join-Path $root 'assembly-overlay.hash'))) {
                if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Overlay data root unexpectedly linked; refusing traversal.' }
                foreach ($child in Get-ChildItem -LiteralPath $path -Force -Directory) {
                    if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($child.FullName, $false) }
                }
                continue # Keep the owned Managed copy; never recursively delete mixed resource trees.
            }
            if (!((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Expected a junction, not an ordinary directory: $name" }
            [IO.Directory]::Delete($path, $false)
        }
    }
    Write-Output 'Game-resource junctions unlinked. Private evidence and local copies retained.'
    exit 0
}
Assert-QualificationUnused $root
$provenance = Assert-QualificationInputs $root
$journalBefore = @{}
if ($provenance.PSObject.Properties['journalCoordinated'] -and $provenance.journalCoordinated) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'Saves') -Filter '*.vgmissionjournal.json' -File) { $journalBefore[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$transferBefore = @{}
if ($provenance.PSObject.Properties['stockpileCoordinated'] -and $provenance.stockpileCoordinated) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'Saves') -Filter '*.vgstockpile-transfers.json' -File) { $transferBefore[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash }
}
$negativeBefore = $null
if (($provenance.missionJournal -or $provenance.stockpile -or ($provenance.PSObject.Properties['vanillaLoadControl'] -and $provenance.vanillaLoadControl)) -and $provenance.scenario -ne 'Full') { $negativeBefore = SaveHashes @((Join-Path $root 'Saves')) }
# Unity PlayerPrefs are shared even with a separate executable. Preserve this inspected title's key.
$prefsNative = 'HKCU\Software\Bat Roost Games\VanguardGalaxy'
$prefsFile = Join-Path $root 'playerprefs-before.reg'
[IO.File]::WriteAllText((Join-Path $root 'run-started.txt'), [DateTime]::UtcNow.ToString('o'))
$hadPrefs = Save-QualificationPrefs $prefsNative $prefsFile
$exe = Join-Path $game 'VanguardGalaxy.exe'
$process = $null
try {
    $arguments = @('--fse-shim-applied','-screen-fullscreen','0','-logFile', ('"' + (Join-Path $root 'Player.log') + '"'), '--vgmodapi-qualification-root', ('"' + $root + '"'))
    if ($Diagnostics) { $arguments += '--vgmodapi-qualification-diagnostics' }
    $process = Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $arguments -PassThru
    @{ pid=$process.Id; executable=$exe } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'process.json')
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Owned game process timed out.' }
}
finally {
    try {
        if ($null -ne $process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        Get-Process VanguardGalaxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe } | ForEach-Object { Stop-Process -InputObject $_; $_.WaitForExit() }
    }
    finally {
        try {
            Restore-QualificationPrefs $prefsNative $prefsFile $hadPrefs
            [IO.File]::WriteAllText((Join-Path $root 'playerprefs-restored.txt'), 'PASS')
        }
        catch {
            # Preserve this failure even if the independent save-hash check also throws.
            try { [IO.File]::WriteAllText((Join-Path $root 'playerprefs-restore-failed.txt'), $_.Exception.ToString()) } catch { }
            throw
        }
        finally {
            $manifest = Get-Content -LiteralPath (Join-Path $root 'original-save-hashes.json') -Raw | ConvertFrom-Json
            $after = SaveHashes $manifest.directories
            $beforeKeys = @($manifest.files.PSObject.Properties.Name)
            if ($beforeKeys.Count -ne $after.Count) { throw 'Original save directory file set changed during qualification.' }
            foreach ($property in $manifest.files.PSObject.Properties) {
                if ($after[$property.Name] -ne $property.Value) { throw "Original save changed: $($property.Name)" }
            }
            [IO.File]::WriteAllText((Join-Path $root 'original-saves-unchanged.txt'), 'PASS')
        }
    }
}
$result = Join-Path $root 'result.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
if ($null -ne $negativeBefore) {
    $negativeAfter = SaveHashes @((Join-Path $root 'Saves'))
    if ($negativeBefore.Count -ne $negativeAfter.Count) { throw 'Negative/control run changed sandbox file set; inspect the result for vanilla failure/quit saves.' }
    foreach ($key in $negativeBefore.Keys) {
        if ($negativeAfter[$key] -ne $negativeBefore[$key]) { throw 'Negative/control run changed a sandbox fixture or sidecar.' }
    }
    [IO.File]::WriteAllText((Join-Path $root 'negative-consumer-files-unchanged.txt'), 'PASS')
}
$null = Assert-QualificationInputs $root
if (!(Test-Path -LiteralPath $result)) { throw 'Game exited without a qualification result; inspect sandbox logs.' }
Assert-VanillaControlReceipt $root $provenance
Assert-PersistenceProbeReceipt $root $provenance
if ($provenance.PSObject.Properties['stockpileCoordinated'] -and $provenance.stockpileCoordinated) {
    $after = @(Get-ChildItem -LiteralPath (Join-Path $root 'Saves') -Filter '*.vgstockpile-transfers.json' -File)
    if ($after.Count -ne $transferBefore.Count + 1) { throw 'Unexpected coordinated transfer sidecar file set.' }
    foreach ($file in $after) {
        if ($file.Name -eq 'qa-stockpile-import-refusal.vgstockpile-transfers.json') {
            if ((Get-Content -LiteralPath $file.FullName -Raw) -ne '{ corrupt') { throw 'Protected import fixture changed.' }
        } elseif ($transferBefore[$file.Name] -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Legacy transfer source changed.' }
    }
}
if ($provenance.PSObject.Properties['journalCoordinated'] -and $provenance.journalCoordinated) {
    $journalAfter = @(Get-ChildItem -LiteralPath (Join-Path $root 'Saves') -Filter '*.vgmissionjournal.json' -File)
    if ($journalAfter.Count -ne $journalBefore.Count) { throw 'Coordinated journal changed legacy sidecar file set.' }
    foreach ($file in $journalAfter) { if ($journalBefore[$file.Name] -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'Coordinated journal changed legacy source bytes.' } }
}
if ((Get-Content -LiteralPath $result -TotalCount 1) -ne 'PASS') { throw 'Qualification failed; inspect sandbox logs.' }
