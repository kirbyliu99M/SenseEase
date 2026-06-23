#Requires -Version 5.1
<#
.SYNOPSIS
    Collects SenseEase diagnostic logs into a zip for support reporting.

.DESCRIPTION
    Packages ONLY the diagnostic and crash files from %LOCALAPPDATA%\SenseEase —
    specifically the diag\ folder, the logs\ folder, crash.log, and llama-server.log.

    The logs\ folder holds the daily-rolled JSONL audit logs (the event log and the
    chatbot-action log). These record only technical events — categories, timestamps,
    numeric state snapshots, and action lifecycle metadata. They contain NO chat text,
    NO calibration answers, and NO comfort-profile data, so they are safe to share.

    PRIVACY RATIONALE
    -----------------
    The full SenseEase data folder also contains files with personal health
    information that you should NOT share unless you explicitly choose to:

      - chat_history.jsonl          (your conversation with the AI assistant)
      - calibration_history.jsonl   (your calibration Q&A, may include health details)
      - model_trace.jsonl           (raw AI input/output for every chat turn)
      - habituation_profile.json    (your comfort profile, calibration responses,
                                     vestibular/migraine flags, personalisation notes)
      - behavior_log.jsonl          (daily usage aggregates)

    This script explicitly EXCLUDES all of the above.  The files it DOES include
    (diag\*, logs\*, crash.log, llama-server.log) contain only technical diagnostics:
    timing numbers, error codes, exception types, redacted paths, and the daily-rolled
    event / chatbot-action audit logs.  They are sufficient for diagnosing crashes and
    performance issues without exposing any health or conversation data.

    If you need to share the full folder for a specific investigation, see the
    opt-in command at the bottom of this script.  Read the warning there first.

.NOTES
    PowerShell 5.1 compatible (Windows built-in).
    Safe to run without Administrator — reads only your own AppData.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Paths ────────────────────────────────────────────────────────────────────
$senseEaseRoot = Join-Path $env:LOCALAPPDATA 'SenseEase'
$diagDir       = Join-Path $senseEaseRoot   'diag'
$logsDir       = Join-Path $senseEaseRoot   'logs'
$crashLog      = Join-Path $senseEaseRoot   'crash.log'
$llamaLogs     = @(Get-ChildItem -Path $senseEaseRoot -Filter 'llama-server*.log' -File -ErrorAction SilentlyContinue)

$dateStamp = Get-Date -Format 'yyyyMMdd-HHmm'
$outZip    = Join-Path ([Environment]::GetFolderPath('Desktop')) "SenseEase-diag-$dateStamp.zip"

# ── Collect only the safe diagnostic files ───────────────────────────────────
$toZip = [System.Collections.Generic.List[string]]::new()

if (Test-Path $diagDir) {
    Get-ChildItem -Path $diagDir -File | ForEach-Object { $toZip.Add($_.FullName) }
}

if (Test-Path $logsDir) {
    Get-ChildItem -Path $logsDir -File | ForEach-Object { $toZip.Add($_.FullName) }
}

if (Test-Path $crashLog)  { $toZip.Add($crashLog) }
foreach ($l in $llamaLogs) { $toZip.Add($l.FullName) }

if ($toZip.Count -eq 0) {
    Write-Host "No diagnostic files found under $senseEaseRoot — nothing to zip."
    exit 0
}

# ── Build the zip (PS 5.1: Compress-Archive requires -LiteralPath for arrays) ─
Write-Host "Packaging $($toZip.Count) file(s) into:`n  $outZip"

Compress-Archive -LiteralPath $toZip.ToArray() -DestinationPath $outZip -Force

Write-Host "Done. Zip written to: $outZip"
Write-Host ""
Write-Host "This zip contains only technical diagnostics (crash log, llama-server log,"
Write-Host "the diag\ folder, and the logs\ folder of daily event / chatbot-action audit"
Write-Host "logs). It does NOT contain your chat history, calibration answers, AI model"
Write-Host "trace, comfort profile, or usage aggregates."
Write-Host ""
Write-Host "Send it to the test team along with a description of the problem."

# ─────────────────────────────────────────────────────────────────────────────
# OPT-IN: Full-folder zip (NOT recommended for routine support)
# ─────────────────────────────────────────────────────────────────────────────
# If the test team explicitly asks for the full data folder, run the block below
# in a SEPARATE PowerShell session after reading this warning:
#
#   WARNING — the full folder zip includes files that contain personal health
#   information: your AI chat history, calibration answers (which may include
#   details about vestibular conditions, migraines, or other health factors),
#   raw AI model traces, and your personalised comfort profile.  Only share
#   this if you are comfortable with the test team seeing that content.
#
#   $full = "$env:USERPROFILE\Desktop\SenseEase-FULL-$((Get-Date -Format 'yyyyMMdd-HHmm')).zip"
#   Compress-Archive -Path "$env:LOCALAPPDATA\SenseEase\*" -DestinationPath $full -Force
#   "Full zip written to: $full"
