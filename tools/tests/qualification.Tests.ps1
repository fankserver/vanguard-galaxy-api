# Windows-only, fake files only. Does not launch Unity or touch real game/profile data.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\qualification.ps1'
. (Join-Path $PSScriptRoot '..\qualification-inputs.ps1')
# Hosted Windows TEMP can use an 8.3 alias; match FileInfo's canonical full paths.
$work = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('vgmodapi-harness-test-' + [Guid]::NewGuid().ToString('N'))))
$fakeGame = Join-Path $work 'installed'
$build = Join-Path $work 'build'
$original = Join-Path $work 'original'
$fixtures = Join-Path $work 'fixtures'
$sandbox = Join-Path $work 'sandbox'
$sandboxes = @($sandbox)
function Put($relative, $text) {
    $path = Join-Path $work $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}
function Assert($condition, $message) { if (!$condition) { throw $message } }
try {
    foreach ($name in @('VanguardGalaxy.exe','UnityPlayer.dll','winhttp.dll','BepInEx\core\BepInEx.Preloader.dll')) { Put "installed\$name" 'fake-not-executable' }
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) { Put "installed\$name\sentinel.txt" 'keep' }
    Put 'installed\doorstop_config.ini' "[General]`ntarget_assembly=C:\outside\BepInEx.Preloader.dll"
    foreach ($name in @('VGModAPI.dll','VGModAPI.Core.dll','VGModAPI.Abstractions.dll','unexpected.dll')) { Put "build\artifacts\VGModAPI\$name" 'fake-assembly' }
    Put 'build\tools\QualificationGuard\bin\Release\netstandard2.1\QualificationGuard.dll' 'fake-guard'
    Put 'build\tools\QualificationRunner\bin\Release\netstandard2.1\QualificationRunner.dll' 'fake-runner'
    Put 'build\examples\LifecycleObserver\bin\Release\netstandard2.1\LifecycleObserver.dll' 'fake-observer'
    Put 'original\real.save' 'original'
    Put 'fixtures\a.save' 'fixture-a'
    Put 'fixtures\b.save' 'fixture-b'
    $options = @{ GameDir=$fakeGame; OriginalSaveDir=$original; SaveA=(Join-Path $fixtures 'a.save'); SaveB=(Join-Path $fixtures 'b.save'); BuildRoot=$build; BuildRevision='fixture-test' }
    & $script -Action Prepare -SandboxRoot $sandbox @options
    $future = Get-Content (Join-Path $sandbox 'Saves\fixture-future.save') -Raw | ConvertFrom-Json
    Assert ($future.Version -eq '99.0.0.0') 'Future fixture must use valid two-digit-or-shorter version segments.'
    $manifest = Get-Content (Join-Path $sandbox 'original-save-hashes.json') -Raw | ConvertFrom-Json
    Assert (@($manifest.files.PSObject.Properties.Name) -contains (Join-Path $original 'real.save')) 'Real save directory was not protected when fixtures live elsewhere.'
    Assert (@($manifest.files.PSObject.Properties).Count -eq 3) 'Expected original and both fixture hashes.'
    Assert (!(Test-Path (Join-Path $sandbox 'game\BepInEx\plugins\unexpected.dll'))) 'Package allowlist failed.'
    $config = Get-Content (Join-Path $sandbox 'game\doorstop_config.ini') -Raw
    Assert ($config.Contains("[General]`nenabled=true`ntarget_assembly=BepInEx\core\BepInEx.Preloader.dll") -and !$config.Contains('C:\outside')) 'Doorstop 4 preloader config is not enabled and sandbox-relative.'
    Assert ($config.Contains('[UnityMono]') -and !$config.Contains('[UnityDoorstop]') -and !$config.Contains('targetAssembly=')) 'Legacy Doorstop keys must not replace the inspected format.'
    $provenance = Get-Content (Join-Path $sandbox 'build-provenance.json') -Raw | ConvertFrom-Json
    Assert (@($provenance.plugins.PSObject.Properties).Count -eq 6) 'Missing plugin provenance.'
    foreach ($mode in @('MissingApi','UnavailableApi')) {
        $other = Join-Path $work $mode
        $sandboxes += $other
        & $script -Action Prepare -SandboxRoot $other -Scenario $mode @options
        $p = Get-Content (Join-Path $other 'build-provenance.json') -Raw | ConvertFrom-Json
        $expected = if ($mode -eq 'MissingApi') { 1 } else { 4 }
        Assert (@($p.plugins.PSObject.Properties).Count -eq $expected) 'Wrong negative-scenario plugin set.'
        Assert ($p.scenario -eq $mode) 'Scenario provenance missing.'
        Assert (!(Test-Path (Join-Path $other 'game\BepInEx\plugins\QualificationRunner.dll'))) 'Negative scenario copied API-dependent runner.'
        Assert (Test-Path (Join-Path $other 'game\BepInEx\plugins\QualificationGuard.dll')) 'Independent guard missing.'
        & $script -Action Cleanup -SandboxRoot $other
    }
    $null = Assert-QualificationInputs $sandbox
    foreach ($mutation in @('extra-file','extra-directory','changed-hash','changed-scenario')) {
        $extra = Join-Path $sandbox 'game\BepInEx\plugins\extra.dll'
        $dll = Join-Path $sandbox 'game\BepInEx\plugins\VGModAPI.dll'
        $modeFile = Join-Path $sandbox 'scenario.txt'
        switch ($mutation) {
            'extra-file' { [IO.File]::WriteAllText($extra, 'extra') }
            'extra-directory' { [IO.Directory]::CreateDirectory($extra) | Out-Null }
            'changed-hash' { [IO.File]::WriteAllText($dll, 'changed') }
            'changed-scenario' { [IO.File]::WriteAllText($modeFile, 'MissingApi') }
        }
        $rejected = $false
        try { $null = Assert-QualificationInputs $sandbox } catch { $rejected = $true }
        Assert $rejected "Prepared input mutation accepted: $mutation"
        if (Test-Path -LiteralPath $extra) { Remove-Item -LiteralPath $extra -Force }
        [IO.File]::WriteAllText($dll, 'fake-assembly')
        [IO.File]::WriteAllText($modeFile, 'Full')
    }
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot $sandbox @options } catch { $rejected = $true }
    Assert $rejected 'Reused sandbox was accepted.'
    $rejected = $false
    try { & $script -Action Prepare -SandboxRoot (Join-Path $original 'unsafe') @options } catch { $rejected = $true }
    Assert $rejected 'Sandbox inside protected data was accepted.'
    & $script -Action Cleanup -SandboxRoot $sandbox
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
        Assert (!(Test-Path (Join-Path $sandbox "game\$name"))) 'Junction survived Cleanup.'
        Assert ((Get-Content (Join-Path $fakeGame "$name\sentinel.txt")) -eq 'keep') 'Cleanup modified the junction target.'
    }
    Assert ((Get-Content (Join-Path $original 'real.save')) -eq 'original') 'Original fake save changed.'
    Write-Output 'PASS: manifest coverage, plugin allowlist, local preloader, provenance, reuse/path refusal, non-recursive cleanup.'
}
finally {
    # Even on assertion failure, unlink before deleting this wholly synthetic fixture tree.
    foreach ($root in $sandboxes) {
        foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
            $path = Join-Path $root "game\$name"
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($path, $false) }
        }
    }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
