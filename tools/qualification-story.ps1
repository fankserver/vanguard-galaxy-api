function Assert-StoryAbsentReceipt([string]$Root) {
    $result = @(Get-Content -LiteralPath (Join-Path $Root 'story-absent.txt'))
    if ($result.Count -ne 2 -or $result[0] -cne 'PASS' -or $result[1] -cne 'owned-story-absent-assemblies-v1') { throw 'Absent-story receipt incomplete.' }
    $outcome = Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw | ConvertFrom-Json
    foreach ($field in @('timedOut','killed','selfTerminated')) {
        if (!$outcome.PSObject.Properties[$field] -or $outcome.$field -isnot [bool]) { throw 'Invalid absent-story exit evidence.' }
    }
    if ($outcome.selfTerminated -ne $true) { throw 'Absent-story process did not self-terminate.' }
    Assert-QualificationExitOutcome $outcome 'Absent-story probe'
}

function Assert-StoryReceipt([string]$Root) {
    $outcomePath = Join-Path $Root 'run-outcome.json'
    if (!(Test-Path -LiteralPath $outcomePath -PathType Leaf)) { throw 'Missing story exit outcome.' }
    $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
    foreach ($field in @('timedOut','killed','exitCode','selfTerminated')) {
        if (!$outcome.PSObject.Properties[$field]) { throw "Missing story outcome field $field" }
    }
    foreach ($field in @('timedOut','killed','selfTerminated')) {
        if ($outcome.$field -isnot [bool]) { throw "Invalid story outcome boolean $field" }
    }
    if ($outcome.selfTerminated -ne $true) { throw 'Story process did not terminate itself.' }
    Assert-QualificationExitOutcome $outcome 'Story probe'
    $newGame = @(Get-Content -LiteralPath (Join-Path $Root 'story-new-game.txt'))
    if ($newGame.Count -ne 2 -or $newGame[0] -cne 'PASS' -or $newGame[1] -cne 'owned-new-game-roundtrip-v1') { throw 'Owned new-game proof incomplete.' }
    $objectives = @(Get-Content -LiteralPath (Join-Path $Root 'story-objectives.txt') -ErrorAction Stop)
    if ($objectives.Count -ne 2 -or $objectives[0] -cne 'PASS' -or $objectives[1] -cne 'owners;partial-reload;stale-session;inactive-step;authored-beat;generated-objective;duplicate;native-claim;revision-reorder;repeated-instance;rollback') { throw 'Owned objective proof incomplete.' }
    $cases = @(Get-Content -LiteralPath (Join-Path $Root 'story-cases.txt'))
    $expected = @('independent-authors','offered-roundtrip','active-roundtrip','native-completion','save-refusals','older-save-rollback','cross-slot-return','repeat-job','provider-unregistered-first-reload','provider-unregistered-second-reload')
    if (($cases -join "`n") -cne ($expected -join "`n")) { throw 'Story cases missing, duplicated or out of order.' }
    $result = @(Get-Content -LiteralPath (Join-Path $Root 'story-result.txt'))
    if ($result.Count -ne 2 -or $result[0] -cne 'PASS' -or $result[1] -cne 'owned-story-v1') { throw 'Story receipt incomplete.' }
}
