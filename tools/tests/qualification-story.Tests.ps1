$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-profile.ps1')
. (Join-Path $PSScriptRoot '..\qualification-story.ps1')
. (Join-Path $PSScriptRoot '..\qualification-inputs.ps1')
$root = Join-Path $env:TEMP ('vg-story-receipt-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw 'Invalid story evidence was accepted.' }
}
try {
    $cases = @('independent-authors','offered-roundtrip','active-roundtrip','native-completion','save-refusals','older-save-rollback','cross-slot-return','repeat-job','provider-unregistered-first-reload','provider-unregistered-second-reload')
    $cases | Set-Content (Join-Path $root 'story-cases.txt')
    @('PASS','owned-story-v1') | Set-Content (Join-Path $root 'story-result.txt')
    @('PASS','owned-new-game-roundtrip-v1') | Set-Content (Join-Path $root 'story-new-game.txt')
    $outcome = @{ timedOut=$false; killed=$false; selfTerminated=$true; exitCode=0 }
    $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    Assert-StoryReceipt $root
    Remove-Item (Join-Path $root 'story-new-game.txt')
    Reject { Assert-StoryReceipt $root }
    @('PASS','owned-new-game-roundtrip-v1') | Set-Content (Join-Path $root 'story-new-game.txt')
    foreach ($code in @(-1,0)) {
        $outcome.exitCode=$code
        $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Assert-StoryReceipt $root
    }
    foreach ($field in @('timedOut','killed','selfTerminated','exitCode')) {
        $bad=$outcome.Clone(); $bad.Remove($field)
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Reject { Assert-StoryReceipt $root }
    }
    foreach ($pair in @(@('timedOut',$true),@('killed',$true),@('selfTerminated',$false),@('exitCode',3),@('exitCode',$null),@('timedOut','false'))) {
        $bad=$outcome.Clone(); $bad[$pair[0]]=$pair[1]
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Reject { Assert-StoryReceipt $root }
    }
    $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    for ($i=0; $i -lt $cases.Count; $i++) {
        @($cases | Where-Object { $_ -ne $cases[$i] }) | Set-Content (Join-Path $root 'story-cases.txt')
        Reject { Assert-StoryReceipt $root }
    }
    $configDir = Join-Path $root 'game\BepInEx\config'
    New-Item -ItemType Directory $configDir -Force | Out-Null
    $configPath = Join-Path $configDir 'vgmodapi.cfg'
    $valid = "[Persistence]`nEnabled = true`nRoot = $(Join-Path $root 'state')`n[Story]`nEnabled = true`nProtection = true`n[Missions]`nEnabled = true`n"
    $valid | Set-Content $configPath
    Assert-StoryConfiguration $root
    foreach ($badConfig in @($valid.Replace('true','false'), $valid.Replace('Root =','WrongRoot ='), ($valid + "[Story]`nEnabled = true`n"), ($valid + "[Persistence]`nRoot = wrong`n"))) {
        $badConfig | Set-Content $configPath
        Reject { Assert-StoryConfiguration $root }
    }
    function Attribute($type, $values) {
        return [pscustomobject]@{ AttributeType=[pscustomobject]@{FullName=$type}; ConstructorArguments=@($values | ForEach-Object { [pscustomobject]@{Value=$_} }) }
    }
    function Author($revision, $pluginId='vg-story-campaign', $dependency=1) {
        return [pscustomobject]@{
            Name=[pscustomobject]@{Name='OwnedStoryCampaign'}
            CustomAttributes=@((Attribute 'System.Reflection.AssemblyInformationalVersionAttribute' @("0.1.12+$revision")))
            MainModule=[pscustomobject]@{Types=@([pscustomobject]@{
                FullName='OwnedStoryCampaign.Plugin'
                CustomAttributes=@((Attribute 'BepInEx.BepInPlugin' @($pluginId,'Example','0.1.0')), (Attribute 'BepInEx.BepInDependency' @('vgmodapi',$dependency)))
            })}
        }
    }
    Assert-StoryIsolation ([pscustomobject]@{})
    foreach ($name in @('menuInspection','modMenuProbe','travelStation','travelCrossSystem','travelWormholeFixture','travelResilience','travelRecovery','travelFastLane','missionTransitionsProbe','missionIdentityProbe','contentReferenceProbe','journalMissionEventsProbe','journalCoordinated','stockpileCoordinated','vanillaLoadControl','assemblyOverlay','echoAbsentProbe','echoTravelProbe','animaTravelProbe','travelJournalComparison')) {
        Reject { Assert-StoryIsolation ([pscustomobject]@{ $name=$true }) }
        Assert-StoryIsolation ([pscustomobject]@{ $name=$false })
    }
    $revision = 'a' * 40
    Assert-QualificationAssemblyRevision (Author $revision) 'OwnedStoryCampaign' $revision
    Reject { Assert-QualificationAssemblyRevision (Author ('b' * 40)) 'OwnedStoryCampaign' $revision }
    Reject { Assert-QualificationAssemblyRevision (Author $revision) 'VGModAPI' $revision }
    Assert-StoryAuthorMetadata (Author $revision) 'OwnedStoryCampaign' $revision
    Reject { Assert-StoryAuthorMetadata (Author ('b' * 40)) 'OwnedStoryCampaign' $revision }
    Reject { Assert-StoryAuthorMetadata (Author $revision 'wrong-plugin') 'OwnedStoryCampaign' $revision }
    Reject { Assert-StoryAuthorMetadata (Author $revision 'vg-story-campaign' 2) 'OwnedStoryCampaign' $revision }
    Reject { Assert-StoryAuthorMetadata (Author $revision) 'OwnedStoryJob' $revision }
    $outcome | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    @('PASS','owned-story-absent-assemblies-v1') | Set-Content (Join-Path $root 'story-absent.txt')
    Assert-StoryAbsentReceipt $root
    Remove-Item (Join-Path $root 'story-absent.txt')
    Reject { Assert-StoryAbsentReceipt $root }
    @('PASS','owned-story-absent-assemblies-v1') | Set-Content (Join-Path $root 'story-absent.txt')
    foreach ($field in @('timedOut','killed','selfTerminated','exitCode')) {
        $bad=$outcome.Clone(); $bad.Remove($field)
        $bad | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
        Reject { Assert-StoryAbsentReceipt $root }
    }
    Write-Output 'PASS: story receipt, config, absent-author outcome and stale/wrong author rejection.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
