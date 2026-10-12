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
        Invoke-Exe (Get-Tar) @('-c', '-f', $out, '-C', $src, '.') | Out-Null
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
# Probe: a missing repo/release is expected on the first run, so its "not found" stays quiet.
Invoke-Exe gh @('repo', 'view', $store.repo, '--json', 'name') -Quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating PRIVATE repo $($store.repo)"
    Invoke-Exe gh @('repo', 'create', $store.repo, '--private',
                    '--description', 'SenseEase non-redownloadable build dependencies (private)') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gh repo create failed' }
}
$vis = Invoke-Exe gh @('repo', 'view', $store.repo, '--json', 'visibility', '--jq', '.visibility') | Select-Object -First 1
if ($vis -ne 'PRIVATE') { throw "Refusing to upload: $($store.repo) is '$vis', not PRIVATE." }

Invoke-Exe gh @('release', 'view', $store.tag, '-R', $store.repo) -Quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Invoke-Exe gh @('release', 'create', $store.tag, '-R', $store.repo,
                    '--title', "SenseEase dependencies $($store.tag)",
                    '--notes', 'Backup of the non-redownloadable SenseEase build dependencies. Restore with scripts/deps/Restore-Deps.ps1.') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
# Resume support: skip assets already uploaded with the same size (SHA256SUMS is always re-sent).
$have = @{}
Invoke-Exe gh @('release', 'view', $store.tag, '-R', $store.repo, '--json', 'assets',
                '--jq', '.assets[] | [.name, .size] | @tsv') -Quiet | ForEach-Object {   # no embedded quotes: PS 5.1 mangles them for native args
    $kv = $_ -split "`t"
    if ($kv.Count -eq 2) { $have[$kv[0]] = [int64]$kv[1] }
}
$files = Get-ChildItem -Path $OutDir -File | Sort-Object Name
$i = 0
foreach ($f in $files) {
    $i++
    if ($f.Name -ne 'SHA256SUMS' -and $have.ContainsKey($f.Name) -and $have[$f.Name] -eq $f.Length) {
        Write-Host ('skip      {0}/{1}  {2}  (already uploaded)' -f $i, $files.Count, $f.Name)
        continue
    }
    Write-Host ('uploading {0}/{1}  {2}  ({3:N0} MB)  {4:HH:mm:ss}' -f $i, $files.Count, $f.Name, ($f.Length / 1MB), (Get-Date))
    Invoke-Exe gh @('release', 'upload', $store.tag, $f.FullName, '-R', $store.repo, '--clobber') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "upload failed: $($f.Name) - re-run the same command to resume (uploads use --clobber)" }
}
Write-Host "Done: $($files.Count) files in $($store.repo)@$($store.tag)"
