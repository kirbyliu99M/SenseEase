# SenseEase — Pillar 2: Voice, Tone & Content Rewrite

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

# Voice & Tone Guide

> **⚠ Chatbot voice is now governed by [docs/CHATBOT_BEHAVIOR_SPEC.md](../CHATBOT_BEHAVIOR_SPEC.md) (2026-06-19 tuning sprint).** The chatbot tuning target is **Qwen3-4B-Instruct-2507** (not Ministral 3B). In §5 below and elsewhere, "Ministral 3B" and `MistralChatbotService.cs` are historical — the class lives in `LocalLlmChatbotService.cs` now. For any chatbot conflict, the behavior spec wins (notably: ZH is clinical/concise per the spec, overriding the warm-你 register §B-3 prescribes for the chatbot specifically).

I have enough grounded evidence. I have the real strings, their file:line locations, the chatbot system prompt, and the design tokens. Now I'll write the Voice & Tone guide.

# Voice & Tone

> This guide governs **every word the user reads** — dashboard labels, the notification Card, calibration questions, the local assistant's replies, tooltips, settings rows, error and block states, and installer copy. It sits one level below the Calm design tokens: where the token spec decides how things *look*, this decides how things *sound*. When a string and a token conflict (e.g. an "alarm-red" word on a muted-clay chip), the **word loses** — the comfort token is the source of truth and the copy must match its register. Authority order is unchanged from the guardrails: Clinical safety (A) > Privacy (B) > Engineering locks (C) > Product semantics (D) > brand preference. Voice is a brand-preference layer and **never** overrides a HARD-STOP (a calmer word may not soften a crisis hotline, hide a blocked proposal, or rename a privacy disclosure away).

---

## 1. Personality

SenseEase speaks like **a calm, well-trained companion who happens to be a clinician** — present, unhurried, quietly competent, and on the user's side. It is the steady voice in a room that has gotten too loud. Picture an occupational therapist or a good nurse on a quiet ward: they never gasp at a reading, never lecture, never gush. They tell you what is happening in plain words, tell you what they are already doing about it, and leave you in control.

Four adjectives, in priority order, with the failure mode each guards against:

1. **Calm** (never alarmist). The product's entire reason to exist is that its users are over-stimulated and sometimes nauseated. Copy that spikes adrenaline is the product failing at its own job. No exclamation marks in status copy, no "Warning!", no urgency theater.
2. **Warm** (never clinical-cold). Plain does not mean robotic. We acknowledge the person — "you're protected," not "protection active." The cold telemetry headline (`VIMS Pressure`, `Leaky Integrator`, `Optical Flow Index` at `MainWindow.xaml:213/226/232`) is the current cardinal sin: it is correct and completely unreassuring.
3. **Competent** (never cute, never vague). Calm and warm without competence reads as a meditation app and loses trust the moment strain actually rises. We are precise about what we sense and what we do. No emoji as load-bearing meaning (retire the `💡` `U+1F4A1` proposal header at `NotificationCardWindow.xaml:145`), no baby-talk, no mascot.
4. **Dignified** (never patronizing). The user is an adult managing a real condition, often a medical one. We never congratulate them for breathing, never "Oops!", never imply fragility. We offer, we don't command; we explain, we don't scold.

**One-line personality test for any string:** *Would this sentence calm a nauseated person who is about to get a migraine, while still sounding like it knows exactly what it's doing?* If it spikes them, talks down to them, or sounds like a dashboard, rewrite it.

---

## 2. Voice principles

**VP-1 — State, don't alarm.** Describe the situation as a fact already being handled, not as an event demanding reaction. The user should never feel summoned. "Easing the edges while this settles" beats "High visual strain detected!" The mask is *already working* by the time we speak — our copy reflects that the help is in motion, not pending.

**VP-2 — Plain language over telemetry.** Engineering terms are for the Advanced/Diagnostics surface only (`Type.Mono`, 13px, per the type scale). The headline a user reads is their *comfort state*, never a sensor reading. `Optical Flow Index`, `VIMS Pressure`, `Leaky Integrator`, `FMS Score`, `FOV Radius`, `Clear zone` (`MainWindow.xaml:213/226/232/239/245/352`) are all telemetry leaking onto the primary surface — they move behind an "Advanced" disclosure or get translated to comfort language. The product term "VIMS" may appear once, defined, never as a recurring label.

**VP-3 — We act; the user stays in control.** SenseEase narrates what *it* is doing ("easing," "steadying," "staying out of your way") and always leaves an honest, reversible choice. We propose, we never seize. The assistant *suggests* (`AI suggests:` prefix, H7); it does not *decide*. Every protective action has a plainly-worded way to soften or pause it — except the clinical floors, which we explain rather than offer.

**VP-4 — Brevity is a kindness.** A nauseated person cannot parse a paragraph. Card body text is one or two short sentences. Notification titles are a phrase, not a sentence. Aim for ≤ 14 words in any status line, ≤ 2 sentences in any card body. The current 60-character bilingual run-on banner (`MainWindow.xaml:188`) is a readability failure for the exact user we serve.

**VP-5 — Reassure with specificity, not platitudes.** "You're fine" is empty; "Things have settled — protection is back to light" is reassuring *because* it's concrete. Competence is what makes warmth believable. Name what changed and what we did about it, briefly.

**VP-6 — Symmetry of honesty.** We are equally plain about good and not-good states. We don't hide strain (that would betray trust and violate A5 — blocked proposals and rising strain are surfaced, never suppressed), and we don't dramatize it. Rising strain is "building," handled, temporary — stated in the same even tone as "steady."

**VP-7 — One idea per string.** Mirrors the design system's "one idea per screen." A label says one thing. A card asks one question or reports one state. The bilingual cram of two languages plus two clauses into a single `TextBlock` (`MainWindow.xaml:188/376/389`) violates this and must be split (see §6).

---

## 3. Word lists

These are **defaults, not a censor**. The avoid-words carry alarm, blame, or cold-clinical affect; the prefer-words carry calm agency. Context can justify an exception, but the burden is on the exception.

### 3.1 Avoid → Prefer (English)

| Avoid (current/typical) | Why it fails | Prefer |
|---|---|---|
| Alert, Warning, Danger | adrenaline; the `AlertBtn` style name and red `#E54747` are retired | (no banner verb needed — state the situation) "Easing now," "Strain is building" |
| Error, Failed, Invalid | blame + alarm | "That didn't go through," "Let's try that again," "Check this field" |
| Abnormal, Critical, Severe (as UI label) | clinical-cold, frightening | "High," "More than usual," "Stronger than before" |
| Detected / Detection (as headline) | surveillance affect | "Noticing," "Picking up," or omit ("Things are busy on screen") |
| High visual strain detected | alarmist telemetry | "Easing the edges while this settles" |
| Mask ACTIVE / Mask inactive (`MainWindow.xaml:270/273`) | shouting caps + jargon | "Easing now" / "Resting — nothing to ease" |
| Pressure, Index, Integrator, Score, Radius | engineering telemetry on primary surface | "Comfort," "Steady / Rising / High," "Clear view" |
| Power Save / 省電 (mis-frames a sensing toggle, `:333`) | wrong mental model (it's pause-sensing) | "Pause sensing," "Take a break" |
| Failed to / Could not / Unable to | cold + negative-leading | lead with the recovery: "Let's try again — …" |
| Are you sure? / This cannot be undone! | scare-prompt | "This clears your earned comfort settings. Keep them?" (honest, not scary) |
| Disabled / Not available | terse, slightly punitive | "Off for your profile — here's why" |
| Should, Must, You need to | commanding, patronizing | "You can," "If it helps," "When you're ready" |
| Optimal / Maximize / Boost | marketing-loud | "Comfortable," "Steady," "Right for you" |
| ! (exclamation in status) | manufactured urgency | (period; let the calm tone carry it) |
| Symptoms, Diagnosis (outside calibration/medical) | over-clinical for everyday UI | "How it felt," "What you noticed" |

**Allowed-with-care:** *VIMS* (product term, define once), *vection* (assistant only, gloss it), *overlay/mask* (acceptable as the named feature, but prefer "the easing" / "the soft edges" in reassurance copy). Clinical terms are fine on the **crisis card** and **medical-advisory** surfaces, where plainness and accuracy outrank softening (A1, A5).

### 3.2 Comfort-state lexicon (the three bands — D2 source of truth)

The 1–10 tier → 3-band mapping is the single source of truth; copy must read each band correctly and pair with the icon + label (never color-only). Map verbs to the comfort tokens, not the old alarm hues:

| Band (tier) | Token | Status word | Plain phrase | Verb when acting |
|---|---|---|---|---|
| Grey (1–5) | `ComfortSteady` `#2F7D6E` | **Steady** | "All steady" / "You're protected" | "resting," "staying light" |
| Amber (6–7) | `ComfortRising` `#9A6B22` | **Rising** | "Strain is building" | "easing the edges," "steadying things" |
| Red (8–10) | `ComfortHigh` `#B65C4E` | **High** | "Strain is high — easing now" | "easing firmly," "holding the edges soft" |
| Idle | `SurfaceMuted`/`TextSecondary` | **Resting** | "Quiet for now" | — |

> Never "Red," "Alarm," "Critical," or caps-lock for the High band. It is **clay, not red** (A5): the word matches the muted hue. The High band still reads as *handled* ("easing now"), not *emergency*.

### 3.3 繁體中文 避免 → 偏好

| 避免 | 失敗原因 | 偏好 |
|---|---|---|
| 警告、危險、警報 | 觸發焦慮 | （直接描述狀態）「正在緩和」「壓力上升中」 |
| 錯誤、失敗、無效 | 指責＋冷硬 | 「沒有送出」「再試一次」「請確認這欄」 |
| 異常、嚴重、緊急（作為標籤） | 臨床冷感、嚇人 | 「較高」「比平常多一些」 |
| 偵測到高度視覺壓力（`NotificationCardWindow.xaml.cs:482`） | 監控感＋警報 | 「畫面動得比較多，正在替你緩和邊緣」 |
| 視覺壓力、壓力指數、強度（標題） | 工程術語 | 「目前狀態」「平穩／上升／較高」 |
| 省電（誤導為節能，實為暫停感測，`:333`） | 心智模型錯誤 | 「暫停感測」「休息一下」 |
| 必須、應該、請務必 | 命令、說教 | 「可以」「需要的話」「準備好就」 |
| 您確定嗎？／無法復原！ | 恐嚇式提示 | 「這會清除你長期累積的舒適設定，要保留嗎？」 |
| 不適、症狀（日常 UI 中） | 過度臨床 | 「剛才的感受」「你注意到的」 |

> 繁中偏好「緩和／平穩／溫和／放鬆」一系；避免「！」與全形驚嘆號在狀態列出現。

---

## 4. How to talk about strain without inducing anxiety

This is the hardest copy in the app: the user is, by definition, already vulnerable when these strings appear. Five rules:

**S-1 — Lead with the action, not the alarm.** The mask is **already easing** by the time the Card speaks (`MaskActive` flips before `ApplyContentToUi` runs). Copy must reflect that help is *in progress*, never that danger is *pending*. Current `NotificationCardWindow.xaml.cs:482` — "偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。" / "High visual strain detected. Activate protection immediately…" — does the opposite: it announces threat and *commands* the already-protected user to act. Rewrite leads with what SenseEase is doing for them.

**S-2 — Strain is temporary and external, never the user's fault.** Frame it as something happening *on screen*, that *passes*. "The screen's been busy — easing the edges until it settles." Never "your strain is high" (locates the problem in the body), never "you're at risk."

**S-3 — Never quantify strain to the user as a rising number.** A climbing `壓力指數 {N}` in the Card title (`NotificationCardWindow.xaml.cs:467`, where pressure can exceed 300) invites the user to *watch a number climb*, which is itself anxiety-inducing and a vection-adjacent fixation. The headline is the **comfort word** (Steady / Rising / High); the raw figure lives only behind Advanced. The tier "N / 10" is acceptable *only if* framed as a calm scale, not a threat meter — prefer the word over the number.

**S-4 — Calm-out, never cut-in (ties to A4).** Copy that accompanies a tier change must read as a gentle settling, matching the ≥300 ms eased mask transition. No "Now activating," no countdown, no flashing language. The words move at the same speed the mask does.

**S-5 — Always pair strain copy with a calm exit.** Every strain message names a way to ease off (pause sensing, soften, "let it settle") — *except* where a clinical floor forbids it (vestibular escape-hatch, A3; the mask floor, A2), in which case we explain the protection plainly rather than offering to remove it: "Kept soft for your profile." Strain copy that offers no agency reads as helpless; strain copy that offers to defeat the clinical floor violates A2/A3.

---

## 5. The local assistant's voice

The assistant runs **fully on-device** (Ministral 3B, Vulkan, loopback — nothing leaves the machine). Its voice is the personality of §1 made conversational, with extra constraints because it generates free text.

**A-1 — It is a calm helper, not a chatbot persona, not a doctor.** No name, no avatar, no personality quirks, no "As an AI…". It refers to the app as "I" sparingly and to actions plainly: "I've eased the edges a little." The system prompt already enforces empathetic, cautious replies and small deltas (`MistralChatbotService.cs:54–58`); the *voice* layer adds: short sentences, no hype, mirror the user's intensity in words as well as deltas.

**A-2 — Acknowledge → act → offer reversal, in that order, in two sentences.** e.g. user: "頭有點暈" → "聽起來有點暈，我把邊緣稍微收一點，應該會穩一些。如果太暗，跟我說就好。" Acknowledge the feeling, state the small concrete action, leave the door open. Never explain the shader math, never name `fov_delta`/`mask_depth` to the user.

**A-3 — Suggestions are framed as offers and are visibly the AI's, not the app's.** Proposal cards carry the literal `AI suggests:` prefix, italic, rationale truncated to 120 chars with injection lines stripped (H7, `NotificationCardWindow.xaml.cs` proposal path). The assistant never speaks in the app's authoritative status voice — it is advisory. Copy: "AI suggests: a slightly smaller clear zone while this video plays." Decline must stay honest and easy (and disable on expiry, D4).

**A-4 — The assistant never touches the clinical floors, and says so plainly when asked.** If a user asks to remove protection a vestibular profile pins, the assistant surfaces the history first and complies only within the floor (system prompt `:94`, advisory=vestibular). Voice: gentle, non-negotiating, never apologetic-to-the-point-of-caving: "Because of what you told us about your balance, I keep a little protection on even when you ask me to clear it — that one's there for your comfort." It does **not** offer a workaround.

**A-5 — Crisis text is never the assistant's (HARD-STOP A1).** If the safety floor fires, the assistant is *bypassed entirely* — fixed hotline text (Taiwan 1925 安心專線, US 988, findahelpline.com) renders on a plain, instant, non-dismissing card with no "AI is thinking," no paraphrase, no "tell me more" CTA. The assistant voice guide explicitly **does not apply** to that surface; do not warm it up, animate it, or route it through the bubble pipeline.

**A-6 — Plain-fallback parity.** When the deterministic lexicon fallback answers instead of the LLM, it uses the **same** calm register — never a curt "Command not recognized." Unknown intent → friendly short reply (system prompt `:79`): "I'm not sure I caught that — want me to ease things a little, or leave them as they are?"

---

## 6. Bilingual register (繁體中文 + English)

SenseEase ships Traditional Chinese and English as **co-equal native languages**, not source + translation. The current pattern — both languages crammed into one `TextBlock` as "中文 / English" (`MainWindow.xaml:186/188/376/385/389`, `NotificationCardWindow.xaml:120`, `CalibrationCardView.xaml:138/141/161`) — fails both audiences: it doubles line length (hurting the nauseated reader, VP-4), forces a slash-reading rhythm native to neither language, and prevents per-language typographic tuning.

**B-1 — Structure: one language per string, selected by locale.** Replace inline "zh / en" runs with resource-keyed strings (`Strings.zh-Hant.resx` / `Strings.en.resx`). Each language gets its own `TextBlock`, its own line-height, its own reflow. The bilingual *slash* form survives **only** in deliberately co-presented spots (e.g. the calibration option labels where seeing both terms aids recall — `頭暈 / Dizziness`) and the single opt-in restart note where the dual disclosure is intentional (`需重新啟動 / Requires restart`, B1).

**B-2 — Write each language idiomatically, then check parity of *meaning*, not words.** 繁中 is not word-glued English. "You're protected" → 「保護中，放心」 (not 「您被保護」, which is stiff passive-voice translationese). "Easing the edges while this settles" → 「正在替你緩和邊緣，等畫面穩下來」. Parity test: a native reader of each should feel the *same calm*, even if the literal words differ.

**B-3 — Register: warm-polite, not formal-stiff, not casual-cute.**
- 繁中: use 「你」 over 「您」 for warmth without distance — the app is a companion, not a bank (current copy mixes 您/你 inconsistently: 您 at `CalibrationCardView.xaml:156`, 你 implied elsewhere; **standardize on 你**). Avoid 書面語 stiffness (「請務必」「敬請」) and avoid 注音/網路 slang. Sentence-final 「就好」「沒關係」「慢慢來」 carry the calm. No 全形驚嘆號 in status copy.
- English: contractions ("you're," "we'll," "didn't") for warmth; sentence case, never Title Case for body; no exclamation in status; American-neutral spelling to match `findahelpline.com` and the 988 reference.

**B-4 — Typography per language.** zh-Hant uses `Microsoft JhengHei UI` / `Noto Sans CJK TC` fallback at the **same 14px body floor** but with looser line-height (CJK needs ≥1.6× vs Latin's 1.4×) — never below the §2 floors. Latin uses the `Segoe UI Variable Text` stack. Diagnostics mono is Latin-only (`Cascadia Code`); never set CJK in mono.

**B-5 — Product/medical terms stay stable across languages.** "VIMS" stays "VIMS" (defined once per language). Hotline strings are **fixed text, language-correct, never localized away** (A1): the Taiwan 1925 安心專線 line shows in zh, the 988 line in en, both always present regardless of locale (a user in distress may read either). Comfort-band words have one locked translation each: Steady = 平穩, Rising = 上升, High = 較高 — never re-coined per screen (D2 single-source rule applies to copy too).

---

## 7. Before / after rewrites (real current strings)

Each cites the live string, the principle it violates, and a bilingual rewrite. Wording is decision-grade; engineers may paste these into the resx once approved.

**1. Notification Card — High-strain body** (`NotificationCardWindow.xaml.cs:482`)
- **Before (zh/en):** "偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。" / *(en analog)* "High visual strain detected. Activate protection immediately or pause your current activity."
- **Violates:** S-1, S-2, VP-1 (alarmist; commands an already-protected user; "detected" surveillance affect; "immediately").
- **After (zh):** 「畫面動得比較多，我正在替你緩和邊緣。需要的話可以先暫停一下。」
- **After (en):** "The screen's been busy — easing the edges while this settles. Take a break if it helps."

**2. Notification Card — Amber body** (`NotificationCardWindow.xaml.cs:481`)
- **Before:** "偵測到中度視覺壓力。建議短暫休息或暗化螢幕。"
- **Violates:** VP-2 ("視覺壓力" telemetry as headline), S-1.
- **After (zh):** 「壓力慢慢上升，我把邊緣稍微收一點。想休息隨時可以。」
- **After (en):** "Strain is building a little — softening the edges to keep it steady. Rest whenever you like."

**3. Notification Card title — telemetry headline** (`NotificationCardWindow.xaml.cs:467`)
- **Before:** "VIMS 視覺壓力　第 {tier} 級 / 10　·　壓力指數 {pressure}"
- **Violates:** VP-2, S-3 (raw unbounded pressure number as the headline a strained user stares at).
- **After (zh):** 「目前狀態：較高 · 正在緩和」 *(band word + action; raw 壓力指數 moves to Advanced)*
- **After (en):** "Right now: High · easing" *(tier "N/10" optional, calm-framed, behind Advanced)*

**4. Dashboard stat cards — telemetry as primary surface** (`MainWindow.xaml:213/226/232/239/245`)
- **Before:** "Optical Flow Index" · "VIMS Pressure" / "Leaky Integrator" · "FOV Radius" / "Clear zone"
- **Violates:** VP-2 (engineering telemetry is the dashboard headline; `#7B8DB0` label also fails contrast).
- **After (primary surface):** the hero reads the comfort state — "You're protected · steady" with a `Status.Chip` (icon+label+`ComfortSteady`). The five telemetry labels move under an **"Advanced"** disclosure rendered in `Type.Mono`, where "Optical Flow Index," "VIMS Pressure," etc. are acceptable for power users.

**5. Mask status row** (`MainWindow.xaml:270/273`)
- **Before:** "Mask inactive" / "Mask ACTIVE" (caps-lock, color-only dot at `:255`)
- **Violates:** VP-1 (shouting caps), D2 (color-only status), §1 (jargon "Mask").
- **After (en):** `StatusChip` — Resting: "Resting — nothing to ease" (moon icon, neutral); Active: "Easing now" (shield/waves icon, `ComfortHigh`).
- **After (zh):** 「目前平穩，未啟動」/「正在緩和」.

**6. Power-Save toggle** (`MainWindow.xaml:333`, `NotificationCardWindow.xaml.cs:448–449`)
- **Before:** "省電" + glyph; card "VIMS · 省電模式中" / "感測已暫停，可點擊「省電」恢復。"
- **Violates:** §3.1 (mis-frames a sensing on/off toggle as energy-saving), VP-2.
- **After (zh):** toggle 「暫停感測」; card 標題「感測已暫停」, body「需要時再打開，我就繼續替你留意。」
- **After (en):** toggle "Pause sensing"; card "Sensing paused — turn it back on whenever you're ready, and I'll keep watch."

**7. First-run welcome banner** (`MainWindow.xaml:186/188`)
- **Before:** "● 歡迎使用 SenseEase / Welcome to SenseEase" + "先跑 60 秒的 setup demo，系統會依您的反應建議個人化預設值 / Run the 60 s setup demo so SenseEase can suggest personalised defaults based on your reaction."
- **Violates:** VP-4 (one 60-char run-on string), VP-7 / B-1 (two languages + two clauses crammed in one `TextBlock`), 您-stiffness.
- **After (zh, locale-split):** title 「歡迎使用 SenseEase」; body 「先看 60 秒的小短片，我就能依你的反應幫你調好預設值。」
- **After (en):** title "Welcome to SenseEase"; body "Watch a 60-second clip and I'll set comfortable defaults from how you react."

**8. Skip / Run-Setup buttons** (`MainWindow.xaml:192/198`)
- **Before:** "Skip" (`#9AB6E0`, fails contrast) / "Run Setup Demo"
- **Violates:** §3.1 register (terse), token-contrast.
- **After (en):** "Not now" (`Btn.Quiet`) / "Start the 60-second setup" (`Btn.Primary`).
- **After (zh):** 「先略過」/「開始 60 秒設定」.

**9. Reset State button** (`MainWindow.xaml:332`, styled `AlertBtn` red)
- **Before:** "Reset State" on the alarm-red `AlertBtn`.
- **Violates:** §1 (jargon), D3 (ambiguous "reset" risks conflating shader-reset with the sacred habituation `_offset`), A5/color (red).
- **After (en):** "Reset visual settings" (`Btn.Quiet` or muted), with a confirm preview: "This returns the easing to its defaults. Your earned comfort progress stays." *(distinct from a separate, clearly-labeled "Delete all my data," which also must not zero `_offset`, D3.)*
- **After (zh):** 「重設視覺設定」+ 確認「這會把緩和效果還原成預設，你長期累積的舒適進度會保留。」

**10. Calibration Q1** (`CalibrationCardView.xaml:156/158`)
- **Before:** "Q1: 整段影片裡，哪個時段最不舒服？" / "Which segment felt worst? Pick one — or 'all fine' if nothing bothered you."
- **Violates:** §3.1 ("不舒服"/"worst" lead with discomfort), B-3 (您-context).
- **After (zh):** 「剛才的短片裡，哪一段你最有感覺？」 副標 「選一個就好；都還好的話也可以選『全程都還好』。」
- **After (en):** "Which part of the clip did you feel most? Pick one — or 'all fine' if none stood out."

**11. Blocked-proposal note (A5 surface)** (`NotificationCardWindow.xaml:163–169`, `#8C2F23`)
- **Before:** *(generic/empty `SafetyNote`)* rendered red.
- **Violates:** A5 (must stay visible, muted-clay not alarm-red, plain reason — never a generic "couldn't do that").
- **After (zh):** ⚠ 「這項建議會把保護調得比你目前設定還低，所以先保留著。」 (icon + `ComfortHigh` clay text, never `#E54747`).
- **After (en):** ⚠ "Holding off on that one — it would lower your protection below what's comfortable for you."

---

## 8. Governance checklist

Before any user-facing string ships, it passes all of:

- [ ] No avoid-word (alert/warning/danger/error/abnormal/critical/detected-as-headline/!) without a justified exception. (§3)
- [ ] Status/strain copy **leads with the action**, frames strain as external + temporary, and never shows a climbing raw number as the headline. (§4, S-1/S-3)
- [ ] Comfort state uses the locked band lexicon (Steady/Rising/High = 平穩/上升/較高) and is paired with icon + label, never color alone. (D2, §3.2)
- [ ] Telemetry terms (OFI / VIMS Pressure / Integrator / FMS / Radius) appear **only** behind Advanced/Diagnostics in `Type.Mono`. (VP-2)
- [ ] One language per string via resx; both read native; co-presented bilingual only where intentional. zh uses 你, warm-polite, no 全形！. (§6)
- [ ] ≤ ~14 words per status line, ≤ 2 sentences per card body. (VP-4)
- [ ] Assistant text = acknowledge → act → offer reversal, ≤ 2 sentences, `AI suggests:` for proposals, never names shader params. (§5)
- [ ] **HARD-STOP untouched:** crisis hotline text is fixed/unlocalized-away/un-warmed; vestibular and mask floors are explained, never offered-around; privacy disclosures (restart note, "nothing leaves your device") are intact and not softened to vagueness. (A1/A2/A3/B1)
- [ ] Destructive/reset copy is unambiguous and never conflates shader-reset with the habituation `_offset` wipe; honest consequence preview, no scare-prompt. (D3)

---

# Copy — Home + Insights (all metric labels, status lines, comfort-state language, event log, session summary)

I now have complete, decision-grade evidence for every user-facing string in Home (Dashboard tab) and Insights (Eye-Protection tab), plus all the dynamic VM strings, event-log categories/messages, and column headers. I have everything needed to write the rewrite section.

---

# Content Rewrite — Home (Dashboard) + Insights (Eye-Protection)

## 0. Scope & evidence base

This section rewrites **every user-facing string** on the two primary read surfaces — the **Home/Dashboard** tab (`MainWindow.xaml:168–480`) and the **Insights/Eye-Protection** tab (`MainWindow.xaml:483–638`) — plus the dynamic strings their ViewModel emits (`MainViewModel.cs`) and the **event-log rows** they render (`EventLogEntry.cs`). It covers status lines, metric labels, comfort-state language, the event log, the session summary, and the **empty / loading / error / permission microcopy that is entirely absent today**.

The driving rule (VP-2, §4 of the Voice guide, D2): the **headline a user reads is their comfort state, never a sensor reading**. Today the dashboard headline is five engineering scalars — `Optical Flow Index` / `VIMS Pressure` / `Leaky Integrator` / `FOV Radius` / `Clear zone` at `MainWindow.xaml:213/226/232/239/245`, rendered as 32px `CardValue` numbers (`:79–84`) with `#7B8DB0` labels that fail contrast (`:75`). The Insights tab repeats the same telemetry plus `VIMS Tier` and `Mask State` (`:495–536`). All of it migrates to human language; the raw scalars survive only behind an **Advanced** disclosure in `Type.Mono` (13px, per the type scale).

---

## 1. Bilingual strategy — RECOMMENDATION

**Adopt: one language per string, locale-selected via `.resx`, with a single in-app language toggle. Retire the inline `中文 / English` cram entirely on these two surfaces.**

### 1.1 Why the current "中文 / English" cram must go (B-1, VP-4, VP-7)

The dashboard crams two languages into single `TextBlock`s in at least six places:
- `MainWindow.xaml:186` — `● 歡迎使用 SenseEase / Welcome to SenseEase`
- `MainWindow.xaml:188` — a **60-character bilingual run-on** (`先跑 60 秒的 setup demo… / Run the 60 s setup demo…`)
- `MainWindow.xaml:376` — `保持開啟 / Always-on Overlay`
- `MainWindow.xaml:385` — `保持遮罩開啟 / Keep overlay visible`
- `MainWindow.xaml:389` — `顯示器周邊維持暗角… / Periphery stays dim…`
- `MainWindow.xaml:483/570/595` — tab + section headers (`護眼儀表板 / Eye Protection`, `VIMS 干預監測 / Interception Monitor`, `本次工作階段 / Session Summary`)

And the VM hard-codes slash-bilingual into runtime strings — `HabituationButtonText` (`MainViewModel.cs:780` `恢復再適應 / Resume`), the habituation log lines (`:603`), the recalibrate button (`:661`). This doubles line length for a nauseated reader (VP-4), forces a slash-reading rhythm native to neither language, and blocks per-language line-height tuning (CJK needs ≥1.6× vs Latin 1.4×, B-4).

### 1.2 The chosen mechanism

| Element | Decision |
|---|---|
| Resource files | `Strings.zh-Hant.resx` (default culture for this market) + `Strings.en.resx`, under `src/SenseEase.App/Resources/`. |
| Binding in XAML | `Text="{x:Static res:Strings.Home_HeroSteady}"` for static labels; for dynamic state, the VM exposes already-localized properties (e.g. `ComfortHeadline`, `ComfortSubline`) so the view binds one string and the **VM owns the resource lookup**. |
| Language toggle | A `ListRow` switch in Settings ("語言 / Language", zh-Hant ⇄ English). On change, set `Thread.CurrentThread.CurrentUICulture` and raise `PropertyChanged` on all localized VM strings (the app already has a `UserPreferences.Changed` broadcast seam — `MainViewModel.cs` subscribes to it, per the Settings comment at `:918–925`). No restart needed for text (only head-tracking needs restart, B1). |
| Typography per language | zh-Hant → `Microsoft JhengHei UI` fallback `Noto Sans CJK TC`, 14px floor, line-height ≥1.6×; Latin → `Segoe UI Variable Text`, 14px floor, 1.4× (Typography §2 / §6 B-4). Diagnostics stay `Cascadia Code`, Latin-only. |
| Co-presented bilingual survivors | **None on Home/Insights.** The slash form survives only in the deliberate spots named elsewhere in the guide (calibration recall labels; the head-tracking restart note `需重新啟動 / Requires restart`, B1). The Home/Insights surfaces are fully locale-split. |

### 1.3 How a rewritten string works under it

Worked example — the mask-status row (`MainWindow.xaml:267–279`, currently `Mask inactive` / `Mask ACTIVE`):

```xml
<!-- View binds ONE already-localized phrase + a StatusChip variant key -->
<controls:StatusChip Variant="{Binding ComfortChipVariant}"
                     Text="{Binding ComfortStatusLine}"
                     AutomationProperties.Name="{Binding ComfortStatusAnnounce}"/>
```
```csharp
// VM resolves locale once; resx holds both languages
public string ComfortStatusLine => MaskActive
    ? Strings.Home_Status_Easing      // en: "Easing now"   zh: 正在緩和
    : Strings.Home_Status_Resting;    // en: "Resting — nothing to ease"  zh: 目前平穩，未啟動
```
```
# Strings.en.resx                 # Strings.zh-Hant.resx
Home_Status_Easing  = Easing now   Home_Status_Easing  = 正在緩和
Home_Status_Resting = Resting — nothing to ease
                                   Home_Status_Resting = 目前平穩，未啟動
```

The view never concatenates languages; line-height, font, and reflow are correct per culture; and the `StatusChip` (§4 component) carries icon + label + color so the state is never color-only (D2).

---

## 2. Information-architecture change (why the table looks the way it does)

The five 32px telemetry cards (`MainWindow.xaml:208–284`) are replaced by **one comfort hero** ("You're protected · steady", `Type.Display` 28/34, `Status.Chip`) plus a collapsed **"Advanced (sensor readings)"** disclosure that keeps the raw numbers for power users in `Type.Mono`. This satisfies VP-2 / governance §8 (telemetry only behind Advanced) while honoring C5 (no behavior change — the same bound properties, just relabeled and re-grouped). The Insights tab keeps richer numbers (it *is* the diagnostics surface) but relabels them to plain language and frames the live chart as "what we noticed," not "VIMS Pressure trajectory."

---

## 3. Before / After — HOME (Dashboard tab)

> Token notes: hero = `Type.Display` on `Surface`/`TextPrimary`; labels = `Type.Caption`/`Type.CaptionStrong` on `TextSecondary` (retires `#7B8DB0`, `:75`); comfort words pair with `Status.Chip` (icon+label, never color-only — retires the bare `Ellipse` dots at `:255`). Advanced readings = `Type.Mono` 13px.

### 3.1 Header & mode badge

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:124` | `OS-level Preventive Visual Comfort Prototype` | `Quiet visual comfort, always on` | `安靜的視覺舒適，隨時在你身邊` | VP-2: drops "OS-level/Prototype" engineering self-description; warm, plain. |
| `:143` mode badge `● DEMO MODE` | `● DEMO MODE` | `Setup preview` | `設定預覽` | §3.1: caps + "mode" jargon → calm noun. |
| `:143` `● SCREEN CAPTURE` (`ModeLabel`, `MainViewModel.cs:682`) | `SCREEN CAPTURE` | `Watching your screen` | `正在留意畫面` | VP-2: "capture" reads as surveillance; reframe as gentle attention. |
| `:143` `CURSOR VELOCITY` (`MainViewModel.cs:683`) | `CURSOR VELOCITY` | `Watching cursor motion` | `正在留意游標移動` | Same; plain language. |

### 3.2 First-run welcome banner (`:177–203`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:186` title | `● 歡迎使用 SenseEase / Welcome to SenseEase` | `Welcome to SenseEase` | `歡迎使用 SenseEase` | B-1 split; drop the `●` glyph (§5 iconography). |
| `:188` body (60-char run-on) | `先跑 60 秒的 setup demo，系統會依您的反應建議個人化預設值 / Run the 60 s setup demo so SenseEase can suggest personalised defaults based on your reaction.` | `Watch a 60-second clip and I'll set comfortable defaults from how you react.` | `先看 60 秒的小短片，我就能依你的反應幫你調好預設值。` | VP-4/VP-7/B-1: split, shortened; 您→你 (B-3). |
| `:192` button | `Skip` (`#9AB6E0`, fails contrast `:194`) | `Not now` | `先略過` | §3.1 register; `Btn.Quiet` fixes contrast. |
| `:198` button | `Run Setup Demo` | `Start the 60-second setup` | `開始 60 秒設定` | Concrete, calm; `Btn.Primary`. |

### 3.3 Status cards → comfort hero (`:208–284`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:213` label | `Optical Flow Index` | **(hero, primary):** `You're protected` · sub `Screen is calm` — **(Advanced):** `Screen motion` | **(主要):** `保護中，放心` · 副 `畫面很平穩` — **(進階):** `畫面動態` | VP-2: telemetry term → comfort hero on primary; raw label moves to Advanced/Mono. |
| `:219` | `/ 150` | (Advanced only) `0–150` | (進階) `0–150` | Scale caption belongs to Advanced. |
| `:226` label | `VIMS Pressure` | **(Advanced):** `Strain building` | **(進階):** `壓力累積` | VP-2; the *primary* read is the comfort band (§3.4), not this number. |
| `:232` label | `Leaky Integrator` | **(Advanced):** `Strain (smoothed)` | **(進階):** `壓力（平滑後）` | VP-2: pure engineering term; demoted + humanized. |
| `:239` label | `FOV Radius` | `Comfort zone` | `舒適範圍` | VP-2: "FOV Radius" → human spatial word. |
| `:245` label | `Clear zone` | `Clear view in the middle` | `中央保持清晰` | Reassuring; states what it *means* to the user. |
| `:252` label | `Current Mode` | `Right now` | `目前狀態` | Drops "Mode" jargon. |
| `:253` value `{Mode}` = `Driver`/`Passenger` (`MainViewModel.cs:717`) | `Driver` / `Passenger` | `Active — lighter touch` / `Watching — full protection` | `操作中 — 輕微緩和` / `觀看中 — 完整保護` | D1: translate Driver/Passenger to plain language; preserve "Driver = lighter, Passenger = full" meaning, never invert. |
| `:270` | `Mask inactive` | `Resting — nothing to ease` | `目前平穩，未啟動` | §1/VP-1; `Status.Chip` Idle (moon icon). |
| `:273` | `Mask ACTIVE` (caps, `#E54747`) | `Easing now` | `正在緩和` | VP-1 (no caps), §1 ("Mask" jargon), D2 (color-only dot → chip); clay not red. |

### 3.4 NEW — comfort-band headline (replaces the telemetry hero)

The single source of truth is the **1–10 tier → 3-band mapping** (`VimsTier`, `MainWindow.xaml:508`, D2). The hero reads the band word, paired with a `Status.Chip`, never a number:

| Band (tier) | Token | New EN hero | New 中文 hero | Status word (chip) |
|---|---|---|---|---|
| Grey (1–5) | `ComfortSteady #2F7D6E` | `You're protected · steady` | `保護中 · 平穩` | Steady / 平穩 |
| Amber (6–7) | `ComfortRising #9A6B22` | `Strain is building · easing the edges` | `壓力上升中 · 正在緩和邊緣` | Rising / 上升 |
| Red (8–10) | `ComfortHigh #B65C4E` | `Strain is high · easing now` | `壓力較高 · 正在加強緩和` | High / 較高 |
| Idle (`ActivityStatus`=Idle) | `SurfaceMuted`/`TextSecondary` | `Quiet for now` | `目前很安靜` | Resting / 休息中 |

> Locked translations (B-5, D2): Steady=平穩, Rising=上升, High=較高 — never re-coined. S-3: no climbing raw number as the headline; the band word leads.

### 3.5 Controls section (`:293–434`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:295` | `Controls` | `Adjust & try` | `調整與體驗` | Plainer section name. |
| `:298` | `Demo & Overlay` | `Setup & easing` | `設定與緩和` | Drops "Overlay" jargon. |
| `:309` | `Calibration & Demo` | `Run the 60-second setup` | `執行 60 秒設定` | Consistent with banner CTA. |
| `:311` tooltip | `Plays a 60 s vection clip, then suggests a personalised preset based on your reaction. Re-running adds to existing preferences.` | `Plays a 60-second clip, then suggests comfort settings from how you react. Running it again builds on what you have.` | `播放 60 秒短片，再依你的反應建議舒適設定。再執行一次會在現有設定上累加。` | "vection/preset" → plain; warm. |
| `:322` | `Stop Demo` (hidden) | `Stop the clip` | `停止短片` | Plain (kept for parity though collapsed). |
| `:332` | `Reset State` (on red `AlertBtn`) | `Reset visual settings` | `重設視覺設定` | §1 jargon, D3 (must not read as wiping progress), A5 (off the red). Confirm preview: EN `This returns the easing to its defaults. Your earned comfort progress stays.` / 中 `這會把緩和效果還原成預設，你長期累積的舒適進度會保留。` |
| `:352` | `FMS Score: ` | (Advanced) `Sickness estimate` | (進階) `不適估計` | VP-2: "FMS" is telemetry; demote to Advanced, humanize. |
| `:355` | `   |   Overlay: ` | `Easing:` | `緩和：` | Drops "Overlay" jargon. |
| `:362` | `Visible` | `On` | `開啟` | Plain. |
| `:359` | `Hidden` | `Off` | `關閉` | Plain. |
| `:376` | `保持開啟 / Always-on Overlay` | `Always-on easing` | `持續緩和` | B-1 split; "overlay" → "easing". |
| `:385` | `保持遮罩開啟 / Keep overlay visible` | `Keep a gentle dim on` (`Toggle.Switch`) | `持續保持柔和暗角` | B-1 split; reassuring framing; bare CheckBox → Switch (§4). |
| `:389` | `顯示器周邊維持暗角；偵測到動態時自動加深 / Periphery stays dim; darkens on detected motion.` | `The edges stay softly dim, and deepen on their own when the screen gets busy.` | `畫面邊緣保持柔和暗角，畫面變忙時會自動加深。` | B-1 split; "detected" surveillance affect removed (§3.1). |
| `:398` | `基準強度 / Baseline intensity` | `Resting dim level` | `基準暗角強度` | B-1 split; plainer. |
| `:407` | `L{0}` (e.g. `L4`) | `Level {0}` | `第 {0} 級` | Drop the `L` shorthand for a readable word. |
| `:414` | `再適應 Habituation Engine` | `Building tolerance` | `逐步適應` | VP-2: "Habituation Engine" → human outcome. |
| `:426` | `再適應進度 Progress: {0}%` | `Tolerance built: {0}%` | `適應進度：{0}%` | Humanize. |
| `:430`/`MainViewModel.cs:780` | `恢復再適應 / Resume` · `暫停再適應 / Pause` | `Resume` · `Pause` | `繼續` · `暫停` | B-1 split (VM owns locale); button text is single-language. |

### 3.6 Event Log (`:441–476`, `EventLogEntry.cs`)

The whole panel is engineering-facing today (columns `Time / OFI / Pressure / FOV Radius / Mask / Action`). Reframe it as a calm, plain **Activity** list. The columns relabel; the per-row `Message` strings (emitted by `AppendLog`, `MainViewModel.cs`) are rewritten to plain language.

**Panel chrome:**

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:448` | `Event Log` | `Recent activity` | `近期活動` | VP-2: "Event Log" → human. |
| `:451` | `Latest {0} entries` | `Last {0} updates` | `最近 {0} 筆` | Warmer noun. |
| `:460` col | `Time` | `When` | `時間` | Plainer header. |
| `:462` col | `OFI` | (move under Advanced toggle) `Screen motion` | `畫面動態` | VP-2; column hidden unless Advanced on. |
| `:464` col | `Pressure` | (Advanced) `Strain` | `壓力` | VP-2. |
| `:466` col | `FOV Radius` | (Advanced) `Comfort zone` | `舒適範圍` | VP-2. |
| `:468` col | `Mask` | `Easing` | `緩和` | §1 jargon. |
| `:470` col | `Action` | `What happened` | `發生了什麼` | Plain, narrative. |
| `:469` `MaskSnapshot` value (`—`/`ON`) | `—` / `ON` | `—` / `On` | `—` / `開` | Sentence case, no caps. |

**Per-row Message strings (live `AppendLog` calls):**

| Location (MainViewModel.cs) | Current Message | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:626` | `SenseEase started, inference loop ready.` | `SenseEase is ready and watching gently.` | `SenseEase 已就緒，正在輕輕留意。` | Warm, plain. |
| `:1341` (mask on) | `Pressure {N} exceeded threshold, FOV mask activated.` | `Screen got busy — eased the edges.` | `畫面變忙了 — 已緩和邊緣。` | S-1/S-2: external + already-handled; drop raw `Pressure {N}` (S-3). |
| `:1343` (mask off) | `Pressure returned below threshold, FOV mask released.` | `Things settled — back to light.` | `已平穩下來 — 緩和恢復輕微。` | VP-5: concrete reassurance. |
| `:1319` | `OFI reached max 150, demo ended.` | `Setup clip finished.` | `設定短片結束。` | Plain. |
| `:986` | `Demo video not found, running synthetic OFI ramp instead.` | `Setup clip unavailable — running a quick built-in preview instead.` | `找不到設定短片 — 改用內建的快速預覽。` | §3.1: "not found" blame → recovery-led. |
| `:1023` | `Demo 影片啟動失敗: {type}: {msg}` | `Couldn't start the clip — let's try that again.` | `短片沒能開始 — 再試一次就好。` | §3.1: lead with recovery, hide stack type from user. |
| `:602/603` | `再適應已暫停 (Habituation paused)` / `再適應已恢復 (Habituation resumed)` | `Tolerance-building paused.` / `Tolerance-building resumed.` | `已暫停逐步適應。` / `已繼續逐步適應。` | B-1 split; humanize "habituation". |
| `:590` | `使用者略過首次校正 / User dismissed first-run setup.` | `Setup skipped for now.` | `先略過設定。` | B-1 split; no blame. |
| `:675/1153` | `感測模式切換失敗: …` / `感測失敗…` (Error category) | `Sensing hiccup — still watching as best I can.` | `感測有點小狀況 — 我會盡量繼續留意。` | §3.1: error→calm, reassure continuity. |
| `EventLogEntry.cs:9` `Category` (`Error`) | `Error` | `Notice` | `提醒` | §3.1: "Error" carries blame/alarm. |
| `Category` `Inference` | `Inference` | `Easing` | `緩和` | VP-2. |
| `Category` `Feedback` | `Feedback` | `You said` | `你說` | Warm. |

> NEW — **empty state** (event log has 0 rows; currently shows a blank table): EN `Nothing to note yet — it's been calm.` / 中 `目前沒有紀錄 — 一切都很平穩。` Rendered `Type.Body` `TextSecondary`, centered, with the Idle `Status.Chip` moon icon.

---

## 4. Before / After — INSIGHTS (Eye-Protection tab)

### 4.1 Tab + live metrics strip (`:483–537`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:483` tab header | `護眼儀表板 / Eye Protection` | `Insights` | `綜覽` | B-1 split (one language per UI); calmer than "儀表板/dashboard". |
| `:495` | `Optical Flow` | `Screen motion` | `畫面動態` | VP-2. |
| `:502` | `VIMS Pressure` | `Strain` | `壓力` | VP-2. |
| `:510` | `VIMS Tier` + value `{N} / 10` | `Comfort level` + `{N} of 10` | `舒適等級` + `{N} / 10` | VP-2; S-3 framing: a calm scale, not a threat meter; pair the number with the band word from §3.4. |
| `:517` | `FOV Radius` | `Comfort zone` | `舒適範圍` | VP-2. |
| `:525/528` | `OFF` / `ACTIVE` (caps, `#E54747`) | `Resting` / `Easing` | `休息中` / `緩和中` | VP-1 (no caps), §1; clay not red. |
| `:535` | `Mask State` | `Easing` | `緩和狀態` | §1 jargon. |
| `:558` `ActivityStatus` `Idle` (`MainViewModel.cs:566`) | `Idle` | `Quiet` | `安靜` | Warmer. |
| `:558` `Suppressed: {reason}` (`MainViewModel.cs:566`) | `Suppressed: {reason}` | `Ignoring window/typing motion` | `略過視窗／打字造成的移動` | VP-2: "Suppressed" jargon → plain explanation of *why* it's calm. |
| `:561` `AutoModeText` `Auto · {Mode} · idle {s}s` (`MainViewModel.cs:575`) | `Auto · Driver · idle 3.2s` | `Auto · Active · quiet 3.2s` | `自動 · 操作中 · 安靜 3.2 秒` | D1: Driver→Active, Passenger→Watching; "idle"→"quiet". |
| `:579` `AutoModeText` `Manual` (`MainViewModel.cs:579`) | `Manual` | `Manual` | `手動` | Keep; localize zh. |

### 4.2 Interception chart (`:567–588`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:570` | `VIMS 干預監測 / Interception Monitor` | `What we noticed` | `我們留意到的` | B-1 split; VP-2: "Interception Monitor" → plain. |
| `:572` | `即時 OFI 與 VIMS Pressure 軌跡;紅色虛線為 mask 啟動閾值 (85)` | `Screen motion and strain over the last little while. The dashed line is where easing begins.` | `最近一小段時間的畫面動態與壓力。虛線是開始緩和的位置。` | VP-2/S-3: drop OFI/VIMS/raw "85"; "red dashed" → neutral "dashed" (the line is no longer red, A5). |
| `:580` legend | `OFI` | `Screen motion` | `畫面動態` | VP-2. |
| `:583` legend | `VIMS Pressure` | `Strain` | `壓力` | VP-2. |
| `:586` legend | `Trigger threshold (85)` | `Easing begins` | `開始緩和` | VP-2/S-3: hide the raw threshold number from primary; lives in Advanced only. |

> Chart styling note (A4/D2): the trigger line moves off alarm-red `#E54747` (`:585`) to a neutral `Separator`/`TextTertiary` dashed stroke; the pressure trace uses `Accent #3A6EA5` (retires `#024AD8`, `:582`), the motion trace uses `TextSecondary`. Under Reduce-Motion the chart renders static and updates on a slow tick (Motion §3.4 item 2) — this is decorative motion, so it may be stilled (the **mask** cross-fade may not, A4).

### 4.3 Session summary (`:592–633`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:595` | `本次工作階段 / Session Summary` | `This session` | `本次使用` | B-1 split; warmer. |
| `:601` | `FOV Intercepts` | `Times we eased in` | `緩和次數` | VP-2: "intercepts" jargon → plain count of help given. |
| `:608` | `Peak Optical Flow` | `Busiest moment` | `畫面最忙的時刻` | VP-2; human. |
| `:615` | `Peak VIMS Pressure` | `Highest strain` | `壓力最高點` | VP-2. |
| `:622` | `Mask Active Time` | `Time easing` | `緩和總時間` | §1 jargon. |
| `:629` | `Session Uptime` | `Watching for` | `已陪伴` | VP-2/warm; "uptime" → human. |
| `SessionMaskActiveText`/`SessionUptimeText` (`MainViewModel.cs:868/873`, `FormatDuration`) | `0m`/`1h 23m` raw | keep numeric format (`1h 23m` / `1 小時 23 分`) | `1 小時 23 分` | Localize the unit suffixes; values unchanged (C5). |

---

## 5. NEW — missing microcopy (empty / loading / error / permission)

None of these states have copy today; each is rendered with the named token/component. All follow §1 register (calm, recovery-led, never `Error!`).

### 5.1 Loading / warm-up

The sensing pipeline **suppresses OFI to 0 for the first 2.0 s** (warm-up, per the capability map / `WgcSensingProvider.cs:203,500`) and the WGC session can take a beat to start. Today the dashboard just shows `0.0` with no explanation.

| State | New EN | New 中文 | Component / token |
|---|---|---|---|
| First 2 s warm-up | `Settling in — getting a feel for your screen.` | `正在熟悉你的畫面，稍等一下。` | Hero `Type.Body` `TextSecondary`, Idle `Status.Chip` (moon), no spinner motion under Reduce-Motion. |
| Chart has < N points | `Gathering a little history…` | `正在累積一點紀錄…` | Chart placeholder, `Type.Caption` `TextSecondary`. |
| Assistant model loading | `One moment — getting ready.` | `稍等一下，正在準備。` | (parity with §5 assistant voice; no "AI is thinking"). |

### 5.2 Empty states

| State | New EN | New 中文 | Component |
|---|---|---|---|
| Event log, 0 rows | `Nothing to note yet — it's been calm.` | `目前沒有紀錄 — 一切都很平穩。` | Centered `Type.Body` + Idle chip. |
| Session summary, fresh session (all zeros) | `Just getting started — your numbers will fill in as you go.` | `剛開始而已 — 數據會隨使用慢慢累積。` | `Card`, `Type.Caption` `TextSecondary`. |
| Insights before any sensing | `Nothing to show yet — start the 60-second setup and I'll begin.` | `目前還沒有資料 — 開始 60 秒設定，我就會開始留意。` | `Card` + `Btn.Primary` CTA. |

### 5.3 Error / degraded

Recovery-led, no stack types surfaced, app keeps reassuring continuity (§3.1, S-5). Replaces the raw `感測模式切換失敗: {type}: {msg}` (`MainViewModel.cs:675`) and `Demo 影片啟動失敗` (`:1023`) currently dumped into the log.

| State | New EN | New 中文 | Token |
|---|---|---|---|
| Screen sensing unavailable (WGC failed) | `Can't watch the screen right now — I'll keep the gentle dim on for you.` | `目前無法留意畫面 — 我會先替你保持柔和暗角。` | Inline `Card.Compact`, `ComfortHigh` clay text + ⚠ glyph (never `#E54747`), A5. |
| Sensing watchdog reset (2 s stuck-session catch, `WgcSensingProvider.cs:259`) | `Reconnected to your screen — all good.` | `已重新連上畫面 — 一切正常。` | Transient toast, `ComfortSteady`. |
| Setup clip missing | `Setup clip isn't here — running a quick built-in preview instead.` | `找不到設定短片 — 改用內建快速預覽。` | `Type.Body`, recovery-led. |
| Assistant offline (llama-server down) | `The assistant is resting — your protection keeps working without it.` | `小幫手暫時休息中 — 你的保護不受影響，照常運作。` | `Card.Compact`, neutral; reassures core still works. |

### 5.4 Permission / privacy (webcam — B1/B3 HARD-STOP)

Head-tracking is **OFF by default**; the webcam never opens until explicit opt-in. These strings appear on Home only as a calm, honest opt-in surface (no dark pattern, no "enable for best experience", restart consequence disclosed — B1).

| State | New EN | New 中文 | Notes |
|---|---|---|---|
| Opt-in row label | `Let the dim follow your gaze (uses your webcam)` | `讓暗角跟著你的視線（會用到網路攝影機）` | `Toggle.Switch`, **off by default**, never pre-checked (B1). |
| Opt-in helper | `Runs entirely on your device — no images are saved or sent. Needs a restart to turn on.` | `完全在你的裝置上運作 — 不會儲存或上傳任何影像。開啟後需要重新啟動。` | Co-presented restart note (`需重新啟動 / Requires restart`) survives here per B1; privacy line must NOT be softened to vagueness (governance §8). |
| Webcam permission denied / camera busy | `Couldn't reach the camera — gaze-follow stays off, everything else keeps working.` | `無法使用攝影機 — 視線跟隨會保持關閉，其他功能照常運作。` | Recovery-led; reassures core unaffected. |
| Active-tracking status affordance (driven by `face_present` scalar only, B3) | `Gaze-follow is on — Windows shows a camera light while it runs.` | `視線跟隨已開啟 — 執行時 Windows 會顯示攝影機指示燈。` | B3: status from scalar only; **no** live preview / face-mesh / "see what it sees". |

---

## 6. Governance check (this section vs §8 checklist)

- **No avoid-words** as headlines: `detected`, `Mask ACTIVE`, `Error`, `Pressure {N}`, `threshold`, `OFF`/`ACTIVE` caps all removed from primary surfaces (§3.1).
- **Strain copy leads with the action**, frames strain as external + temporary, and **no climbing raw number is the headline** — the mask-on log line drops `Pressure {N}` and `VimsTier` is reframed as a calm "{N} of 10" scale (S-1/S-3).
- **Comfort bands** use the locked lexicon Steady/Rising/High = 平穩/上升/較高, always paired with `Status.Chip` icon+label (D2, §3.4) — never the bare `Ellipse` dots at `:255/543/579`.
- **Telemetry** (Screen motion/Strain/Comfort zone/Sickness estimate, formerly OFI/Pressure/Integrator/FOV/FMS) appears **only** behind the Advanced disclosure in `Type.Mono` (VP-2, §2/§3.3).
- **One language per string** via `.resx`; both read native; co-presented bilingual retained **only** for the webcam restart note (B1). zh uses 你, warm-polite, no 全形！ (§6 / B-3).
- **HARD-STOPs untouched:** webcam stays off-by-default with the restart + "nothing leaves your device" disclosure intact and un-softened (B1/B3); the chart trigger line moves off alarm-red to neutral and the High band is clay `#B65C4E` not `#E54747` (A5); no copy here touches the crisis surface, the mask floor, or the habituation `_offset` (the "Reset visual settings" rename explicitly preserves earned progress, D3).
- **Behavior unchanged (C5):** every rewrite is a label/resource swap over the same bound VM properties and the same `AppendLog`/`EventLogEntry` plumbing — no tier thresholds, no `TierColorPalette`, no test-pinned value altered.

**Evidence read:** `src/SenseEase.App/MainWindow.xaml` (full), `src/SenseEase.App/ViewModels/MainViewModel.cs:566–605,665–693,717–780,853–875,1308–1343,1478–1512`, `src/SenseEase.Core/Models/EventLogEntry.cs`.

---

# Copy — AI Assistant + Calibration (chat statuses, Q1-Q5 questions/options, proposals, recalibrate/invite banners, profile picker)

I now have a complete inventory of every user-facing string across the AI Assistant + Calibration surfaces. I have the live strings, their file:line locations, the design tokens, the crisis HARD-STOP text, and the bilingual-cram evidence. Writing the final deliverable.

# AI Assistant & Calibration — Content Rewrite (Voice & Tone Pass)

This section rewrites every user-facing string on the **AI Assistant tab** (`MainWindow.xaml:641–916`), the **in-card chatbot** (`NotificationCardWindow.xaml` + `.xaml.cs`), the **Calibration card** (`CalibrationCardView.xaml`, `CalibrationCardViewModel.cs`), the **profile picker** (`HabituationProposalLexicon.cs:107–115`, `MainViewModel.cs:253–284`), and the **recalibrate / invitation banners**. Every string is paired with its locked Calm token, the principle it currently violates, and a bilingual rewrite ready to paste into `.resx`. Engineering telemetry is translated to human comfort language per the brief: *Optical Flow Index → "Screen motion", VIMS Pressure / Leaky Integrator → "Strain building", Mask active → "Easing your view", FOV Radius → "Comfort zone", Habituation → "Building tolerance", Driver / Passenger → "Active / Watching".*

> **HARD-STOP honored:** the crisis safety-floor strings (`MistralChatbotService.cs:892/904` — Taiwan 1925 安心專線, US 988, findahelpline.com) are **NOT** in any table below. They are fixed text, un-warmed, un-localized-away, never routed through the bubble pipeline (A1, A5). Do not touch them.

---

## 1. Bilingual strategy — RECOMMENDED: single-language + `.resx`, locale-selected

**Decision: replace every inline `中文 / English` cram with one language per string, resource-keyed.** The current pattern crams both languages into a single `TextBlock` or `Content` — e.g. `校正 / Calibration` (`CalibrationCardView.xaml:138`), `頭暈 / Dizziness` (`:191`), `Q1: 整段影片裡…` followed by an English subtitle line (`:156/158`), the welcome banner run-on at `MainWindow.xaml:188`, and `重新校準 / Recalibrate` (`:661`). This fails the exact user we serve: it doubles line length (VP-4 — a nauseated person cannot parse a 60-char slash-run), forces a slash-reading rhythm native to neither language (VP-7), and blocks per-language line-height tuning (CJK needs ≥1.6× vs Latin 1.4×, §6 B-4).

### 1.1 Mechanism
- Two resource files: `Strings.zh-Hant.resx` and `Strings.en.resx`, keyed identically (e.g. `Calib.Q1.Title`, `Chat.Status.Ready`, `Profile.Sensitive.Desc`).
- Resolve at runtime by a single app-level `CurrentUICulture` (default to OS locale, with an in-Settings override). One `TextBlock` per string; each language gets its own `Type.Body` (14/20) block with its own line-height resource (`LineHeight.CJK` = 22 vs `LineHeight.Latin` = 20).
- zh-Hant binds `FontFamily = Microsoft JhengHei UI, Noto Sans CJK TC`; Latin binds the `Segoe UI Variable Text` stack (§6 B-4). Both at the **14px body floor** — never the current 10/11px (`CalibrationCardView.xaml:93/100`, scale sub-labels `:231–338`).

### 1.2 How a rewritten string works under it
```xml
<!-- BEFORE (CalibrationCardView.xaml:156–158): two languages, two clauses, one block -->
<TextBlock Style="{StaticResource CalibTitle}" Text="Q1: 整段影片裡，哪個時段最不舒服？"/>
<TextBlock Style="{StaticResource CalibSubtitle}" Text="Which segment felt worst? Pick one — or 'all fine'…"/>

<!-- AFTER: one resx key, locale picks the language, Type.Heading + Type.Caption tokens -->
<TextBlock Style="{StaticResource Type.Heading}"  Text="{x:Static res:Strings.Calib_Q1_Title}"/>
<TextBlock Style="{StaticResource Type.Caption}"  Foreground="{DynamicResource TextSecondary}"
           Text="{x:Static res:Strings.Calib_Q1_Sub}"/>
```
`Strings.zh-Hant.resx`: `Calib_Q1_Title = 剛才的短片裡，哪一段你最有感覺？`
`Strings.en.resx`: `Calib_Q1_Title = Which part of the clip did you feel most?`

### 1.3 The two intentional exceptions (slash form survives)
Per §6 B-1, keep the co-presented bilingual slash **only** where seeing both terms aids recall or is a deliberate dual disclosure:
1. **Calibration symptom/option labels** where the medical term recall matters (`頭暈 / Dizziness`) — but these move to `Option.Cell` rows with the term as primary and the gloss as secondary, not a slash cram.
2. **The single restart disclosure** for webcam opt-in (`需重新啟動 / Requires restart`, B1) — lives in Settings, not this surface.

Everything in the tables below assumes the resx split; the slash forms shown are illustrative of the *content*, not the final layout (each language renders in its own block).

---

## 2. AI Assistant tab — chat shell, statuses, banners

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `MainWindow.xaml:641` | `AI Assistant` (tab header) | `Assistant` | `小幫手` | §1 personality — "Assistant" is fine; drop the "AI" prefix as a recurring label (it reads as engineering branding). The bubble author already says it's AI (A1, A3). |
| `MainWindow.xaml:642` | tab bg `#E2ECFF` (saturated blue) | — | — | D2/token: re-skin to `Canvas` `#F6F7F9`. Resolves the "two clashing palettes" audit fact (light console vs dark popup). |
| `ChatbotViewModel.cs:50–52` | `您好，我是 SenseEase AI 助理。請描述您的視覺不適（暈眩、噁心、頭痛、眼睛疲勞），我會為您調整防護設定。` / `Hi — I'm the SenseEase AI assistant. Describe any visual discomfort and I'll adjust your overlay protection.` | `Hi — I'm here to help. Tell me how your eyes or head feel, and I'll ease things for you.` | `嗨，我在這裡。跟我說你的眼睛或頭現在怎麼樣，我幫你緩和一下。` | §5 A-1/A-2 (acknowledge→act, warm not clinical), B-3 (你 not 您). Drops the clinical symptom-list lead and "防護設定" jargon; leads with presence. |
| `ChatbotViewModel.cs:935` | `SenseEase AI` (bubble author) | `Assistant` | `小幫手` | §5 A-1 — no chatbot persona name; "Assistant" keeps it advisory and plain. Stays visibly the AI's voice, not the app's status voice (A3). |
| `ChatbotViewModel.cs:935` | `You` (user bubble author) | `You` | `你` | Keep; B-3 你 in zh. |
| `ChatbotViewModel.cs:237` | `Loading Ministral 3B… (first load ~30s on CPU; chat is disabled meanwhile)` | `Getting ready — first start takes about 30 seconds. You can type when it's done.` | `正在準備中，第一次啟動約 30 秒，好了就可以輸入。` | VP-2 (model name "Ministral 3B" is engineering telemetry → Advanced only), VP-4 brevity. Loading microcopy that reassures, not a spec line. |
| `ChatbotViewModel.cs:285` | `Ministral 3B ready` | `Ready when you are.` | `準備好了，隨時跟我說。` | VP-2 — no model name on the primary surface; warm idle state. |
| `ChatbotViewModel.cs:286` | `Lexicon fallback (no GGUF found at: {AppConfig.ModelPath})` | `Ready — running in simple mode.` (path/`GGUF` detail → Advanced/Diagnostics only) | `準備好了，目前用簡易模式。` | §5 A-6 plain-fallback parity; VP-2 ("Lexicon fallback", "GGUF", absolute path = telemetry + a B2 path-leak risk on a user surface). |
| `ChatbotViewModel.cs:238` | `Model not found at: {AppConfig.ModelPath}` | `Running in simple mode — full assistant isn't installed.` (path → Diagnostics) | `目前用簡易模式，完整小幫手尚未安裝。` | §3.1 (no cold "not found" + path), B2 (no absolute path on user surface). |
| `ChatbotViewModel.cs:294` | `Load failed: {ex.GetType().Name}: {ex.Message}` | `Couldn't start the full assistant — simple mode is on, so you can keep going.` (exception → Diagnostics) | `完整小幫手沒能啟動，已切到簡易模式，你可以繼續使用。` | §3.1 (lead with recovery not "failed"), B2 (raw exception text must not surface). |
| `ChatbotViewModel.cs:526` | `[Error] {ex.Message}` (assistant bubble on send failure) | `That didn't go through — give it another try in a moment.` | `剛才沒送出去，等一下再試一次就好。` | §3.1 ("Error"/raw exception → blame+alarm; lead with recovery). |
| `ChatbotViewModel.cs:957` / typing dots `MainWindow.xaml:717` | animated "thinking" dots, `#7B8DB0` | (keep dots; add `AutomationProperties.Name`) `Thinking…` | `思考中…` | §5 A-1; under Reduce-Motion the storyboard becomes a static `…` (§3.4 contract). Screen-reader label currently absent (`AutomationProperties = 0`). |
| `MainWindow.xaml:882` (`StatusText` host) `#7B8DB0`, 11px | (binds the above statuses) | — | — | Type.Caption (12) floor + `TextSecondary` `#525B6B` (7.4:1) — the current `#7B8DB0` 11px fails contrast (~3.6:1) and the 10/11px floor. |
| `MainWindow.xaml:909` | `Send` | `Send` | `送出` | Keep EN; localize zh. |
| `MainWindow.xaml:912` | `Clear` | `Clear chat` | `清除對話` | §3.1 — "Clear" alone is ambiguous (clears chat, not settings). D3 disambiguation: this must never read as "reset settings". `Btn.Quiet`. |
| `MainWindow.xaml:899` input `TextBox` (no placeholder today) | *(empty)* | placeholder: `Tell me how it feels…` | `跟我說你的感覺…` | **Missing microcopy** — input has no placeholder. Adds a calm, inviting prompt (VP-5). `Type.Body`, `TextTertiary` placeholder via `Field.Text` adorner (retire VisualBrush hack). |
| Chat history (empty, first run) — **none exists today** | *(blank scroll area)* | empty-state: `Nothing here yet. When something feels off — dizzy, queasy, tired eyes — just say so.` | `這裡還是空的。覺得不對勁（頭暈、想吐、眼睛累）時，跟我說一聲就好。` | **Missing empty-state.** VP-5 reassure-with-specificity; gives the user permission to speak first. Renders in `TextSecondary`, centered, `Type.Body`. |
| `MainWindow.xaml:661` | `重新校準 / Recalibrate` (slash cram) | `Redo setup` | `重新設定` | §6 B-1 split; §3.1 — "Recalibrate" is engineering. `Btn.Quiet` or `Btn.Ghost`. |
| `MainWindow.xaml:665` (ToolTip) | `Open the LLM calibration dialog without requiring a fresh demo.` | `Answer a few quick questions again — no need to re-watch the clip.` | `再回答幾個小問題就好，不用重看短片。` | VP-2 ("LLM calibration dialog" telemetry), §3.1 warm. |

### 2.1 Invitation banner (`MainWindow.xaml:831–862`) — dark card on light dashboard (palette clash)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:841` (title) | `想更新你的設定嗎？跟系統聊聊剛才的感覺。` | `Want to update your settings?` | `想更新你的設定嗎？` | VP-7 one idea per line; splits the run-on. Drops "跟系統" (cold). |
| `:844` (subtitle, `#9AB6E0` fails contrast) | `Want to refresh your settings? Chat about what you just felt.` | `Tell me how the clip felt and I'll tune things.` | `跟我說短片的感覺，我幫你調一下。` | Contrast: `#9AB6E0` ~3.6:1 → `TextSecondary`. §6 B-2 idiomatic, not glued. |
| `:850` | `跳過 / Skip` | `Not now` | `先略過` | §3.1 register (terse "Skip" → warmer "Not now"); B-1 split. `Btn.Quiet`. |
| `:856` | `開始 calibration / Start calibration` | `Start` | `開始` | §6 B-1 — drop the mixed-language "calibration"; the title already says what it is. `Btn.Primary`. |
| `:833` banner `Background="#0E1320"` (dark) on `#E2ECFF` dashboard | — | — | — | **Palette clash fix** (the documented "dark cards on a light dashboard" audit fact): re-skin to `Card` (`Surface` `#FFFFFF`, `Elev.2`, no `#3D5AFE` colored border). |

---

## 3. In-card chatbot & notification body (`NotificationCardWindow`)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `NotificationCardWindow.xaml.cs:467` (title) | `VIMS 視覺壓力　第 {tier} 級 / 10　·　壓力指數 {pressure}` | `Right now: {Steady\|Rising\|High} · easing` | `目前狀態：{平穩\|上升\|較高} · 正在緩和` | **S-3 (critical):** the raw unbounded `壓力指數` (>300, `:464`) as the headline invites a strained user to watch a number climb. Replace with the locked band word (D2). Raw figure → Advanced only (VP-2). |
| `xaml.cs:480` (Grey body) | `目前視覺壓力低。` | `All steady — nothing to ease right now.` | `目前都很平穩，沒什麼需要處理的。` | §3.2 Steady lexicon; VP-2 (no "視覺壓力" telemetry headline). |
| `xaml.cs:481` (Amber body) | `偵測到中度視覺壓力。建議短暫休息或暗化螢幕。` | `Strain is building a little — softening the edges to keep it steady. Rest whenever you like.` | `壓力慢慢上升，我把邊緣稍微收一點。想休息隨時可以。` | S-1 (lead with action), §3.1 (retire "偵測到" surveillance + "視覺壓力" telemetry). Strain copy pairs a calm exit (S-5). |
| `xaml.cs:482` (Red body) | `偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。` | `The screen's been busy — easing the edges while this settles. Take a break if it helps.` | `畫面動得比較多，我正在替你緩和邊緣。需要的話可以先暫停一下。` | **S-1/S-2 (cardinal fix):** current copy announces threat + *commands* an already-protected user ("立即"/"immediately"). Lead with what SenseEase is doing; frame strain as external + temporary; clay not red. |
| `xaml.cs:448` (Power-Save title) | `VIMS · 省電模式中` | `Sensing paused` | `感測已暫停` | §3.1 — "省電/Power Save" mis-frames a sensing toggle as energy-saving (wrong mental model); VP-2 drop "VIMS". |
| `xaml.cs:449` (Power-Save body) | `感測已暫停，可點擊「省電」恢復。` | `Turn it back on whenever you're ready, and I'll keep watch.` | `需要時再打開，我就繼續替你留意。` | §3.1; VP-3 (user in control, calm exit). |
| `xaml:145` (proposal header) | `💡 SenseEase 建議` (`U+1F4A1`) | `AI suggests` (Lightbulb `Icon.*`, not emoji) | `AI 建議` | §1 (retire load-bearing emoji), A3 (literal `AI suggests:` prefix, visibly the AI's, not the app's authoritative voice). Fluent `Lightbulb` icon per §5. |
| `xaml:163–169` (`SafetyNote`, `#8C2F23`) | *(generic / empty)* rendered near-red | ⚠ `Holding off on that one — it would lower your protection below what's comfortable for you.` | ⚠ `這項建議會把保護調得比你目前設定還低，所以先保留著。` | **A5 HARD-STOP:** blocked proposals stay visible, plain reason, `ComfortHigh` muted clay `#B65C4E` (never alarm red `#E54747`), warning glyph — never a generic "couldn't do that". |
| `xaml:178` | `接受` (Accept) | `Accept` | `接受` | A3 — keep honest; localize. `Btn.Primary`. |
| `xaml:188` | `拒絕` (Decline) | `No thanks` | `不用了` | §3.1 warmer; D4 — Decline must **disable on expiry**, not stay visually active. `Btn.Quiet`. |
| `xaml:198` | `稍後` (Later) | `Later` | `稍後` | Keep; localize. |
| `xaml:210` (`ProposalExpiryText`, 10px) | *(countdown, e.g. "15s")* | `Expires in {n}s` | `{n} 秒後失效` | D4 — calm expiry, not a threat countdown; 10px → `Type.Caption` (12). S-4 no flashing. |
| `xaml:237` (eyebrow, 11px) | `視覺不適程度` | `How much are you feeling it?` | `現在的感受有多明顯？` | §3.1 ("不適/discomfort" leads with the problem; frame as a calm question), 11px → `Type.CaptionStrong` (12). |
| `xaml:260/274/288/302` (four-hue ramp) | `輕微` / `中等` / `明顯` / `嚴重` with blue→amber→orange→red bg (`:253/267/281/295`) | `A little` / `Some` / `A lot` / `Strong` | `一點點` / `有些` / `明顯` / `很強` | §3.2 / Scale.Segmented — replace the **categorical-alarm** four-hue ramp with a single-hue sequential ramp (`ComfortSteadyWeak → ComfortHigh`); never blue→amber→orange→alarm-red. "嚴重/Severe" softened (avoid clinical-cold). |
| `xaml:343` | `開啟主視窗` (Open dashboard) | `Open dashboard` | `開啟主視窗` | Keep; localize EN. `Btn.Primary`. |
| `xaml:347` | `啟動保護` (Engage, orange `#FF9B6B`) | `Ease now` | `現在緩和` | S-1 — "啟動保護/Activate protection" commands; the mask is *already* easing. Re-skin orange `#FF9B6B` → `Accent`/`ComfortHigh` per token. |
| `xaml:357` | `6 小時內不再提示` (Mute) | `Quiet for 6 hours` | `6 小時內保持安靜` | D4 — mute is band-scoped + in-memory; copy must not imply it persists across restarts. §3.1 warmer than "不再提示". |
| `xaml:333` (toggle label) | `省電` | `Pause sensing` | `暫停感測` | §3.1 — re-frame energy-saving → pause-sensing (the actual semantic). |
| `xaml:328`/`120` (ToolTips) | `暫停 / 恢復 SenseEase 感測（不會關閉應用程式）` / `設定 / Settings` | `Pause or resume sensing (the app stays open)` / `Settings` | `暫停或恢復感測（應用程式不會關閉）` / `設定` | §6 B-1 split; keep the honest "stays open" reassurance. |
| `xaml:400` (placeholder) | `跟 SenseEase 說話` | `Tell me how it feels…` | `跟我說你的感覺…` | Consistency with §2 input; warmer than "說話". `Field.Text` adorner (retire VisualBrush). |
| `xaml:417` | `送出` (Send) | `Send` | `送出` | Keep; localize. |
| `xaml:423` (`ChatStatusText`, 10px `#7A8499`) | *(one-line ack)* | (binds ack) | — | 10px → `Type.Caption` (12), `TextSecondary`. |
| `ChatbotViewModel.cs:789` | `⚙ 已重設所有個人化設定 / Overlay settings reset to default` | `Your easing settings are back to default. Your earned comfort progress stays.` | `緩和設定已還原成預設；你長期累積的舒適進度會保留。` | **D3 HARD-STOP:** must make explicit the reset does **not** zero the sacred habituation `_offset`. Current copy ("已重設所有個人化設定"/"all") is dangerously ambiguous. |
| `ChatbotViewModel.cs:790/856` | `（開啟 Overlay 以查看…）` / `_開啟 Overlay 以查看…_` | `Open the overlay to see it.` | `打開暗角效果就能看到。` | §1 jargon "Overlay" → "the overlay / soft edges" in reassurance copy (allowed-with-care, §3.1). |
| `ChatbotViewModel.cs:852` | `**系統調整 / System adjustment**` | `Adjusted for you` | `已為你調整` | §3.1 — "System adjustment" is cold-clinical; warm it. The per-row deltas (`FOV +5%`, `Mask depth`) are telemetry → keep terse but `Type.Mono` Advanced register. |

---

## 4. Calibration card (`CalibrationCardView.xaml` + VM)

Telemetry-translation note: calibration is the one place where **symptom/medical terms are appropriate** (it is a structured intake, §3.1 "clinical terms fine on medical-advisory surfaces"), but the *framing* must stay calm and lead with "how it felt," never "worst / discomfort" (§3.1, rewrite #10).

### 4.1 Header & progress

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:138` | `校正 / Calibration` (badge) | `Quick setup` | `快速設定` | §3.1 — "Calibration/校正" is engineering; B-1 split. `Badge.Accent`. |
| `:141` | `請依您剛才看完 demo 的感受作答 / Answer based on how the demo felt` | `A few questions about how the clip felt.` | `幾個關於剛才短片感受的小問題。` | §6 B-1/B-3 (split, 你, drop 您); VP-4. |
| `:145` (`CardCounterText`) `CalibrationCardViewModel.cs:66` `"{i} / {CardCount}"` | `3 / 5` | `Step 3 of 5` | `第 3 步，共 5 步` | Use `Progress.Dots` (`AutomationProperties.Name = "Step 3 of 5"`) — currently a bare number with no screen-reader context. |

### 4.2 Q1 — segment (`:154–180`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:156` (title) | `Q1: 整段影片裡，哪個時段最不舒服？` | `Which part of the clip did you feel most?` | `剛才的短片裡，哪一段你最有感覺？` | §3.1 rewrite #10 — "最不舒服/worst" leads with discomfort; "有感覺/felt most" is dignified, not alarming. Drop "Q1:" engineering prefix. |
| `:158` (sub) | `Which segment felt worst? Pick one — or 'all fine' if nothing bothered you.` | `Pick one — or "all fine" if none stood out.` | `選一個就好；都還好的話也可以選「全程都還好」。` | §6 B-2 idiomatic; B-1 split. |
| `:161` | `開頭 / Start (first 1/3)` | `The start` | `開頭` | B-1 split; drop "(first 1/3)" engineering precision (VP-4). |
| `:165` | `中段 / Middle` | `The middle` | `中段` | B-1 split. |
| `:169` | `結尾 / End (last 1/3)` | `The end` | `結尾` | B-1 split. |
| `:173` | `全程都還好 / All fine throughout` | `All fine throughout` | `全程都還好` | B-1 split; keep the reassuring "all fine" option. |
| `:178` (all freetext labels `:178/206/358/387/422`) | `補充 (選填) / Notes (optional)` | `Anything to add? (optional)` | `想補充的話？（選填）` | B-1 split; §3.1 warmer. `Field.Text` placeholder, not a VisualBrush hack. |

### 4.3 Q2 — symptoms (`:184–208`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:186` | `Q2: 您主要感覺到哪些症狀？(可複選)` | `What did you notice? (pick any)` | `你注意到哪些感覺？（可複選）` | §3.1 — "症狀/symptoms" over-clinical for the question framing (rewrite uses "感覺/notice"); B-3 你; drop "Q2:". |
| `:188` (sub) | `Which symptoms did you notice? Multi-select.` | `Choose any that fit.` | `符合的都可以選。` | VP-4; B-2. |
| `:191` | `頭暈 / Dizziness` | `Dizziness` | `頭暈` | `Option.Cell` row, term as primary + gloss secondary (the medical-recall exception, §1.3). |
| `:193` | `想吐 / Nausea` | `Nausea` | `想吐` | Same. |
| `:195` | `眼睛疲勞 / Eye strain` | `Tired eyes` | `眼睛疲勞` | §3.1 — "Eye strain" → softer "Tired eyes" in EN; zh keeps clinical term (intake). |
| `:197` | `頭痛 / Headache` | `Headache` | `頭痛` | Keep. |
| `:199` | `平衡感差 / Imbalance` | `Off-balance` | `平衡感差` | §3.1 warmer EN. |
| `:201` | `方向感混亂 / Disorientation` | `Disoriented` | `方向感混亂` | Keep. |
| `:203` | `都沒有 / None of the above` | `None of these` | `都沒有` | B-1 split. |

### 4.4 Q3 — intensity 1–5 (`:211–360`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:214` (title) | `Q3: 您感受的整體強度？(必選)` | `Overall, how strong was it?` | `整體來說，感覺有多強？` | §3.1 B-3 (你, drop 您); drop "Q3:" + "(必選)" — replace with non-blocking "Required to continue" guidance only if user tries to skip. |
| `:216` (sub) | `Overall intensity (1 = none / 5 = severe). Required to continue.` | `1 = didn't notice it · 5 = very strong` | `1＝幾乎沒感覺 · 5＝非常強` | §3.1 — "severe" clinical-cold; recast scale ends warmly. The Scale.Segmented sub-labels move off the failing 10px `#7B8DB0` (`:231–338`) to `Type.Caption` (12) `TextSecondary`. |
| `:231` | `無 / None` (cell 1, 10px) | `Barely` | `幾乎沒有` | §2 type floor (10px→12); §3.1 warmer. Single-hue Scale.Segmented ramp, not the `#024AD8` flat selected fill. |
| `:258` | `輕微 / Mild` (cell 2) | `A little` | `一點點` | Same. |
| `:285` | `中等 / Moderate` (cell 3) | `Some` | `中等` | Same. |
| `:312` | `明顯 / Strong` (cell 4) | `A lot` | `明顯` | Same. |
| `:338` | `嚴重 / Severe` (cell 5) | `Very strong` | `非常強` | §3.1 — "Severe/嚴重" alarming as a scale cap; "Very strong" is honest without dread (S-2). |

### 4.5 Q4 — baseline comparison (`:363–389`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:366` (title) | `Q4: 跟您平常看的影片相比，這次感受如何？` | `Compared with videos you usually watch, how was this?` | `跟你平常看的影片比，這次感覺如何？` | B-3 (你); drop "Q4:". |
| `:368` (sub) | `Compared with the video content you usually watch, how did this feel?` | `Just your sense of it — no wrong answer.` | `憑感覺就好，沒有標準答案。` | VP-5 reassure; removes the redundant restatement. |
| `:371` | `比平常更不舒服 / Worse than usual` | `Harder than usual` | `比平常更有感覺` | §3.1 — "更不舒服/Worse" leads with discomfort → "Harder/更有感覺" (S-2 external framing). |
| `:375` | `跟平常差不多 / Similar to usual` | `About the same` | `跟平常差不多` | B-1 split. |
| `:379` | `比平常還好 / Better than usual` | `Easier than usual` | `比平常還輕鬆` | B-1 split. |
| `:383` | `我平常不看這類動態畫面 / I don't usually watch this kind of content` | `I don't usually watch this kind of thing` | `我平常不太看這類動態畫面` | B-1 split; VP-4 trim. |

### 4.6 Q5 — risk factors (`:392–424`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:395` (title) | `Q5: 以下是否有符合您的長期狀況？(可複選)` | `Any of these apply to you? (pick any)` | `以下有符合你的狀況嗎？（可複選）` | §3.1 B-3 (你); drop "Q5:" + "長期狀況" stiffness. This is the clinical-intake exception, so terms stay precise. |
| `:397` (sub) | `Any of these long-term risk factors? Multi-select.` | `This helps me set safer defaults. Skip any you'd rather not share.` | `這能幫我設定更安全的預設值；不想分享的可以略過。` | VP-5 (say *why* you ask) + B2/privacy reassurance for health-adjacent data (the vestibular answer drives a medical advisory). |
| `:400` | `容易暈車 / 暈船 / Motion sickness` | `Motion sickness (cars, boats)` | `容易暈車或暈船` | B-1 split. |
| `:403` | `偏頭痛 / Migraine` | `Migraine` | `偏頭痛` | Keep. |
| `:406` | `有前庭/內耳相關診斷 / Diagnosed vestibular condition` | `A diagnosed inner-ear or balance condition` | `有前庭／內耳相關診斷` | Keep precise (drives the vestibular floor, A3); B-1 split. |
| `:409` | `使用 VR 容易不適 / VR makes me feel sick` | `VR makes me feel unwell` | `用 VR 容易不舒服` | §3.1 slightly softer; B-1 split. |
| `:412` | `不明原因頭暈 / Unexplained dizziness` | `Dizziness with no clear cause` | `不明原因的頭暈` | B-1 split. |
| `:415` | `都沒有 / None of the above` | `None of these` | `都沒有` | Consistency with Q2. |
| `:418` | `跳過不答 / Prefer not to share` | `Prefer not to say` | `不想回答` | B2 dignity — honest opt-out, not framed as skipping a duty. |

### 4.7 Footer buttons (`:437–462`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:438` | `取消 / Cancel` (styled `CalibAlertBtn`, `#7B8DB0`) | `Cancel` | `取消` | B-1 split; `Btn.Quiet`. Retire the misnamed "Alert" button style. |
| `:441` | `上一題 / Back` | `Back` | `上一步` | B-1 split; `Btn.Ghost`. |
| `:445` | `下一題 / Next` | `Next` | `下一步` | B-1 split; `Btn.Primary`. |
| `:461` | `完成 / Complete` | `Done` | `完成` | B-1 split; warmer than "Complete". `Btn.Primary`. |

### 4.8 Calibration dismissed / abandoned (`MainViewModel.cs:1232`)

| Location | Current string | New EN | New 中文 | Why |
|---|---|---|---|---|
| `:1232` | `校正卡已關閉（未完成 Q3，無紀錄）/ Card dismissed before Q3 — no profile change.` | `Setup closed — nothing changed.` | `設定已關閉，沒有任何變動。` | §3.1 — internal log copy if ever surfaced; plain, no engineering ("Q3", "profile"). VP-5 reassures nothing was lost. |

---

## 5. Profile picker (`HabituationProposalLexicon.cs:107–115`, `MainViewModel.cs`)

The picker renders `Name` (bold `#1E5BB0`) + `Description` per card (`MainWindow.xaml:799–807`). Today the canonical `Name` shown is the **raw enum** (`RESILIENT` / `TYPICAL` / `SENSITIVE`) — engineering identifiers leaking to the user. Map to comfort-language display names; keep the enum as the value sent back (`OnProfileSelected` uses `p.Name` at `ChatbotViewModel.cs:1025`, so the **display name** and the **value** must be decoupled — add a `DisplayName` field, send the canonical enum).

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `ChatbotViewModel.cs:330` (prompt) | `請選擇您想切換的 profile：` | `Pick the comfort level that fits you:` | `選一個適合你的舒適程度：` | §3.1 B-3 (你); VP-2 ("profile" → "comfort level"). |
| `ChatbotViewModel.cs:331` | `目前是 {currentProfile}。請選擇您想切換的 profile：` | `You're on {current} now. Pick another if you'd like:` | `你目前是「{current}」。想換的話選一個：` | VP-3 (offer, not command); B-3. |
| `ChatbotViewModel.cs:325` | `{canonical}（目前）` | `{name} (current)` | `{name}（目前）` | Display name not raw enum; keep "(current)" marker. |
| `Lexicon.cs:110` Name `RESILIENT` | `RESILIENT` | `Easygoing` | `較放鬆` | §3.1 — raw enum on user surface; comfort-language display name (value stays `RESILIENT`). |
| `Lexicon.cs:111` desc | `為高耐受族群設計。視覺保護觸發較緩慢、回復較快。適合對視覺刺激較不敏感的使用者。` | `Eases in slowly and lifts quickly. Good if screen motion rarely bothers you.` | `緩和得慢、回復得快。適合不太受畫面動態影響的你。` | VP-2 (translate "觸發/回復" engineering rhythm to plain); VP-4 trim; B-3. |
| `Lexicon.cs:112` Name `TYPICAL` | `TYPICAL` | `Balanced` | `平衡` | §3.1 comfort display name. |
| `Lexicon.cs:113` desc | `一般使用者預設。反應速度與保護強度間取得平衡，適合多數人的日常使用。` | `A balance of speed and protection — a good fit for most people.` | `在反應速度和保護之間取得平衡，適合大多數人的日常。` | VP-4; B-2 idiomatic. |
| `Lexicon.cs:114` Name `SENSITIVE` | `SENSITIVE` | `Gentle` | `更溫和` | §3.1 — "Sensitive" can read as labeling the *user*; "Gentle/溫和" describes the *protection* (S-2 external framing). |
| `Lexicon.cs:115` desc | `為敏感族群設計。視覺保護更早觸發、保持時間更長。適合容易頭暈、疲勞的使用者。` | `Eases in earlier and stays longer. Good if you tire or get dizzy easily.` | `更早開始緩和、維持得更久。適合容易頭暈、疲勞的你。` | VP-2/VP-4; B-3. |
| `MainViewModel.cs:274` (confirm) | `已切換到 {canon} profile。` | `You're on {DisplayName} now.` | `已切換到「{DisplayName}」。` | §3.1 — use display name not enum; warm. |
| `MainViewModel.cs:258` (unknown) | `未知 profile：{profileName}。` | `I didn't recognize that one — pick from the list?` | `我認不出那個，從清單選一個好嗎？` | §3.1 (no cold "未知/unknown"); §5 A-6 friendly fallback. |
| `MainViewModel.cs:278` (blocked) | `⚠ 無法切換到 {canon}：{result.Reason}` | `⚠ Keeping your current level — {plain reason}. It protects your comfort.` | `⚠ 先保留你目前的程度，{plain reason}。這是為了你的舒適。` | **A5 HARD-STOP** — block reason stays visible, `ComfortHigh` clay + ⚠, plainly explained (e.g. vestibular cap), never a generic failure. The vestibular floor is explained, never offered-around (A4). |
| `MainViewModel.cs:283` (error) | `切換失敗：{result.Reason ?? "unknown"}` | `That didn't go through — try again in a moment.` | `剛才沒切換成功，等一下再試一次。` | §3.1 (lead with recovery, not "失敗/failed"); raw reason → Diagnostics. |

> **Display-name decoupling (engineering note):** add `DisplayName` to `ProfileOption` (`ChatbotViewModel.cs:1038`) and `ProfileDescriptions`; the click handler must still send the canonical enum (`RESILIENT`/`TYPICAL`/`SENSITIVE`) to `HabituationOverrideProcessor` so D2 tier mapping and the vestibular cap (`CalibrationProcessor.cs:95`) are untouched (C5 — behavior unchanged, visual/copy only).

---

## 6. Missing microcopy to add (empty / loading / error / permission)

These states are **absent today** and must ship with the rebrand (governance §8 — every state legible and calm):

| State | Surface | New EN | New 中文 (Traditional) | Notes |
|---|---|---|---|---|
| **Empty chat (first run)** | AI Assistant history (`MainWindow.xaml:676`) | `Nothing here yet. When something feels off — dizzy, queasy, tired eyes — just say so.` | `這裡還是空的。覺得不對勁（頭暈、想吐、眼睛累）時，跟我說一聲就好。` | Centered `Type.Body` `TextSecondary`; VP-5. |
| **Model loading** | status bar | `Getting ready — about 30 seconds. You can type when it's done.` | `正在準備中，大約 30 秒，好了就能輸入。` | Replaces `ChatbotViewModel.cs:237` Ministral-3B line. Send button disabled, no spinner motion under Reduce-Motion. |
| **Simple mode (LLM unavailable)** | status bar | `Ready — running in simple mode.` | `準備好了，目前用簡易模式。` | §5 A-6 parity. Path/GGUF detail → Advanced only (B2). |
| **Send failed** | assistant bubble | `That didn't go through — give it another try in a moment.` | `剛才沒送出去，等一下再試一次就好。` | Replaces `[Error] {ex.Message}` (`:526`). |
| **Reply timeout / model busy** | assistant bubble (new) | `Still thinking — this one's taking a bit. Hang on, or try a shorter message.` | `還在想，這題比較久一點。稍等一下，或換句短一點的也行。` | VP-1 calm, no alarm; gives an out. |
| **Input too long (240 cap, `xaml:393`)** | inline hint (new) | `That's a bit long — try a shorter note.` | `有點長了，短一點比較好喔。` | §3.1 — not "Invalid/Max length exceeded". |
| **Webcam permission (head-tracking opt-in)** | Settings caption (referenced from chat advisory) | `Uses your webcam locally — nothing leaves your device. Needs a restart.` | `在本機使用你的攝影機，所有資料都不會離開裝置。需要重新啟動。` | **B1 HARD-STOP** — honest, un-softened, restart note intact; never pre-checked, never "Enable for the best experience". |
| **Webcam in use by another app** | Settings/status (new) | `Your camera's busy elsewhere right now — close the other app and try again.` | `攝影機正在被別的程式使用，先關掉那個再試一次。` | §3.1 plain recovery; no blame. |
| **Assistant offline / no profile yet** | empty banner area (new) | `No setup yet — watch the 60-second clip whenever you're ready and I'll learn your comfort.` | `還沒設定過，準備好就看 60 秒短片，我會學會你的舒適程度。` | VP-3/VP-5; ties to the welcome flow without nagging. |
| **Calibration Q3 required nudge** | inline under scale (new, replaces blocking `(必選)`) | `Pick one to continue.` | `選一個就能繼續。` | §3.1 — non-punitive; the field guidance, not a wall. Shown only on attempted skip. |

---

## 7. Token & component crosswalk for this surface

Every rewritten string above lands on these locked Calm tokens/components (so copy and visual register agree — §0 "the word loses" rule):

- **Type:** card titles → `Type.Heading` (16/22); question titles → `Type.Heading`; body/options → `Type.Body` (14/20, the floor — retires the 11px `CalibSubtitle` `:93` and every 10px scale sub-label `:231–338`); helper/eyebrow/timestamps → `Type.Caption` (12, retires the 10px `#7B8DB0` timestamps `MainWindow.xaml:694/815` and 10/11px statuses); telemetry deltas (Advanced only) → `Type.Mono` (13).
- **Color:** `TextPrimary` `#1A2230` (retires `#0A1F5C`); `TextSecondary` `#525B6B` (retires the contrast-failing `#7B8DB0`/`#9AB6E0`); `Accent` `#3A6EA5` (retires `#024AD8`/`#3D5AFE`/`#1E5BB0`); comfort bands `ComfortSteady` `#2F7D6E` / `ComfortRising` `#9A6B22` / `ComfortHigh` `#B65C4E` (retires `#E54747` alarm red and the four-hue discomfort ramp `#E5F0F8/#FFF1D6/#FFE0CC/#FBD0CB`); `Canvas` `#F6F7F9` for the tab bg (retires `#E2ECFF`).
- **Components:** `Btn.Primary` (Send/Next/Done/Start), `Btn.Quiet` (Cancel/Clear/Not now/No thanks), `Btn.Ghost` (Back/Redo setup), `Scale.Segmented` (Q3 + discomfort ramp — defined once, retires the 5× 40-line copied templates and the four-hue alarm ramp), `Option.Cell` (Q2/Q5 multi-select), `Progress.Dots` (`AutomationProperties.Name = "Step 3 of 5"`), `Status.Chip` (comfort-band headline, icon+label+color, never color-only), `Card` (`Surface`/`Elev.2`, retires the dark `#0E1320` invitation banner and the blue `#5B82C6` shadow), `Field.Text` (input placeholders via adorner, retires the VisualBrush hack `NotificationCardWindow.xaml:398`).
- **Motion:** typing dots (`MainWindow.xaml:717`) and proposal countdown become static under `ReduceMotion` (§3.4); the clinical mask cross-fade is never affected (A4).
- **Icons:** Fluent `Lightbulb` for `AI suggests` (retires `💡 U+1F4A1` at `:145`), `Dismiss` for `×`, `Settings` for `⚙`, `Warning` glyph on the A5 block note.

**Files that must change for this content pass:** `src/SenseEase.App/Views/CalibrationCardView.xaml`, `src/SenseEase.App/Views/NotificationCardWindow.xaml` (+`.xaml.cs:448/449/467/478–483`), `src/SenseEase.App/MainWindow.xaml:641–916`, `src/SenseEase.App/ViewModels/ChatbotViewModel.cs:50–52/237/238/285/286/294/325/330/331/526/789/790/852/856/935`, `src/SenseEase.App/ViewModels/CalibrationCardViewModel.cs:66`, `src/SenseEase.App/ViewModels/MainViewModel.cs:258/274/278/283/1232`, `src/SenseEase.Core/Inference/HabituationProposalLexicon.cs:107–115` — all behind new `Strings.zh-Hant.resx` / `Strings.en.resx` keys. **Do not touch** `MistralChatbotService.cs:892/904` (crisis text, A1) or the canonical profile enum values sent to the engine (C5).

---

# Copy — Settings + Pop-up Card + Onboarding + tray menu + restart/error toasts

I have all the evidence needed. Delivering the content rewrite section.

---

# Content Rewrite — Settings, Pop-up Card, Onboarding, Tray Menu, Restart & Error Toasts

> Scope: every user-facing string in `SettingsContent.xaml`, `SettingsWindow.xaml`, `NotificationCardWindow.xaml(.cs)`, `TrayIconService.cs`, the first-run + calibration-invitation banners in `MainWindow.xaml`, `SpikePopupWindow.xaml`, `DemoVideoWindow.xaml(.cs)`, and the restart/permission/error toasts in `SettingsContent.xaml.cs` / `ChatbotViewModel.cs`. Every rewrite is scored against the Voice & Tone guide §1–8 and the Immersive-Design Guardrails (A–D). Telemetry never appears on a primary surface (VP-2); strain copy leads with the action (S-1); the comfort lexicon is locked (Steady/Rising/High = 平穩/上升/較高, D2); hotline text is untouched (A1).

---

## 1. Bilingual strategy — RECOMMENDATION

**Adopt single-language-per-string via `.resx`, locale-selected at startup; retire the inline `中文 / English` cram everywhere except three deliberately co-presented spots.**

### 1.1 Why the current cram fails

The current pattern crams both languages into one `TextBlock`:

- First-run body is a **single 60-character bilingual run-on** (`MainWindow.xaml:188`) — `"先跑 60 秒的 setup demo… / Run the 60 s setup demo so SenseEase can suggest personalised defaults based on your reaction."` — doubling line length for the exact nauseated reader the app serves (violates VP-4, VP-7, B-1).
- Settings repeats it on every section header (`通知與顯示` is zh-only at `:44` but `背景常駐 / Background` at `:80`, `頭部追蹤 / Head Tracking` at `:114`, `跟隨模式 / Follow Mode` at `:161` are slash-crammed) and stacks two full paragraphs (zh at `:148`, en at `:153`) per head-tracking explanation.
- Register is **inconsistent**: `您` (formal) at `SettingsContent.xaml:148/153`-context and `CalibrationCardView.xaml:141/156`, vs implied `你` elsewhere. The guide locks **`你`** (B-3).
- The tray menu is **zh-only** (`TrayIconService.cs:118/121/124/264–267/289/319`), the Pop-up Card is **largely zh-only** (`啟動保護`, `開啟主視窗`, `6 小時內不再提示`), and the dashboard banners are **slash-bilingual** — three different policies in one app.

### 1.2 The strategy

1. **Stand up a minimal localization layer.** Two resource dictionaries — `Strings.zh-Hant.resx` (default) + `Strings.en.resx` — resolved by `CultureInfo.CurrentUICulture` at startup, with a manual language toggle in Settings (`ListRow` + value, §Settings below). The tray's own code comment already flags this as the intended fix: *"When an i18n service lands in a future sprint, replace the literal strings with resource lookups here"* (`TrayIconService.cs:42–45`). This rebrand is that sprint.
2. **One `TextBlock` per language, locale-selected** — not two stacked TextBlocks, not a slash run. Each language gets its own line-height (CJK ≥1.6×, Latin 1.4×, both at the **14px `Type.Body` floor**, B-4) and its own reflow. This retires the duplicate zh+en paragraph stacks at `SettingsContent.xaml:148/153` and `:172/176/184/188`.
3. **Keys, not literals.** `x:Uid` + `{x:Static res:Strings.Key}` (or a `LocExtension`) so the same key drives the tray menu, the card, and Settings — no copy-pasted translations (mirrors the D2 single-source rule for copy, B-5).
4. **Co-presented bilingual survives in exactly three places** where seeing both terms aids recall or is a deliberate disclosure (B-1):
   - **Calibration option labels** — `頭暈 / Dizziness` (`CalibrationCardView.xaml:191–203`): the dual term aids symptom recognition.
   - **The single restart-consequence note** — `需重新啟動 / Requires restart`: dual disclosure is intentional (B1 privacy invariant).
   - **The crisis hotline card** — Taiwan 1925 安心專線 and US 988 both always render regardless of locale, because a person in distress may read either (A1, B-5). **This surface is not rewritten here** and not routed through any localization swap.

### 1.3 How the new strings work under it

```xml
<!-- BEFORE: one TextBlock, two languages, 60 chars, 您-stiff -->
<TextBlock Text="先跑 60 秒的 setup demo，系統會依您的反應建議個人化預設值 / Run the 60 s setup demo so SenseEase can suggest personalised defaults based on your reaction."
           Foreground="#F0F4FA" FontSize="12" TextWrapping="Wrap"/>

<!-- AFTER: locale-selected single language, Type.Body (14px), TextSecondary token -->
<TextBlock x:Uid="FirstRun_Body"
           Style="{StaticResource Type.Body}"
           Foreground="{DynamicResource TextSecondary}"
           TextWrapping="Wrap"/>
```
```xml
<!-- Strings.zh-Hant.resx -->  <data name="FirstRun_Body" xml:space="preserve">
  <value>先看 60 秒的小短片，我就能依你的反應幫你調好預設值。</value></data>
<!-- Strings.en.resx -->       <data name="FirstRun_Body" xml:space="preserve">
  <value>Watch a 60-second clip and I'll set comfortable defaults from how you react.</value></data>
```

The same key pattern replaces `TrayIconService.cs:118` literal `Header = "顯示主視窗 / Open Dashboard"` with `Header = Strings.Tray_OpenDashboard` (one term per locale), and the card's `EngageBtn.Content` (`NotificationCardWindow.xaml:346`).

---

## 2. Onboarding — first-run banner, calibration-invitation banner, demo HUD

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `MainWindow.xaml:186` | `● 歡迎使用 SenseEase / Welcome to SenseEase` | Welcome to SenseEase | 歡迎使用 SenseEase | Split per-locale; drop bare `●` color-dot (D2). Title = `Type.Title` (20). |
| `MainWindow.xaml:188` | `先跑 60 秒的 setup demo，系統會依您的反應建議個人化預設值 / Run the 60 s setup demo…` | Watch a 60-second clip and I'll set comfortable defaults from how you react. | 先看 60 秒的小短片，我就能依你的反應幫你調好預設值。 | VP-4 (one short sentence), B-1 (split), 您→你 (B-3). `Type.Body` 14px on `TextSecondary`. |
| `MainWindow.xaml:192` | `Skip` (text `#9AB6E0`, fails contrast ~2.6:1) | Not now | 先略過 | Terse→gentle (§3.1); `Btn.Quiet` token, fixes contrast. |
| `MainWindow.xaml:198` | `Run Setup Demo` | Start the 60-second setup | 開始 60 秒設定 | Concrete, calm CTA (§7 rewrite 8); `Btn.Primary`. |
| `MainWindow.xaml:178` banner card | dark `#0E1320` card + `#3D5AFE` blue border on light dashboard | (no copy — re-skin to `Card` on `Surface`, `Elev.2`) | — | Resolves the two-clashing-palettes defect; no colored border (Calm §4 Card). |
| `MainWindow.xaml:841` | `想更新你的設定嗎？跟系統聊聊剛才的感覺。` | Want to update your settings? Tell me how that just felt. | 想更新設定嗎？跟我說說剛才的感覺就好。 | Already warm; tighten, 你-consistent, "跟我" (companion voice §5). |
| `MainWindow.xaml:844` | `Want to refresh your settings? Chat about what you just felt.` | (becomes the EN locale of the row above) | — | Collapse the stacked zh+en into one locale-selected string (B-1). |
| `MainWindow.xaml:850` | `跳過 / Skip` | Not now | 先略過 | Consistency with first-run; `Btn.Quiet`. |
| `MainWindow.xaml:856` | `開始 calibration / Start calibration` | Start | 開始 | One language; the banner context already says what starts (VP-7). `Btn.Primary`. |
| `DemoVideoWindow.xaml:25` | `● SENSEEASE DEMO` (caps, `#FFB347` amber) | SenseEase setup clip | SenseEase 設定短片 | Retire shouting caps (VP-1) + bare `●`; this is calibration, not a "demo". |
| `DemoVideoWindow.xaml:27` | `Screen-capture sensing is live.` | I'm watching how the screen moves while this plays. | 我正在看畫面的動態，幫你做準備。 | "sensing is live" = surveillance-cold (§3.1); reframe as the companion preparing for you (S-2). |
| `DemoVideoWindow.xaml:29` | `Mask + chatbot will react to motion.` | The soft edges may come and go — that's normal. | 邊緣可能會柔和地變化，這是正常的。 | "Mask + chatbot will react" = engineering (VP-2); reassure instead (VP-5). |
| `DemoVideoWindow.xaml:31` | `Press ESC or close window to stop` | Press Esc or close the window to stop anytime. | 隨時按 Esc 或關閉視窗就能停止。 | Add "anytime" = always-an-exit reassurance (S-5); fix `#9AB6E0` contrast → `TextSecondary`. |

**Demo-clip wellbeing note (currently missing).** Add one calm line under the HUD, since the user is about to be deliberately exposed to vection: 

- EN: "If it ever feels too much, stop — that tells me plenty too." 
- 中文: 「如果覺得不舒服，停下來就好，這也是很好的資訊。」 (`Type.Caption` 12px, `TextSecondary`; frames stopping as success, not failure — dignity, §1.)

---

## 3. Pop-up notification Card

### 3.1 Title + body (the hardest copy — strain states, `NotificationCardWindow.xaml.cs`)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:467` (title) | `VIMS 視覺壓力　第 {tier} 級 / 10　·　壓力指數 {Math.Round(pressure)}` | Right now: {Steady/Rising/High} · {resting/easing} | 目前狀態：{平穩／上升／較高} · {正在守候／正在緩和} | VP-2 + **S-3**: raw unbounded `pressure` (>300 observed, per the code comment at `:463–465`) as the headline invites the user to watch a number climb. Headline = comfort word only; raw figure → Advanced. |
| `:480` (Grey body) | `目前視覺壓力低。` | All steady — I'm keeping watch in the background. | 目前平穩，我在背景幫你留意。 | "視覺壓力" telemetry → comfort word (D2); add the reassuring "keeping watch" (VP-5). |
| `:481` (Amber body) | `偵測到中度視覺壓力。建議短暫休息或暗化螢幕。` | Strain is building a little — softening the edges to keep it steady. Rest whenever you like. | 壓力慢慢上升，我把邊緣稍微收一點。想休息隨時可以。 | "偵測到…建議…" commands an already-protected user (S-1); lead with the action, offer a calm exit (S-5). §7 rewrite 2. |
| `:482` (Red body) | `偵測到高度視覺壓力。建議立即啟動保護或暫停當前活動。` | The screen's been busy — easing the edges while this settles. Take a break if it helps. | 畫面動得比較多，我正在替你緩和邊緣。需要的話可以先暫停一下。 | The cardinal sin: announces threat + commands "immediately" to a user the mask is *already* protecting (`MaskActive` flips before `ApplyContentToUi`). Strain is external+temporary (S-2). §7 rewrite 1. |
| `:448` (PowerSave title) | `VIMS · 省電模式中` | Sensing paused | 感測已暫停 | "省電/Power Save" mis-frames a sensing toggle as energy-saving (§3.1); drop "VIMS". |
| `:449` (PowerSave body) | `感測已暫停，可點擊「省電」恢復。` | Turn it back on whenever you're ready, and I'll keep watch. | 需要時再打開，我就繼續替你留意。 | §7 rewrite 6; companion voice (§5). |

> **Title-template note for engineering:** the band/action words come from `TierColorPalette.BandForTier` mapped to the locked lexicon. Keep the `N/10` tier and raw `pressure` available **only** behind an "Advanced" disclosure rendered in `Type.Mono` (13px) — they are diagnostics, not the headline (VP-2). The card title must pair the comfort word with the `Status.Chip` icon, never color-only (D2).

### 3.2 Card controls, proposal, discomfort ramp, chat input

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:145` proposal header | `💡 SenseEase 建議` (emoji `U+1F4A1`) | AI suggests | AI 建議 | Retire load-bearing emoji (§1 Competent); the `AI suggests:` prefix is the locked affordance (A3, H7), italic. |
| `:163–169` `ProposalSafetyNoteText` (empty, rendered `#8C2F23`) | *(generic/empty)* | ⚠ Holding off on that one — it would lower your protection below what's comfortable for you. | ⚠ 這項建議會把保護調得比你目前的設定低，所以先保留著。 | A5: must stay visible, plain reason, **muted clay `ComfortHigh #B65C4E`** not alarm red `#E54747`. §7 rewrite 11. |
| `:178` `ProposalAcceptBtn` | `接受` | Accept | 接受 | One language; `Btn.Primary`. |
| `:189` `ProposalDeclineBtn` | `拒絕` | Not now | 先不要 | "拒絕/reject" is harsh for declining one's own helper (§3.1, dignified); must disable on expiry (D4). |
| `:199` `ProposalLaterBtn` | `稍後` | Later | 等一下 | `Btn.Quiet`. |
| `:237` discomfort eyebrow | `視覺不適程度` (`#5A6478`, 11px) | How's it feeling? | 現在感覺如何？ | "不適程度/discomfort level" leads with discomfort (§3.1); warm question. Raise 11px→`Type.Caption` 12 (§2 no-11px rule). |
| `:260` discomfort btn 1 | `輕微` | A little | 一點點 | Single-hue sequential ramp `ComfortSteadyWeak→ComfortHigh` via `Scale.Segmented`, NOT the current four-hue alarm ramp (`:253/267/281/295`). |
| `:274` discomfort btn 2 | `中等` | Some | 有一些 | (ditto) |
| `:288` discomfort btn 3 | `明顯` | A lot | 蠻明顯 | (ditto) |
| `:302` discomfort btn 4 | `嚴重` | Too much | 受不了 | Plain human register, not clinical "severe" (§3.1). |
| `:333` PowerSave toggle label | `省電` (+ `U+23FB` glyph) | Pause sensing | 暫停感測 | §3.1: corrects the energy-saving mis-frame; Fluent `Pause` icon (§5). |
| `:328` PowerSave tooltip | `暫停 / 恢復 SenseEase 感測（不會關閉應用程式）` | Pauses what I'm sensing. The app stays running. | 暫停我的感測，應用程式不會關閉。 | Already correct mental model; warm it + split locale. |
| `:343` `OpenDashboardBtn` | `開啟主視窗` | Open dashboard | 開啟主視窗 | `Btn.Primary`; locale-keyed. |
| `:346` `EngageBtn` | `啟動保護` (`#FF9B6B` orange fill, `#101418` text) | Ease now | 馬上緩和 | "啟動保護/activate protection" implies it's off (it isn't); name what it does (VP-3). Re-skin off the loud orange to `Accent`. |
| `:357` `MuteBtn` | `6 小時內不再提示` | Quiet for 6 hours | 6 小時內安靜 | "不再提示/no more alerts" is terse; mute is band-scoped + in-memory — copy must not imply it persists across restarts (D4). `Btn.Quiet`. |
| `:400` chat placeholder | `跟 SenseEase 說話` (`#9AA3B0`, VisualBrush hack) | Tell me how it feels | 跟我說說感覺 | Companion voice (§5); retire the VisualBrush placeholder hack → real `Field.Text` adorner. |
| `:417` `ChatSendBtn` | `送出` | Send | 送出 | `Btn.Primary`. |
| `:120` settings gear tooltip | `設定 / Settings` | Settings | 設定 | Split locale. Replace `⚙` literal with Fluent `Settings` icon (§5). |
| `:102` close `×` | `×` | (icon only) | (icon only) | Replace `×` glyph with Fluent `Dismiss`; add `AutomationProperties.Name="Close"`/「關閉」. |

### 3.3 Card chat status strings (`NotificationCardWindow.xaml.cs`)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:699` | `Chatbot 尚未連線。` | I'm not ready just yet — give me a moment. | 我還在準備，稍等一下下。 | "尚未連線/not connected" = cold-technical (§3.1); warm + non-blaming. |
| `:706` | `傳送中…` | Sending… | 傳送中… | Fine; keep. |
| `:717` | `傳送失敗：{ex.GetType().Name}` | That didn't go through — try once more? | 沒有送出，再試一次好嗎？ | Lead with recovery, never expose exception type names to users (§3.1, B2). |
| `:732` | `已套用。` | Done — eased a little. | 好了，幫你緩和一點。 | "已套用/applied" is robotic; name what changed (VP-5). |

---

## 4. Settings (`SettingsContent.xaml`, `SettingsWindow.xaml`)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `SettingsContent.xaml:44` | `通知與顯示` (`Bold` 16, `#0A1F5C`) | Notifications & display | 通知與顯示 | `Section.Header` token (`Type.CaptionStrong`, `TextSecondary`). |
| `:52` Toggle A | `讓 SenseEase 主動展開通知卡` | Let SenseEase open the card on its own | 讓 SenseEase 自己展開通知卡 | Warm; `Toggle.Switch` not bare CheckBox; `AutomationProperties.Name` required. |
| `:57` Toggle A helper | `開啟時，系統偵測到中度以上不適會自動展開通知卡。預設關閉以避免非預期視覺干擾。` | When on, the card opens itself if strain rises. Off by default so nothing surprises you. | 開啟時，壓力上升我會自己展開卡片。預設關閉，避免突然的視覺干擾。 | "偵測到…不適" surveillance+discomfort framing (§3.1); auto-expand fires on band-crossing not every tick (D4). Raise 11px→12. |
| `:67` Toggle B | `Bar 與 Tray 顏色隨 tier 變化` | Match the bar and tray colour to your comfort state | 讓狀態列和工作匣顏色跟著舒適狀態變化 | "tier" is jargon (VP-2); "comfort state" per D2. |
| `:72` Toggle B helper | `…顏色會依視覺壓力 tier 變化（灰 / 琥珀 / 紅）；關閉時永遠維持灰色透明。` | When on, they shift Steady · Rising · High. When off, they stay a calm neutral. | 開啟時會在平穩・上升・較高之間變化；關閉時維持中性灰。 | Locked band lexicon (D2); "紅/red" → "較高/High" (it's clay, not red, A5). |
| `:80` | `背景常駐 / Background` | Running in the background | 背景常駐 | `Section.Header`; drop slash-cram (B-1). |
| `:87` Toggle C | `關閉視窗時最小化到工作匣 (不結束程式)` | Keep SenseEase running when I close the window | 關掉視窗時讓 SenseEase 繼續在背景執行 | Lead with intent, not mechanism; warm. |
| `:92` Toggle C helper | `…按下主視窗的 X 只會把視窗收到右下角的工作匣…要完全結束請用…「退出 SenseEase」。` | Closing the window tucks it into the tray; SenseEase keeps watching. To quit fully, use "Quit SenseEase" in the tray menu. | 關掉視窗只會收到工作匣，SenseEase 繼續留意。要完全結束，用工作匣選單的「退出 SenseEase」。 | Tighten (VP-4); 你-voice; "keeps watching" companion (§5). |
| `:101` Toggle D | `登入 Windows 時自動啟動 SenseEase` | Start SenseEase when I sign in to Windows | 登入 Windows 時自動啟動 SenseEase | Fine; warm to 你. |
| `:106` Toggle D helper | `…會跟著 Windows 登入自動啟動並直接最小化到工作匣…透過 HKCU 註冊表項目實作（不需系統管理員權限）。` | It starts quietly in the tray at sign-in — no admin rights needed. | 登入時會安靜地在工作匣啟動，不需要系統管理員權限。 | Drop "HKCU 註冊表項目" engineering detail (VP-2); keep the reassuring "no admin" part. |
| `:114` | `頭部追蹤 / Head Tracking` | Head tracking | 頭部追蹤 | `Section.Header`; split locale. |
| `:122` head-track enable | `啟用頭部追蹤 / Enable Head Tracking` | Use my webcam to follow my gaze | 用 webcam 讓遮罩跟著我的視線 | Honest about webcam up front (B1); split locale; `Toggle.Switch` must NOT be pre-checked (B1). |
| `:135` `RestartButton` | `需重新啟動 / 立即重啟` | Requires restart — restart now | 需重新啟動／立即重啟 | **Co-presented bilingual KEPT** (B1 dual disclosure is intentional). Re-skin off the amber `#FFF4D6`/`#8B5A00` to `Btn.Quiet`. |
| `:148` + `:153` (two stacked paras) | zh `…讀取 webcam（MediaPipe 推論臉部姿態）…預設關閉以保護隱私。` + en duplicate | Uses your webcam locally to follow your gaze with the soft edges. Nothing ever leaves your device. Off by default. | 在本機用 webcam 讓邊緣跟著你的視線。所有資料都不會離開你的裝置。預設關閉。 | Collapse zh+en stack → one locale string (B-1); **keep the "nothing leaves your device" privacy disclosure intact, not softened to vagueness** (B1 HARD-STOP). |
| `:146` restart tooltip | `點擊立即重啟…&#x0a;Click to restart SenseEase now…` | Restart now to apply the head-tracking change | 立即重啟以套用頭部追蹤變更 | Split locale. |
| `:161` | `跟隨模式 / Follow Mode` | How the edges follow you | 跟隨模式 | Plain-language sub-header. |
| `:167` Balanced radio | `平衡 Balanced (建議 / recommended)` | Balanced (recommended) | 平衡（建議） | One language; `recommended` once. |
| `:172` Balanced helper | `遮罩中心限制在螢幕中央 80% 區域，周邊永遠維持部分遮蔽（臨床預設、減輕視覺動暈）。` | The soft edges stay gently in from the corners — calmer, and the default we suggest. | 邊緣的柔化會稍微留在角落內側——比較穩，也是建議的預設。 | "遮罩中心…80% 區域" engineering (VP-2); the `[0.10,0.90]` clamp is clinical (A3) — explain it plainly, don't expose coordinates. |
| `:179` Full-follow radio | `完整跟隨 Full follow (邊緣較完整，可能感覺較不穩定)` | Full follow (reaches the corners — may feel busier) | 完整跟隨（可到角落，可能感覺較不穩） | Honest trade-off, calm register. |
| `:184` Full-follow helper | `遮罩中心可走到螢幕角落…周邊變化較大、可能感覺較不穩定。` | Lets you see all the way to the corners; the edges move more, which some find less steady. | 可以看到整個角落；邊緣變化較大，有些人會覺得較不穩。 | Plain; no jargon. |
| `:193` escape-hatch toggle | `啟用暫時透明 / Enable temporary transparency` | Quick peek at the corners | 偷瞄角落（暫時變透明） | Plain; `Toggle.Switch`. Disabled state for vestibular users must read as a calm "unavailable for your profile" (A3). |
| `:198` escape-hatch helper | `連點兩下 Ctrl…短暫降低 FOV 遮罩到 30% 透明度 3 秒，方便偷瞄螢幕角落。` | Double-tap Ctrl to soften the edges for 3 seconds when you need to glance at a corner. | 連點兩下 Ctrl，邊緣會柔化 3 秒，方便你瞄一下角落。 | Drop "FOV 遮罩 30% 透明度" engineering; note it dips to 0.30 not 0 (A3) — "soften", never "clear". |
| `:205` vestibular note | `（你的 calibration 紀錄顯示前庭敏感，此功能已停用以維持視覺保護。Disabled because vestibular sensitivity detected;…）` | Kept on for your profile. Because you mentioned balance sensitivity, I keep a little protection here even during a peek. | 因為你提到平衡比較敏感，這個功能先停用，讓保護一直在。 | A3/A4: explain the clinical floor plainly, **never offer a workaround**; companion non-negotiating voice (§5 A-4). Re-skin `#C2410C` → `ComfortHigh` clay, paired with a glyph (D2). |
| `SettingsWindow.xaml:32` Close | `關閉 / Close` | Close | 關閉 | Split locale; `Btn.Quiet`. |
| `SettingsWindow.xaml:5` title | `SenseEase 設定` | SenseEase Settings | SenseEase 設定 | Window chrome, locale-keyed. |

**New Settings microcopy (currently missing):**
- **Language toggle row** (enables the .resx strategy): EN "Language" → values "中文 / English"; 中文「語言」. `List.Row` with trailing value.
- **Theme row** (the Calm system ships Light/Dark/High-Contrast): EN "Appearance — Light · Dark · Match Windows"; 中文「外觀 — 淺色・深色・跟隨系統」.
- **Reduce-motion row** (binding safety switch, §3.4 of the Calm spec; currently 0 motion-pref checks exist): EN "Reduce motion" + helper "Calms animations across the app. The gentle dimming always stays smooth."; 中文「減少動態效果」+「讓全app的動畫變平靜；柔和的變暗一定會保持平順。」 — the helper must state that the **clinical mask cross-fade is never disabled** (A4).
- **"Reset visual settings"** (`Btn.Quiet`/muted, distinct from Delete-all-data, D3): confirm preview EN "This returns the easing to its defaults. Your earned comfort progress stays."; 中文「這會把緩和效果還原成預設，你長期累積的舒適進度會保留。」 — must NOT zero the habituation `_offset` (D3). §7 rewrite 9.

---

## 5. Tray menu (`TrayIconService.cs`)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `:138` tooltip | `SenseEase` | SenseEase — {Steady/Rising/High} | SenseEase — {平穩／上升／較高} | Tooltip can carry the comfort word (the menu shows detail). |
| `:269/282` status header/tooltip | `VIMS Tier {N} · {band}` / `SenseEase — Tier {N} · {band}` | You're protected · {Steady/Rising/High} | 保護中 · {平穩／上升／較高} | VP-2: drop "VIMS Tier {N}" telemetry; comfort word only (D2). |
| `:264` band Grey | `舒緩` | Steady | 平穩 | Locked lexicon (D2/B-5). |
| `:265` band Amber | `中度` | Rising | 上升 | Locked lexicon. |
| `:266` band Red | `高壓` | High | 較高 | Locked lexicon; "高壓/high-pressure" → "較高" (clay, not alarm, A5). |
| `:118` menu item | `顯示主視窗 / Open Dashboard` | Open dashboard | 開啟主視窗 | Split locale. |
| `:289` toggle bar | `隱藏 bar` / `顯示 bar` | Hide the bar / Show the bar | 隱藏狀態列／顯示狀態列 | "bar" untranslated → "狀態列"; English added. |
| `:121` settings | `設定` | Settings | 設定 | Add English locale. |
| `:124` quit | `退出 SenseEase` | Quit SenseEase | 退出 SenseEase | Add English locale. |
| `:319` PowerSave indicator | `Power Save ON` | Sensing paused | 感測已暫停 | §3.1 corrects the energy-saving mis-frame; matches the card's new PowerSave copy. |

---

## 6. Restart, permission & error toasts (MessageBox + status lines)

| Location (file:line) | Current string | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|---|
| `SettingsContent.xaml.cs:206–209` autostart fail | `無法寫入登入啟動設定。可能的原因：- 系統管理員…- 註冊表權限不足。…手動把 SenseEase 加到…「啟動」資料夾…` | Couldn't set up auto-start — your system may restrict startup apps. You can add SenseEase to the Windows Startup folder manually instead. | 沒辦法設定自動啟動，你的系統可能限制了啟動項目。你可以手動把 SenseEase 加到 Windows「啟動」資料夾。 | Lead with the recovery, not the failure (§3.1 "Failed to"); drop "註冊表權限" jargon (VP-2). |
| `SettingsContent.xaml.cs:261` restart confirm | `重啟 SenseEase 以套用變更？\n\nRestart SenseEase to apply changes?` | Restart SenseEase to apply this change? | 重新啟動 SenseEase 以套用變更？ | Locale-split (don't stack both in one box); calm question. |
| `SettingsContent.xaml.cs:278` exe-not-found | `無法定位 SenseEase 執行檔，請手動重啟。…Unable to locate…` | I couldn't restart automatically — please reopen SenseEase yourself. | 沒辦法自動重啟，請手動再打開 SenseEase。 | Non-blaming; locale-split. |
| `SettingsContent.xaml.cs:302` restart fail | `重啟失敗：{ex.Message}\n\nRestart failed: {ex.Message}` | I couldn't restart this time — please reopen SenseEase yourself. | 這次沒能重啟，請手動再打開 SenseEase。 | Never surface raw `ex.Message` to users (§3.1, B2); calm fallback. |
| `MainViewModel.cs:279` profile-switch fail | `⚠ 無法切換到 {canon}：{result.Reason}` | ⚠ Couldn't switch to {name} — {reason} | ⚠ 沒辦法切換到 {name}：{reason} | Keep the transparent block reason (A5); muted clay `ComfortHigh`, not red. |
| `ChatbotViewModel.cs:285` model ready | `Ministral 3B ready` | Assistant ready | 助理已就緒 | Drop the model name from the user surface (VP-2); plain. |
| `ChatbotViewModel.cs:286` fallback | `Lexicon fallback (no GGUF found at: {path})` | Assistant ready (offline mode) | 助理已就緒（離線模式） | Never expose GGUF path / fallback internals (VP-2, B2 no absolute paths); plain-fallback parity (A-6). |
| `ChatbotViewModel.cs:294` load fail | `Load failed: {ex.GetType().Name}: {ex.Message}` | Assistant unavailable right now — you can still adjust things in Settings. | 助理暫時無法使用，你還是可以在設定裡調整。 | Never surface exception type+message (§3.1, B2); offer an alternative path (S-5). |
| `SpikePopupWindow.xaml:4` title | `SenseEase — VIMS Alert` | SenseEase | SenseEase | "Alert" is an avoid-word (§3.1); this surface should not announce threat. |
| `SpikePopupWindow.xaml:23` header | `● VIMS spike` (`#FF9B6B`) | A quick check-in | 想跟你確認一下 | "spike/alert" alarmist (S-1); reframe as a gentle check-in. Re-skin off orange; pair glyph+label (D2). |
| `SpikePopupWindow.xaml:89` footer | `點選選項後將自動寫入聊天助理 / Selected answer is sent to AI Assistant.` | Your answer goes to the assistant. | 你的回答會送給助理。 | Tighten + split locale; `Type.Caption`. |

> The `SpikePopupWindow` whole surface is dark (`#CC1A1A1A`) on the otherwise-light app — a clashing-palette defect. Re-skin to the `Card` token on `Surface` with `Elev.3`; its strain copy must lead with the action and never show the raw `LastNormalizedSpike` figure (S-3).

---

## 7. Microcopy currently MISSING (empty / loading / permission states)

The app has **no** empty-state, loading-state, or webcam-permission copy today — these gaps are filled here, all in the locked register.

| State (where it should live) | New EN | New 中文 (Traditional) | Why |
|---|---|---|---|
| **Webcam permission denied** (head-tracking opt-in path; `SidecarHeadPoseReader` start failure today only logs `:252`) | Windows didn't let me reach your camera. Head tracking stays off — nothing else changes. | Windows 沒有允許使用相機，頭部追蹤先保持關閉，其他都不受影響。 | Honest, non-alarming permission failure (B1, B3); reassure that core protection is unaffected. |
| **Webcam in use by another app** | Your camera's busy elsewhere right now. I'll keep the edges centred until it's free. | 相機正被其他程式使用，我先把邊緣保持置中，等它空出來。 | Plain cause + what SenseEase does meanwhile (VP-5); mask falls back to (0.5,0.5) per the capability map. |
| **Head-tracking active confirmation** (driven by `face_present`, B3 allowed) | Following your gaze. Windows shows a camera light while this is on. | 正在跟著你的視線。開啟期間 Windows 會顯示相機指示燈。 | Calm self-verification hint (B3); **must NOT** request a webcam preview/face-mesh (B3 HARD-STOP). |
| **Assistant thinking** (replaces the typing-dots storyboard, `MainWindow.xaml:709–743`, under Reduce-Motion becomes static `…`) | Thinking… | 思考中… | One word; static under Reduce-Motion (§3.4). Never appears on the crisis surface (A5). |
| **Chat history empty** (chat tab, no current empty state) | I'm here when you need me. Tell me how things feel, or ask me to ease the edges. | 我在這裡。跟我說感覺，或叫我把邊緣緩和一點都可以。 | Inviting empty state (§5 companion voice); names the two things the user can do. |
| **Event log empty** (`MainWindow.xaml:455` ListView, no empty text) | Nothing to show yet — it's been calm. | 還沒有紀錄，目前都很平穩。 | Reframes "empty" as a good thing (VP-6 symmetry); `TextSecondary`, centred. |
| **Sensing warming up** (first 2.0s WGC warm-up suppressed to 0, per capability map; no current copy) | Getting my bearings… | 正在熟悉畫面… | Honest about the 2s warm-up; calm, not "loading…". |
| **Demo video failed to load** (`DemoVideoWindow.xaml.cs:31/53` — `Demo video not found:\n{path}` / `Playback failed: {ex.Message}`) | I couldn't start the setup clip. You can skip setup for now and adjust things later in Settings. | 沒辦法播放設定短片。你可以先略過，之後在設定裡再調整。 | Never show the raw path / `ex.Message` (B2); offer the skip path (S-5). |
| **Sensing paused (ambient reminder)** (while Power-Save is on) | Sensing's paused — turn it back on whenever you're ready. | 感測暫停中，準備好隨時再打開。 | Matches card + tray PowerSave copy; reassuring, reversible (VP-3). |
| **Mask resting (dashboard hero idle)** (replaces `Mask inactive`, `MainWindow.xaml:270`) | Resting — nothing to ease right now. | 目前平穩，沒有需要緩和的。 | `Status.Chip` Idle variant (moon icon); states the calm good-state plainly (D2). §7 rewrite 5. |
| **Mask easing (dashboard hero active)** (replaces `Mask ACTIVE`, `:273`, caps) | Easing your view now. | 正在替你緩和畫面。 | Retire shouting caps + "Mask" jargon; `Status.Chip` High variant (D2). |

---

## 8. Governance — this rewrite passes the §8 checklist

- **No avoid-words ship:** "Alert" (`SpikePopupWindow.xaml:4/23`), "detected/偵測到" (`NotificationCardWindow.xaml.cs:481/482`), "Power Save/省電" (`:333/448`, `TrayIconService.cs:319`), "Mask ACTIVE" caps (`MainWindow.xaml:273`), "Failed/無法" toasts, and the `💡` emoji (`:145`) are all retired.
- **Strain copy leads with the action**, frames strain as external+temporary, and the raw unbounded `pressure` (>300) is removed from the card headline (S-1/S-3, `:467`).
- **Comfort lexicon locked** Steady/Rising/High = 平穩/上升/較高 across card, tray, settings, paired with `Status.Chip` icon+label (D2) — never color-only (retires bare `Ellipse` dots at `MainWindow.xaml:255/543/579`).
- **Telemetry moved behind Advanced** (`Type.Mono`): OFI, VIMS Pressure, Leaky Integrator, FMS, FOV Radius, raw tier number.
- **One language per string via .resx**, both idiomatic, `你` standardized; co-presented bilingual kept only for calibration option labels, the restart note, and the (untouched) hotline card.
- **HARD-STOPs intact:** the crisis hotline card (`MistralChatbotService.cs:892`, `CalibrationChatbotService.cs:330`) is **not** rewritten, not localized-away, not warmed, not routed through the bubble; the vestibular escape-hatch note explains the floor without offering a workaround (A3/A4); the "nothing leaves your device" webcam disclosure stays explicit (B1); "Reset visual settings" copy never conflates shader-reset with the habituation `_offset` wipe (D3).

Evidence files read: `src/SenseEase.App/MainWindow.xaml`, `Views/SettingsContent.xaml(.cs)`, `Views/SettingsWindow.xaml`, `Views/NotificationCardWindow.xaml(.cs)`, `Views/CalibrationCardView.xaml`, `Views/DemoVideoWindow.xaml(.cs)`, `Views/SpikePopupWindow.xaml`, `Services/TrayIconService.cs`, `ViewModels/ChatbotViewModel.cs`, `ViewModels/MainViewModel.cs`.
