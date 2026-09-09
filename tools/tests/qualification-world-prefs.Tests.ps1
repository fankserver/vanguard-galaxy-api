$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-prefs.ps1')
# Replace all registry operations. These tests never read or write any registry key.
$script:exists = $true; $script:restores = 0
function Save-QualificationPrefs([string]$Key, [string]$Path) {
    if ($Key -cne 'HKCU\Software\Bat Roost Games\VanguardGalaxy') { throw 'Wrong preferences key.' }
    if ($script:exists) { [IO.File]::WriteAllText($Path, 'synthetic backup') }
    return $script:exists
}
function Restore-QualificationPrefs([string]$Key, [string]$Path, [bool]$Existed) {
    if ($Key -cne 'HKCU\Software\Bat Roost Games\VanguardGalaxy') { throw 'Wrong preferences key.' }
    $script:restores++
}
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Unsafe preferences restoration accepted.' } }
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$created = $false
try {
    $null = New-Item -ItemType Directory $root; $created = $true
    $receipt = Save-WorldPrefs $root
    $outcome = @{ cleanupPending=$false; cleanupFailure=$null }
    Restore-WorldPrefs $root $receipt $outcome
    if ($script:restores -ne 1) { throw 'Valid backup was not restored.' }
    Reject { Save-WorldPrefs $root }
    [IO.File]::WriteAllText($receipt.path, 'changed backup')
    Reject { Restore-WorldPrefs $root $receipt $outcome }
    [IO.File]::WriteAllText($receipt.path, 'synthetic backup')
    $outcome.cleanupPending = $true
    Reject { Restore-WorldPrefs $root $receipt $outcome }
    if ($script:restores -ne 1) { throw 'Rejected restoration reached registry adapter.' }
    $outcome.cleanupPending = $false
    Remove-Item -LiteralPath $receipt.path
    $script:exists = $false
    $receipt = Save-WorldPrefs $root
    Restore-WorldPrefs $root $receipt $outcome
    if ($script:restores -ne 2) { throw 'Absent preferences handling missing.' }
    Write-Output 'World preferences wrapper tests passed with fake registry operations only.'
} finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
