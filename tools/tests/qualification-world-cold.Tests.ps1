$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-cold.ps1')
function Reject([scriptblock]$Action) { $failed=$false; try { & $Action } catch { $failed=$true }; if (!$failed) { throw 'Invalid cold provenance accepted.' } }
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$created=$false
try {
    $null = New-Item -ItemType Directory $root; $created=$true
    $null = New-Item -ItemType Directory (Join-Path $root 'creation-evidence')
    $generation = Join-Path $root 'world-created-generation.txt'; [IO.File]::WriteAllText($generation, 'synthetic generation')
    $run=[Guid]::NewGuid(); $prior=[Guid]::NewGuid(); $head='a'*40
    $record=@{ schema='world-empty-phase-v1'; root=$root; runId=$prior.ToString('D'); phase='create'; reviewedHead=$head;
        approvalSha256=('b'*64); pid=42; startedUtc='2026-01-01T00:00:00.0000000Z'; generationReceiptSha256=(Get-FileHash $generation -Algorithm SHA256).Hash.ToLowerInvariant() }
    $path=Join-Path $root 'creation-evidence\world-phase-accepted.json'
    [IO.File]::WriteAllText($path, ($record | ConvertTo-Json))
    $digest=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $accepted=Read-WorldCreationEvidence $root $run $head $digest
    Reject { Read-WorldCreationEvidence $root $prior $head $digest }
    Reject { Read-WorldCreationEvidence $root $run ('c'*40) $digest }
    Reject { Read-WorldCreationEvidence $root $run $head ('d'*64) }
    Reject { Assert-WorldColdProcess $accepted @{pid=42; startedUtc=$record.startedUtc} }
    Assert-WorldColdProcess $accepted @{pid=42; startedUtc='2026-01-01T00:01:00.0000000Z'}
    [IO.File]::WriteAllText($generation, 'changed generation')
    Reject { Read-WorldCreationEvidence $root $run $head $digest }
    [IO.File]::WriteAllText($generation, 'synthetic generation')
    [IO.File]::WriteAllText($path, ('[' + ($record | ConvertTo-Json) + ']'))
    $arrayDigest=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Read-WorldCreationEvidence $root $run $head $arrayDigest }
    Write-Output 'Cold provenance tests passed with synthetic receipts; no process started.'
} finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
