# Recovery helpers shared by the launcher and synthetic tests; no work on import.
function Assert-QualificationUnused([string]$Root) {
    foreach ($name in @('result.txt','process.json','run-started.txt','playerprefs-before.reg','vanilla-load-control.txt','vanilla-orbit-failure.txt','persistence-probe.txt','journal-coordinated.txt','stockpile-coordinated.txt','content-reference.txt','mission-transitions.txt','mission-events.tsv','mission-clear.txt','mission-guild.txt','mission-waves.txt','mission-wave-events.tsv','mission-identity.txt','journal-mission-events.txt','anima-missions.txt','anima-mission-events.tsv','travel-station.txt','travel-station-receipt.tsv','travel-station-events.tsv','travel-cross-system.txt','travel-cross-system-receipt.tsv','travel-cross-system-events.tsv','travel-cross-system-fault.txt','run-outcome.json')) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) { throw 'Sandbox already ran. Preserve recovery evidence and prepare a fresh directory.' }
    }
}
# Owned-process lifetime helpers. Windows PowerShell only keeps Process.ExitCode available when the
# handle was materialised while the process was still alive, so the handle is cached immediately
# after Start-Process and the object is kept alive (and disposed) by the caller after the outcome
# has been recorded. An unknown exit code stays unknown; it is never rewritten to success.
function Start-QualificationProcess([string]$FilePath, [string]$WorkingDirectory, [string[]]$ArgumentList) {
    $process = Start-Process -FilePath $FilePath -WorkingDirectory $WorkingDirectory -ArgumentList $ArgumentList -PassThru
    $null = $process.Handle
    return $process
}
function Wait-QualificationProcess($Process, [int]$TimeoutSeconds) {
    $outcome = @{ timedOut = $false; killed = $false; exitCode = $null }
    if ($null -eq $Process) { return $outcome }
    if (!$Process.WaitForExit($TimeoutSeconds * 1000)) {
        $outcome.timedOut = $true
        if (!$Process.HasExited) { $outcome.killed = $true; $Process.Kill(); $null = $Process.WaitForExit(30000) }
    }
    if ($Process.HasExited) { $outcome.exitCode = $Process.ExitCode }
    return $outcome
}
# Expected exit code of a NORMAL quit of the inspected game. Source-proven, not inferred from a
# receipt: ApplicationQuitHandler.OnApplicationQuit() runs SteamStatsManager.HandleApplicationQuit
# and GameManager.HandleApplicationQuit (the quit-time autosave that the logs show as
# "Quiting, save!") and then, outside the editor, calls Process.GetCurrentProcess().Kill(); the Mono
# System.dll shipped with the game implements Process.Kill() as TerminateProcess(handle, -1), so the
# operating system always reports -1 (0xFFFFFFFF) and the value passed to Application.Quit(...) is
# never observable. Two runs whose Application.Quit argument differed (0 for a passing run, 1 for a
# failing one) both reported -1, and neither produced a Windows Application Error or a Unity crash
# dump. Only this exact value and a clean 0 are accepted; every other code, and an unknown code,
# still refuses the run, so a crash (which reports its own status code, e.g. an access violation)
# cannot be mistaken for the game's self-termination.
$GameSelfTerminationExitCode = -1
function Assert-QualificationExitOutcome($Outcome, [string]$Context) {
    if ($null -eq $Outcome) { throw "$Context has no recorded launcher outcome." }
    if ($Outcome.timedOut) { throw "$Context timed out; its evidence is incomplete." }
    if ($Outcome.killed) { throw "$Context was terminated by the launcher; its evidence is incomplete." }
    if ($null -eq $Outcome.exitCode) { throw "$Context has an unknown exit code." }
    $code = [int]$Outcome.exitCode
    if ($code -ne 0 -and $code -ne $GameSelfTerminationExitCode) { throw "$Context exited with code $code." }
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
