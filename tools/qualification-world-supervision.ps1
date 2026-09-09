# Caller supplies the exact validated description and a fresh process object retained until cleanup.
# This function is not an authorization boundary; do not call it without the exclusive native lease.
function Invoke-WorldProcessLifetime([Diagnostics.ProcessStartInfo]$Info, [ValidateRange(1,3600)][int]$TimeoutSeconds, $Process) {
    if ($null -eq $Process -or $null -eq $Info -or $Info.UseShellExecute) { throw 'Owned process and shell-disabled description required.' }
    $outcome = @{ started=$false; pid=$null; startedUtc=$null; timedOut=$false; killed=$false; exitCode=$null; failure=$null; cleanupFailure=$null; cleanupPending=$false }
    $startAttempted = $false
    try {
        $Process.StartInfo = $Info
        $startAttempted = $true
        if (!$Process.Start()) { throw 'Owned world process did not start.' }
        $outcome.started = $true
        $outcome.pid = $Process.get_Id()
        $null = $Process.get_Handle() # Retain the native process handle before waiting.
        $outcome.startedUtc = $Process.get_StartTime().ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        if (!$Process.WaitForExit($TimeoutSeconds * 1000)) { $outcome.timedOut = $true }
    } catch { $outcome.failure = $_.Exception.ToString() }
    finally {
        if ($startAttempted -and !$outcome.started) {
            try {
                # Start can fail after associating a child. Recover ownership from this object,
                # never by searching a name or assuming that a throwing call did nothing.
                $outcome.pid = $Process.get_Id()
                if ($outcome.pid -le 0) { throw [IO.InvalidDataException]::new('Invalid associated process identity.') }
                $outcome.started = $true
            } catch {
                # Process.Id documents InvalidOperationException for an unassociated object.
                # Other inspection failures cannot establish absence and retain the handle.
                if ($_.Exception.GetBaseException() -isnot [InvalidOperationException]) {
                    $outcome.cleanupFailure = $_.Exception.ToString()
                    $outcome.cleanupPending = $true
                }
            }
        }
        try {
            if ($outcome.started) {
                if (!$Process.get_HasExited()) {
                    $outcome.killed = $true
                    $Process.Kill()
                    $null = $Process.WaitForExit(30000)
                }
                $outcome.cleanupPending = !$Process.get_HasExited()
                if (!$outcome.cleanupPending) { $outcome.exitCode = $Process.get_ExitCode() }
            }
        } catch { $outcome.cleanupFailure = $_.Exception.ToString(); $outcome.cleanupPending = $outcome.started }
        if (!$outcome.cleanupPending) {
            try { $Process.Dispose() } catch { $outcome.cleanupFailure = $_.Exception.ToString() }
        }
        # Never dispose a still-running handle or imply its lease can be released.
    }
    return $outcome
}

function Assert-WorldProcessOutcome($Outcome) {
    if ($null -eq $Outcome -or !$Outcome.started -or $null -eq $Outcome.pid -or $Outcome.pid -le 0 -or [string]::IsNullOrEmpty($Outcome.startedUtc) -or
        $Outcome.timedOut -or $Outcome.killed -or $Outcome.cleanupPending -or
        $null -ne $Outcome.failure -or $null -ne $Outcome.cleanupFailure -or $null -eq $Outcome.exitCode) {
        throw 'World process did not complete cleanly; retain failure evidence and any pending ownership.'
    }
    # Inspected game shutdown may self-terminate with -1; receipts must independently prove success.
    if ($Outcome.exitCode -ne 0 -and $Outcome.exitCode -ne -1) { throw 'Unexpected world process exit code.' }
}
