# SEC — confirmed-open (12)

Coverage: Read fully: MistralChatbotService, ChatbotViewModel, SafetyFloor, HabituationOverrideParser, LongitudinalObservationWriter, all Infrastructure/Storage stores (Atomic/Integrity/PathValidator/EventLog/ChatbotAction/AdjustmentLog/HabituationProfile/BehaviorAggregate), SidecarHeadPoseReader, head_tracker.py + PRIVACY.md + requirements.txt, WgcDiagLog, WindowsActivityFilter, App.xaml.cs, AppConfig, AutoStartRegistry, LlamaServerCalibrationClient, installer .iss, stage-pre-alpha-assets.ps1, HANDBOOK log-zip, safety-floor tests. Verified fixed in code: H1-H4/H8, H5/H6/H7, H9/H10/H12/H13/H29/H39, H14 rework, H16/H46/NE16/NE23, H19 (PathMap+redaction), H24/H25/H28, H30/H31, H34 (Mistral only), H35/H36/H38, H40, H43, H44, H49, H51, EM6, EM7, NE10/NE22, W1W2 #1/#3-#7/#9/#10/#13; OBS-3 scalar-only IPC holds; no network egress beyond 127.0.0.1:8181; DDC/CI fully removed; behavior_log numeric-only; model_trace dev-gated. Not examined: WgcSensingProvider/MainViewModel/MockInferenceEngine in full, full test suite, llama-server binary itself.

#### SEC-01 [P2] Username/install paths still leak into wgc_diag.txt and llama-server.log (PathRedactor never built)
- file: src/SenseEase.Infrastructure/Real/MistralChatbotService.cs:418-419, 493-495 | effort: S | carriedOver: True
- evidence: Code comments state: "PathRedactor (H20) is not present in this build — write the line verbatim." App.xaml.cs:275-278 writes build={Assembly.Location} (C:\Users\<name>\...) into wgc_diag.txt; SidecarHeadPoseReader.cs:319 logs the full sidecar path. crash.log redacts (H19 fixed) but these two system logs do not.
- current: OS account name lands verbatim in wgc_diag.txt and llama-server.log; the handbook log-zip flow (HANDBOOK_en.md:232-240) sends both to the test team, violating the no-PII-in-system-logs invariant (LOG-PATHS §7).
- intended: System logs carry no PII; \Users\<name>\ segments redacted (H20/H21).
- recommendation: Move App.xaml.cs's UserPathRedaction regex into Infrastructure (e.g. a small PathRedactor static) and apply it in WgcDiagLog.Append/WriteHeader and in MistralChatbotService.StartStderrPump before each line is written. Also redact the SidecarHeadPoseReader Started/not-found log strings.
- verdict: isReal=True fixed=False conf=high | Verified verbatim: no-redaction comments at MistralChatbotService.cs:418-419/493-495; App.xaml.cs:275-278 logs Assembly.Location; SidecarHeadPoseReader.cs:319 logs full exe path into wgc_diag.txt sink. WgcDiagLog has zero redaction; only crash.log redacts. Handbook zip includes wgc_diag*.txt.

#### SEC-02 [P2] Tester log-collection zips the full PII corpus (chat transcripts, habituation/medical profile)
- file: docs/HANDBOOK_en.md:232-246 | effort: S | carriedOver: True
- evidence: PowerShell snippet: Compress-Archive -Path "$env:LOCALAPPDATA\SenseEase\*" — includes chat_history.jsonl, calibration_history.jsonl, habituation_profile.json (medical_advisory/vestibular flags), behavior_log. A privacy note exists but the default flow still bundles health-adjacent disclosures (H22).
- current: Default support flow uploads chat text and the vestibular/medical-advisory profile to the test team.
- intended: Bug reports need diag/system logs only; content-bearing PII files excluded by default.
- recommendation: Change the snippet (or ship scripts/collect-logs.ps1) to include only logs\, diag\, crash.log, llama-server.log and explicitly exclude chat_history.jsonl, calibration_history.jsonl, model_trace.jsonl, habituation_profile.json, behavior_log.jsonl; offer the full zip as an explicit opt-in second command.
- verdict: isReal=True fixed=False conf=high | HANDBOOK_en.md:232-240 zips full %LOCALAPPDATA%\SenseEase\*; AppConfig.cs:151-192 confirms chat_history.jsonl, calibration_history.jsonl, habituation_profile.json live there. No scripts/collect-logs.ps1 exists. Privacy note (244-246) is disclosure, not data minimization; default flow still bundles health-adjacent PII.

#### SEC-03 [P2] H32 integrity gate is delete-to-bypass: absent manifest = pass, manifest unsigned in the same user-writable dir
- file: src/SenseEase.Infrastructure/Storage/IntegrityVerifier.cs:43-53 | effort: M | carriedOver: True
- evidence: Verify() returns true when install.manifest.json is missing. stage-pre-alpha-assets.ps1:230-287 now generates the manifest, but it ships unsigned into {localappdata}\Programs\SenseEase — same ACL as the binaries it guards. An attacker who can replace llama-server.exe/head_tracker.exe can delete or rewrite the manifest.
- current: Gate detects accidental corruption only; provides no tamper resistance against the local-write attacker H32/H44/H45 postulate.
- intended: Tampered sub-process binaries fail closed before spawn.
- recommendation: After H26 (Authenticode cert, external) lands: sign SenseEase.App.exe and either compile the expected sub-binary hashes into the signed assembly or Authenticode-sign llama-server.exe/head_tracker.exe and verify signatures instead of a deletable sidecar manifest. Until then, document the gate's limited scope in docs/SECURITY.md.
- verdict: isReal=True fixed=False conf=high | IntegrityVerifier.cs:48-53 returns true on absent manifest; stage-pre-alpha-assets.ps1:229-287 emits unsigned JSON beside the binaries. Delete-to-bypass holds. Partially by-design (documented Alpha-1 no-op; SECURITY.md marks signing 'open', blocked on external H26), but the tamper-resistance gap is real and the delete-bypass scope is undocumented. | corr: Script section starts at line 229, not 230 (trivial).

#### SEC-04 [P2] 1MB/day audit-log cap silently drops the rest of the day, including decline-cooldown and Safety entries
- file: src/SenseEase.Infrastructure/Storage/FileEventLogStore.cs:129-140 | effort: S | carriedOver: True
- evidence: AuditLogFiles.Append: `if (File.Exists(path) && new FileInfo(path).Length >= maxBytes) return;` — no marker, no rotation. FileChatbotActionLog.cs:104 uses the same helper, so Declined entries past the cap lose restart durability (rule-5 cooldown) and forensic Habituation./Safety. events vanish (W1W2 #11).
- current: Once a day file hits 1MB every later durable event that UTC day is silently discarded.
- intended: Durable forensic trail survives the day; safety-relevant entries never dropped silently.
- recommendation: On cap: roll to event-log-YYYY-MM-DD.overflow1.jsonl (bounded count), or at minimum always append Safety.* and Chatbot.Declined entries regardless of cap and write a single 'cap reached, N dropped' marker line.
- verdict: isReal=True fixed=False conf=high | FileEventLogStore.cs:136 silently returns at 1MB/day cap, no marker or rotation. FileChatbotActionLog.cs:104 uses the same helper; decline-cooldown rehydrates from these files via LoadRecent/MostRecentDeclineFor, so capped Declined entries lose restart durability. Safety./Habituation. are durable prefixes. 'Best-effort' comment exists but silent Safety drops are a genuine gap.

#### SEC-05 [P3] Local-time timestamps still persisted in durable forensic logs (default DateTime.Now)
- file: src/SenseEase.Core/Models/EventLogEntry.cs:6 | effort: S | carriedOver: True
- evidence: EventLogEntry.Timestamp defaults to DateTime.Now. HabituationOverrideApplicator.cs:75-134 and MainViewModel.cs:392/417/437 append durable 'Habituation.*' entries without setting Timestamp, so local time is serialized into event-log-YYYY-MM-DD.jsonl files named by UTC day (W1W2 #12/NE21). ChatbotViewModel.cs:257 also persists chat bubbles with DateTime.Now.
- current: Forensic clock skew: ReadDurable(since) compares mixed-kind DateTimes; file day boundary and record timestamps disagree by the UTC offset.
- intended: UTC ISO-8601 timestamps in all persisted records (locked logging constraint).
- recommendation: Change the EventLogEntry.Timestamp default to DateTime.UtcNow, audit the ~10 appenders, and convert to local time only in the dashboard 'Time' display helper.
- verdict: isReal=True fixed=False conf=high | EventLogEntry.cs:6 defaults to DateTime.Now. HabituationOverrideApplicator.cs:75-134 and MainViewModel.cs:392/417/437 (also 742) append durable entries without Timestamp; MockInferenceEngine and ChatbotViewModel:1092 set UtcNow, so ReadDurable(since) mixes kinds. ChatbotViewModel.cs:257 persists chat with DateTime.Now. P3 appropriate.

#### SEC-06 [P3] llama-server Bearer key passed on the child command line — readable by the same-user attacker H9 defends against
- file: src/SenseEase.Infrastructure/Real/MistralChatbotService.cs:385 | effort: S | carriedOver: False
- evidence: serverArgs includes `--api-key {_apiKey}`. Any process in the same session can read another process's command line (WMI Win32_Process / NtQueryInformationProcess) — exactly the unauthenticated-local-process class the H9 loopback auth excludes.
- current: The per-process random key is trivially recoverable by any local same-user process for the server's lifetime.
- intended: Loopback credential not disclosed outside the two cooperating processes.
- recommendation: Pass the key via the child environment instead (llama-server supports LLAMA_ARG_API_KEY env-var args): set psi.EnvironmentVariables["LLAMA_ARG_API_KEY"] and drop --api-key from the command line. Environment blocks of other processes are not readable without elevated handles.
- verdict: isReal=True fixed=False conf=high | Confirmed: MistralChatbotService.cs:385 passes `--api-key {_apiKey}` in serverArgs. Same-user processes can read child command lines, undermining H9's loopback-auth threat model for the server's lifetime. Env-var delivery (LLAMA_ARG_API_KEY) is a valid mitigation. P3 fits (same-user attacker).

#### SEC-07 [P3] LlamaServerCalibrationClient missing the H34 hardening applied to MistralChatbotService
- file: src/SenseEase.Infrastructure/Real/LlamaServerCalibrationClient.cs:52, 126-127 | effort: S | carriedOver: True
- evidence: HttpClient Timeout = 5 minutes, no MaxResponseContentBufferSize, unbounded ReadAsStringAsync, JsonDocument.Parse with no MaxDepth. H34's 60s/512KiB/MaxDepth-16 fix landed only in MistralChatbotService. Client is live (consumed by UserModelRetrospectiveAnalyzer).
- current: A wedged/hostile loopback responder can hold the UM4 background task 5 minutes and buffer an unbounded body.
- intended: All loopback LLM clients share the same response cap and timeout.
- recommendation: Mirror the MistralChatbotService settings: Timeout 60s, MaxResponseContentBufferSize 512KiB, JsonDocumentOptions MaxDepth=16.
- verdict: isReal=True fixed=False conf=high | Confirmed: LlamaServerCalibrationClient.cs:52 Timeout=5min, :126-127 unbounded ReadAsStringAsync + JsonDocument.Parse without MaxDepth. MistralChatbotService has 60s/512KiB/MaxDepth16 (lines 79-80, 93). Client is live per its own header (consumed via ICalibrationLlmClient by UserModelRetrospectiveAnalyzer).

#### SEC-08 [P3] Remaining JSONL stores keep unbounded ReadAllLines and uncapped growth (H41/H42 residual)
- file: src/SenseEase.Infrastructure/Storage/FileAdjustmentLog.cs:35, 71 | effort: S | carriedOver: True
- evidence: FileAdjustmentLog.Load (ReadAllLines, no per-line bound, no file cap/rotation on adjustments.jsonl), FileChatbotActionLog.LoadRecent:113 and DailyBehaviorAggregateWriter:83 likewise. The huge-line guard from 623f82e was added only to FileChatHistoryStore.
- current: A tampered or corrupted multi-GB single line in these files is fully buffered at startup; adjustments.jsonl grows without bound across months.
- intended: Startup-read DoS guards and rotation on all persisted JSONL (H41/H42).
- recommendation: Reuse the FileChatHistoryStore pattern: File.ReadLines + skip lines >64KB; add a startup trim for adjustments.jsonl (e.g. keep last 2000 records — WeightFor already tares at the last reset).
- verdict: isReal=True fixed=False conf=high | Confirmed: FileAdjustmentLog.cs:35 ReadAllLines, :71 append with no rotation; FileChatbotActionLog.cs:113 and DailyBehaviorAggregateWriter.cs:83 also ReadAllLines. FileChatHistoryStore alone has the 64KB MaxLineChars guard. Minor nuance: FileChatbotActionLog writes via AuditLogFiles with MaxDailyFileBytes, so growth cap mainly affects adjustments.jsonl.

#### SEC-09 [P3] Schema-downgrade guard depends on a deletable sibling flag file (H14 residual)
- file: src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs:208-215 | effort: M | carriedOver: True
- evidence: IsSchemaDowngrade() == File.Exists(schema_floor_v2.flag). Deleting the flag then planting a schema_version:1 profile null-fills MedicalAdvisoryShown (known-open per HABITUATION_ADJUSTMENT_PLAN §4.D). Mitigation observed: MockInferenceEngine.cs:1713 re-writes MedicalAdvisoryShown from the in-memory vestibular flag, narrowing the impact.
- current: Two-file local edit bypasses the v1-downgrade refusal; no checksum binds flag to profile.
- intended: A v1 file on an install that has written v2+ is always refused.
- recommendation: Store the schema floor inside user-preferences.json (a second independent location) or embed an HMAC of (schema_version, updated_at) keyed on a per-install random ID; given Q3=B lets any user disable protection anyway, P3 is appropriate — fold into the H44/H45 tamper-resistance wave.
- verdict: isReal=True fixed=False conf=high | Confirmed: FileHabituationProfileStore.cs:208-215 IsSchemaDowngrade == File.Exists(flag), no checksum binding. HABITUATION_ADJUSTMENT_PLAN §4.D documents it as known-loose. Mitigation verified at src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:1713 (re-writes MedicalAdvisoryShown). Documented residual, not by-design acceptance; P3 appropriate. | corr: Mitigation file lives at src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs (Mock folder), not Engines

#### SEC-10 [P2] External-gated supply-chain items remain open: unsigned binaries, stale llama.cpp, opencv CVE pin, PyInstaller unpinned
- file: tools/head-tracker-sidecar/requirements.txt:18 | effort: L | carriedOver: True
- evidence: opencv-python==4.10.0.84 still pinned (H47 CVE-2025-53644 per backlog); PyInstaller absent from requirements.txt (H48 unpinned); llama.cpp b9294 binaries unchanged (H33, CVE-2026-21869 per backlog); installer/SenseEase-v0.11.iss produces an unsigned exe and no SignTool step (H26 — cert procurement external).
- current: Alpha installers train testers through SmartScreen bypass; shipped native deps carry known CVEs.
- intended: Signed installer/exes; CVE-current dependency pins (SECURITY.md §5).
- recommendation: Keep these on the Beta-entry checklist: procure the Authenticode cert (long lead, start now), bump opencv-python to the patched 4.10.x+, pin PyInstaller in requirements.txt, rebuild llama.cpp from a current tag, then regenerate the H32 manifest and .sha256 sidecars.
- verdict: isReal=True fixed=False conf=high | Confirmed: requirements.txt:18 opencv-python==4.10.0.84; PyInstaller absent; installer/SenseEase-v0.11.iss has no SignTool step; llama.cpp b9294 references persist. Backlog confirms H26/H33/H47/H48 with cited CVEs. Nuance: CVE-2026-21869 needs --context-shift, which the app explicitly disables (--no-context-shift), so live exploit is gated.

#### SEC-11 [P3] Engine snapshot persist can still null calibration/advisory fields when the profile file exists but reads null (W1W2 #2 residual)
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:~1700-1720 (MaybePersistProfileSnapshot) | effort: M | carriedOver: True
- evidence: Snapshot carries forward existing?.MedicalAdvisoryShown / existing?.CalibrationResponses etc.; if Load() returns null (file locked beyond AtomicJsonWriter's 5-retry backoff, or malformed JSON) while the file exists, the next Save overwrites those fields with null. Vestibular advisory is re-derived from _vestibularProtection, so the safety core survives, but calibration history fields can be wiped.
- current: Transient AV lock or corruption during a dirty tick can erase carried-forward fields the engine doesn't own.
- intended: A persist never clobbers other writers' fields on a transient read failure (EM2 merge-preserve guarantee).
- recommendation: Make TryRead distinguish 'file missing' from 'file unreadable' (return a sentinel or out flag); MaybePersistProfileSnapshot should skip the persist (retry next tick) when the file exists but could not be read.
- verdict: isReal=True fixed=False conf=medium | Main clobber path already fixed: BuildProfileSnapshot returns null when existing is null and _profileExistedAtLoad, and Update skips Save. Residual is real but narrow: sessions booting without a profile keep the flag false, so mid-session-created files can still be wiped on a transient AV-lock read. P3 fits. | corr: Code is in BuildProfileSnapshot (lines 1639-1721, guard at 1653), not MaybePersistProfileSnapshot (1480-1532). Scope narrower than evidence states: file-existed-at-boot case is already guarded by W2.0-C (8845c72).

#### SEC-12 [P3] PRIVACY.md allowlist omits the --diag CSV escape valve (H18 residual, doc-only)
- file: tools/head-tracker-sidecar/PRIVACY.md:21-25 | effort: S | carriedOver: True
- evidence: Doc: "The sidecar writes nothing to disk under normal operation." head_tracker.py:543-545/681-691 writes a per-frame yaw/pitch/cx/cy CSV to any caller-supplied path under --diag. Production host never passes it (SidecarHeadPoseReader.BuildSidecarArgs:608-617 emits only --input/--no-mirror), so OBS-3 holds at runtime, but the privacy doc's forbidden-list doesn't mention the flag.
- current: A privacy reviewer reading PRIVACY.md would not learn the diagnostic file-write path exists.
- intended: PRIVACY.md documents every disk-write path with its gating condition.
- recommendation: Add a one-paragraph note to PRIVACY.md: --diag exists, writes derived pose scalars only (never frames/landmarks), is dev-only, and is never passed by the C# host; cite BuildSidecarArgs as the enforcement point.
- verdict: isReal=True fixed=False conf=high | All citations accurate today: PRIVACY.md:21-25 claims no disk writes; head_tracker.py:543-545/677-691 writes diag CSV under --diag; BuildSidecarArgs (SidecarHeadPoseReader.cs:608-616) emits only --input/--no-mirror so runtime holds. Doc omits the flag entirely. Genuine doc-only P3 gap, not fixed.