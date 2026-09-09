$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-receipts.ps1')
$root = Join-Path $env:TEMP ('vg-world-receipts-' + [Guid]::NewGuid().ToString('N'))
function Reject([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (!$failed) { throw 'Invalid world receipt accepted.' }
}
try {
    $null = New-Item -ItemType Directory (Join-Path $root 'Saves') -Force
    [IO.File]::WriteAllText((Join-Path $root 'result.txt'), 'PASS')
    Reject { Assert-WorldPhaseReceipt $root 'create' }
    $save = Join-Path $root 'Saves\qa-owned-world.save'
    [IO.File]::WriteAllText($save, 'native-fixture')
    $idA = 'vgmodapi.world.v1.' + ('a' * 64); $idB = 'vgmodapi.world.v1.' + ('b' * 64)
    $generation = @('PAIRED-WORLD-GENERATION', [IO.Path]::GetFullPath($save), (Get-FileHash $save -Algorithm SHA256).Hash.ToLowerInvariant(), ('c' * 64), [Guid]::NewGuid().ToString('D'), $idA, $idB)
    $created = Join-Path $root 'world-created-generation.txt'
    [IO.File]::WriteAllLines($created, $generation)
    $phase = Join-Path $root 'owned-world.txt'
    $note = 'Phase evidence only; paired commit verification, ordered references and control matrix remain separate requirements.'
    [IO.File]::WriteAllLines($phase, @('PUBLIC-CREATE-NATIVE-MEMBERSHIP-SAVE', $idA, $idB, $note))
    Assert-WorldPhaseReceipt $root 'create'
    Reject { Assert-WorldPhaseReceipt $root 'cold' }
    [IO.File]::WriteAllLines($phase, @('COLD-LOOKUP-NATIVE-MEMBERSHIP', $idA, $idB, $note))
    Reject { Assert-WorldPhaseReceipt $root 'cold' }
    $cold = Join-Path $root 'world-cold-generation.txt'
    [IO.File]::WriteAllLines($cold, $generation)
    Assert-WorldPhaseReceipt $root 'cold'
    $changed = $generation.Clone(); $changed[4] = [Guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllLines($cold, $changed)
    Reject { Assert-WorldPhaseReceipt $root 'cold' }
    [IO.File]::WriteAllLines($phase, @('PUBLIC-CREATE-NATIVE-MEMBERSHIP-SAVE', $idA, $idB, $note))
    foreach ($case in @(@(1, ($save + '.other')), @(3, 'invalid'), @(4, [Guid]::Empty.ToString('D')), @(6, $idA))) {
        $bad = $generation.Clone(); $bad[$case[0]] = $case[1]
        [IO.File]::WriteAllLines($created, $bad)
        Reject { Assert-WorldGenerationReceipt $root 'world-created-generation.txt' }
    }
    [IO.File]::WriteAllLines($created, $generation)
    [IO.File]::WriteAllText($save, 'changed')
    Reject { Assert-WorldPhaseReceipt $root 'create' }
    [IO.File]::WriteAllText($created, ('x' * 4097))
    Reject { Assert-WorldGenerationReceipt $root 'world-created-generation.txt' }
    Write-Output 'World receipt checks passed (no game execution).'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
