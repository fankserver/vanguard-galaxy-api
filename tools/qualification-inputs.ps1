# Prepared-input helpers; safe to exercise with synthetic files.
function Copy-QualificationJournalHistory($Sources, [string]$Saves) {
    foreach ($source in $Sources) {
        if (!(Test-Path -LiteralPath ($source.FullName + '.vgmissionjournal.json') -PathType Leaf)) { throw 'Journal pilot requires copied history for both fixtures.' }
    }
    for ($i = 0; $i -lt 2; $i++) {
        $name = if ($i -eq 0) { 'fixture-a' } else { 'fixture-b' }
        Copy-Item -LiteralPath ($Sources[$i].FullName + '.vgmissionjournal.json') -Destination (Join-Path $Saves ($name + '.save.vgmissionjournal.json'))
    }
}

# Read-only verification.
function Assert-QualificationInputs([string]$Root) {
    $provenance = Get-Content -LiteralPath (Join-Path $Root 'build-provenance.json') -Raw | ConvertFrom-Json
    if ($provenance.scenario -notin @('Full','MissingApi','UnavailableApi') -or
        (Get-Content -LiteralPath (Join-Path $Root 'scenario.txt') -Raw).Trim() -cne $provenance.scenario) { throw 'Prepared scenario changed.' }
    $journalMarker = Join-Path $Root 'missionjournal.enabled'
    if ([bool]$provenance.missionJournal -ne (Test-Path -LiteralPath $journalMarker -PathType Leaf)) { throw 'Prepared consumer selection changed.' }
    if ($provenance.missionJournal -and (Get-Content -LiteralPath $journalMarker -Raw).Trim() -ne 'pilot-v1') { throw 'Unknown consumer pilot marker.' }
    $stockpile = $provenance.PSObject.Properties['stockpile'] -and [bool]$provenance.stockpile
    $stockpileMarker = Join-Path $Root 'stockpile.enabled'
    if ([bool]$stockpile -ne (Test-Path -LiteralPath $stockpileMarker -PathType Leaf)) { throw 'Prepared Stockpile selection changed.' }
    if ($stockpile -and (Get-Content -LiteralPath $stockpileMarker -Raw).Trim() -ne 'pilot-v1') { throw 'Unknown Stockpile pilot marker.' }
    $expected = @('QualificationGuard.dll')
    if ($provenance.scenario -ne 'MissingApi') { $expected += @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll') }
    if ($provenance.scenario -eq 'Full') { $expected += @('QualificationRunner.dll','LifecycleObserver.dll') }
    if ($provenance.missionJournal) { $expected += @('VGMissionJournal.dll','Newtonsoft.Json.dll') }
    if ($stockpile) { $expected += @('VGStockpile.dll','Newtonsoft.Json.dll') }
    $expected = @($expected | Select-Object -Unique)
    if (@($provenance.plugins.PSObject.Properties).Count -ne $expected.Count -or
        @($provenance.plugins.PSObject.Properties.Name | Where-Object { $_ -notin $expected }).Count -gt 0) { throw 'Scenario plugin allowlist mismatch.' }
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    $actual = @(Get-ChildItem -LiteralPath $plugins -Force)
    if ($actual.Count -ne @($provenance.plugins.PSObject.Properties).Count -or
        @($actual | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -notin @($provenance.plugins.PSObject.Properties.Name) }).Count -gt 0) { throw 'Prepared plugin set changed.' }
    foreach ($property in $provenance.plugins.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $plugins $property.Name) -Algorithm SHA256).Hash -ne $property.Value) { throw 'Prepared plugin changed; refuse stale provenance.' }
    }
    return $provenance
}
