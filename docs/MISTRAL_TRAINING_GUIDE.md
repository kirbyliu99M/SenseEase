# SenseEase Mistral 訓練說明書（交付版）

## 1. 目的與範圍
- 目標模型：本地 Mistral（GGUF 推論），用於 SenseEase AI Assistant。
- 任務目標：
  - 正確判定使用者意圖：`ReportDiscomfort` / `ReportComfort` / `Unknown`
  - 輸出必須與系統功能綁定：`OFI / Pressure / FOV / Mask`
  - 中英雙語穩定回覆，避免泛用健康建議。
- 非目標：
  - 不做醫療診斷
  - 不回答與 SenseEase 無關領域（金融、法律、一般閒聊延展）。

## 2. 目前系統行為（需對齊）
- `ReportDiscomfort`：縮小 FOV 清晰區、加強遮罩（保護收緊）。
- `ReportComfort`：在監測下逐步放寬保護。
- `Unknown`：要求使用者補充是否暈眩/噁心/頭痛/眼睛不適。
- 回覆限制：
  - 最多 2 句短句
  - 跟隨使用者語言
  - 必須提到系統動作（FOV/mask/protection/monitoring）。

## 3. 資料集需求
- 語言比例：
  - 繁中 40%
  - 英文 40%
  - 簡中 + 中英混雜 20%
- 至少樣本量（建議）：
  - `ReportDiscomfort`：3,000+
  - `ReportComfort`：2,000+
  - `Unknown`：2,000+
  - 總量：7,000~12,000 條
- 難例覆蓋：
  - 否定句：`I am not dizzy`, `不暈了`
  - 反諷/口語：`kinda sick`, `快吐了`
  - 模糊回報：`有點怪怪的`
  - 混語：`今天很 dizzy 但比昨天好`

## 4. 標註規範
- 欄位定義：
  - `user_text`: 原始輸入
  - `intent`: `ReportDiscomfort|ReportComfort|Unknown`
  - `fov_delta`: `-15|0`（目前版本）
  - `language`: `zh-TW|zh-CN|en|mixed`
  - `assistant_response`: 受控目標回覆（2句內）
- 標註判定優先序：
  1. 明確不適詞（暈、噁心、頭痛、眼痛、dizzy、nausea）=> `ReportDiscomfort`
  2. 明確恢復詞（好多了、ok now、better）=> `ReportComfort`
  3. 無法確認 => `Unknown`
  4. 若同時出現且語意為「曾經不適但現在改善」，標為 `ReportComfort`

## 5. 訓練資料格式（JSONL）
每行一筆：
```json
{"messages":[{"role":"system","content":"You are SenseEase AI..."},{"role":"user","content":"I feel dizzy watching FPV video"},{"role":"assistant","content":"Discomfort detected. I tightened the FOV protection mask to reduce peripheral visual load. I will keep monitoring OFI and pressure."}],"meta":{"intent":"ReportDiscomfort","fov_delta":-15,"language":"en"}}
```

繁中例：
```json
{"messages":[{"role":"system","content":"你是 SenseEase AI..."},{"role":"user","content":"我有點暈想吐"},{"role":"assistant","content":"已偵測到不適，我已收緊 FOV 保護與遮罩，降低周邊視覺刺激。系統會持續監測 OFI 與壓力變化。"}],"meta":{"intent":"ReportDiscomfort","fov_delta":-15,"language":"zh-TW"}}
```

## 6. 模型訓練建議
- 建議流程：
  1. SFT（監督微調）先做意圖+回覆格式對齊
  2. DPO/ORPO（可選）強化「系統相關」偏好
- 重要超參（交由訓練團隊調校）：
  - context length: 2k~4k
  - epoch: 2~4（避免過擬合）
  - learning rate: 低學習率起步
  - 保留 bilingual batch 混合
- 資料切分：
  - train/valid/test = 8/1/1
  - test 必須包含否定句與 mixed language。

## 7. 驗收指標（必達）
- Intent 分類：
  - macro-F1 >= 0.92
  - `ReportDiscomfort` recall >= 0.95（優先避免漏判）
- 回覆合規：
  - 句數 <= 2：>= 99%
  - 含系統動作關鍵詞：>= 99%
  - 離題率（人工抽查）<= 2%
- 語言一致性：
  - 與使用者語言一致 >= 98%

## 8. 安全與法遵
- 禁止輸出：
  - 醫療診斷結論
  - 藥物劑量建議
  - 與產品無關長篇建議
- 必備聲明（可短句）：
  - 僅提供視覺舒適度輔助，不替代醫療判斷。

## 9. 上線前回歸清單（對本專案）
- 對照檔案：
  - `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs`
  - `src/SenseEase.Infrastructure/Nlp/FeedbackLexicon.cs`
  - `src/SenseEase.App/ViewModels/ChatbotViewModel.cs`
- 測試案例至少 30 條：
  - EN discomfort / comfort / unknown 各 5
  - ZH-TW discomfort / comfort / unknown 各 5
  - mixed + negation 共 5
- 驗證項目：
  - `result.Intent` 正確
  - `result.FovDelta` 符合規則
  - 回覆不離題且有系統動作語意

## 10. 交付物清單（給其他部門）
- `dataset_v1.jsonl`（已標註）
- `labeling_guideline_v1.pdf`（可由本文件轉製）
- `training_config_v1.yaml`
- `eval_report_v1.pdf`（含 confusion matrix、失敗樣例）
- `release_note_v1.md`（與目前 app 版本相容性）

## 11. 建議時程
- D1~D3：資料整理與標註校準
- D4~D6：SFT 訓練與初次評估
- D7：回歸測試與模型封版

