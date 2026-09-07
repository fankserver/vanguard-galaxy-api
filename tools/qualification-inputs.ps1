# Prepared-input helpers; safe to exercise with synthetic files.
# Accepted Anima pilot shapes, each pinned to the exact hard API dependency that version declares.
# The consumer travel probe additionally requires the 0.4.0 shape, which is the first one that
# observes system visits through the public travel surface.
$AnimaPilotShapes = @{ '0.3.0.0' = '0.1.8'; '0.4.0.0' = '0.1.9' }
$AnimaTravelProbeVersion = '0.4.0.0'
function Assert-AnimaAssemblyMetadata($Assembly, [switch]$TravelProbe) {
    $version = $Assembly.Name.Version.ToString()
    if ($Assembly.Name.Name -ne 'VGAnima' -or !$AnimaPilotShapes.ContainsKey($version)) { throw 'Only Anima 0.3.0/0.4.0 pilot shapes accepted.' }
    if ($TravelProbe -and $version -ne $AnimaTravelProbeVersion) { throw "Anima consumer travel probe requires the $AnimaTravelProbeVersion shape; got $version." }
    $plugin = @($Assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGAnima.Plugin' })
    if ($plugin.Count -ne 1) { throw 'Anima plugin metadata missing or duplicated.' }
    $minimumApi = $AnimaPilotShapes[$version]
    $dependency = @($plugin[0].CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments.Count -eq 2 -and
        $_.ConstructorArguments[0].Value -eq 'vgmodapi' -and $_.ConstructorArguments[1].Value -eq $minimumApi
    })
    if ($dependency.Count -ne 1) { throw "Anima $version must hard-require API $minimumApi before its startup sweeper." }
}
# Consumer metadata is read with Mono.Cecil ONLY, but decoding a custom attribute's ENUM argument
# forces Cecil to RESOLVE the assembly that declares the enum. Cecil's default reader has no useful
# search path here and does not throw: it yields an attribute with ZERO constructor arguments, which
# a naive shape check reads as "the declaration is missing". That is exactly how qa-87 refused the
# candidate Echo build whose source really does declare
# [BepInDependency("vgmodapi", DependencyFlags.SoftDependency)]: BepInEx.BepInDependency/DependencyFlags
# lives in BepInEx.dll, which the reader could not resolve. Anima's declaration takes two STRINGS and
# needs no resolution, which is why only Echo hit it.
#
# So every consumer metadata read goes through this bounded, explicit resolver. Required reference
# directories: the candidate's own directory, the sandbox BepInEx core (BepInEx.dll, for the plugin
# attributes) and the installed Managed directory (the game/Unity types a consumer signature may
# name). Cecil's implicit "."/"bin" probing is removed so nothing outside that list is read, the
# candidate is opened InMemory (never locked, never written) and both the assembly and the resolver
# are disposed. Nothing is loaded into the PowerShell process and no consumer binary is copied here.
function Get-ConsumerMetadataReferenceDirs([string]$CandidateDir, [string]$SandboxRoot, [string]$GameDir) {
    $dirs = @($CandidateDir, (Join-Path $SandboxRoot 'game\BepInEx\core'), (Join-Path $GameDir 'VanguardGalaxy_Data\Managed'))
    return @($dirs | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -Unique)
}
function Read-ConsumerAssembly([string]$Path, [string[]]$ReferenceDirs) {
    $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
    foreach ($existing in @($resolver.GetSearchDirectories())) { $resolver.RemoveSearchDirectory($existing) }
    foreach ($directory in $ReferenceDirs) { $resolver.AddSearchDirectory($directory) }
    $parameters = New-Object Mono.Cecil.ReaderParameters
    $parameters.AssemblyResolver = $resolver
    $parameters.InMemory = $true
    $parameters.ReadSymbols = $false
    try { $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path, $parameters) }
    catch { $resolver.Dispose(); throw }
    return [pscustomobject]@{ Assembly = $assembly; Resolver = $resolver; ReferenceDirs = $ReferenceDirs }
}
function Close-ConsumerAssembly($Reader) {
    if ($null -eq $Reader) { return }
    if ($Reader.Assembly) { $Reader.Assembly.Dispose() }
    if ($Reader.Resolver) { $Reader.Resolver.Dispose() }
}

# Accepted Echo pilot shapes. The arrival-snap probe requires 0.7.0, the first release whose
# autopilot arrival-snap is driven by the API's RouteCompleted fact instead of a native hook.
$EchoPilotVersions = @('0.7.0.0')
$EchoTravelProbeVersion = '0.7.0.0'
function Assert-EchoAssemblyMetadata($Assembly, [switch]$TravelProbe) {
    $version = $Assembly.Name.Version.ToString()
    if ($Assembly.Name.Name -ne 'VGEcho' -or $version -notin $EchoPilotVersions) { throw 'Only Echo 0.7.0 pilot shape accepted.' }
    if ($TravelProbe -and $version -ne $EchoTravelProbeVersion) { throw "Echo consumer travel probe requires the $EchoTravelProbeVersion shape; got $version." }
    $plugin = @($Assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGEcho.Plugin' })
    if ($plugin.Count -ne 1) { throw 'Echo plugin metadata missing or duplicated.' }
    # SOFT dependency (flag 2): the same build must still load with the API absent, which the
    # separate MissingApi control exercises natively.
    $declarations = @($plugin[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' })
    # An attribute whose arguments did not decode is an UNREADABLE metadata blob, not a missing or
    # wrong declaration; reporting it as "not soft" is a false equivalence that hid the real qa-87
    # cause (the flags enum lives in BepInEx.dll, which the reader could not resolve).
    $undecodable = @($declarations | Where-Object { $_.ConstructorArguments.Count -eq 0 })
    if ($undecodable.Count -gt 0) {
        throw 'Echo BepInDependency arguments could not be decoded; the metadata reader needs BepInEx.dll (and the installed Managed references) resolvable. This is a reader configuration failure, not a consumer shape failure.'
    }
    $dependency = @($declarations | Where-Object {
        $_.ConstructorArguments.Count -eq 2 -and
        $_.ConstructorArguments[0].Value -eq 'vgmodapi' -and [int]$_.ConstructorArguments[1].Value -eq 2
    })
    if ($dependency.Count -ne 1) { throw 'Echo must declare the API as a SOFT dependency.' }
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
    if ($Provenance.PSObject.Properties['travelResilience'] -and $Provenance.travelResilience) {
        Assert-TravelResilienceReceipt $Root
    }
    if ($Provenance.PSObject.Properties['animaTravelProbe'] -and $Provenance.animaTravelProbe) {
        Assert-AnimaTravelReceipt $Root
    }
    if ($Provenance.PSObject.Properties['echoTravelProbe'] -and $Provenance.echoTravelProbe) {
        Assert-EchoTravelReceipt $Root
    }
    if ($Provenance.PSObject.Properties['echoAbsentProbe'] -and $Provenance.echoAbsentProbe) {
        Assert-EchoAbsentReceipt $Root $Provenance
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
# The third separate optional phase reserves its own process time ON TOP of the other two; it never
# replaces them and never turns their optional NOT-RUN rows for these cells into coverage.
$TravelResiliencePhase = 'travel-resilience-v1'
$TravelResilienceRequiredCases = @('empty-origin-reroute','restore-relink-dock','stale-session-replay')
$TravelResilienceBudgetSeconds = 2400
# MANDATORY subcase row of restore-relink-dock: the same-docking-size branch, driven as the native
# re-init of the CURRENT owned ship, so it needs no second owned ship and a not-run row is refused.
$TravelResilienceRequiredSubcaseRows = @('restore-reinit-same-size')
# The actual-consumer probe REUSES the two native travel phases in place (it must observe them
# before the Anima mission pilot disposes the consumer's visit observer), so it reserves only its
# own consumer loads/saves on top of their existing reservations.
$AnimaTravelPhase = 'anima-travel-consumer-v1'
$AnimaTravelRequiredCases = @('consumer-binding','non-travel-quiet','gate-arrival-visit','wormhole-arrival-visit','visit-persistence','recording-degraded')
$AnimaTravelRequiredSubcaseRows = @('gate-visit-reload','gate-visit-rollback','regional-history-fixture')
$AnimaTravelBudgetSeconds = 1200
# The scenario names the two reused phases record in result.txt, used as the ordering proof.
$AnimaTravelReusedPhaseScenarios = @("native-travel-station-$TravelStationPhase", "native-travel-$TravelCrossSystemPhase")
# The Echo arrival-snap probe reuses the SAME two phases and owns their ordering, so exactly one
# consumer probe may be selected per run (refused at Prepare and re-checked in provenance).
$EchoTravelPhase = 'echo-travel-consumer-v1'
$EchoTravelRequiredCases = @('arrival-snap-binding','no-snap-quiet','in-system-final-snap','gate-final-snap','wormhole-final-snap','earlier-subscriber-supersession','snap-stop-degradation')
$EchoTravelRequiredSubcaseRows = @('declared-probe-controls')
$EchoTravelBudgetSeconds = 1800
$EchoTravelReusedPhaseScenarios = $AnimaTravelReusedPhaseScenarios
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
# The resilience phase is validated separately and with its own mandatory cases: a passing
# in-system or cross-system receipt can never stand in for it.
function Assert-TravelResilienceReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Travel resilience' 'travel-resilience' $TravelResiliencePhase $TravelResilienceRequiredCases $TravelResilienceBudgetSeconds
    # Mandatory subcase rows are checked independently of the case identities: they are not coverage
    # of a case, but a missing, duplicated, not-run or failed subcase row is never accepted.
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'travel-resilience.txt'))
    if ($summary -notcontains ("required-subcases=" + ($TravelResilienceRequiredSubcaseRows -join ','))) { throw 'Travel resilience receipt declares different mandatory subcases.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'travel-resilience-receipt.tsv'))
    foreach ($subcase in $TravelResilienceRequiredSubcaseRows) {
        if ($subcase -in $TravelResilienceRequiredCases) { throw 'A mandatory subcase row must not also be a case identity.' }
        $matched = @($rows | Where-Object { ($_ -split "`t")[0] -eq $subcase })
        if ($matched.Count -ne 1) { throw "Mandatory travel resilience subcase is missing or duplicated: $subcase" }
        if (($matched[0] -split "`t")[2] -ne 'passed') { throw "Mandatory travel resilience subcase did not pass: $subcase" }
        if ($summary -notcontains "required-subcase $subcase=passed") { throw "Travel resilience summary and receipt disagree about $subcase." }
    }
}
# The actual-consumer travel probe is validated separately and with its own mandatory cases. It
# REUSES the two native travel phases rather than repeating them, so its receipt can never stand in
# for theirs and theirs can never stand in for it.
function Assert-AnimaTravelReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Anima consumer travel' 'anima-travel' $AnimaTravelPhase $AnimaTravelRequiredCases $AnimaTravelBudgetSeconds
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'anima-travel.txt'))
    if ($summary -notcontains ("required-subcases=" + ($AnimaTravelRequiredSubcaseRows -join ','))) { throw 'Anima consumer travel receipt declares different mandatory subcases.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'anima-travel-receipt.tsv'))
    foreach ($subcase in $AnimaTravelRequiredSubcaseRows) {
        if ($subcase -in $AnimaTravelRequiredCases) { throw 'A mandatory subcase row must not also be a case identity.' }
        $matched = @($rows | Where-Object { ($_ -split "`t")[0] -eq $subcase })
        if ($matched.Count -ne 1) { throw "Mandatory Anima consumer travel subcase is missing or duplicated: $subcase" }
        if (($matched[0] -split "`t")[2] -ne 'passed') { throw "Mandatory Anima consumer travel subcase did not pass: $subcase" }
        if ($summary -notcontains "required-subcase $subcase=passed") { throw "Anima consumer travel summary and receipt disagree about $subcase." }
    }
    # ORDERING PROOF from the run's own result log: the consumer probe observed both reused native
    # travel phases, each recorded exactly once, and completed BEFORE the Anima mission pilot, whose
    # final StopProvider permanently disposes the consumer's visit observer.
    $passed = @(Get-Content -LiteralPath (Join-Path $Root 'result.txt'))
    $expected = @($AnimaTravelPhase) + $AnimaTravelReusedPhaseScenarios + @('native-anima-api-missions')
    foreach ($name in $expected) {
        if (@($passed | Where-Object { $_ -eq $name }).Count -ne 1) { throw "Expected exactly one recorded '$name' scenario in the run result." }
    }
    $probeIndex = [Array]::IndexOf($passed, $AnimaTravelPhase)
    foreach ($name in $AnimaTravelReusedPhaseScenarios) {
        if ([Array]::IndexOf($passed, $name) -gt $probeIndex) { throw "The consumer probe completed before the reused phase '$name'." }
    }
    if ([Array]::IndexOf($passed, 'native-anima-api-missions') -lt $probeIndex) { throw 'The Anima mission pilot ran before the consumer travel probe; its StopProvider disposes the observer the probe needs.' }
}
# The Echo arrival-snap probe is validated separately and with its own mandatory cases; it reuses
# the two native travel phases rather than repeating them, so neither receipt can stand in for the
# other.
function Assert-EchoTravelReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Echo consumer travel' 'echo-travel' $EchoTravelPhase $EchoTravelRequiredCases $EchoTravelBudgetSeconds
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'echo-travel.txt'))
    if ($summary -notcontains ("required-subcases=" + ($EchoTravelRequiredSubcaseRows -join ','))) { throw 'Echo consumer travel receipt declares different mandatory subcases.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'echo-travel-receipt.tsv'))
    foreach ($subcase in $EchoTravelRequiredSubcaseRows) {
        if ($subcase -in $EchoTravelRequiredCases) { throw 'A mandatory subcase row must not also be a case identity.' }
        $matched = @($rows | Where-Object { ($_ -split "`t")[0] -eq $subcase })
        if ($matched.Count -ne 1) { throw "Mandatory Echo consumer travel subcase is missing or duplicated: $subcase" }
        if (($matched[0] -split "`t")[2] -ne 'passed') { throw "Mandatory Echo consumer travel subcase did not pass: $subcase" }
        if ($summary -notcontains "required-subcase $subcase=passed") { throw "Echo consumer travel summary and receipt disagree about $subcase." }
        # The declared controls row must actually name them; an empty setup row proves nothing.
        foreach ($control in @('idleTimerSeed','suppressedFindActivityBodies','subscriptionReorderings','EtaSync=false')) {
            if (($matched[0] -split "`t")[7] -notlike "*$control*") { throw "Echo consumer travel controls row does not declare $control." }
        }
    }
    # ORDERING PROOF from the run's own result log: the probe observed both reused native travel
    # phases, each recorded exactly once, and completed after them.
    $passed = @(Get-Content -LiteralPath (Join-Path $Root 'result.txt'))
    $expected = @($EchoTravelPhase) + $EchoTravelReusedPhaseScenarios
    foreach ($name in $expected) {
        if (@($passed | Where-Object { $_ -eq $name }).Count -ne 1) { throw "Expected exactly one recorded '$name' scenario in the run result." }
    }
    $probeIndex = [Array]::IndexOf($passed, $EchoTravelPhase)
    foreach ($name in $EchoTravelReusedPhaseScenarios) {
        if ([Array]::IndexOf($passed, $name) -gt $probeIndex) { throw "The Echo consumer probe completed before the reused phase '$name'." }
    }
}
# The API-ABSENT control is its own MissingApi run: plugin load and patch installation are recorded
# separately from observed hook invocations, which only exist with the gameplay load control.
function Assert-EchoAbsentReceipt([string]$Root, $Provenance) {
    $receipt = Join-Path $Root 'echo-absent.txt'
    if (!(Test-Path -LiteralPath $receipt)) { throw 'Echo API-absent control did not complete.' }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines[0] -cne 'PASS') { throw 'Echo API-absent control did not report PASS.' }
    if ($lines -notcontains 'arrivalSnap=unbound') { throw 'Echo API-absent control did not report an unbound arrival-snap.' }
    if ($lines -notcontains ('echoVersion=' + $EchoTravelProbeVersion.Substring(0, 5))) { throw 'Echo API-absent control reported another consumer version.' }
    $gameplay = $null -ne $Provenance -and $Provenance.PSObject.Properties['vanillaLoadControl'] -and [bool]$Provenance.vanillaLoadControl
    $invocations = @($lines | Where-Object { $_ -like 'idleUpdateInvocations=*' })
    if ($invocations.Count -ne 1) { throw 'Echo API-absent control did not record its native hook invocations.' }
    $count = [int]($invocations[0] -replace '^idleUpdateInvocations=', '')
    if ($gameplay -and $count -le 0) { throw 'Echo API-absent control observed no native hook invocation during the gameplay load control.' }
    if (!$gameplay -and $count -ne 0) { throw 'Echo API-absent control claims hook invocations without a gameplay load control.' }
    if ($lines -notcontains ('gameplayLoadControl=' + $gameplay)) { throw 'Echo API-absent control misreports its gameplay load control selection.' }
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
    # The actual-consumer travel probe is an ADDITIONAL selection that requires the installed
    # consumer AND both native travel phases plus the wormhole fixture, because it only compares the
    # consumer's own records against arrivals those phases already qualified.
    $animaTravel = $provenance.PSObject.Properties['animaTravelProbe'] -and [bool]$provenance.animaTravelProbe
    $animaTravelMarker = Join-Path $Root 'anima-travel.enabled'
    if ([bool]$animaTravel -ne (Test-Path -LiteralPath $animaTravelMarker -PathType Leaf)) { throw 'Anima consumer travel selection changed.' }
    if ($animaTravel) {
        if (!$anima -or !$travelStation -or !$travelCrossSystem -or !$wormholeFixture -or
            (Get-Content -LiteralPath $animaTravelMarker -Raw).Trim() -ne 'anima-travel-v1') { throw 'Invalid Anima consumer travel selection.' }
        if (!$provenance.PSObject.Properties['animaTravelBudgetSeconds'] -or
            [int]$provenance.animaTravelBudgetSeconds -ne $AnimaTravelBudgetSeconds) { throw 'Anima consumer travel budget reservation changed.' }
        # REQUIRED, not merely checked when present: a provenance with the property removed must
        # never pass while the probe is selected.
        if (!$provenance.PSObject.Properties['animaVersion'] -or $provenance.animaVersion -ne $AnimaTravelProbeVersion) { throw 'Anima consumer travel probe requires the pinned consumer version in provenance.' }
    }
    # The Echo consumer selection: its own binary, its exact source revision and the sandbox-only
    # configuration that keeps ETA-sync off so an ETA write can never look like an arrival snap.
    $echo = $provenance.PSObject.Properties['echo'] -and [bool]$provenance.echo
    $echoMarker = Join-Path $Root 'echo.enabled'
    if ([bool]$echo -ne (Test-Path -LiteralPath $echoMarker -PathType Leaf)) { throw 'Echo selection changed.' }
    if ($echo) {
        if ($provenance.echoRevision -notmatch '^[0-9a-f]{40}$' -or (Get-Content -LiteralPath $echoMarker -Raw).Trim() -ne 'echo-v1') { throw 'Invalid Echo selection.' }
        $echoConfig = Get-Content -LiteralPath (Join-Path $Root 'game\BepInEx\config\vgecho.cfg') -Raw
        $blocks = [regex]::Matches($echoConfig, '(?ms)^\[Autopilot\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
        if ($blocks.Count -ne 1) { throw 'Echo autopilot configuration section changed.' }
        foreach ($entry in @(@{Key='TimingEnabled';Value='true'}, @{Key='ArrivalSnap';Value='true'}, @{Key='EtaSync';Value='false'})) {
            if ([regex]::Matches($blocks[0].Groups['body'].Value, "(?m)^$($entry.Key)\s*=").Count -ne 1 -or
                [regex]::Matches($blocks[0].Groups['body'].Value, "(?m)^$($entry.Key)\s*=\s*$($entry.Value)\s*$").Count -ne 1) { throw 'Echo arrival-snap configuration changed.' }
        }
    }
    $echoTravel = $provenance.PSObject.Properties['echoTravelProbe'] -and [bool]$provenance.echoTravelProbe
    $echoTravelMarker = Join-Path $Root 'echo-travel.enabled'
    if ([bool]$echoTravel -ne (Test-Path -LiteralPath $echoTravelMarker -PathType Leaf)) { throw 'Echo consumer travel selection changed.' }
    if ($echoTravel) {
        if (!$echo -or !$travelStation -or !$travelCrossSystem -or !$wormholeFixture -or
            (Get-Content -LiteralPath $echoTravelMarker -Raw).Trim() -ne 'echo-travel-v1') { throw 'Invalid Echo consumer travel selection.' }
        if (!$provenance.PSObject.Properties['echoTravelBudgetSeconds'] -or
            [int]$provenance.echoTravelBudgetSeconds -ne $EchoTravelBudgetSeconds) { throw 'Echo consumer travel budget reservation changed.' }
        if (!$provenance.PSObject.Properties['echoVersion'] -or $provenance.echoVersion -ne $EchoTravelProbeVersion) { throw 'Echo consumer travel probe requires the pinned consumer version in provenance.' }
        if ($anima -and $animaTravel) { throw 'Both consumer travel probes claim the reused travel phases.' }
        if ($provenance.scenario -ne 'Full') { throw 'Echo consumer travel probe requires Full.' }
    }
    $echoAbsent = $provenance.PSObject.Properties['echoAbsentProbe'] -and [bool]$provenance.echoAbsentProbe
    $echoAbsentMarker = Join-Path $Root 'echo-absent.enabled'
    if ([bool]$echoAbsent -ne (Test-Path -LiteralPath $echoAbsentMarker -PathType Leaf)) { throw 'Echo API-absent selection changed.' }
    if ($echoAbsent) {
        if (!$echo -or $provenance.scenario -ne 'MissingApi' -or $echoTravel -or
            (Get-Content -LiteralPath $echoAbsentMarker -Raw).Trim() -ne 'echo-absent-v1') { throw 'Invalid Echo API-absent selection.' }
    }
    # The resilience phase is an ADDITIONAL selection on top of the in-system phase; it reuses the
    # same [Travel] capability configuration and reserves its own separate process budget.
    $travelResilience = $provenance.PSObject.Properties['travelResilience'] -and [bool]$provenance.travelResilience
    $resilienceMarker = Join-Path $Root 'travel-resilience.enabled'
    if ([bool]$travelResilience -ne (Test-Path -LiteralPath $resilienceMarker -PathType Leaf)) { throw 'Travel resilience selection changed.' }
    if ($travelResilience) {
        if (!$travelStation -or (Get-Content -LiteralPath $resilienceMarker -Raw).Trim() -ne 'resilience-v1') { throw 'Invalid travel resilience selection.' }
        if (!$provenance.PSObject.Properties['travelResilienceBudgetSeconds'] -or
            [int]$provenance.travelResilienceBudgetSeconds -ne $TravelResilienceBudgetSeconds) { throw 'Travel resilience budget reservation changed.' }
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
    if ($echo) { $expected += @('VGEcho.dll') }
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
