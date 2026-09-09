$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-run.ps1')
# Replace all game, registry and production-read boundaries. Only synthetic evidence files are real.
function New-WorldProcessStartInfo($Root, $RunId, $Phase) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.UseShellExecute=$false; $info.FileName='fixture-only'; $info.WorkingDirectory=$Root; $info.Arguments=$Phase
    return $info
}
function Assert-WorldRunPreflight($Root, $RunId, $Phase, $Head, $Path, $Digest) {
    $script:preflights++
    return [pscustomobject]@{ timeoutSeconds=600; root=$Root; process=(Get-WorldProcessDescription (New-WorldProcessStartInfo $Root $RunId $Phase) | ConvertTo-Json -Depth 5 | ConvertFrom-Json) }
}
function Assert-NoWorldGameProcess { }
function Get-WorldRunPreservation($Record) {
    $script:observations++
    return @{ fixture=@{ present=($script:mode -eq 'preservation' -and $script:observations -eq 3); inventory=@{} } }
}
function Save-WorldPrefs($Root) { return @{ path=(Join-Path $Root 'synthetic-backup'); existed=$false; sha256=$null } }
function Restore-WorldPrefs($Root, $Receipt, $Outcome) { $script:restores++ }
function Assert-WorldReceiptPaths($Root, $Phase) { }
function Assert-WorldPhaseReceipt($Root, $Phase) { if ($script:mode -eq 'receipt') { throw 'Synthetic receipt rejection.' } }
function Invoke-WorldProcessLifetime($Info, $Timeout, $Process) {
    if ($script:preflights -ne 2 -or !(Test-Path (Join-Path $Info.WorkingDirectory 'world-launch-before.json')) -or
        !(Test-Path (Join-Path $Info.WorkingDirectory 'world-prefs-receipt.json'))) { throw 'Launch preceded durable recovery inputs.' }
    $script:starts++
    $Process.Dispose() # This is an unstarted object; no OS child exists in this test.
    return @{ started=$true; pid=42; timedOut=($script:mode -eq 'timeout'); killed=($script:mode -eq 'timeout');
        exitCode=0; cleanupPending=($script:mode -eq 'pending'); failure=$null; cleanupFailure=$null }
}
foreach ($case in @('clean','timeout','pending','preservation','receipt')) {
    $script:mode=$case; $script:preflights=0; $script:observations=0; $script:restores=0; $script:starts=0
    $root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
    $created=$false
    try {
        $null = New-Item -ItemType Directory $root; $created=$true
        $null = New-Item -ItemType Directory (Join-Path $root 'temp')
        $run=[Guid]::NewGuid(); $failed=$false
        try { Invoke-WorldQualificationPhase $root $run 'create' ('a'*40) 'fixture' ('b'*64) } catch { $failed=$true }
        if (!$failed -or $script:preflights -ne 0) { throw 'Missing lease attestation reached preflight.' }
        $failed=$false
        try { $null = Invoke-WorldQualificationPhase $root $run 'create' ('a'*40) 'fixture' ('b'*64) -ExclusiveLeaseConfirmed } catch { $failed=$true }
        if ($failed -ne ($case -ne 'clean') -or $script:starts -ne 1) { throw "Wrong composed result for $case." }
        if (!(Test-Path (Join-Path $root 'world-process-outcome.json'))) { throw 'Process outcome evidence missing.' }
        if ($case -eq 'pending') {
            if ($script:restores -ne 0 -or (Test-Path (Join-Path $root 'world-launch-after.json'))) { throw 'Pending child reached restoration.' }
        } elseif ($script:restores -ne 1 -or !(Test-Path (Join-Path $root 'world-launch-after.json'))) { throw 'Post-process preservation evidence missing.' }
    } finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
}
Write-Output 'World phase orchestration tests passed with fake process/registry/production boundaries only.'
