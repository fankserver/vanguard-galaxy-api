$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-approval.ps1')
$root = Join-Path $env:TEMP ('world-approval-fixture-' + [Guid]::NewGuid().ToString('N'))
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Invalid approved record accepted.' } }
try {
    $null = New-Item -ItemType Directory $root
    $path = Join-Path $root 'record.json'; $run = [Guid]::NewGuid(); $head = 'a' * 40
    $environment = @{}; for ($index = 0; $index -lt 10; $index++) { $environment['key' + $index] = 'fixture' }
    $process = @{ fileName='fixture.exe'; workingDirectory=$root; arguments='fixture'; environment=$environment }
    $record = @{ timeoutSeconds=600; preservationRoots=@('C:\fixture\plugins','C:\fixture\config','C:\fixture\saves'); process=$process; schema='world-empty-run-v1'; root=$root; gameDirectory=$env:TEMP; runId=$run.ToString('D'); phase='create'; reviewedHead=$head; authorizationSha256=('b' * 64); gameInventory=@{ 'input.dll'=('F:' + ('c' * 64)) }; saveInventory=@{ 'fixture-a.save'=('F:' + ('d' * 64)) }; stateInventory=@{} }
    [IO.File]::WriteAllText($path, ($record | ConvertTo-Json -Depth 4))
    $approved = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = Read-WorldApprovedRun $path $approved $root $run 'create' $head
    Reject { Read-WorldApprovedRun $path ('e' * 64) $root $run 'create' $head }
    Reject { Read-WorldApprovedRun $path $approved ($root + '-other') $run 'create' $head }
    Reject { Read-WorldApprovedRun $path $approved $root ([Guid]::NewGuid()) 'create' $head }
    Reject { Read-WorldApprovedRun $path $approved $root $run 'cold' $head }
    Reject { Read-WorldApprovedRun $path $approved $root $run 'create' ('f' * 40) }
    $objectJson = $record | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($path, ('[' + $objectJson + ']'))
    $arrayDigest = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Read-WorldApprovedRun $path $arrayDigest $root $run 'create' $head }
    [IO.File]::WriteAllText($path, (" `t`r`n" + $objectJson))
    $whitespaceDigest = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = Read-WorldApprovedRun $path $whitespaceDigest $root $run 'create' $head
    $record.extra = 'unexpected'; [IO.File]::WriteAllText($path, ($record | ConvertTo-Json -Depth 4))
    Reject { Read-WorldApprovedRun $path $approved $root $run 'create' $head }
    $changedHash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Read-WorldApprovedRun $path $changedHash $root $run 'create' $head }
    [IO.File]::WriteAllBytes($path, (New-Object byte[] 4194305))
    Reject { Read-WorldApprovedRun $path $approved $root $run 'create' $head }
    Write-Output 'Approved-record binding tests passed with synthetic data; no authorization issued or game launched.'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
