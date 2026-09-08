$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-bars.ps1')
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
    Write-Output 'PASS owned-bar receipt and launcher parsing'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
