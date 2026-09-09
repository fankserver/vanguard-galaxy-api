. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

# Verify the three permitted resource junctions against an externally selected game installation.
# This is target identity evidence, not complete resource-content integrity or execution permission.
function Assert-WorldResourceTargets([string]$Root, [string]$GameDirectory) {
    $rootPath = Assert-WorldSandboxRoot $Root
    $original = Assert-WorldUnlinkedPath $GameDirectory
    if (!(Test-Path -LiteralPath $original -PathType Container)) { throw 'Game installation is not a directory.' }
    $sandboxGame = Assert-WorldUnlinkedPath (Join-Path $rootPath 'game')
    if (!(Test-Path -LiteralPath $sandboxGame -PathType Container)) { throw 'Sandbox game root is not a directory.' }
    if ($original.TrimEnd('\') -ieq $sandboxGame.TrimEnd('\') -or $original.TrimEnd('\') -ieq $rootPath -or
        $original.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $rootPath.StartsWith($original.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Original game and sandbox overlap.' }
    foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge', 'D3D12')) {
        $source = Join-Path $original $name
        $path = Join-Path $sandboxGame $name
        $entry = $null
        try { $entry = Get-Item -LiteralPath $path -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { }
        if (!(Test-Path -LiteralPath $source -PathType Container)) {
            if ($name -eq 'VanguardGalaxy_Data' -or $name -eq 'MonoBleedingEdge' -or $null -ne $entry) { throw 'Required resource source missing or unexpected resource attachment.' }
            continue
        }
        $null = Assert-WorldUnlinkedPath $source
        if ($null -eq $entry -or !$entry.PSIsContainer -or !($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $entry.LinkType -cne 'Junction') { throw 'Expected explicit resource junction.' }
        $targets = @($entry.Target)
        if ($targets.Count -ne 1 -or ![IO.Path]::IsPathRooted($targets[0]) -or
            [IO.Path]::GetFullPath($targets[0]).TrimEnd('\') -ine [IO.Path]::GetFullPath($source).TrimEnd('\')) { throw 'World resource junction targets a different installation.' }
    }
}
