. (Join-Path $PSScriptRoot 'qualification-world-preflight.ps1')

# Derive required directories from the approved source installation and current OS profile.
# An external production persistence root is included; nested state is already covered by config.
function Get-WorldProductionRoots([string]$GameDirectory) {
    $game = (Assert-WorldUnlinkedPath $GameDirectory).TrimEnd('\')
    $config = Join-Path $game 'BepInEx\config'
    $roots = New-Object 'System.Collections.Generic.List[string]'
    $roots.Add((Join-Path $game 'BepInEx\plugins'))
    $roots.Add($config)
    $profile = [Environment]::GetFolderPath('UserProfile')
    if ([string]::IsNullOrEmpty($profile)) { throw 'Current OS profile unavailable.' }
    $roots.Add((Join-Path $profile 'AppData\LocalLow\Bat Roost Games\VanguardGalaxy\Saves'))
    $file = Assert-WorldUnlinkedPath (Join-Path $config 'vgmodapi.cfg') $false
    if (Test-Path -LiteralPath $file) {
        $entries = Read-WorldIni $file
        if ($entries.ContainsKey('Persistence/Root')) {
            $state = $entries['Persistence/Root']
            if (![IO.Path]::IsPathRooted($state)) { throw 'Production persistence root is not absolute.' }
            $state = (Assert-WorldUnlinkedPath $state $false).TrimEnd('\')
            if ($state -ine $config -and !$state.StartsWith($config + '\', [StringComparison]::OrdinalIgnoreCase)) { $roots.Add($state) }
        }
    }
    return $roots.ToArray()
}

function Assert-WorldPreservationRoots([string[]]$Actual, [string[]]$Expected, [string]$Sandbox) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Actual) { if (!$set.Add($path)) { throw 'Duplicate production preservation root.' } }
    if ($set.Count -lt 3 -or $set.Count -ne $Expected.Count) { throw 'Approved preservation root set differs.' }
    $sandboxPath = (Assert-WorldSandboxRoot $Sandbox).TrimEnd('\')
    foreach ($path in $Expected) {
        if (!$set.Remove($path)) { throw 'Approved preservation root missing or duplicated.' }
        if ($path -ieq $sandboxPath -or $path.StartsWith($sandboxPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $sandboxPath.StartsWith($path.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Production preservation root overlaps sandbox.' }
    }
}

# Read-only before/after observations, not backups or permission to run. Call under the
# exclusive lease; persist private evidence separately. Never silently restore changed files.
function Get-WorldPreservationSnapshot([string[]]$Directories) {
    if ($Directories.Count -lt 1 -or $Directories.Count -gt 8) { throw 'Invalid preservation root count.' }
    $snapshot = New-Object 'System.Collections.Generic.SortedDictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in $Directories) {
        $path = Assert-WorldUnlinkedPath $directory $false
        if ($snapshot.ContainsKey($path)) { throw 'Duplicate preservation root.' }
        $present = Test-Path -LiteralPath $path
        $inventory = $null
        if ($present) { $inventory = Get-WorldDataInventory $path }
        $snapshot.Add($path, @{ present=$present; inventory=$inventory })
    }
    return ,$snapshot
}

# Receives the actual in-memory snapshots, not arbitrary/deserialized approval records.
function Assert-WorldPreservation($Before, $After) {
    if ($null -eq $Before -or $null -eq $After -or $Before.Count -ne $After.Count -or $Before.Count -lt 1) { throw 'Preservation root set differs.' }
    foreach ($root in $Before.Keys) {
        if (!$After.ContainsKey($root)) { throw 'Preservation root missing.' }
        $left = $Before[$root]; $right = $After[$root]
        if ($left.present -ne $right.present) { throw 'Preserved directory presence changed.' }
        if (!$left.present) { continue }
        if ($left.inventory.Count -ne $right.inventory.Count) { throw 'Preserved inventory set changed.' }
        foreach ($entry in $left.inventory.GetEnumerator()) {
            if (!$right.inventory.ContainsKey($entry.Key) -or $right.inventory[$entry.Key] -cne $entry.Value) { throw 'Preserved directory content changed.' }
        }
    }
}
