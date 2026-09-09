. (Join-Path $PSScriptRoot 'qualification-world-run.ps1')

# Inventory is freshly collected locally, never interpreted from an untrusted JSON record.
function Copy-WorldEvidenceTree([string]$Source, [string]$Destination, $Inventory) {
    $null = New-Item -ItemType Directory $Destination
    foreach ($entry in $Inventory.GetEnumerator()) {
        $target = Join-Path $Destination $entry.Key.Replace('/', '\')
        if ($entry.Value -ceq 'D') { $null = New-Item -ItemType Directory $target -Force }
        elseif ($entry.Value.StartsWith('F:', [StringComparison]::Ordinal)) {
            $null = New-Item -ItemType Directory ([IO.Path]::GetDirectoryName($target)) -Force
            [IO.File]::Copy((Join-Path $Source $entry.Key.Replace('/', '\')), $target, $false)
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Value.Substring(2)) { throw 'Archived file differs from observed source.' }
        } elseif (!$entry.Value.StartsWith('J:', [StringComparison]::Ordinal)) { throw 'Unknown archive inventory entry.' }
        # Resource junctions remain inventory metadata only; never copy or traverse their targets.
    }
}

# Requires external lease and a separately approved creation acceptance digest. Originals are
# retained until copies verify; partial archives are kept for inspection, never silently retried.
function Save-WorldCreationArchive([string]$Root, [string]$GameDirectory, [Guid]$ColdRunId,
    [string]$ReviewedHead, [string]$CreationDigest, [switch]$ExclusiveLeaseConfirmed) {
    if (!$ExclusiveLeaseConfirmed) { throw 'Already-held exclusive lease confirmation required.' }
    if ($ColdRunId -eq [Guid]::Empty) { throw 'Cold run identity must be nonempty.' }
    $rootPath = Assert-WorldSandboxRoot $Root
    Assert-NoWorldGameProcess
    $archive = Assert-WorldUnlinkedPath (Join-Path $rootPath 'creation-evidence') $false
    if (Test-Path -LiteralPath $archive) { throw 'Creation archive already exists; retain and inspect it.' }
    $files = @(Get-ChildItem -LiteralPath $rootPath -Force -ErrorAction Stop)
    foreach ($entry in $files) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked root artifact refused.' }
        if ($entry.PSIsContainer -and $entry.Name -cnotin @('game','Saves','state','temp')) { throw 'Unknown root directory must be preserved explicitly.' }
    }
    foreach ($name in @('world-launch-before.json','world-launch-after.json','world-process-outcome.json','world-prefs-receipt.json','world-phase-accepted.json','world-created-generation.txt','Player.log','result.txt','events.tsv','isolation-armed.txt','owned-world.txt','world-qualification.authorization','qualification.marker','scenario.txt','original-save-directory.txt','world.enabled')) {
        $path = Assert-WorldUnlinkedPath (Join-Path $rootPath $name)
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Required creation evidence is missing.' }
    }
    $null = New-Item -ItemType Directory $archive
    # Establish the externally pinned source before copying bulk evidence or changing any original.
    [IO.File]::Copy((Join-Path $rootPath 'world-phase-accepted.json'), (Join-Path $archive 'world-phase-accepted.json'), $false)
    $null = Read-WorldCreationEvidence $rootPath $ColdRunId $ReviewedHead $CreationDigest
    $rootInventory = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($entry in $files | Where-Object { !$_.PSIsContainer }) {
        $hash = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $rootInventory.Add($entry.Name, 'F:' + $hash)
        $target = Join-Path $archive $entry.Name
        if ($entry.Name -cne 'world-phase-accepted.json') { [IO.File]::Copy($entry.FullName, $target, $false) }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Root evidence changed during archival.' }
    }
    $inventories = @{}
    $inventories.game = Get-WorldLaunchInventory $rootPath $GameDirectory
    Copy-WorldEvidenceTree (Join-Path $rootPath 'game') (Join-Path $archive 'game-owned') $inventories.game
    foreach ($name in @('Saves','state','temp')) {
        $inventories[$name] = Get-WorldDataInventory (Join-Path $rootPath $name)
        Copy-WorldEvidenceTree (Join-Path $rootPath $name) (Join-Path $archive $name) $inventories[$name]
    }
    $receipt = Write-WorldPrivateEvidence $rootPath 'world-creation-archive.json' @{ root=$rootPath; creationSha256=$CreationDigest; rootFiles=$rootInventory; trees=$inventories }
    [IO.File]::Copy($receipt, (Join-Path $archive 'world-creation-archive.json'), $false)
    # Clear only outputs with verified copies. Saves/state and the canonical generation receipt stay.
    foreach ($name in @('world-launch-before.json','world-launch-after.json','world-process-outcome.json','world-prefs-receipt.json','world-phase-accepted.json','Player.log','result.txt','events.tsv','isolation-armed.txt','owned-world.txt','playerprefs-before.reg','playerprefs-before.reg.verified.reg')) {
        if (!$rootInventory.ContainsKey($name)) { continue }
        $path = Assert-WorldUnlinkedPath (Join-Path $rootPath $name)
        if ('F:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $rootInventory[$name]) { throw 'Original evidence changed before output cleanup.' }
        [IO.File]::Delete($path)
    }
    # Retain the original temporary directory too; no recursive deletion is needed.
    [IO.Directory]::Move((Join-Path $rootPath 'temp'), (Join-Path $archive 'temp-original'))
    $null = New-Item -ItemType Directory (Join-Path $rootPath 'temp')
    return $archive
}
