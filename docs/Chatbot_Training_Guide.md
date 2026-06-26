# SenseEase Mistral 訓練說明書（交付版）

> 對齊基準：本說明書與生產 NLU 契約（`MistralChatbotService.SystemPrompt`、
> `NluResult`、`ChatbotNluService`、`FeedbackLexicon`）逐欄對齊。任何欄位名稱、
> 列舉值、數值範圍若與下列原始碼不一致，以原始碼為準：
> - `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs`（系統提示、JSON 契約、override 區塊）
> - `src/SenseEase.Core/Models/NluResult.cs`（解析後欄位）
> - `src/SenseEase.Infrastructure/Nlp/FeedbackLexicon.cs`（無模型 lexicon 回退）

## 1. 目的與範圍
- 目標模型：本地 Mistral / Ministral 3B（GGUF 推論，透過 `llama-server.exe` 的 OpenAI 相容 HTTP 介面），用於 SenseEase AI Assistant 的 NLU 大腦。
- 任務目標：
  - 正確判定使用者意圖（共 7 種，見 §2）。
  - 對 `ReportDiscomfort` 輸出個人化的 shader 參數 delta（`fov_delta` / `mask_depth_delta` / `grid_visibility_delta` / `feather_width_delta`），其餘意圖一律輸出零 delta。
  - 必要時輸出寫入側結構化區塊（`[PROFILE_OVERRIDE]` / `[OBSERVATION_ADD]` / `[RESUME_ACKNOWLEDGE]`，見 §6）。
  - 中英雙語穩定回覆，避免泛用健康建議。
- 非目標：
  - 不做醫療診斷。
  - 不回答與 SenseEase 無關領域（金融、法律、一般閒聊延展）。

## 2. 意圖分類（共 7 種，需對齊）

JSON envelope 的 `intent` 欄位 **永遠** 是下列 6 個標準值之一。第 7 個意圖類別
（profile / sensitivity / strike-limit 調整）**不會** 出現在 envelope 的 `intent`，
而是放進 §6 的 `[PROFILE_OVERRIDE]` 區塊。

| # | Intent | 觸發情境 | Delta 行為 |
|---|--------|---------|-----------|
| 1 | `ReportDiscomfort` | 使用者「現在」回報暈眩 / 噁心 / 頭痛 / 眼睛疲勞 / 視覺不適 | 套用個人化 delta（4 欄），severity 1–5 僅依當前文字 |
| 2 | `ReportComfort` | 使用者表示好轉 / 穩定 / 道謝 | 零 delta，給予安撫 |
| 3 | `ResetRequest` | 使用者要求復原 / 重設 / 回到預設 | 零 delta（app 負責實際 reset） |
| 4 | `AnalyzeRequest` | 使用者詢問過去 session / 原因 / 模式 | 零 delta，引用 `[RecentAdjustments]` / `[RecentEvents]` 的具體數值 |
| 5 | `FollowupRequest` | 使用者表達不適但**未指名症狀**（如「怪怪的」「I feel off」） | 零 delta，渲染 2–4 顆選項卡 |
| 6 | `Unknown` | 問候 / 離題 / 不明的非不適訊息 | 零 delta，友善簡短回覆 |
| 7 | PROFILE_OVERRIDE 意圖族 | 使用者明確要求調整 sensitivity / 切換 profile / 調整 strike limit | envelope intent 設為 `AnalyzeRequest`，實際提案寫進 `[PROFILE_OVERRIDE]` 區塊（見 §6） |

重要對齊規則（與系統提示一致）：
- 使用者回報「當前」不適（即使同時提到過去、或帶有 `[habituation_state]` 背景）→ 仍為 `ReportDiscomfort`，不可降級為 `AnalyzeRequest`。
- 使用者已**指名**症狀（「我頭痛」「I have a headache」）→ 直接 `ReportDiscomfort` 套用 delta，**不要**用 followup 卡再問是哪種症狀。
- `FollowupRequest` 僅在「表達不適」且「未指名症狀」時使用。
- 嚴禁把 `adjust_sensitivity` / `switch_profile` / `adjust_strike_limit` 當成 envelope 的 `intent`；那些是 PROFILE_OVERRIDE 區塊內的 intent。
- severity 永遠只看**當前訊息**，不可從歷史累加。

## 3. 致動參數（你的 delta 物理上會做什麼）

四個 delta 直接改動使用者螢幕，務必謹慎；非致動意圖一律全部填 0。

| JSON 欄位 | 型別與範圍 | 預設基準 | 語意 |
|-----------|-----------|---------|------|
| `fov_delta` | int **[-30, 0]**（百分比） | 100%（全清晰） | 越負→中央清晰區越小、周邊遮罩越大、周邊運動越少、vection 越少。host 在 15% 設下底夾（使用者永遠保有一些清晰視野）。 |
| `mask_depth_delta` | float **[-0.3, 0.3]** | 0.74 | 周邊遮罩不透明度微調。正值加深（更多周邊運動遮蔽），負值變淡。輕度不適：小正 mask + 小負 fov；強症狀：更深 mask。 |
| `grid_visibility_delta` | float **[-0.3, 0.3]** | 0.75 | 靜態 Chang-Hain 錨點網格能見度。提供固定視覺參考，對暈眩 / 旋轉 / 眩暈特別有效（抑制 vection）；對純眼睛疲勞幫助較少。 |
| `feather_width_delta` | float **[-0.3, 0.3]** | 0.50 | 遮罩邊緣柔和度。正值柔化過渡。**少用**——僅當使用者明確抱怨邊緣太硬 / 太軟時調整。 |

個人化政策（與系統提示對齊）：
- 鼓勵依使用者「確切措辭」選擇細粒度 delta，**不要**用預設 bucket。
- 預設小 delta。大 delta（`|md| ≥ 0.18`、`|gv| ≥ 0.15`、`fov ≤ -22`）只有在使用者明確描述「嚴重 / 無法忍受 / 失能」症狀時才合理。
- 選 delta 前先讀 `[RecentAdjustments]`；若已累積大幅調整，改用小 nudge（-3..-5）或零，並在回覆中說明。
- delta 量值要鏡射使用者的強度詞：「a little dizzy」→ 極小步；「very dizzy」→ 有感步；「about to throw up」→ 強步。
- 調整**跨 session 持續**，只有 `ResetRequest` 會還原預設。猶豫時偏向較小 delta + 安撫回覆。
- `AnalyzeRequest` / `FollowupRequest` / `Unknown` / `ResetRequest` / `ReportComfort`：一律零 delta，無例外。

## 4. JSON 契約（FIELDS）

模型輸出 **單行 compact JSON**，無 markdown fence，JSON 外無任何文字。host 以
snake_case 反序列化（`JsonNamingPolicy.SnakeCaseLower`）。

| 欄位 | 必填性 | 型別 / 範圍 |
|------|--------|-----------|
| `intent` | REQUIRED | `{ReportDiscomfort, ReportComfort, ResetRequest, AnalyzeRequest, FollowupRequest, Unknown}` |
| `severity` | REQUIRED | int 1–5（非不適意圖用 1；host `Math.Clamp(1,5)`） |
| `reply` | REQUIRED | 非空字串 |
| `fov_delta` | OPTIONAL | int **[-30, 0]**（非致動意圖省略或 0；host 夾至 [-30,0]） |
| `mask_depth_delta` | OPTIONAL | float **[-0.3, 0.3]** |
| `grid_visibility_delta` | OPTIONAL | float **[-0.3, 0.3]** |
| `feather_width_delta` | OPTIONAL | float **[-0.3, 0.3]** |
| `followup_question` | REQUIRED IFF `intent=FollowupRequest` | 字串 |
| `followup_options` | REQUIRED IFF `intent=FollowupRequest` | 2–4 個字串，每個 ≤ 30 字 |

對齊備註：
- 四個 delta 在 host 端為 nullable——「明確填 0」與「省略欄位」被區分對待；任一非 null 即視為「模型已介入」並採用模型的四個值（缺漏者視為 0）。
- 若 `ReportDiscomfort` 且 severity ≥ 2 但四個 delta 全為 0，host 視為「模型未介入」並退回 lexicon 的 severity-bucket 預設值（見 §8）。
- `AnalyzeRequest` / `FollowupRequest` 為非致動意圖，host 強制四個 delta 歸零，即使模型誤填也不採用。

## 5. 訓練資料格式（JSONL）

每行一筆。`meta` 用於評估對齊，請填齊四個 delta 與 severity。

EN（ReportDiscomfort，含 4 個 delta）：
```json
{"messages":[{"role":"system","content":"You are the NLU brain of SenseEase..."},{"role":"user","content":"I have a mild headache"},{"role":"assistant","content":"{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"Got it — I've slightly tightened the field of view and softened the mask edges to ease the visual load.\",\"fov_delta\":-7,\"mask_depth_delta\":0.05,\"grid_visibility_delta\":0.02,\"feather_width_delta\":0.03}"}],"meta":{"intent":"ReportDiscomfort","severity":2,"fov_delta":-7,"mask_depth_delta":0.05,"grid_visibility_delta":0.02,"feather_width_delta":0.03,"language":"en"}}
```

繁中（ReportDiscomfort）：
```json
{"messages":[{"role":"system","content":"You are the NLU brain of SenseEase..."},{"role":"user","content":"我覺得有點頭暈"},{"role":"assistant","content":"{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"了解，我已稍微收緊 FOV、加深周邊遮罩，並提升錨點網格能見度以穩定視覺。\",\"fov_delta\":-8,\"mask_depth_delta\":0.06,\"grid_visibility_delta\":0.04,\"feather_width_delta\":0.0}"}],"meta":{"intent":"ReportDiscomfort","severity":2,"fov_delta":-8,"mask_depth_delta":0.06,"grid_visibility_delta":0.04,"feather_width_delta":0.0,"language":"zh-TW"}}
```

FollowupRequest（未指名症狀，須附 `followup_question` + `followup_options`）：
```json
{"messages":[{"role":"system","content":"You are the NLU brain of SenseEase..."},{"role":"user","content":"I feel off"},{"role":"assistant","content":"{\"intent\":\"FollowupRequest\",\"severity\":1,\"reply\":\"Which sensation matches what you're feeling?\",\"followup_question\":\"Which sensation matches what you're feeling?\",\"followup_options\":[\"Dizziness\",\"Nausea\",\"Headache\",\"Eye strain\"],\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}"}],"meta":{"intent":"FollowupRequest","severity":1,"language":"en"}}
```

AnalyzeRequest + PROFILE_OVERRIDE（envelope 後另起一行附 override 區塊，見 §6）：
```json
{"messages":[{"role":"system","content":"You are the NLU brain of SenseEase..."},{"role":"user","content":"我最近 5 天 tier 一直在 6-7 穩定，可以把 sensitivity 調鬆一點嗎?"},{"role":"assistant","content":"{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"看到您過去 5 天 tier 維持在 6-7 穩定，我建議把 sensitivity 從 1.0 調至 0.9 — 卡片上會出現詳細選項。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n[PROFILE_OVERRIDE]\n{\"intent\":\"adjust_sensitivity\",\"target_field\":\"personal_pressure_scale\",\"proposed_value\":0.9,\"rationale\":\"用戶最近 5 天 tier 穩定在 6-7，可以小幅放鬆\",\"expires_at_iso\":\"2026-05-30T17:00:00Z\"}\n[/PROFILE_OVERRIDE]"}],"meta":{"intent":"AnalyzeRequest","severity":1,"override_intent":"adjust_sensitivity","language":"zh-TW"}}
```

## 6. 寫入側結構化區塊（PROFILE_OVERRIDE / OBSERVATION_ADD / RESUME_ACKNOWLEDGE）

多數回合**不**輸出任何區塊。需要時，在 JSON envelope 的右大括號 `}` 之後**另起新行**附上，
**絕不**嵌入 envelope 欄位之間。這些區塊不顯示給使用者（host 渲染前剝除），用於驅動引擎提案或縱向筆記。

關鍵規則（小模型常錯，務必於訓練資料中強化）：
1. envelope 的 `intent` 永遠是 §2 的 6 個標準值；`adjust_sensitivity` / `switch_profile` / `adjust_strike_limit` 只能出現在 `[PROFILE_OVERRIDE]` 區塊內。
2. override 區塊在 envelope `}` 之後另起新行，不可嵌入欄位之間。
3. 標籤名稱**精確**為 `[PROFILE_OVERRIDE]` / `[/PROFILE_OVERRIDE]`、`[OBSERVATION_ADD]` / `[/OBSERVATION_ADD]`、`[RESUME_ACKNOWLEDGE]` / `[/RESUME_ACKNOWLEDGE]`，不可用近義詞。
4. 使用者明確要求 profile / sensitivity / strike-limit 變更時，envelope intent 為 `AnalyzeRequest`，提案放在 `[PROFILE_OVERRIDE]`。

### 6.1 `[PROFILE_OVERRIDE]`
```
[PROFILE_OVERRIDE]
{"intent":"adjust_sensitivity"|"switch_profile"|"adjust_strike_limit","target_field":"<field>","proposed_value":<v>,"rationale":"<reason>","expires_at_iso":"<UTC ISO8601>"}
[/PROFILE_OVERRIDE]
```
- 每則回覆最多 1 個。僅在使用者明確要求且 habituation 歷史支持時使用。
- `expires_at_iso` = 今天 + 1 天的 ISO 8601 UTC（host 對超過 7 天者上限封頂）。
- `target_field` 必須是 snake_case 引擎欄位：
  - `adjust_sensitivity` → `personal_pressure_scale`，範圍 **[0.7, 1.5]**（高=更敏感，低=更放鬆）。
  - `switch_profile` → `profile`，值 ∈ `{REACTIVE, ADAPTING, RESILIENT}`（舊字串 SENSITIVE/TYPICAL 仍可相容解析）。
  - `adjust_strike_limit` → `severe_strike_limit`，範圍 **[1, 5]**。
- 若 `[habituation_state]` 顯示 `advisory=vestibular`，**不可**提出放鬆提案（host 會硬性拒絕並向使用者顯示安全訊息）。

### 6.2 `[OBSERVATION_ADD]`
```
[OBSERVATION_ADD]
<關於使用者的一行觀察，≤ 200 字，使用使用者的語言>
[/OBSERVATION_ADD]
```
- 慎用。僅在使用者分享了**跨 session 的有意義資訊**（如「我在週末症狀更嚴重」、「scrolling 30 分鐘後一定會 spike」）時使用，非單次 session 內的反應。每則回覆最多 2 個。

### 6.3 `[RESUME_ACKNOWLEDGE]`
```
[RESUME_ACKNOWLEDGE]
{"user_confirmed":true|false,"user_quote":"<verbatim user text>"}
[/RESUME_ACKNOWLEDGE]
```
- 僅在 `[habituation_state]` 顯示 `awaiting_user_confirm=true` 且使用者明確表達願意恢復（true）或不願（false）時使用。`user_quote` 為簡短逐字片段。

ANTI-RULES（反規則）：
- 不可在不適回報上無故輸出這些區塊——即時不適的致動器是 envelope 的四個 delta，不是 `[PROFILE_OVERRIDE]`。
- 不可在 `FollowupRequest` 回合輸出 `[PROFILE_OVERRIDE]`（先收集資訊）。
- 不可在可見回覆文字中提及這些區塊（它們是靜默機制）。

## 7. 標註規範
- 欄位定義：
  - `user_text`: 原始輸入。
  - `intent`: §2 的 6 個 envelope 值之一（profile 調整類請另記 `override_intent`）。
  - `severity`: int 1–5（非不適意圖用 1）。
  - `fov_delta`: int **[-30, 0]**。
  - `mask_depth_delta` / `grid_visibility_delta` / `feather_width_delta`: float **[-0.3, 0.3]**。
  - `language`: `zh-TW|zh-CN|en|mixed`。
  - `assistant_response`: 受控目標回覆（見 §9 句數 / 字數上限）。
- 標註判定優先序：
  1. 明確不適詞（暈、噁心、頭痛、眼痛、dizzy、nausea）且**已指名症狀** ⇒ `ReportDiscomfort`，依強度給四個 delta。
  2. 不適但**未指名**（「怪怪的」「feel off」）⇒ `FollowupRequest`，附 2–4 選項。
  3. 明確恢復詞（好多了、ok now、better）⇒ `ReportComfort`，零 delta。
  4. 詢問過去 session / 原因 ⇒ `AnalyzeRequest`，零 delta。
  5. 明確要求復原 / 重設 ⇒ `ResetRequest`，零 delta。
  6. 明確要求調 sensitivity / profile / strike limit ⇒ envelope `AnalyzeRequest` + `[PROFILE_OVERRIDE]`。
  7. 其餘（問候、離題）⇒ `Unknown`，零 delta。
  8. 若同時出現且語意為「曾經不適但現在改善」，標為 `ReportComfort`。

## 8. 無模型 lexicon 回退（對齊用）

當 GGUF 未載入、HTTP 失敗或 envelope 解析失敗時，host 退回 `FeedbackLexicon`。
訓練資料的 delta 量值應與此 severity-bucket 邏輯**同向**，避免模型與回退路徑行為相左：

| severity | `fov_delta` | `mask_depth_delta` | `grid_visibility_delta` |
|----------|-------------|--------------------|--------------------------|
| 1 | -6 | 0.05 | 0.03 |
| 2 | -10 | 0.08 | 0.03 |
| 3 | -15 | 0.12 | 0.06 |
| 4 | -22 | 0.18 | 0.06 |
| 5 | -30 | 0.25 | 0.06 |

（`feather_width_delta` 不由 severity 推導，僅由明確的邊緣抱怨觸發；方向性線索如「太亮 / darker」會覆寫 mask-depth 預設。）

## 9. 回覆風格（REPLY STYLE）
- 輸出：單行 compact JSON，無 markdown fence，JSON 外無評論。
- 長度：
  - `ReportDiscomfort` / `ReportComfort` / `ResetRequest`：≤ 1 句、≤ 30 字（words）。
  - `AnalyzeRequest` / `Unknown` / `FollowupRequest`：≤ 2 句、≤ 55 字（words）。
- 語氣：溫暖簡潔，用使用者語言（「我已稍微收緊視野」），不要露出內部標籤（如「fov_delta=-7」）。
- 語言：C# 層在每個 user turn 釘選回覆語言（Han 字元偵測 → 繁中，否則 → 美式英文）。**絕對遵守**該指示，不可混用語言、不可用簡體中文、不可輸出假名 / 諺文。

## 10. 資料集需求
- 語言比例：繁中 40% / 英文 40% / 簡中 + 中英混雜 20%。
- 至少樣本量（建議，依 7 意圖分佈）：
  - `ReportDiscomfort`：3,000+（涵蓋 severity 1–5、四個 delta 的多樣組合）。
  - `FollowupRequest`：1,500+（未指名症狀）。
  - `ReportComfort`：2,000+。
  - `AnalyzeRequest`：1,500+（含引用 `[RecentAdjustments]` / `[RecentEvents]` 數值）。
  - `ResetRequest`：800+。
  - `Unknown`：2,000+。
  - PROFILE_OVERRIDE 三族（adjust_sensitivity / switch_profile / adjust_strike_limit）合計：1,500+。
  - 總量：12,000~16,000 條。
- 難例覆蓋：
  - 否定句：`I am not dizzy`、`不暈了`（應 → `ReportComfort`）。
  - 已指名 vs 未指名：`I have a headache`（→ ReportDiscomfort）vs `I feel off`（→ FollowupRequest）。
  - 反諷 / 口語：`kinda sick`、`快吐了`。
  - 混語：`今天很 dizzy 但比昨天好`。
  - override 邊界：把 `adjust_sensitivity` 誤放進 envelope intent 的**負例**，以及正確放進 `[PROFILE_OVERRIDE]` 的正例。

## 11. 模型訓練建議
- 建議流程：
  1. SFT（監督微調）先做意圖 + JSON 契約 + override 區塊格式對齊。
  2. DPO/ORPO（可選）強化「系統相關」偏好與「delta 量值鏡射強度詞」。
- 重要超參（交由訓練團隊調校）：
  - context length：2k~4k（注意系統提示本身約 2.1 KB tokens，生產 `--ctx-size 8192`）。
  - epoch：2~4（避免過擬合）。
  - learning rate：低學習率起步。
  - 保留 bilingual batch 混合。
- 資料切分：train/valid/test = 8/1/1；test 必須包含否定句、mixed language、PROFILE_OVERRIDE 三族。

## 12. 驗收指標（必達）
- Intent 分類（7 意圖）：
  - macro-F1 ≥ 0.92。
  - `ReportDiscomfort` recall ≥ 0.95（優先避免漏判）。
  - PROFILE_OVERRIDE intent 不得洩漏到 envelope `intent`（洩漏率 = 0%）。
- Delta 合規：
  - `fov_delta` ∈ [-30, 0]、三個 shader delta ∈ [-0.3, 0.3] 的比例 ≥ 99.5%（host 仍會夾值，但訓練不應依賴夾值）。
  - 非致動意圖（Analyze / Followup / Reset / Comfort / Unknown）四個 delta 全為 0 的比例 ≥ 99%。
- 回覆合規：
  - 單行 JSON、可解析比例 ≥ 99%。
  - 句數 / 字數符合 §9：≥ 99%。
  - 含系統動作關鍵詞（FOV / mask / grid / protection / monitoring）：ReportDiscomfort 回覆 ≥ 99%。
  - 離題率（人工抽查）≤ 2%。
- 語言一致性：與使用者語言一致 ≥ 98%。

## 13. 安全與法遵
- 禁止輸出：醫療診斷結論、藥物劑量建議、與產品無關長篇建議。
- 必備聲明（可短句）：僅提供視覺舒適度輔助，不替代醫療判斷。
- 自我傷害 / 嚴重困擾文字由 host 的 `SafetyFloor` 攔截，**永不**送入模型；訓練資料不應包含此類正樣本。

## 14. 上線前回歸清單（對本專案）
- 對照檔案：
  - `src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs`（系統提示、JSON 契約、override 解析點）。
  - `src/SenseEase.Infrastructure/Nlp/FeedbackLexicon.cs`（lexicon 回退）。
  - `src/SenseEase.Core/Models/NluResult.cs`（解析後欄位）。
  - `src/SenseEase.App/ViewModels/ChatbotViewModel.cs`（套用 delta + override 區塊處理）。
- 測試案例至少 42 條：
  - EN 與 ZH-TW 各覆蓋 6 個 envelope 意圖（各 3 條）。
  - mixed + negation 共 5。
  - PROFILE_OVERRIDE 三族各 1（含 advisory=vestibular 拒絕案例）。
- 驗證項目：
  - `result.Intent` 正確（6 envelope 值之一）。
  - `result.FovDelta` ∈ [-30, 0]、三個 shader delta ∈ [-0.3, 0.3] 且符合 severity 規則。
  - 非致動意圖四個 delta 為 0。
  - `[PROFILE_OVERRIDE]` 能被 host 解析（intent / target_field / proposed_value / expires_at_iso 齊全）。
  - 回覆不離題且有系統動作語意。

## 15. 交付物清單（給其他部門）
- `dataset_v1.jsonl`（已標註，含 7 意圖 + override 區塊正負例）。
- `labeling_guideline_v1.pdf`（可由本文件轉製）。
- `training_config_v1.yaml`。
- `eval_report_v1.pdf`（含 7×7 confusion matrix、delta 範圍違規率、override 洩漏率、失敗樣例）。
- `release_note_v1.md`（與目前 app 版本相容性）。

## 16. 建議時程
- D1~D3：資料整理與標註校準（含 override 區塊與 7 意圖平衡）。
- D4~D6：SFT 訓練與初次評估。
- D7：回歸測試與模型封版。
