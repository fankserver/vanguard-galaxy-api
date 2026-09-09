$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-supervision.ps1')
Add-Type @'
using System;
using System.Diagnostics;
public sealed class WorldFakeProcess {
    public ProcessStartInfo StartInfo { get; set; }
    public string Mode;
    public bool Disposed, Killed, Exited;
    public int Calls;
    public int Id { get { return 42; } }
    public IntPtr Handle { get { if (Mode == "handle") throw new Exception("handle failed"); return new IntPtr(1); } }
    public bool HasExited { get { return Exited; } }
    public int ExitCode { get { return 0; } }
    public bool Start() { if (Mode == "start") throw new Exception("start failed"); if (Mode == "false") return false; return true; }
    public bool WaitForExit(int milliseconds) { Calls++; if (Mode == "wait" && !Killed) throw new Exception("wait failed"); if (Mode == "exit" || Killed) Exited = true; return Exited; }
    public void Kill() { Killed = true; if (Mode == "unkillable") throw new Exception("kill failed"); Exited = true; }
    public void Dispose() { Disposed = true; }
}
'@
$info = New-Object Diagnostics.ProcessStartInfo; $info.UseShellExecute = $false
foreach ($mode in @('exit','timeout','start','false','handle','wait','unkillable')) {
    $process = New-Object WorldFakeProcess; $process.Mode = $mode
    $outcome = Invoke-WorldProcessLifetime $info 1 $process
    if ($mode -in @('start','false')) {
        if ($outcome.started -or $outcome.killed -or !$outcome.failure -or !$process.Disposed) { throw 'Incorrect failed-start cleanup.' }
    } elseif ($mode -eq 'unkillable') {
        if (!$outcome.cleanupPending -or !$outcome.cleanupFailure -or $process.Disposed) { throw 'Live owned handle was lost.' }
    } else {
        if (!$outcome.started -or $outcome.pid -ne 42 -or $outcome.exitCode -ne 0 -or !$process.Disposed -or $outcome.cleanupPending) { throw 'Incorrect owned-process completion.' }
        if ($mode -eq 'exit' -and ($outcome.killed -or $outcome.timedOut -or $outcome.failure)) { throw 'Clean exit misclassified.' }
        if ($mode -eq 'timeout' -and (!$outcome.timedOut -or !$outcome.killed)) { throw 'Timeout not retained.' }
        if ($mode -in @('handle','wait') -and (!$outcome.failure -or !$outcome.killed)) { throw 'Failure cleanup lost evidence.' }
    }
}
Write-Output 'World supervision state tests passed with fake processes; no child started.'
