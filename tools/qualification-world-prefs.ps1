. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')
. (Join-Path $PSScriptRoot 'qualification-profile.ps1')

# Call only with the exclusive lease, before launch. Keep this receipt in memory and private storage.
function Save-WorldPrefs([string]$Root) {
    $rootPath = Assert-WorldSandboxRoot $Root
    $path = Assert-WorldUnlinkedPath (Join-Path $rootPath 'playerprefs-before.reg') $false
    if (Test-Path -LiteralPath $path) { throw 'Prior preferences evidence must be retained before another phase.' }
    $existed = Save-QualificationPrefs 'HKCU\Software\Bat Roost Games\VanguardGalaxy' $path
    $digest = $null
    if ($existed) {
        $null = Assert-WorldUnlinkedPath $path
        $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return @{ path=$path; existed=$existed; sha256=$digest }
}

# Receipt/outcome must be the actual in-memory objects, not deserialized or caller-authored records.
function Restore-WorldPrefs([string]$Root, $Receipt, $Outcome) {
    if ($null -eq $Outcome -or $Outcome.cleanupPending -or $null -ne $Outcome.cleanupFailure) { throw 'Process cleanup must be established before preferences restoration.' }
    $rootPath = Assert-WorldSandboxRoot $Root
    $path = Assert-WorldUnlinkedPath (Join-Path $rootPath 'playerprefs-before.reg') $false
    if ($null -eq $Receipt -or $Receipt.path -cne $path) { throw 'Preferences receipt belongs to another path.' }
    $verification = Assert-WorldUnlinkedPath ($path + '.verified.reg') $false
    if (Test-Path -LiteralPath $verification) { throw 'Preferences verification output must be fresh.' }
    if ($Receipt.existed) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Receipt.sha256) { throw 'Preferences backup changed; refuse destructive restoration.' }
    } elseif ((Test-Path -LiteralPath $path) -or $null -ne $Receipt.sha256) { throw 'Inconsistent absent preferences receipt.' }
    Restore-QualificationPrefs 'HKCU\Software\Bat Roost Games\VanguardGalaxy' $path $Receipt.existed
}
