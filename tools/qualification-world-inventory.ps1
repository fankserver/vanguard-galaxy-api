. (Join-Path $PSScriptRoot 'qualification-world-resources.ps1')

# Complete point-in-time inventory of the owned game tree. Resource junctions are recorded,
# never traversed. Use before launch; generated runtime logs/caches are not input artifacts.
function Get-WorldLaunchInventory([string]$Root, [string]$GameDirectory) {
    Assert-WorldResourceTargets $Root $GameDirectory
    $game = Join-Path ([IO.Path]::GetFullPath($Root)) 'game'
    $result = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($game)
    while ($pending.Count) {
        $directory = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if ($result.Count -ge 10000) { throw 'World input inventory exceeds limit.' }
            $relative = $entry.FullName.Substring($game.Length + 1).Replace('\', '/')
            if ($relative.Split('/').Count -gt 64) { throw 'World input tree exceeds depth limit.' }
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                if ($relative -cnotin @('VanguardGalaxy_Data', 'MonoBleedingEdge', 'D3D12')) { throw 'Unexpected linked world input.' }
                $result.Add($relative, 'J:' + [IO.Path]::GetFullPath(@($entry.Target)[0]).TrimEnd('\'))
            } elseif ($entry.PSIsContainer) {
                $result.Add($relative, 'D')
                $pending.Push($entry.FullName)
            } else {
                $result.Add($relative, 'F:' + (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
            }
        }
    }
    return ,$result
}

# Expected is a caller-provided, independently approved inventory, not data derived from current files.
function Assert-WorldLaunchInventory([string]$Root, [string]$GameDirectory, [System.Collections.IDictionary]$Expected) {
    if ($null -eq $Expected -or $Expected.Count -eq 0 -or $Expected.Count -gt 10000) { throw 'Missing or excessive approved world inventory.' }
    $actual = Get-WorldLaunchInventory $Root $GameDirectory
    if ($actual.Count -ne $Expected.Count) { throw 'World launch inventory file/directory set changed.' }
    foreach ($key in $Expected.Keys) {
        if ($key -isnot [string] -or !$actual.ContainsKey($key) -or $Expected[$key] -isnot [string] -or $Expected[$key] -cne $actual[$key]) { throw 'World launch input differs from approved inventory.' }
    }
}
