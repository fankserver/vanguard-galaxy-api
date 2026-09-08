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
    [string]$AnimaBin,
    [string]$AnimaRevision,
    [switch]$AnimaTravelProbe,
    [string]$EchoBin,
    [string]$EchoRevision,
    [switch]$EchoTravelProbe,
    [switch]$EchoAbsentProbe,
    [string]$TravelJournalBin,
    [string]$TravelJournalRevision,
    [string]$TravelJournalSha256,
    [switch]$TravelJournalComparison,
    [switch]$AssemblyOverlay,
    [switch]$VanillaLoadControl,
    [switch]$MenuInspection,
    [switch]$ForgeCommandProbe,
    [switch]$ForgeReadProbe,
    [switch]$ModMenuProbe,
    [switch]$ModInformationProbe,
    [string]$TlsFixture,
    [switch]$PersistenceProbe,
    [switch]$StoryProbe,
    [string]$BarConsumerManifest,
    [switch]$BarProbe,
    [switch]$BarLinkedStory,
    [switch]$BarColdSequence,
    [ValidateSet('absent','consumer')][string]$BarColdPhase,
    [string]$BarAuthorABin,
    [string]$BarAuthorBBin,
    [switch]$StoryColdSequence,
    [switch]$StoryDefinitionColdPhase,
    [switch]$StoryAbsentProbe,
    [string]$StoryDonorRoot,
    [string]$StoryCampaignBin,
    [string]$StoryJobBin,
    [switch]$JournalCoordinated,
    [switch]$StockpileCoordinated,
    [switch]$ContentReferenceProbe,
    [switch]$MissionTransitionsProbe,
    [switch]$MissionIdentityProbe,
    [switch]$JournalMissionEventsProbe,
    [switch]$TravelStation,
    [switch]$TravelCrossSystem,
    [switch]$TravelWormholeFixture,
    [switch]$TravelResilience,
    [switch]$TravelRecoveryContinuation,
    [switch]$TravelFastLane,
    [string]$BuildRevision = 'unknown',
    [switch]$Diagnostics,
    [ValidateSet('Full','MissingApi','UnavailableApi')][string]$Scenario = 'Full',
    # The optional native travel phases reserve their own process time on top of the base budget,
    # so the lifetime knob must be able to cover base + every selected phase.
    [ValidateRange(1,10800)][int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'qualification-profile.ps1')
. (Join-Path $PSScriptRoot 'qualification-inputs.ps1')
. (Join-Path $PSScriptRoot 'qualification-story.ps1')
. (Join-Path $PSScriptRoot 'qualification-bars.ps1')
. (Join-Path $PSScriptRoot 'qualification-bar-consumers.ps1')
if ($BarConsumerManifest) {
    $allowed = @('Action','SandboxRoot','GameDir','OriginalSaveDir','SaveA','SaveB','BuildRoot','BuildRevision','TimeoutSeconds','Diagnostics','Scenario','BarConsumerManifest')
    if ($Action -ne 'Prepare' -or $Scenario -ne 'Full' -or @($PSBoundParameters.Keys | Where-Object { $_ -notin $allowed }).Count) { throw 'Consumer bars require isolated Full preparation.' }
}
if ($BarProbe) {
    $allowed = @('Action','SandboxRoot','GameDir','OriginalSaveDir','SaveA','SaveB','BuildRoot','BuildRevision','TimeoutSeconds','Diagnostics','Scenario','BarProbe','BarLinkedStory','BarColdSequence','BarAuthorABin','BarAuthorBBin')
    if ($Action -ne 'Prepare' -or $Scenario -ne 'Full' -or !$BarAuthorABin -or !$BarAuthorBBin -or @($PSBoundParameters.Keys | Where-Object { $_ -notin $allowed }).Count) { throw 'Bar probe requires isolated Full preparation and both author binaries.' }
} elseif ($BarAuthorABin -or $BarAuthorBBin) { throw 'Bar author binaries require BarProbe.' }
if ($BarLinkedStory -and ($Action -ne 'Prepare' -or !$BarProbe -or $BarColdSequence)) { throw 'Linked bars require isolated bar preparation without a cold sequence.' }
if ($BarColdSequence -and ($Action -ne 'Prepare' -or !$BarProbe)) { throw 'Cold bar sequence requires bar preparation.' }
if ($BarColdPhase -and ($Action -ne 'Run' -or $StoryDefinitionColdPhase)) { throw 'Cold bar phase requires an independent Run.' }
. (Join-Path $PSScriptRoot 'qualification-story-cold.ps1')
if ($StoryColdSequence -and ($Action -ne 'Prepare' -or !$StoryProbe)) { throw 'Cold sequence requires StoryProbe preparation.' }
if ($StoryDefinitionColdPhase -and $Action -ne 'Run') { throw 'Cold phase is a Run-only selection.' }
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
    if ($StoryAbsentProbe) {
        if (!$StoryDonorRoot) { throw 'Absent-story probe requires a completed story donor sandbox.' }
        Assert-StoryDonorReceipt $StoryDonorRoot
        if (!(SamePath $SaveA (Join-Path $StoryDonorRoot 'Saves\qa-story-active.save'))) { throw 'Absent-story fixture must be the donor active snapshot.' }
        $donorResult = @(Get-Content -LiteralPath (Join-Path $StoryDonorRoot 'result.txt'))
        if ($donorResult[0] -cne 'PASS') { throw 'Story donor did not finish successfully.' }
    }
    if (!$StoryAbsentProbe -and $StoryDonorRoot) { throw 'Story donor requires absent-story probe.' }
    if ($StoryAbsentProbe -and ($StoryProbe -or $Scenario -ne 'Full' -or $StoryCampaignBin -or $StoryJobBin -or $MissionJournalBin -or $AnimaBin -or $StockpileBin -or $EchoBin -or $TravelJournalBin -or $PersistenceProbe)) { throw 'Absent-story probe requires isolated Full without authors or consumers.' }
    if ($StoryProbe -and ($Scenario -ne 'Full' -or !$StoryCampaignBin -or !$StoryJobBin -or $MissionJournalBin -or $AnimaBin -or $StockpileBin -or $EchoBin -or $TravelJournalBin -or $PersistenceProbe)) { throw 'Story probe requires Full, both author binaries, and no consumer or synthetic persistence probe.' }
    if (($StoryProbe -or $StoryAbsentProbe) -and ($TravelStation -or $TravelCrossSystem -or $TravelWormholeFixture -or $TravelResilience -or $TravelRecoveryContinuation -or $TravelFastLane -or $MissionTransitionsProbe -or $MissionIdentityProbe -or $ContentReferenceProbe -or $JournalMissionEventsProbe -or $JournalCoordinated -or $StockpileCoordinated -or $VanillaLoadControl -or $AssemblyOverlay -or $EchoAbsentProbe -or $EchoTravelProbe -or $AnimaTravelProbe -or $TravelJournalComparison)) { throw 'Story probe cannot be combined with optional probes.' }
    if (!$StoryProbe -and ($StoryCampaignBin -or $StoryJobBin)) { throw 'Story author binaries require StoryProbe.' }
    if ($MenuInspection -and ($Scenario -ne 'MissingApi' -or $VanillaLoadControl -or $MissionJournalBin -or $StockpileBin -or $AnimaBin -or $EchoBin -or $TravelJournalBin)) { throw 'Menu inspection requires an API-absent menu-only run without consumers.' }
    if ($ForgeCommandProbe) { $ForgeReadProbe = $true }
    if ($ForgeReadProbe) {
        $otherSwitches = @($PSBoundParameters.Keys | Where-Object { $PSBoundParameters[$_] -is [Management.Automation.SwitchParameter] -and $PSBoundParameters[$_].IsPresent -and $_ -notin @('ForgeReadProbe','ForgeCommandProbe','Diagnostics') })
        if ($Scenario -ne 'Full' -or $otherSwitches.Count -or $MissionJournalBin -or $StockpileBin -or $AnimaBin -or $EchoBin -or $TravelJournalBin -or $BarConsumerManifest) { throw 'Forge read probe requires Full without other probes or consumers.' }
    }
    if ($ModInformationProbe -and !$ModMenuProbe) { throw 'Information probe requires ModMenuProbe.' }
    if ($ModInformationProbe) {
        if (!$MissionJournalBin -or !$StockpileBin) { throw 'Full information qualification requires both real consumers.' }
        if (!$TlsFixture -or !(Test-Path -LiteralPath $TlsFixture -PathType Leaf) -or (Get-Item -LiteralPath $TlsFixture).Length -gt 16384 -or ((Get-Item -LiteralPath $TlsFixture).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Information probe requires a bounded, unlinked throwaway TLS fixture.' }
    } elseif ($TlsFixture) { throw 'TLS fixture requires information probe.' }
    if ($ModMenuProbe) {
        $otherSwitches = @($PSBoundParameters.Keys | Where-Object { $PSBoundParameters[$_] -is [Management.Automation.SwitchParameter] -and $PSBoundParameters[$_].IsPresent -and $_ -notin @('ModMenuProbe','ModInformationProbe','Diagnostics') })
        if ($Scenario -ne 'Full' -or $otherSwitches.Count -or ((!$ModInformationProbe) -and ($MissionJournalBin -or $StockpileBin)) -or $AnimaBin -or $EchoBin -or $TravelJournalBin) { throw 'Mod menu probe requires Full without unrelated probes or consumers.' }
    }
    if ($PersistenceProbe -and $Scenario -ne 'Full') { throw 'Persistence probe requires Full.' }
    # The two consumer travel probes own the SAME reused native travel phases, so exactly one may
    # own a run; this conflict is checked before any per-probe prerequisite.
    if ($EchoTravelProbe -and $AnimaTravelProbe) { throw 'The Anima and Echo consumer travel probes both own the reused travel phases; select one per run.' }
    # The archived-journal comparison owns the SAME two reused travel phases, so it is refused
    # together with either consumer travel probe.
    if ($TravelJournalComparison -and ($AnimaTravelProbe -or $EchoTravelProbe)) { throw 'The archived-journal comparison and a consumer travel probe both own the reused travel phases; select one per run.' }
    if ($TravelJournalBin -and ($TravelJournalRevision -notmatch '^[0-9a-f]{40}$' -or $TravelJournalSha256 -notmatch '^[0-9a-fA-F]{64}$')) { throw 'The archived TravelJournal requires its exact source revision and binary SHA-256; the archive is never rebuilt, so exactly one binary is accepted.' }
    if (($TravelJournalRevision -or $TravelJournalSha256) -and !$TravelJournalBin) { throw 'The archived TravelJournal pins were supplied without the archived binary.' }
    # The archived plugin is NEVER installed as a passive ridealong: it patches the game, so it is
    # only ever prepared for the run that compares it.
    if ($TravelJournalBin -and !$TravelJournalComparison) { throw 'The archived TravelJournal binary is only prepared for the archived-journal comparison; it is never installed as a passive ridealong.' }
    # ARCHIVE COMPARISON ONLY: the archived plugin patches the same native travel/save methods the
    # consumers observe, so no consumer plugin is prepared beside it - not even for a non-travel
    # selection. Refused here, before any file is copied, and again by provenance validation at Run.
    if (($TravelJournalBin -or $TravelJournalComparison) -and ($AnimaBin -or $EchoBin)) { throw 'The archived-journal comparison sandbox carries the archive alone; prepare it without a consumer plugin (-AnimaBin/-EchoBin).' }
    if ($TravelJournalComparison -and (!$TravelJournalBin -or !$TravelStation -or !$TravelCrossSystem -or !$TravelWormholeFixture)) { throw 'Archived-journal comparison requires the archived binary, both native travel phases and the wormhole fixture selection.' }
    if ($TravelJournalComparison -and $Scenario -ne 'Full') { throw 'Archived-journal comparison requires Full.' }
    if ($AnimaBin -and (!$MissionIdentityProbe -or !$MissionJournalBin -or $AnimaRevision -notmatch '^[0-9a-f]{40}$')) { throw 'Anima requires identity probes, journal-provided JSON runtime and exact source revision.' }
    if ($AnimaBin) { $null = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $AnimaBin 'VGAnima.dll')) }
    # The actual-consumer travel probe compares the installed consumer's own records against
    # arrivals the two native travel phases already qualify; the wormhole case needs the opt-in
    # fixture, without which no wormhole arrival can ever be witnessed.
    if ($AnimaTravelProbe -and (!$AnimaBin -or !$TravelStation -or !$TravelCrossSystem -or !$TravelWormholeFixture)) { throw 'Anima consumer travel probe requires the Anima consumer, both native travel phases and the wormhole fixture selection.' }
    # The Echo arrival-snap probe consumes the SAME two reused native travel phases as the Anima
    # consumer probe and owns their ordering, so exactly one consumer probe may own a run.
    if ($EchoBin -and $EchoRevision -notmatch '^[0-9a-f]{40}$') { throw 'Echo requires its exact source revision.' }
    if ($EchoBin) { $null = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $EchoBin 'VGEcho.dll')) }
    if ($EchoTravelProbe -and (!$EchoBin -or !$TravelStation -or !$TravelCrossSystem -or !$TravelWormholeFixture)) { throw 'Echo consumer travel probe requires the Echo consumer, both native travel phases and the wormhole fixture selection.' }
    if ($EchoTravelProbe -and $Scenario -ne 'Full') { throw 'Echo consumer travel probe requires Full.' }
    # The API-ABSENT control is a separate selection in its own MissingApi sandbox; it never runs
    # beside the Full probe.
    if ($EchoAbsentProbe -and (!$EchoBin -or $Scenario -ne 'MissingApi')) { throw 'Echo API-absent control requires the Echo consumer and the MissingApi scenario.' }
    if ($EchoAbsentProbe -and $EchoTravelProbe) { throw 'The Echo API-absent control and the Full arrival-snap probe are separate runs.' }
    if ($JournalMissionEventsProbe -and (!$JournalCoordinated -or !$MissionIdentityProbe)) { throw 'Journal mission events require API-managed journal and mission identity probes.' }
    if ($MissionIdentityProbe -and (!$MissionTransitionsProbe -or !$PersistenceProbe)) { throw 'Mission identity probe requires mission transitions and persistence probes.' }
    if ($MissionTransitionsProbe -and $Scenario -ne 'Full') { throw 'Mission transitions probe requires Full.' }
    if ($TravelStation -and $Scenario -ne 'Full') { throw 'Travel/station pilot requires Full.' }
    if ($TravelCrossSystem -and !$TravelStation) { throw 'Cross-system travel phase requires the travel/station selection.' }
    # Opt-in disposable sandbox test data. Without it the cross-system phase never creates native
    # content and a fixture world without a wormhole keeps reporting its honest mandatory NOT-RUN.
    if ($TravelWormholeFixture -and !$TravelCrossSystem) { throw 'Wormhole fixture creation requires the cross-system travel phase.' }
    # The resilience phase reuses the same [Travel] capability configuration and reserves its own
    # process time; it is independent of the cross-system phase.
    if ($TravelResilience -and !$TravelStation) { throw 'Travel resilience phase requires the travel/station selection.' }
    # The recovery/continuation phase drives its own in-system routes and its own multi-waypoint gate
    # route, so it needs the travel capability but not the cross-system phase.
    if ($TravelRecoveryContinuation -and !$TravelStation) { throw 'Travel recovery/continuation phase requires the travel/station selection.' }
    if ($TravelRecoveryContinuation -and $Scenario -ne 'Full') { throw 'Travel recovery/continuation phase requires Full.' }
    # The fast-lane phase drives its own two-gate planner route, so it needs the travel capability
    # but neither the cross-system nor the recovery phase.
    if ($TravelFastLane -and !$TravelStation) { throw 'Travel fast-lane phase requires the travel/station selection.' }
    if ($TravelFastLane -and $Scenario -ne 'Full') { throw 'Travel fast-lane phase requires Full.' }
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
        foreach ($name in @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','vgmodapi.vgmod.json')) {
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
    $travelJournalVersion = ''
    if ($TravelJournalBin) {
        # The archived plugin is installed UNCHANGED: only its prebuilt DLL is copied, never its PDB
        # or deps.json, and only when its bytes are exactly the pinned, source-attested build.
        $candidate = Join-Path $TravelJournalBin 'VGTravelJournal.dll'
        $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        Assert-TravelJournalPins $TravelJournalRevision $TravelJournalSha256 $actualHash
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        $reader = Read-ConsumerAssembly $candidate (Get-ConsumerMetadataReferenceDirs $TravelJournalBin $root $GameDir)
        try {
            Assert-TravelJournalAssemblyMetadata $reader.Assembly $TravelJournalRevision
            $travelJournalVersion = $reader.Assembly.Name.Version.ToString()
        } finally { Close-ConsumerAssembly $reader }
        Copy-Item -LiteralPath $candidate -Destination $plugins
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        # Sandbox-only journal configuration. MaxEvents = 0 is the archived plugin's own documented
        # unbounded value, so its FIFO eviction can never silently drop a compared row.
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgtraveljournal.cfg'), "[Journal]`r`nVerbose = true`r`nMaxEvents = 0`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'travel-journal-revision.txt'), $TravelJournalRevision)
        [IO.File]::WriteAllText((Join-Path $root 'travel-journal.pinned-sha256.txt'), $actualHash)
    }
    $echoVersion = ''
    if ($EchoBin) {
        $candidate = Join-Path $EchoBin 'VGEcho.dll'
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        # Bounded explicit resolver: Echo's dependency flags are an ENUM declared in BepInEx.dll, and
        # an unresolved enum silently decodes to zero arguments (qa-87). See qualification-inputs.ps1.
        $reader = Read-ConsumerAssembly $candidate (Get-ConsumerMetadataReferenceDirs $EchoBin $root $GameDir)
        try {
            Assert-EchoAssemblyMetadata $reader.Assembly -TravelProbe:$EchoTravelProbe
            $echoVersion = $reader.Assembly.Name.Version.ToString()
        } finally { Close-ConsumerAssembly $reader }
        Copy-Item -LiteralPath $candidate -Destination $plugins
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        # Sandbox-only Echo configuration: the arrival-snap master and feature on, ETA-sync OFF so
        # an ETA write can never be mistaken for an arrival snap during the isolated positives.
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgecho.cfg'), "[Autopilot]`r`nTimingEnabled = true`r`nEtaSync = false`r`nArrivalSnap = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'echo.enabled'), 'echo-v1')
    }
    $animaVersion = ''
    if ($AnimaBin) {
        $candidate = Join-Path $AnimaBin 'VGAnima.dll'
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        # Anima's declaration takes two strings and needs no enum resolution, but it reads through
        # the same bounded resolver so a future enum argument cannot repeat the qa-87 silent decode.
        $reader = Read-ConsumerAssembly $candidate (Get-ConsumerMetadataReferenceDirs $AnimaBin $root $GameDir)
        try {
            Assert-AnimaAssemblyMetadata $reader.Assembly -TravelProbe:$AnimaTravelProbe
            $animaVersion = $reader.Assembly.Name.Version.ToString()
        } finally { Close-ConsumerAssembly $reader }
        Copy-Item -LiteralPath $candidate -Destination $plugins
        New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $bep 'config\vganima.cfg'), "[General]`r`nEnabled = true`r`n[Llm]`r`nEnabled = false`r`nBaseUrl = `r`nApiKey = `r`n")
        [IO.File]::WriteAllText((Join-Path $root 'anima-missions.enabled'), 'anima-v1')
    }
    # Unselected pilots explicitly use legacy mode; selected pilots exercise the new defaults.
    New-Item -ItemType Directory -Path (Join-Path $bep 'config') -Force | Out-Null
    if ($StockpileBin -and !$StockpileCoordinated) { [IO.File]::AppendAllText((Join-Path $bep 'config\vgstockpile.cfg'), "[Persistence]`r`nUseApiSaveData = false`r`n") }
    if ($MissionJournalBin -and !$JournalCoordinated) { [IO.File]::WriteAllText((Join-Path $bep 'config\vgmissionjournal.cfg'), "[Persistence]`r`nUseApiSaveData = false`r`n") }
    if ($StoryProbe -or $StoryAbsentProbe) {
        Add-Type -Path (Join-Path $bep 'core\Mono.Cecil.dll')
        foreach ($name in @('VGModAPI','VGModAPI.Core','VGModAPI.Abstractions','QualificationRunner','QualificationGuard','LifecycleObserver')) {
            $reader = Read-ConsumerAssembly (Join-Path $plugins ($name + '.dll')) (Get-ConsumerMetadataReferenceDirs $plugins $root $GameDir)
            try { Assert-QualificationAssemblyRevision $reader.Assembly $name $BuildRevision }
            finally { Close-ConsumerAssembly $reader }
        }
        if ($StoryProbe) { foreach ($author in @(@('OwnedStoryCampaign',$StoryCampaignBin),@('OwnedStoryJob',$StoryJobBin))) {
            $candidate = Join-Path $author[1] ($author[0] + '.dll')
            $reader = Read-ConsumerAssembly $candidate (Get-ConsumerMetadataReferenceDirs $author[1] $root $GameDir)
            try { Assert-StoryAuthorMetadata $reader.Assembly $author[0] $BuildRevision }
            finally { Close-ConsumerAssembly $reader }
            Copy-Item -LiteralPath $candidate -Destination $plugins
        } }
        [IO.File]::WriteAllText((Join-Path $bep 'config\vgmodapi.cfg'), "[Persistence]`r`nEnabled = true`r`nRoot = $(Join-Path $root 'state')`r`n[Story]`r`nEnabled = true`r`nProtection = true`r`n[Missions]`r`nEnabled = true`r`n")
        if ($StoryProbe) { [IO.File]::WriteAllText((Join-Path $root 'story.enabled'), 'owned-story-v1') }
        if ($StoryAbsentProbe) { [IO.File]::WriteAllText((Join-Path $root 'story-absent.enabled'), 'owned-story-absent-v1') }
    }
    if ($BarProbe) { Initialize-BarProbe $root $BarAuthorABin $BarAuthorBBin }
    if ($BarLinkedStory) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "[Missions]`r`nEnabled = true`r`nIdentityContinuity = true`r`n[Story]`r`nEnabled = true`r`nProtection = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'bar-linked.enabled'), 'linked-bars-v1')
    }
    if ($BarConsumerManifest) { Initialize-BarConsumers $root $BarConsumerManifest }
    if (!$PersistenceProbe -and !$StoryProbe -and !$StoryAbsentProbe -and !$BarProbe -and !$BarConsumerManifest) { [IO.File]::WriteAllText((Join-Path $bep 'config\vgmodapi.cfg'), "[Persistence]`r`nEnabled = false`r`n") }
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
    if ($ForgeReadProbe) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "`r`n[Recipes]`r`nEnabled = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'forge-reads.enabled'), 'forge-reads-v1')
        if ($ForgeCommandProbe) {
            [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "CommandsEnabled = true`r`n")
            [IO.File]::WriteAllText((Join-Path $root 'forge-commands.enabled'), 'forge-commands-v1')
        }
    }
    if ($MenuInspection) { [IO.File]::WriteAllText((Join-Path $root 'menu-inspection.enabled'), 'menu-inspection-v1') }
    if ($ModMenuProbe) { [IO.File]::WriteAllText((Join-Path $root 'mod-menu-probe.enabled'), 'mod-menu-probe-v3') }
    if ($ModInformationProbe) {
        Copy-Item -LiteralPath $TlsFixture -Destination (Join-Path $root 'untrusted-test.pfx')
        [IO.File]::WriteAllText((Join-Path $root 'mod-information-probe.enabled'), 'mod-information-probe-v3')
    }
    if ($ContentReferenceProbe) { [IO.File]::WriteAllText((Join-Path $root 'content-reference.enabled'), 'refs-v1') }
    if ($JournalMissionEventsProbe) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmissionjournal.cfg'), "`r`n[Missions]`r`nUseApiMissionEvents = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'journal-mission-events.enabled'), 'journal-events-v1')
    }
    if ($TravelStation) {
        [IO.File]::AppendAllText((Join-Path $bep 'config\vgmodapi.cfg'), "`r`n[Travel]`r`nEnabled = true`r`n")
        [IO.File]::WriteAllText((Join-Path $root 'travel-station.enabled'), 'travel-v1')
    }
    if ($TravelCrossSystem) { [IO.File]::WriteAllText((Join-Path $root 'travel-cross-system.enabled'), 'cross-system-v1') }
    if ($TravelWormholeFixture) { [IO.File]::WriteAllText((Join-Path $root 'travel-wormhole-fixture.enabled'), 'wormhole-fixture-v1') }
    if ($TravelResilience) { [IO.File]::WriteAllText((Join-Path $root 'travel-resilience.enabled'), 'resilience-v1') }
    if ($TravelRecoveryContinuation) { [IO.File]::WriteAllText((Join-Path $root 'travel-recovery.enabled'), 'recovery-continuation-v1') }
    if ($TravelFastLane) { [IO.File]::WriteAllText((Join-Path $root 'travel-fast-lane.enabled'), 'fast-lane-v1') }
    if ($AnimaTravelProbe) { [IO.File]::WriteAllText((Join-Path $root 'anima-travel.enabled'), 'anima-travel-v1') }
    if ($EchoTravelProbe) { [IO.File]::WriteAllText((Join-Path $root 'echo-travel.enabled'), 'echo-travel-v1') }
    if ($EchoAbsentProbe) { [IO.File]::WriteAllText((Join-Path $root 'echo-absent.enabled'), 'echo-absent-v1') }
    if ($TravelJournalComparison) { [IO.File]::WriteAllText((Join-Path $root 'travel-journal.enabled'), 'travel-journal-v1') }
    @{ forgeCommandProbe=[bool]$ForgeCommandProbe; forgeReadProbe=[bool]$ForgeReadProbe; barConsumers=[bool]$BarConsumerManifest; barConsumerManifestHash=$(if ($BarConsumerManifest) { (Get-FileHash -LiteralPath (Join-Path $root 'bar-consumer-sources.json') -Algorithm SHA256).Hash }); barConsumerTools=$(if ($BarConsumerManifest) { Get-BarConsumerToolsInventory $root }); barLinkedStory=[bool]$BarLinkedStory; barColdSequence=[bool]$BarColdSequence; barProbe=[bool]$BarProbe; storyColdSequence=[bool]$StoryColdSequence; storyDonorRoot=$StoryDonorRoot; storyDonorHash=$(if ($StoryAbsentProbe) { (Get-FileHash -LiteralPath $SaveA -Algorithm SHA256).Hash } else { '' }); storyAbsentProbe=[bool]$StoryAbsentProbe; storyProbe=[bool]$StoryProbe; menuInspection=[bool]$MenuInspection; modMenuProbe=[bool]$ModMenuProbe; modInformationProbe=[bool]$ModInformationProbe; modInformationCertificateSha256=$(if ($ModInformationProbe) { (Get-FileHash -LiteralPath (Join-Path $root 'untrusted-test.pfx') -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }); travelJournal=[bool]$TravelJournalBin; travelJournalRevision=$TravelJournalRevision; travelJournalSha256=$TravelJournalSha256; travelJournalVersion=$travelJournalVersion; travelJournalComparison=[bool]$TravelJournalComparison; travelJournalBudgetSeconds=$(if ($TravelJournalComparison) { $TravelJournalBudgetSeconds } else { 0 }); echo=[bool]$EchoBin; echoRevision=$EchoRevision; echoVersion=$echoVersion; echoTravelProbe=[bool]$EchoTravelProbe; echoTravelBudgetSeconds=$(if ($EchoTravelProbe) { $EchoTravelBudgetSeconds } else { 0 }); echoAbsentProbe=[bool]$EchoAbsentProbe; anima=[bool]$AnimaBin; animaRevision=$AnimaRevision; animaVersion=$animaVersion; animaTravelProbe=[bool]$AnimaTravelProbe; animaTravelBudgetSeconds=$(if ($AnimaTravelProbe) { $AnimaTravelBudgetSeconds } else { 0 }); journalMissionEventsProbe=[bool]$JournalMissionEventsProbe; missionIdentityProbe=[bool]$MissionIdentityProbe; missionTransitionsProbe=[bool]$MissionTransitionsProbe; contentReferenceProbe=[bool]$ContentReferenceProbe; stockpileCoordinated=[bool]$StockpileCoordinated; journalCoordinated=[bool]$JournalCoordinated; persistenceProbe=[bool]$PersistenceProbe; vanillaLoadControl=[bool]$VanillaLoadControl; assemblyOverlay=$overlay; stockpile=[bool]$StockpileBin; missionJournal=[bool]$MissionJournalBin; travelStation=[bool]$TravelStation; travelStationBudgetSeconds=$(if ($TravelStation) { $TravelStationBudgetSeconds } else { 0 }); travelCrossSystem=[bool]$TravelCrossSystem; travelCrossSystemBudgetSeconds=$(if ($TravelCrossSystem) { $TravelCrossSystemBudgetSeconds } else { 0 }); travelWormholeFixture=[bool]$TravelWormholeFixture; travelResilience=[bool]$TravelResilience; travelResilienceBudgetSeconds=$(if ($TravelResilience) { $TravelResilienceBudgetSeconds } else { 0 }); travelRecovery=[bool]$TravelRecoveryContinuation; travelRecoveryBudgetSeconds=$(if ($TravelRecoveryContinuation) { $TravelRecoveryBudgetSeconds } else { 0 }); travelFastLane=[bool]$TravelFastLane; travelFastLaneBudgetSeconds=$(if ($TravelFastLane) { $TravelFastLaneBudgetSeconds } else { 0 }); scenario=$Scenario; revision=$BuildRevision; preparedUtc=[DateTime]::UtcNow.ToString('o'); plugins=$hashes } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'build-provenance.json')
    # Prevent Steam's restart path; the runner disables SteamManager before arming checks.
    [IO.File]::WriteAllText((Join-Path $game 'steam_appid.txt'), '3471800')
    $saves = Join-Path $root 'Saves'
    New-Item -ItemType Directory -Path $saves | Out-Null
    if ($AnimaBin) {
        for ($i = 0; $i -lt 2; $i++) {
            $name = if ($i -eq 0) { 'fixture-a' } else { 'fixture-b' }
            $sidecar = $sources[$i].FullName + '.vganima.json'
            if (Test-Path -LiteralPath $sidecar -PathType Leaf) { Copy-Item -LiteralPath $sidecar -Destination (Join-Path $saves ($name + '.save.vganima.json')) }
        }
    }
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
if (!$StoryDefinitionColdPhase -and !$BarColdPhase) { Assert-QualificationUnused $root }
$provenance = Assert-QualificationInputs $root
if ($provenance.PSObject.Properties['modInformationProbe'] -and $provenance.modInformationProbe -and $TimeoutSeconds -lt 900) { throw 'Full information probe requires at least 900 seconds.' }
if ($provenance.PSObject.Properties['storyAbsentProbe'] -and $provenance.storyAbsentProbe -and $TimeoutSeconds -lt 2100) { throw 'Absent-story probe requires base plus300seconds (2100 total).' }
if ($provenance.PSObject.Properties['storyProbe'] -and $provenance.storyProbe -and $TimeoutSeconds -lt 5400) { throw 'Story probe requires 5400 seconds including objective reload/claim waits and execution margin.' }
# The travel/station phase adds its own bounded waits on top of every existing Full pilot, so the
# process lifetime must be reserved BEFORE launching: a launcher kill mid-phase would otherwise
# destroy a run that cannot finish. -TimeoutSeconds is the existing lifetime knob.
if ($provenance.PSObject.Properties['travelStation'] -and $provenance.travelStation) {
    $required = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds
    if ($provenance.PSObject.Properties['travelCrossSystem'] -and $provenance.travelCrossSystem) { $required += $TravelCrossSystemBudgetSeconds }
    if ($provenance.PSObject.Properties['travelResilience'] -and $provenance.travelResilience) { $required += $TravelResilienceBudgetSeconds }
    if ($provenance.PSObject.Properties['travelRecovery'] -and $provenance.travelRecovery) { $required += $TravelRecoveryBudgetSeconds }
    if ($provenance.PSObject.Properties['travelFastLane'] -and $provenance.travelFastLane) { $required += $TravelFastLaneBudgetSeconds }
    if ($provenance.PSObject.Properties['animaTravelProbe'] -and $provenance.animaTravelProbe) { $required += $AnimaTravelBudgetSeconds }
    if ($provenance.PSObject.Properties['echoTravelProbe'] -and $provenance.echoTravelProbe) { $required += $EchoTravelBudgetSeconds }
    if ($provenance.PSObject.Properties['travelJournalComparison'] -and $provenance.travelJournalComparison) { $required += $TravelJournalBudgetSeconds }
    if ($TimeoutSeconds -lt $required) { throw "Travel/station runs need -TimeoutSeconds at least $required (base $QualificationBaseTimeoutSeconds + phases); got $TimeoutSeconds." }
}
if ($StoryDefinitionColdPhase) { Start-StoryColdPhase $root $provenance }
if ($BarColdPhase) { Start-BarColdPhase $root $provenance $BarColdPhase }
$journalBefore = @{}
if ($provenance.PSObject.Properties['journalCoordinated'] -and $provenance.journalCoordinated) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'Saves') -Filter '*.vgmissionjournal.json' -File) { $journalBefore[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$travelJournalRoots = @()
$travelJournalBefore = @{}
if ($provenance.PSObject.Properties['travelJournalComparison'] -and $provenance.travelJournalComparison) {
    # Sampled BEFORE the launch so the post-exit audit can name exactly what this run created.
    $travelJournalRoots = Get-TravelJournalAuditRoots $root
    $travelJournalBefore = Get-TravelJournalFiles $travelJournalRoots
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
$outcome = @{ timedOut = $false; killed = $false; exitCode = $null }
try {
    $arguments = @('--fse-shim-applied','-screen-fullscreen','0','-logFile', ('"' + (Join-Path $root 'Player.log') + '"'), '--vgmodapi-qualification-root', ('"' + $root + '"'))
    if ($Diagnostics) { $arguments += '--vgmodapi-qualification-diagnostics' }
    if ($StoryDefinitionColdPhase) { $arguments += '--vgmodapi-story-definition-cold' }
    if ($provenance.PSObject.Properties['barConsumers'] -and $provenance.barConsumers) { $arguments += '--vgmodapi-bar-consumers' }
    if ($provenance.PSObject.Properties['barProbe'] -and $provenance.barProbe) {
        $arguments += $(if ($BarColdPhase) { '--vgmodapi-bars-cold-' + $BarColdPhase } elseif ($provenance.PSObject.Properties['barLinkedStory'] -and $provenance.barLinkedStory) { '--vgmodapi-bars-linked' } else { '--vgmodapi-bars-only' })
    }
    # The handle is cached inside the helper before any wait, so a genuine exit code is observable.
    $process = Start-QualificationProcess $exe $game $arguments
    @{ pid=$process.Id; executable=$exe } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'process.json')
    $outcome = Wait-QualificationProcess $process $TimeoutSeconds
}
finally {
    try {
        if ($null -ne $process -and !$process.HasExited) { $outcome.killed = $true; $process.Kill(); $null = $process.WaitForExit(30000) }
        if ($null -ne $process -and $null -eq $outcome.exitCode -and $process.HasExited) { $outcome.exitCode = $process.ExitCode }
        Get-Process VanguardGalaxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe } | ForEach-Object { $outcome.killed = $true; Stop-Process -InputObject $_; $_.WaitForExit() }
    }
    finally {
        # Diagnostic finalization: record exactly how the owned process ended, with no claim beyond
        # it. Receipt validation refuses a terminated run, so an incomplete pilot cannot look green.
        @{ timedOut=$outcome.timedOut; killed=$outcome.killed; exitCode=$outcome.exitCode;
           selfTerminated=($null -ne $outcome.exitCode -and [int]$outcome.exitCode -eq $GameSelfTerminationExitCode);
           timeoutSeconds=$TimeoutSeconds; endedUtc=[DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'run-outcome.json')
        if ($null -ne $process) { $process.Dispose(); $process = $null }
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
if ($outcome.timedOut) { throw 'Owned game process timed out.' }
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
if ($provenance.PSObject.Properties['travelJournalComparison'] -and $provenance.travelJournalComparison) {
    # The in-run containment case can only speak as of its own phase boundary; the archived plugin
    # still writes when the game quits, so the final location evidence is taken here, after exit.
    Assert-TravelJournalContainment $root $travelJournalRoots $travelJournalBefore
}
$null = Assert-QualificationInputs $root
if (!(Test-Path -LiteralPath $result)) { throw 'Game exited without a qualification result; inspect sandbox logs.' }
if ($provenance.PSObject.Properties['storyProbe'] -and $provenance.storyProbe) {
    if ($StoryDefinitionColdPhase) { Assert-StoryColdReceipt $root }
    else { Assert-StoryReceipt $root }
}
if ($provenance.PSObject.Properties['storyAbsentProbe'] -and $provenance.storyAbsentProbe) { Assert-StoryAbsentReceipt $root }
if ($provenance.PSObject.Properties['barConsumers'] -and $provenance.barConsumers) { Assert-BarConsumerReceipt $root }
if ($provenance.PSObject.Properties['barProbe'] -and $provenance.barProbe) {
    if ($BarColdPhase) { Assert-BarColdReceipt $root $BarColdPhase } elseif ($provenance.PSObject.Properties['barLinkedStory'] -and $provenance.barLinkedStory) { Assert-BarLinkedReceipt $root } else { Assert-BarReceipt $root }
}
Assert-VanillaControlReceipt $root $provenance
Assert-ForgeReadReceipt $root $provenance
Assert-ForgeCommandReceipt $root $provenance
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
if ($provenance.PSObject.Properties['barColdSequence'] -and $provenance.barColdSequence) {
    $phase = if ($BarColdPhase) { $BarColdPhase } else { 'producer' }
    $finalized = Join-Path $root ('bar-' + $phase + '-finalized.txt')
    [IO.File]::WriteAllText(($finalized + '.tmp'), 'PASS')
    [IO.File]::Move(($finalized + '.tmp'), $finalized)
}
if (!$StoryDefinitionColdPhase -and $provenance.PSObject.Properties['storyColdSequence'] -and $provenance.storyColdSequence) {
    $finalized = Join-Path $root 'story-producer-finalized.txt'
    [IO.File]::WriteAllText(($finalized + '.tmp'), 'PASS')
    [IO.File]::Move(($finalized + '.tmp'), $finalized)
}
