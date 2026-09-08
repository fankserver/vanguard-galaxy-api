param([Parameter(Mandatory=$true)][string]$SandboxRoot, [int]$TimeoutSeconds = 600)
$ErrorActionPreference = 'Stop'
if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 900) { throw 'Browser observation budget out of bounds.' }
$root = (Resolve-Path -LiteralPath $SandboxRoot).Path
$expectedRoot = Join-Path $env:LOCALAPPDATA 'Temp'
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked browser observation root is forbidden.' }
if (!(Split-Path $root -Parent).Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $root -Leaf) -notmatch '^VGModAPI-qa-[0-9]+$') { throw 'Browser observation needs an owned qualification root.' }
if ([IO.File]::ReadAllText((Join-Path $root 'mod-information-probe.enabled')) -cne 'mod-information-probe-v3') { throw 'Browser observer requires full information selection.' }
$request = Join-Path $root 'browser-launch-request.txt'
$receipt = Join-Path $root 'browser-launch.receipt'
$image = Join-Path $root 'mod-release-browser.png'
foreach ($path in @($request,$receipt,$image)) { if (Test-Path -LiteralPath $path) { throw 'Refusing to reuse browser evidence.' } }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class BrowserObservation {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
$destination = 'https://github.com/fankserver/vanguard-galaxy-api/releases'
$releaseTitlePattern = '^Releases.*fankserver/vanguard-galaxy-api(?:\s|$)' # Branding suffix is not stable; exact HTTPS URI below is authoritative.
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$game = $null
try {
    while (!(Test-Path -LiteralPath $request -PathType Leaf)) {
        if (Test-Path -LiteralPath (Join-Path $root 'run-outcome.json')) { throw 'Native run ended before requesting a browser.' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'No explicit browser launch request arrived.' }
        Start-Sleep -Milliseconds 100
    }
    if ((Get-Item -LiteralPath $request).Length -gt 512 -or [IO.File]::ReadAllText($request) -cne "mod-release-browser-v1`n$destination`n") { throw 'Unexpected browser launch request.' }
    $identity = Get-Content -LiteralPath (Join-Path $root 'process.json') -Raw | ConvertFrom-Json
    $game = Get-Process -Id $identity.pid
    $exe = Join-Path $root 'game\VanguardGalaxy.exe'
    if (!$game.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase) -or !$identity.executable.Equals($exe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Game process identity changed.' }
    $browserDeadline = [DateTime]::UtcNow.AddSeconds(40)
    $verified = $false
    while ([DateTime]::UtcNow -lt $browserDeadline -and !$verified) {
        if ($game.HasExited) { throw 'Owned game exited before browser verification.' }
        $window = [BrowserObservation]::GetForegroundWindow()
        $title = New-Object Text.StringBuilder 1024
        $null = [BrowserObservation]::GetWindowText($window, $title, 1024)
        # Never inspect or capture unrelated browser tabs/windows.
        if ($title.ToString() -match $releaseTitlePattern) {
            $element = [Windows.Automation.AutomationElement]::FromHandle($window)
            $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
            foreach ($edit in $element.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
                if ($edit.Current.Name -ne 'Address and search bar') { continue }
                $pattern = $null
                if (!$edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { continue }
                $url = $pattern.Current.Value.TrimEnd('/')
                if ($url -ceq $destination.Substring(8) -and [BrowserObservation]::GetForegroundWindow() -eq $window) {
                    # Chrome may elide the scheme until its address bar is selected. Do not edit or navigate.
                    $addressShell = New-Object -ComObject WScript.Shell
                    $addressShell.SendKeys('^l')
                    Start-Sleep -Milliseconds 150
                    $url = $pattern.Current.Value.TrimEnd('/')
                }
                if ($url -cne $destination -or [BrowserObservation]::GetForegroundWindow() -ne $window) { continue }
                # Close address suggestions before capturing any pixels. Never capture tabs, bookmarks or profile chrome.
                $captureShell = New-Object -ComObject WScript.Shell
                $captureShell.SendKeys('{ESC}')
                Start-Sleep -Milliseconds 150
                $documentCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Document)
                $documents = @($element.FindAll([Windows.Automation.TreeScope]::Descendants, $documentCondition) | Where-Object { !$_.Current.IsOffscreen -and $_.Current.Name -match 'Releases.*fankserver/vanguard-galaxy-api' })
                if ($documents.Count -ne 1) { throw 'A unique public release document was not found.' }
                $document = $documents[0]
                $bounds = $document.Current.BoundingRectangle
                $windowBounds = $element.Current.BoundingRectangle
                # Exclude the website's top account/navigation bar as well as all browser chrome.
                $x = [Math]::Max($bounds.X + 8, $windowBounds.X + 8)
                $y = $bounds.Y + 180
                $width = [Math]::Min($bounds.Right, $windowBounds.Right) - $x - 8
                $height = [Math]::Min($bounds.Bottom, $windowBounds.Bottom) - $y - 8
                if ($width -lt 100 -or $height -lt 100 -or $width -gt 8192 -or $height -gt 8192) { throw 'Unexpected page-content bounds.' }
                $bitmap = New-Object Drawing.Bitmap([int]$width, [int]$height)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $freshUrl = $pattern.Current.Value.TrimEnd('/')
                    if ([BrowserObservation]::GetForegroundWindow() -ne $window -or !$document.Current.BoundingRectangle.Equals($bounds) -or
                        ($freshUrl -cne $destination -and $freshUrl -cne $destination.Substring(8))) { throw 'Browser identity changed before page capture.' }
                    $graphics.CopyFromScreen([int]$x, [int]$y, 0, 0, $bitmap.Size)
                    $bitmap.Save($image, [Drawing.Imaging.ImageFormat]::Png)
                } finally { $graphics.Dispose(); $bitmap.Dispose() }
                $verified = $true; break
            }
        }
        if (!$verified) { Start-Sleep -Milliseconds 200 }
    }
    if (!$verified) { throw 'Default browser did not show the exact public release destination.' }
    $shell = New-Object -ComObject WScript.Shell
    $null = $shell.AppActivate($game.Id)
    Start-Sleep -Milliseconds 300
    $null = [BrowserObservation]::SetForegroundWindow($game.MainWindowHandle)
    if ([BrowserObservation]::GetForegroundWindow() -ne $game.MainWindowHandle) { throw 'Could not restore owned game focus.' }
    $hash = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($receipt + '.tmp'), "PASS`nmod-release-browser-v1`nurl=$destination`nsha256=$hash`n")
    [IO.File]::Move(($receipt + '.tmp'), $receipt)
    Write-Host 'PASS: explicit release action reached the exact default-browser URI; owned game focus restored.'
} finally {
    # The default browser may predate this run. Never close it or kill its process.
    if ($null -ne $game -and !$game.HasExited) { $null = [BrowserObservation]::SetForegroundWindow($game.MainWindowHandle) }
}
