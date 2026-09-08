# A planned second process in the same canonical save directory, never a retry.
function Start-StoryColdPhase([string]$Root, $Provenance) {
    if (!$Provenance.PSObject.Properties['storyColdSequence'] -or !$Provenance.storyColdSequence -or !$Provenance.storyProbe) { throw 'Cold phase was not planned at preparation.' }
    if (Get-Process VanguardGalaxy -ErrorAction SilentlyContinue) { throw 'A game process is still active.' }
    $finalized = Join-Path $Root 'story-producer-finalized.txt'
    if (!(Test-Path -LiteralPath $finalized -PathType Leaf) -or (Get-Content -LiteralPath $finalized -Raw).Trim() -cne 'PASS') { throw 'Producer launcher finalization is missing.' }
    if (Test-Path -LiteralPath (Join-Path $Root 'playerprefs-restore-failed.txt')) { throw 'Producer preference restoration failed.' }
    Assert-StoryReceipt $Root
    $result = @(Get-Content -LiteralPath (Join-Path $Root 'result.txt'))
    $outcome = Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw | ConvertFrom-Json
    if ($result[0] -cne 'PASS' -or !$outcome.selfTerminated -or $outcome.killed -or $outcome.timedOut -or $outcome.exitCode -ne -1) { throw 'Cold phase requires a clean, self-terminated producer.' }
    $donor = @(Get-Content -LiteralPath (Join-Path $Root 'story-definition-donor.txt'))
    if ($donor.Count -ne 6 -or $donor[0] -cne 'PASS' -or $donor[4] -eq $donor[5]) { throw 'Invalid cold donor receipt.' }
    $null = [Guid]::ParseExact($donor[4], 'D'); $null = [Guid]::ParseExact($donor[5], 'D')
    if (!(Test-Path -LiteralPath (Join-Path $Root 'Saves\qa-story-definition-cold.save') -PathType Leaf)) { throw 'Missing cold donor save.' }
    $claim = [IO.File]::Open((Join-Path $Root 'story-cold-phase-started.txt'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $claim.Dispose()
    $archive = Join-Path $Root 'story-producer-evidence'
    if (Test-Path -LiteralPath $archive) { throw 'Producer evidence already exists.' }
    $null = New-Item -ItemType Directory -Path $archive
    Get-ChildItem -LiteralPath $Root -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $archive }
    Copy-Item -LiteralPath (Join-Path $Root 'game\BepInEx\LogOutput.log') -Destination (Join-Path $archive 'BepInEx.log')
    # Require the new process to produce its own success result; retain all producer evidence.
    Remove-Item -LiteralPath (Join-Path $Root 'result.txt')
}

function Assert-StoryColdReceipt([string]$Root) {
    $outcome = Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw -ErrorAction Stop | ConvertFrom-Json
    foreach ($field in @('timedOut','killed','exitCode','selfTerminated')) {
        if (!$outcome.PSObject.Properties[$field]) { throw "Missing cold outcome field $field" }
    }
    foreach ($field in @('timedOut','killed','selfTerminated')) {
        if ($outcome.$field -isnot [bool]) { throw "Invalid cold outcome boolean $field" }
    }
    if (!$outcome.selfTerminated) { throw 'Cold process did not self-terminate.' }
    Assert-QualificationExitOutcome $outcome 'Cold story probe'
    $receipt = @(Get-Content -LiteralPath (Join-Path $Root 'story-definition-cold.txt'))
    if ($receipt.Count -ne 2 -or $receipt[0] -cne 'PASS' -or $receipt[1] -cne 'changed-startup;active;offered;retained-target;retained-amount;reload;normal-retirement') { throw 'Cold-start receipt incomplete.' }
}
