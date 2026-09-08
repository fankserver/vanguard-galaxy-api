# Controlled composition only: speech and LLM requests are disabled. Not a campaign/travel probe.
function Get-BarConsumerToolsInventory([string]$Root) {
    $tools = Join-Path $Root 'game\BepInEx\plugins\tools'
    $directory = Get-Item -LiteralPath $tools -ErrorAction Stop
    if (!$directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'TTS tools are not a physical directory.' }
    $inventory = @{}
    foreach ($entry in @(Get-ChildItem -LiteralPath $tools -Recurse -Force -ErrorAction Stop)) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'TTS tools contain a link.' }
        $relative = $entry.FullName.Substring($directory.FullName.TrimEnd('\').Length + 1)
        $inventory[$relative] = if ($entry.PSIsContainer) { 'directory' } else { (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash }
    }
    return $inventory
}

function Assert-BarConsumerToolsInventory([string]$Root, $Expected) {
    $inventory = Get-BarConsumerToolsInventory $Root
    $recorded = @($Expected.PSObject.Properties)
    if ($inventory.Count -ne $recorded.Count) { throw 'TTS tools inventory changed.' }
    foreach ($entry in $recorded) { if (!$inventory.ContainsKey($entry.Name) -or $inventory[$entry.Name] -cne $entry.Value) { throw 'TTS tools changed.' } }
}

function Assert-BarConsumerInputs([string]$Root, $Provenance) {
    Assert-StoryIsolation $Provenance
    foreach ($key in @('barProbe','barColdSequence','storyProbe','storyAbsentProbe','storyColdSequence','persistenceProbe','missionJournal','stockpile','anima','echo','travelJournal')) {
        if ($Provenance.PSObject.Properties[$key] -and $Provenance.$key) { throw "Consumer bars conflict with $key." }
    }
    if ($Provenance.scenario -cne 'Full' -or (Get-Content -LiteralPath (Join-Path $Root 'bar-consumers.enabled') -Raw) -cne 'controlled-bar-consumers-v1') { throw 'Invalid consumer bar selection.' }
    $manifest = Join-Path $Root 'bar-consumer-sources.json'
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne $Provenance.barConsumerManifestHash) { throw 'Consumer manifest changed.' }
    $spec = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $pluginDir = Join-Path $Root 'game\BepInEx\plugins'
    $identities = @{ VGAnima='vganima'; VGTTS='vgtts'; 'VanguardGalaxy.CustomMission'='com.vanguardgalaxy.custommission' }
    if (@($spec.plugins).Count -ne 3 -or @($spec.plugins.name | Sort-Object -Unique).Count -ne 3) { throw 'Consumer manifest cardinality changed.' }
    Add-Type -Path (Join-Path $Root 'game\BepInEx\core\Mono.Cecil.dll')
    foreach ($entry in $spec.plugins) {
        if (!$identities.ContainsKey($entry.name) -or $entry.revision -cnotmatch '^[0-9a-f]{40}$' -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid consumer provenance.' }
        $dll = Join-Path $pluginDir ($entry.name + '.dll')
        if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Consumer differs from its source receipt.' }
        $reader = Read-ConsumerAssembly $dll (Get-ConsumerMetadataReferenceDirs $pluginDir $Root (Join-Path $Root 'game'))
        try {
            $attributes = @($reader.Assembly.MainModule.Types | ForEach-Object { $_.CustomAttributes } | Where-Object { $_.AttributeType.FullName -ceq 'BepInEx.BepInPlugin' })
            if ($attributes.Count -ne 1 -or $attributes[0].ConstructorArguments[0].Value -cne $identities[$entry.name]) { throw 'Consumer plugin identity mismatch.' }
        } finally { Close-ConsumerAssembly $reader }
    }
    foreach ($name in @('VGModAPI','VGModAPI.Core','VGModAPI.Abstractions','QualificationRunner','QualificationGuard','LifecycleObserver')) {
        $reader = Read-ConsumerAssembly (Join-Path $pluginDir ($name + '.dll')) (Get-ConsumerMetadataReferenceDirs $pluginDir $Root (Join-Path $Root 'game'))
        try { Assert-QualificationAssemblyRevision $reader.Assembly $name $Provenance.revision } finally { Close-ConsumerAssembly $reader }
    }
    Assert-BarConsumerToolsInventory $Root $Provenance.barConsumerTools
    $configDir = Join-Path $Root 'game\BepInEx\config'
    $api = Get-TravelJournalConfigEntries (Join-Path $configDir 'vgmodapi.cfg')
    if (!$api.ContainsKey('Bars/Enabled') -or $api['Bars/Enabled'] -ine 'true') { throw 'Consumer bars require Bars/Enabled.' }
    Assert-ApiPersistenceRoot $Root
    if ($api['Bars/ExclusiveProviders'] -cne 'com.vanguardgalaxy.custommission') { throw 'Consumer bar policy changed.' }
    $anima = Get-TravelJournalConfigEntries (Join-Path $configDir 'vganima.cfg')
    if ($anima['Llm/Enabled'] -ine 'false' -or $anima['Llm/BaseUrl'] -or $anima['Llm/ApiKey']) { throw 'LLM requests must remain disabled.' }
    $tts = Get-TravelJournalConfigEntries (Join-Path $configDir 'vgtts.cfg')
    foreach ($key in @('General/Enabled','General/DialogueTTS','General/EchoTTS')) { if ($tts[$key] -ine 'false') { throw 'Speech must remain disabled.' } }
}
function Initialize-BarConsumers([string]$Root, [string]$Manifest) {
    $spec = Get-Content -LiteralPath $Manifest -Raw -ErrorAction Stop | ConvertFrom-Json
    $expected = @('VGAnima','VGTTS','VanguardGalaxy.CustomMission')
    if (@($spec.plugins).Count -ne 3 -or @($spec.plugins.name | Sort-Object -Unique).Count -ne 3 -or
        @($spec.plugins | Where-Object { $_.name -cnotin $expected }).Count) { throw 'Exact three-consumer manifest required.' }
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    foreach ($entry in $spec.plugins) {
        if ($entry.revision -cnotmatch '^[0-9a-f]{40}$' -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Consumer source revision/hash missing.' }
        if ([Reflection.AssemblyName]::GetAssemblyName($entry.path).Name -cne $entry.name) { throw 'Consumer assembly name mismatch.' }
        if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Consumer binary hash mismatch.' }
        Copy-Item -LiteralPath $entry.path -Destination (Join-Path $plugins ($entry.name + '.dll')) -ErrorAction Stop
    }
    if ($spec.jsonRuntime.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        (Get-FileHash -LiteralPath $spec.jsonRuntime.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $spec.jsonRuntime.sha256 -or
        [Reflection.AssemblyName]::GetAssemblyName($spec.jsonRuntime.path).Name -cne 'Newtonsoft.Json') { throw 'JSON runtime identity/hash mismatch.' }
    Copy-Item -LiteralPath $spec.jsonRuntime.path -Destination (Join-Path $plugins 'Newtonsoft.Json.dll') -ErrorAction Stop
    $source = Get-Item -LiteralPath $spec.ttsTools -ErrorAction Stop
    if (!$source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'TTS tools require a physical directory.' }
    $entries = @(Get-ChildItem -LiteralPath $source.FullName -Recurse -Force -ErrorAction Stop)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'TTS tools must not contain links.' }
    foreach ($required in @('sherpa\sherpa-onnx-tts.exe','kokoro\model.onnx')) {
        if (!(Test-Path -LiteralPath (Join-Path $source.FullName $required) -PathType Leaf)) { throw 'Incomplete TTS bundle.' }
    }
    $destination = Join-Path $plugins 'tools'
    if (Test-Path -LiteralPath $destination) { throw 'TTS tools destination already exists.' }
    Copy-Item -LiteralPath $source.FullName -Destination $destination -Recurse -ErrorAction Stop
    foreach ($file in @($entries | Where-Object { !$_.PSIsContainer })) {
        $relative = $file.FullName.Substring($source.FullName.TrimEnd('\').Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $destination $relative) -Algorithm SHA256).Hash) { throw 'TTS bundle copy changed.' }
    }
    Copy-Item -LiteralPath $Manifest -Destination (Join-Path $Root 'bar-consumer-sources.json') -ErrorAction Stop
    $config = Join-Path $Root 'game\BepInEx\config'
    [IO.File]::WriteAllText((Join-Path $config 'vgmodapi.cfg'), "[Persistence]`r`nRoot = $(Join-Path $Root 'state')`r`n[Bars]`r`nEnabled = true`r`nExclusiveProviders = com.vanguardgalaxy.custommission`r`n")
    [IO.File]::WriteAllText((Join-Path $config 'vganima.cfg'), "[Llm]`r`nEnabled = false`r`nBaseUrl = `r`nApiKey = `r`n")
    [IO.File]::WriteAllText((Join-Path $config 'vgtts.cfg'), "[General]`r`nEnabled = false`r`nDialogueTTS = false`r`nEchoTTS = false`r`n")
    [IO.File]::WriteAllText((Join-Path $Root 'bar-consumers.enabled'), 'controlled-bar-consumers-v1')
}

function Assert-BarConsumerReceipt([string]$Root) {
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'bar-consumers.txt') -ErrorAction Stop)
    if ($rows.Count -ne 2 -or $rows[0] -cne 'PASS' -or $rows[1] -cne 'actual-foundation-builder;four-exclusive-contacts;actual-anima-finalization;denied-additive-offer;forced-native-refresh;tts-finalized-boundary;permission-revocation;context-restored') { throw 'Incomplete consumer bar receipt.' }
    $preparation = @(Get-Content -LiteralPath (Join-Path $Root 'bar-consumer-preparation.txt') -ErrorAction Stop)
    if ($preparation.Count -ne 2 -or $preparation[0] -cnotmatch '^native-force-refreshes=[1-8]$' -or $preparation[1] -cnotmatch '^retained-vanilla=[0-4]$') { throw 'Invalid consumer capacity preparation.' }
}
