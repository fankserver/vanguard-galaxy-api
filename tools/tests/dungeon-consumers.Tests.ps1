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
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-consumers.enabled'), 'dungeon-consumers-v3')
    [IO.File]::WriteAllText((Join-Path $config 'vg.boardalways.cfg'), "[General]`r`nEnabled = true`r`nDifficultyModifier = 2`r`nIntegrityDamageMultiplier = 1.5`r`n")
    [IO.File]::WriteAllText((Join-Path $config 'vgmodapi.example.cargo.cfg'), "[Content]`r`nRewardItemId = Titanium Plate`r`n")
    Assert-DungeonConsumerSelection $root $p
    [IO.File]::WriteAllText((Join-Path $config 'vg.boardalways.cfg'), "[General]`r`n## BepInEx rewrite`r`nEnabled = true`r`nDifficultyModifier = 2.0`r`nIntegrityDamageMultiplier = 1.500`r`n")
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
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v3','attach-duplicate-commands-active-walk-retreat'))
    $walk = Join-Path $root 'dungeon-walk.txt'
    $walkLines = @('PASS','dungeon-walk-v1','manual-arrival-retreat-settlement','donor-crew-reconciled')
    [IO.File]::WriteAllLines($walk, $walkLines)
    $log = @('Loading [Board Always 0.4.0]','Loading [Cargo recovery example 0.1.0]','Board Always v0.4.0 registered public boarding policies.','Cargo attach: Attached','Cargo attach: TargetInUse')
    [IO.File]::WriteAllLines((Join-Path $root 'Player.log'), $log[0..3]); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'Player.log'), $log); Reject { Assert-DungeonConsumerReceipt $root $p }
    $commands = Join-Path $root 'dungeon-commands.txt'
    [IO.File]::WriteAllLines($commands, @('PASS','dungeon-commands-v1','control-start-options-refusals-cancel-before-tick','crew-and-docking-preserved'))
    Assert-DungeonConsumerReceipt $root $p
    [IO.File]::AppendAllText($commands, 'unexpected'); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($commands, @('PASS','dungeon-commands-v1','control-start-options-refusals-cancel-before-tick','crew-and-docking-preserved'))
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-consumers.enabled'), 'dungeon-consumers-v1'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-consumers.enabled'), 'dungeon-consumers-v2'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'dungeon-consumers.enabled'), 'dungeon-consumers-v3')
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v1','attach-then-duplicate')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v2','attach-then-duplicate-command-admission-cancel')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v3','attach-duplicate-commands-active-walk-retreat'))
    Remove-Item $walk; Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($walk, @('INCOMPLETE')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($walk, $walkLines); Assert-DungeonConsumerReceipt $root $p
    $m.mode = 'Invalid'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    Reject { Read-DungeonConsumerManifest $sources }
    $m.mode = 'Combat'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    $p.dungeonConsumerManifestHash = (Get-FileHash $sources -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Assert-DungeonConsumerSelection $root $p }
    $combatMarker = Join-Path $root 'dungeon-combat.enabled'
    [IO.File]::WriteAllText($combatMarker, 'dungeon-combat-v1')
    Assert-DungeonConsumerSelection $root $p
    Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v3','attach-duplicate-commands-active-combat'))
    Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($walk, @('PASS','dungeon-combat-v1','manual-victory-choice-extraction','donor-crew-reconciled'))
    Assert-DungeonConsumerReceipt $root $p
    [IO.File]::WriteAllText($combatMarker, 'wrong'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($combatMarker, 'dungeon-combat-v1')
    $m.mode = 'Reward'; $m.rewardItemId = 'Wrong item'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    Reject { Read-DungeonConsumerManifest $sources }
    $m.rewardItemId = 'Titanium Plate'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    $p.dungeonConsumerManifestHash = (Get-FileHash $sources -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Assert-DungeonConsumerSelection $root $p }
    $rewardMarker = Join-Path $root 'dungeon-reward.enabled'
    [IO.File]::WriteAllText($rewardMarker, 'dungeon-reward-v1'); Assert-DungeonConsumerSelection $root $p
    Reject { Assert-DungeonConsumerReceipt $root $p }
    $rewardReceipt = Join-Path $root 'dungeon-reward.txt'
    [IO.File]::WriteAllLines($rewardReceipt, @('INCOMPLETE')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($rewardReceipt, @('PASS','dungeon-reward-v1','authored-two-multiplied-four-cargo-delivered')); Assert-DungeonConsumerReceipt $root $p
    [IO.File]::WriteAllText($rewardMarker, 'wrong'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($rewardMarker, 'dungeon-reward-v1')
    Remove-Item $combatMarker,$rewardMarker
    $m.mode = 'SaveLoad'; $m | ConvertTo-Json -Depth 5 | Set-Content $sources
    $p.dungeonConsumerManifestHash = (Get-FileHash $sources -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Assert-DungeonConsumerSelection $root $p }
    $saveMarker = Join-Path $root 'dungeon-save.enabled'
    [IO.File]::WriteAllText($saveMarker, 'dungeon-save-v4'); Assert-DungeonConsumerSelection $root $p
    [IO.File]::WriteAllLines((Join-Path $root 'dungeon-consumers.txt'), @('INPUTS_SENT','dungeon-consumers-v3','attach-duplicate-commands-active-walk-retreat'))
    [IO.File]::WriteAllLines($walk, $walkLines)
    Reject { Assert-DungeonConsumerReceipt $root $p }
    $saveReceipt = Join-Path $root 'dungeon-save.txt'
    [IO.File]::WriteAllLines($saveReceipt, @('INCOMPLETE')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($saveReceipt, @('PASS','dungeon-save-v1','active-save-reload-occurrence-control-crew-return')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($saveReceipt, @('PASS','dungeon-save-v2','save-as-slot-switch-resolved-reload-older-checkpoint-crew-return')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($saveReceipt, @('PASS','dungeon-save-v3','save-failures-save-as-slot-switch-resolved-reload-in-place-rollback')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($saveReceipt, @('PASS','dungeon-save-v4','save-failures-transitions-rollback-provider-recovery')); Reject { Assert-DungeonConsumerReceipt $root $p }
    $faults = Join-Path $root 'dungeon-save-faults.txt'
    [IO.File]::WriteAllLines($faults, @('INCOMPLETE')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($faults, @('PASS','dungeon-save-faults-v1','skipped-and-failed-write-live-state-preserved')); Reject { Assert-DungeonConsumerReceipt $root $p }
    $providerReceipt = Join-Path $root 'dungeon-provider.txt'
    [IO.File]::WriteAllLines($providerReceipt, @('INCOMPLETE')); Reject { Assert-DungeonConsumerReceipt $root $p }
    [IO.File]::WriteAllLines($providerReceipt, @('PASS','dungeon-provider-v1','absent-registration-version-refusal-matching-behavior-once')); Assert-DungeonConsumerReceipt $root $p
    [IO.File]::WriteAllText($saveMarker, 'wrong'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($saveMarker, 'dungeon-save-v1'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($saveMarker, 'dungeon-save-v2'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($saveMarker, 'dungeon-save-v3'); Reject { Assert-DungeonConsumerSelection $root $p }
    [IO.File]::WriteAllText($saveMarker, 'dungeon-save-v4')
    [IO.File]::AppendAllText((Join-Path $root 'Player.log'), 'Cargo attach: Attached'); Reject { Assert-DungeonConsumerReceipt $root $p }
    'PASS dungeon consumer manifest, selection, configuration and result gates (synthetic only)'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
