# Idempotent V1.3 compressed installer build. Re-runnable: skips any package whose .exe is already >500MB.
# Survives interruption — just re-invoke. Writes installer\beta-v1.3\_build_summary.txt + per-pkg .sha256.
$ISCC = "C:\Users\kirby\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
$repo = "C:\Users\kirby\Desktop\SenseEase APP"
$out  = "$repo\installer\beta-v1.3"
$log  = "$out\_build_summary.txt"
"=== RUN $(Get-Date -Format o) ===" | Out-File $log -Append -Encoding utf8

$jobs = @(
  @{ name='OV-Cpp';    iss='SenseEase-beta-v1.1-intel-ov.iss'; dist='beta-v1.3-cpp';  outname='SenseEase-Beta-V1.3-OV-Cpp' },
  @{ name='OV-Python'; iss='SenseEase-beta-v1.1-intel-ov.iss'; dist='beta-v1.3-py';   outname='SenseEase-Beta-V1.3-OV-Python' },
  @{ name='AMD';       iss='SenseEase-beta-v1.1-amd.iss';      dist='beta-v1.3-amd';  outname=$null },
  @{ name='Auto';      iss='SenseEase-beta-v1.1-auto.iss';     dist='beta-v1.3-auto'; outname=$null }
)
foreach ($j in $jobs) {
  $exe = Get-ChildItem "$out\$($j.name)" -Filter *.exe -EA SilentlyContinue | Select-Object -First 1
  if ($exe -and $exe.Length -gt 500MB) { "    SKIP $($j.name) ($([math]::Round($exe.Length/1MB,0)) MB)" | Out-File $log -Append -Encoding utf8; continue }
  "=== $($j.name) build $(Get-Date -Format HH:mm:ss) ===" | Out-File $log -Append -Encoding utf8
  $a = @("/Qp", "/DDistRoot=$repo\dist\$($j.dist)", "/O$out\$($j.name)")
  if ($j.outname) { $a += "/DOutputName=$($j.outname)" }
  $a += "$repo\installer\$($j.iss)"
  & $ISCC @a *> "$out\$($j.name)\_iscc.log"
  "    $($j.name) ISCC exit=$LASTEXITCODE  $(Get-Date -Format HH:mm:ss)" | Out-File $log -Append -Encoding utf8
}
"=== outputs + sha256 ===" | Out-File $log -Append -Encoding utf8
Get-ChildItem $out -Recurse -Filter *.exe | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
  "$h *$($_.Name)" | Out-File "$($_.FullName).sha256" -Encoding ascii
  ("  {0,-44} {1,6} MB  {2}" -f $_.Name, [math]::Round($_.Length/1MB,0), $h.Substring(0,16)) | Out-File $log -Append -Encoding utf8
}
"V1.3 BUILD DONE $(Get-Date -Format o)" | Out-File $log -Append -Encoding utf8
