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
    $cases = @(Get-Content -LiteralPath (Join-Path $Root 'story-cases.txt'))
    $expected = @('independent-authors','offered-roundtrip','active-roundtrip','native-completion','save-refusals','older-save-rollback','cross-slot-return','repeat-job','provider-unregistered-first-reload','provider-unregistered-second-reload')
    if (($cases -join "`n") -cne ($expected -join "`n")) { throw 'Story cases missing, duplicated or out of order.' }
    $result = @(Get-Content -LiteralPath (Join-Path $Root 'story-result.txt'))
    if ($result.Count -ne 2 -or $result[0] -cne 'PASS' -or $result[1] -cne 'owned-story-v1') { throw 'Story receipt incomplete.' }
}
