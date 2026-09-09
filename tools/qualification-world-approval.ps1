. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

# The caller must obtain ExpectedDigest from the independently approved run record.
# This function verifies that binding; it never approves current files or grants a native lease.
function Read-WorldApprovedRun([string]$Path, [string]$ExpectedDigest, [string]$Root, [Guid]$RunId,
    [ValidateSet('create','cold')][string]$Phase, [string]$ReviewedHead) {
    if ($ExpectedDigest -cnotmatch '^[0-9a-f]{64}$' -or $ReviewedHead -cnotmatch '^[0-9a-f]{40}$' -or $RunId -eq [Guid]::Empty) { throw 'Missing approved digest, reviewed head or run identity.' }
    $path = Assert-WorldUnlinkedPath $Path
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $memory = New-Object IO.MemoryStream
    try {
        $buffer = New-Object byte[] 8192
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $count -gt 4194304) { throw 'Approved world record exceeds limit.' }
            $memory.Write($buffer, 0, $count)
        }
        $bytes = $memory.ToArray()
    } finally { $memory.Dispose(); $stream.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($digest -cne $ExpectedDigest) { throw 'World approval record differs from the approved digest.' }
    $json = (New-Object Text.UTF8Encoding($false, $true)).GetString($bytes)
    # PowerShell may enumerate a singleton array into its sole object during assignment.
    # Check the JSON root token before conversion can erase the distinction.
    if (!$json.TrimStart([char[]]@(' ', "`t", "`r", "`n")).StartsWith('{', [StringComparison]::Ordinal)) { throw 'Approved world record must be a JSON object.' }
    $record = $json | ConvertFrom-Json
    $names = @('schema','root','gameDirectory','runId','phase','reviewedHead','authorizationSha256','gameInventory','saveInventory','stateInventory')
    if ($null -eq $record -or @($record.PSObject.Properties).Count -ne $names.Count -or
        @($record.PSObject.Properties | Where-Object { $_.Name -cnotin $names }).Count) { throw 'Unexpected world approval schema.' }
    foreach ($name in @('schema','root','gameDirectory','runId','phase','reviewedHead','authorizationSha256')) {
        if ($record.$name -isnot [string]) { throw 'Invalid world approval field type.' }
    }
    if ($record.schema -cne 'world-empty-run-v1' -or $record.runId -cne $RunId.ToString('D') -or
        $record.phase -cne $Phase -or $record.reviewedHead -cne $ReviewedHead -or
        ![IO.Path]::IsPathRooted($record.root) -or [IO.Path]::GetFullPath($record.root).TrimEnd('\') -ine [IO.Path]::GetFullPath($Root).TrimEnd('\') -or
        ![IO.Path]::IsPathRooted($record.gameDirectory) -or $record.authorizationSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'World approval scope mismatch.' }
    foreach ($name in @('gameInventory','saveInventory','stateInventory')) {
        if ($null -eq $record.$name -or $record.$name -isnot [System.Management.Automation.PSCustomObject] -or
            @($record.$name.PSObject.Properties).Count -gt 10000) { throw 'Invalid approved inventory shape.' }
        foreach ($entry in $record.$name.PSObject.Properties) {
            if ($entry.Value -isnot [string]) { throw 'Invalid approved inventory value.' }
        }
    }
    if (@($record.gameInventory.PSObject.Properties).Count -eq 0 -or @($record.saveInventory.PSObject.Properties).Count -eq 0) { throw 'Empty required approved inventory.' }
    return $record
}
