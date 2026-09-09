. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

function Read-WorldCreationEvidence([string]$Root, [Guid]$RunId, [string]$ReviewedHead, [string]$ExpectedDigest) {
    if ($ExpectedDigest -cnotmatch '^[0-9a-f]{64}$') { throw 'Cold phase requires an externally approved creation-evidence digest.' }
    $rootPath = Assert-WorldSandboxRoot $Root
    $path = Assert-WorldUnlinkedPath (Join-Path $rootPath 'creation-evidence\world-phase-accepted.json')
    $stream = [IO.File]::OpenRead($path); $memory = New-Object IO.MemoryStream
    try {
        $buffer = New-Object byte[] 8192
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $count -gt 65536) { throw 'Creation evidence exceeds limit.' }
            $memory.Write($buffer, 0, $count)
        }
        $bytes = $memory.ToArray()
    } finally { $memory.Dispose(); $stream.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($digest -cne $ExpectedDigest) { throw 'Creation evidence differs from approved digest.' }
    $json = (New-Object Text.UTF8Encoding($false, $true)).GetString($bytes)
    if (!$json.TrimStart([char[]]@(' ', "`t", "`r", "`n")).StartsWith('{', [StringComparison]::Ordinal)) { throw 'Creation evidence must be an object.' }
    $record = $json | ConvertFrom-Json
    $names = @('schema','root','runId','phase','reviewedHead','approvalSha256','pid','startedUtc','generationReceiptSha256')
    if (@($record.PSObject.Properties).Count -ne $names.Count -or @($record.PSObject.Properties | Where-Object { $_.Name -cnotin $names }).Count) { throw 'Invalid creation evidence schema.' }
    foreach ($name in $names) { if ($name -ne 'pid' -and $record.$name -isnot [string]) { throw 'Invalid creation evidence field.' } }
    if ($record.schema -cne 'world-empty-phase-v1' -or $record.phase -cne 'create' -or $record.reviewedHead -cne $ReviewedHead -or
        ![IO.Path]::IsPathRooted($record.root) -or [IO.Path]::GetFullPath($record.root).TrimEnd('\') -ine $rootPath -or
        $record.approvalSha256 -cnotmatch '^[0-9a-f]{64}$' -or $record.generationReceiptSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        ($record.pid -isnot [int] -and $record.pid -isnot [long]) -or $record.pid -le 0) { throw 'Creation evidence scope mismatch.' }
    $previous = [Guid]::ParseExact($record.runId, 'D')
    if ($previous -eq [Guid]::Empty -or $previous -eq $RunId) { throw 'Cold phase requires a different run identity.' }
    $started = [DateTime]::ParseExact($record.startedUtc, 'O', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    if ($started.Kind -ne [DateTimeKind]::Utc) { throw 'Creation process timestamp must be UTC.' }
    $generation = Assert-WorldUnlinkedPath (Join-Path $rootPath 'world-created-generation.txt')
    if ((Get-FileHash -LiteralPath $generation -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.generationReceiptSha256) { throw 'Creation generation receipt changed.' }
    return $record
}

function Assert-WorldColdProcess($Creation, $Outcome) {
    if ($Creation.pid -eq $Outcome.pid -and $Creation.startedUtc -ceq $Outcome.startedUtc) { throw 'Cold phase reused creation process identity.' }
}
