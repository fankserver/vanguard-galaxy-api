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
    & $script -Action Prepare -SandboxRoot $sandbox @options
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
    $validJournal = "[Persistence]`nUseCoordinatedPersistence = true`nImportLegacySidecars = true`n"
    [IO.File]::WriteAllText($journalConfig, $validJournal)
    $null = Assert-QualificationInputs $probeRoot
    foreach ($changed in @($validJournal.Replace('true','false'), ($validJournal + "ImportLegacySidecars = false`n"), $validJournal.Replace('[Persistence]','[Other]'))) {
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
    foreach ($name in @('mission-transitions.txt','mission-clear.txt','mission-guild.txt')) {
        $rejected = $false
        try { Assert-PersistenceProbeReceipt $probeRoot $probeProvenance } catch { $rejected = $true }
        Assert $rejected "Missing mission receipt accepted: $name"
        [IO.File]::WriteAllText((Join-Path $probeRoot $name), 'PASS')
    }
    Assert-PersistenceProbeReceipt $probeRoot $probeProvenance
    [IO.File]::WriteAllText($apiConfig, "[Persistence]`nEnabled = true`nRoot = C:\foreign-root`n[Missions]`nEnabled = true`n")
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
    [IO.File]::WriteAllText($marker, 'invalid')
    $rejected = $false
    try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
    Assert $rejected 'Invalid consumer marker accepted.'
    [IO.File]::WriteAllText($marker, 'pilot-v1')
    $consumerProvenance.stockpile = $true
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
