# Uses only a unique synthetic registry key, never the game's real PlayerPrefs.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')
$id = [Guid]::NewGuid().ToString('N')
$key = "HKCU\Software\VGModAPI-test-$id"
$path = "Registry::HKEY_CURRENT_USER\Software\VGModAPI-test-$id"
$snapshot = Join-Path $env:TEMP "vgmodapi-profile-$id.reg"
$state = Join-Path $env:TEMP "vgmodapi-recovery-$id"
try {
    [IO.Directory]::CreateDirectory($state) | Out-Null
    Assert-QualificationUnused $state
    foreach ($name in @('result.txt','process.json','run-started.txt','playerprefs-before.reg','vanilla-load-control.txt','vanilla-orbit-failure.txt','persistence-probe.txt','journal-coordinated.txt','stockpile-coordinated.txt','content-reference.txt')) {
        $evidence = Join-Path $state $name
        [IO.File]::WriteAllText($evidence, 'preserve')
        $refused = $false
        try { Assert-QualificationUnused $state } catch { $refused = $_.Exception.Message.Contains('Sandbox already ran') }
        if (!$refused -or [IO.File]::ReadAllText($evidence) -ne 'preserve') { throw 'Existing recovery evidence was not protected.' }
        Remove-Item -LiteralPath $evidence
    }
    $exists = Save-QualificationPrefs $key $snapshot
    if ($exists) { throw 'Synthetic key unexpectedly exists.' }
    New-Item -Path $path | Out-Null
    New-ItemProperty -LiteralPath $path -Name width -PropertyType DWord -Value 1920 | Out-Null
    New-ItemProperty -LiteralPath $path -Name text -PropertyType String -Value 'original' | Out-Null
    New-ItemProperty -LiteralPath $path -Name bytes -PropertyType Binary -Value ([byte[]](1,2,255)) | Out-Null
    $exists = Save-QualificationPrefs $key $snapshot
    Set-ItemProperty -LiteralPath $path -Name width -Value 1024
    New-ItemProperty -LiteralPath $path -Name extra -PropertyType String -Value 'new' | Out-Null
    Restore-QualificationPrefs $key $snapshot $exists
    if ((Get-ItemPropertyValue -LiteralPath $path -Name width) -ne 1920) { throw 'Original value was not restored.' }
    if ((Get-Item -LiteralPath $path).GetValueNames() -contains 'extra') { throw 'New value survived restore.' }
    $refused = $false
    try { Restore-QualificationPrefs $key ($snapshot + '.missing') $true } catch { $refused = $true }
    if (!$refused -or !(Test-Path -LiteralPath $path)) { throw 'Missing snapshot did not preserve the current key.' }
    # Shadow only the native registry command to simulate a successful but mismatched export.
    function reg.exe {
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'export') { [IO.File]::WriteAllText([string]$args[2], 'mismatched export') }
    }
    $mismatch = $false
    try { Restore-QualificationPrefs $key $snapshot $true }
    catch { $mismatch = $_.Exception.Message.Contains('differ from the snapshot') }
    finally { Remove-Item Function:\reg.exe }
    if (!$mismatch) { throw 'Verification accepted a mismatched export.' }
    Restore-QualificationPrefs $key $snapshot $true
    Restore-QualificationPrefs $key $snapshot $false
    if (Test-Path -LiteralPath $path) { throw 'Originally absent key was not removed.' }
    # Owned-process lifetime: a benign short-lived child only (cmd.exe), never the game, never the
    # real profile. The cached handle must make a genuine exit code observable in both directions.
    $shell = Join-Path $env:SystemRoot 'System32\cmd.exe'
    foreach ($expected in @(0, 3)) {
        $child = Start-QualificationProcess $shell $env:TEMP @('/c', "exit $expected")
        $result = Wait-QualificationProcess $child 30
        try {
            if ($result.timedOut -or $result.killed) { throw "Benign child process was reported as terminated (exit $expected)." }
            if ($null -eq $result.exitCode) { throw "Exit code was unknown after waiting (exit $expected)." }
            if ($result.exitCode -ne $expected) { throw "Wrong exit code: expected $expected, got $($result.exitCode)." }
        } finally { $child.Dispose() }
    }
    # A process that outlives its deadline is killed and reported as terminated, never as success.
    $sleeper = Start-QualificationProcess $shell $env:TEMP @('/c', 'ping -n 30 127.0.0.1 >nul')
    $slow = Wait-QualificationProcess $sleeper 1
    try {
        if (!$slow.timedOut -or !$slow.killed) { throw 'A process past its deadline was not reported as terminated.' }
        if ($slow.exitCode -eq 0) { throw 'A killed process must never report a successful exit code.' }
    } finally { $sleeper.Dispose() }
    if ($null -ne (Wait-QualificationProcess $null 1).exitCode) { throw 'A missing process must report an unknown exit code.' }
    # Control for the inspected game's quit path: ApplicationQuitHandler.OnApplicationQuit ends with
    # Process.GetCurrentProcess().Kill(), and the Mono System.dll implements Kill() as
    # TerminateProcess(handle, -1). A benign child that self-terminates the same way must therefore
    # report exactly $GameSelfTerminationExitCode on this host. No game is launched.
    $self = Start-QualificationProcess $PSHOME\powershell.exe $env:TEMP @('-NoProfile','-Command','[Diagnostics.Process]::GetCurrentProcess().Kill()')
    $selfOutcome = Wait-QualificationProcess $self 60
    try {
        if ($selfOutcome.timedOut -or $selfOutcome.killed) { throw 'The self-terminating control was reported as launcher-terminated.' }
        if ($selfOutcome.exitCode -ne $GameSelfTerminationExitCode) { throw "Self-termination reported $($selfOutcome.exitCode), expected $GameSelfTerminationExitCode." }
    } finally { $self.Dispose() }
    # The acceptance rule: only a clean 0 and that source-proven self-termination code pass.
    Assert-QualificationExitOutcome @{timedOut=$false;killed=$false;exitCode=0} 'control'
    Assert-QualificationExitOutcome @{timedOut=$false;killed=$false;exitCode=$GameSelfTerminationExitCode} 'control'
    foreach ($bad in @(@{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1},
        @{timedOut=$false;killed=$false;exitCode=-1073741819}, @{timedOut=$true;killed=$false;exitCode=0},
        @{timedOut=$false;killed=$true;exitCode=0})) {
        $refused = $false
        try { Assert-QualificationExitOutcome $bad 'control' } catch { $refused = $true }
        if (!$refused) { throw ('An unacceptable launcher outcome was accepted: exit ' + $bad.exitCode) }
    }
    $refused = $false
    try { Assert-QualificationExitOutcome $null 'control' } catch { $refused = $true }
    if (!$refused) { throw 'A missing launcher outcome was accepted.' }
    Write-Output 'PASS: PlayerPrefs snapshot/restore, typed values, removal of added values, absent-key cleanup, missing-backup/reused-run refusal, verification mismatch detection, owned-process exit-code capture, deadline kill, self-termination exit-code control and the exit-outcome acceptance rule; synthetic registry and benign child processes only.'
}
finally {
    if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse }
    foreach ($file in @($snapshot, ($snapshot + '.verified.reg'))) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
