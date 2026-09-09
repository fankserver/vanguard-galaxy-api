. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

# Constructs a launch description only. Approval, preservation, lease and lifetime controls
# must be established by the caller before starting this process.
function New-WorldProcessStartInfo([string]$Root, [Guid]$RunId, [ValidateSet('create','cold')][string]$Phase) {
    $rootPath = Assert-WorldSandboxRoot $Root
    if ($RunId -eq [Guid]::Empty -or $rootPath.Contains('"') -or $rootPath.Contains("`n") -or $rootPath.Contains("`r")) { throw 'Invalid world process identity/path.' }
    $game = Assert-WorldUnlinkedPath (Join-Path $rootPath 'game')
    $exe = Assert-WorldUnlinkedPath (Join-Path $game 'VanguardGalaxy.exe')
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Missing world game executable.' }
    $temporary = Assert-WorldUnlinkedPath (Join-Path $rootPath 'temp')
    if (!(Test-Path -LiteralPath $temporary -PathType Container)) { throw 'World process temporary directory missing.' }
    $log = Assert-WorldUnlinkedPath (Join-Path $rootPath 'Player.log') $false
    if (Test-Path -LiteralPath $log) { throw 'World log output must be fresh; retain prior phase evidence before another run.' }
    $windows = [Environment]::GetFolderPath('Windows')
    $system = [Environment]::GetFolderPath('System')
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe; $info.WorkingDirectory = $game; $info.UseShellExecute = $false
    $flag = if ($Phase -eq 'create') { '--vgmodapi-world-only' } else { '--vgmodapi-world-cold' }
    $info.Arguments = '--fse-shim-applied -screen-fullscreen 0 -logFile "' + $log + '" --vgmodapi-qualification-root "' + $rootPath + '" --vgmodapi-world-run ' + $RunId.ToString('D') + ' ' + $flag
    # Do not inherit Doorstop/Mono/BepInEx/Steam or arbitrary PATH overrides.
    $info.EnvironmentVariables.Clear()
    $environment = @{
        SystemRoot=$windows; WINDIR=$windows; SystemDrive=[IO.Path]::GetPathRoot($windows).TrimEnd('\');
        ComSpec=(Join-Path $system 'cmd.exe'); PATH=$system;
        USERPROFILE=[Environment]::GetFolderPath('UserProfile');
        APPDATA=[Environment]::GetFolderPath('ApplicationData'); LOCALAPPDATA=[Environment]::GetFolderPath('LocalApplicationData');
        TEMP=$temporary; TMP=$temporary
    }
    foreach ($entry in $environment.GetEnumerator()) {
        if ([string]::IsNullOrEmpty($entry.Value)) { throw 'Required operating-system folder unavailable.' }
        $info.EnvironmentVariables[$entry.Key] = $entry.Value
    }
    return $info
}

function Get-WorldProcessDescription([Diagnostics.ProcessStartInfo]$Info) {
    $environment = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($key in $Info.EnvironmentVariables.Keys) { $environment.Add($key, $Info.EnvironmentVariables[$key]) }
    return @{ fileName=$Info.FileName; workingDirectory=$Info.WorkingDirectory; arguments=$Info.Arguments; environment=$environment }
}

function Assert-WorldProcessDescription([Diagnostics.ProcessStartInfo]$Info, $Expected) {
    $actual = Get-WorldProcessDescription $Info
    foreach ($name in @('fileName','workingDirectory','arguments')) {
        if ($Expected.$name -cne $actual[$name]) { throw 'World process differs from approved launch description.' }
    }
    $entries = @($Expected.environment.PSObject.Properties)
    if ($entries.Count -ne $actual.environment.Count) { throw 'Approved world environment differs.' }
    foreach ($entry in $entries) {
        if (!$actual.environment.ContainsKey($entry.Name) -or $actual.environment[$entry.Name] -cne $entry.Value) { throw 'World environment differs from approved launch description.' }
    }
}
