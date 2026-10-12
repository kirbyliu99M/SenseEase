# Idempotent V1.3.1 compressed installer build. Re-runnable: skips any package already built (single-file
# .exe >500MB, OR spanned stub.exe + at least one .bin). Survives interruption — just re-invoke; it resumes.
# Writes installer\beta-v1.3.1\_build_summary.txt + per-package .sha256 (covers .exe AND .bin slices).
$ISCC = "C:\Users\kirby\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
$repo = "C:\Users\kirby\Desktop\SenseEase APP"
$out  = "$repo\installer\beta-v1.3.1"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$log  = "$out\_build_summary.txt"
"=== RUN $(Get-Date -Format o) ===" | Out-File $log -Append -Encoding utf8

# AMD first (the field-tested platform), then Auto (also AMD-capable), then the two Intel-OV single-files.
$jobs = @(
  @{ name='AMD';       iss='SenseEase-beta-v1.1-amd.iss';      dist='beta-v1.3.1-amd';  outname=$null },
  @{ name='Auto';      iss='SenseEase-beta-v1.1-auto.iss';     dist='beta-v1.3.1-auto'; outname=$null },
  @{ name='OV-Cpp';    iss='SenseEase-beta-v1.1-intel-ov.iss'; dist='beta-v1.3.1-cpp';  outname='SenseEase-Beta-V1.3.1-OV-Cpp' },
  @{ name='OV-Python'; iss='SenseEase-beta-v1.1-intel-ov.iss'; dist='beta-v1.3.1-py';   outname='SenseEase-Beta-V1.3.1-OV-Python' }
)
foreach ($j in $jobs) {
  $pdir = "$out\$($j.name)"
  New-Item -ItemType Directory -Path $pdir -Force | Out-Null
  # Correct idempotent skip-check: single-file = fat .exe; spanned = stub .exe + >=1 .bin, total >500MB.
  $exe = Get-ChildItem $pdir -Filter *.exe -EA SilentlyContinue | Select-Object -First 1
  $done = $false
  if ($exe) {
    $bins  = @(Get-ChildItem $pdir -Filter *.bin -EA SilentlyContinue)
    $total = $exe.Length + (($bins | Measure-Object Length -Sum).Sum)
    if ($exe.Length -gt 500MB) { $done = $true }
    elseif ($bins.Count -ge 1 -and $total -gt 500MB) { $done = $true }
  }
  if ($done) { "    SKIP $($j.name) (already built)" | Out-File $log -Append -Encoding utf8; continue }

  "=== $($j.name) build start $(Get-Date -Format HH:mm:ss) ===" | Out-File $log -Append -Encoding utf8
  $a = @("/Qp", "/DDistRoot=$repo\dist\$($j.dist)", "/O$pdir")
  if ($j.outname) { $a += "/DOutputName=$($j.outname)" }
  $a += "$repo\installer\$($j.iss)"
  & $ISCC @a *> "$pdir\_iscc.log"
  "    $($j.name) ISCC exit=$LASTEXITCODE  $(Get-Date -Format HH:mm:ss)" | Out-File $log -Append -Encoding utf8
}

"=== outputs + sha256 ===" | Out-File $log -Append -Encoding utf8
Get-ChildItem $out -Recurse -Include *.exe,*.bin -EA SilentlyContinue | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
  "$h *$($_.Name)" | Out-File "$($_.FullName).sha256" -Encoding ascii
  ("  {0,-40} {1,6} MB" -f $_.Name, [math]::Round($_.Length/1MB,0)) | Out-File $log -Append -Encoding utf8
}
"V1.3.1 BUILD LOOP DONE $(Get-Date -Format o)" | Out-File $log -Append -Encoding utf8