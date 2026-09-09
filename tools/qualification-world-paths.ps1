# Path prerequisites only; callers still need exclusive filesystem control and run authorization.
function Assert-WorldUnlinkedPath([string]$Path, [bool]$MustExist = $true) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($MustExist -and !(Test-Path -LiteralPath $full)) { throw 'Required world path is missing.' }
    $current = $full
    while ($current) {
        $entry = $null
        try { $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { }
        if ($null -ne $entry -and ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked world path or ancestor refused.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Assert-WorldSandboxRoot([string]$Root, [bool]$MustExist = $true) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $parent = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')
    $name = [IO.Path]::GetFileName($full)
    $number = 0
    if ([IO.Path]::GetDirectoryName($full) -ine $parent -or $name -cnotmatch '^VGModAPI-qa-[0-9]+$' -or
        ![int]::TryParse($name.Substring(12), [ref]$number) -or $number -le 0) { throw 'World sandbox must be a direct numbered local Temp directory.' }
    $null = Assert-WorldUnlinkedPath $full $MustExist
    if (Test-Path -LiteralPath $full) {
        if (!(Get-Item -LiteralPath $full -Force -ErrorAction Stop).PSIsContainer) { throw 'World sandbox root must be a directory.' }
    }
    return $full
}

function Assert-WorldReceiptPaths([string]$Root, [ValidateSet('create','cold')][string]$Phase) {
    $full = Assert-WorldSandboxRoot $Root
    foreach ($name in @('Saves\qa-owned-world.save', 'owned-world.txt', 'world-created-generation.txt')) {
        $null = Assert-WorldUnlinkedPath (Join-Path $full $name)
    }
    if ($Phase -eq 'cold') { $null = Assert-WorldUnlinkedPath (Join-Path $full 'world-cold-generation.txt') }
}
