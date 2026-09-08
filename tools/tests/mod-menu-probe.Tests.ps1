$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-profile.ps1')
. (Join-Path $PSScriptRoot '../qualification-inputs.ps1')
function Reject($action, $label) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    if (!$rejected) { throw "Accepted invalid case: $label" }
}
$browserSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../qualification-browser.ps1'))
$titleMatch = [regex]::Match($browserSource, '(?m)^\$releaseTitlePattern = ''([^'']+)''')
if (!$titleMatch.Success) { throw 'Missing shared browser title pattern.' }
$titlePattern = $titleMatch.Groups[1].Value
foreach ($title in @('Releases - fankserver/vanguard-galaxy-api - Google Chrome', 'Releases - fankserver/vanguard-galaxy-api - GitHub - Google Chrome')) {
    if ($title -notmatch $titlePattern) { throw 'Rejected valid public release title variation.' }
}
foreach ($title in @('Settings - Google Chrome', 'Releases - unrelated/private', 'Releases - fankserver/vanguard-galaxy-api-private')) {
    if ($title -match $titlePattern) { throw 'Accepted unrelated browser title.' }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-menu-synthetic-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $p = [pscustomobject]@{ scenario='Full'; modMenuProbe=$true; menuInspection=$false; missionJournal=$false; storyProbe=$false }
    Reject { Assert-ModMenuProbeSelection $root $p } 'missing marker'
    [IO.File]::WriteAllText((Join-Path $root 'mod-menu-probe.enabled'), 'mod-menu-probe-v3')
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
    $p.missionJournal = $false
    foreach ($selection in @('missionJournal','storyProbe')) {
        foreach ($invalid in @('true', 1, 'false', 0, $true)) {
            $p.$selection = $invalid
            Reject { Assert-ModMenuProbeSelection $root $p } 'malformed conflicting selection'
        }
        $p.$selection = $false
    }
    Reject { Assert-ModMenuProbeReceipt $root $p } 'missing outcome'
    $outcome = Join-Path $root 'run-outcome.json'
    $snapshot = Join-Path $root 'mod-menu-probe.txt'
    $receipt = Join-Path $root 'mod-menu-probe.receipt'
    $text = "synthetic evidence, not a native pass`n"
    foreach ($name in @('mod-menu-original.png','mod-menu-1280.png','mod-update-disclosure.png','mod-menu-scale.png','mod-api-unavailable.png')) {
        $image = Join-Path $root $name
        [IO.File]::WriteAllText($image, 'synthetic image placeholder, not a screenshot')
        $imageHash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
        $text += "screenshot=$name resolution=1280x720 sha256=$imageHash`n"
    }
    [IO.File]::WriteAllText($snapshot, $text)
    $hash = (Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($receipt, "PASS`nmod-menu-probe-v3`nsha256=$hash`n")
    foreach ($invalid in @(@{timedOut=$true;killed=$false;exitCode=0}, @{timedOut=$false;killed=$true;exitCode=0}, @{timedOut=$false;killed=$false;exitCode=$null}, @{timedOut=$false;killed=$false;exitCode=1})) {
        $invalid | ConvertTo-Json | Set-Content -LiteralPath $outcome
        Reject { Assert-ModMenuProbeReceipt $root $p } 'abnormal exit'
    }
    foreach ($code in @(0,-1)) {
        @{timedOut=$false;killed=$false;exitCode=$code} | ConvertTo-Json | Set-Content -LiteralPath $outcome
        Assert-ModMenuProbeReceipt $root $p
    }
    [IO.File]::WriteAllText($receipt, "PASS`nmod-menu-probe-v2`nsha256=$hash`n")
    Reject { Assert-ModMenuProbeReceipt $root $p } 'legacy receipt cannot attest updated controls'
    [IO.File]::WriteAllText($receipt, "PASS`nmod-menu-probe-v3`nsha256=$hash`n")
    foreach ($name in @('mod-menu-1280.png','mod-update-disclosure.png')) {
        $image = Join-Path $root $name
        [IO.File]::AppendAllText($image, ' changed')
        Reject { Assert-ModMenuProbeReceipt $root $p } "changed screenshot $name"
        Remove-Item -LiteralPath $image
        Reject { Assert-ModMenuProbeReceipt $root $p } "missing screenshot $name"
        [IO.File]::WriteAllText($image, 'synthetic image placeholder, not a screenshot')
        Assert-ModMenuProbeReceipt $root $p
    }
    $p | Add-Member -NotePropertyName modInformationProbe -NotePropertyValue $true
    $p.missionJournal = $true
    $p | Add-Member -NotePropertyName stockpile -NotePropertyValue $true
    Reject { Assert-ModInformationProbeSelection $root $p } 'missing information marker'
    [IO.File]::WriteAllText((Join-Path $root 'mod-information-probe.enabled'), 'mod-information-probe-v1')
    Reject { Assert-ModInformationProbeSelection $root $p } 'legacy information mode lacks wire failures'
    [IO.File]::WriteAllText((Join-Path $root 'mod-information-probe.enabled'), 'mod-information-probe-v3')
    $certificate = Join-Path $root 'untrusted-test.pfx'
    [IO.File]::WriteAllText($certificate, 'synthetic hash fixture, not a certificate')
    $p | Add-Member -NotePropertyName modInformationCertificateSha256 -NotePropertyValue ((Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash.ToLowerInvariant())
    Assert-ModInformationProbeSelection $root $p
    [IO.File]::AppendAllText($certificate, ' changed')
    Reject { Assert-ModInformationProbeSelection $root $p } 'changed TLS fixture'
    [IO.File]::WriteAllText($certificate, 'synthetic hash fixture, not a certificate')
    $p.modInformationProbe = 'true'
    Reject { Assert-ModInformationProbeSelection $root $p } 'information string Boolean'
    $p.modInformationProbe = $true
    $infoSnapshot = Join-Path $root 'mod-information-probe.txt'
    $infoReceipt = Join-Path $root 'mod-information-probe.receipt'
    $facts = @('controlled-default-manual-coalescing-cooldown','controlled-six-hour-automatic-disable','controlled-dns-tls-timeout-retain-last-success','controlled-rate-limit','controlled-disk-cache-expiry-channel-installed-version','controlled-invalid-oversized-channel-redirect-policy','controlled-quit-mid-check','wire-platform-tls-parser-stable','wire-platform-tls-parser-experimental','wire-https-redirect','wire-invalid-oversized-channel-rejected','wire-dns-name-resolution-failure','wire-tls-untrusted-certificate-rejected','wire-stalled-handshake-canceled','unity-main-thread-menu-responsive','inventory-two-real-consumers-without-metadata','inventory-malformed-wrong-guid-isolated','gameplay-two-loads-return-single-entry','real-stockpile-ui-journal-coexistence','new-game-save-load-return','ui-confirmed-manual-without-automatic-browser','ui-confirmed-automatic-and-opt-out','ui-explicit-default-browser-release','ui-scale-restored','actual-api-unavailable-loader-presence')
    function Write-InformationEvidence($selectedFacts) {
        [IO.File]::WriteAllText($infoSnapshot, (($selectedFacts | ForEach-Object { "$_=PASS" }) -join "`n"))
        $digest = (Get-FileHash -LiteralPath $infoSnapshot -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($infoReceipt, "PASS`nmod-information-probe-v3`nsha256=$digest`n")
    }
    $browserImage = Join-Path $root 'mod-release-browser.png'
    $browserRequest = Join-Path $root 'browser-launch-request.txt'
    $browserReceipt = Join-Path $root 'browser-launch.receipt'
    [IO.File]::WriteAllText($browserImage, 'synthetic browser image, not runtime evidence')
    [IO.File]::WriteAllText($browserRequest, "mod-release-browser-v1`nhttps://github.com/fankserver/vanguard-galaxy-api/releases`n")
    $browserHash = (Get-FileHash -LiteralPath $browserImage -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($browserReceipt, "PASS`nmod-release-browser-v1`nurl=https://github.com/fankserver/vanguard-galaxy-api/releases`nsha256=$browserHash`n")
    Assert-ReleaseBrowserReceipt $root
    [IO.File]::AppendAllText($browserImage, ' changed')
    Reject { Assert-ReleaseBrowserReceipt $root } 'changed browser image'
    [IO.File]::WriteAllText($browserImage, 'synthetic browser image, not runtime evidence')
    [IO.File]::AppendAllText($browserRequest, ' changed')
    Reject { Assert-ReleaseBrowserReceipt $root } 'changed browser destination'
    [IO.File]::WriteAllText($browserRequest, "mod-release-browser-v1`nhttps://github.com/fankserver/vanguard-galaxy-api/releases`n")
    Write-InformationEvidence $facts
    Assert-ModInformationProbeReceipt $root $p
    foreach ($fact in $facts) {
        Write-InformationEvidence @($facts | Where-Object { $_ -ne $fact })
        Reject { Assert-ModInformationProbeReceipt $root $p } "missing fact $fact"
        Write-InformationEvidence @($facts + $fact)
        Reject { Assert-ModInformationProbeReceipt $root $p } "duplicate fact $fact"
    }
    Write-InformationEvidence $facts
    [IO.File]::AppendAllText($infoSnapshot, ' changed')
    Reject { Assert-ModInformationProbeReceipt $root $p } 'changed information evidence'
    Write-InformationEvidence $facts
    [IO.File]::AppendAllText($snapshot, ' changed')
    Reject { Assert-ModMenuProbeReceipt $root $p } 'changed evidence'
    [IO.File]::WriteAllText($receipt, "PASS`n")
    Reject { Assert-ModMenuProbeReceipt $root $p } 'truncated receipt'
    Write-Host 'PASS: menu probe synthetic selection/receipt/exit rejection cases (no game launch).'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
