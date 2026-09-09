$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-profile.ps1')
. (Join-Path $PSScriptRoot '../qualification-inputs.ps1')
function Reject($action) { $rejected = $false; try { & $action } catch { $rejected = $true }; if (!$rejected) { throw 'Invalid Forge evidence accepted.' } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-forge-synthetic-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $root 'game/BepInEx/config') -Force | Out-Null
try {
    $p = [pscustomobject]@{ forgeReadProbe=$true; scenario='Full'; modMenuProbe=$false }
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-reads.enabled'), 'forge-reads-v1')
    $config = Join-Path $root 'game/BepInEx/config/vgmodapi.cfg'
    [IO.File]::WriteAllText($config, "[Recipes]`r`n# generated comment`r`nEnabled = true`r`n")
    Assert-ForgeReadSelection $root $p
    $p.modMenuProbe = $true; Reject { Assert-ForgeReadSelection $root $p }; $p.modMenuProbe = $false
    $p.forgeReadProbe = 'true'; Reject { Assert-ForgeReadSelection $root $p }; $p.forgeReadProbe = $true
    [IO.File]::WriteAllText($config, "[Recipes]`r`nEnabled = false`r`n[Other]`r`nEnabled = true`r`n")
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText($config, "[Recipes]`r`nEnabled = true`r`n")
    Reject { Assert-ForgeReadReceipt $root $p }
    @{timedOut=$false;killed=$false;exitCode=0} | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    $facts = Join-Path $root 'forge-reads.txt'; $receipt = Join-Path $root 'forge-reads.receipt'
    [IO.File]::WriteAllLines($facts, @('PASS','forge-reads-v1','catalog=2','quotes=1','restored=0'))
    $hash = (Get-FileHash $facts -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllLines($receipt, @('PASS','forge-reads-v1',"sha256=$hash"))
    Assert-ForgeReadReceipt $root $p
    [IO.File]::AppendAllText($facts, 'tampered'); Reject { Assert-ForgeReadReceipt $root $p }
    [IO.File]::WriteAllLines($facts, @('PASS','forge-reads-v1','catalog=2','quotes=1','restored=0'))
    $p | Add-Member forgeCommandProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-commands.enabled'), 'forge-commands-v3')
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::AppendAllText($config, "CommandsEnabled = true`r`n")
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgeCommandReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-commands.txt'), @('PASS','forge-commands-v3','settings-replay-restored','forge-queue-cancel-replay-refusal-direct-start-capacity'))
    Assert-ForgeCommandReceipt $root $p
    $p.forgeCommandProbe = $false; Reject { Assert-ForgeReadSelection $root $p }
    $p.forgeCommandProbe = $true
    $p | Add-Member forgePersistenceProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-persistence.enabled'), 'forge-persistence-v1')
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgePersistenceReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-persistence.txt'), @('PASS','forge-persistence-v1','paused-jobs-roundtrip-save-as-slot-switch'))
    Assert-ForgePersistenceReceipt $root $p
    $p.forgeCommandProbe = $false; Reject { Assert-ForgeReadSelection $root $p }
    $p.forgeCommandProbe = $true; $p.forgePersistenceProbe = $false
    Remove-Item (Join-Path $root 'forge-persistence.enabled')
    $p | Add-Member forgeDeliveryProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-delivery.enabled'), 'forge-delivery-v1')
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgeDeliveryReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-delivery.txt'), @('PASS','forge-delivery-v1','partial-cancel-multi-batch-inventory'))
    Assert-ForgeDeliveryReceipt $root $p
    $p.forgePersistenceProbe = $true; Reject { Assert-ForgeReadSelection $root $p }
    $p.forgePersistenceProbe = $false; $p.forgeDeliveryProbe = $false
    Remove-Item (Join-Path $root 'forge-delivery.enabled')
    $p | Add-Member refineryProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'refinery.enabled'), 'refinery-v3')
    Assert-ForgeReadSelection $root $p
    Reject { Assert-RefineryReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'refinery.txt'), @('PASS','refinery-v3','fractional-partial-multiple-refund-extraction-replay-favourites'))
    Assert-RefineryReceipt $root $p
    [IO.File]::WriteAllLines((Join-Path $root 'refinery.txt'), @('PASS','refinery-v1','fractional-partial-refund-extraction-replay'))
    Reject { Assert-RefineryReceipt $root $p }
    $p.forgeDeliveryProbe = $true; Reject { Assert-ForgeReadSelection $root $p }
    $p.forgeDeliveryProbe = $false; $p.refineryProbe = $false; $p.forgeCommandProbe = $false
    Remove-Item (Join-Path $root 'refinery.enabled'), (Join-Path $root 'forge-commands.enabled')
    $p | Add-Member forgeUiProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-ui.enabled'), 'forge-ui-v3')
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgeUiReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-ui.txt'), @('PASS','forge-ui-v3','variants-pointer-disabled-stale-reopen-dispose-nonoverlap-scale-recovery'))
    Reject { Assert-ForgeUiReceipt $root $p }
    $image = Join-Path $root 'forge-ui-actions.png'; $imageRecord = Join-Path $root 'forge-ui-actions.txt'
    [IO.File]::WriteAllBytes($image, [byte[]]@(137,80,78,71,13,10,26,10))
    [IO.File]::WriteAllText($imageRecord, 'sha256=' + (Get-FileHash $image -Algorithm SHA256).Hash.ToLowerInvariant())
    Reject { Assert-ForgeUiReceipt $root $p }
    $scaled = Join-Path $root 'forge-ui-scaled.png'; $scaledRecord = Join-Path $root 'forge-ui-scaled.txt'
    Copy-Item $image $scaled; Copy-Item $imageRecord $scaledRecord
    Assert-ForgeUiReceipt $root $p
    [IO.File]::AppendAllText($image, 'changed'); Reject { Assert-ForgeUiReceipt $root $p }
    Remove-Item $image; Reject { Assert-ForgeUiReceipt $root $p }
    [IO.File]::WriteAllBytes($image, [byte[]]@(137,80,78,71,13,10,26,10))
    Assert-ForgeUiReceipt $root $p
    [IO.File]::AppendAllText($scaled, 'changed'); Reject { Assert-ForgeUiReceipt $root $p }
    Copy-Item $image $scaled -Force
    Assert-ForgeUiReceipt $root $p
    $p.forgeUiProbe = $false; Reject { Assert-ForgeUiSelection $root $p }; $p.forgeUiProbe = $true
    $p.forgeCommandProbe = $true; Reject { Assert-ForgeUiSelection $root $p }; $p.forgeCommandProbe = $false
    [IO.File]::WriteAllText((Join-Path $root 'forge-ui.txt'), 'INCOMPLETE')
    Reject { Assert-ForgeUiReceipt $root $p }
    $p.forgeUiProbe = $false; Remove-Item (Join-Path $root 'forge-ui.enabled')
    $p | Add-Member blueprintPinProbe $true
    $p | Add-Member blueprintPinRevision ('a' * 40)
    $p | Add-Member blueprintPinSha256 ('b' * 64)
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'blueprint-pin.enabled'), 'blueprint-pin-v11')
    $binDir = Join-Path $root 'game\BepInEx\plugins'; New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    $binary = Join-Path $binDir 'VGBlueprintPin.dll'; [IO.File]::WriteAllText($binary, 'synthetic binary')
    Reject { Assert-ForgeReadSelection $root $p }
    $p.blueprintPinSha256 = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::AppendAllText((Join-Path $root 'game\BepInEx\config\vgmodapi.cfg'), "`n[Hud]`nEnabled = true`n")
    Assert-ForgeReadSelection $root $p
    Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'blueprint-pin.txt'), @('PASS','blueprint-pin-v11','pin-batch-exact-variant-navigation-close-producer-routes-queue-partial-cancel-multiunit-reload-saveas-switch-inspector-isolation-context-generated-routing-bonus-fullcargo'))
    Reject { Assert-BlueprintPinReceipt $root $p }
    $cargo = Join-Path $root 'forge-cargo-full.txt'
    [IO.File]::WriteAllLines($cargo, @('PASS','full-cargo-armory-fallback','capacity-restored'))
    $bonus = Join-Path $root 'forge-bonus.txt'
    [IO.File]::WriteAllLines($bonus, @('PASS','one-batch-two-generated-deliveries','skill-fixture-restored'))
    $routing = Join-Path $root 'forge-routing.txt'
    [IO.File]::WriteAllLines($routing, @('PASS','armory-and-cargo-delivered','preference-restored'))
    $generated = Join-Path $root 'forge-generated.txt'
    [IO.File]::WriteAllLines($generated, @('PASS','generated-equipment-delivered','level-and-inventory-reconciled'))
    $context = Join-Path $root 'forge-missing-context.txt'
    [IO.File]::WriteAllLines($context, @('PASS','missing-recipe-refused','native-undock-navigation-refused'))
    $isolation = Join-Path $root 'forge-callback-isolation.txt'
    [IO.File]::WriteAllLines($isolation, @('PASS','throwing-listener-isolated','following-listener-delivered'))
    $pinImage = Join-Path $root 'blueprint-pin-view.png'; $pinRecord = Join-Path $root 'blueprint-pin-view.txt'
    Copy-Item $image $pinImage; Copy-Item $imageRecord $pinRecord
    Reject { Assert-BlueprintPinReceipt $root $p }
    foreach ($stem in @('blueprint-pin-producers','blueprint-pin-partial','blueprint-pin-cancelled','forge-consumers')) {
        Reject { Assert-BlueprintPinReceipt $root $p }
        Copy-Item $image (Join-Path $root ($stem + '.png')); Copy-Item $imageRecord (Join-Path $root ($stem + '.txt'))
    }
    Assert-BlueprintPinReceipt $root $p
    Remove-Item $cargo; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($cargo, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($cargo, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($cargo, @('PASS','full-cargo-armory-fallback','capacity-restored'))
    Remove-Item $bonus; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($bonus, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($bonus, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($bonus, @('PASS','one-batch-two-generated-deliveries','skill-fixture-restored'))
    Remove-Item $routing; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($routing, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($routing, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($routing, @('PASS','armory-and-cargo-delivered','preference-restored'))
    Remove-Item $generated; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($generated, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($generated, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($generated, @('PASS','generated-equipment-delivered','level-and-inventory-reconciled'))
    Remove-Item $context; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($context, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($context, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($context, @('PASS','missing-recipe-refused','native-undock-navigation-refused'))
    Remove-Item $isolation; Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($isolation, 'INCOMPLETE'); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllText($isolation, ('x' * 257)); Reject { Assert-BlueprintPinReceipt $root $p }
    [IO.File]::WriteAllLines($isolation, @('PASS','throwing-listener-isolated','following-listener-delivered'))
    foreach ($stem in @('blueprint-pin-partial','blueprint-pin-cancelled','forge-consumers')) {
        $extra = Join-Path $root ($stem + '.png')
        [IO.File]::AppendAllText($extra, 'changed'); Reject { Assert-BlueprintPinReceipt $root $p }
        Copy-Item $image $extra -Force
    }
    $validConfig = [IO.File]::ReadAllText($config)
    [IO.File]::WriteAllText($config, $validConfig.Replace('CommandsEnabled = true', 'CommandsEnabled = false'))
    Reject { Assert-BlueprintPinSelection $root $p }
    [IO.File]::WriteAllText($config, $validConfig)
    [IO.File]::WriteAllText((Join-Path $root 'blueprint-pin.enabled'), 'blueprint-pin-v10')
    Reject { Assert-BlueprintPinSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'blueprint-pin.enabled'), 'blueprint-pin-v11')
    [IO.File]::AppendAllText($pinImage, 'changed'); Reject { Assert-BlueprintPinReceipt $root $p }
    Copy-Item $image $pinImage -Force
    $producerImage = Join-Path $root 'blueprint-pin-producers.png'
    [IO.File]::AppendAllText($producerImage, 'changed'); Reject { Assert-BlueprintPinReceipt $root $p }
    Copy-Item $image $producerImage -Force
    Assert-BlueprintPinReceipt $root $p
    $p.forgeCommandProbe = $true; Reject { Assert-BlueprintPinSelection $root $p }; $p.forgeCommandProbe = $false
    $p.blueprintPinProbe = $false; Reject { Assert-BlueprintPinSelection $root $p }; $p.blueprintPinProbe = $true
    [IO.File]::AppendAllText($binary, 'changed'); Reject { Assert-BlueprintPinSelection $root $p }
    'PASS Forge/refinery probe selection and receipt tests'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
