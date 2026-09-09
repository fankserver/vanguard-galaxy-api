. (Join-Path $PSScriptRoot 'qualification-world-approval.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-inputs.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-inventory.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-config.ps1')

function Get-WorldDataInventory([string]$Directory) {
    $base = (Assert-WorldUnlinkedPath $Directory).TrimEnd('\')
    if (!(Test-Path -LiteralPath $base -PathType Container)) { throw 'World data root must be a directory.' }
    $result = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    $pending = New-Object 'System.Collections.Generic.Stack[string]'; $pending.Push($base)
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if ($result.Count -ge 10000) { throw 'World data inventory exceeds limit.' }
            $relative = $entry.FullName.Substring($base.Length + 1).Replace('\', '/')
            if ($relative.Split('/').Count -gt 64 -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked or excessively nested world data.' }
            if ($entry.PSIsContainer) { $result.Add($relative, 'D'); $pending.Push($entry.FullName) }
            else { $result.Add($relative, 'F:' + (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) }
        }
    }
    return ,$result
}

function Convert-WorldApprovedInventory($Object) {
    $result = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($property in $Object.PSObject.Properties) { $result.Add($property.Name, $property.Value) }
    return ,$result
}

# Composes approved expectations with current observations without writing or launching anything.
function Assert-WorldRunPreflight([string]$Root, [Guid]$RunId, [ValidateSet('create','cold')][string]$Phase,
    [string]$ReviewedHead, [string]$ApprovalPath, [string]$ApprovalDigest) {
    $record = Read-WorldApprovedRun $ApprovalPath $ApprovalDigest $Root $RunId $Phase $ReviewedHead
    Assert-WorldConfiguration $Root
    $observed = Assert-WorldQualificationInputs $Root $RunId
    if ($observed.authorizationSha256 -cne $record.authorizationSha256) { throw 'Authorization file differs from approved run record.' }
    $conflicts = @(Get-ChildItem -LiteralPath $Root -Filter '*.enabled' -Force | Where-Object { $_.Name -cne 'world.enabled' })
    if ($conflicts.Count) { throw 'Conflicting sandbox phase markers.' }
    Assert-WorldLaunchInventory $Root $record.gameDirectory (Convert-WorldApprovedInventory $record.gameInventory)
    foreach ($pair in @(@('Saves', 'saveInventory'), @('state', 'stateInventory'))) {
        $expected = Convert-WorldApprovedInventory $record.($pair[1])
        $actual = Get-WorldDataInventory (Join-Path $Root $pair[0])
        if ($actual.Count -ne $expected.Count) { throw 'World data inventory set differs from approved input.' }
        foreach ($entry in $actual.GetEnumerator()) {
            if (!$expected.ContainsKey($entry.Key) -or $expected[$entry.Key] -cne $entry.Value) { throw 'World data differs from approved input.' }
        }
    }
    return $record
}
