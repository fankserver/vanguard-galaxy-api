$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-preservation.ps1')
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Preservation drift accepted.' } }
$root = Join-Path $env:TEMP ('world-preservation-' + [Guid]::NewGuid().ToString('N'))
$created = $false
try {
    $null = New-Item -ItemType Directory $root; $created = $true
    $files = Join-Path $root 'files'; $absent = Join-Path $root 'absent'
    $null = New-Item -ItemType Directory $files
    $file = Join-Path $files 'fixture'; [IO.File]::WriteAllText($file, 'original')
    $roots = @($files, $absent)
    $before = Get-WorldPreservationSnapshot $roots
    Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots)
    [IO.File]::WriteAllText($file, 'changed')
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    [IO.File]::WriteAllText($file, 'original')
    $empty = Join-Path $files 'empty'; $null = New-Item -ItemType Directory $empty
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    Remove-Item $empty
    $null = New-Item -ItemType Directory $absent
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    Remove-Item $absent
    Reject { Get-WorldPreservationSnapshot @($files, $files.ToUpperInvariant()) }
    Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots)
    Write-Output 'World preservation tests passed on synthetic directories; no production files read or restored.'
} finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
