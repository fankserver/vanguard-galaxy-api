# Caller supplies the exact validated description and a fresh process object retained until cleanup.
# This function is not an authorization boundary; do not call it without the exclusive native lease.
function Invoke-WorldProcessLifetime([Diagnostics.ProcessStartInfo]$Info, [ValidateRange(1,3600)][int]$TimeoutSeconds, $Process) {
    if ($null -eq $Process -or $null -eq $Info -or $Info.UseShellExecute) { throw 'Owned process and shell-disabled description required.' }
    $outcome = @{ started=$false; pid=$null; timedOut=$false; killed=$false; exitCode=$null; failure=$null; cleanupFailure=$null; cleanupPending=$false }
    try {
        $Process.StartInfo = $Info
        if (!$Process.Start()) { throw 'Owned world process did not start.' }
        $outcome.started = $true
        $outcome.pid = $Process.get_Id()
        $null = $Process.get_Handle() # Retain the native process handle before waiting.
        if (!$Process.WaitForExit($TimeoutSeconds * 1000)) { $outcome.timedOut = $true }
    } catch { $outcome.failure = $_.Exception.ToString() }
    finally {
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
