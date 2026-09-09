# Prepared-input helpers; safe to exercise with synthetic files.
. (Join-Path $PSScriptRoot 'qualification-bar-consumers.ps1')
. (Join-Path $PSScriptRoot 'qualification-bars.ps1')
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
# The ARCHIVED TravelJournal is accepted only as the exact prebuilt, source-attested binary. Its
# assembly version (0.1.0) and its BepInEx plugin version (0.2.0) deliberately differ, and its
# embedded informational version carries the archive revision it was compiled from; all three are
# required, so a rebuilt or otherwise different binary can never be prepared.
$TravelJournalAssemblyVersion = '0.1.0.0'
$TravelJournalPluginVersion = '0.2.0'
# HARD PIN, committed here and not caller-supplied: exactly one archived binary, built from exactly
# one archive revision, is ever accepted. A caller may repeat these pins but can never widen them,
# so no rebuilt, re-signed or otherwise different VGTravelJournal.dll can be prepared or deployed -
# assembly and plugin version metadata alone would not distinguish a rebuild.
$TravelJournalPinnedRevision = '818d8b7e13a7841703bdd99e173e7dd993f6895c'
$TravelJournalPinnedSha256 = 'f253c3eefb967af7a1472dfb48bd926bff14b84b7219208facdc594387b1fdad'
function Assert-TravelJournalPins([string]$Revision, [string]$Sha256, [string]$ActualHash) {
    if ($Revision -ne $TravelJournalPinnedRevision) {
        throw "The archived TravelJournal source revision '$Revision' is not the committed pin $TravelJournalPinnedRevision."
    }
    if ($Sha256.ToUpperInvariant() -ne $TravelJournalPinnedSha256.ToUpperInvariant()) {
        throw "The archived TravelJournal binary hash pin '$Sha256' is not the committed pin $TravelJournalPinnedSha256."
    }
    if ($ActualHash.ToUpperInvariant() -ne $TravelJournalPinnedSha256.ToUpperInvariant()) {
        throw "Archived TravelJournal binary hash $ActualHash is not the committed pin $TravelJournalPinnedSha256; the archive must not be rebuilt."
    }
}
function Assert-TravelJournalAssemblyMetadata($Assembly, [string]$Revision) {
    if ($Assembly.Name.Name -ne 'VGTravelJournal' -or $Assembly.Name.Version.ToString() -ne $TravelJournalAssemblyVersion) {
        throw "Only the archived VGTravelJournal $TravelJournalAssemblyVersion prebuilt is accepted."
    }
    $informational = @($Assembly.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute' })
    if ($informational.Count -ne 1) { throw 'The archived assembly declares no informational version to attest its source revision.' }
    $expected = '0.1.0+' + $Revision
    if ($informational[0].ConstructorArguments[0].Value -ne $expected) {
        throw "The archived assembly's embedded source revision is '$($informational[0].ConstructorArguments[0].Value)', not the pinned '$expected'."
    }
    $plugin = @($Assembly.MainModule.Types | Where-Object { $_.FullName -eq 'VGTravelJournal.Plugin' })
    if ($plugin.Count -ne 1) { throw 'Archived TravelJournal plugin metadata missing or duplicated.' }
    $bepInPlugin = @($plugin[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
    if ($bepInPlugin.Count -ne 1 -or $bepInPlugin[0].ConstructorArguments[0].Value -ne 'vgtraveljournal' -or
        $bepInPlugin[0].ConstructorArguments[2].Value -ne $TravelJournalPluginVersion) {
        throw "The archived plugin must declare vgtraveljournal $TravelJournalPluginVersion."
    }
}
# The sandbox-only journal configuration is validated by its PARSED SEMANTICS, never by its bytes:
# BepInEx 5.4 rewrites a plugin's config file on the first Bind (SaveOnConfigSet is on by default and
# the archive never disables it), adding its own header, '##' descriptions and spacing. Comments and
# unknown sections/keys are therefore benign, while a missing, duplicated or changed effective value
# is refused - a duplicate key is ambiguous and is never resolved silently.
$TravelJournalConfigRelativePath = 'game\BepInEx\config\vgtraveljournal.cfg'
$TravelJournalConfigRequired = @(@{Key='Journal/Verbose'; Value='true'}, @{Key='Journal/MaxEvents'; Value='0'})
function Assert-QualificationAssemblyRevision($Assembly, [string]$Name, [string]$Revision) {
    if ($Assembly.Name.Name -cne $Name -or $Revision -cnotmatch '^[0-9a-f]{40}$') { throw 'Qualification assembly identity/revision invalid.' }
    $versions = @($Assembly.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute' })
    if ($versions.Count -ne 1 -or $versions[0].ConstructorArguments[0].Value -cnotmatch ('^[0-9]+\.[0-9]+\.[0-9]+\+' + $Revision + '$')) { throw "Stale qualification assembly: $Name is not built from $Revision." }
}

function Assert-StoryAuthorMetadata($Assembly, [string]$Name, [string]$Revision) {
    $identities = @{ OwnedStoryCampaign='vg-story-campaign'; OwnedStoryJob='vg-story-job' }
    if (!$identities.ContainsKey($Name) -or $Assembly.Name.Name -cne $Name -or $Revision -cnotmatch '^[0-9a-f]{40}$') { throw 'Unexpected story author identity or revision.' }
    $versions = @($Assembly.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute' })
    if ($versions.Count -ne 1 -or $versions[0].ConstructorArguments[0].Value -cnotmatch ('^[0-9]+\.[0-9]+\.[0-9]+\+' + $Revision + '$')) { throw 'Story author source revision mismatch.' }
    $types = @($Assembly.MainModule.Types | Where-Object { $_.FullName -ceq ($Name + '.Plugin') })
    if ($types.Count -ne 1) { throw 'Story author plugin type missing.' }
    $plugins = @($types[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
    if ($plugins.Count -ne 1 -or $plugins[0].ConstructorArguments[0].Value -cne $identities[$Name] -or $plugins[0].ConstructorArguments[2].Value -cne '0.1.0') { throw 'Story author plugin metadata mismatch.' }
    $dependencies = @($types[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' })
    if ($dependencies.Count -ne 1 -or $dependencies[0].ConstructorArguments[0].Value -cne 'vgmodapi' -or [int]$dependencies[0].ConstructorArguments[1].Value -ne 1) { throw 'Story author requires the API hard dependency.' }
}

function Assert-StoryIsolation($Selection) {
    foreach ($name in @('menuInspection','modMenuProbe','modInformationProbe','travelStation','travelCrossSystem','travelWormholeFixture','travelResilience','travelRecovery','travelFastLane','missionTransitionsProbe','missionIdentityProbe','contentReferenceProbe','journalMissionEventsProbe','journalCoordinated','stockpileCoordinated','vanillaLoadControl','assemblyOverlay','echoAbsentProbe','echoTravelProbe','animaTravelProbe','travelJournalComparison')) {
        if ($Selection.PSObject.Properties[$name] -and $Selection.$name) { throw "Story probe conflicts with $name." }
    }
}

function Assert-StoryConfiguration([string]$Root) {
    $path = Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'
    $entries = Get-TravelJournalConfigEntries $path
    foreach ($key in @('Story/Enabled','Story/Protection')) {
        if (!$entries.ContainsKey($key) -or $entries[$key] -ine 'true') { throw "Story configuration requires $key=true." }
    }
    Assert-ApiPersistenceRoot $Root
}

function Assert-ApiPersistenceRoot([string]$Root) {
    $entries = Get-TravelJournalConfigEntries (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg')
    if (!$entries.ContainsKey('Persistence/Root') -or [IO.Path]::GetFullPath($entries['Persistence/Root']) -ine [IO.Path]::GetFullPath((Join-Path $Root 'state'))) { throw 'API persistence root must remain inside the sandbox state directory.' }
}

function Get-TravelJournalConfigEntries([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'The archived TravelJournal configuration file is missing.' }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The archived TravelJournal configuration must be a regular file, not a link.' }
    $entries = @{}
    $section = ''
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $text = $line.Trim()
        if ($text -eq '' -or $text.StartsWith('#')) { continue }   # BepInEx's own header/descriptions
        if ($text -match '^\[(?<name>[^\]]+)\]$') { $section = $Matches['name'].Trim(); continue }
        $split = $text.IndexOf('=')
        if ($split -lt 1) { throw 'Unparseable entry in the archived TravelJournal configuration.' }
        $key = $section + '/' + $text.Substring(0, $split).Trim()
        if ($entries.ContainsKey($key)) { throw "Duplicate '$key' entry in the archived TravelJournal configuration; its effective value is ambiguous." }
        $entries[$key] = $text.Substring($split + 1).Trim()
    }
    return $entries
}
function Assert-TravelJournalConfigSemantics([string]$Root) {
    $path = Join-Path $Root $TravelJournalConfigRelativePath
    $entries = Get-TravelJournalConfigEntries $path
    foreach ($required in $TravelJournalConfigRequired) {
        if (!$entries.ContainsKey($required.Key)) { throw "The archived TravelJournal configuration no longer binds $($required.Key)." }
        if ($entries[$required.Key] -ne $required.Value) {
            throw "Archived TravelJournal configuration changed; $($required.Key) must stay $($required.Value) (MaxEvents 0 is unbounded, so no compared row can be evicted)."
        }
    }
    return $path
}
# The archived plugin flushes its own journal at QUIT, after every in-run case has closed. These
# helpers therefore audit file locations only AFTER the owned process exited, over the explicitly
# named roots below; nothing is ever claimed about the rest of the file system.
$TravelJournalFilePatterns = @('.save.vgtraveljournal.json', '.vgtraveljournal.corrupt.', '.vgtraveljournal.json.tmp')
function Get-TravelJournalAuditRoots([string]$Root) {
    return @((Join-Path $Root 'Saves'), (Join-Path $Root 'game\BepInEx\plugins'), (Join-Path $Root 'game\BepInEx\config'),
        (Join-Path $Root 'game\BepInEx'), (Join-Path $Root 'game'), $Root)
}
function Get-TravelJournalFiles([string[]]$Roots) {
    $found = @{}
    foreach ($auditRoot in $Roots) {
        if (!(Test-Path -LiteralPath $auditRoot -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $auditRoot -File -Force) {
            if ($file.Name -like '*vgtraveljournal*') { $found[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        }
    }
    return $found
}
function Assert-TravelJournalContainment([string]$Root, [string[]]$Roots, $Before) {
    $saves = [IO.Path]::GetFullPath((Join-Path $Root 'Saves'))
    # The two PREPARED archive inputs. They carry the plugin's name but are the launcher's own
    # deployment, so they may exist outside the saves. The BINARY must still hash exactly unchanged.
    # The configuration must not: BepInEx rewrites it on the archive's first Bind, so it is
    # re-validated by its parsed semantics and its old/new hashes are recorded instead.
    $binary = [IO.Path]::GetFullPath((Join-Path $Root 'game\BepInEx\plugins\VGTravelJournal.dll'))
    $config = [IO.Path]::GetFullPath((Join-Path $Root $TravelJournalConfigRelativePath))
    $null = Assert-TravelJournalConfigSemantics $Root
    $after = Get-TravelJournalFiles $Roots
    $created = 0
    $lines = @('POST-QUIT ARCHIVED JOURNAL AUDIT', ('audited-roots=' + ($Roots -join ';')),
        ('patterns=' + ($TravelJournalFilePatterns -join ' ')))
    foreach ($path in @($after.Keys | Sort-Object)) {
        $name = Split-Path -Leaf $path
        $full = [IO.Path]::GetFullPath($path)
        $directory = [IO.Path]::GetFullPath((Split-Path -Parent $path))
        $state = if (!$Before.ContainsKey($path)) { 'created' } elseif ($Before[$path] -eq $after[$path]) { 'unchanged' } else { 'rewritten' }
        if ($full -eq $binary) {
            if ($state -ne 'unchanged') { throw "The prepared archive binary changed during the run: $name" }
            $lines += ("file`t" + $name + "`t" + $state + "`tprepared-binary`t" + $directory)
            continue
        }
        if ($full -eq $config) {
            # Expected: a BepInEx rewrite on the archive's first Bind. Byte equality is NOT claimed;
            # the parsed semantics were revalidated above and both hashes are recorded.
            $lines += ("file`t" + $name + "`t" + $state + "`tprepared-config (BepInEx rewrite permitted; semantics revalidated)`t" + $directory)
            if ($state -ne 'unchanged') {
                $lines += ("config-rewrite`told=" + $Before[$path] + "`tnew=" + $after[$path] + "`tsemantics=pass")
            }
            continue
        }
        if ($state -eq 'created') { $created++ }
        $lines += ("file`t" + $name + "`t" + $state + "`tjournal-output`t" + $directory)
        if ($directory -ne $saves) { throw "The archived journal left a file outside the sandbox saves after quit: $name" }
        if (@($TravelJournalFilePatterns | Where-Object { $name.Contains($_) }).Count -eq 0) { throw "Unexpected archived journal file after quit: $name" }
    }
    $lines += ('files=' + $after.Count + ' created=' + $created + ' preparedBinaryHash=unchanged preparedConfig=semantics-revalidated')
    $lines += 'scope=after the owned process exited and the archived plugin flushed at quit; no claim is made about locations outside the audited roots'
    [IO.File]::WriteAllLines((Join-Path $Root 'travel-journal-postquit-audit.txt'), [string[]]$lines)
}
function Assert-ModInformationProbeSelection([string]$Root, $Provenance) {
    $property = $Provenance.PSObject.Properties['modInformationProbe']
    if ($property -and $property.Value -isnot [bool]) { throw 'Invalid information probe flag.' }
    $selected = $property -and $property.Value
    $marker = Join-Path $Root 'mod-information-probe.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Information probe selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.PSObject.Properties['modMenuProbe'] -or $Provenance.modMenuProbe -isnot [bool] -or !$Provenance.modMenuProbe -or $Provenance.scenario -ne 'Full' -or [IO.File]::ReadAllText($marker) -cne 'mod-information-probe-v3') { throw 'Invalid information probe selection.' }
    foreach ($consumer in @('missionJournal','stockpile')) {
        $item = $Provenance.PSObject.Properties[$consumer]
        if (!$item -or $item.Value -isnot [bool] -or !$item.Value) { throw 'Full information probe needs both real consumers.' }
    }
    $certificate = Join-Path $Root 'untrusted-test.pfx'
    if (!(Test-Path -LiteralPath $certificate -PathType Leaf) -or (Get-Item -LiteralPath $certificate).Length -gt 16384 -or ((Get-Item -LiteralPath $certificate).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'TLS fixture missing, linked or oversized.' }
    if (!$Provenance.PSObject.Properties['modInformationCertificateSha256'] -or $Provenance.modInformationCertificateSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Provenance.modInformationCertificateSha256) { throw 'TLS fixture identity changed.' }
}
function Assert-ReleaseBrowserReceipt([string]$Root) {
    $request = Join-Path $Root 'browser-launch-request.txt'
    $receipt = Join-Path $Root 'browser-launch.receipt'
    $image = Join-Path $Root 'mod-release-browser.png'
    foreach ($path in @($request,$receipt,$image)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Browser evidence missing or linked.' }
    }
    if ((Get-Item -LiteralPath $request).Length -gt 512 -or (Get-Item -LiteralPath $receipt).Length -gt 512 -or (Get-Item -LiteralPath $image).Length -gt 20971520) { throw 'Browser evidence exceeds its bound.' }
    $url = 'https://github.com/fankserver/vanguard-galaxy-api/releases'
    if ([IO.File]::ReadAllText($request) -cne "mod-release-browser-v1`n$url`n") { throw 'Browser request destination changed.' }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines.Count -ne 4 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'mod-release-browser-v1' -or $lines[2] -cne "url=$url" -or $lines[3] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Invalid browser receipt.' }
    if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[3].Substring(7)) { throw 'Browser screenshot changed.' }
}
function Assert-ModInformationProbeReceipt([string]$Root, $Provenance) {
    Assert-ModInformationProbeSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['modInformationProbe'] -or !$Provenance.modInformationProbe) { return }
    Assert-ModMenuProbeReceipt $Root $Provenance
    Assert-ReleaseBrowserReceipt $Root
    $receipt = Join-Path $Root 'mod-information-probe.receipt'
    $snapshot = Join-Path $Root 'mod-information-probe.txt'
    foreach ($path in @($receipt,$snapshot)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 16384 -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Information probe evidence missing, linked or oversized.' }
    }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'mod-information-probe-v3' -or $lines[2] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Invalid information probe receipt.' }
    if ((Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[2].Substring(7)) { throw 'Information probe evidence changed.' }
    $facts = @(Get-Content -LiteralPath $snapshot)
    foreach ($fact in @('controlled-default-manual-coalescing-cooldown','controlled-six-hour-automatic-disable','controlled-dns-tls-timeout-retain-last-success','controlled-rate-limit','controlled-disk-cache-expiry-channel-installed-version','controlled-invalid-oversized-channel-redirect-policy','controlled-quit-mid-check','wire-platform-tls-parser-stable','wire-platform-tls-parser-experimental','wire-https-redirect','wire-invalid-oversized-channel-rejected','wire-dns-name-resolution-failure','wire-tls-untrusted-certificate-rejected','wire-stalled-handshake-canceled','unity-main-thread-menu-responsive','inventory-two-real-consumers-without-metadata','inventory-malformed-wrong-guid-isolated','gameplay-two-loads-return-single-entry','real-stockpile-ui-journal-coexistence','new-game-save-load-return','ui-immediate-refresh-without-automatic-browser','ui-automatic-checks-without-toggle','ui-explicit-default-browser-release','ui-scale-restored','actual-api-unavailable-loader-presence')) {
        if (@($facts | Where-Object { $_ -ceq ($fact + '=PASS') }).Count -ne 1) { throw "Missing or duplicate information probe fact: $fact" }
    }
}
function Assert-BlueprintPinSelection([string]$Root, $Provenance) {
    $flag = $Provenance.PSObject.Properties['blueprintPinProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid Blueprint Pin flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'blueprint-pin.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Blueprint Pin selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeReadProbe -or $Provenance.forgeUiProbe -or $Provenance.forgeCommandProbe -or $Provenance.refineryProbe -or $Provenance.forgeDeliveryProbe -or $Provenance.forgePersistenceProbe -or [IO.File]::ReadAllText($marker) -cne 'blueprint-pin-v1') { throw 'Invalid Blueprint Pin selection.' }
    if ($Provenance.blueprintPinRevision -cnotmatch '^[0-9a-f]{40}$' -or $Provenance.blueprintPinSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid Blueprint Pin provenance.' }
    $config = [IO.File]::ReadAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'))
    if ($config -cnotmatch '(?ms)^\[Hud\]\r?\n(?:(?!^\[).)*?^Enabled = true\r?$') { throw 'Blueprint Pin requires HUD integration.' }
    $binary = Join-Path $Root 'game\BepInEx\plugins\VGBlueprintPin.dll'
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Provenance.blueprintPinSha256) { throw 'Blueprint Pin binary changed.' }
}
function Assert-BlueprintPinReceipt([string]$Root, $Provenance) {
    Assert-ForgeReadReceipt $Root $Provenance
    Assert-BlueprintPinSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['blueprintPinProbe'] -or !$Provenance.blueprintPinProbe) { return }
    $file = Join-Path $Root 'blueprint-pin.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized Blueprint Pin receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'blueprint-pin-v1' -or $lines[2] -cne 'pin-batch-exact-variant-navigation-close') { throw 'Incomplete Blueprint Pin receipt.' }
    $image = Join-Path $Root 'blueprint-pin-view.png'; $record = Join-Path $Root 'blueprint-pin-view.txt'
    foreach ($path in @($image,$record)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0 -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Blueprint Pin image evidence missing, empty or linked.' }
    }
    if ((Get-Item -LiteralPath $image).Length -gt 20MB -or (Get-Item -LiteralPath $record).Length -gt 256) { throw 'Blueprint Pin image evidence oversized.' }
    $hashLines = @(Get-Content -LiteralPath $record)
    if ($hashLines.Count -ne 1 -or $hashLines[0] -cnotmatch '^sha256=[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashLines[0].Substring(7)) { throw 'Blueprint Pin screenshot changed.' }
}
function Assert-ForgeUiSelection([string]$Root, $Provenance) {
    $flag = $Provenance.PSObject.Properties['forgeUiProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid Forge UI flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'forge-ui.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Forge UI selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeReadProbe -or $Provenance.forgeCommandProbe -or $Provenance.refineryProbe -or $Provenance.forgeDeliveryProbe -or $Provenance.forgePersistenceProbe -or [IO.File]::ReadAllText($marker) -cne 'forge-ui-v3') { throw 'Invalid Forge UI selection.' }
}
function Assert-ForgeUiReceipt([string]$Root, $Provenance) {
    Assert-ForgeReadReceipt $Root $Provenance
    Assert-ForgeUiSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['forgeUiProbe'] -or !$Provenance.forgeUiProbe) { return }
    $file = Join-Path $Root 'forge-ui.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized Forge UI receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'forge-ui-v3' -or $lines[2] -cne 'variants-pointer-disabled-stale-reopen-dispose-nonoverlap-scale-recovery') { throw 'Incomplete Forge UI receipt.' }
    foreach ($stem in @('forge-ui-actions','forge-ui-scaled')) {
    $image = Join-Path $Root ($stem + '.png'); $record = Join-Path $Root ($stem + '.txt')
    foreach ($path in @($image,$record)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0 -or ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Forge UI image evidence missing, empty or linked.' }
    }
    if ((Get-Item -LiteralPath $image).Length -gt 20MB -or (Get-Item -LiteralPath $record).Length -gt 256) { throw 'Forge UI image evidence oversized.' }
    $hashLines = @(Get-Content -LiteralPath $record)
    if ($hashLines.Count -ne 1 -or $hashLines[0] -cnotmatch '^sha256=[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashLines[0].Substring(7)) { throw 'Forge UI screenshot changed.' }
    }
}
function Assert-RefinerySelection([string]$Root, $Provenance) {
    Assert-ForgeUiSelection $Root $Provenance
    Assert-BlueprintPinSelection $Root $Provenance
    $flag = $Provenance.PSObject.Properties['refineryProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid refinery flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'refinery.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Refinery selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeCommandProbe -or $Provenance.forgePersistenceProbe -or $Provenance.forgeDeliveryProbe -or [IO.File]::ReadAllText($marker) -cne 'refinery-v3') { throw 'Invalid refinery selection.' }
}
function Assert-RefineryReceipt([string]$Root, $Provenance) {
    Assert-ForgeCommandReceipt $Root $Provenance
    Assert-RefinerySelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['refineryProbe'] -or !$Provenance.refineryProbe) { return }
    $file = Join-Path $Root 'refinery.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized refinery receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'refinery-v3' -or $lines[2] -cne 'fractional-partial-multiple-refund-extraction-replay-favourites') { throw 'Incomplete refinery receipt.' }
}
function Assert-ForgeDeliverySelection([string]$Root, $Provenance) {
    Assert-RefinerySelection $Root $Provenance
    $flag = $Provenance.PSObject.Properties['forgeDeliveryProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid crafting delivery flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'forge-delivery.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Crafting delivery selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeCommandProbe -or $Provenance.forgePersistenceProbe -or [IO.File]::ReadAllText($marker) -cne 'forge-delivery-v1') { throw 'Invalid crafting delivery selection.' }
}
function Assert-ForgeDeliveryReceipt([string]$Root, $Provenance) {
    Assert-ForgeCommandReceipt $Root $Provenance
    Assert-ForgeDeliverySelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['forgeDeliveryProbe'] -or !$Provenance.forgeDeliveryProbe) { return }
    $file = Join-Path $Root 'forge-delivery.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized crafting delivery receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'forge-delivery-v1' -or $lines[2] -cne 'partial-cancel-multi-batch-inventory') { throw 'Incomplete crafting delivery receipt.' }
}
function Assert-ForgePersistenceSelection([string]$Root, $Provenance) {
    Assert-ForgeDeliverySelection $Root $Provenance
    $flag = $Provenance.PSObject.Properties['forgePersistenceProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid crafting persistence flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'forge-persistence.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Crafting persistence selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeCommandProbe -or [IO.File]::ReadAllText($marker) -cne 'forge-persistence-v1') { throw 'Invalid crafting persistence selection.' }
}
function Assert-ForgePersistenceReceipt([string]$Root, $Provenance) {
    Assert-ForgeCommandReceipt $Root $Provenance
    Assert-ForgePersistenceSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['forgePersistenceProbe'] -or !$Provenance.forgePersistenceProbe) { return }
    $file = Join-Path $Root 'forge-persistence.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized crafting persistence receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'forge-persistence-v1' -or $lines[2] -cne 'paused-jobs-roundtrip-save-as-slot-switch') { throw 'Incomplete crafting persistence receipt.' }
}
function Assert-ForgeCommandSelection([string]$Root, $Provenance) {
    Assert-ForgePersistenceSelection $Root $Provenance
    $flag = $Provenance.PSObject.Properties['forgeCommandProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid Forge command flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'forge-commands.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Forge command selection changed.' }
    if (!$selected) { return }
    if (!$Provenance.forgeReadProbe -or [IO.File]::ReadAllText($marker) -cne 'forge-commands-v3') { throw 'Invalid Forge command selection.' }
    $config = [IO.File]::ReadAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'))
    if ($config -cnotmatch '(?ms)^\[Recipes\]\r?\n(?:(?!^\[).)*?^CommandsEnabled = true\r?$') { throw 'Crafting commands not enabled.' }
}
function Assert-ForgeCommandReceipt([string]$Root, $Provenance) {
    Assert-ForgeReadReceipt $Root $Provenance
    Assert-ForgeCommandSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['forgeCommandProbe'] -or !$Provenance.forgeCommandProbe) { return }
    $file = Join-Path $Root 'forge-commands.txt'
    if ((Get-Item -LiteralPath $file).Length -gt 512) { throw 'Oversized Forge command receipt.' }
    $lines = @(Get-Content -LiteralPath $file)
    if ($lines.Count -ne 4 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'forge-commands-v3' -or $lines[2] -cne 'settings-replay-restored' -or $lines[3] -cne 'forge-queue-cancel-replay-refusal-direct-start-capacity') { throw 'Incomplete Forge command receipt.' }
}
function Assert-DungeonReadinessSelection([string]$Root, $Provenance) {
    $flag = $Provenance.PSObject.Properties['dungeonReadinessProbe']
    if ($flag -and $flag.Value -isnot [bool]) { throw 'Invalid dungeon readiness flag.' }
    $selected = $flag -and $flag.Value
    $marker = Join-Path $Root 'dungeon-readiness.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Dungeon readiness selection changed.' }
    if (!$selected) { return }
    if ($Provenance.scenario -ne 'Full' -or [IO.File]::ReadAllText($marker) -cne 'dungeon-readiness-v1') { throw 'Invalid dungeon readiness selection.' }
    foreach ($entry in $Provenance.PSObject.Properties) {
        if ($entry.Name -ne 'dungeonReadinessProbe' -and $entry.Value -is [bool] -and $entry.Value) { throw 'Dungeon readiness cannot combine other probes or consumers.' }
    }
    if ($null -ne $Provenance.assemblyOverlay) { throw 'Dungeon readiness cannot use an assembly overlay.' }
    $config = [IO.File]::ReadAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'))
    foreach ($section in @('Boarding','Dungeons')) {
        if ($config -cnotmatch ('(?ms)^\[' + $section + '\]\r?\n(?:(?!^\[).)*?^Enabled = true\r?$')) { throw 'Dungeon integration configuration changed.' }
    }
}
function Assert-DungeonReadinessReceipt([string]$Root, $Provenance) {
    Assert-DungeonReadinessSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['dungeonReadinessProbe'] -or !$Provenance.dungeonReadinessProbe) { return }
    Assert-QualificationExitOutcome (Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw | ConvertFrom-Json) 'Dungeon readiness'
    $receipt = Join-Path $Root 'dungeon-readiness.receipt'; $snapshot = Join-Path $Root 'dungeon-readiness.txt'
    if ((Get-Item -LiteralPath $receipt).Length -gt 256 -or (Get-Item -LiteralPath $snapshot).Length -gt 4096) { throw 'Dungeon evidence too large.' }
    $lines = @(Get-Content -LiteralPath $receipt); $facts = @(Get-Content -LiteralPath $snapshot)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'dungeon-readiness-v1' -or $lines[2] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Invalid dungeon readiness receipt.' }
    if ((Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[2].Substring(7)) { throw 'Dungeon readiness evidence changed.' }
    if ($facts.Count -ne 4 -or $facts[0] -cne 'PASS' -or $facts[1] -cne 'dungeon-readiness-v1' -or $facts[2] -cnotmatch '^targets=[0-9]+$' -or $facts[3] -cne 'operations=0') { throw 'Invalid dungeon readiness facts.' }
}
function Assert-ForgeReadSelection([string]$Root, $Provenance) {
    Assert-ForgeCommandSelection $Root $Provenance
    $property = $Provenance.PSObject.Properties['forgeReadProbe']
    if ($property -and $property.Value -isnot [bool]) { throw 'Invalid Forge read flag.' }
    $selected = $property -and $property.Value
    $marker = Join-Path $Root 'forge-reads.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Forge read selection changed.' }
    if (!$selected) { return }
    if ($Provenance.scenario -ne 'Full' -or [IO.File]::ReadAllText($marker) -cne 'forge-reads-v1') { throw 'Invalid Forge read selection.' }
    foreach ($entry in $Provenance.PSObject.Properties) {
        if ($entry.Name -notin @('forgeReadProbe','forgeCommandProbe','forgePersistenceProbe','forgeDeliveryProbe','refineryProbe','forgeUiProbe','blueprintPinProbe') -and $entry.Value -is [bool] -and $entry.Value) { throw 'Forge reads cannot combine other scenarios or consumers.' }
    }
    if ($null -ne $Provenance.assemblyOverlay) { throw 'Forge reads cannot use an assembly overlay.' }
    $config = [IO.File]::ReadAllText((Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg'))
    if ($config -cnotmatch '(?ms)^\[Recipes\]\r?\n(?:(?!^\[).)*?^Enabled = true\r?$') { throw 'Forge read recipe integration is not enabled.' }
}
function Assert-ForgeReadReceipt([string]$Root, $Provenance) {
    Assert-ForgeReadSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['forgeReadProbe'] -or !$Provenance.forgeReadProbe) { return }
    Assert-QualificationExitOutcome (Get-Content -LiteralPath (Join-Path $Root 'run-outcome.json') -Raw | ConvertFrom-Json) 'Forge reads'
    $receipt = Join-Path $Root 'forge-reads.receipt'; $snapshot = Join-Path $Root 'forge-reads.txt'
    if ((Get-Item -LiteralPath $receipt).Length -gt 256 -or (Get-Item -LiteralPath $snapshot).Length -gt 4096) { throw 'Forge evidence too large.' }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'forge-reads-v1' -or $lines[2] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Invalid Forge receipt.' }
    if ((Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[2].Substring(7)) { throw 'Forge evidence changed.' }
    $facts = @(Get-Content -LiteralPath $snapshot)
    if ($facts.Count -ne 5 -or $facts[0] -cne 'PASS' -or $facts[1] -cne 'forge-reads-v1' -or $facts[2] -cnotmatch '^catalog=[1-9][0-9]*$' -or $facts[3] -cnotmatch '^quotes=[1-9][0-9]*$' -or $facts[4] -cnotmatch '^restored=[0-9]+$') { throw 'Invalid Forge facts.' }
}
function Assert-ModMenuProbeSelection([string]$Root, $Provenance) {
    Assert-ModInformationProbeSelection $Root $Provenance
    $property = $Provenance.PSObject.Properties['modMenuProbe']
    if ($property -and $property.Value -isnot [bool]) { throw 'Invalid mod menu probe flag.' }
    $selected = $property -and $property.Value
    $marker = Join-Path $Root 'mod-menu-probe.enabled'
    if ([bool]$selected -ne (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Mod menu probe selection changed.' }
    if (!$selected) { return }
    if ($Provenance.scenario -ne 'Full' -or [IO.File]::ReadAllText($marker) -cne 'mod-menu-probe-v3') { throw 'Invalid mod menu probe selection.' }
    $selections = @('blueprintPinProbe','storyProbe','menuInspection','travelJournal','travelJournalComparison','echo','echoTravelProbe','echoAbsentProbe','anima','animaTravelProbe','journalMissionEventsProbe','missionIdentityProbe','missionTransitionsProbe','contentReferenceProbe','stockpileCoordinated','journalCoordinated','persistenceProbe','vanillaLoadControl','stockpile','missionJournal','travelStation','travelCrossSystem','travelWormholeFixture','travelResilience','travelRecovery','travelFastLane')
    if ($null -ne $Provenance.assemblyOverlay) { throw 'Mod menu probe cannot use an assembly overlay.' }
    foreach ($name in $selections) {
        $item = $Provenance.PSObject.Properties[$name]
        if ($Provenance.PSObject.Properties['modInformationProbe'] -and $Provenance.modInformationProbe -and $name -in @('missionJournal','stockpile')) { continue }
        if ($item -and ($item.Value -isnot [bool] -or $item.Value)) { throw 'Mod menu probe cannot be combined with consumers or other probes; selection fields must be Boolean false.' }
    }
}
function Assert-ModMenuProbeReceipt([string]$Root, $Provenance) {
    Assert-ModMenuProbeSelection $Root $Provenance
    if (!$Provenance.PSObject.Properties['modMenuProbe'] -or !$Provenance.modMenuProbe) { return }
    $outcome = Join-Path $Root 'run-outcome.json'
    if (!(Test-Path -LiteralPath $outcome -PathType Leaf)) { throw 'Mod menu probe has no exit outcome.' }
    Assert-QualificationExitOutcome (Get-Content -LiteralPath $outcome -Raw | ConvertFrom-Json) 'Mod menu probe'
    $receipt = Join-Path $Root 'mod-menu-probe.receipt'
    $snapshot = Join-Path $Root 'mod-menu-probe.txt'
    if (!(Test-Path -LiteralPath $receipt -PathType Leaf) -or !(Test-Path -LiteralPath $snapshot -PathType Leaf)) { throw 'Mod menu probe evidence missing.' }
    if ((Get-Item -LiteralPath $receipt).Length -gt 256 -or (Get-Item -LiteralPath $snapshot).Length -gt 1048576) { throw 'Mod menu probe evidence too large.' }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'mod-menu-probe-v3' -or $lines[2] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Invalid mod menu probe receipt.' }
    if ((Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[2].Substring(7)) { throw 'Mod menu probe evidence changed.' }
    $images = @('mod-menu-original.png','mod-menu-1280.png','mod-update-status.png')
    if ($Provenance.PSObject.Properties['modInformationProbe'] -and $Provenance.modInformationProbe) { $images += @('mod-menu-scale.png','mod-api-unavailable.png') }
    foreach ($name in $images) {
        $image = Join-Path $Root $name
        if (!(Test-Path -LiteralPath $image -PathType Leaf) -or (Get-Item -LiteralPath $image).Length -gt 20971520) { throw 'Menu screenshot missing or oversized.' }
        $record = @(Get-Content -LiteralPath $snapshot | Where-Object { $_.StartsWith("screenshot=$name ") })
        if ($record.Count -ne 1 -or $record[0] -cnotmatch '^screenshot=[^ ]+ resolution=[1-9][0-9]*x[1-9][0-9]* sha256=([0-9a-f]{64})$') { throw 'Invalid menu screenshot record.' }
        $expected = $Matches[1]
        if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw 'Menu screenshot hash mismatch.' }
    }
}
function Assert-MenuInspectionReceipt([string]$Root, $Provenance) {
    if (!$Provenance.PSObject.Properties['menuInspection'] -or !$Provenance.menuInspection) { return }
    if ($Provenance.scenario -ne 'MissingApi') { throw 'Menu inspection requires MissingApi provenance.' }
    $outcomePath = Join-Path $Root 'run-outcome.json'
    if (!(Test-Path -LiteralPath $outcomePath -PathType Leaf)) { throw 'Menu inspection has no recorded launcher outcome.' }
    Assert-QualificationExitOutcome (Get-Content -LiteralPath $outcomePath -Raw | ConvertFrom-Json) 'Menu inspection'
    $marker = Join-Path $Root 'menu-inspection.enabled'
    if (!(Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker) -cne 'menu-inspection-v1') { throw 'Menu inspection selection is missing or changed.' }
    $receipt = Join-Path $Root 'menu-inspection.receipt'
    $snapshot = Join-Path $Root 'menu-inspection.txt'
    if (!(Test-Path -LiteralPath $receipt) -or !(Test-Path -LiteralPath $snapshot)) { throw 'Menu inspection evidence is missing.' }
    if ((Get-Item -LiteralPath $receipt).Length -gt 256 -or (Get-Item -LiteralPath $snapshot).Length -gt 1048576) { throw 'Menu inspection evidence exceeds its bound.' }
    $lines = @(Get-Content -LiteralPath $receipt)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'PASS' -or $lines[1] -cne 'menu-inspection-v1' -or $lines[2] -cnotmatch '^sha256=[0-9a-f]{64}$') { throw 'Menu inspection receipt is invalid.' }
    if ((Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lines[2].Substring(7)) { throw 'Menu inspection snapshot hash mismatch.' }
}
function Assert-PersistenceProbeReceipt([string]$Root, $Provenance) {
    Assert-MenuInspectionReceipt $Root $Provenance
    Assert-ModMenuProbeReceipt $Root $Provenance
    Assert-ModInformationProbeReceipt $Root $Provenance
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
    if ($Provenance.PSObject.Properties['travelRecovery'] -and $Provenance.travelRecovery) {
        Assert-TravelRecoveryReceipt $Root
    }
    if ($Provenance.PSObject.Properties['travelFastLane'] -and $Provenance.travelFastLane) {
        Assert-TravelFastLaneReceipt $Root
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
    if ($Provenance.PSObject.Properties['travelJournalComparison'] -and $Provenance.travelJournalComparison) {
        Assert-TravelJournalReceipt $Root
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
# The fourth separate optional phase reserves its own process time ON TOP of the in-system phase.
# It closes the two travel cells the other phases deliberately left open (a positively driven
# RecoveredPlacement and the post-gate in-system chain continuation) and never widens theirs.
$TravelRecoveryPhase = 'travel-recovery-continuation-v1'
$TravelRecoveryRequiredCases = @('recovered-placement','post-gate-continuation')
$TravelRecoveryBudgetSeconds = 4200
# Bounded diagnostic rows: one per driven recovery attempt, persisted as the attempt starts and
# rewritten with its outcome. They are never coverage, but a receipt that lost them is refused.
$TravelRecoveryAttemptRow = 'recovered-placement-attempt'
$TravelRecoveryMaxAttempts = 3
# COMMITTED terminal outcomes an attempt row may carry. A receipt can never satisfy the attempt log
# with an arbitrary string, and the started state carries no outcome at all: it is only a failure
# artifact of an attempt that never reached one.
$TravelRecoveryAttemptSuccess = 'cancelled-in-live-window'
$TravelRecoveryAttemptMisses = @('native-arrival-first','route-already-ended','timeout-no-route','native-travel-refused')
$TravelRecoveryAttemptOutcomes = @($TravelRecoveryAttemptSuccess) + $TravelRecoveryAttemptMisses +
    @('timeout-route-running','no-safe-target','abandoned-leg-not-closed','cleanup-placement-unsettled')
# A miss writes its KNOWN reason before its cleanup's side effect and marks it pending until the
# cleanup finished. A receipt that still carries the marker describes an attempt that never
# finished; the reason stays readable, but the receipt is never a completed one.
$TravelRecoveryAttemptPendingMarker = 'cleanup=pending'
# The fifth separate optional phase reserves its own process time ON TOP of the in-system phase. It
# exercises the native fast lane (travelMultiplier = 7), which
# the post-gate continuation phase cannot reach because its follow-on POI is deliberately not a gate.
$TravelFastLanePhase = 'travel-fast-lane-v1'
$TravelFastLaneRequiredCases = @('fast-lane-gate-chain','fast-lane-multiplier-observed')
$TravelFastLaneBudgetSeconds = 2400
# The one native value the charge branch sets; the receipt must publish it as observed evidence.
$TravelFastLaneMultiplier = '7'
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
# The archived-journal comparison reuses the SAME two phases and owns their ordering, so it is
# refused together with either consumer travel probe.
$TravelJournalPhase = 'travel-journal-comparison-v1'
$TravelJournalRequiredCases = @('legacy-binding','in-system-arrival-compatible','chained-arrival-compatible','jumpgate-prefix-lead','wormhole-transit-gap','station-interior-vs-physical','legacy-blind-concepts','journal-io-containment','api-dwell-anchored')
$TravelJournalCompatiblePairCases = @('in-system-arrival-compatible','chained-arrival-compatible')
$TravelJournalDrivenDiscrepancyCases = @('jumpgate-prefix-lead','wormhole-transit-gap','station-interior-vs-physical')
$TravelJournalLegacyIndexedCases = @('in-system-arrival-compatible','chained-arrival-compatible','jumpgate-prefix-lead')
$TravelJournalBudgetSeconds = 900
$TravelJournalReusedPhaseScenarios = $AnimaTravelReusedPhaseScenarios
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
# The recovery/continuation phase is validated separately and with its own mandatory cases: a
# passing in-system, cross-system or resilience receipt can never stand in for it. Both of its cases
# must publish the native evidence that distinguishes them from an ordinary arrival.
function Assert-TravelRecoveryReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Travel recovery/continuation' 'travel-recovery' $TravelRecoveryPhase $TravelRecoveryRequiredCases $TravelRecoveryBudgetSeconds
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'travel-recovery-receipt.tsv'))
    $records = @($rows[1..($rows.Count - 1)] | ForEach-Object { ,($_ -split "`t") })
    $recovery = @($records | Where-Object { $_[0] -eq 'recovered-placement' })
    if ($recovery.Count -ne 1) { throw 'The recovered-placement case is missing or duplicated.' }
    # The persisted attempt log: bounded, consistent with the receipt's own declared bound, never
    # coverage, and never absent for a passed case. A missed attempt keeps its own recorded reason.
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'travel-recovery.txt'))
    if ($summary -notcontains "recovery-attempts=$TravelRecoveryMaxAttempts") { throw 'The recovery receipt declares a different attempt bound.' }
    $attempts = @($records | Where-Object { $_[0] -eq $TravelRecoveryAttemptRow })
    if ($attempts.Count -lt 1) { throw 'The recovery case published no persisted attempt row.' }
    # The COMMITTED bound, not a value the receipt may declare for itself.
    if ($attempts.Count -gt $TravelRecoveryMaxAttempts) { throw 'The recovery case published more attempt rows than the committed bound.' }
    $attemptNumbers = @()
    $attemptOutcomes = @()
    foreach ($attempt in $attempts) {
        if ($attempt[2] -ne 'not-run') { throw 'A recovery attempt row is recorded as coverage.' }
        if ($attempt[7] -notmatch '^attempt(\d+)=\{target=[^,}]*,outcome=([a-z-]+)') {
            throw 'A recovery attempt row carries no numbered terminal outcome (it may still be in its started state).'
        }
        $number = [int]$Matches[1]
        $outcome = $Matches[2]
        if ($TravelRecoveryAttemptOutcomes -notcontains $outcome) { throw "A recovery attempt row carries the unknown outcome '$outcome'." }
        if ($attempt[7] -like "*$TravelRecoveryAttemptPendingMarker*") { throw "A recovery attempt row still marks its own cleanup as pending (known reason '$outcome')." }
        if ($number -lt 1 -or $number -gt $TravelRecoveryMaxAttempts) { throw 'A recovery attempt is numbered outside the committed bound.' }
        if ($attemptNumbers -contains $number) { throw 'A recovery attempt number is recorded twice.' }
        if ($attempt[4] -ne $recovery[0][4]) { throw 'A recovery attempt row belongs to another session than its case.' }
        $attemptNumbers += $number
        $attemptOutcomes += $outcome
    }
    for ($index = 0; $index -lt $attemptNumbers.Count; $index++) {
        if ($attemptNumbers[$index] -ne ($index + 1)) { throw 'Recovery attempts are not numbered contiguously from 1.' }
    }
    $successes = @($attemptOutcomes | Where-Object { $_ -eq $TravelRecoveryAttemptSuccess })
    if ($successes.Count -gt 1) { throw 'The recovery case records more than one successful attempt.' }
    if ($successes.Count -eq 1 -and $attemptOutcomes[$attemptOutcomes.Count - 1] -ne $TravelRecoveryAttemptSuccess) {
        throw 'The successful recovery attempt is not the last one.'
    }
    if ($recovery[0][2] -eq 'passed') {
        if ($successes.Count -ne 1) { throw 'The passed recovery case has no single attempt row reporting the live-route cancel it claims.' }
        foreach ($earlier in @($attemptOutcomes | Select-Object -SkipLast 1)) {
            if ($TravelRecoveryAttemptMisses -notcontains $earlier) { throw "An attempt before the successful one reports '$earlier', which is not a miss the case may continue after." }
        }
    }
    # A recovery is only a recovery when the placement was observed at a loaded, initialized POI
    # with no native route left running: an arrival would have carried an operation instead.
    if ($recovery[0][7] -notlike '*recoveredAt=*' -or $recovery[0][7] -notlike '*placementSnapshot=*') {
        throw 'The recovered-placement case published no recovered location or placement snapshot.'
    }
    if ($recovery[0][7] -notlike '*placementSnapshot=*managerReady=True*' -or $recovery[0][7] -notlike '*placementSnapshot=*travelActive=False*' -or
        $recovery[0][7] -notlike '*placementSnapshot=*waypoints=0*') {
        throw 'The recovered-placement snapshot does not show an initialized POI with no native route running.'
    }
    # The acquisition snapshot is the one taken immediately BEFORE the cancel: it must show a LIVE
    # native route, otherwise the cancel interrupted nothing and the window was an abandoned route.
    if ($recovery[0][7] -notlike '*acquisitionSnapshot=*') { throw 'The recovered-placement case published no acquisition snapshot.' }
    if ($recovery[0][7] -notlike '*acquisitionSnapshot=*travelActive=True*' -or $recovery[0][7] -notlike '*acquisitionSnapshot=*managerReady=True*') {
        throw 'The recovered-placement acquisition snapshot does not show a live native route at an initialized POI.'
    }
    # A missed attempt that had to close its own abandoned leg must also publish that the recovery
    # its cleanup enabled settled inside that attempt's own window.
    foreach ($attempt in $attempts) {
        if ($attempt[7] -match ',outcome=([a-z-]+)' -and $TravelRecoveryAttemptMisses -contains $Matches[1] -and $attempt[7] -like '*missCleanup=*') {
            if ($attempt[7] -notlike '*settlement=*' -or $attempt[7] -notlike '*RecoveredPlacement*') {
                throw 'A missed recovery attempt closed its own leg without publishing the settled cleanup recovery.'
            }
        }
    }
    $continuation = @($records | Where-Object { $_[0] -eq 'post-gate-continuation' })
    if ($continuation.Count -ne 1) { throw 'The post-gate-continuation case is missing or duplicated.' }
    if ($continuation[0][7] -notlike '*legs=3*') { throw 'The post-gate-continuation case did not drive three native legs.' }
    if ($continuation[0][7] -notlike '*routeCompletions=1*') { throw 'The post-gate-continuation case did not publish exactly one route completion.' }
    # The decisive native evidence: the gate arrival still had a remaining waypoint, so withholding
    # the route completion there is observed truth rather than a timing artefact.
    if ($continuation[0][7] -notmatch 'gateArrivalSnapshot=[^;]*waypoints=([1-9][0-9]*)') {
        throw 'The post-gate-continuation case does not record a gate arrival with a remaining native waypoint.'
    }
    if ($continuation[0][7] -notlike '*gateArrivalSnapshot=*usingJumpgate=True*') {
        throw 'The post-gate-continuation gate arrival was not recorded inside the native jump routine.'
    }
    if ($continuation[0][7] -notlike '*completionSnapshot=*waypoints=0*' -or $continuation[0][7] -notlike '*completionSnapshot=*travelActive=False*') {
        throw 'The post-gate-continuation route completion was not recorded at the end of the native route.'
    }
}
# The fast-lane phase is validated separately and with its own mandatory cases: no other receipt can
# stand in for it, and its positive proof must be PUBLISHED, not implied.
function Assert-TravelFastLaneReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Travel fast lane' 'travel-fast-lane' $TravelFastLanePhase $TravelFastLaneRequiredCases $TravelFastLaneBudgetSeconds
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'travel-fast-lane.txt'))
    if ($summary -notcontains "fast-lane-multiplier=$TravelFastLaneMultiplier") { throw 'The fast-lane receipt declares a different native multiplier.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'travel-fast-lane-receipt.tsv'))
    $records = @($rows[1..($rows.Count - 1)] | ForEach-Object { ,($_ -split "`t") })
    # No row may claim an identity this phase does not own: a fabricated or renamed case is refused
    # before any content is read.
    foreach ($record in $records) {
        if ($TravelFastLaneRequiredCases -notcontains $record[0]) { throw "Unknown case identity in the fast-lane receipt: $($record[0])" }
    }
    $chain = @($records | Where-Object { $_[0] -eq 'fast-lane-gate-chain' })
    if ($chain.Count -ne 1) { throw 'The fast-lane-gate-chain case is missing or duplicated.' }
    if ($chain[0][7] -notlike '*gates=2*' -or $chain[0][7] -notlike '*systems=3*') { throw 'The fast-lane chain did not cross two gates into a third system.' }
    if ($chain[0][7] -notlike '*legs=5*') { throw 'The fast-lane chain did not drive five native legs.' }
    if ($chain[0][7] -notlike '*routeCompletions=1*') { throw 'The fast-lane chain did not publish exactly one route completion.' }
    if ($chain[0][7] -notlike '*completionSnapshot=*waypoints=0*' -or $chain[0][7] -notlike '*completionSnapshot=*travelActive=False*') {
        throw 'The fast-lane route completion was not recorded at the end of the native route.'
    }
    $multiplier = @($records | Where-Object { $_[0] -eq 'fast-lane-multiplier-observed' })
    if ($multiplier.Count -ne 1) { throw 'The fast-lane-multiplier-observed case is missing or duplicated.' }
    # The decisive native evidence: the charge branch's own transient, observed on the gate-to-gate
    # leg and absent on the legs around it. The unlock flag is only ever READ.
    if ($multiplier[0][7] -notlike "*fastLaneMultiplier=$TravelFastLaneMultiplier*" -or $multiplier[0][7] -notlike '*fastLaneActive=True*') {
        throw 'The fast-lane case published no observed native multiplier of ' + $TravelFastLaneMultiplier + '.'
    }
    if ($multiplier[0][7] -notlike '*approachMultiplier=1*' -or $multiplier[0][7] -notlike '*postFastLaneMultiplier=1*') {
        throw 'The fast-lane case did not publish the surrounding legs at the resting native multiplier.'
    }
    if ($multiplier[0][7] -notlike '*fastLaneUnlocked=True (read-only; never written)*') {
        throw 'The fast-lane case did not publish the read-only unlock precondition.'
    }
    foreach ($boundary in @('requestedSnapshot','departedSnapshot','arrivedSnapshot')) {
        if ($multiplier[0][7] -notmatch ($boundary + '=[^;]*multiplier=' + $TravelFastLaneMultiplier + ',fastLaneActive=True')) {
            throw "The fast-lane case did not publish the native $boundary at the charge branch's multiplier."
        }
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
# The archived-journal comparison is validated separately and with its own mandatory cases. A
# summary without a compatible pair, without the driven legacy discrepancies, without resolvable
# fact references or without the legacy row indices it compared is refused.
function Assert-TravelJournalReceipt([string]$Root) {
    Assert-TravelPhaseReceipt $Root 'Archived TravelJournal comparison' 'travel-journal' $TravelJournalPhase $TravelJournalRequiredCases $TravelJournalBudgetSeconds
    $summary = @(Get-Content -LiteralPath (Join-Path $Root 'travel-journal.txt'))
    if ($summary -notcontains ("compatible-pairs=" + ($TravelJournalCompatiblePairCases -join ','))) { throw 'Archived-journal receipt declares different compatible-pair cases.' }
    if ($summary -notcontains ("driven-discrepancies=" + ($TravelJournalDrivenDiscrepancyCases -join ','))) { throw 'Archived-journal receipt declares different driven-discrepancy cases.' }
    $rows = @(Get-Content -LiteralPath (Join-Path $Root 'travel-journal-receipt.tsv'))
    $records = @($rows[1..($rows.Count - 1)] | ForEach-Object { ,($_ -split "`t") })
    foreach ($case in $TravelJournalCompatiblePairCases) {
        $matched = @($records | Where-Object { $_[0] -eq $case })
        if ($matched.Count -ne 1 -or $matched[0][7] -notlike '*comparison=compatible*') { throw "Archived-journal case $case did not record a compatible pair." }
    }
    foreach ($case in $TravelJournalDrivenDiscrepancyCases) {
        $matched = @($records | Where-Object { $_[0] -eq $case })
        if ($matched.Count -ne 1 -or ($matched[0][7] -notlike '*comparison=legacy-*')) { throw "Archived-journal case $case did not record a driven legacy discrepancy." }
    }
    foreach ($case in $TravelJournalLegacyIndexedCases) {
        $matched = @($records | Where-Object { $_[0] -eq $case })
        if ($matched[0][7] -notlike '*legacy:*') { throw "Archived-journal case $case names no legacy row indices." }
    }
    # The private evidence copies the phase made must really exist beside the receipt.
    foreach ($slot in @('qa-journal-in-system','qa-journal-in-flight','qa-journal-wormhole')) {
        if (!(Test-Path -LiteralPath (Join-Path $Root ("travel-journal-" + $slot + ".json")) -PathType Leaf)) {
            throw "Archived-journal evidence copy missing for $slot."
        }
    }
    # The dwell case must publish an actually positive anchored dwell.
    $dwell = @($records | Where-Object { $_[0] -eq 'api-dwell-anchored' })
    if ($dwell.Count -ne 1 -or $dwell[0][7] -notlike '*largestDwellSeconds=*') { throw 'Archived-journal dwell case published no measured dwell.' }
    # Round-trip ("R") formatting can be exponential, so the published value is parsed as a number.
    if ($dwell[0][7] -notmatch 'largestDwellSeconds=([0-9][0-9.eE+-]*)') { throw 'Archived-journal dwell case published no measured dwell.' }
    if ([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) -le 0) { throw 'The native dwell case reported no strictly positive anchored dwell.' }
    if ($dwell[0][7] -notlike '*anchorGameSeconds=*' -or $dwell[0][7] -notlike '*departureGameSeconds=*') { throw 'Archived-journal dwell case published no anchor/departure times to re-check.' }
    # Ordering: the comparison observed both reused phases and completed after them.
    $passed = @(Get-Content -LiteralPath (Join-Path $Root 'result.txt'))
    $expected = @($TravelJournalPhase) + $TravelJournalReusedPhaseScenarios
    foreach ($name in $expected) {
        if (@($passed | Where-Object { $_ -eq $name }).Count -ne 1) { throw "Expected exactly one recorded '$name' scenario in the run result." }
    }
    $probeIndex = [Array]::IndexOf($passed, $TravelJournalPhase)
    foreach ($name in $TravelJournalReusedPhaseScenarios) {
        if ([Array]::IndexOf($passed, $name) -gt $probeIndex) { throw "The archived-journal comparison completed before the reused phase '$name'." }
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
    Assert-DungeonReadinessSelection $Root $provenance
    Assert-ForgeReadSelection $Root $provenance
    Assert-ModMenuProbeSelection $Root $provenance
    if ($provenance.scenario -ne 'MissingApi') { Assert-ApiPersistenceRoot $Root }
    $menuProperty = $provenance.PSObject.Properties['menuInspection']
    if ($menuProperty -and $menuProperty.Value -isnot [bool]) { throw 'Menu inspection selection must be boolean.' }
    $menuInspection = $menuProperty -and $menuProperty.Value
    $menuMarker = Join-Path $Root 'menu-inspection.enabled'
    if ([bool]$menuInspection -ne (Test-Path -LiteralPath $menuMarker -PathType Leaf)) { throw 'Menu inspection selection changed.' }
    if ($menuInspection -and ($provenance.scenario -ne 'MissingApi' -or [IO.File]::ReadAllText($menuMarker) -cne 'menu-inspection-v1' -or $provenance.vanillaLoadControl -or $provenance.missionJournal -or $provenance.stockpile -or $provenance.anima -or $provenance.echo -or $provenance.travelJournal)) { throw 'Invalid menu-only inspection selection.' }
    $missionProbe = $provenance.PSObject.Properties['missionTransitionsProbe'] -and [bool]$provenance.missionTransitionsProbe
    $missionMarker = Join-Path $Root 'mission-transitions.enabled'
    if ([bool]$missionProbe -ne (Test-Path -LiteralPath $missionMarker -PathType Leaf)) { throw 'Mission probe selection changed.' }
    if ($missionProbe) {
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $missionMarker -Raw).Trim() -ne 'missions-v1') { throw 'Invalid mission probe selection.' }
    }
    $identityProbe = $provenance.PSObject.Properties['missionIdentityProbe'] -and [bool]$provenance.missionIdentityProbe
    $identityMarker = Join-Path $Root 'mission-identity.enabled'
    if ([bool]$identityProbe -ne (Test-Path -LiteralPath $identityMarker -PathType Leaf)) { throw 'Mission identity selection changed.' }
    if ($identityProbe) {
        if (!$missionProbe -or !$provenance.persistenceProbe -or (Get-Content -LiteralPath $identityMarker -Raw).Trim() -ne 'identity-v1') { throw 'Invalid mission identity selection.' }
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
    }
    # The cross-system phase is an ADDITIONAL selection on top of the in-system phase; it reuses the
    # same native travel service and reserves its own separate process budget.
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
    # The ARCHIVED TravelJournal selection: the exact pinned prebuilt, its source revision, its
    # sandbox-only unbounded journal configuration, and mutual exclusion with the consumer probes.
    $travelJournal = $provenance.PSObject.Properties['travelJournal'] -and [bool]$provenance.travelJournal
    $journalMarkerRevision = Join-Path $Root 'travel-journal-revision.txt'
    if ([bool]$travelJournal -ne (Test-Path -LiteralPath $journalMarkerRevision -PathType Leaf)) { throw 'Archived TravelJournal selection changed.' }
    if ($travelJournal) {
        if ($provenance.travelJournalRevision -notmatch '^[0-9a-f]{40}$' -or
            (Get-Content -LiteralPath $journalMarkerRevision -Raw).Trim() -ne $provenance.travelJournalRevision) { throw 'Invalid archived TravelJournal source revision pin.' }
        if ($provenance.travelJournalSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid archived TravelJournal binary hash pin.' }
        $journalDll = Join-Path $Root 'game\BepInEx\plugins\VGTravelJournal.dll'
        # The deployed bytes, the recorded pins and the committed constants must all be the same one
        # build; the recorded pin alone can never authorise a different binary.
        Assert-TravelJournalPins $provenance.travelJournalRevision $provenance.travelJournalSha256 (Get-FileHash -LiteralPath $journalDll -Algorithm SHA256).Hash
        if ($provenance.travelJournalVersion -ne $TravelJournalAssemblyVersion) { throw 'The archived TravelJournal assembly version pin changed.' }
        if (Test-Path -LiteralPath (Join-Path $Root 'game\BepInEx\plugins\VGTravelJournal.pdb')) { throw 'The archived TravelJournal PDB must never be deployed.' }
        $null = Assert-TravelJournalConfigSemantics $Root
    }
    $travelJournalComparison = $provenance.PSObject.Properties['travelJournalComparison'] -and [bool]$provenance.travelJournalComparison
    $journalMarker = Join-Path $Root 'travel-journal.enabled'
    if ([bool]$travelJournalComparison -ne (Test-Path -LiteralPath $journalMarker -PathType Leaf)) { throw 'Archived-journal comparison selection changed.' }
    if ($travelJournalComparison) {
        if (!$travelJournal -or !$travelStation -or !$travelCrossSystem -or !$wormholeFixture -or
            (Get-Content -LiteralPath $journalMarker -Raw).Trim() -ne 'travel-journal-v1') { throw 'Invalid archived-journal comparison selection.' }
        if (!$provenance.PSObject.Properties['travelJournalBudgetSeconds'] -or
            [int]$provenance.travelJournalBudgetSeconds -ne $TravelJournalBudgetSeconds) { throw 'Archived-journal comparison budget reservation changed.' }
        if ($animaTravel -or $echoTravel) { throw 'The archived-journal comparison and a consumer travel probe both claim the reused travel phases.' }
        if ($provenance.scenario -ne 'Full') { throw 'Archived-journal comparison requires Full.' }
    }
    # The archived plugin patches the game, so it must never sit in a sandbox that does not compare
    # it, and never beside a consumer travel probe that owns the same reused phases.
    if ($travelJournal -and !$travelJournalComparison) { throw 'The archived TravelJournal is installed without the comparison that owns it; it is never a passive ridealong.' }
    if ($travelJournal -and ($animaTravel -or $echoTravel -or $anima -or $echo)) { throw 'The archived TravelJournal is installed beside a consumer plugin; the comparison sandbox carries the archive alone.' }
    # The fast-lane phase is an ADDITIONAL selection on top of the in-system phase; it drives its own
    # two-gate planner route and reserves its own separate process budget.
    $travelFastLane = $provenance.PSObject.Properties['travelFastLane'] -and [bool]$provenance.travelFastLane
    $fastLaneMarker = Join-Path $Root 'travel-fast-lane.enabled'
    if ([bool]$travelFastLane -ne (Test-Path -LiteralPath $fastLaneMarker -PathType Leaf)) { throw 'Travel fast-lane selection changed.' }
    if ($travelFastLane) {
        if (!$travelStation -or (Get-Content -LiteralPath $fastLaneMarker -Raw).Trim() -ne 'fast-lane-v1') { throw 'Invalid travel fast-lane selection.' }
        if (!$provenance.PSObject.Properties['travelFastLaneBudgetSeconds'] -or
            [int]$provenance.travelFastLaneBudgetSeconds -ne $TravelFastLaneBudgetSeconds) { throw 'Travel fast-lane budget reservation changed.' }
        if ($provenance.scenario -ne 'Full') { throw 'Travel fast-lane phase requires Full.' }
    }
    # The recovery/continuation phase is an ADDITIONAL selection on top of the in-system phase; it
    # drives its own routes and reserves its own separate process budget.
    $travelRecovery = $provenance.PSObject.Properties['travelRecovery'] -and [bool]$provenance.travelRecovery
    $recoveryMarker = Join-Path $Root 'travel-recovery.enabled'
    if ([bool]$travelRecovery -ne (Test-Path -LiteralPath $recoveryMarker -PathType Leaf)) { throw 'Travel recovery/continuation selection changed.' }
    if ($travelRecovery) {
        if (!$travelStation -or (Get-Content -LiteralPath $recoveryMarker -Raw).Trim() -ne 'recovery-continuation-v1') { throw 'Invalid travel recovery/continuation selection.' }
        if (!$provenance.PSObject.Properties['travelRecoveryBudgetSeconds'] -or
            [int]$provenance.travelRecoveryBudgetSeconds -ne $TravelRecoveryBudgetSeconds) { throw 'Travel recovery/continuation budget reservation changed.' }
    }
    # The resilience phase is an ADDITIONAL selection on top of the in-system phase; it reuses the
    # same native travel service and reserves its own separate process budget.
    $travelResilience = $provenance.PSObject.Properties['travelResilience'] -and [bool]$provenance.travelResilience
    $resilienceMarker = Join-Path $Root 'travel-resilience.enabled'
    if ([bool]$travelResilience -ne (Test-Path -LiteralPath $resilienceMarker -PathType Leaf)) { throw 'Travel resilience selection changed.' }
    if ($travelResilience) {
        if (!$travelStation -or (Get-Content -LiteralPath $resilienceMarker -Raw).Trim() -ne 'resilience-v1') { throw 'Invalid travel resilience selection.' }
        if (!$provenance.PSObject.Properties['travelResilienceBudgetSeconds'] -or
            [int]$provenance.travelResilienceBudgetSeconds -ne $TravelResilienceBudgetSeconds) { throw 'Travel resilience budget reservation changed.' }
    }
    $barConsumers = $provenance.PSObject.Properties['barConsumers'] -and $provenance.barConsumers -eq $true
    if ([bool]$barConsumers -ne (Test-Path -LiteralPath (Join-Path $Root 'bar-consumers.enabled') -PathType Leaf)) { throw 'Consumer bar selection changed.' }
    if ($barConsumers) { Assert-BarConsumerInputs $Root $provenance }
    $bars = $provenance.PSObject.Properties['barProbe'] -and $provenance.barProbe -eq $true
    if ([bool]$bars -ne (Test-Path -LiteralPath (Join-Path $Root 'bars.enabled') -PathType Leaf)) { throw 'Bar selection changed.' }
    $linkedBars = $provenance.PSObject.Properties['barLinkedStory'] -and $provenance.barLinkedStory -eq $true
    if ([bool]$linkedBars -ne (Test-Path -LiteralPath (Join-Path $Root 'bar-linked.enabled') -PathType Leaf)) { throw 'Linked bar selection changed.' }
    if ($linkedBars) {
        if (!$bars -or ($provenance.PSObject.Properties['barColdSequence'] -and $provenance.barColdSequence) -or
            (Get-Content -LiteralPath (Join-Path $Root 'bar-linked.enabled') -Raw) -cne 'linked-bars-v1') { throw 'Invalid linked bar selection.' }
        Assert-BarLinkedConfiguration $Root
    }
    if ($bars) {
        Assert-StoryIsolation $provenance
        foreach ($name in @('storyProbe','storyAbsentProbe','storyColdSequence','persistenceProbe','missionJournal','stockpile','anima','echo','travelJournal')) {
            if ($provenance.PSObject.Properties[$name] -and $provenance.$name) { throw "Bar probe conflicts with $name." }
        }
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath (Join-Path $Root 'bars.enabled') -Raw) -cne 'owned-bars-v1') { throw 'Invalid bar phase.' }
        $entries = Get-TravelJournalConfigEntries (Join-Path $Root 'game\BepInEx\config\vgmodapi.cfg')
        if (!$entries.ContainsKey('Bars/Enabled') -or $entries['Bars/Enabled'] -ine 'true') { throw 'Bar configuration requires Bars/Enabled=true.' }
        Assert-ApiPersistenceRoot $Root
        if ($entries['Bars/ExclusiveProviders'] -cne 'vg-bar-author-a,vg-bar-author-b') { throw 'Bar permissions changed.' }
        Add-Type -Path (Join-Path $Root 'game\BepInEx\core\Mono.Cecil.dll')
        $pluginDir = Join-Path $Root 'game\BepInEx\plugins'
        foreach ($name in @('VGModAPI','VGModAPI.Core','VGModAPI.Abstractions','QualificationRunner','QualificationGuard','LifecycleObserver','OwnedBarAuthorA','OwnedBarAuthorB')) {
            $reader = Read-ConsumerAssembly (Join-Path $pluginDir ($name + '.dll')) (Get-ConsumerMetadataReferenceDirs $pluginDir $Root (Join-Path $Root 'game'))
            try {
                Assert-QualificationAssemblyRevision $reader.Assembly $name $provenance.revision
                if ($name -like 'OwnedBarAuthor*') {
                    $types = @($reader.Assembly.MainModule.Types | Where-Object { $_.FullName -ceq 'OwnedBarAuthor.Plugin' })
                    if ($types.Count -ne 1) { throw 'Bar author plugin missing.' }
                    $attributes = @($types[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
                    $expectedId = if ($name -ceq 'OwnedBarAuthorA') { 'vg-bar-author-a' } else { 'vg-bar-author-b' }
                    if ($attributes.Count -ne 1 -or $attributes[0].ConstructorArguments[0].Value -cne $expectedId) { throw 'Bar author identity mismatch.' }
                }
            } finally { Close-ConsumerAssembly $reader }
        }
    }
    $story = $provenance.PSObject.Properties['storyProbe'] -and $provenance.storyProbe -eq $true
    $storyAbsent = $provenance.PSObject.Properties['storyAbsentProbe'] -and $provenance.storyAbsentProbe -eq $true
    if ([bool]$storyAbsent -ne (Test-Path -LiteralPath (Join-Path $Root 'story-absent.enabled') -PathType Leaf)) { throw 'Absent-story selection changed.' }
    if ($storyAbsent -and ($story -or $provenance.scenario -ne 'Full' -or $provenance.missionJournal -or $stockpile -or $anima -or $echo -or $travelJournal -or $provenance.persistenceProbe)) { throw 'Invalid absent-story isolation.' }
    if ($storyAbsent) {
        if (!$provenance.storyDonorRoot -or $provenance.storyDonorHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Missing absent-story donor provenance.' }
        $donor = Join-Path $provenance.storyDonorRoot 'Saves\qa-story-active.save'
        if ((Get-FileHash -LiteralPath $donor -Algorithm SHA256).Hash -ine $provenance.storyDonorHash -or (Get-FileHash -LiteralPath (Join-Path $Root 'Saves\fixture-a.save') -Algorithm SHA256).Hash -ine $provenance.storyDonorHash) { throw 'Absent-story source or copied native fixture changed.' }
    }
    if ($storyAbsent -and (Get-Content -LiteralPath (Join-Path $Root 'story-absent.enabled') -Raw).Trim() -ne 'owned-story-absent-v1') { throw 'Unknown absent-story marker.' }
    if ($story -or $storyAbsent) {
        Assert-StoryConfiguration $Root
        Assert-StoryIsolation $provenance
    }
    $probe = $provenance.PSObject.Properties['persistenceProbe'] -and [bool]$provenance.persistenceProbe
    $probeMarker = Join-Path $Root 'persistence-probe.enabled'
    if ([bool]$probe -ne (Test-Path -LiteralPath $probeMarker -PathType Leaf)) { throw 'Persistence probe selection changed.' }
    if ($probe) {
        if ($provenance.scenario -ne 'Full' -or (Get-Content -LiteralPath $probeMarker -Raw).Trim() -ne 'probe-v1') { throw 'Invalid persistence probe marker.' }
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
    if ([bool]$story -ne (Test-Path -LiteralPath (Join-Path $Root 'story.enabled') -PathType Leaf)) { throw 'Story selection changed.' }
    if ($story) {
        if ($provenance.scenario -ne 'Full' -or $provenance.missionJournal -or $stockpile -or $anima -or $echo -or $travelJournal -or $provenance.persistenceProbe) { throw 'Invalid story probe isolation.' }
        if ((Get-Content -LiteralPath (Join-Path $Root 'story.enabled') -Raw).Trim() -ne 'owned-story-v1') { throw 'Unknown story marker.' }
    }
    $expected = @('QualificationGuard.dll')
    if ($story) { $expected += @('OwnedStoryCampaign.dll','OwnedStoryJob.dll') }
    if ($bars) { $expected += @('OwnedBarAuthorA.dll','OwnedBarAuthorB.dll') }
    if ($barConsumers) { $expected += @('VGAnima.dll','VGTTS.dll','VanguardGalaxy.CustomMission.dll','Newtonsoft.Json.dll') }
    if ($provenance.scenario -ne 'MissingApi') { $expected += @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','vgmodapi.vgmod.json') }
    if ($provenance.scenario -eq 'Full') { $expected += @('QualificationRunner.dll','LifecycleObserver.dll') }
    if ($provenance.missionJournal) { $expected += @('VGMissionJournal.dll','Newtonsoft.Json.dll') }
    if ($stockpile) { $expected += @('VGStockpile.dll','Newtonsoft.Json.dll') }
    if ($anima) { $expected += @('VGAnima.dll') }
    if ($echo) { $expected += @('VGEcho.dll') }
    if ($travelJournal) { $expected += @('VGTravelJournal.dll') }
    if ($provenance.PSObject.Properties['blueprintPinProbe'] -and $provenance.blueprintPinProbe) { $expected += @('VGBlueprintPin.dll') }
    $expected = @($expected | Select-Object -Unique)
    if (@($provenance.plugins.PSObject.Properties).Count -ne $expected.Count -or
        @($provenance.plugins.PSObject.Properties.Name | Where-Object { $_ -notin $expected }).Count -gt 0) { throw 'Scenario plugin allowlist mismatch.' }
    $plugins = Join-Path $Root 'game\BepInEx\plugins'
    $actual = @(Get-ChildItem -LiteralPath $plugins -Force)
    if ($barConsumers) {
        # The single sealed tools tree is separately checked, including empty directories and links.
        $actual = @($actual | Where-Object { !($_.PSIsContainer -and $_.Name -ceq 'tools') })
    }
    if ($actual.Count -ne @($provenance.plugins.PSObject.Properties).Count -or
        @($actual | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -notin @($provenance.plugins.PSObject.Properties.Name) }).Count -gt 0) { throw 'Prepared plugin set changed.' }
    foreach ($property in $provenance.plugins.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $plugins $property.Name) -Algorithm SHA256).Hash -ne $property.Value) { throw 'Prepared plugin changed; refuse stale provenance.' }
    }
    return $provenance
}
