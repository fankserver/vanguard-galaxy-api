. (Join-Path $PSScriptRoot 'qualification-world-paths.ps1')

function Read-WorldIni([string]$Path) {
    $path = Assert-WorldUnlinkedPath $Path
    if ((Get-Item -LiteralPath $path).Length -gt 131072) { throw 'World configuration exceeds limit.' }
    $text = (New-Object Text.UTF8Encoding($false, $true)).GetString([IO.File]::ReadAllBytes($path))
    $entries = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    $section = ''
    foreach ($line in $text.Replace("`r`n", "`n").Split("`n")) {
        $value = $line.Trim()
        if ($value -eq '' -or $value.StartsWith('#')) { continue }
        if ($value -match '^\[([^\]]+)\]$') { $section = $Matches[1].Trim(); continue }
        $split = $value.IndexOf('=')
        if ($section -eq '' -or $split -lt 1) { throw 'Unparseable world configuration.' }
        $key = $section + '/' + $value.Substring(0, $split).Trim()
        if ($entries.ContainsKey($key)) { throw 'Duplicate world configuration entry.' }
        $entries.Add($key, $value.Substring($split + 1).Trim())
    }
    return ,$entries
}

function Assert-WorldConfiguration([string]$Root) {
    $rootPath = Assert-WorldSandboxRoot $Root
    $api = Read-WorldIni (Join-Path $rootPath 'game\BepInEx\config\vgmodapi.cfg')
    if (!$api.ContainsKey('WorldProtection/Enabled') -or $api['WorldProtection/Enabled'] -ine 'true' -or
        !$api.ContainsKey('Persistence/Root') -or ![IO.Path]::IsPathRooted($api['Persistence/Root']) -or
        [IO.Path]::GetFullPath($api['Persistence/Root']).TrimEnd('\') -ine (Join-Path $rootPath 'state')) { throw 'World candidate requires protection and sandbox-local state.' }
    $null = Assert-WorldUnlinkedPath (Join-Path $rootPath 'state')
    $doorstop = Read-WorldIni (Join-Path $rootPath 'game\doorstop_config.ini')
    $required = @{
        'General/enabled'='true'; 'General/target_assembly'='BepInEx\core\BepInEx.Preloader.dll'; 'General/redirect_output_log'='false';
        'General/boot_config_override'=''; 'General/ignore_disable_switch'='false';
        'UnityMono/dll_search_path_override'=''; 'UnityMono/debug_enabled'='false'; 'UnityMono/debug_suspend'='false'
    }
    # An exact key set also excludes differently cased aliases, independent of Doorstop's lookup rules.
    if ($doorstop.Count -ne $required.Count -or @($doorstop.Keys | Where-Object { $_ -cnotin @($required.Keys) }).Count) { throw 'Unexpected Doorstop configuration keys.' }
    foreach ($entry in $required.GetEnumerator()) {
        if (!$doorstop.ContainsKey($entry.Key) -or $doorstop[$entry.Key] -cne $entry.Value) { throw 'Doorstop configuration escapes the inspected launch profile.' }
    }
}
