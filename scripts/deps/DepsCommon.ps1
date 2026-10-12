# Shared helpers for Restore-Deps.ps1 / Pack-Deps.ps1 (dot-sourced). Windows PowerShell 5.1 compatible.
# Keep this file ASCII-only: PS 5.1 reads BOM-less UTF-8 as ANSI.

$ErrorActionPreference = 'Stop'

$script:RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ManifestPath = Join-Path $PSScriptRoot 'deps.manifest.json'
$script:PartSize     = 1900MB   # GitHub release assets must be < 2 GiB each

function Read-DepsManifest {
    Get-Content -Raw -Path $script:ManifestPath | ConvertFrom-Json
}

# Expand %VAR% tokens; a relative path is taken relative to the repo root.
function Resolve-DepPath([string]$p) {
    $x = [Environment]::ExpandEnvironmentVariables($p)
    if ([IO.Path]::IsPathRooted($x)) { return $x }
    return (Join-Path $script:RepoRoot $x)
}

function Get-Sha256([string]$file) {
    (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
}

# Tree hash of a directory: SHA-256 over the sorted lines "<relative/path>`t<sha256>`n"
# (forward slashes, ordinal sort, UTF-8). Lets -Verify check a whole folder against one value.
function Get-TreeHash([string]$dir) {
    $root  = (Resolve-Path $dir).Path.TrimEnd('\')
    $lines = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -Path $root -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
        $lines.Add($rel + "`t" + (Get-Sha256 $_.FullName))
    }
    $arr = $lines.ToArray()
    [Array]::Sort($arr, [StringComparer]::Ordinal)
    $text  = ($arr | ForEach-Object { $_ + "`n" }) -join ''
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $sha   = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}

# Split a file into <file>.part01, .part02, ... of at most $script:PartSize bytes. Returns the part paths.
function Split-DepFile([string]$file) {
    $parts  = @()
    $buffer = New-Object byte[] (64MB)
    $in = [IO.File]::OpenRead($file)
    try {
        $n = 1
        while ($in.Position -lt $in.Length) {
            $part = '{0}.part{1:D2}' -f $file, $n
            $out  = [IO.File]::Create($part)
            try {
                $written = 0L
                while ($written -lt $script:PartSize) {
                    $want = [int][Math]::Min($buffer.Length, $script:PartSize - $written)
                    $read = $in.Read($buffer, 0, $want)
                    if ($read -le 0) { break }
                    $out.Write($buffer, 0, $read)
                    $written += $read
                }
            } finally { $out.Dispose() }
            $parts += $part
            $n++
        }
    } finally { $in.Dispose() }
    return $parts
}

# Concatenate parts (in name order) back into one file.
function Join-DepParts([string[]]$parts, [string]$outFile) {
    $out = [IO.File]::Create($outFile)
    try {
        foreach ($p in ($parts | Sort-Object)) {
            $in = [IO.File]::OpenRead($p)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
        }
    } finally { $out.Dispose() }
}

function Get-Tar {
    $t = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $t)) { throw 'tar.exe not found (ships with Windows 10 1803+).' }
    return $t
}

# Local state of one component: Present / Missing / Mismatch.
function Test-DepComponent($c) {
    $dest = Resolve-DepPath $c.dest
    if ($c.type -eq 'file') {
        if (-not (Test-Path $dest -PathType Leaf)) { return 'Missing' }
        if ((Get-Sha256 $dest) -ne $c.sha256) { return 'Mismatch' }
        foreach ($copy in @($c.copies)) {
            if (-not $copy) { continue }
            $cp = Resolve-DepPath $copy
            if (-not (Test-Path $cp -PathType Leaf) -or (Get-Sha256 $cp) -ne $c.sha256) { return 'Mismatch' }
        }
        return 'Present'
    }
    if (-not (Test-Path $dest -PathType Container)) { return 'Missing' }
    if ((Get-TreeHash $dest) -ne $c.treeHash) { return 'Mismatch' }
    return 'Present'
}
