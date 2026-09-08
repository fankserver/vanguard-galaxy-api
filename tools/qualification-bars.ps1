# Controlled independent-author bar phase; does not claim consumer or cold-process qualification.
function Initialize-BarProbe([string]$Root, [string]$AuthorA, [string]$AuthorB) {
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    foreach ($author in @(@('OwnedBarAuthorA', $AuthorA), @('OwnedBarAuthorB', $AuthorB))) {
        $source = Join-Path $author[1] ($author[0] + '.dll')
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing bar author: $source" }
        Copy-Item -LiteralPath $source -Destination $plugins
    }
    [IO.File]::WriteAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'), "[Persistence]`r`nEnabled = true`r`nRoot = $(Join-Path $Root 'state')`r`n[Bars]`r`nEnabled = true`r`nExclusiveProviders = vg-bar-author-a,vg-bar-author-b`r`n")
    [IO.File]::WriteAllText((Join-Path $Root 'bars.enabled'), 'owned-bars-v1')
}

function Assert-BarReceipt([string]$Root) {
    $path = Join-Path $Root 'owned-bars.txt'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing owned-bar receipt.' }
    $rows = @(Get-Content -LiteralPath $path)
    if ($rows.Count -ne 2 -or $rows[0] -cne 'PASS' -or $rows[1] -cne 'independent-authors;repeated-check-update;ui-open;interaction;native-json;exclusive-denial;exclusive-conflict;reload;stale-session;stale-interaction;provider-reconstruction') {
        throw 'Incomplete or unexpected owned-bar receipt.'
    }
}

function Assert-BarColdReceipt([string]$Root, [ValidateSet('absent','consumer')][string]$Phase) {
    $rows = @(Get-Content -LiteralPath (Join-Path $Root ('bar-cold-' + $Phase + '.txt')) -ErrorAction Stop)
    $cases = if ($Phase -eq 'absent') { 'fresh-process;unregistered-providers;no-owned-presentation;same-save' } else { 'fresh-process;retained-identities;retained-seeds;no-place-after-absence' }
    if ($rows.Count -ne 2 -or $rows[0] -cne 'PASS' -or $rows[1] -cne $cases) { throw 'Incomplete cold bar receipt.' }
}

function Start-BarColdPhase([string]$Root, $Provenance, [ValidateSet('absent','consumer')][string]$Phase) {
    if (!$Provenance.barProbe -or !$Provenance.PSObject.Properties['barColdSequence'] -or !$Provenance.barColdSequence) { throw 'Cold bar sequence was not planned.' }
    if (Get-Process VanguardGalaxy -ErrorAction SilentlyContinue) { throw 'A game process is still active.' }
    $previous = if ($Phase -eq 'absent') { 'producer' } else { 'absent' }
    $finalized = Join-Path $Root ('bar-' + $previous + '-finalized.txt')
    if (!(Test-Path -LiteralPath $finalized -PathType Leaf) -or (Get-Content -LiteralPath $finalized -Raw).Trim() -cne 'PASS') { throw 'Prior bar phase was not finalized.' }
    if ($previous -eq 'producer') { Assert-BarReceipt $Root } else { Assert-BarColdReceipt $Root 'absent' }
    if (Test-Path -LiteralPath (Join-Path $Root 'playerprefs-restore-failed.txt')) { throw 'Prior preference restoration failed.' }
    $result = @(Get-Content -LiteralPath (Join-Path $Root 'result.txt'))
    $outcome = Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw | ConvertFrom-Json
    Assert-QualificationExitOutcome $outcome 'Prior bar phase'
    if ($result[0] -cne 'PASS' -or !$outcome.selfTerminated) { throw 'Prior bar phase did not succeed.' }
    $donor = @(Get-Content -LiteralPath (Join-Path $Root 'bar-cold-donor.txt'))
    if ($donor.Count -ne 3 -or $donor[0] -cne 'PASS' -or !$donor[2]) { throw 'Invalid bar donor.' }
    $null = [Guid]::ParseExact($donor[1], 'D')
    if (!(Test-Path -LiteralPath (Join-Path $Root 'Saves\qa-owned-bars.save') -PathType Leaf)) { throw 'Missing bar donor save.' }
    $claim = [IO.File]::Open((Join-Path $Root ('bar-cold-' + $Phase + '-started.txt')), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $claim.Dispose()
    $archive = Join-Path $Root ('bar-' + $previous + '-evidence')
    if (Test-Path -LiteralPath $archive) { throw 'Prior evidence already archived.' }
    $null = New-Item -ItemType Directory -Path $archive
    Get-ChildItem -LiteralPath $Root -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $archive }
    Copy-Item -LiteralPath (Join-Path $Root 'game\BepInEx\LogOutput.log') -Destination (Join-Path $archive 'BepInEx.log')
    Remove-Item -LiteralPath (Join-Path $Root 'result.txt')
}
