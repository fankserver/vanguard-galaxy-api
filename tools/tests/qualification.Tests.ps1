# Windows-only, fake files only. Does not launch Unity or touch real game/profile data.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\qualification.ps1'
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')   # exit-outcome rule and process helpers
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
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-menu-full') -MenuInspection @options }
    catch { $rejected = $_.Exception.Message -like '*Menu inspection requires*' }
    Assert $rejected 'Menu inspection accepted a gameplay scenario.'
    $inspectionRoot = Join-Path $work 'menu-inspection'
    $sandboxes += $inspectionRoot
    & $script -Action Prepare -SandboxRoot $inspectionRoot -Scenario MissingApi -MenuInspection @options
    Assert-QualificationInputs $inspectionRoot
    $inspectionProvenance = Get-Content -LiteralPath (Join-Path $inspectionRoot 'build-provenance.json') -Raw | ConvertFrom-Json
    $rejected = $false
    try { Assert-MenuInspectionReceipt $inspectionRoot $inspectionProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing inspection receipt accepted.'
    $inspectionText = Join-Path $inspectionRoot 'menu-inspection.txt'
    [IO.File]::WriteAllText($inspectionText, "menu-inspection-v1`nsynthetic-only")
    $inspectionHash = (Get-FileHash -LiteralPath $inspectionText -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $inspectionRoot 'menu-inspection.receipt'), "PASS`nmenu-inspection-v1`nsha256=$inspectionHash`n")
    Assert-MenuInspectionReceipt $inspectionRoot $inspectionProvenance
    [IO.File]::AppendAllText($inspectionText, 'changed')
    $rejected = $false
    try { Assert-MenuInspectionReceipt $inspectionRoot $inspectionProvenance } catch { $rejected = $true }
    Assert $rejected 'Changed inspection snapshot accepted.'
    [IO.File]::WriteAllText((Join-Path $inspectionRoot 'menu-inspection.enabled'), 'wrong-marker')
    $rejected = $false
    try { Assert-QualificationInputs $inspectionRoot } catch { $rejected = $true }
    Assert $rejected 'Changed inspection marker accepted.'
    [IO.File]::WriteAllText((Join-Path $inspectionRoot 'menu-inspection.enabled'), 'menu-inspection-v1')
    Assert-QualificationInputs $inspectionRoot
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
    Assert-AnimaAssemblyMetadata (AnimaMetadata '0.4.0.0' '0.1.9')
    foreach ($metadata in @((AnimaMetadata '0.2.0.0' '0.1.8'), (AnimaMetadata '0.3.0.0' '0.1.1'),
        (AnimaMetadata '0.4.0.0' '0.1.8'), (AnimaMetadata '0.5.0.0' '0.1.9'))) {
        $rejected = $false
        try { Assert-AnimaAssemblyMetadata $metadata } catch { $rejected = $true }
        Assert $rejected 'Unsupported Anima version/dependency metadata accepted.'
    }
    # The consumer travel probe needs the first shape that observes visits through the public API.
    Assert-AnimaAssemblyMetadata (AnimaMetadata '0.4.0.0' '0.1.9') -TravelProbe
    $rejected = $false
    try { Assert-AnimaAssemblyMetadata (AnimaMetadata '0.3.0.0' '0.1.8') -TravelProbe } catch { $rejected = $true }
    Assert $rejected 'Anima consumer travel probe accepted the older mission-only consumer shape.'
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
    # evidence or the observed event trace do not actually support.
    $travelSession = [Guid]::NewGuid().ToString()
    $travelOperation = [Guid]::NewGuid().ToString()
    function TravelRow($case, $status, $session, $evidence) { return ($case + "`tdescription`t" + $status + "`tsystem:poi`t" + $session + "`t`t" + $evidence + "`tdetail") }
    # Sequence/surface/case label are independent: the label is only the observation context.
    function TravelEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t" + $travelOperation + "`tArrived`tInSystem`tsystem:a`tsystem:b`tsystem:b`t1.000`t") }
    function TravelSummary($rows, $first) {
        # The comma keeps each split row an array instead of unrolling its columns.
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelStationPhase", "budgetSeconds=$TravelStationBudgetSeconds",
            ("required=" + ($TravelStationRequiredCases -join ',')),
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
    # Realistic overlap: every case references its own events by surface/sequence, and the dock's
    # station events are observed while the chained route is still the active label.
    $validRows = @()
    $validEvents = @()
    $sequence = 0
    foreach ($case in $TravelStationRequiredCases) {
        $sequence++
        if ($case -eq 'station-dock') {
            $validRows += (TravelRow $case 'passed' $travelSession 'station:1')
            $validEvents += (TravelEvent 'station' 1 'chained-route' $travelSession)
        } else {
            $validRows += (TravelRow $case 'passed' $travelSession ("travel:" + $sequence))
            $validEvents += (TravelEvent 'travel' $sequence $case $travelSession)
        }
    }
    $validRows += (TravelRow 'cross-system-jumpgate' 'not-run' $travelSession '')
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing travel/station receipt files accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    Assert-PersistenceProbeReceipt $travelRoot $travelProvenance
    $failedRows = @($validRows[0..4]) + @(TravelRow 'station-dock' 'failed' $travelSession 'station:1')
    AssertTravelRejected $failedRows $validEvents (TravelSummary $failedRows 'PASS') 'Claimed PASS with a failed case row accepted.'
    Assert ((Test-Path -LiteralPath (Join-Path $travelRoot 'travel-station-receipt.tsv')) -and (Test-Path -LiteralPath (Join-Path $travelRoot 'travel-station-events.tsv'))) 'A failed attempt must keep its receipt and event diagnostics.'
    $skippedRows = @($TravelStationRequiredCases | ForEach-Object { TravelRow $_ 'not-run' $travelSession '' })
    AssertTravelRejected $skippedRows $validEvents (TravelSummary $skippedRows 'PASS') 'All-skipped coverage accepted as PASS.'
    $missingRows = @($validRows | Where-Object { $_ -notlike 'chained-route*' })
    AssertTravelRejected $missingRows $validEvents (TravelSummary $missingRows 'PASS') 'Missing mandatory case accepted.'
    # The dock case claims an event nobody observed.
    AssertTravelRejected $validRows @($validEvents | Where-Object { $_ -notlike "1`tstation*" }) (TravelSummary $validRows 'PASS') 'Required case referencing an unobserved event accepted.'
    # Evidence exists, but only for another session.
    $foreignSessionEvents = @($validEvents | ForEach-Object { $_ -replace [regex]::Escape($travelSession), ([Guid]::NewGuid().ToString()) })
    AssertTravelRejected $validRows $foreignSessionEvents (TravelSummary $validRows 'PASS') 'Receipt identities absent from the event trace accepted.'
    # A required case with no evidence reference at all cannot pass.
    $noEvidenceRows = @($validRows | Where-Object { $_ -notlike 'station-dock*' }) + @(TravelRow 'station-dock' 'passed' $travelSession '')
    AssertTravelRejected $noEvidenceRows $validEvents (TravelSummary $noEvidenceRows 'PASS') 'Required case without evidence accepted.'
    AssertTravelRejected $validRows $validEvents (TravelSummary $validRows 'FAIL') 'Failed attempt summary accepted.'
    # An externally terminated pilot leaves INCOMPLETE evidence; that is never a pass.
    AssertTravelRejected $validRows $validEvents @('INCOMPLETE', "phase=$TravelStationPhase", "budgetSeconds=$TravelStationBudgetSeconds", 'activeCase=station-dock', 'rows=5 passed=5 failed=0 notRun=0', 'result=pilot still running or externally terminated; this is not a pass.') 'Incomplete checkpoint accepted as a pass.'
    $foreignPhase = @((TravelSummary $validRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { 'phase=travel-other-phase' } else { $_ } })
    AssertTravelRejected $validRows $validEvents $foreignPhase 'Foreign phase claim accepted.'
    $overBudget = @((TravelSummary $validRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($TravelStationBudgetSeconds + 1)" } else { $_ } })
    AssertTravelRejected $validRows $validEvents $overBudget 'Phase budget above the launcher reservation accepted.'
    $wrongCounts = (TravelSummary $validRows 'PASS')
    $wrongCounts[4] = 'rows=99 passed=99 failed=0 notRun=0'
    AssertTravelRejected $validRows $validEvents $wrongCounts 'Summary counts disagreeing with the receipt accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    [IO.File]::WriteAllLines((Join-Path $travelRoot 'travel-station-events.tsv'), [string[]]@("sequence`tkind") + [string[]]$validEvents)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
    Assert $rejected 'Changed event trace header accepted.'
    WriteTravelOutputs $validRows $validEvents (TravelSummary $validRows 'PASS')
    Assert-PersistenceProbeReceipt $travelRoot $travelProvenance
    # A launcher-terminated, unknown-exit or unexpected-exit run is refused even with a PASS receipt
    # on disk. Only a clean 0 and the game's source-proven self-termination code are accepted.
    $outcomePath = Join-Path $travelRoot 'run-outcome.json'
    foreach ($outcome in @(@{timedOut=$true;killed=$true;exitCode=$null}, @{timedOut=$false;killed=$true;exitCode=$null},
        @{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1},
        @{timedOut=$false;killed=$false;exitCode=3}, @{timedOut=$false;killed=$false;exitCode=-1073741819},
        @{timedOut=$true;killed=$false;exitCode=$GameSelfTerminationExitCode})) {
        $outcome | ConvertTo-Json | Set-Content -LiteralPath $outcomePath
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $travelRoot $travelProvenance } catch { $rejected = $true }
        Assert $rejected ('Terminated, unknown or unexpected launcher outcome accepted: exit ' + $outcome.exitCode)
    }
    foreach ($accepted in @(0, $GameSelfTerminationExitCode)) {
        @{timedOut=$false;killed=$false;exitCode=$accepted} | ConvertTo-Json | Set-Content -LiteralPath $outcomePath
        Assert-PersistenceProbeReceipt $travelRoot $travelProvenance
    }
    Remove-Item -LiteralPath $outcomePath
    # The prepared budget reservation is pinned in provenance and cannot be edited afterwards.
    Assert ($travelProvenance.travelStationBudgetSeconds -eq $TravelStationBudgetSeconds) 'Prepared travel/station budget reservation missing.'
    $provenancePath = Join-Path $travelRoot 'build-provenance.json'
    $provenanceText = [IO.File]::ReadAllText($provenancePath)
    [IO.File]::WriteAllText($provenancePath, ($provenanceText -replace '"travelStationBudgetSeconds": *\d+', '"travelStationBudgetSeconds": 60'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $travelRoot } catch { $rejected = $true }
    Assert $rejected 'Edited travel/station budget reservation accepted.'
    [IO.File]::WriteAllText($provenancePath, $provenanceText)
    $null = Assert-QualificationInputs $travelRoot
    # The launcher must refuse a too-short process lifetime BEFORE starting the game, and a sandbox
    # that already holds travel/station evidence must never be reused.
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $travelRoot -TimeoutSeconds ($QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds) @options }
    catch { $rejected = $_.Exception.Message -like '*already ran*' }
    Assert $rejected 'Reused travel/station sandbox accepted.'
    Assert (!(Test-Path -LiteralPath (Join-Path $travelRoot 'run-started.txt'))) 'The launcher started the game on a reused sandbox.'
    & $script -Action Cleanup -SandboxRoot $travelRoot
    $travelTimeoutRoot = Join-Path $work 'travel-timeout-sandbox'
    $sandboxes += $travelTimeoutRoot
    & $script -Action Prepare -SandboxRoot $travelTimeoutRoot -TravelStation @options
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $travelTimeoutRoot -TimeoutSeconds $QualificationBaseTimeoutSeconds @options }
    catch { $rejected = $_.Exception.Message -like '*-TimeoutSeconds at least*' }
    Assert $rejected 'Travel/station run accepted a process lifetime shorter than the phase budget.'
    Assert (!(Test-Path -LiteralPath (Join-Path $travelTimeoutRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient lifetime.'
    & $script -Action Cleanup -SandboxRoot $travelTimeoutRoot
    # --- separate optional cross-system travel phase --------------------------------------------
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-cross-system') -TravelCrossSystem @options }
    catch { $rejected = $_.Exception.Message -like '*requires the travel/station selection*' }
    Assert $rejected 'Cross-system phase accepted without the travel/station selection.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'invalid-cross-system'))) 'Rejected cross-system selection left a prepared sandbox.'
    $crossRoot = Join-Path $work 'travel-cross-system-sandbox'
    $sandboxes += $crossRoot
    & $script -Action Prepare -SandboxRoot $crossRoot -TravelStation -TravelCrossSystem @options
    $crossProvenance = Assert-QualificationInputs $crossRoot
    Assert ($crossProvenance.travelCrossSystem -and $crossProvenance.travelCrossSystemBudgetSeconds -eq $TravelCrossSystemBudgetSeconds) 'Prepared cross-system selection/budget missing.'
    $crossMarker = Join-Path $crossRoot 'travel-cross-system.enabled'
    [IO.File]::WriteAllText($crossMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $crossRoot } catch { $rejected = $true }
    Assert $rejected 'Changed cross-system marker accepted.'
    [IO.File]::WriteAllText($crossMarker, 'cross-system-v1')
    $crossProvenancePath = Join-Path $crossRoot 'build-provenance.json'
    $crossProvenanceText = [IO.File]::ReadAllText($crossProvenancePath)
    [IO.File]::WriteAllText($crossProvenancePath, ($crossProvenanceText -replace '"travelCrossSystemBudgetSeconds": *\d+', '"travelCrossSystemBudgetSeconds": 60'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $crossRoot } catch { $rejected = $true }
    Assert $rejected 'Edited cross-system budget reservation accepted.'
    [IO.File]::WriteAllText($crossProvenancePath, $crossProvenanceText)
    $null = Assert-QualificationInputs $crossRoot
    # Synthetic receipts only. Both phases are validated independently in the same sandbox, so a
    # passing in-system receipt can never stand in for the mandatory cross-system cases.
    $crossSession = [Guid]::NewGuid().ToString()
    $crossOperation = [Guid]::NewGuid().ToString()
    function CrossEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t" + $crossOperation + "`tArrived`tJumpGate`tsystem-1:gate-1`tsystem-2:gate-2`tsystem-2:gate-2`t1.000`t") }
    function CrossSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelCrossSystemPhase", "budgetSeconds=$TravelCrossSystemBudgetSeconds",
            ("required=" + ($TravelCrossSystemRequiredCases -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelCrossSystemRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    function WriteCrossOutputsTo($root, $rows, $events, $summary) {
        [IO.File]::WriteAllLines((Join-Path $root 'travel-cross-system-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $root 'travel-cross-system-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $root 'travel-cross-system.txt'), [string[]]$summary)
    }
    function WriteCrossOutputs($rows, $events, $summary) { WriteCrossOutputsTo $crossRoot $rows $events $summary }
    # A fixture-preparation row: an optional passed row with no evidence whose detail records the
    # native factory it used and that it observed no travel facts.
    function FixtureRow($session, $detail) { return ($TravelWormholeFixtureCase + "`tfixture preparation`tpassed`tsystem-1:wh-1`t" + $session + "`t`t`t" + $detail) }
    $validFixtureDetail = 'selection=travel-wormhole-fixture; ' + $TravelWormholeFactorySignature + 'Source.Galaxy.SystemMapData, System.Boolean, System.Collections.Generic.List`1<Source.Galaxy.POI.Wormhole>) : Source.Galaxy.POI.Wormhole; wormholesBefore=0; wormholesAfter=2; source=system-1:wh-1; destination=system-2:wh-2; travelFactsDuringCreation=0; fixture preparation only, not travel evidence.'
    function AssertCrossRejected($rows, $events, $summary, $message) {
        WriteCrossOutputs $rows $events $summary
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $crossRoot $crossProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    # The same sandbox also holds the in-system phase, which must keep its own required cases.
    $stationRows = @()
    $stationEvents = @()
    $sequence = 0
    foreach ($case in $TravelStationRequiredCases) {
        $sequence++
        $stationRows += (TravelRow $case 'passed' $crossSession ("travel:" + $sequence))
        $stationEvents += (TravelEvent 'travel' $sequence $case $crossSession)
    }
    $stationRows += (TravelRow 'cross-system-jumpgate' 'not-run' $crossSession '')
    $stationRows += (TravelRow 'cross-system-wormhole' 'not-run' $crossSession '')
    [IO.File]::WriteAllLines((Join-Path $crossRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$stationRows)
    [IO.File]::WriteAllLines((Join-Path $crossRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$stationEvents)
    [IO.File]::WriteAllLines((Join-Path $crossRoot 'travel-station.txt'), [string[]](TravelSummary $stationRows 'PASS'))
    # A complete in-system phase alone is NOT the cross-system phase.
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $crossRoot $crossProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing cross-system receipt accepted because the in-system phase passed.'
    $crossRows = @()
    $crossEvents = @()
    $sequence = 0
    foreach ($case in $TravelCrossSystemRequiredCases) {
        $sequence++
        $crossRows += (TravelRow $case 'passed' $crossSession ("travel:" + $sequence))
        $crossEvents += (CrossEvent 'travel' $sequence $case $crossSession)
    }
    WriteCrossOutputs $crossRows $crossEvents (CrossSummary $crossRows 'PASS')
    Assert-PersistenceProbeReceipt $crossRoot $crossProvenance
    # A fixture that cannot exercise a mandatory native routine is a FAIL, never an empty PASS.
    $crossSkipped = @($TravelCrossSystemRequiredCases | ForEach-Object { TravelRow $_ 'not-run' $crossSession '' })
    AssertCrossRejected $crossSkipped $crossEvents (CrossSummary $crossSkipped 'PASS') 'All-skipped cross-system coverage accepted as PASS.'
    $crossWormholeSkipped = @($crossRows[0]) + @(TravelRow 'cross-system-wormhole' 'not-run' $crossSession '')
    AssertCrossRejected $crossWormholeSkipped $crossEvents (CrossSummary $crossWormholeSkipped 'PASS') 'A not-run mandatory wormhole case accepted as PASS.'
    $crossFailed = @($crossRows[0]) + @(TravelRow 'cross-system-wormhole' 'failed' $crossSession 'travel:2')
    AssertCrossRejected $crossFailed $crossEvents (CrossSummary $crossFailed 'PASS') 'Claimed cross-system PASS with a failed case accepted.'
    Assert ((Test-Path -LiteralPath (Join-Path $crossRoot 'travel-cross-system-receipt.tsv')) -and (Test-Path -LiteralPath (Join-Path $crossRoot 'travel-cross-system-events.tsv'))) 'A failed cross-system attempt must keep its receipt and event diagnostics.'
    AssertCrossRejected @($crossRows[0]) $crossEvents (CrossSummary @($crossRows[0]) 'PASS') 'Missing mandatory cross-system case accepted.'
    AssertCrossRejected $crossRows @($crossEvents[0]) (CrossSummary $crossRows 'PASS') 'Cross-system case referencing an unobserved event accepted.'
    $crossForeign = @($crossEvents | ForEach-Object { $_ -replace [regex]::Escape($crossSession), ([Guid]::NewGuid().ToString()) })
    AssertCrossRejected $crossRows $crossForeign (CrossSummary $crossRows 'PASS') 'Cross-system identities absent from the event trace accepted.'
    AssertCrossRejected $crossRows $crossEvents (CrossSummary $crossRows 'FAIL') 'Failed cross-system attempt summary accepted.'
    AssertCrossRejected $crossRows $crossEvents @('INCOMPLETE', "phase=$TravelCrossSystemPhase", "budgetSeconds=$TravelCrossSystemBudgetSeconds", 'activeCase=cross-system-wormhole', 'rows=2 passed=2 failed=0 notRun=0', 'result=pilot still running or externally terminated; this is not a pass.') 'Incomplete cross-system checkpoint accepted as a pass.'
    $crossForeignPhase = @((CrossSummary $crossRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelStationPhase" } else { $_ } })
    AssertCrossRejected $crossRows $crossEvents $crossForeignPhase 'Cross-system receipt declaring the in-system phase accepted.'
    $crossOverBudget = @((CrossSummary $crossRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($TravelCrossSystemBudgetSeconds + 1)" } else { $_ } })
    AssertCrossRejected $crossRows $crossEvents $crossOverBudget 'Cross-system budget above the launcher reservation accepted.'
    $crossWrongCounts = (CrossSummary $crossRows 'PASS')
    $crossWrongCounts[4] = 'rows=99 passed=99 failed=0 notRun=0'
    AssertCrossRejected $crossRows $crossEvents $crossWrongCounts 'Cross-system summary counts disagreeing with the receipt accepted.'
    WriteCrossOutputs $crossRows $crossEvents (CrossSummary $crossRows 'PASS')
    [IO.File]::WriteAllLines((Join-Path $crossRoot 'travel-cross-system-events.tsv'), [string[]]@("sequence`tkind") + [string[]]$crossEvents)
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $crossRoot $crossProvenance } catch { $rejected = $true }
    Assert $rejected 'Changed cross-system event trace header accepted.'
    WriteCrossOutputs $crossRows $crossEvents (CrossSummary $crossRows 'PASS')
    Assert-PersistenceProbeReceipt $crossRoot $crossProvenance
    $crossOutcomePath = Join-Path $crossRoot 'run-outcome.json'
    foreach ($outcome in @(@{timedOut=$true;killed=$true;exitCode=$null}, @{timedOut=$false;killed=$true;exitCode=$null},
        @{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1})) {
        $outcome | ConvertTo-Json | Set-Content -LiteralPath $crossOutcomePath
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $crossRoot $crossProvenance } catch { $rejected = $true }
        Assert $rejected ('Terminated or unexpected launcher outcome accepted for the cross-system phase: exit ' + $outcome.exitCode)
    }
    foreach ($accepted in @(0, $GameSelfTerminationExitCode)) {
        @{timedOut=$false;killed=$false;exitCode=$accepted} | ConvertTo-Json | Set-Content -LiteralPath $crossOutcomePath
        Assert-PersistenceProbeReceipt $crossRoot $crossProvenance
    }
    Remove-Item -LiteralPath $crossOutcomePath
    # Without the explicit opt-in selection the pilot must never create native content, so a
    # preparation row in this sandbox is a tamper/misbehaviour signal.
    AssertCrossRejected ($crossRows + @(FixtureRow $crossSession $validFixtureDetail)) $crossEvents (CrossSummary ($crossRows + @(FixtureRow $crossSession $validFixtureDetail)) 'PASS') 'Fixture preparation recorded without the fixture selection accepted.'
    WriteCrossOutputs $crossRows $crossEvents (CrossSummary $crossRows 'PASS')
    Assert-PersistenceProbeReceipt $crossRoot $crossProvenance
    & $script -Action Cleanup -SandboxRoot $crossRoot
    # --- opt-in disposable native wormhole fixture ----------------------------------------------
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-wormhole-fixture') -TravelStation -TravelWormholeFixture @options }
    catch { $rejected = $_.Exception.Message -like '*requires the cross-system travel phase*' }
    Assert $rejected 'Wormhole fixture accepted without the cross-system phase.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'invalid-wormhole-fixture'))) 'Rejected wormhole fixture selection left a prepared sandbox.'
    $fixtureRoot = Join-Path $work 'travel-wormhole-fixture-sandbox'
    $sandboxes += $fixtureRoot
    & $script -Action Prepare -SandboxRoot $fixtureRoot -TravelStation -TravelCrossSystem -TravelWormholeFixture @options
    $fixtureProvenance = Assert-QualificationInputs $fixtureRoot
    Assert ($fixtureProvenance.travelWormholeFixture) 'Prepared wormhole fixture selection missing from provenance.'
    $fixtureMarker = Join-Path $fixtureRoot 'travel-wormhole-fixture.enabled'
    Assert ((Get-Content -LiteralPath $fixtureMarker -Raw).Trim() -eq 'wormhole-fixture-v1') 'Unexpected wormhole fixture marker.'
    [IO.File]::WriteAllText($fixtureMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $fixtureRoot } catch { $rejected = $true }
    Assert $rejected 'Changed wormhole fixture marker accepted.'
    [IO.File]::WriteAllText($fixtureMarker, 'wormhole-fixture-v1')
    Remove-Item -LiteralPath $fixtureMarker
    $rejected = $false
    try { $null = Assert-QualificationInputs $fixtureRoot } catch { $rejected = $true }
    Assert $rejected 'Removed wormhole fixture marker accepted while provenance still selects it.'
    [IO.File]::WriteAllText($fixtureMarker, 'wormhole-fixture-v1')
    $null = Assert-QualificationInputs $fixtureRoot
    # The in-system phase is selected here too and keeps its own unchanged required cases.
    [IO.File]::WriteAllLines((Join-Path $fixtureRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$stationRows)
    [IO.File]::WriteAllLines((Join-Path $fixtureRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$stationEvents)
    [IO.File]::WriteAllLines((Join-Path $fixtureRoot 'travel-station.txt'), [string[]](TravelSummary $stationRows 'PASS'))
    function AssertFixtureRejected($rows, $message) {
        WriteCrossOutputsTo $fixtureRoot $rows $crossEvents (CrossSummary $rows 'PASS')
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $fixtureRoot $fixtureProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    $fixtureRows = $crossRows + @(FixtureRow $crossSession $validFixtureDetail)
    WriteCrossOutputsTo $fixtureRoot $fixtureRows $crossEvents (CrossSummary $fixtureRows 'PASS')
    Assert-PersistenceProbeReceipt $fixtureRoot $fixtureProvenance
    # Preparation is never coverage: the mandatory cases must still be there and still pass.
    $fixtureOnly = @($crossRows[0]) + @(FixtureRow $crossSession $validFixtureDetail)
    AssertFixtureRejected $fixtureOnly 'Fixture preparation accepted in place of the mandatory wormhole case.'
    $fixtureSkipped = @($crossRows[0], (TravelRow 'cross-system-wormhole' 'not-run' $crossSession ''), (FixtureRow $crossSession $validFixtureDetail))
    AssertFixtureRejected $fixtureSkipped 'Prepared fixture with a not-run wormhole case accepted as PASS.'
    AssertFixtureRejected ($crossRows + @(FixtureRow $crossSession $validFixtureDetail) + @(FixtureRow $crossSession $validFixtureDetail)) 'Duplicated fixture preparation accepted.'
    AssertFixtureRejected ($crossRows + @(FixtureRow $crossSession ($validFixtureDetail -replace 'travelFactsDuringCreation=0','travelFactsDuringCreation=3'))) 'Fixture preparation that observed travel facts accepted.'
    AssertFixtureRejected ($crossRows + @(FixtureRow $crossSession 'selection=travel-wormhole-fixture; wormholesBefore=0; wormholesAfter=2; travelFactsDuringCreation=0')) 'Fixture preparation without the recorded native factory accepted.'
    AssertFixtureRejected ($crossRows + @(($TravelWormholeFixtureCase + "`tfixture preparation`tpassed`tsystem-1:wh-1`t" + $crossSession + "`t`ttravel:1`t" + $validFixtureDetail))) 'Fixture preparation claiming observed travel events accepted.'
    AssertFixtureRejected ($crossRows + @(($TravelWormholeFixtureCase + "`tfixture preparation`tnot-run`t`t" + $crossSession + "`t`t`tno destination system"))) 'Incomplete fixture preparation accepted.'
    WriteCrossOutputsTo $fixtureRoot $fixtureRows $crossEvents (CrossSummary $fixtureRows 'PASS')
    Assert-PersistenceProbeReceipt $fixtureRoot $fixtureProvenance
    & $script -Action Cleanup -SandboxRoot $fixtureRoot
    # The launcher must reserve base + BOTH phase budgets before starting the game.
    $crossTimeoutRoot = Join-Path $work 'travel-cross-timeout-sandbox'
    $sandboxes += $crossTimeoutRoot
    & $script -Action Prepare -SandboxRoot $crossTimeoutRoot -TravelStation -TravelCrossSystem @options
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $crossTimeoutRoot -TimeoutSeconds ($QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds) @options }
    catch { $rejected = $_.Exception.Message -like '*-TimeoutSeconds at least*' }
    Assert $rejected 'Cross-system run accepted a lifetime that only covers the in-system phase.'
    # Exactly one second under the derived minimum (base + both reservations) must still be refused.
    $crossMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelCrossSystemBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $crossTimeoutRoot -TimeoutSeconds ($crossMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $crossMinimum*" }
    Assert $rejected 'Cross-system run accepted a lifetime one second below the derived minimum.'
    Assert (!(Test-Path -LiteralPath (Join-Path $crossTimeoutRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient cross-system lifetime.'
    & $script -Action Cleanup -SandboxRoot $crossTimeoutRoot
    # --- separate optional travel resilience phase ----------------------------------------------
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-resilience') -TravelResilience @options }
    catch { $rejected = $_.Exception.Message -like '*requires the travel/station selection*' }
    Assert $rejected 'Resilience phase accepted without the travel/station selection.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'invalid-resilience'))) 'Rejected resilience selection left a prepared sandbox.'
    $resilienceRoot = Join-Path $work 'travel-resilience-sandbox'
    $sandboxes += $resilienceRoot
    & $script -Action Prepare -SandboxRoot $resilienceRoot -TravelStation -TravelResilience @options
    $resilienceProvenance = Assert-QualificationInputs $resilienceRoot
    Assert ($resilienceProvenance.travelResilience -and $resilienceProvenance.travelResilienceBudgetSeconds -eq $TravelResilienceBudgetSeconds) 'Prepared resilience selection/budget missing.'
    $resilienceMarker = Join-Path $resilienceRoot 'travel-resilience.enabled'
    [IO.File]::WriteAllText($resilienceMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $resilienceRoot } catch { $rejected = $true }
    Assert $rejected 'Changed resilience marker accepted.'
    [IO.File]::WriteAllText($resilienceMarker, 'resilience-v1')
    Remove-Item -LiteralPath $resilienceMarker
    $rejected = $false
    try { $null = Assert-QualificationInputs $resilienceRoot } catch { $rejected = $true }
    Assert $rejected 'Removed resilience marker accepted while provenance still selects it.'
    [IO.File]::WriteAllText($resilienceMarker, 'resilience-v1')
    $resilienceProvenancePath = Join-Path $resilienceRoot 'build-provenance.json'
    $resilienceProvenanceText = [IO.File]::ReadAllText($resilienceProvenancePath)
    [IO.File]::WriteAllText($resilienceProvenancePath, ($resilienceProvenanceText -replace '"travelResilienceBudgetSeconds": *\d+', '"travelResilienceBudgetSeconds": 60'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $resilienceRoot } catch { $rejected = $true }
    Assert $rejected 'Edited resilience budget reservation accepted.'
    [IO.File]::WriteAllText($resilienceProvenancePath, $resilienceProvenanceText)
    $null = Assert-QualificationInputs $resilienceRoot
    # Synthetic receipts only. The in-system phase is selected in this sandbox too and keeps its own
    # required cases; a passing in-system receipt can never stand in for these three cases.
    $resilienceSession = [Guid]::NewGuid().ToString()
    function ResilienceEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t`tDeparted`tInSystem`tsystem-1:station-1`tsystem-1:poi-b`tsystem-1:poi-b`t1.000`t") }
    function ResilienceSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelResiliencePhase", "budgetSeconds=$TravelResilienceBudgetSeconds",
            ("required=" + ($TravelResilienceRequiredCases -join ',')),
            ("required-subcases=" + ($TravelResilienceRequiredSubcaseRows -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelResilienceRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        foreach ($subcase in $TravelResilienceRequiredSubcaseRows) {
            $matched = @($records | Where-Object { $_[0] -eq $subcase })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-subcase $subcase=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    function WriteResilienceOutputs($rows, $events, $summary) {
        [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-resilience-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-resilience-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-resilience.txt'), [string[]]$summary)
    }
    function AssertResilienceRejected($rows, $events, $summary, $message) {
        WriteResilienceOutputs $rows $events $summary
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    $resilienceStationRows = @()
    $resilienceStationEvents = @()
    $sequence = 0
    foreach ($case in $TravelStationRequiredCases) {
        $sequence++
        $resilienceStationRows += (TravelRow $case 'passed' $resilienceSession ("travel:" + $sequence))
        $resilienceStationEvents += (TravelEvent 'travel' $sequence $case $resilienceSession)
    }
    foreach ($case in @('cross-system-jumpgate','cross-system-wormhole','empty-origin-reroute','restore-relink-dock','stale-session-replay')) {
        $resilienceStationRows += (TravelRow $case 'not-run' $resilienceSession '')
    }
    [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$resilienceStationRows)
    [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$resilienceStationEvents)
    [IO.File]::WriteAllLines((Join-Path $resilienceRoot 'travel-station.txt'), [string[]](TravelSummary $resilienceStationRows 'PASS'))
    # A complete in-system phase alone, including its optional NOT-RUN rows for exactly these three
    # cells, is NOT the resilience phase.
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing resilience receipt accepted because the in-system phase passed.'
    $resilienceCaseRows = @()
    $resilienceEvents = @()
    $sequence = 0
    foreach ($case in $TravelResilienceRequiredCases) {
        $sequence++
        $resilienceCaseRows += (TravelRow $case 'passed' $resilienceSession ("travel:" + $sequence))
        $resilienceEvents += (ResilienceEvent 'travel' $sequence $case $resilienceSession)
    }
    # The mandatory same-size subcase row belongs to every complete receipt: it is not a case
    # identity, but a missing/not-run/duplicated row is refused.
    Assert (@($TravelResilienceRequiredSubcaseRows | Where-Object { $_ -in $TravelResilienceRequiredCases }).Count -eq 0) 'A mandatory subcase row must not also be a case identity.'
    $sameSizeCase = $TravelResilienceRequiredSubcaseRows[0]
    $sameSizeRow = (TravelRow $sameSizeCase 'passed' $resilienceSession 'station:9')
    $resilienceEvents += (ResilienceEvent 'station' 9 $sameSizeCase $resilienceSession)
    $resilienceRows = $resilienceCaseRows + @($sameSizeRow)
    WriteResilienceOutputs $resilienceRows $resilienceEvents (ResilienceSummary $resilienceRows 'PASS')
    Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance
    AssertResilienceRejected $resilienceCaseRows $resilienceEvents (ResilienceSummary $resilienceCaseRows 'PASS') 'Receipt without the mandatory same-size subcase row accepted.'
    $sameSizeNotRun = $resilienceCaseRows + @((TravelRow $sameSizeCase 'not-run' $resilienceSession ''))
    AssertResilienceRejected $sameSizeNotRun $resilienceEvents (ResilienceSummary $sameSizeNotRun 'PASS') 'Not-run mandatory same-size subcase accepted.'
    $sameSizeDuplicated = $resilienceRows + @($sameSizeRow)
    AssertResilienceRejected $sameSizeDuplicated $resilienceEvents (ResilienceSummary $sameSizeDuplicated 'PASS') 'Duplicated mandatory same-size subcase accepted.'
    $subcaseOnly = @($resilienceCaseRows[0], $resilienceCaseRows[2], $sameSizeRow)
    AssertResilienceRejected $subcaseOnly $resilienceEvents (ResilienceSummary $subcaseOnly 'PASS') 'Mandatory subcase row accepted in place of the required restore/relink case.'
    $resilienceSkipped = @($TravelResilienceRequiredCases | ForEach-Object { TravelRow $_ 'not-run' $resilienceSession '' }) + @($sameSizeRow)
    AssertResilienceRejected $resilienceSkipped $resilienceEvents (ResilienceSummary $resilienceSkipped 'PASS') 'All-skipped resilience coverage accepted as PASS.'
    $resilienceFailed = @($resilienceCaseRows[0], $resilienceCaseRows[1], $sameSizeRow) + @(TravelRow 'stale-session-replay' 'failed' $resilienceSession 'travel:3')
    AssertResilienceRejected $resilienceFailed $resilienceEvents (ResilienceSummary $resilienceFailed 'PASS') 'Claimed resilience PASS with a failed case accepted.'
    Assert ((Test-Path -LiteralPath (Join-Path $resilienceRoot 'travel-resilience-receipt.tsv')) -and (Test-Path -LiteralPath (Join-Path $resilienceRoot 'travel-resilience-events.tsv'))) 'A failed resilience attempt must keep its receipt and event diagnostics.'
    AssertResilienceRejected @($resilienceCaseRows[0], $sameSizeRow) $resilienceEvents (ResilienceSummary @($resilienceCaseRows[0], $sameSizeRow) 'PASS') 'Missing mandatory resilience case accepted.'
    AssertResilienceRejected $resilienceRows @($resilienceEvents[0]) (ResilienceSummary $resilienceRows 'PASS') 'Resilience case referencing an unobserved event accepted.'
    $resilienceForeign = @($resilienceEvents | ForEach-Object { $_ -replace [regex]::Escape($resilienceSession), ([Guid]::NewGuid().ToString()) })
    AssertResilienceRejected $resilienceRows $resilienceForeign (ResilienceSummary $resilienceRows 'PASS') 'Resilience identities absent from the event trace accepted.'
    $noEvidence = @($resilienceCaseRows[0], $resilienceCaseRows[1], $sameSizeRow) + @(TravelRow 'stale-session-replay' 'passed' $resilienceSession '')
    AssertResilienceRejected $noEvidence $resilienceEvents (ResilienceSummary $noEvidence 'PASS') 'Required resilience case without evidence accepted.'
    AssertResilienceRejected $resilienceRows $resilienceEvents (ResilienceSummary $resilienceRows 'FAIL') 'Failed resilience attempt summary accepted.'
    AssertResilienceRejected $resilienceRows $resilienceEvents @('INCOMPLETE', "phase=$TravelResiliencePhase", "budgetSeconds=$TravelResilienceBudgetSeconds", 'activeCase=stale-session-replay', 'rows=4 passed=4 failed=0 notRun=0', 'result=pilot still running or externally terminated; this is not a pass.') 'Incomplete resilience checkpoint accepted as a pass.'
    $resilienceForeignPhase = @((ResilienceSummary $resilienceRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelStationPhase" } else { $_ } })
    AssertResilienceRejected $resilienceRows $resilienceEvents $resilienceForeignPhase 'Resilience receipt declaring the in-system phase accepted.'
    $resilienceOverBudget = @((ResilienceSummary $resilienceRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($TravelResilienceBudgetSeconds + 1)" } else { $_ } })
    AssertResilienceRejected $resilienceRows $resilienceEvents $resilienceOverBudget 'Resilience budget above the launcher reservation accepted.'
    $resilienceWrongCounts = (ResilienceSummary $resilienceRows 'PASS')
    $resilienceWrongCounts[5] = 'rows=99 passed=99 failed=0 notRun=0'
    AssertResilienceRejected $resilienceRows $resilienceEvents $resilienceWrongCounts 'Resilience summary counts disagreeing with the receipt accepted.'
    WriteResilienceOutputs $resilienceRows $resilienceEvents (ResilienceSummary $resilienceRows 'PASS')
    Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance
    $resilienceOutcomePath = Join-Path $resilienceRoot 'run-outcome.json'
    foreach ($outcome in @(@{timedOut=$true;killed=$true;exitCode=$null}, @{timedOut=$false;killed=$true;exitCode=$null},
        @{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1})) {
        $outcome | ConvertTo-Json | Set-Content -LiteralPath $resilienceOutcomePath
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance } catch { $rejected = $true }
        Assert $rejected ('Terminated or unexpected launcher outcome accepted for the resilience phase: exit ' + $outcome.exitCode)
    }
    foreach ($accepted in @(0, $GameSelfTerminationExitCode)) {
        @{timedOut=$false;killed=$false;exitCode=$accepted} | ConvertTo-Json | Set-Content -LiteralPath $resilienceOutcomePath
        Assert-PersistenceProbeReceipt $resilienceRoot $resilienceProvenance
    }
    Remove-Item -LiteralPath $resilienceOutcomePath
    & $script -Action Cleanup -SandboxRoot $resilienceRoot
    # The launcher must reserve base + EVERY selected phase budget before starting the game.
    $resilienceTimeoutRoot = Join-Path $work 'travel-resilience-timeout-sandbox'
    $sandboxes += $resilienceTimeoutRoot
    & $script -Action Prepare -SandboxRoot $resilienceTimeoutRoot -TravelStation -TravelCrossSystem -TravelResilience @options
    $allMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelCrossSystemBudgetSeconds + $TravelResilienceBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $resilienceTimeoutRoot -TimeoutSeconds ($allMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $allMinimum*" }
    Assert $rejected 'Resilience run accepted a lifetime one second below the derived minimum of all selected phases.'
    Assert (!(Test-Path -LiteralPath (Join-Path $resilienceTimeoutRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient resilience lifetime.'
    & $script -Action Cleanup -SandboxRoot $resilienceTimeoutRoot

    # --- separate optional travel recovery/continuation phase ------------------------------------
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-recovery') -TravelRecoveryContinuation @options }
    catch { $rejected = $_.Exception.Message -like '*requires the travel/station selection*' }
    Assert $rejected 'Recovery/continuation phase accepted without the travel/station selection.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'invalid-recovery'))) 'Rejected recovery selection left a prepared sandbox.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-recovery-scenario') -Scenario MissingApi -TravelRecoveryContinuation @options }
    catch { $rejected = $true }
    Assert $rejected 'Recovery/continuation phase accepted outside Full.'
    $recoveryRoot = Join-Path $work 'travel-recovery-sandbox'
    $sandboxes += $recoveryRoot
    & $script -Action Prepare -SandboxRoot $recoveryRoot -TravelStation -TravelRecoveryContinuation @options
    $recoveryProvenance = Assert-QualificationInputs $recoveryRoot
    Assert ($recoveryProvenance.travelRecovery -and $recoveryProvenance.travelRecoveryBudgetSeconds -eq $TravelRecoveryBudgetSeconds) 'Prepared recovery selection/budget missing.'
    $recoveryMarker = Join-Path $recoveryRoot 'travel-recovery.enabled'
    [IO.File]::WriteAllText($recoveryMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $recoveryRoot } catch { $rejected = $true }
    Assert $rejected 'Changed recovery marker accepted.'
    [IO.File]::WriteAllText($recoveryMarker, 'recovery-continuation-v1')
    Remove-Item -LiteralPath $recoveryMarker
    $rejected = $false
    try { $null = Assert-QualificationInputs $recoveryRoot } catch { $rejected = $true }
    Assert $rejected 'Removed recovery marker accepted while provenance still selects it.'
    [IO.File]::WriteAllText($recoveryMarker, 'recovery-continuation-v1')
    $recoveryProvenancePath = Join-Path $recoveryRoot 'build-provenance.json'
    $recoveryProvenanceText = [IO.File]::ReadAllText($recoveryProvenancePath)
    [IO.File]::WriteAllText($recoveryProvenancePath, ($recoveryProvenanceText -replace '"travelRecoveryBudgetSeconds": *\d+', '"travelRecoveryBudgetSeconds": 60'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $recoveryRoot } catch { $rejected = $true }
    Assert $rejected 'Edited recovery budget reservation accepted.'
    [IO.File]::WriteAllText($recoveryProvenancePath, $recoveryProvenanceText)
    $recoveryProvenance = Assert-QualificationInputs $recoveryRoot
    # Synthetic receipts only. The in-system phase is selected here too and keeps its own required
    # cases; a passing in-system receipt can never stand in for these two.
    $recoverySession = [Guid]::NewGuid().ToString()
    $recoveryPlacementDetail = 'origin=system-1:station-1; recoveredAt=system-1:poi-target; acquisitionSnapshot=currentPoi=known,managerReady=True,travelActive=True,usingJumpgate=False,waypoints=1,owned=True,location=system-1:poi-target; placementSnapshot=currentPoi=known,managerReady=True,travelActive=False,usingJumpgate=False,waypoints=0,owned=True,location=system-1:poi-target'
    $recoveryAttemptDetail = "attempt1={target=poi-target,outcome=$TravelRecoveryAttemptSuccess}"
    $recoveryContinuationDetail = 'approachGate=system-1:gate-1; legs=3; routeCompletions=1; gateArrivalSnapshot=currentPoi=known,managerReady=True,travelActive=True,usingJumpgate=True,waypoints=1,owned=True,location=system-2:gate-2; completionSnapshot=currentPoi=known,managerReady=True,travelActive=False,usingJumpgate=False,waypoints=0,owned=True,location=system-2:poi-follow'
    function RecoveryRow($case, $status, $session, $evidence, $detail) { return ($case + "`tdescription`t" + $status + "`tsystem:poi`t" + $session + "`t`t" + $evidence + "`t" + $detail) }
    function RecoveryEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t`tArrived`tInSystem`tsystem-1:station-1`tsystem-1:poi-target`tsystem-1:poi-target`t1.000`t") }
    function RecoverySummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelRecoveryPhase", "budgetSeconds=$TravelRecoveryBudgetSeconds",
            ("required=" + ($TravelRecoveryRequiredCases -join ',')), "recovery-attempts=$TravelRecoveryMaxAttempts",
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelRecoveryRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    function WriteRecoveryOutputs($rows, $events, $summary) {
        [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-recovery-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-recovery-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-recovery.txt'), [string[]]$summary)
    }
    function AssertRecoveryRejected($rows, $events, $summary, $message) {
        WriteRecoveryOutputs $rows $events $summary
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $recoveryRoot $recoveryProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    $recoveryStationRows = @()
    $recoveryStationEvents = @()
    $sequence = 0
    foreach ($case in $TravelStationRequiredCases) {
        $sequence++
        $recoveryStationRows += (TravelRow $case 'passed' $recoverySession ("travel:" + $sequence))
        $recoveryStationEvents += (TravelEvent 'travel' $sequence $case $recoverySession)
    }
    foreach ($case in $TravelRecoveryRequiredCases) {
        $recoveryStationRows += (TravelRow $case 'not-run' $recoverySession '')
    }
    [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$recoveryStationRows)
    [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$recoveryStationEvents)
    [IO.File]::WriteAllLines((Join-Path $recoveryRoot 'travel-station.txt'), [string[]](TravelSummary $recoveryStationRows 'PASS'))
    # A complete in-system phase alone, including its optional NOT-RUN rows for exactly these two
    # cells, is NOT the recovery/continuation phase.
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $recoveryRoot $recoveryProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing recovery receipt accepted because the in-system phase passed.'
    $recoveryAttemptRowText = (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' $recoveryAttemptDetail)
    $recoveryRows = @(
        (RecoveryRow 'recovered-placement' 'passed' $recoverySession 'travel:1' $recoveryPlacementDetail),
        (RecoveryRow 'post-gate-continuation' 'passed' $recoverySession 'travel:2' $recoveryContinuationDetail),
        $recoveryAttemptRowText)
    $recoveryEvents = @((RecoveryEvent 'travel' 1 'recovered-placement' $recoverySession),
        (RecoveryEvent 'travel' 2 'post-gate-continuation' $recoverySession))
    WriteRecoveryOutputs $recoveryRows $recoveryEvents (RecoverySummary $recoveryRows 'PASS')
    Assert-PersistenceProbeReceipt $recoveryRoot $recoveryProvenance
    # A recovery row that does not publish the native readiness state it claims is refused, and so
    # is a placement recorded while the native route was still running or a waypoint remained.
    foreach ($broken in @(
        @{Detail='origin=system-1:station-1'; Message='Recovery row without a recovered location or placement snapshot accepted.'},
        @{Detail=($recoveryPlacementDetail -replace 'managerReady=True','managerReady=False'); Message='Recovery placement without an initialized POI accepted.'},
        @{Detail=($recoveryPlacementDetail -replace 'travelActive=False','travelActive=True'); Message='Recovery placement recorded during an active native route accepted.'},
        @{Detail=($recoveryPlacementDetail -replace 'waypoints=0','waypoints=1'); Message='Recovery placement recorded with remaining native waypoints accepted.'})) {
        $mutated = @((RecoveryRow 'recovered-placement' 'passed' $recoverySession 'travel:1' $broken.Detail), $recoveryRows[1], $recoveryAttemptRowText)
        AssertRecoveryRejected $mutated $recoveryEvents (RecoverySummary $mutated 'PASS') $broken.Message
    }
    # A continuation row must publish three legs, exactly one completion, a gate arrival that still
    # had a native waypoint inside the jump routine, and a completion at the real end of the route.
    foreach ($broken in @(
        @{Detail=($recoveryContinuationDetail -replace 'legs=3','legs=2'); Message='Continuation row with fewer than three legs accepted.'},
        @{Detail=($recoveryContinuationDetail -replace 'routeCompletions=1','routeCompletions=2'); Message='Continuation row with two route completions accepted.'},
        @{Detail=($recoveryContinuationDetail -replace 'usingJumpgate=True,waypoints=1','usingJumpgate=True,waypoints=0'); Message='Continuation gate arrival without a remaining waypoint accepted.'},
        @{Detail=($recoveryContinuationDetail -replace 'usingJumpgate=True','usingJumpgate=False'); Message='Continuation gate arrival outside the native jump routine accepted.'},
        @{Detail=($recoveryContinuationDetail -replace 'usingJumpgate=False,waypoints=0,owned=True,location=system-2:poi-follow','usingJumpgate=False,waypoints=3,owned=True,location=system-2:poi-follow'); Message='Continuation completion with remaining native waypoints accepted.'})) {
        $mutated = @($recoveryRows[0], (RecoveryRow 'post-gate-continuation' 'passed' $recoverySession 'travel:2' $broken.Detail), $recoveryAttemptRowText)
        AssertRecoveryRejected $mutated $recoveryEvents (RecoverySummary $mutated 'PASS') $broken.Message
    }
    # The acquisition snapshot must show a LIVE native route: readiness alone would also match an
    # abandoned route whose manager initialized after the route was already gone.
    foreach ($broken in @(
        @{Detail=($recoveryPlacementDetail -replace 'acquisitionSnapshot=currentPoi=known,managerReady=True,travelActive=True','acquisitionSnapshot=currentPoi=known,managerReady=True,travelActive=False'); Message='Recovery acquisition without a live native route accepted.'},
        @{Detail=($recoveryPlacementDetail -replace 'acquisitionSnapshot=[^;]*; ',''); Message='Recovery row without an acquisition snapshot accepted.'})) {
        $mutated = @((RecoveryRow 'recovered-placement' 'passed' $recoverySession 'travel:1' $broken.Detail), $recoveryRows[1], $recoveryAttemptRowText)
        AssertRecoveryRejected $mutated $recoveryEvents (RecoverySummary $mutated 'PASS') $broken.Message
    }
    # The persisted attempt log is required, bounded, never coverage and must report the cancel the
    # passed case claims.
    $recoveryNoAttempts = @($recoveryRows[0], $recoveryRows[1])
    AssertRecoveryRejected $recoveryNoAttempts $recoveryEvents (RecoverySummary $recoveryNoAttempts 'PASS') 'Passed recovery case without any persisted attempt row accepted.'
    function RecoveryAttempt($number, $outcome, $session = $recoverySession, $status = 'not-run') {
        return (RecoveryRow $TravelRecoveryAttemptRow $status $session '' ("attempt$number={target=poi-$number,outcome=$outcome}"))
    }
    # A miss-only log cannot support the cancel a passed case claims, and a two-attempt log whose
    # earlier attempt is a terminal failure is not a continuable miss either.
    $recoveryMissedOnly = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 1 'native-arrival-first'))
    AssertRecoveryRejected $recoveryMissedOnly $recoveryEvents (RecoverySummary $recoveryMissedOnly 'PASS') 'Passed recovery case whose attempts never report the cancel accepted.'
    $recoveryTerminalBefore = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 1 'timeout-route-running'),
        (RecoveryAttempt 2 $TravelRecoveryAttemptSuccess))
    AssertRecoveryRejected $recoveryTerminalBefore $recoveryEvents (RecoverySummary $recoveryTerminalBefore 'PASS') 'A terminal failure before the successful attempt accepted as a continuable miss.'
    $recoveryTooManyAttempts = @($recoveryRows[0], $recoveryRows[1]) + @(1..($TravelRecoveryMaxAttempts + 1) | ForEach-Object {
        RecoveryAttempt $_ 'native-arrival-first' })
    AssertRecoveryRejected $recoveryTooManyAttempts $recoveryEvents (RecoverySummary $recoveryTooManyAttempts 'PASS') 'More attempt rows than the committed bound accepted.'
    $recoveryAttemptAsCoverage = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'passed' $recoverySession 'travel:1' $recoveryAttemptDetail))
    AssertRecoveryRejected $recoveryAttemptAsCoverage $recoveryEvents (RecoverySummary $recoveryAttemptAsCoverage 'PASS') 'A recovery attempt row recorded as coverage accepted.'
    # The started state is a failure artifact, never a terminal outcome.
    $recoveryAttemptStarted = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' 'attempt1={target=poi-target,state=started}'))
    AssertRecoveryRejected $recoveryAttemptStarted $recoveryEvents (RecoverySummary $recoveryAttemptStarted 'PASS') 'A recovery attempt row still in its started state accepted.'
    $recoveryAttemptNoOutcome = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' 'started'))
    AssertRecoveryRejected $recoveryAttemptNoOutcome $recoveryEvents (RecoverySummary $recoveryAttemptNoOutcome 'PASS') 'A recovery attempt row without an outcome accepted.'
    # A miss row that still marks its own cleanup pending never completed the attempt.
    $recoveryAttemptPending = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' ("attempt1={target=poi-1,outcome=timeout-no-route,detail=" + $TravelRecoveryAttemptPendingMarker + "}")))
    AssertRecoveryRejected $recoveryAttemptPending $recoveryEvents (RecoverySummary $recoveryAttemptPending 'PASS') 'A recovery attempt row still marking its cleanup pending accepted.'
    # A miss that closed its own leg must publish the settled cleanup recovery.
    $recoveryAttemptUnsettled = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' 'attempt1={target=poi-1,outcome=timeout-no-route,detail=missCleanup={nativeTravelActive=False,cancelAccepted=True,window=[Requested Departed Cancelled]}}'),
        (RecoveryAttempt 2 $TravelRecoveryAttemptSuccess))
    AssertRecoveryRejected $recoveryAttemptUnsettled $recoveryEvents (RecoverySummary $recoveryAttemptUnsettled 'PASS') 'A missed attempt that closed its leg without a settled cleanup recovery accepted.'
    $recoveryAttemptSettled = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryRow $TravelRecoveryAttemptRow 'not-run' $recoverySession '' 'attempt1={target=poi-1,outcome=timeout-no-route,detail=missCleanup={nativeTravelActive=False,cancelAccepted=True,window=[Requested Departed Cancelled RecoveredPlacement],settlement=currentPoi=known,managerReady=True,travelActive=False,usingJumpgate=False,waypoints=0,owned=True,location=system-1:poi-1}}'),
        (RecoveryAttempt 2 $TravelRecoveryAttemptSuccess))
    WriteRecoveryOutputs $recoveryAttemptSettled $recoveryEvents (RecoverySummary $recoveryAttemptSettled 'PASS')
    Assert-PersistenceProbeReceipt $recoveryRoot $recoveryProvenance
    # Schema regressions: duplicate, gapped, foreign-session, unknown outcome and success-not-last.
    $recoveryAttemptDuplicate = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 1 'native-arrival-first'),
        (RecoveryAttempt 1 $TravelRecoveryAttemptSuccess))
    AssertRecoveryRejected $recoveryAttemptDuplicate $recoveryEvents (RecoverySummary $recoveryAttemptDuplicate 'PASS') 'A duplicated recovery attempt number accepted.'
    $recoveryAttemptGap = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 2 'native-arrival-first'),
        (RecoveryAttempt 3 $TravelRecoveryAttemptSuccess))
    AssertRecoveryRejected $recoveryAttemptGap $recoveryEvents (RecoverySummary $recoveryAttemptGap 'PASS') 'A gapped recovery attempt numbering accepted.'
    $recoveryAttemptForeign = @($recoveryRows[0], $recoveryRows[1],
        (RecoveryAttempt 1 $TravelRecoveryAttemptSuccess ([Guid]::NewGuid().ToString())))
    AssertRecoveryRejected $recoveryAttemptForeign $recoveryEvents (RecoverySummary $recoveryAttemptForeign 'PASS') 'A foreign-session recovery attempt row accepted.'
    $recoveryAttemptUnknown = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 1 'it-worked-out-fine'))
    AssertRecoveryRejected $recoveryAttemptUnknown $recoveryEvents (RecoverySummary $recoveryAttemptUnknown 'PASS') 'An unknown recovery attempt outcome accepted.'
    $recoveryAttemptOutOfOrder = @($recoveryRows[0], $recoveryRows[1], (RecoveryAttempt 1 $TravelRecoveryAttemptSuccess),
        (RecoveryAttempt 2 'native-arrival-first'))
    AssertRecoveryRejected $recoveryAttemptOutOfOrder $recoveryEvents (RecoverySummary $recoveryAttemptOutOfOrder 'PASS') 'A successful recovery attempt that is not the last accepted.'
    # A receipt that forges its own bound cannot widen the committed one.
    $recoveryForgedBound = @((RecoverySummary $recoveryRows 'PASS') | ForEach-Object { if ($_ -like 'recovery-attempts=*') { "recovery-attempts=$($TravelRecoveryMaxAttempts + 5)" } else { $_ } })
    AssertRecoveryRejected $recoveryRows $recoveryEvents $recoveryForgedBound 'A forged recovery attempt bound accepted.'
    $recoveryWrongBound = @((RecoverySummary $recoveryRows 'PASS') | ForEach-Object { if ($_ -like 'recovery-attempts=*') { 'recovery-attempts=99' } else { $_ } })
    AssertRecoveryRejected $recoveryRows $recoveryEvents $recoveryWrongBound 'Recovery receipt declaring a different attempt bound accepted.'
    $recoverySkipped = @($TravelRecoveryRequiredCases | ForEach-Object { RecoveryRow $_ 'not-run' $recoverySession '' 'no native window' }) + @($recoveryAttemptRowText)
    AssertRecoveryRejected $recoverySkipped $recoveryEvents (RecoverySummary $recoverySkipped 'PASS') 'All-skipped recovery coverage accepted as PASS.'
    $recoveryMissing = @($recoveryRows[0], $recoveryAttemptRowText)
    AssertRecoveryRejected $recoveryMissing $recoveryEvents (RecoverySummary $recoveryMissing 'PASS') 'Missing mandatory recovery case accepted.'
    $recoveryDuplicated = $recoveryRows + @($recoveryRows[0])
    AssertRecoveryRejected $recoveryDuplicated $recoveryEvents (RecoverySummary $recoveryDuplicated 'PASS') 'Duplicated recovery case accepted.'
    $recoveryFailed = @($recoveryRows[0], (RecoveryRow 'post-gate-continuation' 'failed' $recoverySession 'travel:2' $recoveryContinuationDetail), $recoveryAttemptRowText)
    AssertRecoveryRejected $recoveryFailed $recoveryEvents (RecoverySummary $recoveryFailed 'PASS') 'Claimed recovery PASS with a failed case accepted.'
    $recoveryForeign = @($recoveryEvents | ForEach-Object { $_ -replace [regex]::Escape($recoverySession), ([Guid]::NewGuid().ToString()) })
    AssertRecoveryRejected $recoveryRows $recoveryForeign (RecoverySummary $recoveryRows 'PASS') 'Recovery identities absent from the event trace accepted.'
    AssertRecoveryRejected $recoveryRows $recoveryEvents (RecoverySummary $recoveryRows 'FAIL') 'Failed recovery attempt summary accepted.'
    AssertRecoveryRejected $recoveryRows $recoveryEvents @('INCOMPLETE', "phase=$TravelRecoveryPhase", "budgetSeconds=$TravelRecoveryBudgetSeconds",
        "recovery-attempts=$TravelRecoveryMaxAttempts", 'activeCase=recovered-placement', 'rows=3 passed=2 failed=0 notRun=1',
        'result=pilot still running or externally terminated; this is not a pass.') 'Incomplete recovery checkpoint accepted as a pass.'
    $recoveryOverBudget = @((RecoverySummary $recoveryRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($TravelRecoveryBudgetSeconds + 1)" } else { $_ } })
    AssertRecoveryRejected $recoveryRows $recoveryEvents $recoveryOverBudget 'Recovery budget above the launcher reservation accepted.'
    $recoveryForeignPhase = @((RecoverySummary $recoveryRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelResiliencePhase" } else { $_ } })
    AssertRecoveryRejected $recoveryRows $recoveryEvents $recoveryForeignPhase 'Recovery receipt declaring another phase accepted.'
    WriteRecoveryOutputs $recoveryRows $recoveryEvents (RecoverySummary $recoveryRows 'PASS')
    Assert-PersistenceProbeReceipt $recoveryRoot $recoveryProvenance
    & $script -Action Cleanup -SandboxRoot $recoveryRoot
    # The launcher must reserve base + EVERY selected phase budget before starting the game.
    $recoveryTimeoutRoot = Join-Path $work 'travel-recovery-timeout-sandbox'
    $sandboxes += $recoveryTimeoutRoot
    & $script -Action Prepare -SandboxRoot $recoveryTimeoutRoot -TravelStation -TravelRecoveryContinuation @options
    $recoveryMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelRecoveryBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $recoveryTimeoutRoot -TimeoutSeconds ($recoveryMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $recoveryMinimum*" }
    Assert $rejected 'Recovery run accepted a lifetime one second below the derived minimum.'
    Assert (!(Test-Path -LiteralPath (Join-Path $recoveryTimeoutRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient recovery lifetime.'
    & $script -Action Cleanup -SandboxRoot $recoveryTimeoutRoot

    # --- separate optional native fast-lane phase ------------------------------------------------
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-fast-lane') -TravelFastLane @options }
    catch { $rejected = $_.Exception.Message -like '*requires the travel/station selection*' }
    Assert $rejected 'Fast-lane phase accepted without the travel/station selection.'
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'invalid-fast-lane'))) 'Rejected fast-lane selection left a prepared sandbox.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-fast-lane-scenario') -Scenario MissingApi -TravelFastLane @options }
    catch { $rejected = $true }
    Assert $rejected 'Fast-lane phase accepted outside Full.'
    $fastLaneRoot = Join-Path $work 'travel-fast-lane-sandbox'
    $sandboxes += $fastLaneRoot
    & $script -Action Prepare -SandboxRoot $fastLaneRoot -TravelStation -TravelFastLane @options
    $fastLaneProvenance = Assert-QualificationInputs $fastLaneRoot
    Assert ($fastLaneProvenance.travelFastLane -and $fastLaneProvenance.travelFastLaneBudgetSeconds -eq $TravelFastLaneBudgetSeconds) 'Prepared fast-lane selection/budget missing.'
    $fastLaneMarker = Join-Path $fastLaneRoot 'travel-fast-lane.enabled'
    [IO.File]::WriteAllText($fastLaneMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $fastLaneRoot } catch { $rejected = $true }
    Assert $rejected 'Changed fast-lane marker accepted.'
    [IO.File]::WriteAllText($fastLaneMarker, 'fast-lane-v1')
    Remove-Item -LiteralPath $fastLaneMarker
    $rejected = $false
    try { $null = Assert-QualificationInputs $fastLaneRoot } catch { $rejected = $true }
    Assert $rejected 'Removed fast-lane marker accepted while provenance still selects it.'
    [IO.File]::WriteAllText($fastLaneMarker, 'fast-lane-v1')
    $fastLaneProvenancePath = Join-Path $fastLaneRoot 'build-provenance.json'
    $fastLaneProvenanceText = [IO.File]::ReadAllText($fastLaneProvenancePath)
    [IO.File]::WriteAllText($fastLaneProvenancePath, ($fastLaneProvenanceText -replace '"travelFastLaneBudgetSeconds": *\d+', '"travelFastLaneBudgetSeconds": 60'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $fastLaneRoot } catch { $rejected = $true }
    Assert $rejected 'Edited fast-lane budget reservation accepted.'
    [IO.File]::WriteAllText($fastLaneProvenancePath, $fastLaneProvenanceText)
    $fastLaneProvenance = Assert-QualificationInputs $fastLaneRoot
    # Synthetic receipts only. The in-system phase is selected here too and keeps its own required
    # cases; a passing in-system receipt can never stand in for these two.
    $fastLaneSession = [Guid]::NewGuid().ToString()
    $fastLaneChainDetail = 'firstGate=system-a:gate-a; secondGate=system-b:gate-b2; destination=system-c:poi-destination; systems=3; gates=2; legs=5; routeCompletions=1; completionSnapshot=currentPoi=known,managerReady=True,travelActive=False,usingJumpgate=False,multiplier=1,fastLaneActive=False,waypoints=0,owned=True,location=system-c:poi-destination'
    $fastLaneSnapshot = 'currentPoi=known,managerReady=True,travelActive=True,usingJumpgate=False,multiplier=7,fastLaneActive=True,waypoints=1,owned=True,location=system-b:gate-b2'
    $fastLaneMultiplierDetail = "fastLaneUnlocked=True (read-only; never written); fastLaneLeg=system-b:gate-b1->system-b:gate-b2; fastLaneMultiplier=7; fastLaneActive=True; approachMultiplier=1; postFastLaneMultiplier=1; requestedSnapshot=$fastLaneSnapshot; departedSnapshot=$fastLaneSnapshot; arrivedSnapshot=$fastLaneSnapshot"
    function FastLaneRow($case, $status, $session, $evidence, $detail) { return ($case + "`tdescription`t" + $status + "`tsystem:poi`t" + $session + "`t`t" + $evidence + "`t" + $detail) }
    function FastLaneEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t`tArrived`tInSystem`tsystem-b:gate-b1`tsystem-b:gate-b2`tsystem-b:gate-b2`t1.000`t") }
    function FastLaneSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelFastLanePhase", "budgetSeconds=$TravelFastLaneBudgetSeconds",
            ("required=" + ($TravelFastLaneRequiredCases -join ',')), "fast-lane-multiplier=$TravelFastLaneMultiplier",
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelFastLaneRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    function WriteFastLaneOutputs($rows, $events, $summary) {
        [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-fast-lane-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-fast-lane-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-fast-lane.txt'), [string[]]$summary)
    }
    function AssertFastLaneRejected($rows, $events, $summary, $message) {
        WriteFastLaneOutputs $rows $events $summary
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $fastLaneRoot $fastLaneProvenance } catch { $rejected = $true }
        Assert $rejected $message
    }
    $fastLaneStationRows = @()
    $fastLaneStationEvents = @()
    $sequence = 0
    foreach ($case in $TravelStationRequiredCases) {
        $sequence++
        $fastLaneStationRows += (TravelRow $case 'passed' $fastLaneSession ("travel:" + $sequence))
        $fastLaneStationEvents += (TravelEvent 'travel' $sequence $case $fastLaneSession)
    }
    [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-station-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$fastLaneStationRows)
    [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-station-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$fastLaneStationEvents)
    [IO.File]::WriteAllLines((Join-Path $fastLaneRoot 'travel-station.txt'), [string[]](TravelSummary $fastLaneStationRows 'PASS'))
    # A complete in-system phase alone is NOT the fast-lane phase.
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $fastLaneRoot $fastLaneProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing fast-lane receipt accepted because the in-system phase passed.'
    $fastLaneRows = @(
        (FastLaneRow 'fast-lane-gate-chain' 'passed' $fastLaneSession 'travel:1' $fastLaneChainDetail),
        (FastLaneRow 'fast-lane-multiplier-observed' 'passed' $fastLaneSession 'travel:2' $fastLaneMultiplierDetail))
    $fastLaneEvents = @((FastLaneEvent 'travel' 1 'fast-lane-gate-chain' $fastLaneSession),
        (FastLaneEvent 'travel' 2 'fast-lane-multiplier-observed' $fastLaneSession))
    WriteFastLaneOutputs $fastLaneRows $fastLaneEvents (FastLaneSummary $fastLaneRows 'PASS')
    Assert-PersistenceProbeReceipt $fastLaneRoot $fastLaneProvenance
    # The chain row must publish the two gates, three systems, five legs and the single completion.
    foreach ($broken in @(
        @{Detail=($fastLaneChainDetail -replace 'gates=2','gates=1'); Message='Fast-lane chain with a single gate accepted.'},
        @{Detail=($fastLaneChainDetail -replace 'systems=3','systems=2'); Message='Fast-lane chain that never reached a third system accepted.'},
        @{Detail=($fastLaneChainDetail -replace 'legs=5','legs=3'); Message='Fast-lane chain with three legs accepted.'},
        @{Detail=($fastLaneChainDetail -replace 'routeCompletions=1','routeCompletions=2'); Message='Fast-lane chain with two route completions accepted.'},
        @{Detail=($fastLaneChainDetail -replace 'completionSnapshot=currentPoi=known,managerReady=True,travelActive=False','completionSnapshot=currentPoi=known,managerReady=True,travelActive=True'); Message='Fast-lane completion recorded during an active native route accepted.'})) {
        $mutated = @((FastLaneRow 'fast-lane-gate-chain' 'passed' $fastLaneSession 'travel:1' $broken.Detail), $fastLaneRows[1])
        AssertFastLaneRejected $mutated $fastLaneEvents (FastLaneSummary $mutated 'PASS') $broken.Message
    }
    # The multiplier row must publish the observed native transient, at every boundary of the
    # gate-to-gate leg, with the surrounding legs at the resting value and a read-only precondition.
    foreach ($broken in @(
        @{Detail=($fastLaneMultiplierDetail -replace 'fastLaneMultiplier=7','fastLaneMultiplier=1'); Message='Fast-lane case without the observed native multiplier accepted.'},
        @{Detail=($fastLaneMultiplierDetail -replace 'approachMultiplier=1','approachMultiplier=7'); Message='Permanent fast-lane state accepted as the charge transient.'},
        @{Detail=($fastLaneMultiplierDetail -replace 'postFastLaneMultiplier=1','postFastLaneMultiplier=7'); Message='Fast-lane state that never reset accepted.'},
        @{Detail=($fastLaneMultiplierDetail -replace 'fastLaneUnlocked=True \(read-only; never written\)','fastLaneUnlocked=True'); Message='Fast-lane case without the read-only unlock precondition accepted.'},
        @{Detail=($fastLaneMultiplierDetail -replace 'departedSnapshot=currentPoi=known,managerReady=True,travelActive=True,usingJumpgate=False,multiplier=7','departedSnapshot=currentPoi=known,managerReady=True,travelActive=True,usingJumpgate=False,multiplier=1'); Message='Fast-lane leg whose departure was not at the charge multiplier accepted.'})) {
        $mutated = @($fastLaneRows[0], (FastLaneRow 'fast-lane-multiplier-observed' 'passed' $fastLaneSession 'travel:2' $broken.Detail))
        AssertFastLaneRejected $mutated $fastLaneEvents (FastLaneSummary $mutated 'PASS') $broken.Message
    }
    $fastLaneSkipped = @($TravelFastLaneRequiredCases | ForEach-Object { FastLaneRow $_ 'not-run' $fastLaneSession '' 'no native two-gate chain' })
    AssertFastLaneRejected $fastLaneSkipped $fastLaneEvents (FastLaneSummary $fastLaneSkipped 'PASS') 'All-skipped fast-lane coverage accepted as PASS.'
    $fastLaneMissing = @($fastLaneRows[0])
    AssertFastLaneRejected $fastLaneMissing $fastLaneEvents (FastLaneSummary $fastLaneMissing 'PASS') 'Missing mandatory fast-lane case accepted.'
    $fastLaneDuplicated = $fastLaneRows + @($fastLaneRows[0])
    AssertFastLaneRejected $fastLaneDuplicated $fastLaneEvents (FastLaneSummary $fastLaneDuplicated 'PASS') 'Duplicated fast-lane case accepted.'
    $fastLaneExtra = $fastLaneRows + @((FastLaneRow 'fast-lane-bonus' 'passed' $fastLaneSession 'travel:1' 'detail'))
    AssertFastLaneRejected $fastLaneExtra $fastLaneEvents (FastLaneSummary $fastLaneExtra 'PASS') 'A fabricated fast-lane case identity accepted.'
    $fastLaneFailed = @($fastLaneRows[0], (FastLaneRow 'fast-lane-multiplier-observed' 'failed' $fastLaneSession 'travel:2' $fastLaneMultiplierDetail))
    AssertFastLaneRejected $fastLaneFailed $fastLaneEvents (FastLaneSummary $fastLaneFailed 'PASS') 'Claimed fast-lane PASS with a failed case accepted.'
    $fastLaneForeign = @($fastLaneEvents | ForEach-Object { $_ -replace [regex]::Escape($fastLaneSession), ([Guid]::NewGuid().ToString()) })
    AssertFastLaneRejected $fastLaneRows $fastLaneForeign (FastLaneSummary $fastLaneRows 'PASS') 'Fast-lane identities absent from the event trace accepted.'
    AssertFastLaneRejected $fastLaneRows $fastLaneEvents (FastLaneSummary $fastLaneRows 'FAIL') 'Failed fast-lane attempt summary accepted.'
    AssertFastLaneRejected $fastLaneRows $fastLaneEvents @('INCOMPLETE', "phase=$TravelFastLanePhase", "budgetSeconds=$TravelFastLaneBudgetSeconds",
        "fast-lane-multiplier=$TravelFastLaneMultiplier", 'activeCase=fast-lane-gate-chain', 'rows=2 passed=2 failed=0 notRun=0',
        'result=pilot still running or externally terminated; this is not a pass.') 'Incomplete fast-lane checkpoint accepted as a pass.'
    $fastLaneOverBudget = @((FastLaneSummary $fastLaneRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($TravelFastLaneBudgetSeconds + 1)" } else { $_ } })
    AssertFastLaneRejected $fastLaneRows $fastLaneEvents $fastLaneOverBudget 'Fast-lane budget above the launcher reservation accepted.'
    $fastLaneForgedMultiplier = @((FastLaneSummary $fastLaneRows 'PASS') | ForEach-Object { if ($_ -like 'fast-lane-multiplier=*') { 'fast-lane-multiplier=2' } else { $_ } })
    AssertFastLaneRejected $fastLaneRows $fastLaneEvents $fastLaneForgedMultiplier 'A forged fast-lane multiplier declaration accepted.'
    $fastLaneForeignPhase = @((FastLaneSummary $fastLaneRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelRecoveryPhase" } else { $_ } })
    AssertFastLaneRejected $fastLaneRows $fastLaneEvents $fastLaneForeignPhase 'Fast-lane receipt declaring another phase accepted.'
    WriteFastLaneOutputs $fastLaneRows $fastLaneEvents (FastLaneSummary $fastLaneRows 'PASS')
    Assert-PersistenceProbeReceipt $fastLaneRoot $fastLaneProvenance
    & $script -Action Cleanup -SandboxRoot $fastLaneRoot
    # The launcher must reserve base + EVERY selected phase budget before starting the game.
    $fastLaneTimeoutRoot = Join-Path $work 'travel-fast-lane-timeout-sandbox'
    $sandboxes += $fastLaneTimeoutRoot
    & $script -Action Prepare -SandboxRoot $fastLaneTimeoutRoot -TravelStation -TravelFastLane @options
    $fastLaneMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelFastLaneBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $fastLaneTimeoutRoot -TimeoutSeconds ($fastLaneMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $fastLaneMinimum*" }
    Assert $rejected 'Fast-lane run accepted a lifetime one second below the derived minimum.'
    Assert (!(Test-Path -LiteralPath (Join-Path $fastLaneTimeoutRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient fast-lane lifetime.'
    & $script -Action Cleanup -SandboxRoot $fastLaneTimeoutRoot

    # --- separate optional actual-consumer travel probe -----------------------------------------
    $invalidConsumerSelections = @(
        @{ TravelStation = $true; TravelCrossSystem = $true; TravelWormholeFixture = $true },  # no consumer binary
        @{ TravelStation = $true })                                                            # no cross-system phase
    for ($i = 0; $i -lt $invalidConsumerSelections.Count; $i++) {
        $invalidRoot = Join-Path $work ('invalid-anima-travel-' + $i)
        $selection = $invalidConsumerSelections[$i]
        $rejected = $false
        try { & $script -Action Prepare -SandboxRoot $invalidRoot -AnimaTravelProbe @selection @options }
        catch { $rejected = $_.Exception.Message -like '*Anima consumer travel probe requires*' }
        Assert $rejected ('Anima consumer travel probe accepted without its prerequisites: ' + $i)
        Assert (!(Test-Path -LiteralPath $invalidRoot)) 'Rejected consumer travel selection left a prepared sandbox.'
    }
    $consumerRoot = Join-Path $work 'anima-travel-sandbox'
    $sandboxes += $consumerRoot
    & $script -Action Prepare -SandboxRoot $consumerRoot -PersistenceProbe -MissionTransitionsProbe -MissionIdentityProbe `
        -TravelStation -TravelCrossSystem -TravelWormholeFixture @options
    # Synthesize the prepared consumer selections this harness cannot build without real consumer
    # binaries; every rule exercised below is still the launcher's own.
    $consumerProvenancePath = Join-Path $consumerRoot 'build-provenance.json'
    $consumerProvenance = Get-Content -LiteralPath $consumerProvenancePath -Raw | ConvertFrom-Json
    $consumerProvenance.missionJournal = $true
    $consumerProvenance.anima = $true
    $consumerProvenance.animaRevision = 'b' * 40
    $consumerProvenance.animaVersion = $AnimaTravelProbeVersion
    $consumerProvenance.animaTravelProbe = $true
    $consumerProvenance.animaTravelBudgetSeconds = $AnimaTravelBudgetSeconds
    foreach ($name in @('VGMissionJournal.dll','Newtonsoft.Json.dll','VGAnima.dll')) {
        $fake = Join-Path $consumerRoot "game\BepInEx\plugins\$name"
        [IO.File]::WriteAllText($fake, 'synthetic-not-executable')
        $consumerProvenance.plugins | Add-Member -NotePropertyName $name -NotePropertyValue (Get-FileHash -LiteralPath $fake).Hash
    }
    $consumerProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $consumerProvenancePath
    [IO.File]::WriteAllText((Join-Path $consumerRoot 'missionjournal.enabled'), 'pilot-v1')
    [IO.File]::WriteAllText((Join-Path $consumerRoot 'game\BepInEx\config\vgmissionjournal.cfg'), "[Persistence]`nUseApiSaveData = false`n")
    [IO.File]::WriteAllText((Join-Path $consumerRoot 'anima-missions.enabled'), 'anima-v1')
    $consumerMarker = Join-Path $consumerRoot 'anima-travel.enabled'
    [IO.File]::WriteAllText($consumerMarker, 'anima-travel-v1')
    [IO.File]::WriteAllText((Join-Path $consumerRoot 'game\BepInEx\config\vganima.cfg'), "[General]`nEnabled = true`n[Llm]`nEnabled = false`nBaseUrl = `nApiKey = `n")
    $consumerProvenance = Assert-QualificationInputs $consumerRoot
    Assert ($consumerProvenance.animaTravelProbe -and $consumerProvenance.animaTravelBudgetSeconds -eq $AnimaTravelBudgetSeconds) 'Prepared consumer travel selection/budget missing.'
    [IO.File]::WriteAllText($consumerMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $consumerRoot } catch { $rejected = $true }
    Assert $rejected 'Changed consumer travel marker accepted.'
    [IO.File]::WriteAllText($consumerMarker, 'anima-travel-v1')
    Remove-Item -LiteralPath $consumerMarker
    $rejected = $false
    try { $null = Assert-QualificationInputs $consumerRoot } catch { $rejected = $true }
    Assert $rejected 'Removed consumer travel marker accepted while provenance still selects it.'
    [IO.File]::WriteAllText($consumerMarker, 'anima-travel-v1')
    $consumerProvenanceText = [IO.File]::ReadAllText($consumerProvenancePath)
    foreach ($edit in @(@{Pattern='"animaTravelBudgetSeconds": *\d+'; Value='"animaTravelBudgetSeconds": 60'; Message='Edited consumer travel budget reservation accepted.'},
        @{Pattern='"animaVersion": *"[^"]*"'; Value='"animaVersion": "0.3.0.0"'; Message='Consumer travel probe accepted the older pinned consumer version.'},
        @{Pattern='"travelWormholeFixture": *true'; Value='"travelWormholeFixture": false'; Message='Consumer travel probe accepted a run without the wormhole fixture selection.'})) {
        [IO.File]::WriteAllText($consumerProvenancePath, ($consumerProvenanceText -replace $edit.Pattern, $edit.Value))
        $rejected = $false
        try { $null = Assert-QualificationInputs $consumerRoot } catch { $rejected = $true }
        Assert $rejected $edit.Message
    }
    # A DELETED property must fail exactly like a wrong one: the consumer version pin is REQUIRED
    # while the probe is selected, never "checked only when present".
    $withoutVersion = Get-Content -LiteralPath $consumerProvenancePath -Raw | ConvertFrom-Json
    $withoutVersion.PSObject.Properties.Remove('animaVersion')
    Assert (!$withoutVersion.PSObject.Properties['animaVersion']) 'Consumer version pin was not removed by the test fixture.'
    $withoutVersion | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $consumerProvenancePath
    $rejected = $false
    try { $null = Assert-QualificationInputs $consumerRoot } catch { $rejected = $true }
    Assert $rejected 'Consumer travel probe accepted provenance with the consumer version pin removed.'
    [IO.File]::WriteAllText($consumerProvenancePath, $consumerProvenanceText)
    $consumerProvenance = Assert-QualificationInputs $consumerRoot
    # The launcher must reserve base + the two reused phases + this probe's own budget.
    $consumerMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelCrossSystemBudgetSeconds + $AnimaTravelBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $consumerRoot -TimeoutSeconds ($consumerMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $consumerMinimum*" }
    Assert $rejected 'Consumer travel run accepted a lifetime one second below the derived minimum.'
    Assert (!(Test-Path -LiteralPath (Join-Path $consumerRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient consumer travel lifetime.'
    # Synthetic receipts only. The probe reuses the two travel phases, so its own receipt must carry
    # its own mandatory cases AND the run must prove it observed them before the mission pilot.
    $consumerSession = [Guid]::NewGuid().ToString()
    function ConsumerEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t`tArrived`tJumpGate`tsystem-1:gate`tsystem-2:poi`tsystem-2:poi`t900.000`t") }
    function ConsumerSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$AnimaTravelPhase", "budgetSeconds=$AnimaTravelBudgetSeconds",
            ("required=" + ($AnimaTravelRequiredCases -join ',')),
            ("required-subcases=" + ($AnimaTravelRequiredSubcaseRows -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $AnimaTravelRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        foreach ($subcase in $AnimaTravelRequiredSubcaseRows) {
            $matched = @($records | Where-Object { $_[0] -eq $subcase })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-subcase $subcase=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    $consumerOrderedResult = @('PASS') + $AnimaTravelReusedPhaseScenarios + @($AnimaTravelPhase, 'native-anima-api-missions')
    function WriteConsumerOutputs($rows, $events, $summary, $result) {
        [IO.File]::WriteAllLines((Join-Path $consumerRoot 'anima-travel-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $consumerRoot 'anima-travel-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $consumerRoot 'anima-travel.txt'), [string[]]$summary)
        [IO.File]::WriteAllLines((Join-Path $consumerRoot 'result.txt'), [string[]]$result)
    }
    function AssertConsumerRejected($rows, $events, $summary, $result, $message) {
        WriteConsumerOutputs $rows $events $summary $result
        $rejected = $false
        try { Assert-AnimaTravelReceipt $consumerRoot } catch { $rejected = $true }
        Assert $rejected $message
    }
    # The ordering proof is the reason this probe exists, so those refusals are matched on their
    # exact reason instead of on "something threw".
    function AssertConsumerOrderRejected($rows, $events, $summary, $result, $expected, $message) {
        WriteConsumerOutputs $rows $events $summary $result
        $reason = ''
        try { Assert-AnimaTravelReceipt $consumerRoot } catch { $reason = $_.Exception.Message }
        Assert ($reason -like $expected) ($message + " Reason was: '$reason'")
    }
    # The receipt is dispatched from the shared probe validator whenever the selection is prepared.
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $consumerRoot ([pscustomobject]@{ animaTravelProbe = $true }) } catch { $rejected = $true }
    Assert $rejected 'Missing consumer travel receipt accepted while the probe was selected.'
    $consumerRows = @()
    $consumerEvents = @()
    $sequence = 0
    foreach ($case in @($AnimaTravelRequiredCases) + @($AnimaTravelRequiredSubcaseRows)) {
        $sequence++
        $consumerRows += (TravelRow $case 'passed' $consumerSession ("travel:" + $sequence))
        $consumerEvents += (ConsumerEvent 'travel' $sequence $case $consumerSession)
    }
    WriteConsumerOutputs $consumerRows $consumerEvents (ConsumerSummary $consumerRows 'PASS') $consumerOrderedResult
    Assert-AnimaTravelReceipt $consumerRoot
    Assert-PersistenceProbeReceipt $consumerRoot ([pscustomobject]@{ animaTravelProbe = $true })
    $consumerSkipped = @($AnimaTravelRequiredCases | ForEach-Object { TravelRow $_ 'not-run' $consumerSession '' }) + @($AnimaTravelRequiredSubcaseRows | ForEach-Object { TravelRow $_ 'passed' $consumerSession 'travel:1' })
    AssertConsumerRejected $consumerSkipped $consumerEvents (ConsumerSummary $consumerSkipped 'PASS') $consumerOrderedResult 'All-skipped consumer coverage accepted as PASS.'
    $lastRow = $consumerRows.Count - 1
    $consumerFailed = @($consumerRows[0..($lastRow - 1)]) + @(TravelRow $AnimaTravelRequiredSubcaseRows[-1] 'failed' $consumerSession ("travel:" + $consumerRows.Count))
    AssertConsumerRejected $consumerFailed $consumerEvents (ConsumerSummary $consumerFailed 'PASS') $consumerOrderedResult 'Claimed consumer PASS with a failed row accepted.'
    $consumerMissingSubcase = @($consumerRows | Where-Object { $_ -notlike ($AnimaTravelRequiredSubcaseRows[0] + "`t*") })
    AssertConsumerRejected $consumerMissingSubcase $consumerEvents (ConsumerSummary $consumerMissingSubcase 'PASS') $consumerOrderedResult 'Consumer receipt without a mandatory subcase row accepted.'
    $consumerMissingCase = @($consumerRows | Where-Object { $_ -notlike "wormhole-arrival-visit`t*" })
    AssertConsumerRejected $consumerMissingCase $consumerEvents (ConsumerSummary $consumerMissingCase 'PASS') $consumerOrderedResult 'Missing mandatory consumer case accepted.'
    $consumerNoEvidence = @($consumerRows | Where-Object { $_ -notlike "visit-persistence`t*" }) + @(TravelRow 'visit-persistence' 'passed' $consumerSession '')
    AssertConsumerRejected $consumerNoEvidence $consumerEvents (ConsumerSummary $consumerNoEvidence 'PASS') $consumerOrderedResult 'Consumer case without observed public events accepted.'
    $consumerForeign = @($consumerEvents | ForEach-Object { $_ -replace [regex]::Escape($consumerSession), ([Guid]::NewGuid().ToString()) })
    AssertConsumerRejected $consumerRows $consumerForeign (ConsumerSummary $consumerRows 'PASS') $consumerOrderedResult 'Consumer identities absent from the event trace accepted.'
    AssertConsumerRejected $consumerRows $consumerEvents (ConsumerSummary $consumerRows 'FAIL') $consumerOrderedResult 'Failed consumer attempt summary accepted.'
    AssertConsumerRejected $consumerRows $consumerEvents @('INCOMPLETE', "phase=$AnimaTravelPhase", "budgetSeconds=$AnimaTravelBudgetSeconds", "required-subcases=$($AnimaTravelRequiredSubcaseRows -join ',')", 'activeCase=gate-arrival-visit', ("rows=" + $consumerRows.Count + " passed=" + $consumerRows.Count + " failed=0 notRun=0"), 'result=pilot still running or externally terminated; this is not a pass.') $consumerOrderedResult 'Incomplete consumer checkpoint accepted as a pass.'
    $consumerForeignPhase = @((ConsumerSummary $consumerRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelCrossSystemPhase" } else { $_ } })
    AssertConsumerRejected $consumerRows $consumerEvents $consumerForeignPhase $consumerOrderedResult 'Consumer receipt declaring a reused travel phase accepted.'
    $consumerOverBudget = @((ConsumerSummary $consumerRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($AnimaTravelBudgetSeconds + 1)" } else { $_ } })
    AssertConsumerRejected $consumerRows $consumerEvents $consumerOverBudget $consumerOrderedResult 'Consumer budget above the launcher reservation accepted.'
    # ORDERING: the probe must have observed both reused phases and finished before the mission
    # pilot, whose StopProvider permanently disposes the consumer's visit observer.
    $consumerSummaryLines = (ConsumerSummary $consumerRows 'PASS')
    $lateProbe = @('PASS') + $AnimaTravelReusedPhaseScenarios + @('native-anima-api-missions', $AnimaTravelPhase)
    AssertConsumerOrderRejected $consumerRows $consumerEvents $consumerSummaryLines $lateProbe '*mission pilot ran before the consumer travel probe*' 'Consumer probe recorded after the Anima mission pilot accepted.'
    $missingReuse = @('PASS', $AnimaTravelReusedPhaseScenarios[0], $AnimaTravelPhase, 'native-anima-api-missions')
    AssertConsumerOrderRejected $consumerRows $consumerEvents $consumerSummaryLines $missingReuse "*exactly one recorded '$($AnimaTravelReusedPhaseScenarios[1])'*" 'Consumer probe accepted without the reused cross-system phase.'
    $duplicatedReuse = @('PASS') + $AnimaTravelReusedPhaseScenarios + $AnimaTravelReusedPhaseScenarios + @($AnimaTravelPhase, 'native-anima-api-missions')
    AssertConsumerOrderRejected $consumerRows $consumerEvents $consumerSummaryLines $duplicatedReuse '*exactly one recorded*' 'A reused travel phase recorded twice was accepted.'
    $probeAfterReuse = @('PASS', $AnimaTravelReusedPhaseScenarios[0], $AnimaTravelPhase, $AnimaTravelReusedPhaseScenarios[1], 'native-anima-api-missions')
    AssertConsumerOrderRejected $consumerRows $consumerEvents $consumerSummaryLines $probeAfterReuse '*completed before the reused phase*' 'Consumer probe completing before a reused phase accepted.'
    WriteConsumerOutputs $consumerRows $consumerEvents $consumerSummaryLines $consumerOrderedResult
    Assert-AnimaTravelReceipt $consumerRoot
    $consumerOutcomePath = Join-Path $consumerRoot 'run-outcome.json'
    @{timedOut=$false;killed=$true;exitCode=$null} | ConvertTo-Json | Set-Content -LiteralPath $consumerOutcomePath
    $rejected = $false
    try { Assert-AnimaTravelReceipt $consumerRoot } catch { $rejected = $true }
    Assert $rejected 'Terminated launcher outcome accepted for the consumer travel probe.'
    Remove-Item -LiteralPath $consumerOutcomePath
    & $script -Action Cleanup -SandboxRoot $consumerRoot

    # --- separate optional actual-consumer Echo arrival-snap probe --------------------------------
    function EchoMetadata($version, $flags) {
        return [pscustomobject]@{
            Name=[pscustomobject]@{Name='VGEcho';Version=[Version]$version}
            MainModule=[pscustomobject]@{Types=@([pscustomobject]@{FullName='VGEcho.Plugin';CustomAttributes=@([pscustomobject]@{
                AttributeType=[pscustomobject]@{FullName='BepInEx.BepInDependency'}
                ConstructorArguments=@([pscustomobject]@{Value='vgmodapi'},[pscustomobject]@{Value=$flags})
            })})}
        }
    }
    Assert-EchoAssemblyMetadata (EchoMetadata '0.7.0.0' 2)
    Assert-EchoAssemblyMetadata (EchoMetadata '0.7.0.0' 2) -TravelProbe
    foreach ($metadata in @((EchoMetadata '0.6.0.0' 2), (EchoMetadata '0.7.0.0' 1), (EchoMetadata '0.8.0.0' 2))) {
        $rejected = $false
        try { Assert-EchoAssemblyMetadata $metadata } catch { $rejected = $true }
        Assert $rejected 'Unsupported or hard-dependency Echo metadata accepted.'
    }
    # qa-87: an UNDECODABLE attribute blob (the flags enum could not be resolved, so Cecil yields
    # zero arguments) must never be reported as a wrong consumer shape. The two failures are
    # different problems and must carry different, non-equivalent diagnostics.
    $undecodable = EchoMetadata '0.7.0.0' 2
    $undecodable.MainModule.Types[0].CustomAttributes[0].ConstructorArguments = @()
    $decodeMessage = ''
    try { Assert-EchoAssemblyMetadata $undecodable } catch { $decodeMessage = $_.Exception.Message }
    Assert ($decodeMessage -like '*could not be decoded*') "Undecodable Echo metadata was not reported as a reader failure: '$decodeMessage'"
    Assert ($decodeMessage -like '*BepInEx.dll*') 'Undecodable Echo metadata does not name the reference the reader needs.'
    $hardMessage = ''
    try { Assert-EchoAssemblyMetadata (EchoMetadata '0.7.0.0' 1) } catch { $hardMessage = $_.Exception.Message }
    Assert ($hardMessage -like '*SOFT dependency*') "Hard-dependency Echo metadata was not reported as a shape failure: '$hardMessage'"
    Assert ($hardMessage -ne $decodeMessage) 'A hard dependency and an unreadable blob must not report the same failure.'
    # The bounded reference set the consumer metadata reader is allowed to read, and nothing else.
    $refRoot = Join-Path $work 'consumer-refs'
    Put 'consumer-refs\game\BepInEx\core\marker.txt' 'core'
    Put 'consumer-refs\candidate\marker.txt' 'candidate'
    Put 'consumer-refs\installed\VanguardGalaxy_Data\Managed\marker.txt' 'managed'
    $refDirs = @(Get-ConsumerMetadataReferenceDirs (Join-Path $refRoot 'candidate') $refRoot (Join-Path $refRoot 'installed'))
    Assert ($refDirs.Count -eq 3) "Expected the candidate, sandbox BepInEx core and installed Managed directories; got $($refDirs.Count)."
    Assert ($refDirs -contains (Join-Path $refRoot 'candidate')) 'Consumer metadata references omit the candidate directory.'
    Assert ($refDirs -contains (Join-Path $refRoot 'game\BepInEx\core')) 'Consumer metadata references omit the sandbox BepInEx core.'
    Assert ($refDirs -contains (Join-Path $refRoot 'installed\VanguardGalaxy_Data\Managed')) 'Consumer metadata references omit the installed Managed directory.'
    $missingRefs = @(Get-ConsumerMetadataReferenceDirs (Join-Path $refRoot 'candidate') (Join-Path $refRoot 'absent') (Join-Path $refRoot 'absent'))
    Assert ($missingRefs.Count -eq 1) 'A non-existent reference directory must be dropped, not searched.'
    # OPT-IN integration regression against a REAL built consumer assembly. Host synthetics cannot
    # reproduce a missing enum resolver, which is exactly what refused the candidate in qa-87, so the
    # real read is the only evidence that the bounded resolver fixed it. Default runs stay
    # self-contained: set VG_QUALIFICATION_ECHO_DLL (and optionally VG_QUALIFICATION_GAME_DIR) to
    # enable it. Nothing is copied, nothing is launched, and the candidate is opened read-only.
    $realEcho = $env:VG_QUALIFICATION_ECHO_DLL
    if ($realEcho -and (Test-Path -LiteralPath $realEcho -PathType Leaf)) {
        $realGame = if ($env:VG_QUALIFICATION_GAME_DIR) { $env:VG_QUALIFICATION_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Vanguard Galaxy' }
        Add-Type -Path (Join-Path $realGame 'BepInEx\core\Mono.Cecil.dll')
        $realDirs = @((Split-Path -Parent $realEcho), (Join-Path $realGame 'BepInEx\core'), (Join-Path $realGame 'VanguardGalaxy_Data\Managed'))
        $reader = Read-ConsumerAssembly $realEcho $realDirs
        try {
            # The SOFT flag really survives the read: the enum argument is decoded, not defaulted.
            $realPlugin = @($reader.Assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGEcho.Plugin' })
            $realDeps = @($realPlugin[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' })
            Assert ($realDeps.Count -ge 1) 'The real Echo assembly declares no BepInDependency.'
            $realArgs = $realDeps[0].ConstructorArguments
            Assert ($realArgs.Count -eq 2) "The real Echo BepInDependency decoded $($realArgs.Count) arguments; the resolver did not resolve its flags enum."
            Assert ($realArgs[0].Value -eq 'vgmodapi') 'The real Echo BepInDependency does not name the API.'
            Assert ($realArgs[1].Type.FullName -like '*DependencyFlags*') 'The real Echo BepInDependency flags argument is not the BepInEx enum.'
            Assert ([int]$realArgs[1].Value -eq 2) "The real Echo BepInDependency is not SOFT: flags=$([int]$realArgs[1].Value)."
            Assert-EchoAssemblyMetadata $reader.Assembly -TravelProbe
        } finally { Close-ConsumerAssembly $reader }
        # The exact qa-87 failure, as a regression: without the reference directories the flags enum
        # cannot be decoded, and the reader must say so instead of blaming the consumer's shape.
        $blindReader = Read-ConsumerAssembly $realEcho @((Split-Path -Parent $realEcho))
        $blindMessage = ''
        try { Assert-EchoAssemblyMetadata $blindReader.Assembly } catch { $blindMessage = $_.Exception.Message }
        finally { Close-ConsumerAssembly $blindReader }
        Assert ($blindMessage -like '*could not be decoded*') "A real read without BepInEx references did not report a reader failure: '$blindMessage'"
        Assert ($blindMessage -notlike '*SOFT dependency*') 'An unresolvable reference must not be reported as a wrong consumer shape.'
        Write-Output 'PASS: real consumer metadata integration regression (bounded resolver decodes the SOFT dependency flags enum).'
    }
    # Both consumer probes own the same reused phases; they are refused together at Prepare.
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-both-consumers') -EchoTravelProbe -AnimaTravelProbe -TravelStation -TravelCrossSystem -TravelWormholeFixture @options }
    catch { $rejected = $_.Exception.Message -like '*both own the reused travel phases*' }
    Assert $rejected 'Both consumer travel probes accepted in one run.'
    foreach ($invalid in @(@{TravelStation=$true;TravelCrossSystem=$true;TravelWormholeFixture=$true}, @{TravelStation=$true})) {
        $rejected = $false
        try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-echo-travel') -EchoTravelProbe @invalid @options }
        catch { $rejected = $_.Exception.Message -like '*Echo consumer travel probe requires*' }
        Assert $rejected 'Echo consumer travel probe accepted without its prerequisites.'
    }
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-echo-absent') -Scenario MissingApi -EchoAbsentProbe @options }
    catch { $rejected = $_.Exception.Message -like '*Echo API-absent control requires*' }
    Assert $rejected 'Echo API-absent control accepted without the Echo consumer.'
    $echoRoot = Join-Path $work 'echo-travel-sandbox'
    $sandboxes += $echoRoot
    & $script -Action Prepare -SandboxRoot $echoRoot -TravelStation -TravelCrossSystem -TravelWormholeFixture @options
    $echoProvenancePath = Join-Path $echoRoot 'build-provenance.json'
    $echoProvenance = Get-Content -LiteralPath $echoProvenancePath -Raw | ConvertFrom-Json
    $echoProvenance.echo = $true
    $echoProvenance.echoRevision = 'c' * 40
    $echoProvenance.echoVersion = $EchoTravelProbeVersion
    $echoProvenance.echoTravelProbe = $true
    $echoProvenance.echoTravelBudgetSeconds = $EchoTravelBudgetSeconds
    $echoDll = Join-Path $echoRoot 'game\BepInEx\plugins\VGEcho.dll'
    [IO.File]::WriteAllText($echoDll, 'synthetic-not-executable')
    $echoProvenance.plugins | Add-Member -NotePropertyName 'VGEcho.dll' -NotePropertyValue (Get-FileHash -LiteralPath $echoDll).Hash
    $echoProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $echoProvenancePath
    [IO.File]::WriteAllText((Join-Path $echoRoot 'echo.enabled'), 'echo-v1')
    $echoMarker = Join-Path $echoRoot 'echo-travel.enabled'
    [IO.File]::WriteAllText($echoMarker, 'echo-travel-v1')
    $validEchoConfig = "[Autopilot]`nTimingEnabled = true`nEtaSync = false`nArrivalSnap = true`n"
    $echoConfigPath = Join-Path $echoRoot 'game\BepInEx\config\vgecho.cfg'
    [IO.File]::WriteAllText($echoConfigPath, $validEchoConfig)
    $echoProvenance = Assert-QualificationInputs $echoRoot
    Assert ($echoProvenance.echoTravelProbe -and $echoProvenance.echoTravelBudgetSeconds -eq $EchoTravelBudgetSeconds) 'Prepared Echo selection/budget missing.'
    # ETA-sync ON would let an ETA write masquerade as an arrival snap during the isolated positives.
    foreach ($changed in @($validEchoConfig.Replace('EtaSync = false','EtaSync = true'),
        $validEchoConfig.Replace('ArrivalSnap = true','ArrivalSnap = false'),
        $validEchoConfig.Replace('TimingEnabled = true','TimingEnabled = false'),
        ($validEchoConfig + "EtaSync = false`n"))) {
        [IO.File]::WriteAllText($echoConfigPath, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $echoRoot } catch { $rejected = $true }
        Assert $rejected 'Changed Echo arrival-snap configuration accepted.'
    }
    [IO.File]::WriteAllText($echoConfigPath, $validEchoConfig)
    [IO.File]::WriteAllText($echoMarker, 'changed')
    $rejected = $false
    try { $null = Assert-QualificationInputs $echoRoot } catch { $rejected = $true }
    Assert $rejected 'Changed Echo consumer travel marker accepted.'
    [IO.File]::WriteAllText($echoMarker, 'echo-travel-v1')
    $echoProvenanceText = [IO.File]::ReadAllText($echoProvenancePath)
    foreach ($edit in @(@{Pattern='"echoTravelBudgetSeconds": *\d+'; Value='"echoTravelBudgetSeconds": 60'; Message='Edited Echo budget reservation accepted.'},
        @{Pattern='"echoVersion": *"[^"]*"'; Value='"echoVersion": "0.6.0.0"'; Message='Echo probe accepted another pinned consumer version.'},
        @{Pattern='"echoRevision": *"[^"]*"'; Value='"echoRevision": "not-a-revision"'; Message='Echo selection accepted an invalid source revision.'},
        @{Pattern='"travelWormholeFixture": *true'; Value='"travelWormholeFixture": false'; Message='Echo probe accepted a run without the wormhole fixture selection.'})) {
        [IO.File]::WriteAllText($echoProvenancePath, ($echoProvenanceText -replace $edit.Pattern, $edit.Value))
        $rejected = $false
        try { $null = Assert-QualificationInputs $echoRoot } catch { $rejected = $true }
        Assert $rejected $edit.Message
    }
    # A DELETED version pin must fail exactly like a wrong one.
    $withoutEchoVersion = Get-Content -LiteralPath $echoProvenancePath -Raw | ConvertFrom-Json
    $withoutEchoVersion.PSObject.Properties.Remove('echoVersion')
    $withoutEchoVersion | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $echoProvenancePath
    $rejected = $false
    try { $null = Assert-QualificationInputs $echoRoot } catch { $rejected = $true }
    Assert $rejected 'Echo probe accepted provenance with the consumer version pin removed.'
    [IO.File]::WriteAllText($echoProvenancePath, $echoProvenanceText)
    $echoProvenance = Assert-QualificationInputs $echoRoot
    $echoMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelCrossSystemBudgetSeconds + $EchoTravelBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $echoRoot -TimeoutSeconds ($echoMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $echoMinimum*" }
    Assert $rejected 'Echo run accepted a lifetime one second below the derived minimum.'
    Assert (!(Test-Path -LiteralPath (Join-Path $echoRoot 'run-started.txt'))) 'The launcher started the game despite an insufficient Echo lifetime.'
    # Synthetic receipts only.
    $echoSession = [Guid]::NewGuid().ToString()
    function EchoEvent($surface, $sequence, $caseLabel, $session) { return ("" + $sequence + "`t" + $surface + "`t" + $caseLabel + "`t" + $session + "`t`tRouteCompleted`tInSystem`tsystem-1:a`tsystem-1:b`tsystem-1:b`t1.000`t") }
    function EchoRow($case, $status, $session, $evidence, $detail) { return ($case + "`tdescription`t" + $status + "`tidentity`t" + $session + "`t`t" + $evidence + "`t" + $detail) }
    $echoControls = 'sandboxConfig=[TimingEnabled=true,ArrivalSnap=true,EtaSync=false]; idleTimerSeed=300s x6; autopilotEngagements=2; suppressedFindActivityBodies=4; subscriptionReorderings=2'
    function EchoSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$EchoTravelPhase", "budgetSeconds=$EchoTravelBudgetSeconds",
            ("required=" + ($EchoTravelRequiredCases -join ',')),
            ("required-subcases=" + ($EchoTravelRequiredSubcaseRows -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $EchoTravelRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        foreach ($subcase in $EchoTravelRequiredSubcaseRows) {
            $matched = @($records | Where-Object { $_[0] -eq $subcase })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-subcase $subcase=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    $echoOrderedResult = @('PASS') + $EchoTravelReusedPhaseScenarios + @($EchoTravelPhase)
    function WriteEchoOutputs($rows, $events, $summary, $result) {
        [IO.File]::WriteAllLines((Join-Path $echoRoot 'echo-travel-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $echoRoot 'echo-travel-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $echoRoot 'echo-travel.txt'), [string[]]$summary)
        [IO.File]::WriteAllLines((Join-Path $echoRoot 'result.txt'), [string[]]$result)
    }
    function AssertEchoRejected($rows, $events, $summary, $result, $message) {
        WriteEchoOutputs $rows $events $summary $result
        $rejected = $false
        try { Assert-EchoTravelReceipt $echoRoot } catch { $rejected = $true }
        Assert $rejected $message
    }
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $echoRoot ([pscustomobject]@{ echoTravelProbe = $true }) } catch { $rejected = $true }
    Assert $rejected 'Missing Echo consumer receipt accepted while the probe was selected.'
    $echoRows = @()
    $echoEvents = @()
    $sequence = 0
    foreach ($case in @($EchoTravelRequiredCases)) {
        $sequence++
        $echoRows += (EchoRow $case 'passed' $echoSession ("travel:" + $sequence) 'detail')
        $echoEvents += (EchoEvent 'travel' $sequence $case $echoSession)
    }
    $sequence++
    $echoRows += (EchoRow $EchoTravelRequiredSubcaseRows[0] 'passed' $echoSession ("travel:" + $sequence) $echoControls)
    $echoEvents += (EchoEvent 'travel' $sequence $EchoTravelRequiredSubcaseRows[0] $echoSession)
    WriteEchoOutputs $echoRows $echoEvents (EchoSummary $echoRows 'PASS') $echoOrderedResult
    Assert-EchoTravelReceipt $echoRoot
    Assert-PersistenceProbeReceipt $echoRoot ([pscustomobject]@{ echoTravelProbe = $true })
    $echoSkipped = @($EchoTravelRequiredCases | ForEach-Object { EchoRow $_ 'not-run' $echoSession '' 'detail' }) + @($echoRows[-1])
    AssertEchoRejected $echoSkipped $echoEvents (EchoSummary $echoSkipped 'PASS') $echoOrderedResult 'All-skipped Echo coverage accepted as PASS.'
    $echoFailed = @($echoRows[0..5]) + @(EchoRow $EchoTravelRequiredCases[-1] 'failed' $echoSession 'travel:7' 'detail') + @($echoRows[-1])
    AssertEchoRejected $echoFailed $echoEvents (EchoSummary $echoFailed 'PASS') $echoOrderedResult 'Claimed Echo PASS with a failed row accepted.'
    $echoNoControls = @($echoRows[0..6])
    AssertEchoRejected $echoNoControls $echoEvents (EchoSummary $echoNoControls 'PASS') $echoOrderedResult 'Echo receipt without the mandatory declared-controls row accepted.'
    $echoEmptyControls = @($echoRows[0..6]) + @(EchoRow $EchoTravelRequiredSubcaseRows[0] 'passed' $echoSession 'travel:8' 'nothing declared')
    AssertEchoRejected $echoEmptyControls $echoEvents (EchoSummary $echoEmptyControls 'PASS') $echoOrderedResult 'Echo controls row without the declared controls accepted.'
    $echoForeign = @($echoEvents | ForEach-Object { $_ -replace [regex]::Escape($echoSession), ([Guid]::NewGuid().ToString()) })
    AssertEchoRejected $echoRows $echoForeign (EchoSummary $echoRows 'PASS') $echoOrderedResult 'Echo identities absent from the event trace accepted.'
    AssertEchoRejected $echoRows $echoEvents (EchoSummary $echoRows 'FAIL') $echoOrderedResult 'Failed Echo attempt summary accepted.'
    $echoForeignPhase = @((EchoSummary $echoRows 'PASS') | ForEach-Object { if ($_ -like 'phase=*') { "phase=$TravelCrossSystemPhase" } else { $_ } })
    AssertEchoRejected $echoRows $echoEvents $echoForeignPhase $echoOrderedResult 'Echo receipt declaring a reused travel phase accepted.'
    $echoOverBudget = @((EchoSummary $echoRows 'PASS') | ForEach-Object { if ($_ -like 'budgetSeconds=*') { "budgetSeconds=$($EchoTravelBudgetSeconds + 1)" } else { $_ } })
    AssertEchoRejected $echoRows $echoEvents $echoOverBudget $echoOrderedResult 'Echo budget above the launcher reservation accepted.'
    $echoLate = @('PASS', $EchoTravelReusedPhaseScenarios[0], $EchoTravelPhase, $EchoTravelReusedPhaseScenarios[1])
    AssertEchoRejected $echoRows $echoEvents (EchoSummary $echoRows 'PASS') $echoLate 'Echo probe completing before a reused phase accepted.'
    $echoMissingReuse = @('PASS', $EchoTravelReusedPhaseScenarios[0], $EchoTravelPhase)
    AssertEchoRejected $echoRows $echoEvents (EchoSummary $echoRows 'PASS') $echoMissingReuse 'Echo probe accepted without the reused cross-system phase.'
    WriteEchoOutputs $echoRows $echoEvents (EchoSummary $echoRows 'PASS') $echoOrderedResult
    Assert-EchoTravelReceipt $echoRoot
    & $script -Action Cleanup -SandboxRoot $echoRoot
    # The API-absent control is its own MissingApi run with its own receipt shape.
    $echoAbsentRoot = Join-Path $work 'echo-absent-sandbox'
    $sandboxes += $echoAbsentRoot
    & $script -Action Prepare -SandboxRoot $echoAbsentRoot -Scenario MissingApi @options
    $absentProvenance = [pscustomobject]@{ echoAbsentProbe = $true; vanillaLoadControl = $false }
    $absentReceipt = Join-Path $echoAbsentRoot 'echo-absent.txt'
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $echoAbsentRoot $absentProvenance } catch { $rejected = $true }
    Assert $rejected 'Missing Echo API-absent receipt accepted.'
    [IO.File]::WriteAllLines($absentReceipt, [string[]]@('PASS','echoVersion=0.7.0','arrivalSnap=unbound','ownedPatches=6','idleUpdateInvocations=0','gameplayLoadControl=False'))
    Assert-PersistenceProbeReceipt $echoAbsentRoot $absentProvenance
    foreach ($broken in @(
        @('FAIL','echoVersion=0.7.0','arrivalSnap=unbound','idleUpdateInvocations=0','gameplayLoadControl=False'),
        @('PASS','echoVersion=0.7.0','arrivalSnap=bound','idleUpdateInvocations=0','gameplayLoadControl=False'),
        @('PASS','echoVersion=0.6.0','arrivalSnap=unbound','idleUpdateInvocations=0','gameplayLoadControl=False'),
        @('PASS','echoVersion=0.7.0','arrivalSnap=unbound','gameplayLoadControl=False'),
        @('PASS','echoVersion=0.7.0','arrivalSnap=unbound','idleUpdateInvocations=12','gameplayLoadControl=False'))) {
        [IO.File]::WriteAllLines($absentReceipt, [string[]]$broken)
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $echoAbsentRoot $absentProvenance } catch { $rejected = $true }
        Assert $rejected 'Broken Echo API-absent receipt accepted.'
    }
    # With the gameplay load control selected, an unexercised hook is refused.
    $absentWithGameplay = [pscustomobject]@{ echoAbsentProbe = $true; vanillaLoadControl = $true }
    [IO.File]::WriteAllLines($absentReceipt, [string[]]@('PASS','echoVersion=0.7.0','arrivalSnap=unbound','idleUpdateInvocations=0','gameplayLoadControl=True'))
    $rejected = $false
    try { Assert-PersistenceProbeReceipt $echoAbsentRoot $absentWithGameplay } catch { $rejected = $true }
    Assert $rejected 'Echo API-absent control accepted without an observed native hook invocation.'
    [IO.File]::WriteAllLines($absentReceipt, [string[]]@('PASS','echoVersion=0.7.0','arrivalSnap=unbound','idleUpdateInvocations=42','gameplayLoadControl=True'))
    Assert-PersistenceProbeReceipt $echoAbsentRoot $absentWithGameplay
    & $script -Action Cleanup -SandboxRoot $echoAbsentRoot

    # --- archived TravelJournal comparison (sandbox-only, archive unchanged) ----------------------
    function JournalMetadata($assemblyVersion, $informational, $pluginVersion) {
        return [pscustomobject]@{
            Name=[pscustomobject]@{Name='VGTravelJournal';Version=[Version]$assemblyVersion}
            CustomAttributes=@([pscustomobject]@{
                AttributeType=[pscustomobject]@{FullName='System.Reflection.AssemblyInformationalVersionAttribute'}
                ConstructorArguments=@([pscustomobject]@{Value=$informational})
            })
            MainModule=[pscustomobject]@{Types=@([pscustomobject]@{FullName='VGTravelJournal.Plugin';CustomAttributes=@([pscustomobject]@{
                AttributeType=[pscustomobject]@{FullName='BepInEx.BepInPlugin'}
                ConstructorArguments=@([pscustomobject]@{Value='vgtraveljournal'},[pscustomobject]@{Value='Vanguard Galaxy Travel Journal'},[pscustomobject]@{Value=$pluginVersion})
            })})}
        }
    }
    $journalRevision = '818d8b7e13a7841703bdd99e173e7dd993f6895c'
    Assert-TravelJournalAssemblyMetadata (JournalMetadata '0.1.0.0' ('0.1.0+' + $journalRevision) '0.2.0') $journalRevision
    foreach ($bad in @(
        @{Meta=(JournalMetadata '0.2.0.0' ('0.1.0+' + $journalRevision) '0.2.0'); Why='wrong assembly version accepted'},
        @{Meta=(JournalMetadata '0.1.0.0' '0.1.0' '0.2.0'); Why='missing source revision accepted'},
        @{Meta=(JournalMetadata '0.1.0.0' ('0.1.0+' + ('a' * 40)) '0.2.0'); Why='foreign source revision accepted'},
        @{Meta=(JournalMetadata '0.1.0.0' ('0.1.0+' + $journalRevision) '0.3.0'); Why='wrong plugin version accepted'})) {
        $rejected = $false
        try { Assert-TravelJournalAssemblyMetadata $bad.Meta $journalRevision } catch { $rejected = $true }
        Assert $rejected ('Archived TravelJournal metadata: ' + $bad.Why)
    }
    # The comparison and either consumer travel probe both own the reused phases.
    foreach ($conflict in @(@{AnimaTravelProbe=$true}, @{EchoTravelProbe=$true})) {
        $rejected = $false
        try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal-conflict') -TravelJournalComparison -TravelStation -TravelCrossSystem -TravelWormholeFixture @conflict @options }
        catch { $rejected = $_.Exception.Message -like '*both own the reused travel phases*' }
        Assert $rejected ('Archived-journal comparison accepted beside ' + ($conflict.Keys -join ','))
    }
    # The committed pins are the ONLY accepted archive; a caller can repeat them but never widen them.
    Assert ($TravelJournalPinnedRevision -eq $journalRevision) 'The committed archive revision pin changed.'
    Assert ($TravelJournalPinnedSha256 -eq 'f253c3eefb967af7a1472dfb48bd926bff14b84b7219208facdc594387b1fdad') 'The committed archive binary pin changed.'
    Assert-TravelJournalPins $TravelJournalPinnedRevision $TravelJournalPinnedSha256 $TravelJournalPinnedSha256.ToUpperInvariant()
    foreach ($bad in @(
        @{Revision=('c' * 40); Sha=$TravelJournalPinnedSha256; Actual=$TravelJournalPinnedSha256; Why='a foreign source revision pin'},
        @{Revision=$TravelJournalPinnedRevision; Sha=('d' * 64); Actual=('d' * 64); Why='a caller-supplied hash other than the committed pin'},
        @{Revision=$TravelJournalPinnedRevision; Sha=$TravelJournalPinnedSha256; Actual=('e' * 64); Why='a binary whose bytes are not the committed pin'},
        @{Revision=$TravelJournalPinnedRevision; Sha=('d' * 64); Actual=('d' * 64); Why='a candidate matching a different hash pin'})) {
        $rejected = $false
        try { Assert-TravelJournalPins $bad.Revision $bad.Sha $bad.Actual } catch { $rejected = $true }
        Assert $rejected ('Archived TravelJournal pin: accepted ' + $bad.Why + '.')
    }
    # Version metadata alone can never authorise a different binary: the same declared identity with
    # foreign bytes is still refused.
    Assert-TravelJournalAssemblyMetadata (JournalMetadata '0.1.0.0' ('0.1.0+' + $journalRevision) '0.2.0') $journalRevision
    $rejected = $false
    try { Assert-TravelJournalPins $journalRevision $TravelJournalPinnedSha256 ('f' * 64) } catch { $rejected = $true }
    Assert $rejected 'Archived TravelJournal accepted matching metadata with unpinned bytes.'
    # ARCHIVE COMPARISON ONLY: no consumer plugin is prepared beside the archive, not even a
    # non-travel selection. Prepare refuses it before creating anything, so Prepare and the Run-time
    # provenance check can no longer disagree.
    foreach ($consumer in @(@{AnimaBin=$build; AnimaRevision=('a' * 40)}, @{EchoBin=$build; EchoRevision=('b' * 40)})) {
        $consumerSandbox = Join-Path $work ('invalid-journal-consumer-' + ($consumer.Keys | Sort-Object)[0])
        $rejected = $false
        try { & $script -Action Prepare -SandboxRoot $consumerSandbox -TravelJournalComparison -TravelJournalBin $build -TravelJournalRevision $TravelJournalPinnedRevision -TravelJournalSha256 $TravelJournalPinnedSha256 -TravelStation -TravelCrossSystem -TravelWormholeFixture @consumer @options }
        catch { $rejected = $_.Exception.Message -like '*carries the archive alone*' }
        Assert $rejected ('Archived-journal comparison accepted beside ' + ($consumer.Keys -join ','))
        Assert (!(Test-Path -LiteralPath $consumerSandbox)) 'The refused archive/consumer Prepare created sandbox files.'
    }
    # The archive patches the game, so it is never installed without the comparison that owns it.
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal-ridealong') -TravelJournalBin $build -TravelJournalRevision $TravelJournalPinnedRevision -TravelJournalSha256 $TravelJournalPinnedSha256 -TravelStation -TravelCrossSystem -TravelWormholeFixture @options }
    catch { $rejected = $_.Exception.Message -like '*never installed as a passive ridealong*' }
    Assert $rejected 'The archived TravelJournal was accepted without the comparison.'
    foreach ($orphan in @(@{TravelJournalRevision=$TravelJournalPinnedRevision}, @{TravelJournalSha256=$TravelJournalPinnedSha256})) {
        $rejected = $false
        try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal-orphan-pin') -TravelJournalComparison -TravelStation -TravelCrossSystem -TravelWormholeFixture @orphan @options }
        catch { $rejected = $_.Exception.Message -like '*pins were supplied without the archived binary*' }
        Assert $rejected ('Archived TravelJournal pins accepted without the binary: ' + ($orphan.Keys -join ','))
    }
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal-pins') -TravelJournalBin $build -TravelJournalRevision 'nope' -TravelJournalSha256 'nope' @options }
    catch { $rejected = $_.Exception.Message -like '*exact source revision and binary SHA-256*' }
    Assert $rejected 'Archived TravelJournal accepted without its revision/hash pins.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $work 'invalid-journal-prereq') -TravelJournalComparison -TravelStation @options }
    catch { $rejected = $_.Exception.Message -like '*Archived-journal comparison requires*' }
    Assert $rejected 'Archived-journal comparison accepted without its prerequisites.'
    $journalRoot = Join-Path $work 'travel-journal-sandbox'
    $sandboxes += $journalRoot
    & $script -Action Prepare -SandboxRoot $journalRoot -TravelStation -TravelCrossSystem -TravelWormholeFixture @options
    # Synthesize the prepared archive selection this harness cannot build without the real prebuilt;
    # every rule exercised below is still the launcher's own.
    $journalDll = Join-Path $journalRoot 'game\BepInEx\plugins\VGTravelJournal.dll'
    [IO.File]::WriteAllText($journalDll, 'synthetic-not-executable')
    # The pinned archive bytes cannot be synthesized - that is exactly what the pin is for - so the
    # remaining SANDBOX rules run against a narrow test double that reports the committed hash for
    # this one synthetic placeholder. The pin rule itself is exercised directly above, against the
    # real committed constants, and the double is removed again as soon as this block ends.
    function Get-FileHash {
        param([string]$LiteralPath, [string]$Algorithm = 'SHA256')
        if ((Split-Path -Leaf $LiteralPath) -eq 'VGTravelJournal.dll' -and
            [IO.File]::ReadAllText($LiteralPath) -eq 'synthetic-not-executable') {
            return [pscustomobject]@{ Algorithm = $Algorithm; Hash = $TravelJournalPinnedSha256.ToUpperInvariant(); Path = $LiteralPath }
        }
        return Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
    }
    $journalHash = (Get-FileHash -LiteralPath $journalDll -Algorithm SHA256).Hash
    Assert ($journalHash -eq $TravelJournalPinnedSha256.ToUpperInvariant()) 'The archived-journal sandbox double did not report the committed pin.'
    $journalProvenancePath = Join-Path $journalRoot 'build-provenance.json'
    $journalProvenance = Get-Content -LiteralPath $journalProvenancePath -Raw | ConvertFrom-Json
    $journalProvenance.travelJournal = $true
    $journalProvenance.travelJournalRevision = $TravelJournalPinnedRevision
    $journalProvenance.travelJournalSha256 = $journalHash
    $journalProvenance.travelJournalVersion = $TravelJournalAssemblyVersion
    $journalProvenance.travelJournalComparison = $true
    $journalProvenance.travelJournalBudgetSeconds = $TravelJournalBudgetSeconds
    $journalProvenance.plugins | Add-Member -NotePropertyName 'VGTravelJournal.dll' -NotePropertyValue $journalHash
    $journalProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journalProvenancePath
    [IO.File]::WriteAllText((Join-Path $journalRoot 'travel-journal-revision.txt'), $journalRevision)
    [IO.File]::WriteAllText((Join-Path $journalRoot 'travel-journal.enabled'), 'travel-journal-v1')
    $validJournalConfig = "[Journal]`nVerbose = true`nMaxEvents = 0`n"
    $journalConfigPath = Join-Path $journalRoot 'game\BepInEx\config\vgtraveljournal.cfg'
    [IO.File]::WriteAllText($journalConfigPath, $validJournalConfig)
    $journalProvenance = Assert-QualificationInputs $journalRoot
    Assert ($journalProvenance.travelJournalComparison -and $journalProvenance.travelJournalBudgetSeconds -eq $TravelJournalBudgetSeconds) 'Prepared archive selection/budget missing.'
    # BepInEx 5.4 rewrites a plugin's config on its first Bind (SaveOnConfigSet), adding a header,
    # '##' descriptions and its own spacing. That rewrite is expected and must still pass, because
    # the SEMANTICS are what is pinned, not the bytes.
    $bepJournalConfig = "## Settings file was created by plugin Vanguard Galaxy Travel Journal v0.2.0`r`n## Plugin GUID: vgtraveljournal`r`n`r`n[Journal]`r`n`r`n## Log every recorded travel event.`r`n# Setting type: Boolean`r`n# Default value: false`r`nVerbose = true`r`n`r`n## Maximum retained events; 0 keeps every event.`r`n# Setting type: Int32`r`n# Default value: 500`r`nMaxEvents = 0`r`n"
    [IO.File]::WriteAllText($journalConfigPath, $bepJournalConfig)
    $null = Assert-QualificationInputs $journalRoot
    # MaxEvents must stay unbounded so the archived FIFO can never silently evict a compared row,
    # and an ambiguous (duplicated or conflicting) entry is never resolved silently.
    foreach ($changed in @($validJournalConfig.Replace('MaxEvents = 0','MaxEvents = 50000'),
        $validJournalConfig.Replace('Verbose = true','Verbose = false'),
        ($validJournalConfig + "MaxEvents = 0`n"),
        ($validJournalConfig + "MaxEvents = 500`n"),
        ($bepJournalConfig + "MaxEvents = 500`r`n"),
        $bepJournalConfig.Replace('MaxEvents = 0','MaxEvents = 500'),
        $bepJournalConfig.Replace('Verbose = true','Verbose = false'),
        $validJournalConfig.Replace('MaxEvents = 0',''),
        $validJournalConfig.Replace('[Journal]','[Other]'))) {
        [IO.File]::WriteAllText($journalConfigPath, $changed)
        $rejected = $false
        try { $null = Assert-QualificationInputs $journalRoot } catch { $rejected = $true }
        Assert $rejected 'Changed archived journal configuration accepted.'
    }
    # A commented-out value is a comment, not a binding.
    [IO.File]::WriteAllText($journalConfigPath, $validJournalConfig.Replace('MaxEvents = 0','## MaxEvents = 0'))
    $rejected = $false
    try { $null = Assert-QualificationInputs $journalRoot } catch { $rejected = $true }
    Assert $rejected 'A commented-out archived journal binding accepted.'
    [IO.File]::WriteAllText($journalConfigPath, $validJournalConfig)
    # The deployed binary must remain exactly the pinned build, and the PDB must never be deployed.
    [IO.File]::WriteAllText($journalDll, 'tampered')
    $rejected = $false
    try { $null = Assert-QualificationInputs $journalRoot } catch { $rejected = $true }
    Assert $rejected 'A prepared archive binary other than the pinned build was accepted.'
    [IO.File]::WriteAllText($journalDll, 'synthetic-not-executable')
    [IO.File]::WriteAllText((Join-Path $journalRoot 'game\BepInEx\plugins\VGTravelJournal.pdb'), 'pdb')
    $rejected = $false
    try { $null = Assert-QualificationInputs $journalRoot } catch { $rejected = $true }
    Assert $rejected 'A deployed archive PDB was accepted.'
    Remove-Item -LiteralPath (Join-Path $journalRoot 'game\BepInEx\plugins\VGTravelJournal.pdb')
    $journalProvenanceText = [IO.File]::ReadAllText($journalProvenancePath)
    foreach ($edit in @(@{Pattern='"travelJournalBudgetSeconds": *\d+'; Value='"travelJournalBudgetSeconds": 60'; Message='Edited archive budget reservation accepted.'},
        @{Pattern='"travelJournalRevision": *"[^"]*"'; Value=('"travelJournalRevision": "' + ('b' * 40) + '"'); Message='Mismatched archive revision pin accepted.'},
        @{Pattern='"travelWormholeFixture": *true'; Value='"travelWormholeFixture": false'; Message='Archive comparison accepted without the wormhole fixture selection.'})) {
        [IO.File]::WriteAllText($journalProvenancePath, ($journalProvenanceText -replace $edit.Pattern, $edit.Value))
        $rejected = $false
        try { $null = Assert-QualificationInputs $journalRoot } catch { $rejected = $true }
        Assert $rejected $edit.Message
    }
    [IO.File]::WriteAllText($journalProvenancePath, $journalProvenanceText)
    $journalProvenance = Assert-QualificationInputs $journalRoot
    $journalMinimum = $QualificationBaseTimeoutSeconds + $TravelStationBudgetSeconds + $TravelCrossSystemBudgetSeconds + $TravelJournalBudgetSeconds
    $rejected = $false
    try { & $script -Action Run -SandboxRoot $journalRoot -TimeoutSeconds ($journalMinimum - 1) @options }
    catch { $rejected = $_.Exception.Message -like "*at least $journalMinimum*" }
    Assert $rejected 'Archive comparison run accepted a lifetime one second below the derived minimum.'
    # Synthetic receipts only.
    $journalSession = [Guid]::NewGuid().ToString()
    function JournalEvent($surface, $sequence, $session) { return ("" + $sequence + "`t" + $surface + "`t case`t" + $session + "`t`tArrived`tInSystem`t`t`t`t1.000`t") }
    function JournalRow($case, $status, $session, $evidence, $detail) { return ($case + "`tdescription`t" + $status + "`tidentity`t" + $session + "`t`t" + $evidence + "`t" + $detail) }
    function JournalSummary($rows, $first) {
        $records = @($rows | ForEach-Object { ,($_ -split "`t") })
        $passed = @($records | Where-Object { $_[2] -eq 'passed' }).Count
        $failed = @($records | Where-Object { $_[2] -eq 'failed' }).Count
        $notRun = @($records | Where-Object { $_[2] -eq 'not-run' }).Count
        $lines = @($first, "phase=$TravelJournalPhase", "budgetSeconds=$TravelJournalBudgetSeconds",
            ("required=" + ($TravelJournalRequiredCases -join ',')),
            ("compatible-pairs=" + ($TravelJournalCompatiblePairCases -join ',')),
            ("driven-discrepancies=" + ($TravelJournalDrivenDiscrepancyCases -join ',')),
            ("rows=" + $records.Count + " passed=$passed failed=$failed notRun=$notRun"))
        foreach ($case in $TravelJournalRequiredCases) {
            $matched = @($records | Where-Object { $_[0] -eq $case })
            $state = if ($matched.Count -eq 1) { $matched[0][2] } elseif ($matched.Count -eq 0) { 'absent' } else { 'duplicated' }
            $lines += "required-case $case=$state"
        }
        return @($lines + @('optional-not-run=', 'fault=none', 'result=phase satisfied'))
    }
    $journalResult = @('PASS') + $TravelJournalReusedPhaseScenarios + @($TravelJournalPhase)
    function WriteJournalOutputs($rows, $events, $summary, $result) {
        [IO.File]::WriteAllLines((Join-Path $journalRoot 'travel-journal-receipt.tsv'), [string[]]@(($TravelStationReceiptHeader -join "`t")) + [string[]]$rows)
        [IO.File]::WriteAllLines((Join-Path $journalRoot 'travel-journal-events.tsv'), [string[]]@(($TravelStationEventHeader -join "`t")) + [string[]]$events)
        [IO.File]::WriteAllLines((Join-Path $journalRoot 'travel-journal.txt'), [string[]]$summary)
        [IO.File]::WriteAllLines((Join-Path $journalRoot 'result.txt'), [string[]]$result)
    }
    function AssertJournalRejected($rows, $events, $summary, $result, $message) {
        WriteJournalOutputs $rows $events $summary $result
        $rejected = $false
        try { Assert-TravelJournalReceipt $journalRoot } catch { $rejected = $true }
        Assert $rejected $message
    }
    foreach ($slot in @('qa-journal-in-system','qa-journal-in-flight','qa-journal-wormhole')) {
        [IO.File]::WriteAllText((Join-Path $journalRoot ("travel-journal-" + $slot + ".json")), '{"version":2,"events":[]}')
    }
    $journalRows = @()
    $journalEvents = @()
    $sequence = 0
    foreach ($case in $TravelJournalRequiredCases) {
        $sequence++
        $detail = switch ($case) {
            { $TravelJournalCompatiblePairCases -contains $_ } { 'comparison=compatible; legacy:0,1' }
            'jumpgate-prefix-lead' { 'comparison=legacy-prefix-requested; legacy:2' }
            'wormhole-transit-gap' { 'comparison=legacy-gap; none for the arrived system' }
            'station-interior-vs-physical' { 'comparison=legacy-timing; outcome=InteriorPrecedesPhysical' }
            'api-dwell-anchored' { 'largestDwellSeconds=42.5; toleranceSeconds=0 (exact); anchorGameSeconds=100.25; departureGameSeconds=142.75; dwellSeconds=42.5' }
            default { 'detail' }
        }
        $journalRows += (JournalRow $case 'passed' $journalSession ("travel:" + $sequence) $detail)
        $journalEvents += (JournalEvent 'travel' $sequence $journalSession)
    }
    WriteJournalOutputs $journalRows $journalEvents (JournalSummary $journalRows 'PASS') $journalResult
    Assert-TravelJournalReceipt $journalRoot
    Assert-PersistenceProbeReceipt $journalRoot ([pscustomobject]@{ travelJournalComparison = $true })
    # A fabricated pass without a compatible pair, without a driven discrepancy, without the legacy
    # row indices, without a positive dwell or without its evidence copies is refused.
    foreach ($mutation in @(
        @{Case='in-system-arrival-compatible'; Detail='comparison=legacy-gap; legacy:0'; Message='Compatible-pair case recorded as a gap accepted.'},
        @{Case='in-system-arrival-compatible'; Detail='comparison=compatible'; Message='Compatible pair without legacy indices accepted.'},
        @{Case='wormhole-transit-gap'; Detail='comparison=compatible'; Message='Driven discrepancy recorded as compatible accepted.'},
        @{Case='api-dwell-anchored'; Detail='largestDwellSeconds=0; anchorGameSeconds=100.25; departureGameSeconds=100.25; dwellSeconds=0'; Message='Dwell case without a positive anchored dwell accepted.'},
        @{Case='api-dwell-anchored'; Detail='largestDwellSeconds=42.5'; Message='Dwell case without its anchor/departure times accepted.'},
        @{Case='api-dwell-anchored'; Detail='no measurement'; Message='Dwell case without a published measurement accepted.'})) {
        $mutated = @($journalRows | ForEach-Object {
            if (($_ -split "`t")[0] -eq $mutation.Case) { JournalRow $mutation.Case 'passed' $journalSession 'travel:1' $mutation.Detail } else { $_ }
        })
        AssertJournalRejected $mutated $journalEvents (JournalSummary $mutated 'PASS') $journalResult $mutation.Message
    }
    # Round-trip formatting can be exponential; a tiny but positive dwell is still a positive dwell.
    $journalExponent = @($journalRows | ForEach-Object {
        if (($_ -split "`t")[0] -eq 'api-dwell-anchored') {
            JournalRow 'api-dwell-anchored' 'passed' $journalSession 'travel:1' 'largestDwellSeconds=1E-09; anchorGameSeconds=100.25; departureGameSeconds=100.250000001; dwellSeconds=1E-09'
        } else { $_ }
    })
    WriteJournalOutputs $journalExponent $journalEvents (JournalSummary $journalExponent 'PASS') $journalResult
    Assert-TravelJournalReceipt $journalRoot
    WriteJournalOutputs $journalRows $journalEvents (JournalSummary $journalRows 'PASS') $journalResult
    $journalMissing = @($journalRows | Where-Object { ($_ -split "`t")[0] -ne 'jumpgate-prefix-lead' })
    AssertJournalRejected $journalMissing $journalEvents (JournalSummary $journalMissing 'PASS') $journalResult 'Missing mandatory archive case accepted.'
    $journalDuplicated = $journalRows + @($journalRows[0])
    AssertJournalRejected $journalDuplicated $journalEvents (JournalSummary $journalDuplicated 'PASS') $journalResult 'Duplicated archive case accepted.'
    $journalForeign = @($journalEvents | ForEach-Object { $_ -replace [regex]::Escape($journalSession), ([Guid]::NewGuid().ToString()) })
    AssertJournalRejected $journalRows $journalForeign (JournalSummary $journalRows 'PASS') $journalResult 'Archive identities absent from the event trace accepted.'
    AssertJournalRejected $journalRows $journalEvents (JournalSummary $journalRows 'FAIL') $journalResult 'Failed archive attempt summary accepted.'
    $journalLate = @('PASS', $TravelJournalReusedPhaseScenarios[0], $TravelJournalPhase, $TravelJournalReusedPhaseScenarios[1])
    AssertJournalRejected $journalRows $journalEvents (JournalSummary $journalRows 'PASS') $journalLate 'Archive comparison completing before a reused phase accepted.'
    WriteJournalOutputs $journalRows $journalEvents (JournalSummary $journalRows 'PASS') $journalResult
    Remove-Item -LiteralPath (Join-Path $journalRoot 'travel-journal-qa-journal-in-flight.json')
    $rejected = $false
    try { Assert-TravelJournalReceipt $journalRoot } catch { $rejected = $true }
    Assert $rejected 'Archive receipt without its private evidence copy accepted.'
    [IO.File]::WriteAllText((Join-Path $journalRoot 'travel-journal-qa-journal-in-flight.json'), '{"version":2,"events":[]}')
    Assert-TravelJournalReceipt $journalRoot
    # POST-QUIT location audit: the archived plugin still flushes when the game exits, so the final
    # file evidence is taken after the owned process ended, over explicitly named roots only.
    $journalAuditRoots = Get-TravelJournalAuditRoots $journalRoot
    $journalSaves = Join-Path $journalRoot 'Saves'
    [IO.File]::WriteAllText((Join-Path $journalSaves 'fixture-a.save.vgtraveljournal.json'), '{"version":2,"events":[]}')
    $journalFilesBefore = Get-TravelJournalFiles $journalAuditRoots
    # The prepared plugin binary and its config carry the archive's name too; both are snapshotted.
    Assert ($journalFilesBefore.Count -eq 3) 'The pre-run archived journal snapshot missed a prepared input or its own saves root.'
    [IO.File]::WriteAllText((Join-Path $journalSaves 'qa-journal-in-system.save.vgtraveljournal.json'), '{"version":2,"events":[]}')
    [IO.File]::WriteAllText((Join-Path $journalSaves 'fixture-a.save.vgtraveljournal.json'), '{"version":2,"events":[{}]}')
    Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore
    $journalAudit = Get-Content -LiteralPath (Join-Path $journalRoot 'travel-journal-postquit-audit.txt')
    Assert (@($journalAudit | Where-Object { $_ -like "file`tqa-journal-in-system.save.vgtraveljournal.json`tcreated*" }).Count -eq 1) 'The post-quit audit did not record the created sidecar.'
    Assert (@($journalAudit | Where-Object { $_ -like "file`tfixture-a.save.vgtraveljournal.json`trewritten*" }).Count -eq 1) 'The post-quit audit did not record the rewritten sidecar.'
    Assert (@($journalAudit | Where-Object { $_ -like 'scope=after the owned process exited*' }).Count -eq 1) 'The post-quit audit did not bound its own scope.'
    # A journal file outside the saves root, or one with an unexpected name, is refused after quit.
    foreach ($stray in @(@{Path=(Join-Path $journalRoot 'game\BepInEx\plugins\leftover.vgtraveljournal.json'); Why='outside the sandbox saves'},
        @{Path=(Join-Path $journalSaves 'qa-journal.vgtraveljournal.bak'); Why='with an unexpected name'})) {
        [IO.File]::WriteAllText($stray.Path, 'stray')
        $rejected = $false
        try { Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore } catch { $rejected = $true }
        Assert $rejected ('A post-quit archived journal file ' + $stray.Why + ' was accepted.')
        Remove-Item -LiteralPath $stray.Path
    }
    Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore
    # The config REWRITE BepInEx performs at Bind is expected: the audit revalidates its semantics
    # and records both hashes instead of claiming the bytes were unchanged.
    [IO.File]::WriteAllText($journalConfigPath, $bepJournalConfig)
    Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore
    $journalAudit = Get-Content -LiteralPath (Join-Path $journalRoot 'travel-journal-postquit-audit.txt')
    Assert (@($journalAudit | Where-Object { $_ -like "config-rewrite`told=*`tnew=*`tsemantics=pass" }).Count -eq 1) 'The post-quit audit did not record the expected BepInEx config rewrite.'
    Assert (@($journalAudit | Where-Object { $_ -like '*preparedBinaryHash=unchanged preparedConfig=semantics-revalidated*' }).Count -eq 1) 'The post-quit audit did not state what it actually verified for the prepared inputs.'
    # A rewrite that changes the pinned semantics is still refused.
    foreach ($broken in @($bepJournalConfig.Replace('MaxEvents = 0','MaxEvents = 500'),
        $bepJournalConfig.Replace('Verbose = true','Verbose = false'),
        ($bepJournalConfig + "MaxEvents = 500`r`n"))) {
        [IO.File]::WriteAllText($journalConfigPath, $broken)
        $rejected = $false
        try { Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore } catch { $rejected = $true }
        Assert $rejected 'A post-quit archived journal configuration with changed semantics was accepted.'
    }
    [IO.File]::WriteAllText($journalConfigPath, $bepJournalConfig)
    # The prepared BINARY, by contrast, must hash exactly unchanged.
    [IO.File]::WriteAllText($journalDll, 'rewritten-binary')
    $rejected = $false
    try { Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore } catch { $rejected = $_.Exception.Message -like '*prepared archive binary changed*' }
    Assert $rejected 'A rewritten prepared archive binary was accepted after quit.'
    [IO.File]::WriteAllText($journalDll, 'synthetic-not-executable')
    [IO.File]::WriteAllText($journalConfigPath, $validJournalConfig)
    Assert-TravelJournalContainment $journalRoot $journalAuditRoots $journalFilesBefore
    Remove-Item -LiteralPath (Join-Path $journalRoot 'travel-journal-postquit-audit.txt')
    & $script -Action Cleanup -SandboxRoot $journalRoot
    Remove-Item -LiteralPath Function:Get-FileHash
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
