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
    if ($Provenance.PSObject.Properties['travelCrossSystem'] -and $Provenance.travelCrossSystem) {
        Assert-TravelCrossSystemReceipt $Root $Provenance
    }
}
$TravelStationPhase = 'travel-in-system-station-v1'
$TravelStationRequiredCases = @('initial-placement','station-undock','in-system-route','early-cancel','chained-route','station-dock')
$TravelStationReceiptHeader = @('case','description','status','nativeIdentity','session','operation','evidence','detail')
# Process time RESERVED for the phase on top of the base budget that covers the existing Full
# pilots. The pilot publishes its own derived budgetSeconds; a receipt claiming more than the
# reservation is refused, so the two cannot drift apart silently.
$QualificationBaseTimeoutSeconds = 1800
$TravelStationBudgetSeconds = 1500
$TravelStationEventHeader = @('apiSequence','surface','case','session','operation','kind','mode','origin','requested','actual','gameSeconds','dwellSeconds')
# The separate optional cross-system phase reserves its own process time ON TOP of the in-system
# phase; it never replaces it and never turns that phase's optional NOT-RUN rows into coverage.
$TravelCrossSystemPhase = 'travel-cross-system-v1'
$TravelCrossSystemRequiredCases = @('cross-system-jumpgate','cross-system-wormhole')
$TravelCrossSystemBudgetSeconds = 2400
# Optional opt-in sandbox fixture preparation row. It is deliberately NOT a required case: creating
# disposable native test data is never coverage of a travel routine.
$TravelWormholeFixtureCase = 'wormhole-fixture-setup'
$TravelWormholeFactorySignature = 'factory=Source.Simulation.World.WormholeSpawner.PlaceWormhole('
# Independent verification of the pilot's own claim: the declared phase, every mandatory case
# identity, the receipt/event files and the identities they share must all agree. A first line of
# PASS is never accepted on its own. The two travel phases publish the same receipt/event shape,
# so they share this validator with their own file prefix, phase identity, required-case list and
# reserved budget; neither phase can satisfy the other's mandatory cases.
function Assert-TravelPhaseReceipt([string]$Root, [string]$Label, [string]$Prefix, [string]$Phase, [string[]]$RequiredCases, [int]$BudgetSeconds) {
    $summaryPath = Join-Path $Root "$Prefix.txt"
    $receiptPath = Join-Path $Root "$Prefix-receipt.tsv"
    $eventsPath = Join-Path $Root "$Prefix-events.tsv"
    foreach ($path in @($summaryPath, $receiptPath, $eventsPath)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Label pilot output missing: $path" }
    }
    # Launcher outcome first: a killed, timed-out, unknown-exit or unexpected-exit run can never be
    # reported as a pass. The game's own quit handler self-terminates with a source-proven code (see
    # Assert-QualificationExitOutcome); any other code still refuses.
    $outcomePath = Join-Path $Root 'run-outcome.json'
    if (Test-Path -LiteralPath $outcomePath -PathType Leaf) {
        $outcome = Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json
        Assert-QualificationExitOutcome $outcome "$Label run"
    }
    $summary = @(Get-Content -LiteralPath $summaryPath)
    if ($summary.Count -lt 3 -or $summary[0] -cne 'PASS') { throw "$Label pilot did not complete with PASS." }
    $budget = @($summary | Where-Object { $_ -like 'budgetSeconds=*' })
    if ($budget.Count -ne 1) { throw "$Label receipt does not declare its phase budget." }
    $declared = [int]($budget[0] -replace '^budgetSeconds=', '')
    if ($declared -le 0 -or $declared -gt $BudgetSeconds) { throw "$Label phase budget $declared exceeds the reserved $BudgetSeconds seconds." }
    if ($summary -notcontains "phase=$Phase") { throw "$Label receipt does not declare the qualified phase." }
    if ($summary -notcontains ("required=" + ($RequiredCases -join ','))) { throw "$Label receipt declares different required cases." }
    if ($summary -notcontains 'fault=none') { throw "$Label pilot recorded a fault." }
    $rows = @(Get-Content -LiteralPath $receiptPath)
    if ($rows.Count -lt 2 -or (($rows[0] -split "`t") -join ',') -cne ($TravelStationReceiptHeader -join ',')) { throw "$Label receipt header changed." }
    $records = @($rows[1..($rows.Count - 1)] | ForEach-Object {
        $columns = $_ -split "`t"
        if ($columns.Count -ne $TravelStationReceiptHeader.Count) { throw "Malformed $Label receipt row." }
        [pscustomobject]@{ Case=$columns[0]; Status=$columns[2]; Session=$columns[4]; Operation=$columns[5]; Evidence=$columns[6] }
    })
    if (@($records | Where-Object { $_.Status -eq 'failed' }).Count -gt 0) { throw "$Label receipt contains failed cases." }
    if (@($records | Where-Object { $_.Status -notin @('passed','not-run') }).Count -gt 0) { throw "Unknown $Label case status." }
    $passed = @($records | Where-Object { $_.Status -eq 'passed' })
    $notRun = @($records | Where-Object { $_.Status -eq 'not-run' })
    if ($passed.Count -eq 0) { throw "$Label receipt has no passed case; empty coverage is not a pass." }
    if ($summary -notcontains ("rows=" + $records.Count + " passed=" + $passed.Count + " failed=0 notRun=" + $notRun.Count)) { throw "$Label summary counts disagree with the receipt." }
    $events = @(Get-Content -LiteralPath $eventsPath)
    if ($events.Count -lt 2 -or (($events[0] -split "`t") -join ',') -cne ($TravelStationEventHeader -join ',')) { throw "$Label event trace missing or its header changed." }
    $eventRows = @($events[1..($events.Count - 1)] | ForEach-Object {
        $columns = $_ -split "`t"
        if ($columns.Count -ne $TravelStationEventHeader.Count) { throw "Malformed $Label event row." }
        [pscustomobject]@{ Sequence=$columns[0]; Surface=$columns[1]; Case=$columns[2]; Session=$columns[3]; Operation=$columns[4] }
    })
    $eventSessions = @($eventRows | ForEach-Object { $_.Session } | Select-Object -Unique)
    $eventOperations = @($eventRows | ForEach-Object { $_.Operation } | Select-Object -Unique)
    # Case evidence is validated by explicit surface/apiSequence/session references, NOT by the
    # event's case label: native cases legitimately overlap (the return hop's dock is observed while
    # the chained route is still driving).
    $eventKeys = @{}
    foreach ($row in $eventRows) { $eventKeys[($row.Surface + ':' + $row.Sequence + ':' + $row.Session)] = $true }
    foreach ($case in $RequiredCases) {
        $matched = @($records | Where-Object { $_.Case -eq $case })
        if ($matched.Count -ne 1) { throw "Required $Label case is missing or duplicated: $case" }
        if ($matched[0].Status -ne 'passed') { throw "Required $Label case did not pass: $case" }
        if ($summary -notcontains "required-case $case=passed") { throw "$Label summary and receipt disagree about $case." }
        if ($matched[0].Session -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { throw "Required $Label case has no session identity: $case" }
        if (!$matched[0].Evidence) { throw "Required $Label case references no observed public events: $case" }
    }
    foreach ($record in $passed) {
        if ($record.Session -notin $eventSessions) { throw "Receipt session identity is absent from the event trace: $($record.Case)" }
        if ($record.Operation -and $record.Operation -notin $eventOperations) { throw "Receipt operation identity is absent from the event trace: $($record.Case)" }
        foreach ($group in @($record.Evidence -split ';' | Where-Object { $_ })) {
            $parts = $group -split ':'
            if ($parts.Count -ne 2 -or $parts[0] -notin @('travel','station')) { throw "Malformed evidence reference in $($record.Case): $group" }
            foreach ($sequence in @($parts[1] -split ',' | Where-Object { $_ })) {
                if (-not $eventKeys.ContainsKey($parts[0] + ':' + $sequence + ':' + $record.Session)) {
                    throw "Case $($record.Case) references an event that is not in the trace for its session: $($parts[0]):$sequence"
                }
            }
        }
    }
}
function Assert-TravelStationReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Travel/station' 'travel-station' $TravelStationPhase $TravelStationRequiredCases $TravelStationBudgetSeconds
}
# The cross-system phase is validated separately and with its own mandatory cases: a passing
# in-system receipt can never stand in for it, and its own optional NOT-RUN rows are never coverage.
function Assert-TravelCrossSystemReceipt([string]$Root, $Provenance) {
    Assert-TravelPhaseReceipt $Root 'Travel cross-system' 'travel-cross-system' $TravelCrossSystemPhase $TravelCrossSystemRequiredCases $TravelCrossSystemBudgetSeconds
    if ($TravelWormholeFixtureCase -in $TravelCrossSystemRequiredCases) { throw 'Fixture preparation must never be a mandatory case.' }
    # A fixture-preparation row is only legitimate under the explicit opt-in selection, must record
    # the native factory it used, must claim no observed travel events and must have seen none.
    $selected = $null -ne $Provenance -and $Provenance.PSObject.Properties['travelWormholeFixture'] -and [bool]$Provenance.travelWormholeFixture
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'travel-cross-system-receipt.tsv'))
    $setup = @($rows | Where-Object { ($_ -split "`t")[0] -eq $TravelWormholeFixtureCase })
    if (!$selected -and $setup.Count -gt 0) { throw 'Wormhole fixture preparation was recorded without the explicit fixture selection.' }
    if ($setup.Count -gt 1) { throw 'Wormhole fixture preparation recorded more than once.' }
    if ($setup.Count -eq 1) {
        $columns = $setup[0] -split "`t"
        if ($columns[2] -ne 'passed') { throw 'Wormhole fixture preparation did not complete.' }
        if ($columns[6]) { throw 'Fixture preparation must not claim observed travel events.' }
        if ($columns[7] -notlike "*$TravelWormholeFactorySignature*") { throw 'Wormhole fixture preparation does not record the native factory it used.' }
        if ($columns[7] -notlike '*travelFactsDuringCreation=0*') { throw 'Wormhole fixture preparation observed travel facts.' }
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
        if (!$provenance.PSObject.Properties['travelStationBudgetSeconds'] -or
            [int]$provenance.travelStationBudgetSeconds -ne $TravelStationBudgetSeconds) { throw 'Travel/station budget reservation changed.' }
        $tsConfig = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg') -Raw
        $tsSections = [regex]::Matches($tsConfig, '(?ms)^\[Travel\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($tsSections.Count -ne 1 -or [regex]::Matches($tsSections[0].Groups['body'].Value, '(?m)^Enabled\s*=').Count -ne 1 -or [regex]::Matches($tsSections[0].Groups['body'].Value, '(?m)^Enabled\s*=\s*true\s*$').Count -ne 1) { throw 'Travel/station config changed.' }
    }
    # The cross-system phase is an ADDITIONAL selection on top of the in-system phase; it reuses the
    # same [Travel] capability configuration and reserves its own separate process budget.
    $travelCrossSystem = $provenance.PSObject.Properties['travelCrossSystem'] -and [bool]$provenance.travelCrossSystem
    $crossMarker = Join-Path $Root 'travel-cross-system.enabled'
    if ([bool]$travelCrossSystem -ne (Test-Path -LiteralPath $crossMarker -PathType Leaf)) { throw 'Travel cross-system selection changed.' }
    if ($travelCrossSystem) {
        if (!$travelStation -or (Get-Content -LiteralPath $crossMarker -Raw).Trim() -ne 'cross-system-v1') { throw 'Invalid travel cross-system selection.' }
        if (!$provenance.PSObject.Properties['travelCrossSystemBudgetSeconds'] -or
            [int]$provenance.travelCrossSystemBudgetSeconds -ne $TravelCrossSystemBudgetSeconds) { throw 'Travel cross-system budget reservation changed.' }
    }
    # Opt-in disposable sandbox test data for the wormhole case. It only ever creates native content
    # in the freshly loaded sandbox fixture clone; the marker and provenance flag must agree exactly,
    # so an unselected run can never create content and a selected run is recorded as such.
    $wormholeFixture = $provenance.PSObject.Properties['travelWormholeFixture'] -and [bool]$provenance.travelWormholeFixture
    $wormholeMarker = Join-Path $Root 'travel-wormhole-fixture.enabled'
    if ([bool]$wormholeFixture -ne (Test-Path -LiteralPath $wormholeMarker -PathType Leaf)) { throw 'Wormhole fixture selection changed.' }
    if ($wormholeFixture) {
        if (!$travelCrossSystem -or (Get-Content -LiteralPath $wormholeMarker -Raw).Trim() -ne 'wormhole-fixture-v1') { throw 'Invalid wormhole fixture selection.' }
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
