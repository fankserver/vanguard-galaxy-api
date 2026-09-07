$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-profile.ps1')
. (Join-Path $PSScriptRoot '../qualification-inputs.ps1')
function Reject($action, $label) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    if (!$rejected) { throw "Accepted invalid case: $label" }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-menu-synthetic-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $p = [pscustomobject]@{ scenario='Full'; modMenuProbe=$true; menuInspection=$false; missionJournal=$false }
    Reject { Assert-ModMenuProbeSelection $root $p } 'missing marker'
    [IO.File]::WriteAllText((Join-Path $root 'mod-menu-probe.enabled'), 'mod-menu-probe-v2')
    Assert-ModMenuProbeSelection $root $p
    $p.modMenuProbe = 'true'
    Reject { Assert-ModMenuProbeSelection $root $p } 'string boolean'
    $p.modMenuProbe = $false
    Reject { Assert-ModMenuProbeSelection $root $p } 'unselected marker'
    $p.modMenuProbe = $true
    $p.scenario = 'MissingApi'
    Reject { Assert-ModMenuProbeSelection $root $p } 'wrong scenario'
    $p.scenario = 'Full'; $p.missionJournal = $true
    Reject { Assert-ModMenuProbeSelection $root $p } 'consumer conflict'
    foreach ($invalid in @('true', 1, 'false', 0)) {
        $p.missionJournal = $invalid
        Reject { Assert-ModMenuProbeSelection $root $p } 'malformed conflicting selection'
    }
    $p.missionJournal = $false
    Reject { Assert-ModMenuProbeReceipt $root $p } 'missing outcome'
    $outcome = Join-Path $root 'run-outcome.json'
    $snapshot = Join-Path $root 'mod-menu-probe.txt'
    $receipt = Join-Path $root 'mod-menu-probe.receipt'
    $text = "synthetic evidence, not a native pass`n"
    foreach ($name in @('mod-menu-original.png','mod-menu-1280.png')) {
        $image = Join-Path $root $name
        [IO.File]::WriteAllText($image, 'synthetic image placeholder, not a screenshot')
        $imageHash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
        $text += "screenshot=$name resolution=1280x720 sha256=$imageHash`n"
    }
    [IO.File]::WriteAllText($snapshot, $text)
    $hash = (Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($receipt, "PASS`nmod-menu-probe-v2`nsha256=$hash`n")
    foreach ($invalid in @(@{timedOut=$true;killed=$false;exitCode=0}, @{timedOut=$false;killed=$true;exitCode=0}, @{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1})) {
        $invalid | ConvertTo-Json | Set-Content -LiteralPath $outcome
        Reject { Assert-ModMenuProbeReceipt $root $p } 'abnormal exit'
    }
    foreach ($code in @(0,-1)) {
        @{timedOut=$false;killed=$false;exitCode=$code} | ConvertTo-Json | Set-Content -LiteralPath $outcome
        Assert-ModMenuProbeReceipt $root $p
    }
    $image = Join-Path $root 'mod-menu-1280.png'
    [IO.File]::AppendAllText($image, ' changed')
    Reject { Assert-ModMenuProbeReceipt $root $p } 'changed screenshot'
    Remove-Item -LiteralPath $image
    Reject { Assert-ModMenuProbeReceipt $root $p } 'missing screenshot'
    [IO.File]::AppendAllText($snapshot, ' changed')
    Reject { Assert-ModMenuProbeReceipt $root $p } 'changed evidence'
    [IO.File]::WriteAllText($receipt, "PASS`n")
    Reject { Assert-ModMenuProbeReceipt $root $p } 'truncated receipt'
    Write-Host 'PASS: menu probe synthetic selection/receipt/exit rejection cases (no game launch).'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
