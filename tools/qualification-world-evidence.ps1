. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

# Private run artifacts only. Never overwrite prior evidence, even after a failed write.
# The caller must persist recovery inputs successfully before launching anything.
function Write-WorldPrivateEvidence([string]$Root,
    [ValidateSet('world-launch-before.json','world-launch-after.json','world-process-outcome.json','world-prefs-receipt.json','world-phase-accepted.json','world-creation-archive.json')][string]$Name,
    $Value) {
    $rootPath = Assert-WorldSandboxRoot $Root
    $path = Assert-WorldUnlinkedPath (Join-Path $rootPath $Name) $false
    $json = ConvertTo-Json -InputObject $Value -Depth 12 -Compress
    $bytes = (New-Object Text.UTF8Encoding($false, $true)).GetBytes($json)
    if ($bytes.Length -gt 16777216) { throw 'Private world evidence exceeds limit.' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    return $path
}
