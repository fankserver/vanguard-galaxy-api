. (Join-Path $PSScriptRoot 'qualification-world-preservation.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-prefs.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-evidence.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-supervision.ps1')
. (Join-Path $PSScriptRoot 'qualification-world-receipts.ps1')

function Assert-NoWorldGameProcess {
    if (@(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -ieq 'VanguardGalaxy' }).Count) { throw 'A game process already exists; refuse launch.' }
}

# Explicit operator attestation is NOT a lease issuer or automatic lease transfer. The caller
# must already hold the externally coordinated exclusive lease and maintainer run authorization.
# This helper never releases that lease, including on cleanup/restoration/evidence failures.
function Invoke-WorldQualificationPhase([string]$Root, [Guid]$RunId,
    [ValidateSet('create','cold')][string]$Phase, [string]$ReviewedHead,
    [string]$ApprovalPath, [string]$ApprovalDigest, [switch]$ExclusiveLeaseConfirmed,
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 600) {
    if (!$ExclusiveLeaseConfirmed) { throw 'Explicit confirmation of an already-held exclusive lease is required.' }
    $record = Assert-WorldRunPreflight $Root $RunId $Phase $ReviewedHead $ApprovalPath $ApprovalDigest
    if ($record.timeoutSeconds -ne $TimeoutSeconds) { throw 'Process deadline differs from approved run.' }
    Assert-NoWorldGameProcess
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked root-level artifact refused.' }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $Root 'temp') -Force -ErrorAction Stop).Count) { throw 'World temporary directory must be empty before launch.' }
    if (Test-Path -LiteralPath (Join-Path $Root 'assembly-overlay.hash')) { throw 'World phase does not admit an assembly overlay.' }
    foreach ($name in @('world-launch-before.json','world-launch-after.json','world-process-outcome.json','world-prefs-receipt.json','result.txt','owned-world.txt','world-cold-generation.txt','events.tsv','isolation-armed.txt')) {
        $path = Assert-WorldUnlinkedPath (Join-Path $Root $name) $false
        if (Test-Path -LiteralPath $path) { throw 'Retain prior phase outputs before another run.' }
    }
    $null = Assert-WorldUnlinkedPath (Join-Path $Root 'world-created-generation.txt') $false
    if ($Phase -eq 'cold') { $null = Assert-WorldGenerationReceipt $Root 'world-created-generation.txt' }
    elseif (Test-Path -LiteralPath (Join-Path $Root 'world-created-generation.txt')) { throw 'Creation receipt already exists.' }
    $before = Get-WorldRunPreservation $record
    $prefs = Save-WorldPrefs $Root
    $null = Write-WorldPrivateEvidence $Root 'world-prefs-receipt.json' $prefs
    $null = Write-WorldPrivateEvidence $Root 'world-launch-before.json' @{
        runId=$RunId.ToString('D'); phase=$Phase; reviewedHead=$ReviewedHead; approvalSha256=$ApprovalDigest;
        preservation=$before; process=$record.process; timeoutSeconds=$TimeoutSeconds
    }
    # Recheck after recovery evidence is durable; only this exact description is passed to Start.
    $record = Assert-WorldRunPreflight $Root $RunId $Phase $ReviewedHead $ApprovalPath $ApprovalDigest
    Assert-WorldPreservation $before (Get-WorldRunPreservation $record)
    $info = New-WorldProcessStartInfo $Root $RunId $Phase
    Assert-WorldProcessDescription $info $record.process
    Assert-NoWorldGameProcess
    $process = New-Object Diagnostics.Process
    $outcome = $null
    try {
        $outcome = Invoke-WorldProcessLifetime $info $TimeoutSeconds $process
        $evidenceFailure = $null
        try { $null = Write-WorldPrivateEvidence $Root 'world-process-outcome.json' $outcome }
        catch { $evidenceFailure = $_.Exception.ToString() }
        if ($outcome.cleanupPending -or $null -ne $outcome.cleanupFailure) { throw 'Owned process cleanup remains unresolved; retain lease and recovery evidence.' }
        $prefsFailure = $null; $preservationFailure = $null; $after = $null
        try { Restore-WorldPrefs $Root $prefs $outcome } catch { $prefsFailure = $_.Exception.ToString() }
        try {
            $after = Get-WorldRunPreservation $record
            Assert-WorldPreservation $before $after
        } catch { $preservationFailure = $_.Exception.ToString() }
        $null = Write-WorldPrivateEvidence $Root 'world-launch-after.json' @{
            runId=$RunId.ToString('D'); phase=$Phase; preservation=$after;
            preferencesFailure=$prefsFailure; preservationFailure=$preservationFailure; processEvidenceFailure=$evidenceFailure; processOutcome=$outcome
        }
        if ($null -ne $prefsFailure -or $null -ne $preservationFailure -or $null -ne $evidenceFailure) { throw 'World preservation failed; retain private recovery evidence.' }
        Assert-WorldProcessOutcome $outcome
        $resultPath = Assert-WorldUnlinkedPath (Join-Path $Root 'result.txt')
        $result = Read-WorldReceipt $resultPath
        if ($result.Count -lt 1 -or $result[0] -cne 'PASS') { throw 'World runner did not report PASS.' }
        Assert-WorldReceiptPaths $Root $Phase
        Assert-WorldPhaseReceipt $Root $Phase
        return $outcome
    } catch {
        # Preserve the actual object for the supervising caller, never search/kill by PID or name.
        if ($null -eq $outcome -or $outcome.cleanupPending) { $_.Exception.Data['OwnedWorldProcess'] = $process }
        throw
    }
}
