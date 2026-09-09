$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-prepare.ps1')
$source = Join-Path $env:TEMP ('world-prepare-source-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$sourceCreated=$false; $attempted=$false
try {
    $null = New-Item -ItemType Directory $source; $sourceCreated=$true
    $game=Join-Path $source 'game'; $plugins=Join-Path $source 'plugins'
    foreach ($path in @($plugins,(Join-Path $game 'BepInEx\core'),(Join-Path $game 'VanguardGalaxy_Data\Managed'),(Join-Path $game 'MonoBleedingEdge'))) { $null = New-Item -ItemType Directory $path -Force }
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','BepInEx\core\BepInEx.Preloader.dll','VanguardGalaxy_Data\Managed\Assembly-CSharp.dll')) { [IO.File]::WriteAllText((Join-Path $game $name), 'non-executable synthetic input') }
    $names=@('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','QualificationGuard.dll','WorldAuthorA.dll','WorldAuthorB.dll','QualificationRunner.dll')
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $plugins $name), $name) }
    $a=Join-Path $source 'a.save'; $b=Join-Path $source 'b.save'
    [IO.File]::WriteAllText($a, 'fixture A'); [IO.File]::WriteAllText($b, 'fixture B')
    $run=[Guid]::NewGuid(); $authorization=Join-Path $source 'authorization'
    $rows=@('vgmodapi-world-empty-v1',$run.ToString('D'),([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+600).ToString(),(Get-FileHash (Join-Path $game 'VanguardGalaxy_Data\Managed\Assembly-CSharp.dll') -Algorithm SHA256).Hash.ToLowerInvariant())
    foreach ($name in $names) { $rows += (Get-FileHash (Join-Path $plugins $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllLines($authorization, $rows)
    if (Test-Path $root) { throw 'Synthetic destination already exists.' }
    $attempted=$true
    $prepared=New-WorldQualificationSandbox $root $game $plugins $a $b $authorization $run
    if ($prepared -cne $root -or [IO.File]::ReadAllText((Join-Path $root 'Saves\fixture-a.save')) -cne 'fixture A') { throw 'Wrong prepared fixture.' }
    $failed=$false; try { New-WorldQualificationSandbox $root $game $plugins $a $b $authorization $run } catch { $failed=$true }
    if (!$failed) { throw 'Existing sandbox overwritten.' }
    Write-Output 'World preparation tests passed using non-executable synthetic files only.'
} finally {
    if ($attempted -and (Test-Path $root)) {
        foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
            $link=Join-Path $root ('game\'+$name); $entry=Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
            if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($link, $false) }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if ($sourceCreated) { Remove-Item -LiteralPath $source -Recurse -Force }
}
