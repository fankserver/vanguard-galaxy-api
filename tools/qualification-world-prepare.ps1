. (Join-Path $PSScriptRoot 'qualification-world-preflight.ps1')

# Preparation only: inputs must already be selected by the operator. Does not issue approval,
# create authorization, launch a game, copy live profiles wholesale, or clean up partial failures.
function New-WorldQualificationSandbox([string]$Root, [string]$GameDirectory, [string]$PluginDirectory,
    [string]$SaveA, [string]$SaveB, [string]$AuthorizationPath, [Guid]$RunId) {
    $rootPath = Assert-WorldSandboxRoot $Root $false
    if (Test-Path -LiteralPath $rootPath) { throw 'World preparation requires a new sandbox.' }
    $sourceGame = (Assert-WorldUnlinkedPath $GameDirectory).TrimEnd('\')
    $sourcePlugins = Assert-WorldUnlinkedPath $PluginDirectory
    $authorization = Assert-WorldUnlinkedPath $AuthorizationPath
    $a = Assert-WorldUnlinkedPath $SaveA; $b = Assert-WorldUnlinkedPath $SaveB
    $core = Assert-WorldUnlinkedPath (Join-Path $sourceGame 'BepInEx\core')
    $coreInventory = Get-WorldDataInventory $core
    $game = Join-Path $rootPath 'game'; $plugins = Join-Path $game 'BepInEx\plugins'
    foreach ($path in @($sourceGame,$sourcePlugins,$authorization,$a,$b)) {
        if ($path -ieq $rootPath -or $path.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or $rootPath.StartsWith($path.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Preparation source is inside destination.' }
    }
    $null = New-Item -ItemType Directory $rootPath
    foreach ($path in @($plugins,(Join-Path $game 'BepInEx\config'),(Join-Path $rootPath 'Saves'),(Join-Path $rootPath 'state'),(Join-Path $rootPath 'temp'))) {
        $null = New-Item -ItemType Directory $path -Force
    }
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','UnityCrashHandler64.exe','dstorage.dll','dstoragecore.dll')) {
        $source = Assert-WorldUnlinkedPath (Join-Path $sourceGame $name) $false
        if (Test-Path -LiteralPath $source -PathType Leaf) { [IO.File]::Copy($source, (Join-Path $game $name), $false) }
        elseif ($name -in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll')) { throw 'Required runtime input missing.' }
    }
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
        $source = Assert-WorldUnlinkedPath (Join-Path $sourceGame $name) $false
        if (Test-Path -LiteralPath $source -PathType Container) { $null = New-Item -ItemType Junction -Path (Join-Path $game $name) -Target $source }
        elseif ($name -ne 'D3D12') { throw 'Required resource directory missing.' }
    }
    Copy-Item -LiteralPath $core -Destination (Join-Path $game 'BepInEx') -Recurse -ErrorAction Stop
    $copiedCore = Get-WorldDataInventory (Join-Path $game 'BepInEx\core')
    if ($copiedCore.Count -ne $coreInventory.Count) { throw 'Copied loader inventory differs.' }
    foreach ($entry in $coreInventory.GetEnumerator()) { if (!$copiedCore.ContainsKey($entry.Key) -or $copiedCore[$entry.Key] -cne $entry.Value) { throw 'Copied loader changed.' } }
    foreach ($name in @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','QualificationGuard.dll','WorldAuthorA.dll','WorldAuthorB.dll','QualificationRunner.dll')) {
        $source = Assert-WorldUnlinkedPath (Join-Path $sourcePlugins $name)
        [IO.File]::Copy($source, (Join-Path $plugins $name), $false)
    }
    [IO.File]::Copy($a, (Join-Path $rootPath 'Saves\fixture-a.save'), $false)
    [IO.File]::Copy($b, (Join-Path $rootPath 'Saves\fixture-b.save'), $false)
    [IO.File]::Copy($authorization, (Join-Path $rootPath 'world-qualification.authorization'), $false)
    [IO.File]::WriteAllText((Join-Path $rootPath 'qualification.marker'), 'vgmodapi-disposable-sandbox-v1')
    [IO.File]::WriteAllText((Join-Path $rootPath 'scenario.txt'), 'Full')
    [IO.File]::WriteAllText((Join-Path $rootPath 'world.enabled'), 'world-empty-v1')
    [IO.File]::WriteAllText((Join-Path $rootPath 'original-save-directory.txt'), (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\Bat Roost Games\VanguardGalaxy\Saves'))
    [IO.File]::WriteAllText((Join-Path $game 'BepInEx\config\vgmodapi.cfg'), "[WorldProtection]`nEnabled=true`n[Persistence]`nRoot=$(Join-Path $rootPath 'state')`n")
    [IO.File]::WriteAllText((Join-Path $game 'doorstop_config.ini'), "[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Preloader.dll`nredirect_output_log=false`nboot_config_override=`nignore_disable_switch=false`n[UnityMono]`ndll_search_path_override=`ndebug_enabled=false`ndebug_suspend=false`n")
    $null = Assert-WorldQualificationInputs $rootPath $RunId
    Assert-WorldConfiguration $rootPath
    Assert-WorldResourceTargets $rootPath $sourceGame
    return $rootPath
}
