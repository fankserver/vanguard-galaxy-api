# Path prerequisites only; callers still need exclusive filesystem control and run authorization.
function Assert-WorldUnlinkedPath([string]$Path, [bool]$MustExist = $true) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($MustExist -and !(Test-Path -LiteralPath $full)) { throw 'Required world path is missing.' }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked world path or ancestor refused.' }
        }
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
    return Assert-WorldUnlinkedPath $full $MustExist
}

function Assert-WorldReceiptPaths([string]$Root, [ValidateSet('create','cold')][string]$Phase) {
    $full = Assert-WorldSandboxRoot $Root
    foreach ($name in @('Saves\qa-owned-world.save', 'owned-world.txt', 'world-created-generation.txt')) {
        $null = Assert-WorldUnlinkedPath (Join-Path $full $name)
    }
    if ($Phase -eq 'cold') { $null = Assert-WorldUnlinkedPath (Join-Path $full 'world-cold-generation.txt') }
}
