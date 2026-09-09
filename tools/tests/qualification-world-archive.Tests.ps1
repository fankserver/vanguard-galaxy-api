$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-archive.ps1')
function Assert-NoWorldGameProcess { } # No process enumeration or launch in this fixture.
$base=Join-Path $env:TEMP ('world-archive-source-'+[Guid]::NewGuid().ToString('N'))
$root=Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'Temp')) ('VGModAPI-qa-'+(Get-Random -Minimum 1000000000 -Maximum 2000000000))
$created=$false; $baseCreated=$false
try {
    $null=New-Item -ItemType Directory $base; $baseCreated=$true
    $null=New-Item -ItemType Directory $root; $created=$true
    foreach ($name in @('game','Saves','state','temp')) { $null=New-Item -ItemType Directory (Join-Path $root $name) }
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge')) {
        $null=New-Item -ItemType Directory (Join-Path $base $name)
        $null=New-Item -ItemType Junction -Path (Join-Path $root ('game\'+$name)) -Target (Join-Path $base $name)
    }
    foreach ($name in @('world-launch-before.json','world-launch-after.json','world-process-outcome.json','world-prefs-receipt.json','world-created-generation.txt','Player.log','result.txt','events.tsv','isolation-armed.txt','owned-world.txt','world-qualification.authorization','qualification.marker','scenario.txt','original-save-directory.txt','world.enabled','game\fixture.bin','Saves\qa-owned-world.save','state\fixture','temp\scratch')) { [IO.File]::WriteAllText((Join-Path $root $name), 'synthetic evidence') }
    $head='a'*40; $run=[Guid]::NewGuid()
    $record=@{schema='world-empty-phase-v1'; root=$root; runId=[Guid]::NewGuid().ToString('D'); phase='create'; reviewedHead=$head; approvalSha256=('b'*64); pid=42; startedUtc='2026-01-01T00:00:00.0000000Z'; generationReceiptSha256=(Get-FileHash (Join-Path $root 'world-created-generation.txt') -Algorithm SHA256).Hash.ToLowerInvariant()}
    $accepted=Join-Path $root 'world-phase-accepted.json'; [IO.File]::WriteAllText($accepted,($record|ConvertTo-Json))
    $digest=(Get-FileHash $accepted -Algorithm SHA256).Hash.ToLowerInvariant()
    $archive=Save-WorldCreationArchive $root $base $run $head $digest -ExclusiveLeaseConfirmed
    foreach ($name in @('world-phase-accepted.json','Player.log','game-owned\fixture.bin','Saves\qa-owned-world.save','state\fixture','temp\scratch','temp-original\scratch','world-creation-archive.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $archive $name) -PathType Leaf)) { throw "Missing archived evidence: $name" }
    }
    if (Test-Path (Join-Path $root 'Player.log')) { throw 'Prior output not cleared after preservation.' }
    if (@(Get-ChildItem (Join-Path $root 'temp') -Force).Count) { throw 'New temporary directory not empty.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'Saves\qa-owned-world.save')) -cne 'synthetic evidence' -or !(Test-Path (Join-Path $root 'world-created-generation.txt'))) { throw 'Canonical inputs moved or changed.' }
    if (Test-Path (Join-Path $archive 'game-owned\VanguardGalaxy_Data')) { throw 'Resource junction copied.' }
    $null=Read-WorldCreationEvidence $root $run $head $digest
    Write-Output 'Creation archive tests passed with synthetic files and owned resource junctions only.'
} finally {
    if ($created) {
        foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge')) {
            $link=Join-Path $root ('game\'+$name); $entry=Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
            if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($link,$false) }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if ($baseCreated) { Remove-Item -LiteralPath $base -Recurse -Force }
}
