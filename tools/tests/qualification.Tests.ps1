# Windows-only, fake files only. Does not launch Unity or touch real game/profile data.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\qualification.ps1'
. (Join-Path $PSScriptRoot '..\qualification-inputs.ps1')
# Hosted Windows TEMP can use an 8.3 alias; match FileInfo's canonical full paths.
$work = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('vgmodapi-harness-test-' + [Guid]::NewGuid().ToString('N'))))
$fakeGame = Join-Path $work 'installed'
$build = Join-Path $work 'build'
$original = Join-Path $work 'original'
$fixtures = Join-Path $work 'fixtures'
$sandbox = Join-Path $work 'sandbox'
$sandboxes = @($sandbox)
function Put($relative, $text) {
    $path = Join-Path $work $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}
function Assert($condition, $message) { if (!$condition) { throw $message } }
try {
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','BepInEx\core\BepInEx.Preloader.dll')) { Put "installed\$name" 'fake-not-executable' }
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) { Put "installed\$name\sentinel.txt" 'keep' }
    Put 'installed\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll' 'synthetic-original-assembly'
    Put 'installed\VanguardGalaxy_Data\Resources\sentinel.txt' 'resource-keep'
    Put 'installed\doorstop_config.ini' "[General]`ntarget_assembly=C:\outside\BepInEx.Preloader.dll"
    foreach ($name in @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','unexpected.dll')) { Put "build\artifacts\VGModAPI\$name" 'fake-assembly' }
    Put 'build\tools\QualificationGuard\bin\Release\netstandard2.1\QualificationGuard.dll' 'fake-guard'
    Put 'build\tools\QualificationRunner\bin\Release\netstandard2.1\QualificationRunner.dll' 'fake-runner'
    Put 'build\examples\LifecycleObserver\bin\Release\netstandard2.1\LifecycleObserver.dll' 'fake-observer'
    Put 'original\real.save' 'original'
    Put 'fixtures\a.save' 'fixture-a'
    Put 'fixtures\b.save' 'fixture-b'
    $options = @{ GameDir=$fakeGame; OriginalSaveDir=$original; SaveA=(Join-Path $fixtures 'a.save'); SaveB=(Join-Path $fixtures 'b.save'); BuildRoot=$build; BuildRevision='fixture-test' }
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal') -JournalCoordinated @options }
    catch { $rejected = $_.Exception.Message -like '*Coordinated journal requires*' }
    Assert $rejected 'Journal coordinated selection accepted without required inputs.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-anima') -AnimaBin $build @options }
    catch { $rejected = $_.Exception.Message -like '*Anima requires*' }
    Assert $rejected 'Anima accepted without safe prerequisites.'
    Put 'bad-anima\VGAnima.dll' 'not-an-assembly'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'bad-anima-root') -AnimaBin (Join-Path $work 'bad-anima') -AnimaRevision ('a' * 40) -MissionIdentityProbe -MissionTransitionsProbe -PersistenceProbe -MissionJournalBin $build @options }
    catch { $rejected = $_.Exception.ToString() -match 'GetAssemblyName|BadImageFormat|manifest' }
    Assert $rejected 'Non-assembly Anima input was accepted or failed at an unrelated gate.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'bad-anima-root'))) 'Bad Anima input left a prepared sandbox.'
    function AnimaMetadata($version, $minimum) {
        return [pscustomobject]@{
            Name=[pscustomobject]@{Name='VGAnima';Version=[Version]$version}
            MainModule=[pscustomobject]@{Types=@([pscustomobject]@{FullName='VGAnima.Plugin';CustomAttributes=@([pscustomobject]@{
                AttributeType=[pscustomobject]@{FullName='BepInEx.BepInDependency'}
                ConstructorArguments=@([pscustomobject]@{Value='vgmodapi'},[pscustomobject]@{Value=$minimum})
            })})}
        }
    }
    Assert-AnimaAssemblyMetadata (AnimaMetadata '0.3.0.0' '0.1.8')
    foreach ($metadata in @((AnimaMetadata '0.2.0.0' '0.1.8'), (AnimaMetadata '0.3.0.0' '0.1.1'))) {
        $rejected = $false
        try { Assert-AnimaAssemblyMetadata $metadata } catch { $rejected = $true }
        Assert $rejected 'Unsupported Anima version/dependency metadata accepted.'
    }
    & $script -Action Prepare -SandboxRoot $sandbox @options
    $legacyConfigPath = Join-Path $sandbox 'game\BepInEx\config\vgmodapi.cfg'
    $legacyConfig = [IO.File]::ReadAllText($legacyConfigPath)
    $null = Assert-QualificationInputs $sandbox
    foreach ($changed in @('[Persistence]', $legacyConfig.Replace('false','true'), ($legacyConfig + "Enabled = false`n"))) {
        [IO.File]::WriteAllText($legacyConfigPath, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
        Assert $rejected 'Missing, enabled or duplicate legacy control setting accepted.'
    }
    [IO.File]::WriteAllText($legacyConfigPath, $legacyConfig)
    $overlayRoot = Join-Path $work 'overlay-sandbox'
    $sandboxes += $overlayRoot
    & $script -Action Prepare -SandboxRoot $overlayRoot -Scenario UnavailableApi -AssemblyOverlay @options
    $overlayProvenance = Assert-QualificationInputs $overlayRoot
    Assert ($overlayProvenance.assemblyOverlay.original -ne $overlayProvenance.assemblyOverlay.modified) 'Overlay did not change identity.'
    Assert ((Get-Content -LiteralPath (Join-Path $fakeGame 'VanguardGalaxy_Data\Managed\Assembly-CSharp.dll')) -eq 'synthetic-original-assembly') 'Overlay changed original assembly.'
    $overlayMarker = Join-Path $overlayRoot 'assembly-overlay.hash'
    $markerBytes = [IO.File]::ReadAllBytes($overlayMarker)
    [IO.File]::WriteAllText($overlayMarker, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $overlayRoot } catch { $rejected = $true }
    Assert $rejected 'Changed overlay marker accepted.'
    [IO.File]::WriteAllBytes($overlayMarker, $markerBytes)
    foreach ($assemblyPath in @((Join-Path $overlayRoot 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll'), $overlayProvenance.assemblyOverlay.source)) {
        $bytes = [IO.File]::ReadAllBytes($assemblyPath)
        [IO.File]::WriteAllText($assemblyPath, 'tampered-synthetic')
        $rejected = $false
        try { $null = Assert-QualificationInputs $overlayRoot } catch { $rejected = $true }
        Assert $rejected 'Changed copied/source assembly accepted.'
        [IO.File]::WriteAllBytes($assemblyPath, $bytes)
    }
    $overlayCopy = Join-Path $overlayRoot 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll'
    $copyBytes = [IO.File]::ReadAllBytes($overlayCopy)
    $overlayProvPath = Join-Path $overlayRoot 'build-provenance.json'
    $overlayProvBytes = [IO.File]::ReadAllBytes($overlayProvPath)
    [IO.File]::WriteAllText($overlayCopy, 'different-code-not-an-overlay')
    $overlayProvenance.assemblyOverlay.modified = (Get-FileHash -LiteralPath $overlayCopy).Hash
    $overlayProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $overlayProvPath
    [IO.File]::WriteAllLines($overlayMarker, [string[]]@($overlayProvenance.assemblyOverlay.modified, $overlayProvenance.assemblyOverlay.original))
    $rejected = $false
    try { $null = Assert-QualificationInputs $overlayRoot } catch { $rejected = $true }
    Assert $rejected 'Consistently repinned non-overlay bytes accepted.'
    [IO.File]::WriteAllBytes($overlayCopy, $copyBytes)
    [IO.File]::WriteAllBytes($overlayProvPath, $overlayProvBytes)
    [IO.File]::WriteAllBytes($overlayMarker, $markerBytes)
    & $script -Action Cleanup -SandboxRoot $overlayRoot
    Assert (!(Test-Path -LiteralPath (Join-Path $overlayRoot 'game\VanguardGalaxy_Data\Resources'))) 'Nested resource junction survived cleanup.'
    Assert (Test-Path -LiteralPath (Join-Path $overlayRoot 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll')) 'Private Managed evidence removed.'
    Assert ((Get-Content -LiteralPath (Join-Path $fakeGame 'VanguardGalaxy_Data\Resources\sentinel.txt')) -eq 'resource-keep') 'Cleanup modified source resource.'
    [IO.File]::WriteAllText((Join-Path $sandbox 'assembly-overlay.hash'), 'forged')
    $rejected = $false
    try { & $script -Action Cleanup -SandboxRoot $sandbox } catch { $rejected = $true }
    Assert $rejected 'Overlay cleanup traversed an ordinary data junction.'
    Remove-Item -LiteralPath (Join-Path $sandbox 'assembly-overlay.hash')
    $probeRoot = Join-Path $work 'persistence-probe-sandbox'
    $sandboxes += $probeRoot
    & $script -Action Prepare -SandboxRoot $probeRoot -PersistenceProbe @options
    $probeProvenance = Assert-QualificationInputs $probeRoot
    $apiConfigPath = Join-Path $probeRoot 'game\BepInEx\config\vgmodapi.cfg'
    $defaultApiConfig = [IO.File]::ReadAllText($apiConfigPath)
    Assert ($defaultApiConfig -notmatch '(?m)^Enabled\s*=') 'Prepared probe does not exercise the enabled default.'
    foreach ($setting in @("Enabled = false`n", "Enabled = true`nEnabled = false`n")) {
        [IO.File]::WriteAllText($apiConfigPath, $defaultApiConfig + $setting)
        $rejected = $false
        try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
        Assert $rejected 'Disabled or ambiguous persistence setting accepted.'
    }
    [IO.File]::WriteAllText($apiConfigPath, $defaultApiConfig)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing persistence receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'persistence-probe.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    [IO.File]::WriteAllText((Join-Path $probeRoot 'missionjournal.enabled'), 'pilot-v1')
    $probeProvenance.journalCoordinated = $true; $probeProvenance.missionJournal = $true
    foreach ($name in @('VGMissionJournal.dll','Newtonsoft.Json.dll')) {
        $fake = Join-Path $probeRoot "game\BepInEx\plugins\$name"
        [IO.File]::WriteAllText($fake, 'synthetic-not-executable')
        $probeProvenance.plugins | Add-Member -NotePropertyName $name -NotePropertyValue (Get-FileHash -LiteralPath $fake).Hash
    }
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    [IO.File]::WriteAllText((Join-Path $probeRoot 'journal-coordinated.enabled'), 'journal-v1')
    $journalConfig = Join-Path $probeRoot 'game\BepInEx\config\vgmissionjournal.cfg'
    $validJournal = "[Persistence]`nImportLegacySidecars = true`n"
    [IO.File]::WriteAllText($journalConfig, $validJournal)
    $null = Assert-QualificationInputs $probeRoot
    foreach ($changed in @($validJournal.Replace('true','false'), ($validJournal + "UseApiSaveData = false`n"), ($validJournal + "UseApiSaveData = true`nUseApiSaveData = false`n"), ($validJournal + "ImportLegacySidecars = false`n"), $validJournal.Replace('[Persistence]','[Other]'))) {
        [IO.File]::WriteAllText($journalConfig, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
        Assert $rejected 'Changed journal configuration accepted.'
    }
    [IO.File]::WriteAllText($journalConfig, $validJournal)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing actual-journal receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'journal-coordinated.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.stockpileCoordinated = $true; $probeProvenance.stockpile = $true
    [IO.File]::WriteAllText((Join-Path $probeRoot 'stockpile.enabled'), 'pilot-v1')
    [IO.File]::WriteAllText((Join-Path $probeRoot 'stockpile-coordinated.enabled'), 'stockpile-v1')
    $fake = Join-Path $probeRoot 'game\BepInEx\plugins\VGStockpile.dll'
    [IO.File]::WriteAllText($fake, 'synthetic-not-executable')
    $probeProvenance.plugins | Add-Member -NotePropertyName 'VGStockpile.dll' -NotePropertyValue (Get-FileHash -LiteralPath $fake).Hash
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $stockpileConfig = Join-Path $probeRoot 'game\BepInEx\config\vgstockpile.cfg'
    [IO.File]::WriteAllText($stockpileConfig, $validJournal)
    $null = Assert-QualificationInputs $probeRoot
    [IO.File]::WriteAllText($stockpileConfig, $validJournal.Replace('true','false'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed coordinated transfer configuration accepted.'
    [IO.File]::WriteAllText($stockpileConfig, $validJournal)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing actual transfer receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'stockpile-coordinated.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.contentReferenceProbe = $true
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $contentMarker = Join-Path $probeRoot 'content-reference.enabled'
    [IO.File]::WriteAllText($contentMarker, 'refs-v1')
    $null = Assert-QualificationInputs $probeRoot
    [IO.File]::WriteAllText($contentMarker, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed content-reference marker accepted.'
    [IO.File]::WriteAllText($contentMarker, 'refs-v1')
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing content-reference receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'content-reference.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.missionTransitionsProbe = $true
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $missionMarker = Join-Path $probeRoot 'mission-transitions.enabled'
    [IO.File]::WriteAllText($missionMarker, 'missions-v1')
    $apiConfig = Join-Path $probeRoot 'game\BepInEx\config\vgmodapi.cfg'
    [IO.File]::AppendAllText($apiConfig, "`n[Missions]`nEnabled = true`n")
    $validApi = [IO.File]::ReadAllText($apiConfig)
    $null = Assert-QualificationInputs $probeRoot
    foreach ($changed in @($validApi.Replace('[Missions]', '[Other]'), ($validApi + "Enabled = false`n"))) {
        [IO.File]::WriteAllText($apiConfig, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
        Assert $rejected 'Changed mission config accepted.'
    }
    [IO.File]::WriteAllText($apiConfig, $validApi)
    [IO.File]::WriteAllText($missionMarker, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed mission marker accepted.'
    [IO.File]::WriteAllText($missionMarker, 'missions-v1')
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing mission receipt accepted.'
    foreach ($name in @('mission-transitions.txt','mission-clear.txt','mission-guild.txt','mission-waves.txt')) {
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
        Assert $rejected "Missing mission receipt accepted: $name"
        [IO.File]::WriteAllText((Join-Path $probeRoot $name), 'PASS')
    }
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.missionIdentityProbe = $true
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $identityMarker = Join-Path $probeRoot 'mission-identity.enabled'
    [IO.File]::WriteAllText($identityMarker, 'identity-v1')
    [IO.File]::AppendAllText($apiConfig, "IdentityContinuity = true`n")
    $null = Assert-QualificationInputs $probeRoot
    [IO.File]::WriteAllText($identityMarker, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed identity marker accepted.'
    [IO.File]::WriteAllText($identityMarker, 'identity-v1')
    [IO.File]::WriteAllText($apiConfig, $validApi + "IdentityContinuity = false`n")
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed identity config accepted.'
    [IO.File]::WriteAllText($apiConfig, $validApi + "IdentityContinuity = true`n")
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing identity receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'mission-identity.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.journalMissionEventsProbe = $true
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $journalEventsMarker = Join-Path $probeRoot 'journal-mission-events.enabled'
    [IO.File]::WriteAllText($journalEventsMarker, 'journal-events-v1')
    $journalEventConfig = Join-Path $probeRoot 'game\BepInEx\config\vgmissionjournal.cfg'
    $journalEventOriginal = Get-Content -LiteralPath $journalEventConfig -Raw
    [IO.File]::AppendAllText($journalEventConfig, "`n[Missions]`nUseApiMissionEvents = true`n")
    $null = Assert-QualificationInputs $probeRoot
    [IO.File]::WriteAllText($journalEventsMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed journal events marker accepted.'
    [IO.File]::WriteAllText($journalEventsMarker, 'journal-events-v1')
    [IO.File]::WriteAllText($journalEventConfig, $journalEventOriginal + "`n[Missions]`nUseApiMissionEvents = false`n")
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed journal events config accepted.'
    [IO.File]::WriteAllText($journalEventConfig, $journalEventOriginal + "`n[Missions]`nUseApiMissionEvents = true`n")
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing journal events receipt accepted.'
    [IO.File]::WriteAllText((Join-Path $probeRoot 'journal-mission-events.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $probeProvenance.anima = $true
    $probeProvenance.animaRevision = 'a' * 40
    $animaDll = Join-Path $probeRoot 'game\BepInEx\plugins\VGAnima.dll'
    [IO.File]::WriteAllText($animaDll, 'synthetic-anima')
    $probeProvenance.plugins | Add-Member -NotePropertyName 'VGAnima.dll' -NotePropertyValue (Get-FileHash $animaDll).Hash
    $probeProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeRoot 'build-provenance.json')
    $animaMarker = Join-Path $probeRoot 'anima-missions.enabled'
    [IO.File]::WriteAllText($animaMarker, 'anima-v1')
    $animaConfig = Join-Path $probeRoot 'game\BepInEx\config\vganima.cfg'
    $validAnima = "[General]`nEnabled = true`n[Llm]`nEnabled = false`nBaseUrl = `nApiKey = `n"
    [IO.File]::WriteAllText($animaConfig, $validAnima)
    $null = Assert-QualificationInputs $probeRoot
    foreach ($changed in @($validAnima.Replace('Enabled = false','Enabled = true'), $validAnima.Replace('Enabled = true','Enabled = false'), $validAnima.Replace('ApiKey = ', 'ApiKey = synthetic-key'), ($validAnima + "[Llm]`nEnabled = false`n"))) {
        [IO.File]::WriteAllText($animaConfig, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
        Assert $rejected 'Changed Anima network/enable configuration accepted.'
    }
    [IO.File]::WriteAllText($animaConfig, $validAnima)
    [IO.File]::WriteAllText($animaMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Changed Anima marker accepted.'
    [IO.File]::WriteAllText($animaMarker, 'anima-v1')
    foreach ($value in @($null,'FAIL')) {
        if ($value) { [IO.File]::WriteAllText((Join-Path $probeRoot 'anima-missions.txt'), $value) }
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
        Assert $rejected 'Missing/failed Anima receipt accepted.'
    }
    [IO.File]::WriteAllText((Join-Path $probeRoot 'anima-missions.txt'), 'PASS')
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    $travelRoot = Join-Path $work 'travel-station-sandbox'
    $sandboxes += $travelRoot
    & $script -Action Prepare -SandboxRoot $travelRoot -TravelStation @options
    $travelProvenance = Assert-QualificationInputs $travelRoot
    $travelConfig = Join-Path $travelRoot 'game\BepInEx\config\vgmodapi.cfg'
    $validTravelConfig = [IO.File]::ReadAllText($travelConfig)
    # The prepared config uses CRLF, so these mutations must too or they silently change nothing.
    Assert ($validTravelConfig -match "(?m)^\[Travel\]\r?\nEnabled = true\s*$") 'Prepared travel config shape changed.'
    foreach ($changed in @($validTravelConfig.Replace("[Travel]`r`nEnabled = true", "[Travel]`r`nEnabled = false"),
        $validTravelConfig.Replace("[Travel]`r`nEnabled = true", "[Travel]`r`nEnabled = true`r`nEnabled = true"),
        $validTravelConfig.Replace("[Travel]`r`nEnabled = true", ""))) {
        Assert ($changed -ne $validTravelConfig) 'Travel config mutation did not change the prepared file.'
        [IO.File]::WriteAllText($travelConfig, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $travelRoot } catch { $rejected = $true }
        Assert $rejected 'Changed Travel enable configuration accepted.'
    }
    [IO.File]::WriteAllText($travelConfig, $validTravelConfig)
    $travelMarker = Join-Path $travelRoot 'travel-station.enabled'
    [IO.File]::WriteAllText($travelMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $travelRoot } catch { $rejected = $true }
    Assert $rejected 'Changed travel/station marker accepted.'
    [IO.File]::WriteAllText($travelMarker, 'travel-v1')
    # Synthetic receipts only: the validator must reject a claimed PASS that the rows, the case
    # identities or the observed event trace do not actually support.
    $travelSession = [Guid]::NewGuid().ToString()
    $travelOperation = [Guid]::NewGuid().ToString()
    function TravelRow($case, $status, $session) { return ($case + "`tdescription`t" + $status + "`tsystem:poi`t" + $session + "`t`tdetail") }
    function TravelEvent($case, $session, $operation) { return ("1`ttravel`t" + $case + "`t" + $session + "`t" + $operation + "`tArrived`tInSystem`tsystem:a`tsystem:b`tsystem:b`t1.000`t") }
    function TravelSummary($rows, $first) {
        # The comma keeps each split row an array instead of unrolling its columns.
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelStationPhase", ("required=" + ($TravelStationRequiredCases -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelStationRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    function WriteTravelOutputs($rows, $events, $summary) {
        [IO.File]::WriteAllLines((Join-Path $travelRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $travelRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $travelRoot 'travel-station.txt'), [string[]]$summary)
    }
    function AssertTravelRejected($rows, $events, $summary, $message) {
        WriteTravelOutputs $rows $events $summary
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    $validRows = @($TravelStationRequiredCases | ForEach-Object { TravelRow $_ 'passed' $travelSession })
    $validRows += (TravelRow 'cross-system-jumpgate' 'not-run' $travelSession)
    $validEvents = @($TravelStationRequiredCases | ForEach-Object { TravelEvent $_ $travelSession $travelOperation })
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing travel/station receipt files accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    Assert-PersistenceProbeReceipt $travelRoot $travelProvenance
    $failedRows = @($validRows[0..4]) + @(TravelRow 'station-dock' 'failed' $travelSession)
    AssertTravelRejected $failedRows $validEvents (TravelSummary $failedRows 'PASS') 'Claimed PASS with a failed case row accepted.'
    Assert ((Test-Path -LiteralPath (Join-Path $travelRoot 'travel-station-receipt.tsv')) -and (Test-Path -LiteralPath (Join-Path $travelRoot 'travel-station-events.tsv'))) 'A failed attempt must keep its receipt and event diagnostics.'
    $skippedRows = @($TravelStationRequiredCases | ForEach-Object { TravelRow $_ 'not-run' $travelSession })
    AssertTravelRejected $skippedRows $validEvents (TravelSummary $skippedRows 'PASS') 'All-skipped coverage accepted as PASS.'
    $missingRows = @($validRows | Where-Object { $_ -notlike 'chained-route*' })
    AssertTravelRejected $missingRows $validEvents (TravelSummary $missingRows 'PASS') 'Missing mandatory case accepted.'
    $staleEvents = @($TravelStationRequiredCases | ForEach-Object { TravelEvent $_ ([Guid]::NewGuid().ToString()) $travelOperation })
    AssertTravelRejected $validRows $staleEvents (TravelSummary $validRows 'PASS') 'Receipt identities absent from the event trace accepted.'
    AssertTravelRejected $validRows @($validEvents | Where-Object { $_ -notlike '*station-dock*' }) (TravelSummary $validRows 'PASS') 'Required case without observed events accepted.'
    AssertTravelRejected $validRows $validEvents (TravelSummary $validRows 'FAIL') 'Failed attempt summary accepted.'
    $foreignPhase = @((TravelSummary $validRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { 'phase=travel-other-phase' } else { $_ } })
    AssertTravelRejected $validRows $validEvents $foreignPhase 'Foreign phase claim accepted.'
    $wrongCounts = (TravelSummary $validRows 'PASS')
    $wrongCounts[3] = 'rows=99 passed=99 failed=0 notRun=0'
    AssertTravelRejected $validRows $validEvents $wrongCounts 'Summary counts disagreeing with the receipt accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    [IO.File]::WriteAllLines((Join-Path $travelRoot 'travel-station-events.tsv'), [string[]]@("sequence`tkind") + [string[]]$validEvents)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
    Assert $rejected 'Changed event trace header accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    Assert-PersistenceProbeReceipt $travelRoot $travelProvenance
    & $script -Action Cleanup -SandboxRoot $travelRoot
    [IO.File]::WriteAllText($apiConfig, "[Persistence]`nEnabled = true`nRoot = C:\foreign-root`n[Missions]`nEnabled = true`nIdentityContinuity = true`n")
    $rejected = $false
    try { $null = Assert-QualificationInputs $probeRoot } catch { $rejected = $true }
    Assert $rejected 'Foreign persistence root accepted.'
    & $script -Action Cleanup -SandboxRoot $probeRoot
    $vanillaRoot = Join-Path $work 'vanilla-control-sandbox'
    $sandboxes += $vanillaRoot
    & $script -Action Prepare -SandboxRoot $vanillaRoot -Scenario MissingApi -VanillaLoadControl @options
    $vanillaProvenance = Assert-QualificationInputs $vanillaRoot
    $receipt = Join-Path $vanillaRoot 'vanilla-load-control.txt'
    $rejected = $false
    try { Assert-VanillaControlReceipt $vanillaRoot $vanillaProvenance } catch { $rejected = $true }
    Assert $rejected 'Old guard without control receipt accepted.'
    [IO.File]::WriteAllText($receipt, 'FAIL')
    $rejected = $false
    try { Assert-VanillaControlReceipt $vanillaRoot $vanillaProvenance } catch { $rejected = $true }
    Assert $rejected 'Failed control receipt accepted.'
    [IO.File]::WriteAllText($receipt, 'PASS')
    Assert-VanillaControlReceipt $vanillaRoot $vanillaProvenance
    $vanillaMarker = Join-Path $vanillaRoot 'vanilla-load.enabled'
    [IO.File]::WriteAllText($vanillaMarker, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $vanillaRoot } catch { $rejected = $true }
    Assert $rejected 'Invalid vanilla load control marker accepted.'
    & $script -Action Cleanup -SandboxRoot $vanillaRoot
    $future = Get-Content (Join-Path $sandbox 'Saves\fixture-future.save') -Raw | ConvertFrom-Json
    Assert ($future.Version -eq '99.0.0.0') 'Future fixture must use valid two-digit-or-shorter version segments.'
    $manifest = Get-Content (Join-Path $sandbox 'original-save-hashes.json') -Raw | ConvertFrom-Json
    Assert (@($manifest.files.PSObject.Properties.Name) -contains (Join-Path $original 'real.save')) 'Real save directory was not protected when fixtures live elsewhere.'
    Assert (@($manifest.files.PSObject.Properties).Count -eq 3) 'Expected original and both fixture hashes.'
    Assert (!(Test-Path (Join-Path $sandbox 'game\BepInEx\plugins\unexpected.dll'))) 'Package allowlist failed.'
    $config = Get-Content (Join-Path $sandbox 'game\doorstop_config.ini') -Raw
    Assert ($config.Contains("[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Preloader.dll") -and !$config.Contains('C:\outside')) 'Doorstop 4 preloader config is not enabled and sandbox-relative.'
    Assert ($config.Contains('[UnityMono]') -and !$config.Contains('[UnityDoorstop]') -and !$config.Contains('targetAssembly=')) 'Legacy Doorstop keys must not replace the inspected format.'
    $provenance = Get-Content (Join-Path $sandbox 'build-provenance.json') -Raw | ConvertFrom-Json
    Assert (@($provenance.plugins.PSObject.Properties).Count -eq 6) 'Missing plugin provenance.'
    foreach ($mode in @('MissingApi','UnavailableApi')) {
        $other = Join-Path $work $mode
        $sandboxes += $other
        & $script -Action Prepare -SandboxRoot $other -Scenario $mode @options
        $p = Get-Content (Join-Path $other 'build-provenance.json') -Raw | ConvertFrom-Json
        $expected = if ($mode -eq 'MissingApi') { 1 } else { 4 }
        Assert (@($p.plugins.PSObject.Properties).Count -eq $expected) 'Wrong negative-scenario plugin set.'
        Assert ($p.scenario -eq $mode) 'Scenario provenance missing.'
        Assert (!(Test-Path (Join-Path $other 'game\BepInEx\plugins\QualificationRunner.dll'))) 'Negative scenario copied API-dependent runner.'
        Assert (Test-Path (Join-Path $other 'game\BepInEx\plugins\QualificationGuard.dll')) 'Independent guard missing.'
        & $script -Action Cleanup -SandboxRoot $other
    }
    $null = Assert-QualificationInputs $sandbox
    foreach ($mutation in @('extra-file','extra-directory','changed-hash','changed-scenario','consumer-marker')) {
        $extra = Join-Path $sandbox 'game\BepInEx\plugins\extra.dll'
        $dll = Join-Path $sandbox 'game\BepInEx\plugins\VGModAPI.dll'
        $modeFile = Join-Path $sandbox 'scenario.txt'
        switch ($mutation) {
            'extra-file' { [IO.File]::WriteAllText($extra, 'extra') }
            'extra-directory' { [IO.Directory]::CreateDirectory($extra) | Out-Null }
            'changed-hash' { [IO.File]::WriteAllText($dll, 'changed') }
            'changed-scenario' { [IO.File]::WriteAllText($modeFile, 'MissingApi') }
            'consumer-marker' { [IO.File]::WriteAllText((Join-Path $sandbox 'missionjournal.enabled'), 'pilot-v1') }
        }
        $rejected = $false
        try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
        Assert $rejected "Prepared input mutation accepted: $mutation"
        if (Test-Path -LiteralPath $extra) { Remove-Item -LiteralPath $extra -Force }
        $marker = Join-Path $sandbox 'missionjournal.enabled'
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
        [IO.File]::WriteAllText($dll, 'fake-assembly')
        [IO.File]::WriteAllText($modeFile, 'Full')
    }
    $provPath = Join-Path $sandbox 'build-provenance.json'
    $originalProvenance = Get-Content -LiteralPath $provPath -Raw
    $consumerProvenance = $originalProvenance | ConvertFrom-Json
    $consumerProvenance.missionJournal = $true
    $legacyJournal = Join-Path $sandbox 'game\BepInEx\config\vgmissionjournal.cfg'
    [IO.File]::WriteAllText($legacyJournal, "[Persistence]`nUseApiSaveData = false`n")
    foreach ($name in @('VGMissionJournal.dll','Newtonsoft.Json.dll')) {
        $file = Join-Path $sandbox ('game\BepInEx\plugins\' + $name)
        [IO.File]::WriteAllText($file, 'synthetic')
        $consumerProvenance.plugins | Add-Member -NotePropertyName $name -NotePropertyValue ((Get-FileHash $file -Algorithm SHA256).Hash)
    }
    $consumerProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $provPath
    $rejected = $false
    try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
    Assert $rejected 'Consumer provenance without marker accepted.'
    $marker = Join-Path $sandbox 'missionjournal.enabled'
    [IO.File]::WriteAllText($marker, 'pilot-v1')
    $null = Assert-QualificationInputs $sandbox
    foreach ($changed in @('[Persistence]', "[Persistence]`nUseApiSaveData = true`n", "[Persistence]`nUseApiSaveData = false`nUseApiSaveData = false`n")) {
        [IO.File]::WriteAllText($legacyJournal, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
        Assert $rejected 'Missing, enabled or duplicate legacy consumer setting accepted.'
    }
    [IO.File]::WriteAllText($legacyJournal, "[Persistence]`nUseApiSaveData = false`n")
    [IO.File]::WriteAllText($marker, 'invalid')
    $rejected = $false
    try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
    Assert $rejected 'Invalid consumer marker accepted.'
    [IO.File]::WriteAllText($marker, 'pilot-v1')
    $consumerProvenance.stockpile = $true
    [IO.File]::WriteAllText((Join-Path $sandbox 'game\BepInEx\config\vgstockpile.cfg'), "[Persistence]`nUseApiSaveData = false`n")
    $stockpileDll = Join-Path $sandbox 'game\BepInEx\plugins\VGStockpile.dll'
    [IO.File]::WriteAllText($stockpileDll, 'synthetic-stockpile')
    $consumerProvenance.plugins | Add-Member -NotePropertyName 'VGStockpile.dll' -NotePropertyValue ((Get-FileHash $stockpileDll).Hash)
    $consumerProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $provPath
    $rejected = $false
    try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
    Assert $rejected 'Stockpile provenance without marker accepted.'
    $stockpileMarker = Join-Path $sandbox 'stockpile.enabled'
    [IO.File]::WriteAllText($stockpileMarker, 'pilot-v1')
    $null = Assert-QualificationInputs $sandbox
    [IO.File]::WriteAllText($stockpileMarker, 'invalid')
    $rejected = $false
    try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
    Assert $rejected 'Invalid Stockpile marker accepted.'
    Remove-Item -LiteralPath $stockpileMarker,$stockpileDll -Force
    Remove-Item -LiteralPath $marker -Force
    foreach ($name in @('VGMissionJournal.dll','Newtonsoft.Json.dll')) { Remove-Item -LiteralPath (Join-Path $sandbox ('game\BepInEx\plugins\' + $name)) -Force }
    [IO.File]::WriteAllText($provPath, $originalProvenance)

    $historySources = @((Get-Item $options.SaveA), (Get-Item $options.SaveB))
    $rejected = $false
    try { Copy-QualificationJournalHistory $historySources (Join-Path $sandbox 'Saves') } catch { $rejected = $true }
    Assert $rejected 'Missing journal histories were accepted.'
    foreach ($source in $historySources) { [IO.File]::WriteAllText(($source.FullName + '.vgmissionjournal.json'), 'synthetic-history') }
    Copy-QualificationJournalHistory $historySources (Join-Path $sandbox 'Saves')
    Assert ((Get-Content (Join-Path $sandbox 'Saves\fixture-b.save.vgmissionjournal.json')) -eq 'synthetic-history') 'Journal history copy failed.'

    $badBin = Join-Path $work 'bad-consumer'
    New-Item -ItemType Directory -Path $badBin | Out-Null
    [IO.File]::WriteAllText((Join-Path $badBin 'VGMissionJournal.dll'), 'not-an-assembly')
    $badRoot = Join-Path $work 'bad-consumer-sandbox'
    $sandboxes += $badRoot
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot $badRoot -MissionJournalBin $badBin @options }
    catch { $rejected = $_.Exception.InnerException -is [BadImageFormatException] }
    Assert $rejected 'Non-assembly consumer did not fail metadata validation.'
    [IO.File]::WriteAllText((Join-Path $badBin 'VGStockpile.dll'), 'not-an-assembly')
    $badRoot = Join-Path $work 'bad-stockpile-sandbox'
    $sandboxes += $badRoot
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot $badRoot -StockpileBin $badBin @options }
    catch { $rejected = $_.Exception.InnerException -is [BadImageFormatException] }
    Assert $rejected 'Non-assembly Stockpile did not fail metadata validation.'

    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot $sandbox @options } catch { $rejected = $true }
    Assert $rejected 'Reused sandbox was accepted.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $original 'unsafe') @options } catch { $rejected = $true }
    Assert $rejected 'Sandbox inside protected data was accepted.'
    & $script -Action Cleanup -SandboxRoot $sandbox
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
        Assert (!(Test-Path (Join-Path $sandbox "game\$name"))) 'Junction survived Cleanup.'
        Assert ((Get-Content (Join-Path $fakeGame "$name\sentinel.txt")) -eq 'keep') 'Cleanup modified the junction target.'
    }
    Assert ((Get-Content (Join-Path $original 'real.save')) -eq 'original') 'Original fake save changed.'
    Write-Output 'PASS: manifest coverage, plugin allowlist, local preloader, provenance, reuse/path refusal, non-recursive cleanup.'
}
finally {
    # Even on assertion failure, unlink before deleting this wholly synthetic fixture tree.
    foreach ($root in $sandboxes) {
        foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
            $path = Join-Path $root "game\$name"
            if (Test-Path -LiteralPath $path) {
                if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($path, $false) }
                elseif ($name -eq 'VanguardGalaxy_Data') {
                    foreach ($child in Get-ChildItem -LiteralPath $path -Force -Directory) {
                        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($child.FullName, $false) }
                    }
                }
            }
        }
    }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
