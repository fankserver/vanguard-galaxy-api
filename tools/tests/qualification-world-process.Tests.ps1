$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-process.ps1')
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
$created = $false; $old = @{}
try {
    if (Test-Path $root) { throw 'Test root exists.' }
    $null = New-Item -ItemType Directory $root; $created = $true
    $null = New-Item -ItemType Directory (Join-Path $root 'game')
    $null = New-Item -ItemType Directory (Join-Path $root 'temp')
    [IO.File]::WriteAllText((Join-Path $root 'game\VanguardGalaxy.exe'), 'not executable; launch description fixture only')
    foreach ($key in @('DOORSTOP_ENABLE', 'MONO_ENV_OPTIONS', 'SteamAppId', 'PATH')) {
        $old[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, 'inherited-poison', 'Process')
    }
    $run = [Guid]::NewGuid()
    foreach ($phase in @('create','cold')) {
        $info = New-WorldProcessStartInfo $root $run $phase
        if ($info.UseShellExecute -or $info.FileName -cne (Join-Path $root 'game\VanguardGalaxy.exe') -or $info.WorkingDirectory -cne (Join-Path $root 'game')) { throw 'Unexpected process target.' }
        if ($info.EnvironmentVariables.Count -ne 10 -or $info.EnvironmentVariables['PATH'] -cne [Environment]::GetFolderPath('System')) { throw 'Unexpected child environment.' }
        foreach ($key in @('DOORSTOP_ENABLE', 'MONO_ENV_OPTIONS', 'SteamAppId')) {
            if ($info.EnvironmentVariables.ContainsKey($key)) { throw 'Inherited loader override leaked.' }
        }
        if ($info.EnvironmentVariables['TEMP'] -cne (Join-Path $root 'temp') -or $info.EnvironmentVariables['TMP'] -cne (Join-Path $root 'temp')) { throw 'Temporary output escaped sandbox.' }
        $expected = if ($phase -eq 'create') { '--vgmodapi-world-only' } else { '--vgmodapi-world-cold' }
        if (!$info.Arguments.EndsWith(' --vgmodapi-world-run ' + $run.ToString('D') + ' ' + $expected)) { throw 'Wrong world process phase/run.' }
    }
    Write-Output 'World launch-description tests passed; no process started.'
} finally {
    foreach ($key in $old.Keys) { [Environment]::SetEnvironmentVariable($key, $old[$key], 'Process') }
    if ($created) { Remove-Item -LiteralPath $root -Recurse -Force }
}
