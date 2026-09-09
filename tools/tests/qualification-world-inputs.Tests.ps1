$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-inputs.ps1')
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Invalid world inputs accepted.' } }
$created = $false
try {
    if (Test-Path $root) { throw 'Synthetic test root exists.' }
    $null = New-Item -ItemType Directory $root; $created = $true
    $plugins = Join-Path $root 'game\BepInEx\plugins'
    foreach ($dir in @($plugins, (Join-Path $root 'Saves'), (Join-Path $root 'game\VanguardGalaxy_Data\Managed'))) { $null = New-Item -ItemType Directory $dir -Force }
    [IO.File]::WriteAllText((Join-Path $root 'qualification.marker'), 'vgmodapi-disposable-sandbox-v1')
    [IO.File]::WriteAllText((Join-Path $root 'scenario.txt'), 'Full')
    [IO.File]::WriteAllText((Join-Path $root 'world.enabled'), 'world-empty-v1')
    $game = Join-Path $root 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll'
    [IO.File]::WriteAllText($game, 'synthetic game hash fixture; not executable')
    $names = @('VGModAPI.dll', 'VGModAPI.Core.dll', 'VGModAPI.Abstractions.dll', 'QualificationGuard.dll', 'WorldAuthorA.dll', 'WorldAuthorB.dll', 'QualificationRunner.dll')
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $plugins $name), ('synthetic-' + $name)) }
    $run = [Guid]::NewGuid()
    $rows = @('vgmodapi-world-empty-v1', $run.ToString('D'), ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + 600).ToString(), (Get-FileHash $game -Algorithm SHA256).Hash.ToLowerInvariant())
    foreach ($name in $names) { $rows += (Get-FileHash (Join-Path $plugins $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest = Join-Path $root 'world-qualification.authorization'
    [IO.File]::WriteAllLines($manifest, $rows)
    $null = Assert-WorldQualificationInputs $root $run
    Reject { Assert-WorldQualificationInputs $root ([Guid]::NewGuid()) }
    foreach ($expiry in @('0', ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + 20000).ToString())) {
        $bad = $rows.Clone(); $bad[2] = $expiry; [IO.File]::WriteAllLines($manifest, $bad)
        Reject { Assert-WorldQualificationInputs $root $run }
    }
    [IO.File]::WriteAllLines($manifest, $rows)
    [IO.File]::WriteAllText((Join-Path $plugins 'extra.dll'), 'extra')
    Reject { Assert-WorldQualificationInputs $root $run }
    Remove-Item (Join-Path $plugins 'extra.dll')
    [IO.File]::WriteAllText((Join-Path $plugins 'WorldAuthorA.dll'), 'changed')
    Reject { Assert-WorldQualificationInputs $root $run }
    [IO.File]::WriteAllText((Join-Path $plugins 'WorldAuthorA.dll'), 'synthetic-WorldAuthorA.dll')
    $null = Assert-WorldQualificationInputs $root $run
    [IO.File]::WriteAllBytes($manifest, [byte[]]@(255, 254, 255))
    Reject { Assert-WorldQualificationInputs $root $run }
    Write-Output 'World input hash checks passed with synthetic files; no executable or game launched.'
} finally { if ($created) { Remove-Item -LiteralPath $root -Recurse -Force } }
