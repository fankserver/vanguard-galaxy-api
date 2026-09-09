$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-paths.ps1')
$root = Join-Path $env:TEMP ('vg-world-paths-' + [Guid]::NewGuid().ToString('N'))
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Unsafe world path accepted.' } }
try {
    $null = New-Item -ItemType Directory $root
    $target = Join-Path $root 'target'; $null = New-Item -ItemType Directory $target
    [IO.File]::WriteAllText((Join-Path $target 'receipt.txt'), 'fixture')
    $null = Assert-WorldUnlinkedPath (Join-Path $target 'receipt.txt')
    Reject { Assert-WorldUnlinkedPath (Join-Path $root 'missing') }
    $null = Assert-WorldUnlinkedPath (Join-Path $root 'missing') $false
    $link = Join-Path $root 'link'
    $null = New-Item -ItemType Junction -Path $link -Target $target
    Reject { Assert-WorldUnlinkedPath $link }
    Reject { Assert-WorldUnlinkedPath (Join-Path $link 'receipt.txt') }
    Reject { Assert-WorldUnlinkedPath (Join-Path $link 'not-created') $false }
    Remove-Item -LiteralPath (Join-Path $target 'receipt.txt')
    [IO.Directory]::Delete($target, $false)
    Reject { Assert-WorldUnlinkedPath $link $false }
    Reject { Assert-WorldUnlinkedPath (Join-Path $link 'not-created') $false }
    Reject { Assert-WorldSandboxRoot $root }
    $parent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp'
    foreach ($name in @('VGModAPI-qa-0', 'VGModAPI-qa--1', 'VGModAPI-qa-2147483648', 'VGModAPI-qa-x')) {
        Reject { Assert-WorldSandboxRoot (Join-Path $parent $name) $false }
    }
    $null = Assert-WorldSandboxRoot (Join-Path $parent 'VGModAPI-qa-2147483646') $false
    $rootFile = Join-Path $parent ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
    $stream = [IO.File]::Open($rootFile, [IO.FileMode]::CreateNew); $stream.Dispose()
    try { Reject { Assert-WorldSandboxRoot $rootFile }; Reject { Assert-WorldSandboxRoot $rootFile $false } }
    finally { [IO.File]::Delete($rootFile) }
    Write-Output 'World path checks passed (no sandbox or game launch).'
} finally {
    $link = Join-Path $root 'link'
    if ($null -ne (Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue)) { [IO.Directory]::Delete($link, $false) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
