param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Run')][string]$Action,
    [Parameter(Mandatory=$true)][string]$SandboxRoot,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Vanguard Galaxy',
    [string]$SaveA,
    [string]$SaveB,
    [string]$BuildRoot,
    [int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($SandboxRoot).TrimEnd('\')
$game = Join-Path $root 'game'
$marker = Join-Path $root 'qualification.marker'
$markerText = 'vgmodapi-disposable-sandbox-v1'
function SamePath($a, $b) { return [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') }
function SaveHashes($directories) {
    $result = @{}
    foreach ($directory in $directories) {
        Get-ChildItem -LiteralPath $directory -File | ForEach-Object {
            $result[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $result
}
if (Get-Process VanguardGalaxy -ErrorAction SilentlyContinue) { throw 'A game process is already running. Refusing concurrent qualification.' }
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $root) { throw 'Sandbox root already exists; use a fresh directory.' }
    if (!$SaveA -or !$SaveB -or !$BuildRoot) { throw 'Prepare requires SaveA, SaveB and BuildRoot.' }
    $sources = @((Get-Item -LiteralPath $SaveA), (Get-Item -LiteralPath $SaveB))
    foreach ($source in $sources) { if ($source.Extension -ne '.save') { throw 'Fixtures must be existing .save files.' } }
    $directories = @($sources | ForEach-Object { $_.DirectoryName } | Select-Object -Unique)
    foreach ($protected in @($GameDir) + $directories) {
        $prefix = [IO.Path]::GetFullPath($protected).TrimEnd('\')
        if ((SamePath $root $prefix) -or $root.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Sandbox cannot be inside the installation or original save directory.' }
    }
    New-Item -ItemType Directory -Path $game | Out-Null
    [IO.File]::WriteAllText($marker, $markerText)
    $before = SaveHashes $directories
    @{ directories=$directories; files=$before } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'original-save-hashes.json')
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','UnityCrashHandler64.exe','dstorage.dll','dstoragecore.dll')) {
        $source = Join-Path $GameDir $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $game }
        elseif ($name -in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini')) { throw "Required runtime file missing: $name" }
    }
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
        $source = Join-Path $GameDir $name
        if (Test-Path -LiteralPath $source) { New-Item -ItemType Junction -Path (Join-Path $game $name) -Target $source | Out-Null }
    }
    $bep = Join-Path $game 'BepInEx'
    New-Item -ItemType Directory -Path $bep | Out-Null
    Copy-Item -LiteralPath (Join-Path $GameDir 'BepInEx\core') -Destination $bep -Recurse
    $plugins = Join-Path $bep 'plugins'
    New-Item -ItemType Directory -Path $plugins | Out-Null
    Copy-Item -LiteralPath (Join-Path $BuildRoot 'artifacts\VGModAPI') -Destination $plugins -Recurse
    Copy-Item -LiteralPath (Join-Path $BuildRoot 'tools\QualificationRunner\bin\Release\netstandard2.1\QualificationRunner.dll') -Destination $plugins
    Copy-Item -LiteralPath (Join-Path $BuildRoot 'examples\LifecycleObserver\bin\Release\netstandard2.1\LifecycleObserver.dll') -Destination $plugins
    # Prevent Steam's restart-if-not-launched path; the runner also disables SteamManager for this process.
    [IO.File]::WriteAllText((Join-Path $game 'steam_appid.txt'), '3471800')
    $saves = Join-Path $root 'Saves'
    New-Item -ItemType Directory -Path $saves | Out-Null
    Copy-Item -LiteralPath $sources[0].FullName -Destination (Join-Path $saves 'fixture-a.save')
    Copy-Item -LiteralPath $sources[1].FullName -Destination (Join-Path $saves 'fixture-b.save')
    [IO.File]::WriteAllText((Join-Path $saves 'fixture-future.save'), '{"Version":"9999.0.0","Player":{}}', (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $saves 'fixture-corrupt.save'), 'not a save', (New-Object Text.UTF8Encoding($false)))
    Write-Output "Prepared sandbox: $root"
    exit 0
}
if (!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $markerText) { throw 'Not a marked qualification sandbox.' }
if ((Test-Path -LiteralPath (Join-Path $root 'result.txt')) -or (Test-Path -LiteralPath (Join-Path $root 'process.json'))) { throw 'Sandbox already ran. Prepare a fresh directory instead of reusing stale evidence.' }
$exe = Join-Path $game 'VanguardGalaxy.exe'
$process = $null
try {
    # Inspected FSEShimBootstrap recognizes this marker; avoid registry mutation and a child relaunch.
    $arguments = @('--fse-shim-applied','-screen-fullscreen','0','-screen-width','1024','-screen-height','768','-logFile', ('"' + (Join-Path $root 'Player.log') + '"'), '--vgmodapi-qualification-root', ('"' + $root + '"'))
    $process = Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $arguments -PassThru
    @{ pid=$process.Id; executable=$exe } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'process.json')
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Owned game process timed out.' }
}
finally {
    if ($null -ne $process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    # Also stop a bootstrap-relaunched child, but only at this fresh sandbox's exact executable path.
    Get-Process VanguardGalaxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe } | ForEach-Object { Stop-Process -InputObject $_ }
    $manifest = Get-Content -LiteralPath (Join-Path $root 'original-save-hashes.json') -Raw | ConvertFrom-Json
    $after = SaveHashes $manifest.directories
    $beforeKeys = @($manifest.files.PSObject.Properties.Name)
    if ($beforeKeys.Count -ne $after.Count) { throw 'Original save directory file set changed during qualification.' }
    foreach ($property in $manifest.files.PSObject.Properties) {
        if ($after[$property.Name] -ne $property.Value) { throw "Original save changed: $($property.Name)" }
    }
    [IO.File]::WriteAllText((Join-Path $root 'original-saves-unchanged.txt'), 'PASS')
}
$result = Join-Path $root 'result.txt'
if (!(Test-Path -LiteralPath $result)) { throw 'Game exited without a qualification result; inspect sandbox logs.' }
Get-Content -LiteralPath $result
if ((Get-Content -LiteralPath $result -TotalCount 1) -ne 'PASS') { throw 'Qualification failed; inspect sandbox logs.' }
