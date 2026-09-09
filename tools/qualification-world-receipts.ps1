# Read-only phase receipt checks. These do not authorize execution or establish process provenance.
function Read-WorldReceipt([string]$Path) {
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer -or $file.Length -gt 4096 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid world receipt file.' }
    return ,([IO.File]::ReadAllLines($file.FullName))
}

function Assert-WorldGenerationReceipt([string]$Root, [string]$Name) {
    $rows = Read-WorldReceipt (Join-Path $Root $Name)
    $save = [IO.Path]::GetFullPath((Join-Path $Root 'Saves\qa-owned-world.save'))
    if ($rows.Count -ne 7 -or $rows[0] -cne 'PAIRED-WORLD-GENERATION' -or $rows[1] -ine $save -or
        $rows[2] -cnotmatch '^[0-9a-f]{64}$' -or $rows[3] -cnotmatch '^[0-9a-f]{64}$' -or
        $rows[5] -cnotmatch '^vgmodapi\.world\.v1\.[0-9a-f]{64}$' -or $rows[6] -cnotmatch '^vgmodapi\.world\.v1\.[0-9a-f]{64}$' -or $rows[5] -ceq $rows[6]) { throw 'Invalid paired world generation receipt.' }
    if ([Guid]::ParseExact($rows[4], 'D') -eq [Guid]::Empty) { throw 'Empty world snapshot identity.' }
    if ((Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash.ToLowerInvariant() -cne $rows[2]) { throw 'Native save no longer matches world receipt.' }
    return ,$rows
}

function Assert-WorldPhaseReceipt([string]$Root, [ValidateSet('create','cold')][string]$Phase) {
    $rows = Read-WorldReceipt (Join-Path $Root 'owned-world.txt')
    $expected = if ($Phase -eq 'create') { 'PUBLIC-CREATE-NATIVE-MEMBERSHIP-SAVE' } else { 'COLD-LOOKUP-NATIVE-MEMBERSHIP' }
    if ($rows.Count -ne 4 -or $rows[0] -cne $expected -or $rows[3] -cne 'Phase evidence only; paired commit verification, ordered references and control matrix remain separate requirements.') { throw 'Missing or incorrect world phase receipt.' }
    $created = Assert-WorldGenerationReceipt $Root 'world-created-generation.txt'
    if ($rows[1] -cne $created[5] -or $rows[2] -cne $created[6]) { throw 'Phase world identities differ from creation.' }
    if ($Phase -eq 'cold') {
        $cold = Assert-WorldGenerationReceipt $Root 'world-cold-generation.txt'
        for ($index = 0; $index -lt 7; $index++) { if ($cold[$index] -cne $created[$index]) { throw 'Cold generation differs from creation.' } }
    }
}
