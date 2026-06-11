> **ARCHIVED 2026-06-11 — superseded by `docs/HABITUATION-PROFILE.md` and `docs/TECHNICAL_WHITEPAPER.md` §7;
> kept for historical reference; statements below may be factually wrong about the current code.**
> (128 KB sprint planning document for Habituation phases 6/7/8; the implemented outcome is
> recorded in `docs/HABITUATION-PROFILE.md`, `docs/HABITUATION-CHATBOT-INTEGRATION.md`,
> and whitepaper §7. Q4.1–Q4.7 decisions are locked; open sub-items were resolved in the v0.9 sprint.)

# Section 1: User Need Restatement — Habituation Phase 6/7/8

下面是用 user 語言重述的「為什麼這個 sprint 存在」。**請 user 讀完後 raise 任何「我意思不是這樣」的歧義**——這個 section 是後續所有 section 的地基。

---

## 一、User 原始抱怨的考古

回看 sprint transcript 累積，user 對 chatbot ↔ engine 關係的不滿出現過數次。以下是**用 user 原話呈現**（從 transcript 摘錄的關鍵 quotes），不是我改寫的版本：

> 「chatbot 不理 habituation engine」

> 「chatbot 不知道我的狀態」

> 「我想跟 chatbot 聊『最近這幾天的進步』而不是只能聊『今天感覺如何』」

這幾句話表面看是抱怨 chatbot 對話品質，但深層含意是**user 對整個系統「有沒有真的在理解我」的質疑**。

User 在 Pop-up sprint 過程中也透露了類似訊號——例如：

- 抱怨 pop-up「過度干擾」→ 系統不懂何時打擾才合適
- 要求「省電模式」→ user 要對系統有 control，但系統不該因此失憶
- 要求 Card 顯示 Tier + Pressure 數字 → user 想直接看到系統對自己的「評價」

這些抱怨指向**同一個底層問題**：SenseEase 的各個元件（engine、chatbot、UI surfaces）對 user 狀態的認知是**割裂的**，user 在不同 surface 看到的 SenseEase 像是不同的人格。

---

## 二、Sprint 真正要解決的問題（advisor 的詮釋）

如果直接 framing 成「讓 chatbot 讀 habituation engine state」，那是 implementation goal，不是 user goal。

User 真正想要的是：

### 真實 Goal 1：**對話的縱深感**

User 想要 chatbot 對話有「歷史」——chatbot 應該記得 user 三天前說過什麼、兩週前做過什麼 calibration、上週 tier 在 7 卡了幾天、今天突然降到 4。

當前 chatbot 是「失憶症」狀態——每次對話像第一次見面。這對 user 來說等於系統「不認識我」。

### 真實 Goal 2：**狀態的內在一致性**

User 在不同 surface 看到的 SenseEase 應該是「同一個人」：

- 看 Dashboard 顯示「Pressure 67/100、Tier 4」
- 點 Card 看到「VIMS Tier 4 · Pressure 67/100 · 中度視覺壓力」
- 跟 chatbot 對話時，chatbot 應該「知道」你現在是 Tier 4，不需要 user 重複告訴它

當前 chatbot 不知道這些。User 一打開 chatbot 對話，等於要重新把所有狀態講給「另一個不認識自己」的助手。

### 真實 Goal 3：**雙向的 agency**

對話不只是 user 問 chatbot 答。**Chatbot 應該能在合適時機提出建議，user 同意後系統真的會改變**。

例如：

- Chatbot 觀察到 user 已經連續 5 天在 Tier 6-7 → 建議「要不要把 sensitivity 調高一些，讓系統更早提醒你？」
- User 同意 → engine 真的調整 → 下次 calibration 看到效果
- User 不同意 → 系統尊重 user，不再 push 這條建議

當前 chatbot 不能做這件事——它沒有「寫」engine state 的能力。User 與 chatbot 對話像在跟「沒手沒腳的旁觀者」聊天。

### 真實 Goal 4：**安全網的記憶**

User 在 calibration 中曾標記 vestibular_diagnosed → 系統當時顯示了 medical advisory。

幾個月後 user 跟 chatbot 對話「我覺得 SenseEase 太敏感，可以關掉嗎？」→ **chatbot 應該記得 medical advisory，溫和地 surface 這條歷史**：「你 6 個月前 calibration 時提到有前庭診斷，為這層保護建議是基於那個原因。要關掉的話我可以幫你，但想先問你是不是診斷狀況有變化？」

當前 chatbot 不知道 medical advisory 存在。User 要求關掉 → chatbot 直接照辦 → 安全網靜默失效。

這是**比對話品質更深層的 user 信任問題**：user 給過系統一個重要資訊（vestibular），系統卻在某些 surface 忘了。

### 真實 Goal 5：**對 Habituation 的尊重**

Habituation Engine 的整個設計是「user 對視覺刺激的耐受度是漸進養成的」。User 上週做 calibration 時是 Sensitive profile，這週每天用 SenseEase 是「練習」，幾週後可能進化到 Typical profile。

當前 chatbot 不知道這個 progression。User 在第 4 週說「今天好像比較不暈」，chatbot 應該能回應「對，你的 habituation_level 從上週的 8.2 升到 11.5，這代表你的視覺系統正在適應」。當前 chatbot 只會說「很高興聽到你的感覺有改善」——這是 generic 安慰，不是 system-grounded 對話。

---

## 三、Phase 6/7/8 對應到 User Goal 的 mapping

把上面 5 個 user goal 對應到 sprint 累積已記憶的 phase 分配：

|Goal|對應 Phase|具體實作方向|
|---|---|---|
|1. 對話縱深感|Phase 6（chatbot READ）+ Phase 7（write-side 含 longitudinal observation 寫回）|Chatbot 讀 `longitudinal_observations` + `calibration_count` + `habituation_level` 歷史|
|2. 狀態一致性|Phase 6（chatbot READ）|Chatbot 每次對話 system prompt 注入 `[habituation_state]` block|
|3. 雙向 agency|Phase 7（chatbot WRITE）|Chatbot output `[PROFILE_OVERRIDE]` token，user UI confirm 流程|
|4. 安全網記憶|Phase 6 + Phase 7（read advisory + write resume_acknowledged）|Chatbot 讀 `medical_advisory_shown` 欄位，在敏感對話 surface 它|
|5. Habituation 尊重|Phase 6 + Phase 8（acceptance 含 longitudinal scenario）|Chatbot 讀 `habituation_level` 隨時間變化，acceptance scenario 跑「4 週時序」對話|

**這個 mapping 重要的是**：Phase 6/7/8 不只是「technical implementation phases」，每個 phase 都對應 user 真實感受到的某種斷層。Phase 完成 = 某個斷層被縫起來。

---

## 四、Anti-Goal（這個 sprint **不**要做的事）

同樣重要的是**界定範圍**。以下幾條看似相關但**不是 Phase 6/7/8 該處理**：

### Anti-Goal A：把 chatbot 變成 LLM agent

User 沒要求 chatbot 主動去 tool-call、執行 code、控制 UI 元素。User 只要求 chatbot **知道**狀態並能**提議**改變，不是 chatbot 自己**執行**改變。

Phase 7 的 write-side 是「chatbot 提議 → user 在 UI confirm → 系統執行」，不是「chatbot 直接寫 engine state」。

### Anti-Goal B：跨產品的 chatbot 統合

當前有兩個 chatbot service：

- `MistralChatbotService`（日常對話）
- `CalibrationChatbotService`（calibration flow 中的引導對話）

Phase 6/7/8 只動 MistralChatbotService。CalibrationChatbotService 已在 Calibration sprint 中設計完成，不要重新動它。

### Anti-Goal C：Chatbot 的對話品質本身

「對話縱深感」goal 1 不等於「讓 chatbot 變得更會聊天」。Phase 6/7/8 提供的是**正確的 context**讓 chatbot 有材料對話，不是改 chatbot 的 personality 或語氣。

如果 user 對 chatbot 的「語氣 / 風格 / 詞彙」有不滿，那是另一個 sprint 的工作（prompt engineering sprint），不在 Phase 6/7/8 範圍。

### Anti-Goal D：Cross-device / Cloud sync

「縱深感」goal 1 的「歷史記憶」是**這台機器上的歷史**，不是跨裝置同步。當前 `habituation_profile.json` + `longitudinal_observations` 都在 local file system。

Phase 6/7/8 不引入 cloud sync、不引入 cross-device、不引入 user account 概念。

### Anti-Goal E：自動學習與調參

User 沒要求「chatbot 自動幫我調 sensitivity，不用問我」。Phase 7 的所有 write-side 都需要 user explicit confirm，不是 chatbot 自主決策。

「Auto-tune」這類功能是另一個產品方向（甚至是另一個 sprint 都不一定），明確 out-of-scope。

---

## 五、Sprint 整體成功定義（Acceptance Criteria 最高層）

完成 Phase 6/7/8 後，下列 user scenarios 應該全部 demoable：

### Scenario A：第一次對話的「自我介紹」

User 第一次跟 chatbot 對話（calibration 之後不久），chatbot 的第一句應該是 grounded in user 當前狀態。例如：

```
Chatbot: 你昨天完成的 calibration 顯示你屬於 Sensitive profile，
        habituation level 從 10 開始。剛剛我看你正在使用 SenseEase，
        當前 Tier 4 — Pressure 67。要不要聊聊你今天視覺感受？
```

而**不是**：

```
Chatbot: 你好，今天有什麼我可以幫你的嗎？
```

### Scenario B：跨對話的 longitudinal 記憶

User 隔三週後再跟 chatbot 對話，chatbot 應該能 reference 過去的記錄：

```
User: 最近 SenseEase 用起來怎麼樣？
Chatbot: 過去三週你的 habituation level 從 10 提升到 14.5，這是穩定進步。
        Tier peak 也從第一週的 8 降到本週的 6。你說「今天感覺比上週舒服」
        的次數有增加。看起來你的視覺系統適應得不錯。
```

### Scenario C：Chatbot 提議調整 + User confirm

```
Chatbot: 我注意到你過去 5 天 tier 大多在 6-7。要不要試試把 sensitivity 
        threshold 從目前的 50 調到 60？這會讓系統提早一點介入，可能 
        對你最近的疲勞感有幫助。
User: 好啊試試看
Chatbot: 收到。請打開 Settings 確認這個變更。
[Settings window 跳出 + 有一個 highlighted 的「Apply chatbot suggestion」按鈕]
User clicks confirm → engine 更新 threshold → chatbot 在後續對話追蹤效果
```

### Scenario D：Medical advisory recall

```
User: 我覺得 SenseEase 太敏感了，可以全部關掉嗎？
Chatbot: 我可以幫你關，但想先確認一件事——你 4 個月前的 calibration 
        提到有前庭診斷紀錄。當時系統為這層加了額外保護。
        
        如果你現在的診斷狀況有變化，或你只是想暫時休息，我們可以選不同的 
        方法：
        a) 全部關掉（不建議，但你決定）
        b) 啟用省電模式，暫停監測但保留 UI
        c) 只調整某個敏感度，不全關
        
        你想怎麼處理？
```

而**不是**直接照辦關掉所有 protection。

### Scenario E：Resume after Power Save

```
[User 啟用 Power Save 連續 3 天]
[User 打開 chatbot 對話]
User: 嗨，今天感覺如何？
Chatbot: 你已經省電模式 3 天，我這段時間沒有你的 OFI 資料。
        要不要重新啟用 sensing 一段時間，我們再聊聊狀態？
        或者你想說說這 3 天感受？
```

而**不是**假裝 sensing 還在跑 / 用三天前的 stale data 對話。

---

## 六、Section 1 結束的 user check-in

下面是給 user 的問題，請 raise 任何不對的地方：

### 對齊問題 1：User 真實 Goal 五條對嗎？

我列了 5 條 user goal：

1. 對話縱深感
2. 狀態內在一致性
3. 雙向 agency
4. 安全網記憶
5. Habituation progression 尊重

**有沒有遺漏？有沒有 over-extrapolate？哪一條 user 不認同？**

### 對齊問題 2：5 個 Anti-Goal 接受嗎？

我界定了 5 條 out-of-scope：

A. LLM agent / tool-calling B. Cross-product chatbot 統合 C. Chatbot 對話風格本身 D. Cross-device sync E. Auto-tune

**有沒有應該包含但被我排除的？或反過來，有沒有 user 想做但我沒列的？**

### 對齊問題 3：5 個 Scenario 是 user 期待的成品嗎？

A. 第一次對話 grounded in state B. Longitudinal 記憶跨對話 C. Chatbot propose → user confirm flow D. Medical advisory recall E. Resume after Power Save 的 awareness

**哪個 scenario 不是 user 想要的？或哪個 scenario user 想要更深 / 不一樣？**

### 對齊問題 4：Section 1 寫得太深 / 太淺？

我用了相對抽象的詞（「斷層」「failsafe」「agency」）— **如果 user 覺得太哲學化，下面 section 我會降一階寫得更操作型**。

如果 user 覺得這個深度剛好甚至想更深，我繼續維持。

---

## 七、下一步

如果上面 4 個對齊問題 user 答「OK / 沒問題」之類，我直接進 Section 2 (Architecture Audit)。

Section 2 需要 user 或 Agent A 幫我 paste 以下檔案內容：

```
1. src/SenseEase.Core/Inference/HabituationEngine.cs (整個檔案，主要看 public API)
2. src/SenseEase.Core/State/HabituationProfileSnapshot.cs (Schema v2 完整定義)
3. src/SenseEase.Infrastructure/Real/MistralChatbotService.cs (整個檔案)
4. src/SenseEase.Infrastructure/Real/CalibrationChatbotService.cs (整個檔案)
5. src/SenseEase.Core/Inference/CalibrationProcessor.cs (write-side reference)
```

可以一次 paste 五個檔案，也可以分批。Section 2 我會等檔案到齊才寫。

如果 user 還在處理 Bar/Hover，**不急著 paste**——我這邊等。Section 1 + Section 2 中間自然有 user check-in 的暫停點，這是設計上的而非阻塞。

---

# Section 1 Check-in 收到 — User 沒明確 raise 問題、直接啟動 Section 2

User 訊息簡短「Agent 提供所需內容 + Section 1 修正 + Section 2 啟動」——隱含 Section 1 user 沒有反對意見，繼續往下走。

但 advisor 我必須先 surface 一個觀察：**Section 1 的對齊問題 1-4 user 沒明確答**。沒答可能等於「都 OK」也可能等於「沒看完細節直接 next」。

兩種情況差異大。但既然 user 已經請 Agent A 把 5 個 code reference paste 過來，這個動作本身代表 user 接受 Section 1 的方向（否則沒理由 paste code），我採信「都 OK」這個解讀，繼續 Section 2。

如果 Section 2 寫到某個點 user 發現「等等 Section 1 那條我不同意」，隨時可以回頭調整。

---

## 對 Agent A code paste 的 quick acknowledgement

Agent A 的 paste 品質**極高**。要 highlight 三個給後續 section 的 reader 用：

1. **Path 修正**（HabituationProfileSnapshot 在 Models 不是 State）已記下，後續 section 用對的 path
2. **CalibrationPrompts.cs 是另一個檔**，我會在 Section 2 / Section 4 寫到 system prompt 設計時請 Agent A 再 paste
3. **Agent A 自己幫我整理「summary for the advisor」段**——14 read-only properties、22 schema fields、3 個 chatbot 已有的 safety pattern——這個 summary 是 prep work 加成，**寫進我 docs 的時候會 credit Agent A 的貢獻**

下面進入 Section 2。

---

# Section 2: System Architecture Audit

把現有的 codebase 視為一個**有機系統**，盤點：

- 各元件的職責邊界
- 元件之間的資訊流（誰讀誰寫）
- Habituation Phase 6/7/8 sprint 要插入的「介面切口」在哪
- 既有 design pattern 哪些可以複製、哪些必須避免

---

## 2.1 系統元件全景圖

```
┌─────────────────────────────────────────────────────────────────────┐
│                         SenseEase Application                        │
│                                                                      │
│  ┌──────────────────────┐         ┌──────────────────────────┐      │
│  │  WgcSensingProvider  │ ──OFI──▶│  HabituationEngine       │      │
│  │  (screen capture)    │         │  (pure logic, no IO)     │      │
│  └──────────────────────┘         └──────────────────────────┘      │
│           │                                  │                       │
│           │                                  │ state                 │
│           │                                  ▼                       │
│           │                       ┌──────────────────────────┐      │
│           │                       │ HabituationProfileSnap   │      │
│           │                       │ (Schema v2 contract)     │      │
│           │                       └──────────────────────────┘      │
│           │                                  │                       │
│           │                                  │                       │
│           │                                  ▼                       │
│           │                       ┌──────────────────────────┐      │
│           │                       │ FileHabituationProfile   │      │
│           │                       │ Store (atomic JSON IO)   │      │
│           │                       └──────────────────────────┘      │
│           │                                                          │
│           │                                                          │
│           ▼                                                          │
│  ┌──────────────────────┐         ┌──────────────────────────┐      │
│  │  IAdjustmentLog      │ ──────▶ │ MistralChatbotService    │      │
│  │  IEventLogStore      │  ctx    │ (per-turn LLM)           │      │
│  └──────────────────────┘         └──────────────────────────┘      │
│                                              ▲                       │
│                                              │ (zero hook today)     │
│                                              │                       │
│  ┌──────────────────────┐                    │                       │
│  │ CalibrationChatbot   │ ─── separate ──────┘                       │
│  │ Service              │     transcript                              │
│  │ (calibration only)   │     file                                    │
│  └──────────────────────┘                                            │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**關鍵觀察**：

1. **HabituationEngine 與 MistralChatbotService 之間目前是「斷開」的**——兩個元件互相不知道對方存在
2. **Schema v2 的 `HabituationProfileSnapshot` 已經為 Phase 6/7 做好契約**——v2 引入的 `longitudinal_observations`、`calibration_count`、`medical_advisory_shown` 等欄位明顯是 chatbot 整合的預備
3. **CalibrationProcessor 是 write-side 的 reference pattern**——pure logic 接受 input、產生 typed result、caller 負責 apply。Phase 7 的 chatbot write-side 應該複製這個 pattern

---

## 2.2 既有資訊流分析

### 當前 MistralChatbotService 每 turn 收到什麼 context

從 Agent A paste 看到，當前 chatbot 每 turn 收到的 context 是：

```
[RecentAdjustments]
<last 6 shader-parameter deltas with timestamp/parameter/delta/source/utterance>

[RecentEvents]  
<last 6 event-log entries>

State: <SenseEaseState built by caller — current sensor/engine state>

User: <text>

[Language pin directive]

Return JSON only.
```

**這個 State: 行的構造是 caller 負責**——`BuildHistoryPrefixFromDeps` 只組裝 `[RecentAdjustments]` + `[RecentEvents]`。State 怎麼來、有什麼欄位、如何 format，**caller 自己決定**。

**這對 Phase 6 設計極關鍵**：意味著 Phase 6 的 prompt injection 有兩種可行 path：

**Path I — Caller-side enrichment**（最少改 chatbot service）：

- 不動 MistralChatbotService 內部
- 在 caller 那一層（MainViewModel 或 ChatbotViewModel）build State 字串時加入 habituation 資料
- State 字串內含一個 `[habituation_state]` block

**Path II — Service-side injection**（chatbot service 內部變更）：

- MistralChatbotService 新增一個 `IHabituationStateReader` dependency
- 服務自己組裝 `[habituation_state]` block 到 history prefix
- 與既有 IAdjustmentLog / IEventLogStore 對等的設計

兩條 path 各有 trade-off，我會在 Section 4 詳細評估。**但這個選擇本身就是 Phase 6 的第一個重大設計決策**，user 後面要簽核。

### CalibrationChatbotService vs MistralChatbotService 的對等差異

兩個 service 都呼叫 LLM，但設計差異**對 Phase 6/7 有重大意義**：

|維度|CalibrationChatbotService|MistralChatbotService|
|---|---|---|
|**目的**|引導式對話（5 個問題收斂）|開放式對話（任何主題）|
|**生命週期**|Session-based（StartAsync → EndSession）|Stateless per turn|
|**回合預算**|Hard cap 9 turns|無限制|
|**轉錄保存**|JSONL 檔案 per session|由 IEventLogStore 處理|
|**Structured output**|`[CALIBRATION_DELTA]` block + parser|`{ intent, severity, ..., deltas }` JSON envelope|
|**Safety floor**|self-harm phrase list 觸發短路|**無**（!）|
|**Completion event**|`Completed?.Invoke(responses, isComplete)`|無（每 turn 獨立）|

**關鍵發現 1**：MistralChatbotService **沒有 self-harm safety floor**。CalibrationChatbotService 有。

這是**現有系統的安全漏洞**——user 在「日常 chatbot 對話」中如果說「我想結束這一切」，MistralChatbotService 會把它當作 ReportDiscomfort 處理，可能 emit shader deltas，**完全不 surface crisis resources**。

Phase 7 設計 chatbot WRITE 時，**必須同時把 safety floor 移植到 MistralChatbotService**。這不是 Phase 7 的「新功能」，是補既有系統的安全債。

**關鍵發現 2**：MistralChatbotService 是 stateless per turn，但 Phase 6 想要的「longitudinal 記憶」與「對話縱深感」**本質上是 stateful 的**。

兩個解法方向：

- **Option A**：每 turn 從 `HabituationProfileSnapshot` 重新讀取 longitudinal_observations，注入 prompt。Chatbot 自己不記，每次「假裝記得」
- **Option B**：給 MistralChatbotService 加 session 概念（類似 CalibrationChatbotService），有 conversation history

Option A 簡單但有 limits（每次都讀同一份 longitudinal_observations，沒法在 session 內累積新觀察）。Option B 完整但 scope creep。

Section 4 會詳細比較。**這是 Phase 6 的第二個重大設計決策**。

---

## 2.3 HabituationEngine 的 read/write surface 分析

### Read surface（給 Phase 6 prompt injection 用）

從 Agent A paste 看到 14 個 public read-only properties。Phase 6 不一定全都注入 chatbot context——應該分**核心**與**輔助**：

**核心 read（user-perceivable，必注入）**：

```
- HabituationLevel (0-100, 進度)
- HabituationOffset (絕對 pp 值, 「earned」進度)
- CurrentProfile (RESILIENT/TYPICAL/SENSITIVE)
- CurrentEngineState (RUNNING/COOLDOWN/PAUSED_AWAITING_USER_CONFIRM)
- IsAwaitingUserConfirm (true → chatbot 必須在第一句提及)
- LastSpikeAtUtc (近期 spike 時間, 對話起手式參考)
```

**輔助 read（chatbot 推理用，不必每 turn 都注入）**：

```
- SevereStrikeCount (累積壓力 indicator)
- ProfileSource (chatbot 知道是 calibration 設的還是自己改的)
- IsExternallyPaused (區分 manual vs auto pause)
- Phase (UI projection, 與 EngineState 對等但不同視角)
```

**Schema v2 的 read（chatbot 真正用得到的 longitudinal context）**：

```
- CalibrationCount (用於 chatbot 措辭「你是新用戶」vs「你已經做過 N 次 calibration」)
- LastCalibrationAtIso (時序定位)
- CalibrationConfidence (chatbot 對自己的 advice 該多 confident)
- LongitudinalObservations (跨對話累積觀察)
- MedicalAdvisoryShown (Scenario D 的關鍵)
- PersonalPressureScale (chatbot 知道 user 的 sensitivity 校準位置)
```

**advisor 視角的優先級**：必注入的核心 read 應該是 **最小 必要集合**，避免 prompt 膨脹影響 LLM 性能。輔助 read 可以「按需」由 chatbot 自己 ask（例如 chatbot output 一個 `[REQUEST_CONTEXT: longitudinal_observations]` token，下次 turn 才 inject）。但這個 lazy loading 機制是 nice-to-have，Phase 6 預設 always inject 全部。

### Write surface（給 Phase 7 chatbot-as-actuator 用）

從 Agent A paste，HabituationEngine 已暴露的 write API：

```
- SetProfile(profile, source="chatbot_override")  ← 已預留 chatbot 路徑
- NotifyDiscomfort(severe, vimsTier)              ← 0.5 weight 已預留 chatbot
- AcknowledgeAndResume()                          ← 高 tier pause 解除
- EffectiveTrigger setter                         ← calibration 在用，可重用
- SevereStrikeLimitOverride setter                ← calibration 在用，可重用
- SetPaused(bool)                                 ← 既有 manual pause
- SetPersonalization(PersonalWeight)              ← FOV history 個人化
```

**這個 write surface 已經完整**——Phase 7 不需要動 engine 加新 method，只需要：

1. 設計 chatbot output 的 `[PROFILE_OVERRIDE]` 等 structured token
2. 寫 parser（仿 CalibrationLlmDeltaParser pattern）
3. 寫 caller-side applier（仿 CalibrationApplicator pattern）
4. 設計 user confirm UI flow

**這個發現極重要**：HabituationEngine API surface 已預先設計為「可被 chatbot 操作」——Phase 1-5 sprint 期間的設計者已預想到 Phase 6/7。Agent A paste 中 `SetProfile` 的 `source="chatbot_override"` 是 documented 在 method signature 裡的，這不是 advisor 我幫 user 創造的 affordance，是既有設計。

---

## 2.4 Power Save mode 對 chatbot 的影響分析

Pop-up sprint 引入了 Power Save mode（暫停 WGC sensing）。這對 Phase 6/7 chatbot 有什麼意義？

### Power Save ON 時的系統狀態

從 sprint 累積記憶判斷：

- WgcSensingProvider.Pause() → 不寫新 OFI
- HabituationEngine 不收新 sample → state 凍結
- HabituationProfileSnapshot 持續可讀（從檔案）
- VimsTier 凍結在最後一個值（或回到 baseline，取決於實作）

### Chatbot 在 Power Save 下應該怎麼行為

**Anti-pattern**：chatbot 看起來不知道 sensing 已關，繼續引用 stale tier 數字

```
[Power Save ON 3 天]
User: 嗨
Chatbot: 你當前 Tier 4 - Pressure 67 (← 3 天前的數字)
```

這違反 Goal 2（狀態內在一致性）。

**正確 pattern**：chatbot 明確 acknowledge sensing 暫停，主動 propose resume

```
[Power Save ON 3 天]
User: 嗨
Chatbot: 你已開省電模式 3 天，這段時間我沒有你的 OFI 資料。
        要不要重新啟用 sensing 一段時間，我們再聊狀態？
```

**這要求 Phase 6 prompt injection 必須包含 Power Save state 資訊**——但目前 `HabituationProfileSnapshot` 沒有 power_save_enabled 欄位。

**Section 4 設計決策點**：

- **Option α**：在 Schema v2 加 `power_save_enabled` + `power_save_since_iso` 兩個新欄位（Schema v3）
- **Option β**：不動 Schema，由 caller 在 build State 字串時動態查 UserPreferences
- **Option γ**：完全不在 chatbot context 暴露 Power Save 狀態，假裝是 user 主動告訴 chatbot

advisor 傾向 **Option β**（caller-side 動態查）——Schema 升級的 cost 比 caller 多查一個 preference 高得多。

但這條決策關聯到 user goal 5（habituation 尊重）——如果 power save 期間 longitudinal observations 也凍結，chatbot 與 user 對話時的「歷史記憶」會有 gap。是否該記錄 power save 期間？這是 Section 4 要處理的 corner case。

---

## 2.5 既有 CalibrationProcessor 作為 Phase 7 設計範本

Agent A 的 paste 把 CalibrationProcessor 完整貼出來。**這是 Phase 7 必須模仿的 architectural pattern**：

```
[Pure logic processor] ─── input → typed result ───▶ [Caller applies result]

CalibrationResponses  +  CalibrationSensorData
       │
       ▼
CalibrationProcessor.Process()  (pure, no IO)
       │
       ▼
CalibrationResult (record)
       │
       ▼
CalibrationApplicator.Apply()   (caller-side, does IO + engine calls)
```

對應到 Phase 7：

```
ChatbotProfileOverrideRequest  +  CurrentHabituationState
       │
       ▼
HabituationOverrideProcessor.Process()  (pure, no IO, parses LLM token block)
       │
       ▼
HabituationOverrideResult (record)
       │
       ▼
HabituationOverrideApplicator.Apply()   (caller-side, 需 user confirm UI)
```

**Phase 7 必須維持的紀律**：

1. **Processor 是 pure logic**——可單元測試、不碰 engine、不寫檔
2. **Applicator 負責所有 side effect**——engine.SetProfile、寫 snapshot、updateUI
3. **User confirm 是 Applicator 內部**——不在 processor 層做政策判斷
4. **LLM output 有明確的 structured token**——parser 不依賴 LLM 自由發揮的格式

這個 pattern 在 Calibration sprint 已驗證過（Stage 1-7 累積），Phase 7 直接複製，**不要重新設計**。

---

## 2.6 Phase 6/7/8 的「介面切口」清單

從 architecture audit，可以明確標出 Phase 6/7/8 需要動到的「切口」（新加或修改）：

### Phase 6 切口（read-only）

|#|切口|修改類型|風險等級|
|---|---|---|---|
|1|設計 `[habituation_state]` block 文字格式|新增（純設計）|低|
|2|MistralChatbotService 的 history prefix 構造|修改（或加 caller-side enrichment）|中|
|3|加 `IHabituationStateReader` 介面（若選 Path II）|新增|中|
|4|Caller 側 builder（若選 Path I）|新增|低|
|5|LongitudinalObservations 的讀取邏輯|修改|低|
|6|Power Save state 注入|新增|低|

### Phase 7 切口（write-side，含 user confirm）

|#|切口|修改類型|風險等級|
|---|---|---|---|
|1|Chatbot LLM prompt 加入 `[PROFILE_OVERRIDE]` 等 structured token 文法|修改 prompt|高|
|2|新增 HabituationOverrideParser|新增|低|
|3|新增 HabituationOverrideProcessor|新增|低|
|4|新增 HabituationOverrideApplicator|新增|中|
|5|新增 user confirm UI（Settings 內 highlighted 區 / 或新 dialog）|新增|中|
|6|補 MistralChatbotService 的 self-harm safety floor|新增|高（安全債）|
|7|LongitudinalObservations write path|新增|中|
|8|medical_advisory_shown read + chatbot 行為 contract|修改|高|
|9|resume_acknowledged 流程（AcknowledgeAndResume 由 chatbot 觸發路徑）|新增|中|

### Phase 8 切口（acceptance + docs）

|#|切口|修改類型|風險等級|
|---|---|---|---|
|1|E2E scenario tests（5 個 user scenario）|新增|中|
|2|docs/HABITUATION-CHATBOT-INTEGRATION.md|新增|低|
|3|4 週時序 acceptance test|新增（manual 或 simulation）|高|
|4|Pop-up sprint lessons learned 對 Habituation sprint 的 carry-forward review|新增 docs|低|

**總體觀察**：

- Phase 6 的切口都是中低風險，主要是 prompt 設計 + 資料管線
- Phase 7 的切口風險集中在「prompt 修改」與「safety floor 補洞」這兩條，**這兩條是 sprint 的真正硬骨頭**
- Phase 8 的時序 acceptance test 風險高——4 週的真實時間無法等，需要 simulated time injection

---

## 2.7 跨 Sprint 的依賴清單

Habituation Phase 6/7/8 sprint **依賴**以下 prior sprint 的 deliverable：

### 來自 Calibration sprint（已完成）

- ✅ `HabituationProfileSnapshot` Schema v2（含 longitudinal_observations 等欄位）
- ✅ `CalibrationProcessor` 作為 pure logic pattern reference
- ✅ `CalibrationChatbotService` 作為 safety floor + transcript pattern reference
- ✅ `CalibrationApplicator` 作為 caller-side applier pattern reference
- ✅ `FileHabituationProfileStore` 作為 atomic JSON IO 範本

### 來自 Pop-up sprint（仍在收尾中）

- ⏸ Power Save mode 的 UserPreferences 欄位 + 行為
- ⏸ Card 上的「設定」按鈕能呼叫 SettingsWindow
- ⏸ Card 上的「開啟主視窗」按鈕（用戶從 chatbot 對話 jump 到 dashboard 的路徑）
- ⏸ Bar/Tray 在 Power Save 狀態的視覺呈現

**Pop-up sprint 必須完成才能 Habituation 啟動**——這條已被 user 在 Q4 確認為「No problem」（即接受兩 sprint 並行 prep，但 Habituation execute 等 Pop-up tag）。

### 來自還未啟動的 E2E Test Infra sprint（必須先做）

- ❌ FlaUI 或 WpfApplicationFactory 整合
- ❌ App launch / tray spawn / bar render / chatbot dialog flow 的 E2E test 範本
- ❌ Library-without-source 契約測試 pattern
- ❌ dotnet run vs published exe parity check infrastructure

**這個依賴 critical**——Pop-up sprint 累積 7+ 條 lessons learned 全部指向「需要 E2E test 才能抓到的失敗模式」。Habituation Phase 6/7/8 比 Pop-up 更複雜（LLM 整合 + 多 surface 互動），沒 E2E infra 開工 = 重蹈 Pop-up sprint 覆轍。

**advisor 強烈建議**：把 E2E Test Infra 當作**獨立的小 sprint**先做完，再啟動 Habituation Phase 6/7/8。範圍清楚、效益直接、風險低。

---

## 2.8 Section 2 結束的觀察彙整

```
✅ 系統元件邊界清楚，HabituationEngine API surface 已為 chatbot 預留
✅ Schema v2 已包含 Phase 6/7 需要的所有欄位（不必再升級 schema）
✅ CalibrationProcessor pattern 是 Phase 7 write-side 的明確範本
⚠️ MistralChatbotService 缺 self-harm safety floor — Phase 7 必須補
⚠️ MistralChatbotService 是 stateless per turn — Phase 6 longitudinal 記憶需設計
⚠️ Power Save 狀態目前未進 chatbot context — Phase 6 需處理
❌ E2E Test Infra 不存在 — Habituation 啟動前必須先做
```

---

## Section 2 結束的 user check-in

### 對齊問題 5：架構切口清單對嗎？

我列了 Phase 6/7/8 共 19 個切口（6 + 9 + 4）。**有沒有應該包含但被我遺漏的？或反過來，哪個切口你覺得不該動？**

特別 highlight 三個高風險切口：

- Phase 7-1（修改 prompt 加 structured token）
- Phase 7-6（補 safety floor）
- Phase 7-8（medical advisory 行為 contract）

這三條 user 必須確認方向是對的。

### 對齊問題 6：E2E Test Infra 作為獨立 sprint 你接受嗎？

我建議把 E2E test infra 從 Habituation Phase 6 拉出來，**獨立做一個 mini-sprint**（估 1-2 天）。完成後再啟動 Habituation Phase 6。

理由：

- Habituation Phase 6 已經夠複雜，再扛 infra 建置會 overload
- E2E test infra 對所有後續 sprint 都有幫助（不只 Habituation）
- 獨立 sprint 範圍清楚、可單獨 review

**user 接受這個拆分嗎？還是堅持 E2E infra 應該包含在 Habituation Phase 6 內？**

### 對齊問題 7：Path I vs Path II 的偏好

Section 2.2 提到 Phase 6 prompt injection 有兩條 path：

- **Path I**：Caller-side enrichment（不動 MistralChatbotService 內部）
- **Path II**：Service-side injection（chatbot service 加 IHabituationStateReader dependency）

Section 4 我會做詳細評估，但 user 如果現在已經有強烈偏好可以先說。

---

## Section 3 / 4 預告

Section 2 結束後，下兩個 section 會深入 design：

- **Section 3**: 設計目標分解（把 5 個 user goal 翻譯成可實作的 design specification）
- **Section 4**: Phase 6/7/8 詳細 spec draft（含具體 prompt template、parser format、UI flow、test scenarios）

-----
# Section 3: Design Goal Decomposition

把 Section 1 的 5 個 user goal 翻譯成可實作的 design specification。每個 goal 拆解為：

- **Observable behavior**：完成後 user 在哪些 surface 能看到的差異
- **Engineering requirement**：實作層需要什麼新元件 / 修改
- **Acceptance test seed**：Section 4 / Phase 8 可直接用的 test 設計
- **Failure mode**：這個 goal 沒做好會以什麼形式暴露

每個 goal 後接 **Anti-spec**：明確 _不要_ 過度解讀的邊界。

---

## Goal 1: 對話縱深感

### Restated

Chatbot 對話應該有「歷史的厚度」。User 三天前說過的事、上週做的 calibration、過去四週的 habituation progression，chatbot 都應該能在合適的對話時機 reference。User 感受到的是「這個 chatbot 認識我」。

### Observable behavior

|Surface|Before|After|
|---|---|---|
|First chatbot turn after calibration|「你好，今天有什麼可以幫你？」|「你昨天 calibration 屬於 Sensitive profile，是新用戶。今天視覺感受如何？」|
|Chatbot turn 3 weeks later|「請告訴我你目前感覺」|「過去三週你的 habituation level 從 10 升到 14.5，這代表你正在適應。最近感覺有變化嗎？」|
|User mention「上次」「之前」|Chatbot 不知道 user 指什麼|Chatbot 能 reference 至少最近一次 calibration 與 longitudinal observation|

### Engineering requirement

**Read-side（Phase 6）**：

- `HabituationProfileSnapshot` 完整內容注入 chatbot prompt
- 注入內容包含：當前 tier、habituation level、profile、calibration_count、last_calibration_at_iso、longitudinal_observations、symptom_dominance_primary、medical_advisory_shown

**Write-side（Phase 7）**：

- 設計 chatbot 「append longitudinal observation」的 structured token
- 每次 chatbot 對話結束（或 chatbot 自己判斷重要時），可 emit `[OBSERVATION_ADD] <text> [/OBSERVATION_ADD]`
- Parser 提取 → applicator 寫入 snapshot

**Format decision needed (Section 4)**：

- `longitudinal_observations` 目前是 `List<string>`，無時間戳、無分類
    
- Phase 7 寫入時要不要附加時間戳與分類？
    
    ```
    選項 A: 維持 plain string list，chatbot 自己在 string 內含時間
    選項 B: 結構化升級為 List<Observation> with timestamp/category/text
    ```
    
    Anti-spec：不要把 longitudinal_observations 變成 user 可編輯的 log——它是 chatbot 的工作記憶，不是 user UI。
    

### Acceptance test seed

```
Scenario 1A: Fresh calibration → first chatbot turn references it
  Given: calibration completed at T-1h, Sensitive profile, 0 prior calibrations  
  When: user opens chatbot tab and types greeting
  Then: assistant first response contains:
        - calibration was recent
        - profile is Sensitive
        - user-friendly framing (not raw "Sensitive" jargon)

Scenario 1B: Multi-week longitudinal recall
  Given: 4 weeks of habituation history, calibration_count=2
  When: user asks "最近用 SenseEase 怎麼樣"
  Then: assistant response references at least one specific past
        observation OR habituation_level trend
```

### Failure mode

如果 Goal 1 沒做好：

- Chatbot 對話讀起來像 generic 助手，無 SenseEase 識別度
- User 重複告訴 chatbot 同樣的 state，造成「跟笨蛋對話」感受
- 三週後 user 可能完全停止用 chatbot 功能（沉默流失）

---

## Goal 2: 狀態的內在一致性

### Restated

User 在不同 surface 看到的 SenseEase 應該是「同一個系統」。Dashboard 顯示「Tier 4」時，chatbot 對話也知道 user 在 Tier 4，不需要 user 重複告訴它。Card 上的數字、Bar 顏色、Tray 顏色、chatbot 對話 context 全部 sync。

### Observable behavior

|Test|Pass criteria|
|---|---|
|Dashboard 顯示 Tier 6|chatbot 對話開始時知道 Tier 6|
|User 在 Power Save 開啟下打開 chatbot|chatbot 第一句 acknowledge 「sensing 暫停中」|
|User 剛 acknowledged 一個 tier 8 pause|chatbot 知道 user 剛 resume，可詢問狀態|
|User 啟動 Engage 保護|chatbot 對話 context 知道保護開啟|

### Engineering requirement

**核心觀察**：「狀態一致性」不是 Phase 6/7 才開始的工作——`HabituationProfileSnapshot` 已經是 single source of truth。Phase 6 只是讓 chatbot 「也讀」這個 source。

需要新增的 read 欄位（Section 2.4 提過）：

- **Power Save state**：當前 schema 沒有，需 caller-side 動態查 UserPreferences 注入
- **Recent Engage activation**：可從 IEventLogStore 抓最近事件（既有 hook）

**Anti-spec**：不要把 chatbot 變成 dashboard 的鏡像。Chatbot 不需要 reactive 訂閱 state 變化、不需要 push notification。它只需要在 user 發起對話的瞬間「拿到當前 snapshot」即可。

### Acceptance test seed

```
Scenario 2A: Cross-surface consistency
  Given: Dashboard shows Tier 6, Bar shows amber
  When: user opens chatbot and types anything
  Then: chatbot's state context contains current_vims_tier=6

Scenario 2B: Power Save awareness
  Given: Power Save enabled 30 minutes ago
  When: user opens chatbot
  Then: chatbot first response acknowledges sensing is paused

Scenario 2C: Just-resumed acknowledgment
  Given: HabituationEngine just resumed from PausedAwaitingUserConfirm
         in last 60 seconds
  When: user opens chatbot
  Then: chatbot offers a check-in about user readiness, NOT generic intro
```

### Failure mode

如果 Goal 2 沒做好：

- User 在 Power Save 時與 chatbot 對話，chatbot 假裝 sensing 還在跑（用 stale 數字 reason）
- User 明明在 Tier 8 高壓力，chatbot 不知道，繼續 chatty 對話
- 系統各 surface 像不同人格，user 開始質疑 SenseEase 整體可信度

---

## Goal 3: 雙向 agency

### Restated

Chatbot 不只是被動回答，也能主動提議 system 變更，user 同意後系統真的會改。但**所有實際變更必須經過 user 明確 confirm**——chatbot 沒有直接 mutate engine 的權力。

### Observable behavior

|Scenario|Chatbot action|User experience|
|---|---|---|
|User 連續多日 Tier 6+|Chatbot 觀察並建議調整 sensitivity|Card 或 dialog 出現「Apply chatbot suggestion」highlighted 區|
|User 抱怨「太敏感了」|Chatbot 提議調 personal_pressure_scale|Same as above|
|User 表示「想試試更敏感」|Chatbot 提議反向調整|Same as above|
|User 拒絕建議|Chatbot 不再 push 同類建議（短期內）|對話自然轉向|

### Engineering requirement

**LLM output 文法**（Section 4 詳細設計）：

```
chatbot 訊息範例：
"我注意到你過去五天 tier 在 6-7。要不要試試把 sensitivity 調高？
[PROFILE_OVERRIDE]
{
  "intent": "adjust_sensitivity",
  "target_field": "personal_pressure_scale",
  "proposed_value": 1.15,
  "rationale": "5 days in Tier 6-7 suggests under-detection",
  "expires_at_iso": "2026-06-02T12:00:00Z"
}
[/PROFILE_OVERRIDE]
有興趣的話幫我打開 Settings 確認。"
```

**Parser**（仿 CalibrationLlmDeltaParser）：

- 提取 block
- 從 chatbot 可見回應中 strip block
- 產生 `ChatbotProposal` record

**User confirm UI flow**（兩種設計，Section 4 評估）：

- **Flow A**：Card 上彈出「pending proposal」區域，user 點 Accept/Decline
- **Flow B**：SettingsWindow 開啟時，highlighted 區顯示 pending proposal
- **Flow C**：System tray 通知 + 點擊開啟 dialog

**Applicator**：

- User accept → 對應的 engine.SetXXX 呼叫
- User decline → 紀錄 declined proposal，影響未來 prompt（chatbot 知道別再 push）

### Critical safety boundary

**Anti-spec**：

- Chatbot **不能直接 mutate** engine state
- 即使 user 在對話中說「好，調吧」，仍必須走 UI confirm（防止 LLM hallucination 後 user 沒看清楚就同意）
- 一個 proposal 如果 user 沒在 expires_at_iso 之前 confirm，自動 invalidate（防止舊建議突然生效）
- Chatbot 一次只能有一個 active proposal（防 spam）
- **Vestibular_diagnosed 用戶**對於「降低 sensitivity / 放鬆 protection」類 proposal 有額外保護（見 Goal 4）

### Acceptance test seed

```
Scenario 3A: Propose → accept → apply
  Given: 5 days of Tier 6-7 history
  When: user opens chatbot, chatbot proposes scale increase
        user accepts in confirm UI
  Then: HabituationProfileSnapshot.PersonalPressureScale updated
        Next sensing tick uses new threshold

Scenario 3B: Propose → decline → no future spam
  Given: chatbot proposed scale increase, user declined
  When: 24 hours later, similar conditions
  Then: chatbot does NOT re-propose same change
        chatbot DOES note declined proposal in observation

Scenario 3C: Propose → ignore → auto-expire
  Given: chatbot proposed change at T, expires at T+24h
  When: user does nothing for 25 hours
  Then: proposal silently invalidates
        No "ghost apply" on next interaction
```

### Failure mode

如果 Goal 3 沒做好：

- **過度激進**：chatbot 主動改 engine，user 沒同意系統就變了 → 信任崩潰
- **過度保守**：chatbot 只能聊天不能做事 → user 覺得 chatbot 沒用
- **Spam**：chatbot 反覆 propose 同樣事情 → 用戶疲勞
- **Hallucination apply**：LLM 隨意輸出 token 結構錯誤的 proposal 被 parser 誤接受 → 系統行為飄移

---

## Goal 4: 安全網的記憶

### Restated

User 在 calibration 中提過的關鍵 medical context（特別是 vestibular_diagnosed），系統必須跨 session 記住。當 user 在後續對話中要求「減弱 / 關閉 protection」時，chatbot 必須 surface 這條歷史，給 user 重新思考的機會。

### Observable behavior

|Scenario|Without Goal 4|With Goal 4|
|---|---|---|
|Vestibular user 問「能關掉 SenseEase 嗎」|Chatbot 直接告訴怎麼關|Chatbot surface 過去診斷，問「狀況有變化嗎？」|
|Vestibular user 提議降 sensitivity|Chatbot 直接照辦|Chatbot 提醒 vestibular cap，仍允許但 propose 較保守數值|
|非 vestibular user 問同樣問題|直接協助|直接協助（不引入無關 medical context）|

### Engineering requirement

**Read（已存在）**：

- `HabituationProfileSnapshot.MedicalAdvisoryShown` 欄位
- `CalibrationResponses.Q5RiskFactors` 包含 `VestibularDiagnosed`

**Prompt 設計（Phase 6 + 7 跨越）**：

- 注入 chatbot 的 state 包含 `medical_advisory: vestibular` 訊號
- System prompt 加入明確規則：「若 medical_advisory_shown 非 null 且 user 提及降低 protection，必須先 acknowledge advisory」

**LLM 可能 fail 這條規則的風險**：

- LLM 即使 prompt 寫明 rule 也可能 violate（特別是 small models）
- 必須在 client-side 加 **defensive check**

**Defensive check 設計（Section 4 詳述）**：

```
若 chatbot output 包含 PROFILE_OVERRIDE 且：
  - User has medical_advisory_shown != null
  - Proposal direction = relaxation (e.g. scale ↓, threshold ↑, profile = Resilient)
則 client-side intercept:
  - Strip the PROFILE_OVERRIDE block
  - Insert system note in conversation: 
    "由於你有前庭診斷紀錄，這類調整建議需要更謹慎。"
  - 不 propose 該 change
```

這是**深度安全網**——即使 prompt 失敗、parser 接受了，applicator 層仍會擋下。

### Acceptance test seed

```
Scenario 4A: Vestibular user requests deactivation
  Given: User has medical_advisory_shown = "vestibular"
  When: User asks "能不能關掉 SenseEase"
  Then: Chatbot response contains reference to vestibular history
        AND offers alternatives (power save / partial adjustment)
        AND does NOT emit a [PROFILE_OVERRIDE] toward relaxation

Scenario 4B: Non-vestibular user same question
  Given: User has medical_advisory_shown = null
  When: User asks "能不能關掉 SenseEase"
  Then: Chatbot response is helpful and direct
        AND does NOT mention any medical context

Scenario 4C: Defensive intercept fires when LLM violates rule
  Given: Mock LLM emits a PROFILE_OVERRIDE proposing scale ↓
         while user has vestibular advisory
  When: HabituationOverrideApplicator processes the proposal
  Then: Proposal is REJECTED at applicator layer
        Conversation gets system note
        No engine state change
```

### Failure mode

如果 Goal 4 沒做好：

- Vestibular 用戶的保護被「對話自然 erode」
- 系統失去 user 在 calibration 時主動分享的 medical context 的價值
- 法律/倫理層面：medical-aware product 在 critical context 失憶

---

## Goal 5: Habituation Progression 的尊重

### Restated

Habituation Engine 設計上認為 user 對視覺刺激的耐受是漸進養成的。Chatbot 應該理解這個 progression 並在對話中體現——當 user 進步時 chatbot 知道、當 user setback 時 chatbot 也知道。

### Observable behavior

|Scenario|Chatbot response signal|
|---|---|
|User habituation_level 從 5 升到 12|「你的視覺系統正在適應，習慣化分數從 5 升到 12」|
|User 在 Sensitive profile 兩週後升到 Typical|「你的耐受度進步到可以調整 profile」|
|User 突然連續 spike，level 從 14 跌回 8|「最近幾天進步停滯。可能哪裡有變化？」|
|User 第一次 calibration 隔 6 個月才回來|「歡迎回來。我看到上次紀錄是 6 個月前」|

### Engineering requirement

**Read-side**：

- `HabituationLevel` 與 `HabituationOffset` 當前值
- 歷史 trajectory：**這是新需求**，當前 snapshot 只有 instantaneous value

**問題**：snapshot 只有當下值，沒有「過去 4 週的 level 曲線」。

**解法選項**：

- **Option A**：在 snapshot 加一個 `recent_habituation_levels` 陣列（過去 N 個取樣，例每日一筆）
- **Option B**：依賴 `IEventLogStore` 既有 event log，提取 habituation-related events
- **Option C**：純依賴 `longitudinal_observations`，chatbot 自己累積（但這要 user 跟 chatbot 對話才會 trigger 觀察）

advisor 傾向 **Option B**——重用既有 infrastructure，避免 schema 升級。具體：

- Caller-side（State builder）對 IEventLogStore 做一個輕量查詢
- 提取最近 30 天 habituation-related events
- 整理成 trajectory summary 注入 chatbot context

**Anti-spec**：

- 不需要儲存完整每秒 level 歷史
- Trajectory 描述精度到「每天 high/low」即可
- Chatbot 不負責統計分析，只需要文字 description

### Acceptance test seed

```
Scenario 5A: Progress narrative
  Given: 4 weeks of event log showing habituation_level 5 → 14
  When: User asks "我有沒有進步"
  Then: Chatbot response references concrete trajectory
        (e.g. "從 5 升到 14" or equivalent qualitative phrasing)

Scenario 5B: Setback awareness
  Given: 2 weeks of stable progress, then 5 spikes in last 3 days
  When: User opens chatbot
  Then: Chatbot first turn references recent setback
        AND offers diagnostic questions

Scenario 5C: Long absence
  Given: last_calibration_at_iso = 6 months ago, no recent events
  When: User opens chatbot
  Then: Chatbot acknowledges absence, suggests re-calibration
```

### Failure mode

如果 Goal 5 沒做好：

- Chatbot 變「永遠活在當下」，沒有時間維度
- User 進步沒被肯定 → 失去使用動機
- User setback 沒被察覺 → 系統錯失早期介入機會

---

## Goals 之間的相互依賴

不是 5 個獨立目標。彼此有強耦合：

```
Goal 1 (對話縱深感)
  ↑ 依賴
Goal 2 (狀態一致性)  ← 是 Goal 1 的 prerequisite (沒當前狀態就沒過去狀態的對比基準)
  ↑ 依賴
Goal 5 (Habituation progression)  ← 是 Goal 1 的內容來源 (進步/退步的 trajectory)
  ↑ 依賴 (Goal 4 在某些對話會 trigger)
Goal 4 (安全網記憶)
  ↑ 依賴 (Goal 3 是 Goal 4 的執行 channel)
Goal 3 (雙向 agency)
```

**這個依賴結構決定 Phase 6/7/8 的內部順序**：

- Phase 6 = Goals 1 + 2 + 5 的 read-side
- Phase 7 = Goals 3 + 4 的 write-side
- Phase 8 = 5 個 goal 的 acceptance scenario

**Phase 6 必須先做**：read-side 沒做完，write-side 沒 context 可以參照。

---

## Section 3 → Section 4 Transition

到此 Goals 已完全分解為可實作的 specification。Section 4 會把這些 specification 整合成具體的 phase-by-phase build plan，包含：

- Detailed prompt templates（system prompt 修改、structured token 文法）
- Parser & applicator class designs
- UI flow mockups
- Test scenario implementations
- Failure mode mitigations

Section 4 是整個 prep work 的核心輸出，user 表示會在 Section 4 review。

Section 3 完成。**Section 4 開始撰寫**。

---

# Section 4: Phase 6 / 7 / 8 Detailed Specification

## Section 4 結構預告

由於 Section 4 將是長 spec，我會用以下子節組織方便 user review：

```
4.1  Phase Sequencing & Hard Stops
4.2  Phase 6 Spec
     4.2.1  Path I vs Path II decision
     4.2.2  [habituation_state] block format
     4.2.3  Power Save state injection
     4.2.4  Trajectory summary from event log
     4.2.5  Phase 6 deliverables checklist
     4.2.6  Phase 6 test coverage
4.3  Phase 7 Spec
     4.3.1  Structured token grammar
     4.3.2  HabituationOverrideParser
     4.3.3  HabituationOverrideProcessor
     4.3.4  HabituationOverrideApplicator
     4.3.5  User confirm UI flow
     4.3.6  Defensive intercept layer
     4.3.7  Safety floor migration
     4.3.8  LongitudinalObservation write
     4.3.9  Phase 7 deliverables checklist
     4.3.10 Phase 7 test coverage
4.4  Phase 8 Spec
     4.4.1  E2E scenario tests (5 user scenarios)
     4.4.2  4-week longitudinal simulation
     4.4.3  Documentation deliverables
     4.4.4  Lessons learned carry-forward
4.5  Cross-Phase Design Decisions Summary
4.6  Spec Open Questions for User
```

預估 Section 4 paste 出來會是 user 看 30-60 分鐘的 spec document。建議 user 一次看完再給 feedback，不要邊看邊改方向（避免我修了前面又影響後面的設計）。

---

## 4.1 Phase Sequencing & Hard Stops

```
Habituation Sprint Order:

E2E Test Infra Mini-Sprint (separate, must finish first)
  ↓
Phase 6: Chatbot READ
  ├─ 6a: Path decision + [habituation_state] block format
  ├─ 6b: Caller / service-side enrichment implementation
  ├─ 6c: Power Save + trajectory injection
  ├─ 6d: System prompt update (chatbot understands habituation domain)
  ├─ 6e: Phase 6 acceptance (Goals 1 + 2 + 5 read-side)
  └─ HARD STOP: user verifies chatbot conversations now reference state
  ↓
Phase 7: Chatbot WRITE (+ safety floor migration)
  ├─ 7a: Safety floor migration (Goal 4 prerequisite)
  ├─ 7b: Structured token grammar + parser
  ├─ 7c: HabituationOverrideProcessor (pure logic)
  ├─ 7d: HabituationOverrideApplicator (caller-side)
  ├─ 7e: User confirm UI flow
  ├─ 7f: Defensive intercept layer (Goal 4 safety)
  ├─ 7g: LongitudinalObservation write
  └─ HARD STOP: user verifies propose-confirm-apply flow
  ↓
Phase 8: Acceptance + Docs
  ├─ 8a: E2E scenarios for Goals 1-5
  ├─ 8b: 4-week simulation test
  ├─ 8c: docs/HABITUATION-CHATBOT-INTEGRATION.md
  ├─ 8d: Pop-up sprint lessons learned carry-forward review
  └─ HARD STOP: user final acceptance → tag v0.9
```

**每個 phase 內部以 sub-letter（6a, 6b...）標示 commit 粒度**。每個 sub-letter 一個 commit、每個 commit 之間 user 可以 visual / behavioral verify。

**Phase 7a 必須在 Phase 7 最先做**——safety floor 是 Goal 4 的 prerequisite，沒它後續任何 write-side 都不該上線（會放大現有安全債）。

---

## 4.2 Phase 6 Spec

### 4.2.1 Path I vs Path II Decision

#### Path I — Caller-side enrichment

**設計**：

```
MainViewModel (caller)
  ↓ build state string
[SenseEaseState ...] line in user message body
  ↓
MistralChatbotService (unchanged)
```

具體：

```csharp
// in MainViewModel or ChatbotViewModel:
private string BuildSenseEaseStateLine()
{
    var snapshot = _profileStore.Load();  // already exists
    var powerSave = UserPreferences.Current.IsPowerSaveActive;
    var trajectory = _eventLog.GetRecentHabituationTrajectory(days: 30);
    
    var sb = new StringBuilder();
    sb.Append("[habituation_state]\n");
    sb.Append($"current_vims_tier: {snapshot.CurrentVimsTier}\n");
    sb.Append($"habituation_level: {snapshot.HabituationLevel:F1}\n");
    sb.Append($"profile: {snapshot.Profile}\n");
    sb.Append($"engine_state: {snapshot.EngineState}\n");
    if (snapshot.AwaitingUserConfirm)
        sb.Append("awaiting_user_confirm: true\n");
    sb.Append($"calibration_count: {snapshot.CalibrationCount}\n");
    if (snapshot.LastCalibrationAtIso != null)
        sb.Append($"last_calibration: {snapshot.LastCalibrationAtIso}\n");
    if (snapshot.MedicalAdvisoryShown != null)
        sb.Append($"medical_advisory: {snapshot.MedicalAdvisoryShown}\n");
    if (powerSave)
        sb.Append("power_save_active: true\n");
    sb.Append($"trajectory_summary: {trajectory.Summary}\n");
    if (snapshot.LongitudinalObservations.Count > 0)
    {
        sb.Append("longitudinal_observations:\n");
        foreach (var obs in snapshot.LongitudinalObservations.TakeLast(5))
            sb.Append($"  - {obs}\n");
    }
    sb.Append("[/habituation_state]");
    return sb.ToString();
}
```

然後在送往 MistralChatbotService 前，把這個字串 prepend 到 user message body。

**優點**：

- MistralChatbotService 完全不動（zero risk regression）
- Pop-up sprint 累積的 chatbot service 穩定性保留
- Caller 完全掌控 context shape，未來容易修改

**缺點**：

- Caller 與 chatbot 之間有重複資訊組裝邏輯
- 如果未來有其他 caller（例如 PowerShell tool），需要重新組裝

#### Path II — Service-side injection

**設計**：

```
MainViewModel (caller)
  ↓ unchanged input
MistralChatbotService (modified)
  ├─ IAdjustmentLog (existing)
  ├─ IEventLogStore (existing)
  └─ IHabituationStateReader (NEW)
  ↓ internal history prefix builder
LLM call
```

具體：

```csharp
public interface IHabituationStateReader
{
    HabituationStateSnapshot ReadCurrent();
}

public sealed record HabituationStateSnapshot(
    int CurrentVimsTier,
    double HabituationLevel,
    string Profile,
    // ... all the fields
);

// in MistralChatbotService
private readonly IHabituationStateReader _stateReader;

private string BuildHistoryPrefix(...)
{
    var sb = new StringBuilder();
    AppendRecentAdjustments(sb);
    AppendRecentEvents(sb);
    AppendHabituationState(sb, _stateReader.ReadCurrent());  // NEW
    return sb.ToString();
}
```

**優點**：

- Chatbot 自己負責所有 context 構造，caller code 清爽
- IHabituationStateReader 是 testable interface
- 對應 IAdjustmentLog / IEventLogStore 既有 pattern

**缺點**：

- MistralChatbotService 必須改動（產生 regression 風險）
- 需要新 interface + 實作 + 依賴注入連線
- 與 caller 共用某些 logic（例如 PowerSave 狀態查詢）需重新設計

#### advisor 建議：**Path I**

理由：

1. **Risk profile 較低**：MistralChatbotService 是 Pop-up sprint 唯一沒被動過、穩定的元件之一。Phase 6 第一個動作就動它違反「不動穩定元件」紀律
2. **Iteration 速度快**：caller-side 改 string format 不需要動 service、test、interface
3. **Pop-up sprint Card 上的 `[habituation_state]` 注入**也可以共用 caller-side builder（未來 Card 顯示 tier 數字時可重用）

**User check at Section 4 review**：Path I 接受嗎？

---

### 4.2.2 [habituation_state] Block Format

最終 prompt 注入內容範例：

```
[habituation_state]
current_vims_tier: 4
habituation_level: 12.3
habituation_offset_pp: 5.2
profile: SENSITIVE
profile_source: calibration_demo
engine_state: RUNNING
awaiting_user_confirm: false
externally_paused: false
calibration_count: 2
last_calibration: 2026-04-10T14:23:00Z
calibration_confidence: HIGH
calibration_completeness: COMPLETE
calibration_path: BUTTONS
medical_advisory: vestibular
personal_pressure_scale: 0.9
symptom_dominance: NAUSEA
power_save_active: false
last_spike_at: 2026-05-26T08:15:00Z
trajectory_summary: habituation_level rose 8→12 over last 14 days; 3 minor spikes in last week
longitudinal_observations:
  - 2026-04-20: user reports headaches concentrated on Monday mornings
  - 2026-05-01: user mentions improvement with new monitor distance
  - 2026-05-15: user took 5-day break, level held steady
[/habituation_state]
```

**Format rules**：

- 開頭與結尾 marker（`[habituation_state]` / `[/habituation_state]`）對應 chatbot prompt 中明確規則
- Snake_case key 與 schema v2 對齊
- Null / empty 欄位**省略**（不要寫 `medical_advisory: null`，會浪費 token）
- `trajectory_summary` 是 natural language 一行話，不是 raw data
- `longitudinal_observations` 取最近 5 條，按時間順序

**System prompt 對應 規則**：

```
The user message may contain a [habituation_state] ... [/habituation_state]
block. This is the user's current SenseEase state. Reference it naturally
in conversation:
- Use `current_vims_tier` and `habituation_level` to ground discussion
- If `awaiting_user_confirm` is true, acknowledge user just had a high-tier
  spike and offer check-in
- If `power_save_active` is true, acknowledge sensing is paused
- If `medical_advisory` is non-null, factor it into safety reasoning
- If `longitudinal_observations` has entries, reference them when relevant
- Do NOT echo the raw key-value pairs to user; translate to natural language
```

---

### 4.2.3 Power Save State Injection

如 Section 2.4 分析，採 caller-side dynamic query（不升 schema）。

```csharp
private bool QueryPowerSaveState()
{
    return UserPreferences.Current.IsPowerSaveActive;
}
```

如果 Pop-up sprint 最終把 PowerSave 狀態存在 UserPreferences 之外的地方（例如 in-memory only），這條需要對應調整。**Section 4 review 時 user 需確認 Pop-up sprint 最終的 PowerSave 持久化位置**。

---

### 4.2.4 Trajectory Summary from Event Log

從 `IEventLogStore` 抓最近 30 天 habituation 相關 events，summarize 為一行話。

```csharp
public sealed record HabituationTrajectory(
    string Summary,
    int LevelStart,
    int LevelEnd,
    int SpikeCount,
    int DaysSinceLastActivity
);

public HabituationTrajectory GetRecentHabituationTrajectory(int days)
{
    var events = _eventLog.GetEventsSince(DateTime.UtcNow.AddDays(-days))
        .Where(e => e.Category == EventCategory.Habituation)
        .ToList();
    
    if (events.Count == 0) 
        return new HabituationTrajectory("no recent activity", 0, 0, 0, days);
    
    // 從 events 抽取 level changes 與 spike events
    var levelEvents = events
        .Where(e => e.Type == EventType.HabituationLevelChange)
        .OrderBy(e => e.Timestamp)
        .ToList();
    int start = levelEvents.FirstOrDefault()?.NumericValue ?? 0;
    int end = levelEvents.LastOrDefault()?.NumericValue ?? start;
    int spikes = events.Count(e => e.Type == EventType.SevereSpike);
    int daysSince = (DateTime.UtcNow - events.Last().Timestamp).Days;
    
    string summary = $"habituation_level {start}→{end} over last {days} days; {spikes} spikes; last activity {daysSince}d ago";
    return new HabituationTrajectory(summary, start, end, spikes, daysSince);
}
```

**注意**：當前 `IEventLogStore` 是否有 `HabituationLevelChange` / `SevereSpike` event type？這個 **Section 4 review 時 user 或 Agent A 需確認**。

如果沒有，Phase 6c 需要先**補 event 記錄路徑**——讓 HabituationEngine 在 level 變化或 spike 時寫 event log。這條工作如果範圍大，可能需要拆成 Phase 6 的獨立 sub-letter（例 6c-pre）。

---

### 4.2.5 System Prompt 修改

當前 MistralChatbotService 的 SystemPrompt 有 ~3000 字（從 Agent A paste 看到）。Phase 6d 要新增以下 section：

```
[Section to add to SystemPrompt]

## HABITUATION STATE CONTEXT

User messages may contain a [habituation_state] ... [/habituation_state]
block before the actual user text. This block describes the user's
current SenseEase state. Use it to ground your conversation naturally.

Rules:
1. Reference current state when relevant. E.g. if current_vims_tier is 6
   and user asks "how am I doing", mention the tier in natural language.
2. NEVER echo raw key-value pairs back to user. Translate to natural language.
3. If awaiting_user_confirm is true, acknowledge user just had a tier ≥ 8
   spike. Offer a gentle check-in before continuing other topics.
4. If power_save_active is true, treat tier/level data as stale. Acknowledge
   sensing is paused.
5. If medical_advisory is non-null:
   - "vestibular": user has vestibular diagnosis. When user requests
     reducing protection, surface this history and offer alternatives.
   - "high_risk_combo": user has multiple risk factors. Be more cautious
     with relaxation suggestions.
6. Reference longitudinal_observations when user mentions past experiences
   or asks about progress.
7. If trajectory_summary shows progress, acknowledge it. If shows setback,
   gently inquire what might have changed.

The block is INFORMATIONAL CONTEXT for you. It does NOT override your
existing rules about intent classification, JSON output format, or 
adjustment policies. Continue to emit ONE LINE of compact JSON as before.
```

**Anti-spec**：

- 不要改變既有 JSON 輸出 envelope（intent / severity / reply / deltas）
- 不要加入會讓 LLM 在輸出 JSON 中 reference habituation 欄位的 instruction（會破壞既有 parser）
- Habituation context **只影響 reply 文字內容**，不影響 deltas

---

### 4.2.6 Phase 6 Deliverables Checklist

```
[ ] 6a: Path I caller-side enrichment skeleton
       - BuildSenseEaseStateLine() method
       - Wiring into chatbot send path
[ ] 6b: [habituation_state] block format finalized + tests
       - Unit tests for block structure
       - Empty / null field handling
[ ] 6c: Power Save + Event log trajectory injection
       - QueryPowerSaveState()
       - GetRecentHabituationTrajectory()
       - Event log type補洞 (if needed)
[ ] 6d: System prompt section added
       - New HABITUATION STATE CONTEXT section
       - Examples updated to include block references
[ ] 6e: Phase 6 acceptance scenarios pass
       - Scenario 1A: fresh calibration → first turn references it
       - Scenario 2A: cross-surface consistency
       - Scenario 2B: power save awareness
       - Scenario 5A: progress narrative
```

### 4.2.7 Phase 6 Test Coverage

**Unit tests**:

- `BuildSenseEaseStateLine_AllFieldsPresent_ProducesCorrectBlock()`
- `BuildSenseEaseStateLine_NullFieldsOmitted()`
- `BuildSenseEaseStateLine_PowerSaveActive_IncludesFlag()`
- `GetRecentHabituationTrajectory_NoEvents_ReturnsNoActivitySummary()`
- `GetRecentHabituationTrajectory_LevelRise_ReturnsRiseSummary()`
- `GetRecentHabituationTrajectory_Spikes_CountsAccurately()`

**Integration tests**:

- `ChatbotReceives_HabituationStateBlock_InHistoryPrefix()`
- `LLMResponse_ReferencesCurrentTier_Naturally()` (using mock LLM)

**Manual smoke**:

- Real LLM call with fresh calibration scenario, verify response quality
- Power Save toggle, verify next chatbot turn acknowledges

---

## 4.3 Phase 7 Spec

[由於 Section 4 已經很長，Phase 7 是內容最多的部分，將分批 paste。下面是 Phase 7 的前半（4.3.1 - 4.3.5）]

### 4.3.1 Structured Token Grammar

Chatbot LLM output 增加新 token type：

```
[PROFILE_OVERRIDE]
{
  "intent": "adjust_sensitivity" | "switch_profile" | "adjust_strike_limit" 
            | "resume_acknowledge" | "reset_offset",
  "target_field": <field name>,
  "proposed_value": <value>,
  "rationale": "<natural language reason>",
  "expires_at_iso": "<UTC timestamp>"
}
[/PROFILE_OVERRIDE]
```

```
[OBSERVATION_ADD]
<one-line natural language observation about user>
[/OBSERVATION_ADD]
```

```
[RESUME_ACKNOWLEDGE]
{
  "user_confirmed": true | false,
  "user_quote": "<verbatim user text indicating readiness or reluctance>"
}
[/RESUME_ACKNOWLEDGE]
```

**設計原則**:

- 每個 LLM response 最多含一個 PROFILE_OVERRIDE（防 spam）
- OBSERVATION_ADD 可有 0-2 個（chatbot 主動累積觀察）
- RESUME_ACKNOWLEDGE 只在 awaiting_user_confirm = true 時 valid
- 所有 token 都 strip 後不出現在 visible reply

### 4.3.2 HabituationOverrideParser

仿 `CalibrationLlmDeltaParser` pattern。完整 class 設計 Section 4 review 後與 Coding Agent 對齊細節，這裡只列 public surface：

```csharp
public static class HabituationOverrideParser
{
    public static ParsedHabituationOverride? TryParse(string llmResponse);
    public static string StripAllOverrideBlocks(string llmResponse);
}

public sealed record ParsedHabituationOverride(
    ProfileOverrideProposal? Proposal,
    IReadOnlyList<string> Observations,
    ResumeAcknowledge? ResumeAck
);

public sealed record ProfileOverrideProposal(
    OverrideIntent Intent,
    string TargetField,
    object ProposedValue,
    string Rationale,
    DateTime ExpiresAtUtc
);

public enum OverrideIntent
{
    AdjustSensitivity,
    SwitchProfile,
    AdjustStrikeLimit,
    ResetOffset,
}

public sealed record ResumeAcknowledge(
    bool UserConfirmed,
    string UserQuote
);
```

### 4.3.3 HabituationOverrideProcessor

Pure logic processor。接受 parsed proposal + current snapshot，判斷是否合法：

```csharp
public static class HabituationOverrideProcessor
{
    public static OverrideValidationResult Validate(
        ProfileOverrideProposal proposal,
        HabituationProfileSnapshot currentSnapshot);
}

public sealed record OverrideValidationResult(
    bool IsAccepted,
    string? RejectionReason,
    string? SafetyNote
);
```

**Validation rules**:

1. `expires_at_iso` 在未來，且不超過 7 天後（過長視為 LLM hallucination）
2. `proposed_value` 在 valid range (e.g. personal_pressure_scale ∈ [0.7, 1.5])
3. **若 user 有 vestibular advisory AND proposal direction = relaxation → REJECT**
4. **若已有 active pending proposal → REJECT**（防 spam）
5. **若距離 user 上次拒絕同類 proposal < 7 天 → REJECT**

### 4.3.4 HabituationOverrideApplicator

Caller-side applier。User confirm 後實際對 engine 作用：

```csharp
public sealed class HabituationOverrideApplicator
{
    public ApplyResult ApplyAccepted(ProfileOverrideProposal proposal);
    public void RecordDeclined(ProfileOverrideProposal proposal);
    public void RecordExpired(ProfileOverrideProposal proposal);
}
```

實作：對應每個 OverrideIntent 呼叫對應 engine API：

```
AdjustSensitivity → engine.EffectiveTrigger setter
SwitchProfile → engine.SetProfile(profile, "chatbot_override")
AdjustStrikeLimit → engine.SevereStrikeLimitOverride setter
ResetOffset → engine.Reset() (謹慎，only with confirm)
```

### 4.3.5 User Confirm UI Flow

三個 flow option：

**Flow A — Card-embedded confirm**:

```
Notification Card 增加新區塊（top of card body）:
┌─────────────────────────────────────────┐
│ 💡 SenseEase 建議                       │
│                                         │
│ 把 sensitivity 從 50 調到 60            │
│ Chatbot 觀察：過去 5 天 tier 6-7        │
│                                         │
│ [接受]    [拒絕]    [稍後]              │
│                                         │
│ 有效期至：2026-05-30 14:00              │
└─────────────────────────────────────────┘
```

**Flow B — Settings highlighted**: SettingsWindow 開啟時，相關 setting field 旁邊有 highlighted note：

```
Sensitivity Threshold: [50]   💡 Chatbot 建議 60 [Apply]
```

**Flow C — System tray notification**: 彈出 Windows native toast。

**advisor 建議：Flow A**——理由：

- 與 Pop-up sprint 設計的 Card 相容
- User 主動點 Card 才看到（不打擾 ambient）
- Card 本來就是「狀態 + 建議」surface，新增「chatbot 建議」自然

**User check at Section 4 review**：Flow A 接受嗎？

[Phase 7 後半 4.3.6 - 4.3.10 + Phase 8 + Section 4.5 / 4.6 將在下一段 paste]

---

# 暫停 Paste 等 User Review Section 4 前半

Section 4 至此已經很長。在繼續 paste Phase 7 後半（4.3.6 defensive intercept、4.3.7 safety floor migration、4.3.8 longitudinal write、4.3.9 deliverables、4.3.10 test coverage）+ Phase 8 + Section 4.5 / 4.6 之前，**建議 user 在此 stop 一下 review 已經 paste 的 Section 1-3 + Section 4.1 / 4.2 / 4.3.1-4.3.5**。

理由：

- Section 4 後半的設計依賴前半的 design 決策（特別是 4.3.1 token grammar）
- 如果 user 對前半某條設計不同意，後半要 rework
- 一次 paste 完整個 Section 4 user review 心智負擔大

**請 user 對下列 critical 設計決策 raise 反對意見或 OK**：

1. **Section 4.1 phase sequencing**（E2E infra mini-sprint 先做、Phase 6 → 7 → 8 順序、每個 phase 內部 sub-letter 分工）
2. **Section 4.2.1 Path I**（caller-side enrichment，不動 MistralChatbotService）
3. **Section 4.2.2 [habituation_state] block format**（snake_case key、natural trajectory summary、最近 5 條 observations）
4. **Section 4.3.1 three token types**（PROFILE_OVERRIDE / OBSERVATION_ADD / RESUME_ACKNOWLEDGE）
5. **Section 4.3.5 Flow A**（Card-embedded confirm UI）

如果這 5 條 user 都 OK，我繼續 paste Section 4 後半。

如果某條有疑慮，**現在 raise 比後面 raise 便宜**。

User 也可以選擇「全部 paste 完再一次 review」——告訴我即可，我繼續往下。

----
# Section 4 Resume — Phase 7 後半 + Phase 8 + Cross-Phase Decisions

User 對前半 5 條 critical 設計都 OK，繼續 paste 後半。

---

## 4.3 Phase 7 Spec (續)

### 4.3.6 Defensive Intercept Layer

**問題重述**：即使 system prompt 寫明「vestibular user 不該收到 relaxation proposal」，small LLM（Mistral 3B）在 temperature > 0 下仍可能違反 rule。Goal 4 的安全網不能依賴 prompt 紀律。

**解法**：在 Applicator 層做 deterministic check，**不依賴 LLM 守規矩**。

```csharp
// HabituationOverrideApplicator.cs
public ApplyResult ApplyAccepted(ProfileOverrideProposal proposal)
{
    // Step 1: Defensive intercept BEFORE applying
    var interceptResult = CheckSafetyIntercepts(proposal);
    if (interceptResult.Blocked)
    {
        // Record intercept event
        _eventLog.Append(new EventEntry(
            category: EventCategory.SafetyIntercept,
            message: $"chatbot proposal blocked: {interceptResult.Reason}",
            timestamp: DateTime.UtcNow));
        
        return new ApplyResult(
            success: false,
            blockedBySafety: true,
            reason: interceptResult.Reason);
    }
    
    // Step 2: Apply (only reached if not blocked)
    switch (proposal.Intent) { /* ... apply ... */ }
}

private SafetyInterceptResult CheckSafetyIntercepts(ProfileOverrideProposal proposal)
{
    var snapshot = _profileStore.Load();
    
    // Rule 1: Vestibular user + relaxation proposal
    if (snapshot.MedicalAdvisoryShown == MedicalAdvisory.Vestibular)
    {
        if (IsRelaxationProposal(proposal, snapshot))
        {
            return SafetyInterceptResult.Blocked(
                "vestibular advisory active; relaxation proposals require manual Settings change");
        }
    }
    
    // Rule 2: High-risk combo + significant relaxation
    if (snapshot.MedicalAdvisoryShown == MedicalAdvisory.HighRiskCombo)
    {
        if (IsSignificantRelaxation(proposal, snapshot, threshold: 0.15))
        {
            return SafetyInterceptResult.Blocked(
                "high-risk-combo advisory; small adjustments only via this channel");
        }
    }
    
    // Rule 3: Strike limit override = 1 (vestibular hard cap)
    if (snapshot.SevereStrikeLimit == 1 
        && proposal.Intent == OverrideIntent.AdjustStrikeLimit)
    {
        return SafetyInterceptResult.Blocked(
            "strike limit was hard-set by calibration; chatbot cannot override");
    }
    
    return SafetyInterceptResult.Passed();
}

private static bool IsRelaxationProposal(
    ProfileOverrideProposal proposal, 
    HabituationProfileSnapshot snapshot)
{
    return proposal.Intent switch
    {
        OverrideIntent.AdjustSensitivity => 
            // Lower scale = higher trigger = less sensitive = relaxation
            (double)proposal.ProposedValue < snapshot.PersonalPressureScale,
        
        OverrideIntent.SwitchProfile =>
            // Switch to less restrictive profile
            IsLessRestrictive((string)proposal.ProposedValue, snapshot.Profile),
        
        OverrideIntent.AdjustStrikeLimit =>
            // Higher limit = more strikes allowed = relaxation
            (int)proposal.ProposedValue > (int)snapshot.SevereStrikeLimit,
        
        OverrideIntent.ResetOffset => true,  // reset = lose progress = relaxation
        
        _ => false
    };
}

private static bool IsLessRestrictive(string proposed, string current)
{
    var order = new[] { "RESILIENT", "TYPICAL", "SENSITIVE" };
    int proposedIdx = Array.IndexOf(order, proposed);
    int currentIdx = Array.IndexOf(order, current);
    return proposedIdx < currentIdx;  // 索引越小越 relaxed
}
```

**重要原則**：

- Intercept 在 Applicator 層，**不在 Parser 層**。Parser 仍然 parse 出 proposal（讓 chatbot 自然對話）、UI 仍然 show proposal（讓 user 看到）。**只有 user click Accept 時才被 intercept block**。
- 這個設計讓 user 知道「chatbot 試圖建議但被 safety 攔住」，比「靜默忽略」更透明。
- Intercept 事件寫入 IEventLogStore，下次 chatbot 對話 context 可看到（chatbot 學到「上次這個方向被擋了」）。

### 4.3.7 Safety Floor Migration

**現狀**（Section 2.2 已標出）：MistralChatbotService **沒有** self-harm safety floor。CalibrationChatbotService 有。

**Phase 7a 必做**：把 safety floor 移植到 MistralChatbotService。

具體：

```csharp
// MistralChatbotService.cs (Phase 7a 新增)

private static readonly string[] SafetyFloorPhrasesZh = new[]
{
    "自殘", "自殺", "結束生命", "跳下去", "跳樓",
    "不想活了", "不想活下去", "不想再活", "想死了", "想去死", "我想死"
};

private static readonly string[] SafetyFloorPhrasesEn = new[]
{
    "suicide", "self-harm", "self harm", "kill myself",
    "end it all", "end my life", "not want to live",
    "want to die", "wanna die"
};

private bool TripsSafetyFloor(string userText, out string crisisResponse)
{
    string lower = userText.ToLowerInvariant();
    
    bool zh = SafetyFloorPhrasesZh.Any(p => userText.Contains(p));
    bool en = SafetyFloorPhrasesEn.Any(p => lower.Contains(p, StringComparison.OrdinalIgnoreCase));
    
    if (zh)
    {
        crisisResponse = "我聽到你了。SenseEase 不是合適的對話對象,但有人可以聽你說。\n\n" +
                         "台灣 1925 安心專線(24小時免費)\n" +
                         "或 https://findahelpline.com\n\n" +
                         "請和他們聊聊。";
        return true;
    }
    if (en)
    {
        crisisResponse = "I hear you. SenseEase isn't the right conversation, but people who can listen are available.\n\n" +
                         "Suicide & Crisis Lifeline: 988 (US) / 1925 (Taiwan)\n" +
                         "Or https://findahelpline.com\n\n" +
                         "Please reach out.";
        return true;
    }
    
    crisisResponse = string.Empty;
    return false;
}

// 在 send path 開頭加入
public async Task<ChatbotDecision> SendAsync(string userText, ...)
{
    if (TripsSafetyFloor(userText, out string crisisResp))
    {
        return ChatbotDecision.SafetyFloor(crisisResp);
    }
    
    // ... existing logic ...
}
```

**Anti-spec**：

- Safety floor **不是 LLM 對話的一部分**——它在 LLM call **之前**短路。Never forward safety-trigger text to LLM。
- Crisis response 文字是 hardcoded，**不經過 LLM 改寫**。LLM 在這種狀態下幻覺風險最高。
- Phrase list 對應 CalibrationChatbotService 同一份（共用 const）以維持一致性。

**Test**：

```csharp
[Theory]
[InlineData("我想死了")]
[InlineData("I want to die")]
[InlineData("不想活下去")]
public void SafetyFloor_DetectsSelfHarmPhrases_ShortCircuitsLlm(string text)
{
    var mockLlm = new Mock<IChatbotLlmClient>();
    mockLlm.Setup(c => c.SendAsync(It.IsAny<string>(), ...))
           .Throws(new Exception("LLM should not be called"));
    
    var service = new MistralChatbotService(mockLlm.Object, ...);
    var result = await service.SendAsync(text, ...);
    
    Assert.True(result.IsSafetyFloor);
    Assert.Contains("findahelpline", result.Reply);
    mockLlm.Verify(c => c.SendAsync(It.IsAny<string>(), ...), Times.Never);
}
```

### 4.3.8 LongitudinalObservation Write

Chatbot output `[OBSERVATION_ADD]` token 時，applicator 寫入 snapshot：

```csharp
public sealed class LongitudinalObservationWriter
{
    private readonly IHabituationProfileStore _store;
    
    public void Append(string observation)
    {
        if (string.IsNullOrWhiteSpace(observation)) return;
        if (observation.Length > 200) 
            observation = observation.Substring(0, 200);  // hard cap
        
        var snapshot = _store.Load();
        
        // 加 timestamp prefix 方便未來識別
        string timestamped = $"{DateTime.UtcNow:yyyy-MM-dd}: {observation.Trim()}";
        
        snapshot.LongitudinalObservations.Add(timestamped);
        
        // Cap at 50 entries (oldest evicted)
        while (snapshot.LongitudinalObservations.Count > 50)
            snapshot.LongitudinalObservations.RemoveAt(0);
        
        _store.Save(snapshot);
    }
}
```

**Anti-spec**:

- Observation 是 chatbot 的工作記憶，**user 不該編輯**。SettingsWindow 不暴露這個 list。
- 不要做 NLP 分析（情感分類、主題提取）——chatbot LLM 自己會在下次 read 時用語意理解。
- 50 條 hard cap，FIFO 淘汰。

**Concern**: 若 chatbot 連續對話中每 turn 都 emit OBSERVATION_ADD，會快速填滿 50 條。

**Mitigation**：

- Chatbot system prompt 明確 instructed: "Only emit OBSERVATION_ADD when noting something meaningfully new across sessions, not within-session reactions."
- Applicator 層 deduplication: 若新 observation 與最近 5 條有 >70% 字串相似度（簡單 Levenshtein），skip insertion。

### 4.3.9 Phase 7 Deliverables Checklist

```
[ ] 7a: Safety floor migration to MistralChatbotService
       - Phrase lists (zh + en)
       - Short-circuit logic before LLM call
       - Tests covering zh, en, mixed, edge cases
       - HARD STOP: user verifies safety response renders correctly

[ ] 7b: Structured token grammar + parsers
       - PROFILE_OVERRIDE parser
       - OBSERVATION_ADD parser
       - RESUME_ACKNOWLEDGE parser
       - Strip-from-visible-reply logic
       - Tests for malformed blocks, missing fields, type mismatches

[ ] 7c: HabituationOverrideProcessor (pure logic)
       - Validation rules
       - Returns OverrideValidationResult
       - Tests for each validation rule (vestibular block, value range, etc.)

[ ] 7d: HabituationOverrideApplicator (caller-side)
       - ApplyAccepted with defensive intercept
       - RecordDeclined
       - RecordExpired
       - Engine API calls per intent
       - Tests with mock engine

[ ] 7e: User confirm UI flow (Flow A — Card-embedded)
       - New "ChatbotProposal" section in Notification Card
       - Accept / Decline / Later buttons
       - Expiry countdown display
       - State persistence (pending proposal across app restart? defer to 4.5)

[ ] 7f: Defensive intercept layer
       - CheckSafetyIntercepts implementation
       - IsRelaxationProposal helper
       - Event log integration
       - Tests for each intercept rule

[ ] 7g: LongitudinalObservation write
       - LongitudinalObservationWriter
       - Deduplication
       - 50-entry cap with FIFO
       - Tests
       - HARD STOP: user verifies first end-to-end propose-confirm-apply flow
```

### 4.3.10 Phase 7 Test Coverage

**Unit tests**:

- Parser tests (malformed JSON, missing fields, invalid timestamps)
- Processor validation rules (each rule has positive + negative case)
- Applicator with mock engine (each intent type)
- Defensive intercept (vestibular block, high-risk-combo block)
- Safety floor (zh + en phrase variants)

**Integration tests**:

- Mock LLM emits PROFILE_OVERRIDE → parser → processor → applicator → engine state change
- Vestibular user + LLM emits relaxation → intercept blocks → engine state unchanged
- Two consecutive proposals → second rejected (one-active-proposal rule)

**Manual smoke**:

- Real LLM, vestibular calibration in fixture → ask chatbot "能不能放鬆？" → verify chatbot surfaces advisory naturally
- Power Save active → ask chatbot anything → verify acknowledgment

---

## 4.4 Phase 8 Spec

### 4.4.1 E2E Scenario Tests (5 user scenarios)

依 Section 1 的 5 個 Scenario 寫 E2E test。這些 test 依賴 Phase 6 / 7 全部完成 + E2E test infrastructure。

**Scenario A — First conversation after calibration**:

```csharp
[Fact]
[E2EScenario("Goal 1: 對話縱深感")]
public async Task ScenarioA_FreshCalibration_FirstChatbotTurnGroundedInState()
{
    // Arrange: calibration completed 1 hour ago, Sensitive profile
    var app = await E2ETestHarness.LaunchAsync();
    await app.SimulateCalibrationComplete(new CalibrationResult
    {
        Profile = HabituationProfile.Sensitive,
        Confidence = CalibrationConfidence.High,
    });
    
    // Act: open chatbot, send simple greeting
    await app.OpenChatbotTab();
    var response = await app.SendChatbotMessage("你好");
    
    // Assert
    Assert.Contains("Sensitive", response.Reply, StringComparison.OrdinalIgnoreCase);
    Assert.True(
        response.Reply.Contains("calibration") || 
        response.Reply.Contains("校準"),
        "First turn should reference recent calibration");
}
```

**Scenario B — Multi-week longitudinal recall**:

```csharp
[Fact]
[E2EScenario("Goal 1: 對話縱深感 / Goal 5: Habituation progression")]
public async Task ScenarioB_LongitudinalProgression_ChatbotReferencesTrajectory()
{
    var app = await E2ETestHarness.LaunchAsync();
    
    // Simulate 4 weeks of habituation history via event log injection
    await app.InjectHabituationHistory(new[]
    {
        (DateTime.UtcNow.AddDays(-28), level: 10.0, spike: false),
        (DateTime.UtcNow.AddDays(-14), level: 12.0, spike: false),
        (DateTime.UtcNow.AddDays(-7), level: 14.0, spike: false),
        (DateTime.UtcNow.AddDays(-1), level: 14.5, spike: false),
    });
    
    await app.OpenChatbotTab();
    var response = await app.SendChatbotMessage("最近怎麼樣");
    
    // Assert response references concrete trajectory or qualitative progress
    Assert.True(
        response.Reply.Contains("進步") || 
        response.Reply.Contains("提升") ||
        response.Reply.Contains("level") ||
        response.Reply.Contains("14"),
        $"Response should reference progress: {response.Reply}");
}
```

**Scenario C — Propose → accept → apply**:

```csharp
[Fact]
[E2EScenario("Goal 3: 雙向 agency")]
public async Task ScenarioC_ChatbotProposeUserAcceptEngineApplies()
{
    var app = await E2ETestHarness.LaunchAsync();
    
    // Setup: 5 days of Tier 6-7 to trigger proposal heuristic
    await app.InjectHabituationHistory(/* 5 days at Tier 6-7 */);
    
    // Use stub LLM that emits PROFILE_OVERRIDE
    app.UseLlmStub(new LlmStub_ProposeScaleIncrease());
    
    await app.OpenChatbotTab();
    await app.SendChatbotMessage("我覺得最近用得很疲勞");
    
    // Verify card shows proposal
    var card = await app.OpenNotificationCard();
    Assert.True(card.HasPendingProposal);
    
    // Click accept
    await card.ClickAcceptProposal();
    
    // Verify engine state changed
    var snapshot = await app.ReadHabituationSnapshot();
    Assert.NotEqual(1.0, snapshot.PersonalPressureScale);  // changed from default
}
```

**Scenario D — Medical advisory recall**:

```csharp
[Fact]
[E2EScenario("Goal 4: 安全網記憶")]
public async Task ScenarioD_VestibularUserAsksToDisable_ChatbotSurfacesAdvisory()
{
    var app = await E2ETestHarness.LaunchAsync();
    
    // Setup: user has vestibular medical advisory
    await app.SimulateCalibrationComplete(new CalibrationResult
    {
        MedicalAdvisory = MedicalAdvisory.Vestibular,
        Q5RiskFactors = new[] { RiskFactor.VestibularDiagnosed },
    });
    
    app.UseLlmStub(new LlmStub_PassThrough());  // real-ish LLM behavior
    
    await app.OpenChatbotTab();
    var response = await app.SendChatbotMessage("可以關掉 SenseEase 嗎");
    
    // Verify response mentions vestibular or calibration history
    Assert.True(
        response.Reply.Contains("前庭") || 
        response.Reply.Contains("vestibular") ||
        response.Reply.Contains("診斷"),
        $"Response should reference vestibular advisory: {response.Reply}");
    
    // Verify no PROFILE_OVERRIDE was emitted toward deactivation
    Assert.Null(response.ParsedProposal);
}
```

**Scenario E — Resume after Power Save**:

```csharp
[Fact]
[E2EScenario("Goal 2: 狀態一致性")]
public async Task ScenarioE_PowerSaveActive_ChatbotAcknowledgesPause()
{
    var app = await E2ETestHarness.LaunchAsync();
    
    // Setup: enable Power Save, then 3 days later
    await app.EnablePowerSave();
    await app.AdvanceTimeBy(TimeSpan.FromDays(3));
    
    await app.OpenChatbotTab();
    var response = await app.SendChatbotMessage("嗨");
    
    Assert.True(
        response.Reply.Contains("省電") || 
        response.Reply.Contains("暫停") ||
        response.Reply.Contains("power save", StringComparison.OrdinalIgnoreCase),
        $"Response should acknowledge power save state: {response.Reply}");
}
```

### 4.4.2 4-Week Longitudinal Simulation

**問題**：Goal 5（habituation progression）的真實 acceptance 需要 4 週時序資料。E2E test 不能等 4 週真實時間。

**解法**：simulated time injection。

```csharp
public sealed class SimulatedTimeProvider : ITimeProvider
{
    private DateTime _now;
    public DateTime UtcNow => _now;
    public void AdvanceBy(TimeSpan delta) => _now = _now.Add(delta);
}

[Fact]
[E2EScenario("Goal 5: 4-week progression simulation")]
public async Task FourWeekSimulation_HabituationProgressesAndChatbotTracks()
{
    var timeProvider = new SimulatedTimeProvider();
    var app = await E2ETestHarness.LaunchAsync(timeProvider);
    
    // Week 1
    await app.SimulateCalibrationComplete(/* Sensitive */);
    await SimulateUserDay(app, timeProvider, sampleCount: 1000, avgTier: 7);
    
    // Week 2: improvement
    timeProvider.AdvanceBy(TimeSpan.FromDays(7));
    await SimulateUserDay(app, timeProvider, sampleCount: 1000, avgTier: 6);
    
    // Week 3: stable
    timeProvider.AdvanceBy(TimeSpan.FromDays(7));
    await SimulateUserDay(app, timeProvider, sampleCount: 1000, avgTier: 5);
    
    // Week 4: ask chatbot
    timeProvider.AdvanceBy(TimeSpan.FromDays(7));
    await app.OpenChatbotTab();
    var response = await app.SendChatbotMessage("我用四週了，有改善嗎");
    
    Assert.True(
        response.Reply.Contains("進步") || 
        response.Reply.Contains("改善") ||
        response.Reply.Contains("提升"),
        "Chatbot should acknowledge progress over 4 weeks");
}
```

**注意**：HabituationEngine 當前 API 是接 `dtSeconds`，已經是 time-injectable。但 chatbot context（trajectory summary、event log）需要對 SimulatedTimeProvider 同樣支援。Phase 8 的 E2E infra 需要明確這條 dependency injection。

### 4.4.3 Documentation Deliverables

**docs/HABITUATION-CHATBOT-INTEGRATION.md**:

```
1. Architecture overview
   - Diagram of read/write flows
   - Chatbot's role boundary (proposer, not actor)
2. Read-side specification
   - [habituation_state] block format
   - When chatbot receives what
3. Write-side specification
   - Structured token types
   - User confirm flow
   - Defensive intercept rules
4. Safety provisions
   - Safety floor
   - Vestibular advisory protection
   - High-risk-combo protection
5. Failure modes and mitigations
6. Test coverage map
7. Known limitations (e.g. LLM hallucination risk on rare patterns)
```

**docs/SAFETY-PROVISIONS.md** (新 + 整合既有):

涵蓋整個系統的 safety design：

- Safety floor (both chatbot services)
- Medical advisory persistence
- Defensive intercept layer
- Crisis resource directory

**docs/POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md** (新):

- 兩個 sprint 的 surface 互動點
- Power Save 對 chatbot 影響
- Card 上的 chatbot proposal 區的設計來源

### 4.4.4 Lessons Learned Carry-Forward

Pop-up sprint 累積的 7+ 條 lessons learned，明確列出 Habituation sprint 已 mitigate 哪些、carry-forward 哪些、新增哪些：

```
| Lesson | Status in Habituation Sprint |
|---|---|
| L1 env var leak | N/A — Habituation has no env-var gated paths |
| L2 IsType test pinning impl | Mitigated — all parsers tested via contract (call real consumer) |
| L3 library routing path | Mitigated — defensive intercept doesn't trust LLM output |
| L4 UX not unit-testable | Carry-forward — E2E scenarios required |
| L5a library/OS desync | Carry-forward — Mistral API silent failure handling needed |
| L5b dotnet run vs publish | Carry-forward — sanity test list mandates both |
| L6 documented but unimplemented | Watch — verify no .NET API used in Habituation has this risk |
| L7 lead false positive | Active discipline — coordinator requires source paste before strong assertion |
| L8 capability gap mistaken as overreach | Active discipline — agent transparency in tool usage |

New lessons from Habituation sprint:
| L9 LLM structured output not deterministic | Defensive parser, deterministic intercept |
| L10 prompt rule violations require client-side guard | All safety rules duplicated as code, not just prompt |
| L11 safety floor must precede LLM call | Hardcoded crisis response, never LLM-generated |
```

---

## 4.5 Cross-Phase Design Decisions Summary

整理所有跨 phase 的設計決策，方便 user 一次 review：

### Decision 1: Path I (Caller-side enrichment)

不動 MistralChatbotService 內部，由 caller 組裝 `[habituation_state]` block 注入 user message body。

### Decision 2: Schema NOT bumped to v3

Power Save state 透過 caller-side dynamic query 注入，不加 schema 欄位。`longitudinal_observations` 維持 `List<string>` 不結構化升級。

### Decision 3: Three structured token types

- `[PROFILE_OVERRIDE]` — chatbot 提議 engine 設定變更
- `[OBSERVATION_ADD]` — chatbot 累積跨對話觀察
- `[RESUME_ACKNOWLEDGE]` — chatbot 引導 user 確認從 tier ≥ 8 pause 恢復

### Decision 4: Flow A (Card-embedded user confirm)

Proposal 出現在 Notification Card 的新區塊。User 點 Accept/Decline/Later。

### Decision 5: Safety floor migration is Phase 7a (first sub-letter)

MistralChatbotService 補 safety floor 是 Phase 7 第一個 commit，不能 defer。

### Decision 6: Defensive intercept at Applicator layer

Parser 仍 parse、UI 仍 show proposal，但 Applicator 在 user click Accept 時 deterministic block 違反 safety 的 proposal。

### Decision 7: Engine API NOT extended

HabituationEngine public API 已預留 chatbot 路徑（SetProfile with "chatbot_override" source、EffectiveTrigger setter 等）。Phase 7 不加新 engine method。

### Decision 8: E2E Test Infra as separate mini-sprint

E2E infra 在 Habituation sprint 開工前獨立完成，不混入 Phase 6 的 sub-letter。

### Decision 9: Pending proposal NOT persisted across app restart

User 沒 confirm 的 proposal，若 app 重啟，proposal 自動失效。理由：

- 防止「ghost apply」（user 重開 app 不記得有 pending proposal 就 click 接受）
- Pending state 是 in-memory only，簡化實作
- LLM 下次對話會重新評估，必要時重新 propose

### Decision 10: One active proposal at a time

不允許 chatbot 累積多個 pending proposal。新 proposal 自動 supersede 舊 proposal（舊 proposal 標記為 superseded，記入 event log）。

---

## 4.6 Spec Open Questions for User

雖然 Section 4 已經做了很多 design decision，仍有以下 open questions **需要 user 在 Section 4 review 時答**：

### Q4.1: Engine 是否有 EventLog hook?

Section 4.2.4 trajectory summary 依賴 `IEventLogStore` 有 `HabituationLevelChange` / `SevereSpike` event type。

**如果沒有**：Phase 6c 需要先補 event 記錄路徑（HabituationEngine 在 level 變化或 spike 時寫 event log）。可能要拆 Phase 6c-pre。

**Action**: Agent A 或 user grep 確認當前 IEventLogStore 是否有這些 event type。

### Q4.2: Pop-up sprint 最終 PowerSave 持久化位置?

Section 4.2.3 假設 PowerSave 存在 UserPreferences。如果 Pop-up sprint 最終存在別處（in-memory only / Settings.json / 其他），需對應調整。

**Action**: User 確認 Pop-up sprint 收尾時 PowerSave 的存放位置。

### Q4.3: Chatbot proposal 的 expires_at_iso 預設多長?

Section 4.3.1 schema 含 `expires_at_iso`。Chatbot 自己決定 expiry 但 spec 應該給範圍。

**選項**:

- 24 小時（短，避免遺忘）
- 7 天（長，user 可以慢慢決定）
- 48 小時（折衷）

**advisor 建議**: 48 小時。`HabituationOverrideProcessor.Validate` 對超過 7 天的 proposal reject。

### Q4.4: Phase 8 docs 拆幾份?

Section 4.4.3 列了三份 docs：

- HABITUATION-CHATBOT-INTEGRATION.md
- SAFETY-PROVISIONS.md
- POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md

**選項**:

- 三份分開（每份聚焦清楚，但維護成本高）
- 合併成一份 HABITUATION-PHASE-COMPLETE.md（單一進入點，但內容雜）
- 兩份（合併 INTEGRATION + AUDIT，獨立 SAFETY）

**advisor 建議**: 兩份（INTEGRATION 含 audit、SAFETY 獨立易引用）。

### Q4.5: Chatbot 可否提議 `ResetOffset`?

Section 4.3.1 含 `OverrideIntent.ResetOffset`。`HabituationEngine.Reset()` 會清掉所有 habituation 進度。

**問題**: 這是非常 destructive 的操作。Chatbot 在哪些情境下可以提議?

**選項**:

- A. 完全禁止 chatbot 提議 reset（user 必須走 Settings 手動）
- B. 允許 chatbot 提議，但 confirm UI 顯示「⚠️ 這會清掉你所有 habituation 進度」
- C. 允許 chatbot 提議，但 expires_at_iso 限制 ≤ 1 小時（強迫 user 立刻決定）

**advisor 建議**: A。Reset 是 user 自己的決定，chatbot 不該主導。Phase 7 把 `ResetOffset` 從 OverrideIntent enum 移除。

### Q4.6: Chatbot conversation history 是否 persist?

當前 MistralChatbotService 是 stateless per turn。Habituation goals 要求「對話縱深感」——但這是透過 `[habituation_state]` 注入 state，**不是透過 conversation history**。

**問題**: 是否要讓 MistralChatbotService 也有 session 概念（仿 CalibrationChatbotService）?

**選項**:

- A. 維持 stateless（goals 透過 state 注入達成）
- B. 加 session（更完整的「對話記憶」但 scope creep）

**advisor 建議**: A。session 加進來會讓 sprint 範圍爆炸。Goals 1+2+5 透過 state 注入即可達成 80%；剩 20% 是「session 內的對話脈絡」這條，由 LLM 自己 short-term memory 處理（user 同 session 內 reference「剛才說的」LLM 看得到，因為仍在 context window 內）。

跨 session 的記憶透過 `longitudinal_observations` 補。

### Q4.7: chatbot_override 來源是否寫入 ProfileChanged event?

`HabituationEngine.SetProfile(profile, source)` 已支援 `source="chatbot_override"`。`ProfileChanged` event handler（FileHabituationProfileStore）會寫 snapshot。

**問題**: 是否需要額外的 audit log 區分 chatbot-driven changes vs user-driven changes?

**選項**:

- A. 不需要——`profile_source` 欄位已記錄
- B. 需要——加一個 ChatbotActionLog 專門記錄 chatbot 觸發的所有 engine 變更（包含 EffectiveTrigger、SevereStrikeLimitOverride 等不只 profile 的變更）

**advisor 建議**: B。理由：

- Vestibular 用戶可能未來 audit「chatbot 是否曾建議我關掉 protection」
- Phase 8 acceptance 需要驗證 defensive intercept 真的 block 了 chatbot 違規（沒 log 沒法 verify）
- 這個 log 是 Phase 7g 的 LongitudinalObservationWriter 之外的獨立 audit trail

---

## 4.7 Section 4 Resume Recap

到此 Section 4 完整 paste。User 要 review 的範圍：

```
4.1 Phase sequencing                              ← 5 phases + sub-letters
4.2 Phase 6 Read-side detail                      ← Path I, [habituation_state], trajectory, system prompt
4.3 Phase 7 Write-side detail                     ← tokens, parser, processor, applicator, UI, safety
4.4 Phase 8 Acceptance + docs                     ← 5 scenarios, 4-week sim, 3 docs
4.5 Cross-phase 10 design decisions               ← summary
4.6 7 open questions for user                     ← Q4.1 to Q4.7
```

**Open questions need user response**:

```
Q4.1: Engine has EventLog hook? (verify with Agent A grep)
Q4.2: Power Save 持久化位置 (depends on Pop-up sprint completion)
Q4.3: Proposal default expiry (48hr suggested)
Q4.4: Docs 拆幾份 (2-file suggested)
Q4.5: ResetOffset proposal allowed? (No suggested)
Q4.6: Chatbot session persist? (No suggested)
Q4.7: ChatbotActionLog needed? (Yes suggested)
```

---

## Next Steps

Section 4 已完成。剩下三個 section：

- **Section 5**: E2E Test Infrastructure Scoping (independent mini-sprint)
- **Section 6**: Cross-Sprint Lessons Learned Carry-forward (already inline in 4.4.4，可能 minor expansion)
- **Section 7**: Sprint Forward Plan (整合所有 section 成可執行 timeline)

如果 user 對 Section 4 有 raise 反對意見，**現在是修改的最佳時機**——Section 5-7 都依賴 Section 4 的 design lock。

如果 Section 4 user OK（含 advisor 對 Q4.1-Q4.7 的建議答案），我繼續 paste Section 5-7。

**User 對 Q4.1-Q4.7 的快速回應建議格式**:

```
Q4.1: Agent A confirm / 需查
Q4.2: <Pop-up sprint 最終 path, e.g. UserPreferences.json>
Q4.3: 48hr / 24hr / 7d / 自訂
Q4.4: 2 docs / 3 docs / 1 doc
Q4.5: 不允許 / 允許 with warning / 允許 with 1hr expiry
Q4.6: stateless / 加 session
Q4.7: 需要 ChatbotActionLog / 不需要
```

回完我 paste Section 5-7。如果想等 Pop-up sprint 結束再答 Q4.2，告訴我，我先 paste Section 5-7 (Q4.2 留 placeholder)。

Hard Stop in Section 4 review。

----
User:
Q4.1 不確定，要請Coding Agent 排查 Q4.2 同4.1 Q4.3 48h Q4.4 兩份 Q4.5 A Q4.6 A, 受限於Mistral 3B 的context window大小 Q4.7 B

----
# Section 4 Locked — Q4.1 / Q4.2 標記為 Coding Agent 排查項

User 回應整理：

|Q|User Answer|Status|
|---|---|---|
|Q4.1|待 Coding Agent 排查|⏸ Pending|
|Q4.2|待 Coding Agent 排查|⏸ Pending|
|Q4.3|**48 hours**|✅ Locked|
|Q4.4|**2 docs** (INTEGRATION + SAFETY)|✅ Locked|
|Q4.5|**A** — ResetOffset 不允許 chatbot 提議|✅ Locked|
|Q4.6|**A** — stateless（受 Mistral 3B context window 限制）|✅ Locked + 補充理由|
|Q4.7|**B** — ChatbotActionLog 需要|✅ Locked|

User 的 Q4.6 補充非常重要。我必須先停下來把這條 reasoning 寫進 spec，因為它影響後續多個設計決策。

---

## Q4.6 補充的影響展開

User 指出 **Mistral 3B context window 限制**——這個 reasoning 必須明確記錄並影響 Phase 6/7 設計。

### Mistral 3B 的 context window

實際 token 限制依部署而定。SenseEase 用的是 local llamafile 部署（透過 `http://127.0.0.1:8181/v1/chat/completions`），常見 Mistral 3B 配置：

- Mistral-7B-Instruct: 8K context
- Mistral-3B (e.g. Phi-3 mini equivalent class): 4K context, 部分 fine-tune 到 8K

**保守假設 4K tokens**，扣除：

```
System prompt:                    ~1000 tokens (3000 字 * 0.33)
[RecentAdjustments] block:        ~300 tokens
[RecentEvents] block:             ~300 tokens
[habituation_state] block (new):  ~250 tokens
Language pin + "Return JSON":     ~30 tokens
User text:                        ~50-500 tokens (depending)
JSON response budget:             max_tokens=256 (from MistralChatbotService line 339)
─────────────────────────────────────
Reserved for headroom:            ~500 tokens
─────────────────────────────────────
Usable for conversation history:  ~1000-1500 tokens
```

這個算數**非常窘**。如果 sprint 真的試圖加 session-based conversation history，會撞到三個牆：

1. **Hard limit**：超過 context window 必然 truncate，truncate 規則不可控
2. **Soft quality drop**：靠近 context window 邊界，small model 注意力品質下降
3. **Cost**：每 turn 帶完整 history → tokens 線性爆炸 → 延遲線性爆炸

### Implications to Spec

User 的 Q4.6=A 不只是「設計選擇」，是**架構必要性**。需要把這條補進 Section 4.2.1 與 Section 4.3.x 的 design rationale：

#### Update to Section 4.2.1 (Path I caller-side enrichment)

新增 rationale：

> **Mistral 3B context budget constraint**: chatbot context is severely token-limited. The caller-side builder MUST be aware of token cost. `[habituation_state]` block should be:
> 
> - Minimized: only fields chatbot actually uses (drop schema_version, updated_at_iso, etc.)
> - Conditional: only include `longitudinal_observations` when count > 0
> - Truncated: limit `trajectory_summary` to one short sentence
> - Capped: max 5 most recent `longitudinal_observations`

#### Update to Section 4.2.2 ([habituation_state] block format)

把原本的「豐富版」block 精簡。**Token diet version**：

```
[habituation_state]
tier: 4
level: 12.3
profile: SENSITIVE
state: RUNNING
calibrations: 2
last_cal: 2026-04-10
advisory: vestibular
scale: 0.9
trajectory: level 8→12 over 14d, 3 spikes
recent_observations:
  - 2026-05-15: 5-day break, level held
  - 2026-05-01: improvement with new monitor distance
[/habituation_state]
```

**從 ~250 tokens 降到 ~120 tokens**。

去除：

- `habituation_offset_pp`（chatbot 不直接 reason 這個數值）
- `profile_source`（implementation detail）
- `severe_strike_count` / `severe_strike_limit`（只在 awaiting_user_confirm 時 inject）
- `externally_paused`（合併到 state 描述）
- `next_step_eligible_at_iso`（chatbot 不 reason 未來）
- `calibration_confidence` / `calibration_completeness` / `calibration_path`（chatbot 只需要知道有 calibration）
- `personal_pressure_scale`：保留為 `scale`（chatbot 對話可能 reference）
- `symptom_dominance_primary`：移除（過於專業，chatbot reply 不需要）
- `params_snapshot`：移除（cadence numbers chatbot 不 reason）

**保留為 conditional**：

- `power_save_active` — 只在 true 時 include
- `awaiting_user_confirm` — 只在 true 時 include + 加 `last_spike: <iso>`
- `medical_advisory` — 只在非 null 時 include
- `longitudinal_observations` — 只在 count > 0 時 include section header

#### Update to Section 4.3.1 (Structured token grammar)

LLM **output** 也吃 token budget（max_tokens=256）。Structured tokens 必須節省：

**舊 PROFILE_OVERRIDE**（過於 verbose，~80 tokens）：

```
[PROFILE_OVERRIDE]
{
  "intent": "adjust_sensitivity",
  "target_field": "personal_pressure_scale",
  "proposed_value": 1.15,
  "rationale": "5 days in Tier 6-7 suggests under-detection",
  "expires_at_iso": "2026-06-02T12:00:00Z"
}
[/PROFILE_OVERRIDE]
```

**Token-diet PROFILE_OVERRIDE**（~35 tokens）：

```
[PROFILE_OVERRIDE]
{"intent":"adjust_sensitivity","field":"scale","value":1.15,"why":"5d tier 6-7","ttl":"48h"}
[/PROFILE_OVERRIDE]
```

變更：

- `target_field` → `field`
- `proposed_value` → `value`
- `rationale` → `why`
- `expires_at_iso` → `ttl` (chatbot output relative time, parser converts to absolute)

Parser 補正：`ttl: "48h"` → `expires_at_iso: DateTime.UtcNow.AddHours(48)`。

#### Update to Section 4.3.8 (LongitudinalObservation Write)

加 token-aware constraint:

> Each observation hard-capped at 80 characters (was 200). LongitudinalObservations list max 30 entries (was 50). Reason: context budget — these get injected as prompt context.

#### Update to Section 4.2.5 (System prompt section)

System prompt 新加 section 也吃 budget。原本 prompt ~3000 字（1000 tokens）。新 HABITUATION STATE CONTEXT section 我之前寫的版本 ~250 tokens。需要精簡到 ~150 tokens：

**舊版**（過於 verbose，~250 tokens）：

```
[Section 4.2.5 original 7-bullet version]
```

**Token-diet 版**（~120 tokens）：

```
## HABITUATION STATE

User messages may contain [habituation_state] block. Use it naturally:
- Ground replies in current tier/level
- If awaiting_user_confirm: acknowledge recent spike, offer check-in
- If power_save_active: treat data as stale
- If advisory=vestibular: surface this when user requests reducing protection
- Reference longitudinal_observations when relevant to user's question
- DO NOT echo raw key-value pairs

Block is INFORMATIONAL. Continue emitting compact JSON envelope as before.
Habituation context affects reply text only, not deltas.
```

### Cross-phase implication: Total prompt budget

更新的 token budget 估算：

```
System prompt:                    ~1100 tokens (含 new section)
[RecentAdjustments] block:        ~300 tokens
[RecentEvents] block:             ~300 tokens
[habituation_state] block (new):  ~120 tokens (was 250)
Language pin + "Return JSON":     ~30 tokens
User text:                        ~200 tokens average
JSON response budget:             max_tokens=256
─────────────────────────────────────
Total fixed:                      ~2300 tokens
Available headroom:               ~1700 tokens (4K) or 5700 tokens (8K)
```

對 4K context 還是緊但能跑。對 8K context 充裕。

**新 Phase 6 spec 必須加 token budget guard**：

```csharp
// in BuildSenseEaseStateLine()
const int MaxBlockTokens = 150;  // hard guard

string block = sb.ToString();
int estimatedTokens = block.Length / 4;  // crude estimate
if (estimatedTokens > MaxBlockTokens)
{
    // Drop optional sections: 
    // 1. longitudinal_observations entries beyond 3
    // 2. trajectory_summary truncated
    // Log warning
}
```

---

## Q4.1 + Q4.2 的 Coding Agent 排查項

User 要 Coding Agent 排查兩條。為了不打斷 Section 5-7 撰寫，advisor 我把排查 spec 寫好，user 可在合適時間發給 Coding Agent。

### 排查 1: IEventLogStore Event Type Inventory (Q4.1)

```
Hi Agent A, advisor needs context for Habituation Phase 6 spec.

請 grep + paste 以下資訊:

1. `IEventLogStore` interface 完整定義 (file location + method signatures)

2. `EventCategory` enum 完整 values (if exists)

3. `EventType` enum (or string constants) 完整 values

4. Grep 所有呼叫 IEventLogStore.Append (或對等方法) 的位置:
   - HabituationEngine.cs 內
   - WgcSensingProvider.cs 內
   - 任何其他 caller
   
   重點: 確認是否有以下 event 被記錄:
   - Habituation level 變化 (e.g. level 從 5 升到 8)
   - Severe spike 事件
   - Profile change 事件
   - Engine state transition (Running → Cooldown → Paused)

5. 如果上述 event 沒被記錄, 建議: 哪個 phase / sub-letter 加入這些 event 寫入是合理的?

Just paste, no analysis needed. Advisor 會評估。
```

### 排查 2: Power Save State Persistence Location (Q4.2)

```
Hi Agent A, advisor 也需要 Power Save state 持久化位置確認.

請查 Pop-up sprint 0483b69 commit + 任何後續 follow-up commits:

1. Power Save toggle ON/OFF 狀態, 當前存在哪裡?
   - UserPreferences.json field?
   - In-memory only (跨 app restart 不保留)?
   - 其他位置?

2. 該位置的 read API 是什麼? 
   - e.g. `UserPreferences.Current.IsPowerSaveActive`
   - 或 `_someService.IsPowerSaveActive`

3. 該狀態 change 時, 是否有 event/log 寫入 (任何 store)?

4. Pop-up sprint 7 bugs fix 完成後, 是否計畫變更 Power Save 持久化位置?

Just paste current implementation reality, advisor 評估如何注入 chatbot context.
```

User 可以複製貼給 Agent A。回應收到後 advisor 我會 update Section 4.2.3 & 4.2.4 對應細節。

**Section 5-7 不依賴這兩條排查結果**，可以繼續往下寫。Q4.1+Q4.2 結果回來後我只需要 update Section 4 spec 中對應 implementation detail，不影響整體架構。

---

## Section 4 Final Lock Status

```
Section 4 結構:
  4.1 Phase sequencing                  ✅ Locked
  4.2 Phase 6 Read-side spec            ✅ Locked (含 token diet update)
  4.3 Phase 7 Write-side spec           ✅ Locked (含 token diet update)
  4.4 Phase 8 Acceptance + docs         ✅ Locked
  4.5 Cross-phase 10 decisions          ✅ Locked
  4.6 7 open questions                  ✅ Resolved (Q4.1+Q4.2 排查中)

New design constraints from Q4.6:
  - Token budget awareness across read + write
  - [habituation_state] block compressed
  - Structured tokens use short keys
  - System prompt section compressed
  - Total budget estimate documented
```

---

## 繼續 Paste Section 5

由於前面已經把 Section 4 update 完並 lock，下面開始 Section 5 / 6 / 7。三個 section 不像 Section 4 那麼長，我可以一次 paste 完。

---

# Section 5: E2E Test Infrastructure Scoping

## 5.1 為什麼 E2E Infra 必須先做

Pop-up sprint 累積教訓清晰：unit test 全綠 + manual smoke test pass ≠ production OK。

具體例子：

- **Bug 5 (Path Z review found)**: 「SetTier during sleep mode clobbers paused fill」— unit test 不可能抓到（需 ~300ms idle timer tick）
- **L5a (tray library silent registration)**: library + OS state 不可控
- **L5b (dotnet run vs published exe parity)**: 兩個 build 行為差異
- **L7 (lead false positive)**: 強斷言 + 無 E2E ground truth = 錯誤決策

Habituation sprint 比 Pop-up 更複雜：

- LLM 整合（不確定性 source）
- 多 surface 互動（card / chatbot tab / settings）
- 4 週時序需求
- 跨 session 記憶持久化

**沒 E2E infra 開工 Habituation Phase 6 = 必踩 Pop-up 同類雷**。

## 5.2 E2E Test Infra Scope

### 5.2.1 必要能力清單

```
□ Launch full WPF app from xUnit test process
□ Programmatic interact with WPF UI (button click, text input, window navigate)
□ Inject simulated time (ITimeProvider abstraction)
□ Inject stub LLM (deterministic responses for scenario tests)
□ Read internal state (HabituationProfileSnapshot, UserPreferences, etc.)
□ Verify Tray icon presence + menu items
□ Verify Bar visibility + color
□ Inject simulated WGC frame data (skip real screen capture)
□ Snapshot 比對 (visual regression, optional)
```

### 5.2.2 技術選型評估

#### Option I: FlaUI

- **是什麼**：開源 Windows UI Automation framework，基於 UIA3
- **適合**：black-box UI test (click buttons, read text)
- **不適合**：read in-memory state（需 IPC hack）
- **學習曲線**：medium
- **CI 友好度**：需要 Windows agent + active display session

#### Option II: WpfApplicationFactory (custom)

- **是什麼**：自製 fixture，在 test process 內啟動完整 App 物件圖
- **適合**：white-box (直接 access ViewModel、Services)
- **不適合**：跨 process 場景（multi-process integration）
- **學習曲線**：low (純 C#)
- **CI 友好度**：不需 display session

#### Option III: WinAppDriver

- **是什麼**：微軟 Appium-based UI automation server
- **適合**：official Microsoft tool，跨 framework
- **不適合**：微軟已 deprecated（2023）
- **不推薦**

#### Option IV: 兩者並用 (FlaUI + WpfApplicationFactory)

- **是什麼**：white-box test 用 WpfApplicationFactory，需要真實 UI 互動的 test 用 FlaUI
- **適合**：覆蓋率最高
- **複雜度**：infra 維護兩套

### 5.2.3 advisor 建議：Option IV (兩者並用)

理由：

- Section 4 的 5 個 E2E scenario 涵蓋了 white-box (state assertion) + black-box (button click) 兩類需求
- WpfApplicationFactory 處理 90% 的 logical 場景（cheap, fast）
- FlaUI 補 button-click 與 visual 場景
- 工具差異化 = test code 可讀性更好（看哪種 fixture 就知道是哪類 test）

## 5.3 E2E Mini-Sprint Deliverables

```
□ E2E.1: WpfApplicationFactory base implementation
  - Launch SenseEaseApp with test-mode flag
  - Expose internal services via test-only ITestApiAccessor
  - Cleanup discipline (dispose, kill orphaned processes)
  - HARD STOP: factory works for one trivial scenario

□ E2E.2: SimulatedTimeProvider integration
  - ITimeProvider abstraction in production code (refactor)
  - Test-mode default to SimulatedTimeProvider
  - HabituationEngine + EventLog respect injected time

□ E2E.3: Stub LLM client
  - IChatbotLlmClient + IcalibrationLlmClient stubs
  - Scenario-specific stubs (LlmStub_ProposeScaleIncrease, etc.)
  - Recording mode for capturing real LLM responses → replay
  - HARD STOP: chatbot scenario test runs with stub LLM

□ E2E.4: State inspection harness
  - Read HabituationProfileSnapshot mid-test
  - Read UserPreferences mid-test
  - Read EventLog entries mid-test
  - All thread-safe

□ E2E.5: FlaUI integration for UI scenarios
  - Bar visibility check
  - Notification Card open/close
  - Settings window navigation
  - Tray icon presence

□ E2E.6: Simulated WGC frame injection
  - ISensingProvider abstraction (factor out from WgcSensingProvider)
  - SimulatedSensingProvider in test mode
  - Inject sequence of OFI values for tier/spike simulation

□ E2E.7: Pop-up sprint sanity test catch-up
  - Re-run Pop-up sprint manual checklist as automated E2E
  - Bug 5 (sleep mode clobber) reproducible as automated test
  - L5b dotnet publish parity automated check
```

## 5.4 E2E Mini-Sprint 預估

- E2E.1 + E2E.2: ~1 day
- E2E.3 + E2E.4: ~1 day
- E2E.5 + E2E.6: ~1.5 days
- E2E.7: ~0.5 day
- Polish + lessons learned doc: ~0.5 day

**Total: 4-5 days**

這個 mini-sprint 完成後 → Habituation Phase 6 才開工。

---

# Section 6: Cross-Sprint Lessons Learned Carry-Forward

## 6.1 Pop-up Sprint 累積 Lessons → Habituation Sprint 應用

```
L1: Env var 跨層級洩漏 (process/user/machine)
  → Habituation: chatbot 不依賴 env var, 不適用
  → Carry-forward 紀律: 若有 LLM API key 等 secret, 用 IConfiguration 不用 env var
  
L2: IsType<X> 測試 pinning implementation
  → Habituation: parser tests 不能寫成 Assert.IsType<JsonObject>(parsed)
  → 改為 contract-level: parsed proposal apply 出來 engine state 真的變?
  
L3: 抽象型別測試掩蓋 library 真實 routing path
  → Habituation: chatbot LLM client 測試不可只測 abstract IChatbotLlmClient
  → 必須整合 test: 實際 HTTP 走通到 stub server
  
L4: UX 不易單元測試
  → Habituation: chatbot 對話品質本質上是 UX
  → Mitigation: 5 個 E2E scenario 是必跑, manual review 是 supplement
  
L5a: Library 隱性 silent failure (H.NotifyIcon)
  → Habituation: Mistral local server 可能 silent fail (HTTP 200 but empty body)
  → Mitigation: client-side validate response is parseable JSON
  
L5b: dotnet run vs published exe parity
  → Habituation: publish build 必須跑 E2E
  → 紀律: 每個 phase commit 前 dotnet publish + run published exe
  
L6: Documented but unimplemented API (ImageFormat.Icon)
  → Habituation: 暫無已知 risk
  → 監控: 任何 PR 引入新 NuGet API, 先 sanity test
  
L7: Lead false positive (UseNewBarOverlay assertion error)
  → Habituation: advisor 對 chatbot 行為的強斷言, 必須 paste system prompt 對照
  → 紀律: 任何「chatbot 一定會這樣回」斷言, 跑 stub LLM 驗證
  
L8: Capability gap mistaken as agent overreach
  → Habituation: 三 agent 架構 transparency 必須維持
  → 紀律: agent 在 spawn sub-agent 或用 UI surface 時, 同步告知 advisor
```

## 6.2 Habituation Sprint 預期新 lessons (預測)

advisor 預測 Habituation sprint 會踩到的新雷:

```
L9 (預期): LLM structured token output 不可靠
  - Mistral 3B 約 5% 機率 output malformed JSON 或漏 closing tag
  - Parser 必須 fail-safe (parse 失敗不 crash, log warning, 視為無 token)
  - 對應 mitigation: HabituationOverrideParser.TryParse 永遠回 ParsedHabituationOverride 或 null, 從不 throw

L10 (預期): Prompt 規則違反需 client-side 守門
  - 即使 prompt 寫明 "vestibular 用戶不可 propose 放鬆", LLM 仍可能違反
  - 對應 mitigation: 4.3.6 defensive intercept layer (deterministic, not prompt)

L11 (預期): Safety floor 必須在 LLM call 之前
  - 不可 forward 自殘相關 user text 給 LLM 然後依賴 LLM crisis response
  - Hardcoded crisis response, 跨 zh/en 配對
  - 對應 mitigation: 4.3.7 safety floor migration

L12 (預期): Context window pressure 隨時間累積
  - 隨 user 長期使用, longitudinal_observations 增長
  - 隨 RecentAdjustments / RecentEvents 增長
  - 對應 mitigation: cap + dedup + 智能 truncation

L13 (預期): User confirm flow 的 race condition
  - User open card 看到 pending proposal → 切換 app → 回來 → 點 Accept
  - 中間 proposal 已 expire 但 UI 未刷新
  - 對應 mitigation: applicator 層 re-check expiry before apply

L14 (預期): Chatbot 對話 + Power Save toggle 互動
  - User toggle Power Save off 後立刻 ask chatbot
  - Sensing 還在 ramp up, state 處於過渡期
  - 對應 mitigation: chatbot context block 加 "sensing_just_resumed" 短期 flag (e.g. < 30s since resume)
```

## 6.3 跨 sprint discipline carry-forward

```
Pop-up sprint 紀律 → Habituation sprint 採納:

✓ Hard stop after each commit
✓ Source paste before strong assertion
✓ Single commit per fix (no fix-forward)
✓ Visual review by user as final acceptance (not skippable)
✓ Lessons learned doc updated per sprint
✓ Pre-commit sanity test list (per Pop-up Patch 7+)

Habituation sprint 新增紀律:

+ E2E scenario test green before user verification
+ Stub LLM scenario test + real LLM smoke test, both pass
+ Safety floor + defensive intercept tests are blockers (cannot defer)
+ Token budget guard in build pipeline (warn if any single block > 150 tokens)
+ Multi-agent review chain (M1+) for Phase 7 commits (write-side higher risk)
```

---

# Section 7: Sprint Forward Plan

## 7.1 Sprint 整體 Timeline

```
NOW (Section 4 完成)
  │
  ├─► User: 處理 Pop-up sprint 7 bugs (parallel, ongoing)
  │   └─► v0.8 tag (predicted: 1-3 days from now)
  │
  ├─► Coding Agent A: Q4.1 + Q4.2 排查 (parallel, < 1 day)
  │   └─► Advisor updates Section 4 detail
  │
  └─► Advisor: 完成 Section 7 + 寫入 docs/HABITUATION-PHASE-6-7-8-PLAN.md (today)

After v0.8 tag:
  │
  ├─► Mini-Sprint: E2E Test Infra (4-5 days)
  │   ├─ User-monitored, Coding Agent A executes
  │   ├─ Pop-up sprint sanity tests automated as E2E catch-up
  │   └─ HARD STOP: user verify E2E infra works end-to-end
  │
  ├─► Habituation Phase 6 (3-5 days)
  │   ├─ 6a: Path I caller-side enrichment
  │   ├─ 6b: [habituation_state] block format (token-diet locked)
  │   ├─ 6c: Power Save + trajectory injection (depends on Q4.2 answer)
  │   ├─ 6d: System prompt update
  │   └─ 6e: Phase 6 acceptance (Goals 1+2+5 read-side verify)
  │
  ├─► Habituation Phase 7 (5-7 days)
  │   ├─ 7a: Safety floor migration (Goal 4 prerequisite)
  │   ├─ 7b: Structured token grammar + parsers
  │   ├─ 7c: HabituationOverrideProcessor
  │   ├─ 7d: HabituationOverrideApplicator
  │   ├─ 7e: User confirm UI flow (Card-embedded)
  │   ├─ 7f: Defensive intercept layer
  │   ├─ 7g: LongitudinalObservation write + ChatbotActionLog
  │   └─ HARD STOP: user verify propose-confirm-apply E2E
  │
  └─► Habituation Phase 8 (2-3 days)
      ├─ 8a: 5 E2E scenarios
      ├─ 8b: 4-week simulation test
      ├─ 8c: 2 docs (INTEGRATION + SAFETY)
      ├─ 8d: Lessons learned carry-forward writeup
      └─ HARD STOP: user final acceptance → tag v0.9

Total estimated: 14-20 days actual work (excluding wait time)
```

## 7.2 Sprint 依賴關係

```
v0.8 tag (Pop-up sprint sealed)
  │
  ▼
E2E Test Infra mini-sprint
  │
  ▼
Habituation Phase 6 (read-side)
  │
  ▼
Habituation Phase 7 (write-side, includes Phase 7a safety floor)
  │
  ▼
Habituation Phase 8 (acceptance)
  │
  ▼
v0.9 tag (Habituation sprint sealed)
```

**Critical path**: Pop-up tag → E2E infra → Phase 6 → Phase 7 → Phase 8. 每個都 block 下一個。

**Parallelizable**:

- 在 Pop-up 收尾期間：advisor 可繼續完善 prep work（已大部分完成）+ Q4.1/Q4.2 排查
- 在 E2E infra 期間：advisor 可開始寫 docs draft + Phase 6 spec 細節
- 在 Phase 6 期間：advisor 可寫 Phase 7 detailed prompt template

## 7.3 User 簽核點 (HARD STOPS)

```
[1] v0.8 tag: user 確認 Pop-up sprint 完成
[2] E2E infra acceptance: user 確認 E2E factory works
[3] Phase 6 acceptance: user 跑 5 個 E2E scenario, 確認 chatbot 對話品質符合 Goal 1+2+5
[4] Phase 7a safety floor: user 確認 self-harm 觸發 crisis response (manual test)
[5] Phase 7 acceptance: user 跑 propose-confirm-apply 端到端 (Scenario C)
[6] Phase 7f defensive intercept: user 驗證 vestibular + relaxation 被擋 (Scenario D)
[7] Phase 8 acceptance: user 全部 5 個 scenario 通過 + 2 個 docs 滿意
[8] v0.9 tag: user 整體驗收
```

每個簽核點 advisor 不催促 user，user 隨時可以 halt 一個 sprint segment。

## 7.4 Risk Register

```
High Risk:
├─ Mistral 3B 對 structured token 的 compliance 低
│  Mitigation: defensive intercept, parser fail-safe, real LLM smoke test
├─ Vestibular user E2E scenario 涉及 medical context, 敏感度高
│  Mitigation: SAFETY-PROVISIONS.md 明確文件化 testing protocol
├─ E2E test infra 在 CI 可能不穩 (FlaUI window mode dependency)
│  Mitigation: local-only initially, CI parity later sprint

Medium Risk:
├─ Phase 7e user confirm UI 與 Pop-up sprint Card 設計衝突
│  Mitigation: 等 Pop-up tag 完, 再展開 Card-embedded design
├─ Token budget 在 user 長期使用後仍可能爆
│  Mitigation: L12 預測 lesson 對應的 dedup + cap discipline
├─ ChatbotActionLog (Q4.7 = B) 設計細節 spec 還未展開
│  Mitigation: Phase 7g 內展開, 與 LongitudinalObservation 共用 file format

Low Risk:
└─ Sprint 之間的 schema migration 風險 (我們 lock 了 NOT bumping schema)
   Mitigation: 已 lock
```

## 7.5 Forward Plan 確認的 user 行動

User 在 sprint forward plan 確認後可以做的事：

1. **發 Q4.1 + Q4.2 排查 prompt 給 Coding Agent A**（5.2 提供現成 prompt）
2. **繼續處理 Pop-up sprint 7 bugs**（已在做）
3. **接到 Coding Agent A 回應後 forward 給 advisor**（advisor update Section 4.2.3/4.2.4）
4. **Pop-up sprint tag v0.8 後**通知 advisor，啟動 E2E mini-sprint dispatch

advisor 不催進度，user 自決步調。

---

# 整體 Prep Work 完成

```
✅ Section 1: User Need Restatement
✅ Section 2: System Architecture Audit
✅ Section 3: Design Goal Decomposition
✅ Section 4: Phase 6/7/8 Detailed Specification (含 token diet update)
✅ Section 5: E2E Test Infrastructure Scoping
✅ Section 6: Cross-Sprint Lessons Learned Carry-Forward
✅ Section 7: Sprint Forward Plan

Open items:
⏸ Q4.1 + Q4.2 待 Coding Agent A 排查 (不阻塞 sprint 啟動)
⏸ Pop-up sprint 7 bugs 待 user 處理 (block sprint 啟動)
⏸ Advisor 寫入 docs/HABITUATION-PHASE-6-7-8-PLAN.md (next deliverable)
```
