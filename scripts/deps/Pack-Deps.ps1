<#
.SYNOPSIS
  Packs the non-redownloadable dependencies into a backup set (tar archives, split under 2 GiB,
  plus SHA256SUMS) and optionally uploads it to the PRIVATE release store.

.DESCRIPTION
  Reads deps.manifest.json. Every component with an "asset" is packed from its local copy
  ("packFrom" if set, otherwise "dest"); the local copy must match the manifest hash first.
  Only reads the sources - nothing in the project is moved or deleted.
  -Upload creates the private repo/release if missing and refuses to upload to a non-private repo.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\deps\Pack-Deps.ps1
  powershell -ExecutionPolicy Bypass -File scripts\deps\Pack-Deps.ps1 -Upload
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [string[]]$Only,
    [switch]$Upload
)

. (Join-Path $PSScriptRoot 'DepsCommon.ps1')

$manifest = Read-DepsManifest
$store    = $manifest.privateStore
if (-not $OutDir) { $OutDir = Join-Path $env:USERPROFILE ("SenseEase-deps-backup\" + $store.tag) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "Backup set: $OutDir"

foreach ($c in $manifest.components) {
    if (-not $c.asset) { continue }
    if ($Only -and ($Only -notcontains $c.id)) { continue }
    $src = if ($c.packFrom) { Resolve-DepPath $c.packFrom } else { Resolve-DepPath $c.dest }
    $out = Join-Path $OutDir $c.asset
    if ((Test-Path $out) -or (Test-Path "$out.part01")) { Write-Host ('{0,-22} already packed' -f $c.id); continue }
    if (-not (Test-Path $src)) { Write-Warning "$($c.id): source missing ($src) - skipped"; continue }

    Write-Host ('{0,-22} packing {1}' -f $c.id, $src)
    if ($c.type -eq 'file') {
        if ((Get-Sha256 $src) -ne $c.sha256) { throw "$($c.id): local file does not match the manifest SHA-256" }
        Copy-Item -Path $src -Destination $out
    } else {
        if ((Get-TreeHash $src) -ne $c.treeHash) { throw "$($c.id): local folder does not match the manifest tree hash" }
        & (Get-Tar) -c -f $out -C $src .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $($c.id)" }
    }
    if ((Get-Item $out).Length -gt $script:PartSize) {
        $parts = Split-DepFile $out
        Remove-Item -Path $out     # the whole archive is our own temp output; the parts replace it
        Write-Host ('{0,-22} split into {1} parts' -f '', $parts.Count)
    }
}

# SHA256SUMS over everything in the set (rebuilt each run).
$sumFile = Join-Path $OutDir 'SHA256SUMS'
$lines = Get-ChildItem -Path $OutDir -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object Name |
         ForEach-Object { '{0}  {1}' -f (Get-Sha256 $_.FullName), $_.Name }
[IO.File]::WriteAllText($sumFile, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Get-ChildItem -Path $OutDir -File | Sort-Object Name |
    ForEach-Object { '  {0,-34} {1,8:N0} MB' -f $_.Name, ($_.Length / 1MB) }

if (-not $Upload) {
    Write-Host ''
    Write-Host 'Packed only. Re-run with -Upload to send the set to the private store.'
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required for -Upload.' }
& gh repo view $store.repo --json name 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating PRIVATE repo $($store.repo)"
    & gh repo create $store.repo --private --description 'SenseEase non-redownloadable build dependencies (private)'
    if ($LASTEXITCODE -ne 0) { throw 'gh repo create failed' }
}
$vis = (& gh repo view $store.repo --json visibility --jq '.visibility')
if ($vis -ne 'PRIVATE') { throw "Refusing to upload: $($store.repo) is $vis, not PRIVATE." }

& gh release view $store.tag -R $store.repo 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & gh release create $store.tag -R $store.repo --title "SenseEase dependencies $($store.tag)" `
        --notes 'Backup of the non-redownloadable SenseEase build dependencies. Restore with scripts/deps/Restore-Deps.ps1.'
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
$files = Get-ChildItem -Path $OutDir -File | Sort-Object Name
foreach ($f in $files) {
    Write-Host "uploading $($f.Name)"
    & gh release upload $store.tag $f.FullName -R $store.repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "upload failed: $($f.Name)" }
}
Write-Host "Done: $($files.Count) files in $($store.repo)@$($store.tag)"
