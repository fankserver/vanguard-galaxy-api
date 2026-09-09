$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-preflight.ps1')
$root = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Invalid world inputs accepted.' } }
$created = $false
$source = Join-Path $env:TEMP ('world-approved-source-' + [Guid]::NewGuid().ToString('N'))
try {
    if (Test-Path $root) { throw 'Synthetic test root exists.' }
    $null = New-Item -ItemType Directory $root; $created = $true
    $plugins = Join-Path $root 'game\BepInEx\plugins'
    foreach ($dir in @($plugins, (Join-Path $root 'Saves'), (Join-Path $root 'state'), (Join-Path $source 'VanguardGalaxy_Data\Managed'), (Join-Path $source 'MonoBleedingEdge'))) { $null = New-Item -ItemType Directory $dir -Force }
    foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge')) { $null = New-Item -ItemType Junction -Path (Join-Path $root ('game\' + $name)) -Target (Join-Path $source $name) }
    [IO.File]::WriteAllText((Join-Path $root 'Saves\fixture-a.save'), 'synthetic fixture')
    [IO.File]::WriteAllText((Join-Path $root 'qualification.marker'), 'vgmodapi-disposable-sandbox-v1')
    [IO.File]::WriteAllText((Join-Path $root 'scenario.txt'), 'Full')
    [IO.File]::WriteAllText((Join-Path $root 'world.enabled'), 'world-empty-v1')
    $null = New-Item -ItemType Directory (Join-Path $root 'game\BepInEx\config')
    $config = Join-Path $root 'game\BepInEx\config\vgmodapi.cfg'
    $configText = "[WorldProtection]`nEnabled = true`n[Persistence]`nRoot = $(Join-Path $root 'state')`n"
    [IO.File]::WriteAllText($config, $configText)
    $doorstop = Join-Path $root 'game\doorstop_config.ini'
    $doorstopText = "[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Preloader.dll`nredirect_output_log=false`nboot_config_override=`nignore_disable_switch=false`n[UnityMono]`ndll_search_path_override=`ndebug_enabled=false`ndebug_suspend=false`n"
    [IO.File]::WriteAllText($doorstop, $doorstopText)
    Assert-WorldConfiguration $root
    [IO.File]::WriteAllText($config, $configText.Replace('[Persistence]', '[persistence]'))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($config, ($configText + "Root=duplicate`n"))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($config, $configText.Replace((Join-Path $root 'state'), $source))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($config, $configText)
    [IO.File]::WriteAllText($doorstop, $doorstopText.Replace('target_assembly=BepInEx\core\BepInEx.Preloader.dll', 'target_assembly=C:\outside.dll'))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($doorstop, ($doorstopText + "[General]`nTARGET_ASSEMBLY=C:\outside.dll`n"))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($doorstop, ($doorstopText + "[GENERAL]`ntarget_assembly=C:\outside.dll`n"))
    Reject { Assert-WorldConfiguration $root }
    [IO.File]::WriteAllText($doorstop, $doorstopText)
    Assert-WorldConfiguration $root
    $game = Join-Path $root 'game\VanguardGalaxy_Data\Managed\Assembly-CSharp.dll'
    [IO.File]::WriteAllText($game, 'synthetic game hash fixture; not executable')
    $names = @('VGModAPI.dll', 'VGModAPI.Core.dll', 'VGModAPI.Abstractions.dll', 'QualificationGuard.dll', 'WorldAuthorA.dll', 'WorldAuthorB.dll', 'QualificationRunner.dll')
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $plugins $name), ('synthetic-' + $name)) }
    $run = [Guid]::NewGuid()
    $rows = @('vgmodapi-world-empty-v1', $run.ToString('D'), ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + 600).ToString(), (Get-FileHash $game -Algorithm SHA256).Hash.ToLowerInvariant())
    foreach ($name in $names) { $rows += (Get-FileHash (Join-Path $plugins $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest = Join-Path $root 'world-qualification.authorization'
    [IO.File]::WriteAllLines($manifest, $rows)
    $observed = Assert-WorldQualificationInputs $root $run
    $head = 'a' * 40; $approval = Join-Path $root 'approved.json'
    $record = @{ schema='world-empty-run-v1'; root=$root; gameDirectory=$source; runId=$run.ToString('D'); phase='create'; reviewedHead=$head; authorizationSha256=$observed.authorizationSha256;
        gameInventory=(Get-WorldLaunchInventory $root $source); saveInventory=(Get-WorldDataInventory (Join-Path $root 'Saves')); stateInventory=(Get-WorldDataInventory (Join-Path $root 'state')) }
    [IO.File]::WriteAllText($approval, ($record | ConvertTo-Json -Depth 5))
    $approvalHash = (Get-FileHash $approval -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash
    $extraState = Join-Path $root 'state\unexpected'
    [IO.File]::WriteAllText($extraState, 'changed')
    Reject { Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash }
    Remove-Item $extraState
    $conflict = Join-Path $root 'bars.enabled'; [IO.File]::WriteAllText($conflict, 'conflict')
    Reject { Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash }
    Remove-Item $conflict
    $validChange = $rows.Clone(); $validChange[2] = ([long]$validChange[2] + 1).ToString()
    [IO.File]::WriteAllLines($manifest, $validChange)
    $null = Assert-WorldQualificationInputs $root $run
    Reject { Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash }
    [IO.File]::WriteAllLines($manifest, $rows)
    $extraInput = Join-Path $root 'game\unapproved.dll'; [IO.File]::WriteAllText($extraInput, 'unapproved')
    Reject { Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash }
    Remove-Item $extraInput
    $null = Assert-WorldRunPreflight $root $run 'create' $head $approval $approvalHash
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
} finally {
    if ($created) {
        foreach ($name in @('VanguardGalaxy_Data', 'MonoBleedingEdge')) {
            $link = Join-Path $root ('game\' + $name)
            $entry = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
            if ($entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($link, $false) }
        }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
    if (Test-Path $source) { Remove-Item -LiteralPath $source -Recurse -Force }
}
