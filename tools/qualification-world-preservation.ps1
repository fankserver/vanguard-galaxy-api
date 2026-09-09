. (Join-Path $PSScriptRoot 'qualification-world-preflight.ps1')

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
