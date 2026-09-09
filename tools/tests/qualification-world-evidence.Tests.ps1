$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-evidence.ps1')
function Reject([scriptblock]$Action) { $failed=$false; try { & $Action } catch { $failed=$true }; if (!$failed) { throw 'Unsafe evidence write accepted.' } }
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$created = $false
try {
    $null = New-Item -ItemType Directory $root; $created = $true
    $value = @{ schema='fixture'; text=('Unicode ' + [char]0x03BB); nested=@{ present=$false } }
    $path = Write-WorldPrivateEvidence $root 'world-launch-before.json' $value
    $decoded = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($decoded.text -cne $value.text -or $decoded.nested.present -ne $false) { throw 'Evidence changed during serialization.' }
    $before = (Get-FileHash $path).Hash
    Reject { Write-WorldPrivateEvidence $root 'world-launch-before.json' @{ replaced=$true } }
    if ((Get-FileHash $path).Hash -cne $before) { throw 'Existing evidence overwritten.' }
    Reject { Write-WorldPrivateEvidence $root '..\escape.json' $value }
    Reject { Write-WorldPrivateEvidence $root 'world-launch-after.json' ('x' * 16777217) }
    if (Test-Path (Join-Path $root 'world-launch-after.json')) { throw 'Oversize evidence created a file.' }
    Write-Output 'World private-evidence tests passed with synthetic content only.'
} finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
