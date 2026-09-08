$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-story-cold.ps1')
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')
function Get-Process { param($Name, $ErrorAction) }
function Assert-StoryReceipt { param($Root) }
function Refuses([scriptblock]$Action) { try { & $Action } catch { return }; throw 'Expected refusal.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-cold-test-' + [Guid]::NewGuid().ToString('N'))
try {
    $null = New-Item -ItemType Directory -Path $root
    Refuses { Start-StoryColdPhase $root ([pscustomobject]@{storyProbe=$true}) }
    $plan = [pscustomobject]@{storyProbe=$true;storyColdSequence=$true}
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'Saves'), (Join-Path $root 'game\BepInEx') -Force
    'PASS' | Set-Content (Join-Path $root 'result.txt')
    @{ selfTerminated=$true;killed=$false;timedOut=$true;exitCode=-1 } | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    Refuses { Start-StoryColdPhase $root $plan }
    @{ selfTerminated=$true;killed=$false;timedOut=$false;exitCode=-1 } | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    @('PASS','author','faction','poi',[Guid]::NewGuid().ToString('D'),[Guid]::NewGuid().ToString('D')) | Set-Content (Join-Path $root 'story-definition-donor.txt')
    'fixture' | Set-Content (Join-Path $root 'Saves\qa-story-definition-cold.save')
    'log' | Set-Content (Join-Path $root 'game\BepInEx\LogOutput.log')
    Refuses { Start-StoryColdPhase $root $plan }
    'PASS' | Set-Content (Join-Path $root 'story-producer-finalized.txt')
    'failure' | Set-Content (Join-Path $root 'playerprefs-restore-failed.txt')
    Refuses { Start-StoryColdPhase $root $plan }
    Remove-Item (Join-Path $root 'playerprefs-restore-failed.txt')
    Start-StoryColdPhase $root $plan
    if (Test-Path (Join-Path $root 'result.txt')) { throw 'Stale success survived.' }
    if (!(Test-Path (Join-Path $root 'story-producer-evidence\result.txt'))) { throw 'Producer evidence missing.' }
    'PASS' | Set-Content (Join-Path $root 'result.txt')
    Refuses { Start-StoryColdPhase $root $plan }
    @('PASS','incomplete') | Set-Content (Join-Path $root 'story-definition-cold.txt')
    Refuses { Assert-StoryColdReceipt $root }
    @('PASS','changed-startup;active;offered;retained-target;retained-amount;reload;normal-retirement') | Set-Content (Join-Path $root 'story-definition-cold.txt')
    Assert-StoryColdReceipt $root
    foreach ($bad in @(
        @{ selfTerminated=$true;killed=$false;timedOut=$false },
        @{ selfTerminated='true';killed=$false;timedOut=$false;exitCode=-1 },
        @{ selfTerminated=$true;killed=$true;timedOut=$false;exitCode=-1 },
        @{ selfTerminated=$true;killed=$false;timedOut=$true;exitCode=-1 },
        @{ selfTerminated=$true;killed=$false;timedOut=$false;exitCode=7 }
    )) {
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Refuses { Assert-StoryColdReceipt $root }
    }
    Write-Output 'PASS planned cold-phase guards, evidence preservation and strict receipt.'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
