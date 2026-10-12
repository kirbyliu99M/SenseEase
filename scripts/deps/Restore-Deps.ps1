<#
.SYNOPSIS
  Restores (or verifies) the large dependencies that are not in git: AI models, native runtimes,
  the OpenVINO GenAI hosts, FastFlowLM and the head-tracker sidecar build.

.DESCRIPTION
  Every component is listed in deps.manifest.json with its destination and SHA-256.
  - Public components are downloaded from their original source and hash-checked.
  - Everything else comes from the PRIVATE release store (privateStore in the manifest),
    downloaded with the GitHub CLI. Run `gh auth login` first; no token is stored in this repo.
  Nothing already present is deleted: a mismatched folder is renamed to <name>.old-<timestamp>.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\deps\Restore-Deps.ps1 -Verify
  powershell -ExecutionPolicy Bypass -File scripts\deps\Restore-Deps.ps1
  powershell -ExecutionPolicy Bypass -File scripts\deps\Restore-Deps.ps1 -Only qwen3-gguf,face-landmarker
#>
[CmdletBinding()]
param(
    [switch]$Verify,            # report only; download nothing
    [string[]]$Only,            # restrict to these component ids
    [switch]$Force,             # re-restore even components that verify as present
    [string]$WorkDir = (Join-Path $env:TEMP 'senseease-deps')
)

. (Join-Path $PSScriptRoot 'DepsCommon.ps1')
$ProgressPreference = 'SilentlyContinue'

$manifest = Read-DepsManifest
$store    = $manifest.privateStore
$isAdmin  = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
$sums     = $null
$failed   = @()

function Get-PrivateSums {
    if ($script:sums) { return $script:sums }
    $f = Join-Path $WorkDir 'SHA256SUMS'
    if (-not (Test-Path $f)) {
        & gh release download $store.tag -R $store.repo -p 'SHA256SUMS' -D $WorkDir --clobber
        if ($LASTEXITCODE -ne 0) { throw "Could not read SHA256SUMS from $($store.repo)@$($store.tag) (gh auth login? access to the private repo?)" }
    }
    $map = @{}
    Get-Content $f | ForEach-Object {
        if ($_ -match '^([0-9a-f]{64})\s+\*?(.+)$') { $map[$matches[2].Trim()] = $matches[1] }
    }
    $script:sums = $map
    return $map
}

# Download one private asset (whole file or .partNN set) into $WorkDir and return the verified local path.
function Get-PrivateAsset([string]$asset) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required for private assets.' }
    $map = Get-PrivateSums
    $names = @($map.Keys | Where-Object { $_ -eq $asset -or $_ -like "$asset.part*" } | Sort-Object)
    if ($names.Count -eq 0) { throw "Asset '$asset' is not listed in SHA256SUMS of $($store.repo)@$($store.tag)." }
    foreach ($n in $names) {
        $local = Join-Path $WorkDir $n
        if ((Test-Path $local) -and (Get-Sha256 $local) -eq $map[$n]) { continue }
        Write-Host "    downloading $n"
        & gh release download $store.tag -R $store.repo -p $n -D $WorkDir --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release download failed for $n" }
        if ((Get-Sha256 $local) -ne $map[$n]) { throw "Checksum mismatch for $n" }
    }
    $target = Join-Path $WorkDir $asset
    if ($names.Count -gt 1 -or $names[0] -ne $asset) {
        Write-Host "    joining $($names.Count) parts"
        Join-DepParts -parts ($names | ForEach-Object { Join-Path $WorkDir $_ }) -outFile $target
    }
    return $target
}

# Move an existing destination aside instead of deleting it.
function Backup-Existing([string]$path) {
    if (Test-Path $path) {
        $aside = '{0}.old-{1}' -f $path.TrimEnd('\', '/'), (Get-Date -Format 'yyyyMMdd-HHmmss')
        Write-Host "    keeping the existing copy as $aside"
        Move-Item -Path $path -Destination $aside
    }
}

function Restore-File($c) {
    $dest = Resolve-DepPath $c.dest
    $tmp  = Join-Path $WorkDir ([IO.Path]::GetFileName($dest))
    $ok   = $false
    if ($c.url) {
        Write-Host "    downloading $($c.url)"
        & curl.exe -L --fail --retry 3 -s -o $tmp $c.url
        if ($LASTEXITCODE -eq 0 -and (Get-Sha256 $tmp) -eq $c.sha256) { $ok = $true }
        elseif ($LASTEXITCODE -eq 0) { Write-Warning "    public file no longer matches the pinned SHA-256" }
    }
    if (-not $ok -and $c.asset) {
        $tmp = Get-PrivateAsset $c.asset
        $ok  = ((Get-Sha256 $tmp) -eq $c.sha256)
    }
    if (-not $ok) { throw "No verified source for $($c.id)" }
    foreach ($target in @($dest) + @($c.copies | Where-Object { $_ } | ForEach-Object { Resolve-DepPath $_ })) {
        if ((Test-Path $target) -and (Get-Sha256 $target) -eq $c.sha256) { continue }
        Backup-Existing $target
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -Path $tmp -Destination $target
    }
}

function Restore-Dir($c) {
    if (-not $c.asset) { throw "No source configured for $($c.id)" }
    $dest    = Resolve-DepPath $c.dest
    $archive = Get-PrivateAsset $c.asset
    $staging = Join-Path $WorkDir ($c.id + '.staging')
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }   # our own temp dir only
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Write-Host "    extracting"
    & (Get-Tar) -x -f $archive -C $staging
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
    if ((Get-TreeHash $staging) -ne $c.treeHash) { throw "Extracted $($c.id) does not match its tree hash" }
    Backup-Existing $dest
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    Move-Item -Path $staging -Destination $dest
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

foreach ($c in $manifest.components) {
    if ($Only -and ($Only -notcontains $c.id)) { continue }
    $state = Test-DepComponent $c
    $line  = '{0,-22} {1,-9} {2}' -f $c.id, $state, $c.dest
    if ($Verify) {
        Write-Host $line
        if ($state -ne 'Present') { $failed += $c.id }
        continue
    }
    if ($state -eq 'Present' -and -not $Force) { Write-Host $line; continue }
    if ($c.requiresAdmin -and -not $isAdmin) {
        Write-Warning "$($c.id): needs an elevated (Administrator) shell to write $($c.dest) - skipped"
        $failed += $c.id
        continue
    }
    Write-Host "$($c.id): restoring ($state)"
    try {
        if ($c.type -eq 'file') { Restore-File $c } else { Restore-Dir $c }
        Write-Host ('{0,-22} {1,-9} {2}' -f $c.id, (Test-DepComponent $c), $c.dest)
    } catch {
        Write-Warning "$($c.id): $($_.Exception.Message)"
        if ($c.rebuild) { Write-Warning "    rebuild instead: $($c.rebuild)" }
        $failed += $c.id
    }
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host ("Not OK: " + ($failed -join ', '))
    exit 1
}
Write-Host ''
Write-Host 'All dependencies present and verified.'
