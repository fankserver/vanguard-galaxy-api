$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\qualification-world-preservation.ps1')
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action } catch { $failed = $true }; if (!$failed) { throw 'Preservation drift accepted.' } }
$root = Join-Path $env:TEMP ('world-preservation-' + [Guid]::NewGuid().ToString('N'))
$created = $false; $sandboxCreated = $false
$sandbox = Join-Path ([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')) ('VGModAPI-qa-' + (Get-Random -Minimum 1000000000 -Maximum 2000000000))
try {
    $null = New-Item -ItemType Directory $root; $created = $true
    $game = Join-Path $root 'source'; $config = Join-Path $game 'BepInEx\config'
    $null = New-Item -ItemType Directory $config -Force
    $required = @(Get-WorldProductionRoots $game)
    if ($required.Count -ne 3 -or $required -notcontains (Join-Path $game 'BepInEx\plugins') -or
        $required -notcontains (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\Bat Roost Games\VanguardGalaxy\Saves')) { throw 'Required production roots missing.' }
    $settings = Join-Path $config 'vgmodapi.cfg'; $external = Join-Path $root 'external-state'
    [IO.File]::WriteAllText($settings, "[Persistence]`nRoot=$external`n")
    $required = @(Get-WorldProductionRoots $game)
    $null = New-Item -ItemType Directory $sandbox; $sandboxCreated = $true
    Assert-WorldPreservationRoots $required $required $sandbox
    Reject { Assert-WorldPreservationRoots $required $required[0..2] $sandbox }
    $overlap = @($required[0..2]) + @($sandbox)
    Reject { Assert-WorldPreservationRoots $overlap $overlap $sandbox }
    if ($required.Count -ne 4 -or $required -notcontains $external) { throw 'External state omitted.' }
    [IO.File]::WriteAllText($settings, "[Persistence]`nRoot=$(Join-Path $config 'VGModAPI-state')`n")
    if (@(Get-WorldProductionRoots $game).Count -ne 3) { throw 'Nested state duplicated.' }
    [IO.File]::WriteAllText($settings, "[Persistence]`nRoot=relative`n")
    Reject { Get-WorldProductionRoots $game }
    $files = Join-Path $root 'files'; $absent = Join-Path $root 'absent'
    $null = New-Item -ItemType Directory $files
    $file = Join-Path $files 'fixture'; [IO.File]::WriteAllText($file, 'original')
    $roots = @($files, $absent)
    $before = Get-WorldPreservationSnapshot $roots
    Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots)
    [IO.File]::WriteAllText($file, 'changed')
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    [IO.File]::WriteAllText($file, 'original')
    $empty = Join-Path $files 'empty'; $null = New-Item -ItemType Directory $empty
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    Remove-Item $empty
    $null = New-Item -ItemType Directory $absent
    Reject { Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots) }
    Remove-Item $absent
    Reject { Get-WorldPreservationSnapshot @($files, $files.ToUpperInvariant()) }
    Assert-WorldPreservation $before (Get-WorldPreservationSnapshot $roots)
    # Observe only the selected path list here, never the real profile save directory.
    $originalSnapshot = (Get-Command Get-WorldPreservationSnapshot).ScriptBlock
    $script:observations = 0
    function Get-WorldPreservationSnapshot([string[]]$Directories) { $script:observations++; return ,$Directories }
    try {
        [IO.File]::WriteAllText($settings, "[Persistence]`nRoot=$external`n")
        $record = [pscustomobject]@{ root=$sandbox; gameDirectory=$game; preservationRoots=@(Get-WorldProductionRoots $game) }
        $selected = @(Get-WorldRunPreservation $record)
        if ($script:observations -ne 1) { throw 'Composed observation missing.' }
        [IO.File]::WriteAllText($settings, "[Persistence]`nRoot=$(Join-Path $root 'different-state')`n")
        Reject { Get-WorldRunPreservation $record }
        if ($script:observations -ne 1) { throw 'Unapproved root reached snapshot reader.' }
    } finally { Set-Item Function:Get-WorldPreservationSnapshot $originalSnapshot }
    Write-Output 'World preservation tests passed on synthetic directories; no production files read or restored.'
} finally {
    if ($created) { Remove-Item -LiteralPath $root -Recurse -Force }
    if ($sandboxCreated) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
