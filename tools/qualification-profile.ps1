# Recovery helpers shared by the launcher and synthetic tests; no work on import.
function Assert-QualificationUnused([string]$Root) {
    foreach ($name in @('result.txt','process.json','run-started.txt','playerprefs-before.reg','vanilla-load-control.txt','vanilla-orbit-failure.txt','persistence-probe.txt','journal-coordinated.txt','stockpile-coordinated.txt','content-reference.txt','mission-transitions.txt','mission-events.tsv','mission-clear.txt','mission-guild.txt','mission-waves.txt','mission-wave-events.tsv','mission-identity.txt','journal-mission-events.txt','anima-missions.txt','anima-mission-events.tsv','travel-station.txt','travel-station-receipt.tsv','travel-station-events.tsv','run-outcome.json')) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) { throw 'Sandbox already ran. Preserve recovery evidence and prepare a fresh directory.' }
    }
}
function Save-QualificationPrefs([string]$Key, [string]$Snapshot) {
    if (!$Key.StartsWith('HKCU\Software\')) { throw 'Only per-user software keys are supported.' }
    $path = 'Registry::HKEY_CURRENT_USER\' + $Key.Substring(5)
    $exists = Test-Path -LiteralPath $path
    if ($exists) {
        & reg.exe export $Key $Snapshot /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot snapshot PlayerPrefs; refusing to launch.' }
    }
    return $exists
}
function Restore-QualificationPrefs([string]$Key, [string]$Snapshot, [bool]$Existed) {
    if (!$Key.StartsWith('HKCU\Software\')) { throw 'Only per-user software keys are supported.' }
    $path = 'Registry::HKEY_CURRENT_USER\' + $Key.Substring(5)
    if ($Existed -and !(Test-Path -LiteralPath $Snapshot -PathType Leaf)) { throw 'Missing PlayerPrefs snapshot; refusing to delete the current key.' }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse }
    if ($Existed) {
        & reg.exe import $Snapshot | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'PlayerPrefs restore failed; retain the private snapshot for recovery.' }
        $verification = $Snapshot + '.verified.reg'
        & reg.exe export $Key $verification /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify restored PlayerPrefs.' }
        if ((Get-FileHash -LiteralPath $Snapshot).Hash -ne (Get-FileHash -LiteralPath $verification).Hash) { throw 'Restored PlayerPrefs differ from the snapshot.' }
    }
    elseif (Test-Path -LiteralPath $path) { throw 'PlayerPrefs key created by the run was not removed.' }
}
