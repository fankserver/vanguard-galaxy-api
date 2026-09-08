$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-bars.ps1')
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')
# The helper test never launches a process; make its process-presence check deterministic.
function Get-Process { param($Name, $ErrorAction) return $null }
$root = Join-Path $env:TEMP ('vg-bar-receipt-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'Invalid bar receipt accepted.' }
}
try {
    foreach ($name in @('qualification.ps1','qualification-inputs.ps1','qualification-bars.ps1')) {
        $tokens = $null; $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot ('..\' + $name)), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "Parse errors in $name : $errors" }
    }
    Reject { Assert-BarReceipt $root }
    Set-Content (Join-Path $root 'owned-bars.txt') @('INCOMPLETE')
    Reject { Assert-BarReceipt $root }
    $cases = 'independent-authors;repeated-check-update;ui-open;interaction;native-json;exclusive-denial;exclusive-conflict;reload;stale-session;stale-interaction;provider-reconstruction'
    Set-Content (Join-Path $root 'owned-bars.txt') @('PASS', $cases)
    Assert-BarReceipt $root
    Add-Content (Join-Path $root 'owned-bars.txt') 'unexpected'
    Reject { Assert-BarReceipt $root }
    Reject { Assert-BarColdReceipt $root 'absent' }
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Saves')
    $save = Join-Path $root 'Saves\qa-owned-bars.save'
    Set-Content $save 'producer'
    $producer = @('PASS', [IO.Path]::GetFullPath($save), (Get-FileHash $save).Hash.ToLowerInvariant(), ('c' * 64), [Guid]::NewGuid().ToString('D'))
    Set-Content (Join-Path $root 'bar-producer-generation.txt') $producer
    Set-Content $save 'absent'
    $committed = @('PASS', $producer[1], (Get-FileHash $save).Hash.ToLowerInvariant(), ('c' * 64), [Guid]::NewGuid().ToString('D'))
    Set-Content (Join-Path $root 'bar-absent-generation.txt') $committed
    Set-Content (Join-Path $root 'bar-cold-absent.txt') @('PASS','fresh-process;unregistered-providers;no-owned-presentation;same-save;fresh-commit')
    Assert-BarColdReceipt $root 'absent'
    Set-Content (Join-Path $root 'bar-absent-generation.txt') $producer
    Reject { Assert-BarColdReceipt $root 'absent' }
    Set-Content (Join-Path $root 'bar-absent-generation.txt') $committed
    Set-Content $save 'tampered'
    Reject { Assert-BarColdReceipt $root 'absent' }
    Reject { Assert-BarColdReceipt $root 'consumer' }
    $planned = [pscustomobject]@{barProbe=$true;barColdSequence=$true}
    Reject { Start-BarColdPhase $root $planned 'absent' }
    Set-Content (Join-Path $root 'owned-bars.txt') @('PASS', $cases)
    Set-Content (Join-Path $root 'bar-producer-finalized.txt') 'PASS'
    Set-Content (Join-Path $root 'result.txt') 'PASS'
    Set-Content (Join-Path $root 'run-outcome.json') '{"selfTerminated":true,"timedOut":false,"killed":false,"exitCode":-1}'
    Set-Content (Join-Path $root 'bar-cold-donor.txt') @('PASS',[Guid]::NewGuid().ToString('D'),'station')
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Saves'),(Join-Path $root 'game\BepInEx') -Force
    Set-Content $save 'fixture'
    $producer[2] = (Get-FileHash $save).Hash.ToLowerInvariant()
    Set-Content (Join-Path $root 'bar-producer-generation.txt') $producer
    Set-Content (Join-Path $root 'game\BepInEx\LogOutput.log') 'fixture'
    Reject { Start-BarColdPhase $root $planned 'consumer' }
    Start-BarColdPhase $root $planned 'absent'
    if (Test-Path (Join-Path $root 'result.txt')) { throw 'Old success result was not retired.' }
    if (!(Test-Path (Join-Path $root 'bar-producer-evidence\result.txt'))) { throw 'Producer evidence was not retained.' }
    Copy-Item (Join-Path $root 'bar-producer-evidence\result.txt') (Join-Path $root 'result.txt')
    Reject { Start-BarColdPhase $root $planned 'absent' }
    Write-Output 'PASS owned-bar receipt, cold sequencing and launcher parsing'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
