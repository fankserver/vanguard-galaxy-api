. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

# Verifies an externally supplied run manifest; never creates authorization or launches a process.
function Assert-WorldQualificationInputs([string]$Root, [Guid]$RunId) {
    $rootPath = Assert-WorldSandboxRoot $Root
    if ($RunId -eq [Guid]::Empty) { throw 'Empty world run identity.' }
    foreach ($name in @('qualification.marker', 'scenario.txt', 'world.enabled', 'world-qualification.authorization', 'Saves', 'game\BepInEx\plugins')) {
        $null = Assert-WorldUnlinkedPath (Join-Path $rootPath $name)
    }
    foreach ($name in @('Saves', 'game\BepInEx\plugins')) {
        if (!(Test-Path -LiteralPath (Join-Path $rootPath $name) -PathType Container)) { throw 'World sandbox directory is missing.' }
    }
    function ReadBounded([string]$Path) {
        if ((Get-Item -LiteralPath $Path).Length -gt 4096) { throw 'Oversized world input text.' }
        return (New-Object Text.UTF8Encoding($false, $true)).GetString([IO.File]::ReadAllBytes($Path))
    }
    if ((ReadBounded (Join-Path $rootPath 'qualification.marker')).Trim() -cne 'vgmodapi-disposable-sandbox-v1' -or
        (ReadBounded (Join-Path $rootPath 'scenario.txt')).Trim() -cne 'Full' -or
        (ReadBounded (Join-Path $rootPath 'world.enabled')).Trim() -cne 'world-empty-v1') { throw 'Incorrect world sandbox markers.' }
    $authorization = Join-Path $rootPath 'world-qualification.authorization'
    $rows = (ReadBounded $authorization).Replace("`r`n", "`n").TrimEnd("`n").Split("`n")
    if ($rows.Count -ne 11 -or $rows[0] -cne 'vgmodapi-world-empty-v1' -or $rows[1] -cne $RunId.ToString('D')) { throw 'Invalid world authorization identity.' }
    $expires = 0L
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($rows[2] -cnotmatch '^[0-9]+$' -or ![long]::TryParse($rows[2], [ref]$expires) -or $expires -le $now -or $expires -gt ($now + 14400)) { throw 'World authorization expired or exceeds four hours.' }
    for ($index = 3; $index -lt 11; $index++) { if ($rows[$index] -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid world input hash.' } }
    $plugins = Join-Path $rootPath 'game\BepInEx\plugins'
    $names = @('VGModAPI.dll', 'VGModAPI.Core.dll', 'VGModAPI.Abstractions.dll', 'QualificationGuard.dll', 'WorldAuthorA.dll', 'WorldAuthorB.dll', 'QualificationRunner.dll')
    $entries = @(Get-ChildItem -LiteralPath $plugins -Force)
    if ($entries.Count -ne 7 -or @($entries | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $names -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) { throw 'World plugin inventory differs from the exact seven-file set.' }
    $gameAssembly = Join-Path $rootPath 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll'
    # Resource junction targets must be independently pinned by the eventual launcher.
    if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash.ToLowerInvariant() -cne $rows[3]) { throw 'Game assembly hash differs from authorization.' }
    for ($index = 0; $index -lt 7; $index++) {
        $path = Assert-WorldUnlinkedPath (Join-Path $plugins $names[$index])
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $rows[$index + 4]) { throw 'World plugin hash differs from authorization.' }
    }
    return @{ runId=$RunId.ToString('D'); expires=$expires; authorizationSha256=(Get-FileHash -LiteralPath $authorization -Algorithm SHA256).Hash.ToLowerInvariant() }
}
