> **[LEGACY]** Superseded by [Beta-1.md](../Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# Code Review — W1 + W2 (recall, extra-high effort)

> Generated 2026-06-05 by code-review-w1w2 workflow (81 agents, 67 candidates -> 52 verified -> 15 ranked). Scope: git diff main...HEAD (25 files, +1137/-181). No code changed by this review.

## 1. src/SenseEase.Core/Safety/SafetyFloor.cs:66

**The two apostrophe-bearing crisis phrases use ASCII U+0027, but NFKC normalization does not fold the curly apostrophe U+2019, so iOS/Windows-autocorrected self-harm messages silently bypass the safety floor entirely.**

A user in crisis types "I don't want to live" / "I don't want to be here" on a phone or with autocorrect, where the apostrophe is U+2019. normalized = Normalize(FormKC).ToLowerInvariant() keeps U+2019, so .Contains("don't want to live", Ordinal) returns false and no apostrophe-free fallback phrase matches; Trips() returns false, the message is routed to the LLM/lexicon/profile-picker, and the crisis line is never shown — empirically verified True->False with only the apostrophe changed. This is the exact miss the floor exists to prevent, on the single most common real-world typing variant.

## 2. src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:825

**When _profileStore.Load() returns null on a transient read failure, BuildProfileSnapshot null-fills MedicalAdvisoryShown into a fresh, well-formed v2 snapshot and Save() overwrites the real profile, permanently wiping the vestibular safety advisory.**

A vestibular user has MedicalAdvisoryShown=Vestibular on disk. A transient IOException (AV/indexer/backup briefly locking habituation_profile.json during File.ReadAllText) makes AtomicJsonWriter.TryRead return null (catch-all), so Load() returns null while the file still exists. The next dirty Update tick runs BuildProfileSnapshot with existing=null, writes MedicalAdvisoryShown=null (plus the calibration_* block) into a valid schema_version=2 file, and File.Replace's it over the real profile. HabituationOverrideApplicator.cs:162/169/178 then evaluate MedicalAdvisoryShown==Vestibular as false, so the vestibular relaxation hard-cap silently disappears and the user can one-click accept a barred relaxation. The wiped file is legitimate v2, so the H14 downgrade guard never catches it; the engine fail-closes the read but fails-OPEN the write.

## 3. src/SenseEase.App/ViewModels/MainViewModel.cs:1501

**The dashboard Feedback box is a second self-harm entry point that logs the RAW user text verbatim into the event log even when the safety floor trips, breaking the H2 'never persist the crisis phrase' invariant the chatbot path enforces.**

User types "I want to die" into the dashboard Feedback box. _nlu.InterpretAsync routes through ChatbotNluService -> SafetyFloor.Trips, returning Intent=Unknown/FovDelta=0/Response=crisis, so SubmitFeedbackAsync skips every adjustment branch and unconditionally calls AppendLog("Feedback", $"\"{utterance}\" -> {result.Intent}", ...) at line 1501 with the raw self-harm text. That phrase is stored in the EventLog, rendered verbatim on the dashboard event table (shoulder-surfable/screenshot-leakable), and later re-embedded into the LLM prompt via AppendHistoryPrefix — whereas the chatbot path persists SafetyFloor.RedactedPlaceholder. The same crisis phrase is redacted in one UI and exposed in the other.

## 4. src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs:153

**The H14 downgrade guard rejects any genuine v1 profile whose updated_at_iso parses to >= 2026-06-01, so a user still on a pre-v2 build past that date who upgrades has Load() return null and their calibrated profile + earned offset reset to RESILIENT defaults.**

A user keeps running an older pre-v2 build (which still writes schema_version:1) after 2026-06-01, so their genuine v1 habituation_profile.json carries updated_at_iso like 2026-06-03 and a non-zero HabituationOffsetPp. On first upgrade to the v2 build, schema_floor_v2.flag does not yet exist (first branch false), so IsSchemaDowngrade falls to the date check, sees 2026-06-03 >= V2SchemaShipDateUtc, and returns true; Load() returns null. The engine boots to RESILIENT defaults, _profileSnapshotDirty becomes true, and the next persist tick overwrites the file with a fresh RESILIENT snapshot — permanently discarding the real SENSITIVE/Vestibular calibration and zeroing the Q4.5-protected offset. A future-set system clock triggers the same loss for any v1 file.

## 5. src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs:150

**The H14 date-guard parses updated_at_iso with RoundtripKind then ToUniversalTime(), so a crafted v1 file with a no-offset (or +08:00) post-ship timestamp is reinterpreted below the ship date in positive-UTC timezones, or a blank/malformed timestamp fails the parse — either way bypassing the downgrade refusal and letting a v1 file null-fill MedicalAdvisoryShown.**

On a fresh install (no schema_floor_v2.flag) in Taiwan (UTC+8), an attacker hand-edits the profile to schema_version:1 with "updated_at_iso":"2026-06-01T07:00:00" (no Z). RoundtripKind yields Kind=Unspecified; ToUniversalTime() treats it as local -> 2026-05-31T23:00:00Z < V2SchemaShipDateUtc, so the >= is false and IsSchemaDowngrade returns false (empirically verified on this Taipei machine). Alternatively a blanked/malformed updated_at_iso makes TryParse return false and short-circuit to 'not a downgrade'. The v1 file is then accepted via LoadLegacyV1AsV2InMemory, MedicalAdvisoryShown null-fills, and HabituationOverrideApplicator Rules 1-3 (gated on ==Vestibular) bypass the vestibular hard-cap — the exact downgrade attack H14 is meant to refuse, with the flag arm deletable by the same write-capable actor.

## 6. src/SenseEase.Core/Safety/SafetyFloor.cs:64

**"kms" is matched as a bare substring (normalized.Contains), so the common distance unit kilometres trips the self-harm crisis floor and blocks the user from the chatbot, lexicon, and LLM.**

A motion-sickness user types "I walked 5 kms today and felt dizzy" or "He drives 30 kms to work". normalized contains "kms" -> Trips() returns true with the English crisis-line response, and the ChatbotViewModel line-1 intercept persists RedactedPlaceholder instead of the user's text and returns with no picker/lexicon/LLM. The user is diverted to a suicide hotline and blocked from getting help with the actual symptom they reported — a deterministic false positive on a standard measurement unit, verified by simulating the exact normalize+Contains logic.

## 7. src/SenseEase.Core/Safety/SafetyFloor.cs:53

**The bare Han phrases "想死" and "跳樓" are substring-matched, so common affectionate/commercial idioms trip the zh crisis floor and get redacted to [REDACTED] and routed to the crisis line.**

User types "想死你了，好久不見" ("I missed you so much") or "百貨公司跳樓大拍賣" (a fixed department-store fire-sale idiom). normalized contains "想死"/"跳樓" -> Trips() returns true with CrisisResponseZh; ChatbotViewModel then persists RedactedPlaceholder for the user's message and short-circuits to the crisis line with no picker/lexicon/LLM. An everyday greeting or shopping comment is intercepted as a suicide crisis and the user's real message is discarded, verified by simulation.

## 8. src/SenseEase.App/ViewModels/HabituationOverrideApplicator.cs:153

**H15 fail-closed on null snapshot is too broad: it blocks the benign fresh/never-calibrated user identically to a corrupt file and surfaces a misleading 'please reload' message for a file that was never written.**

A brand-new user (no habituation_profile.json yet, and no wipe-probe artifact) opens the in-chat profile picker — reachable with no calibration gate via TryDetectProfilePickerRequest — and clicks an option. OnChatbotProfilePickerSelected calls ApplyAccepted; SafeLoadSnapshot()/Load() returns null because the file is simply absent, so CheckSafetyIntercepts returns Block("snapshot unavailable; cannot verify safety state — please reload"). The first-time switch is hard-blocked and logged as Intercepted, and the user is shown "⚠ 無法切換到 {canon}：snapshot unavailable... please reload" — told to reload a profile file that was never written. The same null also blocks the LLM-driven switch_profile proposal at apply-time (Processor.Validate skips the rule on null, so it surfaces then dies on Accept).

## 9. src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:818

**No shipped writer ever sets MedicalAdvisoryShown to a non-null value, so the EM2 carry-forward (existing?.MedicalAdvisoryShown) persists null forever and the vestibular safety intercept it feeds never fires.**

A vestibular user completes calibration: ApplyCalibrationResult sets trigger/strike-limit/profile/count but never sets MedicalAdvisoryShown/SymptomDominancePrimary/Calibration*; CalibrationApplicator.Apply only appends an advisory chat bubble, never writing the field to the store. BuildProfileSnapshot then echoes existing?.MedicalAdvisoryShown (null on disk), and LongitudinalObservationWriter preserves null — so MedicalAdvisoryShown is null on disk forever. On every later boot, HabituationOverrideApplicator.CheckSafetyIntercepts Rules 1-3 (gated on ==Vestibular) are always false, so a vestibular-calibrated user can one-click accept a relaxation override the intercept was built to block. EM2's only test asserts LongitudinalObservations; the advisory-preservation claim is untested and currently moot.

## 10. src/SenseEase.Infrastructure/Real/MistralChatbotService.cs:349

**SendAsync adds the raw pureUserText to the rolling _history buffer BEFORE the SafetyFloor.Trips check, so a tripped crisis message's raw phrase is retained in _history (exposed via the public History property) — contradicting the comment 'The LLM is NEVER given this text'.**

User sends "kill myself". Line 349 unconditionally runs _history.Add(User, pureUserText); line 358 then trips the floor and returns the crisis response, but the raw phrase stays in the 20-entry _history list and is exposed via History => _history.AsReadOnly(). CalibrationChatbotService does this correctly (floor returns BEFORE _history.Add), making the two services inconsistent. The leak is in-memory only today because the prompt is rebuilt statelessly rather than replaying _history, but it violates the stated H2 invariant and would forward the crisis phrase to the model the moment any future change replays _history.

## 11. src/SenseEase.Infrastructure/Storage/FileEventLogStore.cs:100

**AuditLogFiles.Append silently drops every forensic line for the rest of the UTC day once the day file hits the 1MB cap, with no rollover file and no truncation marker; post-cap chatbot declines are also lost across restart, breaking the rule-5 cooldown's restart-survival guarantee.**

During a high-volume burst (flapping pressure, profile churn, or a proposal storm) event-log-YYYY-MM-DD.jsonl reaches 1MB; from then until UTC midnight Append's `if (Length >= maxBytes) return;` discards all further lines with no exception and no sentinel. A forensic reader investigating 'my profile flipped overnight' sees a file that just stops mid-day with no signal distinguishing 'nothing happened' from 'cap hit'. Worse, a post-cap chatbot decline lives only in memory; on restart LoadRecent rebuilds _entries only from the capped file, so MostRecentDeclineFor returns null, Processor rule-5 falls through to Accept, and the chatbot re-proposes the intent the user already declined.

## 12. src/SenseEase.Core/Models/EventLogEntry.cs:6

**Forensic JSONL persists EventLogEntry.Timestamp as DateTime.Now (local Kind) plus a redundant computed Time field while the day-file rolls/prunes on DateTime.UtcNow, so a line can land in a UTC-named file whose date disagrees with its own local timestamp.**

For a user at UTC+8 just after local midnight, Timestamp (DateTime.Now) reads 2026-06-06 00:30 local while DateTime.UtcNow is 2026-06-05 16:30, so the line is appended to event-log-2026-06-05.jsonl but serializes a +08:00 next-local-day timestamp. The sibling FileChatbotActionLog logs the same proposal lifecycle in UTC (TimestampUtc.ToString("o")), so a forensic reconstruction of an overnight profile flip sees two audit logs on different clocks and event timestamps that contradict their own filename's UTC date — defeating the durable-anchor purpose. The un-[JsonIgnore]'d computed Time getter also bloats every durable line.

## 13. tests/SenseEase.Tests/ChatbotViewModelSafetyFloorTests.cs:43

**The safety-floor VM test constructs a real ChatbotViewModel whose ctor hardwires FileChatHistoryStore(AppConfig.ChatHistoryPath), so it reads prior real %LOCALAPPDATA% chat history at construction and appends [REDACTED]/crisis lines to the dev's actual file with no teardown — and never asserts the H2 redaction invariant.**

BuildVm -> new ChatbotViewModel(...) loads the real chat_history.jsonl into vm.Messages; a leftover crisis bubble from a prior run pre-sets crisisShown=true, making the assertions order/environment dependent, and every run permanently appends junk to the dev's real file (no IDisposable/cleanup). The test asserts only proposals==0, crisisShown, and !llmReplyShown — never that the persisted user message is RedactedPlaceholder rather than raw text, so changing ChatbotViewModel.cs:319 from RedactedPlaceholder back to `text` would leak raw self-harm utterances into chat_history.jsonl while every test stays green.

## 14. src/SenseEase.Infrastructure/Storage/FileChatbotActionLog.cs:110

**_entries is an uncapped in-memory list: LoadRecent rehydrates up to 7 days x 1MB (~7MB) of JSONL on construction (synchronously on the UI/boot thread), Record never evicts, and MostRecentDeclineFor does an O(n) tail scan on the per-proposal hot path — unlike FileEventLogStore which caps its window.**

A heavy user with several near-1MB day files adds measurable startup latency parsing ~7MB on the WPF boot thread, and a long tray-resident session accrues tens of thousands of ChatbotActionEntry objects with no FIFO soft-cap (which InMemoryChatbotActionLog already flags as needed). MostRecentDeclineFor, wired into Processor rule-5 per proposal, scans the entire ever-growing list whenever the queried intent was never declined. No crash, but unbounded in-session memory plus O(n) scans that grow with uptime; the in-memory window should be capped the way the event-log store is.

## 15. src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:780

**EM2 puts a full synchronous profile-file Load() (File.ReadAllText + JSON deserialize + schema-guard) on the engine's per-tick persist path inside _gate, on every dirty Update, immediately before Save() writes the same file back — work the per-frame path did zero of before.**

During normal playback the dirty predicate trips on any HabituationLevel delta >5pp, strike change, or engine-state flip, so each MaybePersistProfileSnapshot calls _profileStore.Load() while holding _gate (the same lock Update and every pass-through getter take), then Save()s the same file. Under sustained level/strike churn this is repeated whole-file disk reads + JSON deserialization on the inference thread, serialized against all engine reads — a per-frame latency/throughput regression. The engine already holds the relevant state and could cache the last snapshot rather than re-reading on every self-triggered Save. (This Load->mutate->Save also opens a TOCTOU window vs LongitudinalObservationWriter on the shared store, masked today only because both run on the WPF UI thread.)

