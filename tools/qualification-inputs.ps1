# Prepared-input helpers; safe to exercise with synthetic files.
function Assert-AnimaAssemblyMetadata($Assembly) {
    if ($Assembly.Name.Name -ne 'VGAnima' -or $Assembly.Name.Version.ToString() -ne '0.3.0.0') { throw 'Only Anima 0.3.0 pilot shape accepted.' }
    $plugin = @($Assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGAnima.Plugin' })
    if ($plugin.Count -ne 1) { throw 'Anima plugin metadata missing or duplicated.' }
    $dependency = @($plugin[0].CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and
        $_.ConstructorArguments[0].Value -eq 'vgmodapi' -and $_.ConstructorArguments[1].Value -eq '0.1.8'
    })
    if ($dependency.Count -ne 1) { throw 'Anima must require API before its startup sweeper.' }
}
function Assert-PersistenceProbeReceipt([string]$Root, $Provenance) {
    if ($Provenance.PSObject.Properties['anima'] -and $Provenance.anima) {
        $receipt = Join-Path $Root 'anima-missions.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Anima mission probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['journalMissionEventsProbe'] -and $Provenance.journalMissionEventsProbe) {
        $receipt = Join-Path $Root 'journal-mission-events.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Journal mission events probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['missionIdentityProbe'] -and $Provenance.missionIdentityProbe) {
        $receipt = Join-Path $Root 'mission-identity.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Mission identity probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['missionTransitionsProbe'] -and $Provenance.missionTransitionsProbe) {
        foreach ($name in @('mission-transitions.txt','mission-clear.txt','mission-guild.txt','mission-waves.txt')) {
            $receipt = Join-Path $Root $name
            if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw "Mission probe did not complete: $name" }
        }
    }
    if ($Provenance.PSObject.Properties['contentReferenceProbe'] -and $Provenance.contentReferenceProbe) {
        $receipt = Join-Path $Root 'content-reference.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Content reference probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['stockpileCoordinated'] -and $Provenance.stockpileCoordinated) {
        $receipt = Join-Path $Root 'stockpile-coordinated.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Coordinated Stockpile probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['journalCoordinated'] -and $Provenance.journalCoordinated) {
        $receipt = Join-Path $Root 'journal-coordinated.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Coordinated journal probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['persistenceProbe'] -and $Provenance.persistenceProbe) {
        $receipt = Join-Path $Root 'persistence-probe.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Persistence probe did not complete.' }
    }
    if ($Provenance.PSObject.Properties['travelStation'] -and $Provenance.travelStation) {
        Assert-TravelStationReceipt $Root
    }
}
$TravelStationPhase = 'travel-in-system-station-v1'
$TravelStationRequiredCases = @('initial-placement','station-undock','in-system-route','early-cancel','chained-route','station-dock')
$TravelStationReceiptHeader = @('case','description','status','nativeIdentity','session','operation','detail')
$TravelStationEventHeader = @('apiSequence','surface','case','session','operation','kind','mode','origin','requested','actual','gameSeconds','dwellSeconds')
# Independent verification of the pilot's own claim: the declared phase, every mandatory case
# identity, the receipt/event files and the identities they share must all agree. A first line of
# PASS is never accepted on its own.
function Assert-TravelStationReceipt([string]$Root) {
    $summaryPath = Join-Path $Root 'travel-station.txt'
    $receiptPath = Join-Path $Root 'travel-station-receipt.tsv'
    $eventsPath = Join-Path $Root 'travel-station-events.tsv'
    foreach ($path in @($summaryPath, $receiptPath, $eventsPath)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Travel/station pilot output missing: $path" }
    }
    $summary = @(Get-Content -LiteralPath $summaryPath)
    if ($summary.Count -lt 3 -or $summary[0] -cne 'PASS') { throw 'Travel/station pilot did not complete with PASS.' }
    if ($summary -notcontains "phase=$TravelStationPhase") { throw 'Travel/station receipt does not declare the qualified phase.' }
    if ($summary -notcontains ("required=" + ($TravelStationRequiredCases -join ','))) { throw 'Travel/station receipt declares different required cases.' }
    if ($summary -notcontains 'fault=none') { throw 'Travel/station pilot recorded a fault.' }
    $rows = @(Get-Content -LiteralPath $receiptPath)
    if ($rows.Count -lt 2 -or (($rows[0] -split "`t") -join ',') -cne ($TravelStationReceiptHeader -join ',')) { throw 'Travel/station receipt header changed.' }
    $records = @($rows[1..($rows.Count - 1)] | ForEach-Object {
        $columns = $_ -split "`t"
        if ($columns.Count -ne $TravelStationReceiptHeader.Count) { throw 'Malformed travel/station receipt row.' }
        [pscustomobject]@{ Case=$columns[0]; Status=$columns[2]; Session=$columns[4]; Operation=$columns[5] }
    })
    if (@($records | Where-Object { $_.Status -eq 'failed' }).Count -gt 0) { throw 'Travel/station receipt contains failed cases.' }
    if (@($records | Where-Object { $_.Status -notin @('passed','not-run') }).Count -gt 0) { throw 'Unknown travel/station case status.' }
    $passed = @($records | Where-Object { $_.Status -eq 'passed' })
    $notRun = @($records | Where-Object { $_.Status -eq 'not-run' })
    if ($passed.Count -eq 0) { throw 'Travel/station receipt has no passed case; empty coverage is not a pass.' }
    if ($summary -notcontains ("rows=" + $records.Count + " passed=" + $passed.Count + " failed=0 notRun=" + $notRun.Count)) { throw 'Travel/station summary counts disagree with the receipt.' }
    $events = @(Get-Content -LiteralPath $eventsPath)
    if ($events.Count -lt 2 -or (($events[0] -split "`t") -join ',') -cne ($TravelStationEventHeader -join ',')) { throw 'Travel/station event trace missing or its header changed.' }
    $eventRows = @($events[1..($events.Count - 1)] | ForEach-Object {
        $columns = $_ -split "`t"
        if ($columns.Count -ne $TravelStationEventHeader.Count) { throw 'Malformed travel/station event row.' }
        [pscustomobject]@{ Case=$columns[2]; Session=$columns[3]; Operation=$columns[4] }
    })
    $eventCases = @($eventRows | ForEach-Object { $_.Case } | Select-Object -Unique)
    $eventSessions = @($eventRows | ForEach-Object { $_.Session } | Select-Object -Unique)
    $eventOperations = @($eventRows | ForEach-Object { $_.Operation } | Select-Object -Unique)
    foreach ($case in $TravelStationRequiredCases) {
        $matched = @($records | Where-Object { $_.Case -eq $case })
        if ($matched.Count -ne 1) { throw "Required travel/station case is missing or duplicated: $case" }
        if ($matched[0].Status -ne 'passed') { throw "Required travel/station case did not pass: $case" }
        if ($summary -notcontains "required-case $case=passed") { throw "Travel/station summary and receipt disagree about $case." }
        if ($matched[0].Session -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { throw "Required travel/station case has no session identity: $case" }
        if ($case -notin $eventCases) { throw "No observed public events for required travel/station case: $case" }
    }
    foreach ($record in $passed) {
        if ($record.Session -notin $eventSessions) { throw "Receipt session identity is absent from the event trace: $($record.Case)" }
        if ($record.Operation -and $record.Operation -notin $eventOperations) { throw "Receipt operation identity is absent from the event trace: $($record.Case)" }
    }
}
function Assert-VanillaControlReceipt([string]$Root, $Provenance) {
    if ($Provenance.PSObject.Properties['vanillaLoadControl'] -and $Provenance.vanillaLoadControl) {
        $receipt = Join-Path $Root 'vanilla-load-control.txt'
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -LiteralPath $receipt -TotalCount 1) -ne 'PASS') { throw 'Vanilla gameplay control did not complete successfully.' }
    }
}
function Initialize-QualificationAssemblyOverlay([string]$Root, [string]$GameDir) {
    $sourceData = Join-Path $GameDir 'VanguardGalaxy_Data'
    $data = Join-Path $Root 'game\VanguardGalaxy_Data'
    if (!((Get-Item -LiteralPath $data -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected fresh data junction.' }
    $sourceAssembly = Join-Path $sourceData 'Managed\Assembly-CSharp.dll'
    $original = (Get-FileHash -LiteralPath $sourceAssembly).Hash
    [IO.Directory]::Delete($data, $false)
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $Root 'assembly-overlay.hash'), '') # Cleanup receipt; incomplete preparation cannot run.
    foreach ($entry in Get-ChildItem -LiteralPath $sourceData -Force) {
        $destination = Join-Path $data $entry.Name
        if ($entry.Name -eq 'Managed') {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Managed references must not contain links.' }
            Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse
        } elseif ($entry.PSIsContainer) {
            New-Item -ItemType Junction -Path $destination -Target $entry.FullName | Out-Null
        } else { Copy-Item -LiteralPath $entry.FullName -Destination $destination }
    }
    $copy = Join-Path $data 'Managed\Assembly-CSharp.dll'
    $stream = [IO.File]::Open($copy, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes('VGModAPI-private-hash-probe-v1')
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
    $modified = (Get-FileHash -LiteralPath $copy).Hash
    if ($modified -eq $original -or (Get-FileHash -LiteralPath $sourceAssembly).Hash -ne $original) { throw 'Overlay identity/preservation failure.' }
    [IO.File]::WriteAllLines((Join-Path $Root 'assembly-overlay.hash'), [string[]]@($modified, $original))
    return @{ source=$sourceAssembly; original=$original; modified=$modified }
}
function Copy-QualificationJournalHistory($Sources, [string]$Saves) {
    foreach ($source in $Sources) {
        if (!(Test-Path -LiteralPath ($source.FullName + '.vgmissionjournal.json') -PathType Leaf)) { throw 'Journal pilot requires copied history for both fixtures.' }
    }
    for ($i = 0; $i -lt 2; $i++) {
        $name = if ($i -eq 0) { 'fixture-a' } else { 'fixture-b' }
        Copy-Item -LiteralPath ($Sources[$i].FullName + '.vgmissionjournal.json') -Destination (Join-Path $Saves ($name + '.save.vgmissionjournal.json'))
    }
}

# Read-only verification.
function Assert-QualificationInputs([string]$Root) {
    $provenance = Get-Content -LiteralPath (Join-Path $Root 'build-provenance.json') -Raw | ConvertFrom-Json
    if ($provenance.scenario -notin @('Full','MissingApi','UnavailableApi') -or
        (Get-Content -LiteralPath (Join-Path $Root 'scenario.txt') -Raw).Trim() -cne $provenance.scenario) { throw 'Prepared scenario changed.' }
    $missionProbe = $provenance.PSObject.Properties['missionTransitionsProbe'] -and [bool]$provenance.missionTransitionsProbe
    $missionMarker = Join-Path $Root 'mission-transitions.enabled'
    if ([bool]$missionProbe -ne (Test-Path -LiteralPath $missionMarker -PathType Leaf)) { throw 'Mission probe selection changed.' }
    if ($missionProbe) {
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $missionMarker -Raw).Trim() -ne 'missions-v1') { throw 'Invalid mission probe selection.' }
        $config = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg') -Raw
        $sections = [regex]::Matches($config, '(?ms)^\[Missions\]\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($sections.Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^Enabled\s*=').Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^Enabled\s*=\s*true\s*$').Count -ne 1) { throw 'Mission probe config changed.' }
    }
    $identityProbe = $provenance.PSObject.Properties['missionIdentityProbe'] -and [bool]$provenance.missionIdentityProbe
    $identityMarker = Join-Path $Root 'mission-identity.enabled'
    if ([bool]$identityProbe -ne (Test-Path -LiteralPath $identityMarker -PathType Leaf)) { throw 'Mission identity selection changed.' }
    if ($identityProbe) {
        if (!$missionProbe -or !$provenance.persistenceProbe -or (Get-Content -LiteralPath $identityMarker -Raw).Trim() -ne 'identity-v1') { throw 'Invalid mission identity selection.' }
        if ([regex]::Matches($sections[0].Groups['body'].Value, '(?m)^IdentityContinuity\s*=').Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^IdentityContinuity\s*=\s*true\s*$').Count -ne 1) { throw 'Mission identity config changed.' }
    }
    $anima = $provenance.PSObject.Properties['anima'] -and [bool]$provenance.anima
    $animaMarker = Join-Path $Root 'anima-missions.enabled'
    if ([bool]$anima -ne (Test-Path -LiteralPath $animaMarker -PathType Leaf)) { throw 'Anima selection changed.' }
    if ($anima) {
        if (!$identityProbe -or !$provenance.missionJournal -or $provenance.animaRevision -notmatch '^[0-9a-f]{40}$' -or (Get-Content -LiteralPath $animaMarker -Raw).Trim() -ne 'anima-v1') { throw 'Invalid Anima selection.' }
        $animaConfig = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vganima.cfg') -Raw
        foreach ($section in @('General','Llm')) {
            $blocks = [regex]::Matches($animaConfig, "(?ms)^\[$section\]\s*\r?\n(?<body>.*?)(?=^\[|\z)")
            $value = if ($section -eq 'General') { 'true' } else { 'false' }
            if ($blocks.Count -ne 1 -or [regex]::Matches($blocks[0].Groups['body'].Value, '(?m)^Enabled\s*=').Count -ne 1 -or [regex]::Matches($blocks[0].Groups['body'].Value, "(?m)^Enabled\s*=\s*$value\s*$").Count -ne 1) { throw 'Anima enable/network config changed.' }
            if ($section -eq 'Llm') {
                foreach ($key in @('BaseUrl','ApiKey')) {
                    if ([regex]::Matches($blocks[0].Groups['body'].Value, "(?m)^$key\s*=").Count -ne 1 -or [regex]::Matches($blocks[0].Groups['body'].Value, "(?m)^$key\s*=\s*$").Count -ne 1) { throw 'Anima network settings must remain empty.' }
                }
            }
        }
    }
    $journalEvents = $provenance.PSObject.Properties['journalMissionEventsProbe'] -and [bool]$provenance.journalMissionEventsProbe
    $journalEventsMarker = Join-Path $Root 'journal-mission-events.enabled'
    if ([bool]$journalEvents -ne (Test-Path -LiteralPath $journalEventsMarker -PathType Leaf)) { throw 'Journal mission events selection changed.' }
    if ($journalEvents) {
        if (!$identityProbe -or !$provenance.journalCoordinated -or (Get-Content -LiteralPath $journalEventsMarker -Raw).Trim() -ne 'journal-events-v1') { throw 'Invalid journal mission events selection.' }
        $journalConfig = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmissionjournal.cfg') -Raw
        $eventSections = [regex]::Matches($journalConfig, '(?ms)^\[Missions\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($eventSections.Count -ne 1 -or [regex]::Matches($eventSections[0].Groups['body'].Value, '(?m)^UseApiMissionEvents\s*=').Count -ne 1 -or [regex]::Matches($eventSections[0].Groups['body'].Value, '(?m)^UseApiMissionEvents\s*=\s*true\s*$').Count -ne 1) { throw 'Journal mission events config changed.' }
    }
    $contentProbe = $provenance.PSObject.Properties['contentReferenceProbe'] -and [bool]$provenance.contentReferenceProbe
    $contentMarker = Join-Path $Root 'content-reference.enabled'
    if ([bool]$contentProbe -ne (Test-Path -LiteralPath $contentMarker -PathType Leaf)) { throw 'Content reference selection changed.' }
    if ($contentProbe -and ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $contentMarker -Raw).Trim() -ne 'refs-v1')) { throw 'Invalid content reference probe selection.' }
    $travelStation = $provenance.PSObject.Properties['travelStation'] -and [bool]$provenance.travelStation
    $travelMarker = Join-Path $Root 'travel-station.enabled'
    if ([bool]$travelStation -ne (Test-Path -LiteralPath $travelMarker -PathType Leaf)) { throw 'Travel/station selection changed.' }
    if ($travelStation) {
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $travelMarker -Raw).Trim() -ne 'travel-v1') { throw 'Invalid travel/station selection.' }
        $tsConfig = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg') -Raw
        $tsSections = [regex]::Matches($tsConfig, '(?ms)^\[Travel\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($tsSections.Count -ne 1 -or [regex]::Matches($tsSections[0].Groups['body'].Value, '(?m)^Enabled\s*=').Count -ne 1 -or [regex]::Matches($tsSections[0].Groups['body'].Value, '(?m)^Enabled\s*=\s*true\s*$').Count -ne 1) { throw 'Travel/station config changed.' }
    }
    $probe = $provenance.PSObject.Properties['persistenceProbe'] -and [bool]$provenance.persistenceProbe
    $probeMarker = Join-Path $Root 'persistence-probe.enabled'
    if ([bool]$probe -ne (Test-Path -LiteralPath $probeMarker -PathType Leaf)) { throw 'Persistence probe selection changed.' }
    if ($probe) {
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $probeMarker -Raw).Trim() -ne 'probe-v1') { throw 'Invalid persistence probe marker.' }
        $config = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg') -Raw
        $sections = [regex]::Matches($config, '(?ms)^\[Persistence\]\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($sections.Count -ne 1) { throw 'Persistence probe section changed.' }
        $config = $sections[0].Groups['body'].Value
        $roots = [regex]::Matches($config, '(?m)^Root\s*=\s*([^\r\n]+)')
        $settings = [regex]::Matches($config, '(?m)^Enabled\s*=')
        $enabled = [regex]::Matches($config, '(?m)^Enabled\s*=\s*true\s*$')
        if ($roots.Count -ne 1 -or $settings.Count -gt 1 -or $enabled.Count -ne $settings.Count -or [IO.Path]::GetFullPath($roots[0].Groups[1].Value.Trim()) -ine [IO.Path]::GetFullPath((Join-Path $Root 'state'))) { throw 'Persistence probe root/config changed.' }
    }
    if (!$probe) {
        $config = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg') -Raw
        $sections = [regex]::Matches($config, '(?ms)^\[Persistence\]\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($sections.Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^Enabled\s*=').Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^Enabled\s*=\s*false\s*$').Count -ne 1) { throw 'Legacy control must explicitly disable API-managed saves.' }
    }
    $journalCoordinated = $provenance.PSObject.Properties['journalCoordinated'] -and [bool]$provenance.journalCoordinated
    $journalMarker = Join-Path $Root 'journal-coordinated.enabled'
    if ([bool]$journalCoordinated -ne (Test-Path -LiteralPath $journalMarker -PathType Leaf)) { throw 'Journal coordinated selection changed.' }
    if ($journalCoordinated) {
        if (!$probe -or !$provenance.missionJournal -or (Get-Content -LiteralPath $journalMarker -Raw).Trim() -ne 'journal-v1') { throw 'Invalid coordinated journal selection.' }
        $config = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmissionjournal.cfg') -Raw
        $sections = [regex]::Matches($config, '(?ms)^\[Persistence\]\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($sections.Count -ne 1) { throw 'Coordinated journal section changed.' }
        foreach ($key in @('UseApiSaveData','ImportLegacySidecars')) {
            $count = [regex]::Matches($sections[0].Groups['body'].Value, "(?m)^$key\s*=").Count
            if ($key -eq 'UseApiSaveData' -and $count -eq 0) { continue } # Enabled by default.
            if ($count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, "(?m)^$key\s*=\s*true\s*$").Count -ne 1) { throw 'Journal save-data config changed.' }
        }
    }
    $stockpileCoordinated = $provenance.PSObject.Properties['stockpileCoordinated'] -and [bool]$provenance.stockpileCoordinated
    $stockpileMarker = Join-Path $Root 'stockpile-coordinated.enabled'
    if ([bool]$stockpileCoordinated -ne (Test-Path -LiteralPath $stockpileMarker -PathType Leaf)) { throw 'Stockpile coordinated selection changed.' }
    if ($stockpileCoordinated) {
        if (!$journalCoordinated -or !$provenance.stockpile -or (Get-Content -LiteralPath $stockpileMarker -Raw).Trim() -ne 'stockpile-v1') { throw 'Invalid coordinated Stockpile selection.' }
        $config = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgstockpile.cfg') -Raw
        $sections = [regex]::Matches($config, '(?ms)^\[Persistence\]\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($sections.Count -ne 1) { throw 'Coordinated Stockpile section changed.' }
        foreach ($key in @('UseApiSaveData','ImportLegacySidecars')) {
            $count = [regex]::Matches($sections[0].Groups['body'].Value, "(?m)^$key\s*=").Count
            if ($key -eq 'UseApiSaveData' -and $count -eq 0) { continue } # Enabled by default.
            if ($count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, "(?m)^$key\s*=\s*true\s*$").Count -ne 1) { throw 'Stockpile save-data config changed.' }
        }
    }
    foreach ($control in @(@{installed=$provenance.missionJournal; selected=$journalCoordinated; name='vgmissionjournal'}, @{installed=$provenance.stockpile; selected=$stockpileCoordinated; name='vgstockpile'})) {
        if ($control.installed -and !$control.selected) {
            $config = Get-Content -LiteralPath (Join-Path $Root ("game\BepInEx\config\" + $control.name + '.cfg')) -Raw
            $sections = [regex]::Matches($config, '(?ms)^\[Persistence\]\r?\n(?<body>.*?)(?=^\[|\z)')
            if ($sections.Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^UseApiSaveData\s*=').Count -ne 1 -or [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^UseApiSaveData\s*=\s*false\s*$').Count -ne 1) { throw 'Legacy consumer control must explicitly disable API-managed saves.' }
        }
    }
    $vanilla = $provenance.PSObject.Properties['vanillaLoadControl'] -and [bool]$provenance.vanillaLoadControl
    $vanillaMarker = Join-Path $Root 'vanilla-load.enabled'
    if ([bool]$vanilla -ne (Test-Path -LiteralPath $vanillaMarker -PathType Leaf)) { throw 'Vanilla control selection changed.' }
    if ($vanilla -and ($provenance.scenario -ne 'MissingApi' -or (Get-Content -LiteralPath $vanillaMarker -Raw).Trim() -ne 'control-v1')) { throw 'Invalid vanilla control marker/scenario.' }
    $overlay = if ($provenance.PSObject.Properties['assemblyOverlay']) { $provenance.assemblyOverlay } else { $null }
    $overlayMarker = Join-Path $Root 'assembly-overlay.hash'
    if ([bool]$overlay -ne (Test-Path -LiteralPath $overlayMarker -PathType Leaf)) { throw 'Assembly overlay selection changed.' }
    if ($overlay) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $bytes = [IO.File]::ReadAllBytes($overlay.source)
            $suffix = [Text.Encoding]::ASCII.GetBytes('VGModAPI-private-hash-probe-v1')
            $null = $sha.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
            $null = $sha.TransformFinalBlock($suffix, 0, $suffix.Length)
            $expected = [BitConverter]::ToString($sha.Hash).Replace('-', '')
        } finally { $sha.Dispose() }
        if ($expected -ne $overlay.modified) { throw 'Copy is not exactly source bytes plus the diagnostic overlay.' }
        $lines = @(Get-Content -LiteralPath $overlayMarker)
        if ($provenance.scenario -ne 'UnavailableApi' -or $lines.Count -ne 2 -or $lines[0] -ne $overlay.modified -or $lines[1] -ne $overlay.original -or
            $overlay.original -eq $overlay.modified -or (Get-FileHash -LiteralPath $overlay.source).Hash -ne $overlay.original -or
            (Get-FileHash -LiteralPath (Join-Path $Root 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll')).Hash -ne $overlay.modified) { throw 'Assembly overlay identity/preservation changed.' }
    }
    $journalMarker = Join-Path $Root 'missionjournal.enabled'
    if ([bool]$provenance.missionJournal -ne (Test-Path -LiteralPath $journalMarker -PathType Leaf)) { throw 'Prepared consumer selection changed.' }
    if ($provenance.missionJournal -and (Get-Content -LiteralPath $journalMarker -Raw).Trim() -ne 'pilot-v1') { throw 'Unknown consumer pilot marker.' }
    $stockpile = $provenance.PSObject.Properties['stockpile'] -and [bool]$provenance.stockpile
    $stockpileMarker = Join-Path $Root 'stockpile.enabled'
    if ([bool]$stockpile -ne (Test-Path -LiteralPath $stockpileMarker -PathType Leaf)) { throw 'Prepared Stockpile selection changed.' }
    if ($stockpile -and (Get-Content -LiteralPath $stockpileMarker -Raw).Trim() -ne 'pilot-v1') { throw 'Unknown Stockpile pilot marker.' }
    $expected = @('QualificationGuard.dll')
    if ($provenance.scenario -ne 'MissingApi') { $expected += @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll') }
    if ($provenance.scenario -eq 'Full') { $expected += @('QualificationRunner.dll','LifecycleObserver.dll') }
    if ($provenance.missionJournal) { $expected += @('VGMissionJournal.dll','Newtonsoft.Json.dll') }
    if ($stockpile) { $expected += @('VGStockpile.dll','Newtonsoft.Json.dll') }
    if ($anima) { $expected += @('VGAnima.dll') }
    $expected = @($expected | Select-Object -Unique)
    if (@($provenance.plugins.PSObject.Properties).Count -ne $expected.Count -or
        @($provenance.plugins.PSObject.Properties.Name | Where-Object { $_ -notin $expected }).Count -gt 0) { throw 'Scenario plugin allowlist mismatch.' }
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    $actual = @(Get-ChildItem -LiteralPath $plugins -Force)
    if ($actual.Count -ne @($provenance.plugins.PSObject.Properties).Count -or
        @($actual | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -notin @($provenance.plugins.PSObject.Properties.Name) }).Count -gt 0) { throw 'Prepared plugin set changed.' }
    foreach ($property in $provenance.plugins.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $plugins $property.Name) -Algorithm SHA256).Hash -ne $property.Value) { throw 'Prepared plugin changed; refuse stale provenance.' }
    }
    return $provenance
}
