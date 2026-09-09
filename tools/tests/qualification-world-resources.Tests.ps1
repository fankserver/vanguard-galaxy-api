$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-resources.ps1')
$parent = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')
$root = Join-Path $parent ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$source = Join-Path $parent ('world-resource-fixture-' + [Guid]::NewGuid().ToString('N'))
$created = $false
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Invalid resource target accepted.' } }
try {
    if (Test-Path $root) { throw 'Test root exists.' }
    $null = New-Item -ItemType Directory $root; $created = $true
    $game = Join-Path $root 'game'; $null = New-Item -ItemType Directory $game
    foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge')) { $null = New-Item -ItemType Directory (Join-Path $source $name) -Force }
    Reject { Assert-WorldResourceTargets $root $source }
    foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge')) { $null = New-Item -ItemType Junction -Path (Join-Path $game $name) -Target (Join-Path $source $name) }
    Assert-WorldResourceTargets $root $source
    Reject { Assert-WorldResourceTargets $root $game }
    $data = Join-Path $game 'VanguardGalaxy_Data'
    [IO.Directory]::Delete($data, $false)
    $null = New-Item -ItemType Junction -Path $data -Target (Join-Path $source 'MonoBleedingEdge')
    Reject { Assert-WorldResourceTargets $root $source }
    [IO.Directory]::Delete($data, $false)
    $null = New-Item -ItemType Directory $data
    Reject { Assert-WorldResourceTargets $root $source }
    [IO.Directory]::Delete($data, $false)
    $null = New-Item -ItemType Junction -Path $data -Target (Join-Path $source 'VanguardGalaxy_Data')
    $null = New-Item -ItemType Directory (Join-Path $source 'D3D12')
    Reject { Assert-WorldResourceTargets $root $source }
    $null = New-Item -ItemType Junction -Path (Join-Path $game 'D3D12') -Target (Join-Path $source 'D3D12')
    Assert-WorldResourceTargets $root $source
    Write-Output 'Resource-target tests passed using owned empty directories; no game launched.'
} finally {
    if ($created) {
        foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge', 'D3D12')) {
            $path = Join-Path (Join-Path $root 'game') $name
            $entry = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($path, $false) }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if (Test-Path $source) { Remove-Item -LiteralPath $source -Recurse -Force }
}
