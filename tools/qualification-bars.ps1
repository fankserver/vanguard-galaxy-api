# Controlled independent-author bar phase; does not claim consumer or cold-process qualification.
function Initialize-BarProbe([string]$Root, [string]$AuthorA, [string]$AuthorB) {
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    foreach ($author in @(@('OwnedBarAuthorA', $AuthorA), @('OwnedBarAuthorB', $AuthorB))) {
        $source = Join-Path $author[1] ($author[0] + '.dll')
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing bar author: $source" }
        Copy-Item -LiteralPath $source -Destination $plugins
    }
    [IO.File]::WriteAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'), "[Persistence]`r`nEnabled = true`r`nRoot = $(Join-Path $Root 'state')`r`n[Bars]`r`nEnabled = true`r`nExclusiveProviders = vg-bar-author-a,vg-bar-author-b`r`n")
    [IO.File]::WriteAllText((Join-Path $Root 'bars.enabled'), 'owned-bars-v1')
}

function Assert-BarReceipt([string]$Root) {
    $path = Join-Path $Root 'owned-bars.txt'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Missing owned-bar receipt.' }
    $rows = @(Get-Content -LiteralPath $path)
    if ($rows.Count -ne 2 -or $rows[0] -cne 'PASS' -or $rows[1] -cne 'independent-authors;repeated-open;native-json;exclusive-denial;exclusive-conflict;reload;stale-session;provider-reconstruction') {
        throw 'Incomplete or unexpected owned-bar receipt.'
    }
}
