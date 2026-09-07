$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')
. (Join-Path $PSScriptRoot '..\qualification-story.ps1')
$root = Join-Path $env:TEMP ('vg-story-receipt-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'Invalid story evidence was accepted.' }
}
try {
    $cases = @('independent-authors','offered-roundtrip','active-roundtrip','native-completion','save-refusals','older-save-rollback','cross-slot-return','repeat-job','provider-unregistered','provider-unregistered-reload')
    $cases | Set-Content (Join-Path $root 'story-cases.txt')
    @('PASS','owned-story-v1') | Set-Content (Join-Path $root 'story-result.txt')
    $outcome = @{ timedOut=$false; killed=$false; selfTerminated=$true; exitCode=0 }
    $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    Assert-StoryReceipt $root
    foreach ($code in @(-1,0)) {
        $outcome.exitCode=$code
        $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Assert-StoryReceipt $root
    }
    foreach ($field in @('timedOut','killed','selfTerminated','exitCode')) {
        $bad=$outcome.Clone(); $bad.Remove($field)
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Reject { Assert-StoryReceipt $root }
    }
    foreach ($pair in @(@('timedOut',$true),@('killed',$true),@('selfTerminated',$false),@('exitCode',3),@('exitCode',$null),@('timedOut','false'))) {
        $bad=$outcome.Clone(); $bad[$pair[0]]=$pair[1]
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Reject { Assert-StoryReceipt $root }
    }
    $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    for ($i=0; $i -lt $cases.Count; $i++) {
        @($cases | Where-Object { $_ -ne $cases[$i] }) | Set-Content (Join-Path $root 'story-cases.txt')
        Reject { Assert-StoryReceipt $root }
    }
    Write-Output 'PASS: story receipt omissions and abnormal exit rejection.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
