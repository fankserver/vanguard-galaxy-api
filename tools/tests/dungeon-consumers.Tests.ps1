$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-dungeon-consumers.ps1')
function Reject($action) { $bad=$false; try { & $action } catch { $bad=$true }; if (!$bad) { throw 'Invalid consumer input accepted.' } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-consumer-synthetic-' + [guid]::NewGuid())
$config = Join-Path $root 'game/BepInEx/config'; $plugins = Join-Path $root 'game/BepInEx/plugins'
New-Item -ItemType Directory -Path $config,$plugins -Force | Out-Null
try {
    $m = @{ schema=1; rewardItemId='Titanium Plate'; binaries=@() }
    foreach ($name in $DungeonConsumerNames) {
        $path = Join-Path $plugins $name; [IO.File]::WriteAllBytes($path, [byte[]]@(1,2,3))
        $m.binaries += @{name=$name;path=$path;sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant();revision=('a'*40)}
    }
    $sources = Join-Path $root 'dungeon-consumer-sources.json'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    $p = [pscustomobject]@{scenario='Full';dungeonConsumersProbe=$true;dungeonPanelProbe=$true;dungeonReadinessProbe=$true;dungeonConsumerManifestHash=(Get-FileHash $sources -Algorithm SHA256).Hash.ToLowerInvariant()}
    Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-consumers.enabled'), 'dungeon-consumers-v1')
    [IO.File]::WriteAllText((Join-Path $config 'vg.boardalways.cfg'), "[General]`r`nEnabled = true`r`nDifficultyModifier = 2`r`nIntegrityDamageMultiplier = 1.5`r`n")
    [IO.File]::WriteAllText((Join-Path $config 'vgmodapi.example.cargo.cfg'), "[Content]`r`nRewardItemId = Titanium Plate`r`n")
    Assert-DungeonConsumerSelection $root $p
    $p.dungeonConsumersProbe=$false; Reject { Assert-DungeonConsumerSelection $root $p }; $p.dungeonConsumersProbe=$true
    $p.dungeonPanelProbe=$false; Reject { Assert-DungeonConsumerSelection $root $p }; $p.dungeonPanelProbe=$true
    [IO.File]::AppendAllText($sources, ' '); Reject { Assert-DungeonConsumerSelection $root $p }
    $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    [IO.File]::WriteAllBytes((Join-Path $plugins 'VGBBoardAlways.dll'), [byte[]]@(4)); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllBytes((Join-Path $plugins 'VGBBoardAlways.dll'), [byte[]]@(1,2,3))
    [IO.File]::AppendAllText((Join-Path $config 'vgmodapi.example.cargo.cfg'), 'RewardItemId = wrong'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $config 'vgmodapi.example.cargo.cfg'), "[Content]`r`nRewardItemId = Titanium Plate`r`n")
    Assert-DungeonConsumerSelection $root $p
    Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v1','attach-then-duplicate'))
    $log = @('Loading [Board Always 0.4.0]','Loading [Cargo recovery example 0.1.0]','Board Always v0.4.0 registered public boarding policies.','Cargo attach: Attached','Cargo attach: TargetInUse')
    [IO.File]::WriteAllLines((Join-Path $root 'Player.log'), $log[0..3]); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'Player.log'), $log); Assert-DungeonConsumerReceipt $root $p
    [IO.File]::AppendAllText((Join-Path $root 'Player.log'), 'Cargo attach: Attached'); Reject { Assert-DungeonConsumerReceipt $root $p }
    'PASS dungeon consumer manifest, selection, configuration and result gates (synthetic only)'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
