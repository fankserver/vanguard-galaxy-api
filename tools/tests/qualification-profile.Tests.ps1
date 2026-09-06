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
    foreach ($name in @('result.txt','process.json','run-started.txt','playerprefs-before.reg','vanilla-load-control.txt','vanilla-orbit-failure.txt')) {
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
    Write-Output 'PASS: PlayerPrefs snapshot/restore, typed values, removal of added values, absent-key cleanup, missing-backup/reused-run refusal, verification mismatch detection; synthetic registry only.'
}
finally {
    if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse }
    foreach ($file in @($snapshot, ($snapshot + '.verified.reg'))) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
