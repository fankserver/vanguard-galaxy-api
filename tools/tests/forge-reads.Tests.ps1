$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../qualification-profile.ps1')
. (Join-Path $PSScriptRoot '../qualification-inputs.ps1')
function Reject($action) { $rejected = $false; try { & $action } catch { $rejected = $true }; if (!$rejected) { throw 'Invalid Forge evidence accepted.' } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vg-forge-synthetic-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $root 'game/BepInEx/config') -Force | Out-Null
try {
    $p = [pscustomobject]@{ forgeReadProbe=$true; scenario='Full'; modMenuProbe=$false }
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-reads.enabled'), 'forge-reads-v1')
    $config = Join-Path $root 'game/BepInEx/config/vgmodapi.cfg'
    [IO.File]::WriteAllText($config, "[Recipes]`r`n# generated comment`r`nEnabled = true`r`n")
    Assert-ForgeReadSelection $root $p
    $p.modMenuProbe = $true; Reject { Assert-ForgeReadSelection $root $p }; $p.modMenuProbe = $false
    $p.forgeReadProbe = 'true'; Reject { Assert-ForgeReadSelection $root $p }; $p.forgeReadProbe = $true
    [IO.File]::WriteAllText($config, "[Recipes]`r`nEnabled = false`r`n[Other]`r`nEnabled = true`r`n")
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText($config, "[Recipes]`r`nEnabled = true`r`n")
    Reject { Assert-ForgeReadReceipt $root $p }
    @{timedOut=$false;killed=$false;exitCode=0} | ConvertTo-Json | Set-Content (Join-Path $root 'run-outcome.json')
    $facts = Join-Path $root 'forge-reads.txt'; $receipt = Join-Path $root 'forge-reads.receipt'
    [IO.File]::WriteAllLines($facts, @('PASS','forge-reads-v1','catalog=2','quotes=1','restored=0'))
    $hash = (Get-FileHash $facts -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllLines($receipt, @('PASS','forge-reads-v1',"sha256=$hash"))
    Assert-ForgeReadReceipt $root $p
    [IO.File]::AppendAllText($facts, 'tampered'); Reject { Assert-ForgeReadReceipt $root $p }
    [IO.File]::WriteAllLines($facts, @('PASS','forge-reads-v1','catalog=2','quotes=1','restored=0'))
    $p | Add-Member forgeCommandProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-commands.enabled'), 'forge-commands-v1')
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::AppendAllText($config, "CommandsEnabled = true`r`n")
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgeCommandReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-commands.txt'), @('PASS','forge-commands-v1','settings-replay-restored','forge-queue-cancel-replay'))
    Assert-ForgeCommandReceipt $root $p
    $p.forgeCommandProbe = $false; Reject { Assert-ForgeReadSelection $root $p }
    $p.forgeCommandProbe = $true
    $p | Add-Member forgePersistenceProbe $true
    Reject { Assert-ForgeReadSelection $root $p }
    [IO.File]::WriteAllText((Join-Path $root 'forge-persistence.enabled'), 'forge-persistence-v1')
    Assert-ForgeReadSelection $root $p
    Reject { Assert-ForgePersistenceReceipt $root $p }
    [IO.File]::WriteAllLines((Join-Path $root 'forge-persistence.txt'), @('PASS','forge-persistence-v1','paused-jobs-roundtrip-save-as-slot-switch'))
    Assert-ForgePersistenceReceipt $root $p
    $p.forgeCommandProbe = $false; Reject { Assert-ForgeReadSelection $root $p }
    'PASS Forge read/command/persistence selection and receipt tests'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
