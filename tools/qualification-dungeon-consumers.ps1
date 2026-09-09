# Controlled co-loading only; source paths and receipts stay inside private sandboxes.
$DungeonConsumerNames = @('VGBBoardAlways.dll','DungeonAuthor.dll')
function Read-DungeonConsumerManifest([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 32768 -or ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid dungeon consumer manifest file.' }
    $m = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($m.schema -isnot [int] -or $m.schema -ne 1 -or @($m.binaries).Count -ne 2 -or $m.rewardItemId -isnot [string] -or [string]::IsNullOrWhiteSpace($m.rewardItemId) -or $m.rewardItemId.Length -gt 128 -or $m.rewardItemId -match '[\r\n]') { throw 'Invalid dungeon consumer manifest shape.' }
    foreach ($name in $DungeonConsumerNames) {
        $entries = @($m.binaries | Where-Object { $_.name -ceq $name })
        if ($entries.Count -ne 1 -or $entries[0].sha256 -isnot [string] -or $entries[0].revision -isnot [string] -or $entries[0].path -isnot [string] -or $entries[0].sha256 -cnotmatch '^[0-9a-f]{64}$' -or $entries[0].revision -cnotmatch '^[0-9a-f]{40}$' -or ![IO.Path]::IsPathRooted($entries[0].path)) { throw 'Invalid dungeon consumer binary identity.' }
    }
    if ($m.PSObject.Properties['mode'] -and ($m.mode -isnot [string] -or $m.mode -cnotin @('Retreat','Combat','Reward'))) { throw 'Invalid dungeon consumer mode.' }
    if ($m.PSObject.Properties['mode'] -and $m.mode -ceq 'Reward' -and $m.rewardItemId -cne 'Titanium Plate') { throw 'Reward fixture requires Titanium Plate.' }
    return $m
}
function Install-DungeonConsumers([string]$Manifest, [string]$Root, [string]$Plugins, [string]$Cecil) {
    $m = Read-DungeonConsumerManifest $Manifest
    Add-Type -Path $Cecil
    foreach ($entry in $m.binaries) {
        $file = Get-Item -LiteralPath $entry.path
        if ($file.PSIsContainer -or $file.Length -eq 0 -or $file.Length -gt 20MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Dungeon consumer source differs from manifest.' }
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($entry.path)
        try {
            if ($assembly.Name.Name -cne [IO.Path]::GetFileNameWithoutExtension($entry.name)) { throw 'Wrong dungeon consumer assembly.' }
            $allowed = @('netstandard','BepInEx','UnityEngine','UnityEngine.CoreModule','VGModAPI.Abstractions')
            if (@($assembly.MainModule.AssemblyReferences | Where-Object { $_.Name -notin $allowed }).Count) { throw 'Unsupported dungeon consumer dependency.' }
            $api = @($assembly.MainModule.AssemblyReferences | Where-Object { $_.Name -ceq 'VGModAPI.Abstractions' })
            if ($api.Count -ne 1 -or $api[0].Version.Major -ne 0 -or $api[0].Version.Minor -ne 2) { throw 'Dungeon consumers require typed API 0.2 references.' }
                $typeName = if ($entry.name -ceq 'VGBBoardAlways.dll') { 'VGBBoardAlways.Plugin' } else { 'DungeonAuthor.Plugin' }
                $plugin = $assembly.MainModule.Types | Where-Object { $_.FullName -ceq $typeName }
                $deps = @($plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -ceq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and $_.ConstructorArguments[0].Value -ceq 'vgmodapi' -and $_.ConstructorArguments[1].Value -ceq '0.2.0' })
                if ($deps.Count -ne 1) { throw 'Dungeon consumer must require API 0.2.0 before Awake.' }
        } finally { $assembly.Dispose() }
        Copy-Item -LiteralPath $entry.path -Destination (Join-Path $Plugins $entry.name)
    }
    if ($m.PSObject.Properties['mode'] -and $m.mode -cin @('Combat','Reward')) { [IO.File]::WriteAllText((Join-Path $Root 'dungeon-combat.enabled'), 'dungeon-combat-v1') }
    if ($m.PSObject.Properties['mode'] -and $m.mode -ceq 'Reward') { [IO.File]::WriteAllText((Join-Path $Root 'dungeon-reward.enabled'), 'dungeon-reward-v1') }
    Copy-Item -LiteralPath $Manifest -Destination (Join-Path $Root 'dungeon-consumer-sources.json')
    [IO.File]::WriteAllText((Join-Path $Root 'dungeon-consumers.enabled'), 'dungeon-consumers-v3')
}
function Get-DungeonConsumerConfig([string]$Path, [string]$Section, [string]$Key) {
    $text = [IO.File]::ReadAllText($Path)
    $sections = [regex]::Matches($text, '(?ms)^\[' + [regex]::Escape($Section) + '\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
    if ($sections.Count -ne 1) { throw 'Ambiguous dungeon consumer config section.' }
    $values = [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^' + [regex]::Escape($Key) + '\s*=\s*(.*?)\s*$')
    if ($values.Count -ne 1) { throw 'Ambiguous dungeon consumer config key.' }
    return $values[0].Groups[1].Value.Trim()
}
function Assert-DungeonConsumerSelection([string]$Root, $Provenance) {
    $flag = $Provenance.PSObject.Properties['dungeonConsumersProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid dungeon consumer flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'dungeon-consumers.enabled'; $sources = Join-Path $Root 'dungeon-consumer-sources.json'
    if ((Test-Path -LiteralPath $marker) -ne [bool]$selected -or (Test-Path -LiteralPath $sources) -ne [bool]$selected) { throw 'Dungeon consumer selection mismatch.' }
    if (!$selected) {
        if ((Test-Path -LiteralPath (Join-Path $Root 'dungeon-combat.enabled')) -or (Test-Path -LiteralPath (Join-Path $Root 'dungeon-reward.enabled'))) { throw 'Unselected dungeon combat/reward marker.' }
        return
    }
    if (!$Provenance.dungeonPanelProbe -or !$Provenance.dungeonReadinessProbe -or $Provenance.scenario -cne 'Full' -or [IO.File]::ReadAllText($marker) -cne 'dungeon-consumers-v3') { throw 'Dungeon consumers require the isolated full panel phase.' }
    if ($Provenance.dungeonConsumerManifestHash -cnotmatch '^[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $sources -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Provenance.dungeonConsumerManifestHash) { throw 'Dungeon consumer manifest changed.' }
    $m = Read-DungeonConsumerManifest $sources
    $combat = $m.PSObject.Properties['mode'] -and $m.mode -cin @('Combat','Reward')
    $reward = $m.PSObject.Properties['mode'] -and $m.mode -ceq 'Reward'
    $rewardMarker = Join-Path $Root 'dungeon-reward.enabled'
    if ((Test-Path -LiteralPath $rewardMarker) -ne [bool]$reward -or ($reward -and [IO.File]::ReadAllText($rewardMarker) -cne 'dungeon-reward-v1')) { throw 'Dungeon reward selection mismatch.' }
    $combatMarker = Join-Path $Root 'dungeon-combat.enabled'
    if ((Test-Path -LiteralPath $combatMarker) -ne [bool]$combat -or ($combat -and [IO.File]::ReadAllText($combatMarker) -cne 'dungeon-combat-v1')) { throw 'Dungeon combat selection mismatch.' }
    foreach ($entry in $m.binaries) {
        $path = Join-Path $Root ('game/BepInEx/plugins/' + $entry.name)
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Prepared dungeon consumer changed.' }
    }
    $config = Join-Path $Root 'game/BepInEx/config'
    if ((Get-DungeonConsumerConfig (Join-Path $config 'vg.boardalways.cfg') 'General' 'Enabled') -cne 'true' -or [float]::Parse((Get-DungeonConsumerConfig (Join-Path $config 'vg.boardalways.cfg') 'General' 'DifficultyModifier'), [Globalization.CultureInfo]::InvariantCulture) -ne 2 -or [float]::Parse((Get-DungeonConsumerConfig (Join-Path $config 'vg.boardalways.cfg') 'General' 'IntegrityDamageMultiplier'), [Globalization.CultureInfo]::InvariantCulture) -ne 1.5 -or (Get-DungeonConsumerConfig (Join-Path $config 'vgmodapi.example.cargo.cfg') 'Content' 'RewardItemId') -cne $m.rewardItemId) { throw 'Dungeon consumer configuration changed.' }
}
function Assert-DungeonConsumerReceipt([string]$Root, $Provenance) {
    Assert-DungeonConsumerSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['dungeonConsumersProbe'] -or !$Provenance.dungeonConsumersProbe) { return }
    $file = Join-Path $Root 'dungeon-consumers.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 256) { throw 'Oversized dungeon consumer input receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    $phase = if (Test-Path -LiteralPath (Join-Path $Root 'dungeon-combat.enabled')) { 'attach-duplicate-commands-active-combat' } else { 'attach-duplicate-commands-active-walk-retreat' }
    if ($lines.Count -ne 3 -or $lines[0] -cne 'INPUTS_SENT' -or $lines[1] -cne 'dungeon-consumers-v3' -or $lines[2] -cne $phase) { throw 'Incomplete dungeon consumer inputs.' }
    $walk = [IO.File]::ReadAllLines((Join-Path $Root 'dungeon-walk.txt'))
    $expected = if (Test-Path -LiteralPath (Join-Path $Root 'dungeon-combat.enabled')) { "PASS`ndungeon-combat-v1`nmanual-victory-choice-extraction`ndonor-crew-reconciled" } else { "PASS`ndungeon-walk-v1`nmanual-arrival-retreat-settlement`ndonor-crew-reconciled" }
    if (($walk -join "`n") -cne $expected) { throw 'Incomplete active dungeon walk receipt.' }
    if (Test-Path -LiteralPath (Join-Path $Root 'dungeon-reward.enabled')) {
        $reward = [IO.File]::ReadAllLines((Join-Path $Root 'dungeon-reward.txt'))
        if (($reward -join "`n") -cne "PASS`ndungeon-reward-v1`nauthored-two-multiplied-four-cargo-delivered") { throw 'Incomplete dungeon reward receipt.' }
    }
    $commands = [IO.File]::ReadAllLines((Join-Path $Root 'dungeon-commands.txt'))
    if (($commands -join "`n") -cne "PASS`ndungeon-commands-v1`ncontrol-start-options-refusals-cancel-before-tick`ncrew-and-docking-preserved") { throw 'Incomplete dungeon command admission receipt.' }
    $log = [IO.File]::ReadAllText((Join-Path $Root 'Player.log'))
    foreach ($message in @('Loading [Board Always 0.4.0]','Loading [Cargo recovery example 0.1.0]','Board Always v0.4.0 registered public boarding policies.','Cargo attach: Attached','Cargo attach: TargetInUse')) {
        if ([regex]::Matches($log, [regex]::Escape($message)).Count -ne 1) { throw ('Missing or duplicate consumer evidence: ' + $message) }
    }
    if ($log.IndexOf('Cargo attach: Attached', [StringComparison]::Ordinal) -gt $log.IndexOf('Cargo attach: TargetInUse', [StringComparison]::Ordinal)) { throw 'Consumer attachment results out of order.' }
}
