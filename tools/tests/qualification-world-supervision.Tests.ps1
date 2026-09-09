$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-supervision.ps1')
Add-Type @'
using System;
using System.Diagnostics;
public sealed class WorldFakeProcess {
    public ProcessStartInfo StartInfo { get; set; }
    public string Mode;
    public bool Disposed, Killed, Exited, Associated;
    public int Calls;
    public int Id { get { if (Mode == "inspection") throw new Exception("association inspection failed"); if (!Associated) throw new InvalidOperationException("not associated"); return 42; } }
    public IntPtr Handle { get { if (Mode == "handle") throw new Exception("handle failed"); return new IntPtr(1); } }
    public bool HasExited { get { return Exited; } }
    public int ExitCode { get { return 0; } }
    public bool Start() { if (Mode == "start" || Mode == "inspection") throw new Exception("start failed"); if (Mode == "false") return false; Associated = true; if (Mode == "partial-start") throw new Exception("start failed after association"); return true; }
    public bool WaitForExit(int milliseconds) { Calls++; if (Mode == "wait" && !Killed) throw new Exception("wait failed"); if (Mode == "exit" || Killed) Exited = true; return Exited; }
    public void Kill() { Killed = true; if (Mode == "unkillable") throw new Exception("kill failed"); Exited = true; }
    public void Dispose() { Disposed = true; }
}
'@
$info = New-Object Diagnostics.ProcessStartInfo; $info.UseShellExecute = $false
foreach ($mode in @('exit','timeout','start','false','handle','wait','unkillable','partial-start','inspection')) {
    $process = New-Object WorldFakeProcess; $process.Mode = $mode
    $outcome = Invoke-WorldProcessLifetime $info 1 $process
    $accepted = $true
    try { Assert-WorldProcessOutcome $outcome } catch { $accepted = $false }
    if ($accepted -ne ($mode -eq 'exit')) { throw 'Process outcome gate accepted failure or rejected clean exit.' }
    if ($mode -in @('start','false')) {
        if ($outcome.started -or $outcome.killed -or !$outcome.failure -or !$process.Disposed) { throw 'Incorrect failed-start cleanup.' }
    } elseif ($mode -in @('unkillable','inspection')) {
        if (!$outcome.cleanupPending -or !$outcome.cleanupFailure -or $process.Disposed) { throw 'Live owned handle was lost.' }
    } else {
        if (!$outcome.started -or $outcome.pid -ne 42 -or $outcome.exitCode -ne 0 -or !$process.Disposed -or $outcome.cleanupPending) { throw 'Incorrect owned-process completion.' }
        if ($mode -eq 'exit' -and ($outcome.killed -or $outcome.timedOut -or $outcome.failure)) { throw 'Clean exit misclassified.' }
        if ($mode -eq 'timeout' -and (!$outcome.timedOut -or !$outcome.killed)) { throw 'Timeout not retained.' }
        if ($mode -in @('handle','wait','partial-start') -and (!$outcome.failure -or !$outcome.killed)) { throw 'Failure cleanup lost evidence.' }
    }
}
$clean = @{ started=$true; pid=42; timedOut=$false; killed=$false; cleanupPending=$false; failure=$null; cleanupFailure=$null; exitCode=-1 }
Assert-WorldProcessOutcome $clean
foreach ($code in @(1, 255, -1073741819)) {
    $clean.exitCode = $code; $accepted = $true
    try { Assert-WorldProcessOutcome $clean } catch { $accepted = $false }
    if ($accepted) { throw 'Unexpected exit code accepted.' }
}
Write-Output 'World supervision state tests passed with fake processes; no child started.'
