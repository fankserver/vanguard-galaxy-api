. (Join-Path $PSScriptRoot 'qualification-world-run.ps1')

# Disconnect only inspected resource junctions after the operator establishes cleanup and holds
# the external lease. Keep every regular sandbox/evidence file for private inspection.
function Disconnect-WorldQualificationResources([string]$Root, [string]$GameDirectory, [switch]$ExclusiveLeaseConfirmed) {
    if (!$ExclusiveLeaseConfirmed) { throw 'Already-held exclusive lease confirmation required.' }
    $rootPath = Assert-WorldSandboxRoot $Root
    Assert-NoWorldGameProcess
    $inventory = Get-WorldLaunchInventory $rootPath $GameDirectory
    foreach ($name in @('VanguardGalaxy_Data','MonoBleedingEdge','D3D12')) {
        if (!$inventory.ContainsKey($name)) { continue }
        if (!$inventory[$name].StartsWith('J:', [StringComparison]::Ordinal)) { throw 'Cleanup target is not an inspected resource junction.' }
        # Never use recursive removal: the target installation must remain untouched.
        [IO.Directory]::Delete((Join-Path $rootPath ('game\' + $name)), $false)
    }
}
