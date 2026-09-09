$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-profile.ps1')
. (Join-Path $PSScriptRoot '../qualification-inputs.ps1')
function Reject($action) { $rejected=$false; try { & $action } catch { $rejected=$true }; if (!$rejected) { throw 'Invalid dungeon evidence accepted.' } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-dungeon-synthetic-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $root 'game/BepInEx/config') -Force | Out-Null
try {
    $p = [pscustomobject]@{dungeonReadinessProbe=$true;scenario='Full';forgeReadProbe=$false;assemblyOverlay=$null}
    Reject { Assert-DungeonReadinessSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-readiness.enabled'), 'dungeon-readiness-v1')
    $config = Join-Path $root 'game/BepInEx/config/vgmodapi.cfg'
    [IO.File]::WriteAllText($config, "[Boarding]`r`nEnabled = true`r`n[Dungeons]`r`nEnabled = true`r`n")
    Assert-DungeonReadinessSelection $root $p
    [IO.File]::WriteAllText($config, "[Boarding]`r`nEnabled = false`r`n[Dungeons]`r`nEnabled = true`r`n")
    Reject { Assert-DungeonReadinessSelection $root $p }
    [IO.File]::WriteAllText($config, "[Boarding]`r`nEnabled = true`r`n[Dungeons]`r`nEnabled = true`r`n")
    $p.forgeReadProbe=$true; Reject { Assert-DungeonReadinessSelection $root $p }; $p.forgeReadProbe=$false
    $p.dungeonReadinessProbe='true'; Reject { Assert-DungeonReadinessSelection $root $p }; $p.dungeonReadinessProbe=$true
    $p.scenario='MissingApi'; Reject { Assert-DungeonReadinessSelection $root $p }; $p.scenario='Full'
    Reject { Assert-DungeonReadinessReceipt $root $p }
    @{timedOut=$false;killed=$false;exitCode=0} | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    $facts=Join-Path $root 'dungeon-readiness.txt'; $receipt=Join-Path $root 'dungeon-readiness.receipt'
    [IO.File]::WriteAllLines($facts, @('PASS','dungeon-readiness-v1','targets=0','operations=0'))
    $hash=(Get-FileHash $facts -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v1',"sha256=$hash"))
    Assert-DungeonReadinessReceipt $root $p
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v0',"sha256=$hash"))
    Reject { Assert-DungeonReadinessReceipt $root $p }
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v1',"sha256=$hash",'extra'))
    Reject { Assert-DungeonReadinessReceipt $root $p }
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v1',"sha256=$hash"))
    [IO.File]::WriteAllLines($facts, @('PASS','dungeon-readiness-v1','targets=0','operations=1'))
    $nonemptyHash=(Get-FileHash $facts -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v1',"sha256=$nonemptyHash"))
    Reject { Assert-DungeonReadinessReceipt $root $p }
    [IO.File]::WriteAllLines($facts, @('PASS','dungeon-readiness-v1','targets=0','operations=0'))
    [IO.File]::WriteAllLines($receipt, @('PASS','dungeon-readiness-v1',"sha256=$hash"))
    [IO.File]::AppendAllText($facts, 'changed'); Reject { Assert-DungeonReadinessReceipt $root $p }
    [IO.File]::WriteAllLines($facts, @('PASS','dungeon-readiness-v1','targets=0','operations=0'))
    @{timedOut=$true;killed=$true;exitCode=0} | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    Reject { Assert-DungeonReadinessReceipt $root $p }
    $p.dungeonReadinessProbe=$false; Reject { Assert-DungeonReadinessSelection $root $p }; $p.dungeonReadinessProbe=$true
    @{timedOut=$false;killed=$false;exitCode=0} | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    $p | Add-Member dungeonPanelProbe $true
    Reject { Assert-DungeonReadinessSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-panel.enabled'), 'dungeon-panel-v1')
    Assert-DungeonReadinessSelection $root $p
    Reject { Assert-DungeonReadinessReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-panel.txt'), @('PASS','dungeon-panel-v2','generated-location-pointer-disabled-revalidate-contributors-dispose-stale-reopen-destroy'))
    Reject { Assert-DungeonReadinessReceipt $root $p } # missing image
    $image = Join-Path $root 'dungeon-panel-actions.png'; $record = Join-Path $root 'dungeon-panel-actions.txt'
    [IO.File]::WriteAllBytes($image, [byte[]]@(1,2,3))
    [IO.File]::WriteAllText($record, 'sha256=' + (Get-FileHash $image -Algorithm SHA256).Hash.ToLowerInvariant())
    Assert-DungeonReadinessReceipt $root $p
    [IO.File]::WriteAllBytes($image, [byte[]]@(1,2,4))
    Reject { Assert-DungeonReadinessReceipt $root $p } # tampered image
    [IO.File]::WriteAllBytes($image, [byte[]]@(1,2,3))
    Assert-DungeonReadinessReceipt $root $p
    $p.dungeonReadinessProbe=$false; Reject { Assert-DungeonReadinessSelection $root $p }; $p.dungeonReadinessProbe=$true
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-panel.txt'), 'INCOMPLETE'); Reject { Assert-DungeonReadinessReceipt $root $p }
    $p.dungeonPanelProbe=$false; Reject { Assert-DungeonReadinessSelection $root $p }
    'PASS dungeon readiness selection and bounded receipts (synthetic only)'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
