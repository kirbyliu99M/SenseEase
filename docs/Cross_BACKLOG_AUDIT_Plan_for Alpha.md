# SenseEase — 實作改善計畫(Synthesis + Backlog 稽核整合)

**版本:** v1 — 2026-06-05
**作者依據:** 與 Kirby 的四維 UX×數據透明度分析、Chang–Hain 機制修正、習慣化×神經可塑性×留存查證、頭部追蹤學理檢視
**對照基準:** `docs/TECHNICAL_WHITEPAPER.md` (Pre-Alpha v0.11.1)、`docs/ALPHA_BACKLOG.md` (A–G, 18 real-gap)、`docs/ALPHA_SECURITY_BACKLOG.md` (H1–H53)、語料庫原始論文、外部臨床文獻

> **本文件的定位**:這不是取代既有兩份 backlog,而是補上它們的**盲區**——產品概念層、臨床機制層、留存層、引用誠信層。既有 backlog 是「程式對不對 / 安不安全」的稽核;本計畫是「設計成不成立 / 主張站不站得住」的稽核。兩者共用同一個 Alpha cut。
>
> **如何閱讀**:§1 是根因模型;§2 是對既有 backlog 的稽核(覆蓋、盲區、誤排序);§3 是新增工作流(WS-P/C/R/M/G/H),每項含路徑、步驟、依賴、驗證;§4 是新舊項目對照矩陣;§5 是排程;§6 是**非醫療宣稱紅線**(整個留存敘事的前提);§7 是驗證清單;附錄是引用更正表。
>
> **非醫療紅線(先讀)**:SenseEase 自我定位為「非醫療視覺舒適系統」。本計畫所有與「神經可塑性 / 治療 / 復健」相關的內容,**只能用於設計動機與留存機制的內部依據,對外不得宣稱療效**。詳見 §6。

---

## 1. 根因模型(Synthesis)

四維分析 + 後續修正,收斂成四條根因。理解根因可避免逐條修症狀。

| 根因 | 一句話 | 觸發的症狀 | 對應工作流 |
|---|---|---|---|
| **R1 個人化只接到一個窄縫** | `EffectiveTrigger` 只進習慣化的 spike/calm 判定,**未進** Tier 投影、遮罩遲滯、FOV 半徑、著色器(全跑原始 `TriggerPressure=85` / 全域 shader) | 個人化壓力指數名不副實;Profile/壓力/Tier 三者破碎;著色器全域非分檔 | **WS-P** |
| **R2 量測軸無經驗刻度且代理效度有疑慮** | `WgcMadNormFactor=4.0`、`CursorVelocityNormFactor=120.0`、`TriggerPressure=85`、`τ≈2.81s` 皆 inline 假設、無實測;且 MAD 對亮度變化敏感,而文獻證明亮度不致暈 | 「壓力指數」可信度無上限保證;對專家不可宣稱為量測 | **WS-M** |
| **R3 習慣化機制標籤與臨床機制不符,核心保護方向與治療相反** | 「習慣化」實為**感覺再加權/適應**;徑向變暗遮罩=**迴避**(臨床上要減少的行為);只有錨定格+offset 漸進放寬符合機制,且 offset 只放鬆不加碼 | 遮罩/網格層該分離;G1 理由寫反;留存主張的成立與否 | **WS-C** + **WS-R** |
| **R4 文件↔實作、訓練說明書↔生產、引用↔原文 多處不一致** | 治理/誠信缺口 | 訓練說明書過時(3 意圖 vs 生產 7 意圖);兩份文件互相矛盾;§14 引用標題/年份/作者錯誤 | **WS-G** |

**最高槓桿**:R1 一次解掉「個人化壓力指數、Profile/Tier 破碎、著色器分檔」三件事(WS-P)。**最嚴重**:R3,因為它決定你能不能對外講「神經可塑性 → 持續使用」(WS-C + WS-R)。

---

## 2. 既有 backlog 稽核

### 2.1 覆蓋地圖

**`ALPHA_BACKLOG.md`(A–G,18 項)** — 全部是「白皮書 vs 程式」對照產物:clamp 範圍、缺測試、常數遷移、一個 UX 缺口(G1)。
- 與本計畫**有交集**的只有兩項:**A7**(per-profile 著色器預設,Alpha-2)、**G1**(絕對設定 / `/grid off` slash 指令,Alpha-2)。
- **C5**(Alpha-1,修 `ResetPersonalization` 清掉 `_offset` 的 Q4.5 違規)是本計畫多項的前置依賴。
- 其餘(A1–A6、A8、B1–B7、C1–C4、C6–C7、D1–D3、E1–E4、F1–F2)與本計畫不衝突,屬正確性/治理基礎工。

**`ALPHA_SECURITY_BACKLOG.md`(H1–H53,Alpha-1 18 / Alpha-2 23)** — 全部資安/隱私。
- 與本計畫**最相關**:**H1–H4 + H8**(safety-floor 必須先於 profile-picker / lexicon / 持久化執行——臨床安全承諾的載重鏈)、**H16**(非原子寫入會重置數週習慣化)。
- **H16 放錯抽屜**:它被歸 state-integrity,但本質是**留存致命項**——崩潰中途存檔會抹掉你要拿來講神經可塑性的 `_offset`。應與 WS-R 同級。

### 2.2 三個必須處理的 backlog 內部問題(稽核發現)

**稽核發現 1 — G1 的臨床理由寫反了(必須修正框架)。**
`ALPHA_BACKLOG.md` G1 寫:*"Grid (Chang-Hain pattern) is additive visual aid, so `/grid off` is unconditionally safe"*。但語料庫原始論文(Chang & Hain 2020)與前庭復健文獻顯示:**靜止錨定(=網格)才是視覺性眩暈的治療成分**,變暗遮罩反而是該被管理的迴避行為。G1 的**機制**(absolute-set + `/grid off`)是對的、該保留;但它的**預設政策與定位**錯了。WS-C 必須把 G1 + A7 + 職能治療師輸入統一成:**網格 = 依體質預設的可選「漸進暴露/錨定層」;變暗遮罩 = 一般使用者的舒適層**。

**稽核發現 2 — A7 被低估為品質項,實為臨床層分離的前置依賴。**
A7(per-profile shader defaults)目前 Alpha-2。但「網格依體質預設」(WS-C)與 G1 的 `ResetShaderToProfileDefault()` 都依賴 A7 先落地。應將 A7 與 WS-C 綁定、一起提前。

**稽核發現 3 — 沒有任何 backlog 項目管「臨床主張的文件正確性」。**
`§2.5`/`§14`/`HABITUATION-PROFILE.md §1.2` 的 Chang–Hain 與 Pavlou 引用有實質錯誤(見附錄 A),`MISTRAL_TRAINING_GUIDE.md` 與生產系統脫節。這些對 ATCC 與專家背書是信用問題,卻在 backlog 盲區。WS-G 新增。

### 2.3 稽核結論

> 既有 backlog 把 SenseEase 當「已定義正確的產品,去修 bug 與補洞」。本計畫主張:**在修 bug 之前,先確認『個人化是否真的個人化(R1)』與『習慣化是否真的是習慣化(R3)』——否則 backlog 修得再乾淨,對外的兩個核心主張(個人化壓力指數、神經可塑性留存)仍站不住。**

---

## 3. 實作改善計畫(工作流)

每項格式:**問題 → 根因 → 檔案/路徑 → 步驟 → 依賴既有 backlog → 優先序 → 驗證**。
優先序沿用既有相位:`Alpha-1`(must-fix)/ `Alpha-2`(should-fix)/ `v1.0`。

---

### WS-P|把個人化穿透到使用者看得到的層(根因 R1)

#### P1 — 顯示層壓力指數正規化為個人門檻比值(低風險、最高槓桿)

**問題**:使用者看到的 Tier / band 與拿到的遮罩,全用原始 `TriggerPressure=85`;`EffectiveTrigger`(個人化門檻)只進習慣化的 spike/calm 判定(`HabituationEngine.cs:273, 288`)。SENSITIVE 使用者(scale 1.5、EffectiveTrigger≈56.7)的分數與保護時程,與 RESILIENT 完全相同。
**根因**:R1。
**為何選正規化而非改引擎**:白皮書 §6.5 把「mask 遲滯 / FOV / tier 投影用原始 85」標為 load-bearing(B6)——直接把 `EffectiveTrigger` 接進 tier 投影會改變所有 `scale≠1.0` 使用者的觸發行為,且缺收斂測試。**先在呈現層**解,風險最低、且立刻讓「個人化」對使用者可見。
**檔案/路徑**:
- `src/SenseEase.App/ViewModels/MainViewModel.cs`(新增一個 `DisplayStressPercent` 計算屬性 = `Pressure / EffectiveTrigger × 100`,夾 `[0,100]`;`EffectiveTrigger` 經引擎只讀 getter 取得)
- `src/SenseEase.Core/Inference/HabituationEngine.cs:161-179`(暴露 `EffectiveTrigger` 只讀屬性,若尚未公開)
- 儀表板/卡片顯示分數的 binding 點(`MainWindow.xaml` chart、`NotificationCardWindow`)
**步驟**:
1. 引擎暴露 `public double EffectiveTrigger { get; }` 只讀。
2. ViewModel 加 `DisplayStressPercent`:平時用 `EffectiveTrigger`;`TriggerThresholdOverride` 存在時(前庭分支,`CalibrationProcessor.cs:74`)改用該絕對值當分母。
3. 儀表板「壓力」與卡片標題的數值改綁 `DisplayStressPercent`;**Tier/band 的離散著色維持引擎原值不動**(避免動 load-bearing)。
4. 文件 §6.4 加註:displayed score 與 engine tier 用不同分母,理由即個人化呈現。
**依賴**:無(可獨立先做)。
**外部背書**:領域 SOTA(Shimada et al. 2023, IEEE Access)cross-subject 71% → individualized ~80%——個人化顯著提升嚴重度估計,正是這條路的證據。
**優先序**:**Alpha-1**(這是「個人化壓力指數」對外主張的最小可信實作)。
**驗證**:單元測試——同一 `Pressure`,scale=1.5 的 `DisplayStressPercent` 高於 scale=0.7;vestibular override 時用 50 當分母;Tier 著色不受影響。

#### P2 — (v1.0 候選)評估把 `EffectiveTrigger` 接進 tier 投影與遮罩觸發

**問題**:P1 只解呈現層;若要連「保護觸發時機」也個人化,需動引擎。
**根因**:R1(深層)。
**檔案/路徑**:`MockInferenceEngine.cs:909`(`UpdateVimsTier`)、`:243-257`(mask 遲滯)、`:855-874`(`ComputeFovRadiusPercent`)。
**步驟**:在補齊 D3(`LeakyIntegratorConvergenceTests`)與新增 tier-vs-trigger 收斂測試後,評估把分母由 `TriggerPressure` 換為 `EffectiveTrigger`;需 A/B 視覺驗證(UVHS)。
**依賴**:**D3**(Alpha-2,收斂測試先行);與 P1 的呈現層解法二擇一或併行。
**優先序**:**v1.0**(load-bearing,須完整測試與 UVHS)。
**驗證**:收斂測試 + 對 scale∈{0.7,1.0,1.5} 的 tier/mask 行為快照 + iGPU/dGPU 視覺驗證。

---

### WS-C|臨床機制正名 + 視覺層分離(根因 R3)

#### C-DOC — 把「習慣化」正名為「漸進暴露 / 感覺再加權」,修正 Chang–Hain 機制描述

**問題**:臨床上視覺性眩暈的恢復機制主要是**感覺再加權/適應**,VOR 習慣化作用不大(Lacour et al.);Chang & Hain 2020 描述的也是「校正視覺/前庭權重」=再加權。專案通篇叫「habituation」並把徑向變暗遮罩稱「Chang–Hain mask」,機制描述不準。
**根因**:R3 + R4。
**檔案/路徑**:
- `docs/TECHNICAL_WHITEPAPER.md §2.3, §2.5`(機制段)、`§14`(參考文獻,見附錄 A)
- `docs/HABITUATION-PROFILE.md §1.2`(證據基礎)
- 對外簡報(`A02__十頁企劃書`、決選簡報)用語
**步驟**:
1. 機制段改述:SenseEase 的長期機制(對較重子群)為「**graded exposure → sensory reweighting**」,並明確區分「**錨定**(stationary anchor,治療成分)」與「**周邊衰減**(comfort/avoidance,症狀緩解)」。
2. 「Chang–Hain mask」拆稱:**Chang–Hain anchor grid**(錨)+ **peripheral attenuation mask**(舒適層)。
3. 程式碼識別子可留 `Habituation*`(避免大改),但加 doc-comment 註明臨床對應詞為 sensory reweighting。
**依賴**:附錄 A 的引用更正一起做。
**優先序**:**Alpha-2**(信用/正確性;對外送審前必須)。
**驗證**:葉淨維(OT/感覺衝突專業)審閱機制段;確認用語與其專業一致。

#### C1 — 視覺兩層分離:錨定格成為依體質預設的可選層(整合 G1 + A7 + OT 輸入)

**問題**:目前網格(`ApplyGridIntensity`)由**離散 tier** 驅動、且全域 shader、無乾淨的使用者開關;變暗遮罩由**連續壓力**驅動。職能治療師建議「讓使用者決定要不要網格,較重者才需要」——這與臨床(錨=治療成分)一致。**backlog G1 已半建機制但框架寫反(稽核發現 1);A7 是前置依賴(稽核發現 2)。**
**根因**:R3。
**檔案/路徑**:
- `src/SenseEase.Core/Inference/HabituationProfile.cs:67-206`(新增 `ResolveDefaultsFor(profile)` → 不同體質的 shader 預設,即 A7)
- `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:19`(`_shader` 初始化)、`SetHabituationProfile`(改為依 profile re-seed,除非使用者已個人化)
- `src/SenseEase.App/Views/OverlayWindow.xaml.cs:666-695`(`ApplyGridIntensity` 入口加 gate:`GridVisibility==0` 時恆 0)
- `src/SenseEase.App/Configuration/UserPreferences.cs`(新增 grid 持久化開關,與 Toggle A/B 同路徑)
- `src/SenseEase.Core/Inference/ShaderCommandLexicon.cs`(G1 新增的 `/grid off`,框架改寫)
- `docs/POPUP-OVERLAY.md §4`(Toggle 矩陣補 grid 維度)
**步驟**:
1. **先落地 A7**(`ResolveDefaultsFor`):RESILIENT/TYPICAL 預設 `GridVisibility` 低或 0;SENSITIVE 預設較高;前庭診斷者預設開。
2. 把 G1 的 `/grid off` 與 strict-NL(`關掉格線`)保留為機制,但**改寫其 rationale**:網格非「裝飾」,是「漸進暴露/錨定層」;預設由體質決定,而非一律開。
3. UI 採「**依體質預設 + 輕推薦**」而非裸開關:沿用 `POPUP-OVERLAY §5` 的 no-nag 一次性提示模式。
4. 變暗遮罩維持為一般使用者的舒適層,**不**綁治療敘事。
**依賴**:**A7**(提前到本工作流)、**G1**(框架改寫,機制重用)、**C5**(`/reset shader` 不得碰 offset)。
**優先序**:**Alpha-2**。
**驗證**:測試——SENSITIVE 與 RESILIENT 的 grid 預設可分;`/grid off` 後 `GridVisibility=0` 且遮罩仍運作;前庭者預設開。OT 審閱分層定位。

#### C2 — 錨定格與遮罩的時間相位對齊(separation 的時間脫鉤面)

**問題**:遮罩半徑吃**連續壓力**(`ComputeFovRadiusPercent`),網格吃**離散遲滯 tier**(`tierFraction=(VimsTier-1)/9`),兩層在壓力上升時相位脫鉤(半徑先平滑收縮、網格延遲 200–500ms 才階梯浮現)。對開網格的較重子群更明顯。
**根因**:R3(視覺一致性面)。
**檔案/路徑**:`src/SenseEase.App/Views/OverlayWindow.xaml.cs:666-695`(`ApplyGridIntensity`)、`:77`(`MaskSmoothingFactor=0.25`)。
**步驟**:讓網格 opacity 採與遮罩顏色相同的 `MaskSmoothingFactor=0.25` 緩動,使網格浮現與半徑運動相位對齊;或評估網格改吃連續壓力(與半徑同訊號)。
**依賴**:C1 之後(網格分離確立後再調其動態)。
**優先序**:**Alpha-2**(C1 的後續)。
**驗證**:Demo Mode 視覺驗證——壓力 ramp 時半徑與網格同步起落,無「半徑先、網格後」的脫節。

---

### WS-R|留存 = 監督替身(神經可塑性的前提)

> **核心查證事實**:非監督的軟體式視覺去敏留存很差。Pavlou, Bronstein & Davies 2013 RCT:非監督組 55% 退出、監督組 10%。最接近 SenseEase 形態的研究(線上遊戲化視覺去敏,Nature Sci Rep 2024)退出率 45%。**SenseEase 本質是非監督桌面去敏——不解決監督缺口,神經可塑性不會發生、留存也不會。** AI Chatbot 的真正戰略價值在此。

#### R1c — Chatbot 由被動調參升級為主動「監督替身」

**問題**:現有 chatbot 是 reactive(使用者報不適才調),`HabituationTrajectoryBuilder` 產生「3 level changes, 1 spike」但只在使用者開口時被讀;`longitudinal_observations` 是靜默累積。沒有任何主動的進度回饋、暴露排程、或鼓勵——正是 55% 退出的成因。
**根因**:R3(留存面)。
**檔案/路徑**:
- `src/SenseEase.Core/Inference/HabituationTrajectoryBuilder.cs`(已有軌跡摘要,擴充為可主動觸發的里程碑事件)
- `src/SenseEase.Core/Inference/HabituationStateBlockBuilder.cs`(`[habituation_state]` block 加入「自上次以來的進展」供主動回饋)
- `src/SenseEase.App/ViewModels/ChatbotViewModel.cs`(新增「進展回饋」的主動訊息路徑,比照 picker 短路的前置攔截慣例 `:311`)
- `src/SenseEase.App/ViewModels/MainViewModel.cs`(里程碑事件 → 觸發主動鼓勵訊息)
**步驟**:
1. 定義「進展里程碑」(如:offset 達到體質 cap 的 25%/50%/75%、連續 N 天有暴露、一次成功的 escalation),由軌跡資料觸發。
2. 在里程碑時,chatbot 主動推一則簡短鼓勵 + 下一步建議(本地產生或受控 LLM,**不離題、不醫療**)。
3. 把暴露排程(每日短時)做成可選的溫和提醒,而非強制(避免答案疲勞,呼應 `CALIBRATION.md §4.2` 的 frequency-vs-quality 取捨)。
**依賴**:**C5**(別在 reset 抹掉進度)、**H16**(別在崩潰抹掉進度)、WS-C(escalation 屬錨定層)。
**非醫療紅線**:鼓勵文案不得宣稱治療/可塑性;見 §6。
**優先序**:**Alpha-2**(留存是 Alpha 用戶測試的核心指標)。
**驗證**:測試里程碑觸發邏輯;OT 審閱鼓勵文案的臨床適切性;Alpha 測試追蹤留存率對比基線。

#### R2c — escalation 要「加碼暴露」而非只「放鬆回 baseline」

**問題**:目前 `_offset` 只把清晰區從收緊**放寬回原位**(`HabituationEngine.cs:286-318`),從不把暴露推到使用者舒適線以上。Chang–Hain 的療效成分是「用錨定撐住、主動把前景推大」——只放鬆不加碼,不會驅動再加權。
**根因**:R3(機制面)。
**檔案/路徑**:`src/SenseEase.Core/Inference/HabituationEngine.cs:286-318`(relaxation 狀態機)、`HabituationProfile.cs:138-175`(`HabOffsetCapPp` 等)。
**步驟**:
1. 在**錨定層開啟**且使用者連續穩定時,允許 offset 漸進**超過** baseline(受體質 cap 與安全閘約束),即主動增加可見的(動態)前景暴露。
2. 維持 strike 退避:任何 spike 立即 halve/zero offset(現行 `:273-277` 保留)。
3. **僅對開啟錨定層(治療層)的使用者啟用**;純舒適層使用者不加碼。
**依賴**:WS-C(錨定層分離先確立)、R1c(監督替身在加碼時給安全把關)。
**非醫療紅線**:這是「設計與漸進暴露原則一致」,對外**不得**宣稱為治療。
**優先序**:**v1.0**(機制變更,須 UVHS 視覺驗證 + 安全審查)。
**驗證**:模擬測試 offset 可超 baseline 且 spike 立即退避;對前庭者預設不啟用加碼;UVHS。

#### R3c — 把 H16 提升為留存致命項(跨 backlog 協調)

**問題**:`ALPHA_SECURITY_BACKLOG.md` H16:習慣化存檔非原子,崩潰中途會重置數週 `_offset`。被歸 state-integrity,但它直接抹掉你要拿來講可塑性/留存的那個量。
**根因**:R3(留存面)+ 既有 H16。
**檔案/路徑**:`src/SenseEase.Infrastructure/Storage/`(`AtomicJsonWriter`、`FileHabituationProfileStore`,見 `HABITUATION-PROFILE.md §5.5`)。
**步驟**:採 H16 既定修法(`File.Replace` 原子寫入 + tmp 清理),但**與 WS-R 同級排程**,並補一條測試:模擬崩潰後 `_offset` 不歸零。
**依賴**:即 H16,僅調整優先順位與歸屬敘事。
**優先序**:**Alpha-1**(與 H16 同;留存資料完整性)。
**驗證**:`HabituationProfileStoreTests` 既有崩潰恢復測試 + 新增 offset 保存斷言。

---

### WS-M|量測效度與刻度(根因 R2)

#### M1 — 補 MAD/cursor→OFI 實測校準(把 D1 從測試缺口升為效度項)

**問題**:`WgcMadNormFactor=4.0`、`CursorVelocityNormFactor=120.0`、`TriggerPressure=85` 全是 inline 假設,`baseline/` 無任何 MAD→OFI 對映(D1)。對 ATCC/專家,這代表整條壓力指數縱軸無經驗刻度。
**根因**:R2。
**檔案/路徑**:`baseline/wgc-mad-calibration.csv`(新建)、`baseline/cursor-velocity-calibration.csv`(新建)、`src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:528, 765-771`、新測試 `WgcSensingProviderCalibrationTests`。
**步驟**:採 D1 既定步驟採集對映資料;**額外**:針對代表性內容類型(idle / 文字捲動 / 影片 dolly / UI carousel)記錄 MAD 分布,讓 `4.0`/`120`/`85` 從假設變成可引用的刻度。
**依賴**:即 D1,範圍擴大為「對外可引用的刻度」。
**優先序**:**Alpha-2**(但對送審/背書建議提前)。
**驗證**:CSV 提交 + 校準測試;白皮書 §2.2 的「idle≈0–1 / scroll≈15–40」由 inline-comment 升為有資料支撐。

#### M2 — 亮度混淆抑制(效度修正,文獻驅動)

**問題**:MAD 是逐幀亮度差,會對亮度漸變/閃爍反應;但 Shahal, Hemmerich & Hecht 2016(*Displays*)證明**亮度/對比不調節 VIMS**(中度刺激下)。故 OFI 會把不致暈的亮度變化算成壓力。
**根因**:R2(效度)。
**檔案/路徑**:`src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs`(MAD 計算管線,`ComputeMad` 附近)。
**步驟**:在 MAD 前加一個「全域亮度位移」估計(逐幀 mean-luma 差);當變化主要來自全域亮度位移(無空間結構性運動)時,扣除其貢獻,避免亮度漸變被當光流。保守實作,避免誤殺真實運動。
**依賴**:M1(用校準資料驗證抑制不傷真實運動偵測)。
**外部依據**:Shahal et al. 2016。
**優先序**:**Alpha-2**。
**驗證**:對「純亮度漸變(無運動)」素材,OFI 不再顯著上升;對「真實捲動/影片」OFI 不受影響。

---

### WS-G|治理與引用誠信(根因 R4)

#### G-CITE — 修正 §14 與相關文件的引用錯誤(見附錄 A)

**問題**:`§14` 與 `HABITUATION-PROFILE.md §1.2` 的 Chang–Hain、Pavlou、VIMSSQ-short 引用有實質錯誤(標題、年份、監督/非監督方向、作者歸屬)。對 ATCC 與專家審閱是信用問題。
**根因**:R4。
**檔案/路徑**:`docs/TECHNICAL_WHITEPAPER.md §14`、`docs/HABITUATION-PROFILE.md §1.2`、對外簡報參考文獻。
**步驟**:依附錄 A 逐條更正。
**優先序**:**Alpha-2**(送審/背書前必須)。
**驗證**:對照語料庫 PDF 與外部 DOI 逐條核對。

#### G-TRAIN — 訓練說明書對齊生產系統的 NLU 契約

**問題**:`MISTRAL_TRAINING_GUIDE.md` 規範 3 意圖(`ReportDiscomfort/ReportComfort/Unknown`)、`fov_delta:-15|0`;但生產系統有 7 意圖、`FovDelta[-30,0]`、3 個 shader delta、3 種結構化覆寫區塊(PROFILE_OVERRIDE / OBSERVATION_ADD / RESUME_ACKNOWLEDGE)。照說明書重訓練的模型**無法產生覆寫區塊、且 delta 範圍錯**——升級計畫錨在過時契約上。
**根因**:R4。
**檔案/路徑**:`docs/MISTRAL_TRAINING_GUIDE.md`;對照 `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs`(envelope/system prompt)、`src/SenseEase.Core/Inference/HabituationOverrideParser.cs`(區塊文法)、`FeedbackLexicon.cs:208-210`(delta 表)。
**步驟**:
1. 意圖清單、`FovDelta` 與 shader delta 範圍、三種結構化區塊文法,全面對齊生產。
2. 驗收指標補上「結構化區塊產生可靠度」(3B scale 的真正難處,見 `HABITUATION-CHATBOT-INTEGRATION §7` L9),不只 3 意圖的 macro-F1。
3. 補真模型 E2E 為升級前硬閘(目前只有腳本輸出)。
**依賴**:無。
**優先序**:**Alpha-2**(任何重訓練/升級前必須)。
**驗證**:訓練說明書與 `MistralChatbotService` envelope 逐欄一致;新增覆寫區塊產生率指標。

#### G-DOC — 修正文件互相矛盾與權責歸屬不一

**問題**:`CALIBRATION.md §4.3`(措辭暗示 `EffectiveTrigger` 全域生效)與 `WHITEPAPER §6.5`(範圍限定)矛盾;`QA-AND-TIERING §2.1`(Tier 由 HabituationEngine 維護)與白皮書(Tier 由 `MockInferenceEngine.UpdateVimsTier` 算後傳入)不一;`ARCHITECTURE.md`(已封存至 `docs/archive/ARCHITECTURE.md`,DEAD-13)仍寫 `TriggerPressure=100`(實為 85)、仍列「HP Look to Move SDK」(實為 MediaPipe);以 `docs/TECHNICAL_WHITEPAPER.md` 為準。
**根因**:R4。
**檔案/路徑**:`docs/CALIBRATION.md §4.3`、`docs/QA-AND-TIERING.md §2.1`、`docs/archive/ARCHITECTURE.md`(已封存)。
**步驟**:統一 `EffectiveTrigger` 範圍敘述(以 §6.5 為準);統一 Tier 權責(以白皮書為準);更新 ARCHITECTURE 的過時數值與 SDK 名稱。
**優先序**:**Alpha-2**。
**驗證**:三份文件交叉一致;`CalibrationConfidence` 順帶依 C4 決議(接消費者或標為診斷用)。

---

### WS-H|頭部追蹤穩健性(在 OBS-3 內;接受頭向為主)

> **定位**:頭追蹤是 UX/視窗對位(避免定點遮罩永遠卡中央),非療效機制,亦非注視追蹤。學理天花板「頭≠眼」為**已知且可接受的設計邊界**,文件誠實標示即可。
> **OBS-3 約束(硬限)**:不得 egress landmark/影像/blendshape;`output_face_blendshapes=True` 是 release blocker(`HEAD-TRACKING-PRIVACY §2.5`)。因此**不採眼動/blendshape 路徑**;任何新導出欄位必須通過 E3 schema allowlist 測試。

#### H-CAL — 觀看距離 → 頭姿勢增益校準(解距離瓶頸的學理根)

**問題**:單目無深度,「頭轉幾度 → 螢幕看哪裡」的增益正比於觀看距離;距離未知 → 增益未知。實測距離與多人為瓶頸。
**根因**:頭向粗對位的先天限制。
**檔案/路徑**:`tools/head-tracker-sidecar/head_tracker.py`(sidecar 內計算)、`src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs:636-666`(若新增導出欄位需更新 DTO + E3 測試)、Settings 校準入口。
**步驟**:
1. **每設定一次的「看四角」校準**:擬合頭姿勢→螢幕座標增益,把距離影響一次吸收進增益常數;順手鎖定 sign/scale(防 L16 那類正負號反轉)。
2. **IPD 當距離尺標**:在 **sidecar 內**用雙眼間距像素寬度估相對距離,**只用來修正 cx/cy 增益、不導出 landmark**;若要導出 `distance_norm` 純量,**必須**加入 E3 allowlist 並通過 schema 測試。
3. **有效距離帶 + 降級**:落在可靠區間外(信心低)時,緩回中心並**放寬清晰區**(低信心→少保護、少誤遮),用既有 `face_present` + 信心欄位驅動。
**依賴**:**E3**(schema allowlist 測試;任何新欄位前置)。
**優先序**:**Alpha-2**。
**驗證**:不同身高/距離使用者的對位精度;距離帶外的降級行為;E3 測試涵蓋任何新欄位。

#### H-MULTI — 多人情境的主使用者鎖定(本質是身分歸屬,非精度)

**問題**:多人入鏡時 FaceLandmarker 在多臉間跳;**跟錯人比不跟更糟**——清晰區跳到無關位置=未被使用者 efference copy 預測的周邊偽運動,在最不該加運動時加運動。
**根因**:頭向對位的多臉歧義。
**檔案/路徑**:`tools/head-tracker-sidecar/head_tracker.py`(主臉選取在 sidecar 內)、`SidecarHeadPoseReader.cs`(降級狀態)。
**步驟**:
1. **主臉策略**:最大臉(最近)+最居中為主使用者,選取加時間遲滯(已鎖定者需明顯更大/更近才換)。
2. **跳變抑制**:主臉位置單幀跳超過閾值 → 拒絕該幀、維持前一中心,不 snap。
3. **多臉即降級**:≥2 臉且無法穩定判定主使用者 → 退回中心(0.5,0.5)+放寬清晰區。
4. 把「鎖定主臉 / 多臉降級中」寫入診斷(比照 L16 用 CSV 紀律),設計時驗證而非現場才發現。
**依賴**:H-CAL 的降級機制共用。
**優先序**:**Alpha-2**。
**驗證**:多臉測試素材下不亂跳;降級回中心而非跟錯;診斷可觀察。

#### H-DAMP — (cx,cy) 位置阻尼 + 死區(吸收抖動偽運動)

**問題**:顏色有 `MaskSmoothingFactor=0.25` 臨界平滑,但 (cx,cy) 位置似乎缺等價阻尼+死區;追蹤抖動使遮罩邊界掃動,本身即周邊運動。
**根因**:頭向對位的訊號雜訊。
**檔案/路徑**:`SidecarHeadPoseReader.cs`(平滑)或 `OverlayWindow.xaml.cs`(消費端)。
**步驟**:對 (cx,cy) 加臨界阻尼 + deadband(小抖動不動邊界,大轉頭仍平滑跟上);並加**狀態相依 follow gain**——高 Tier/被誘發時降低或凍結中心移動(別在已誘發時加邊界運動)。
**依賴**:無。
**優先序**:**Alpha-2**。
**驗證**:抖動素材下邊界不掃動;高 Tier 時中心移動受抑制。

---

## 4. 新舊項目對照矩陣

| 新項目 | 關係 | 既有 backlog |
|---|---|---|
| P1 顯示層個人化分數 | 補盲區(無對應) | — |
| P2 引擎個人化觸發 | 依賴 | D3(收斂測試前置) |
| C-DOC 機制正名 | 補盲區 + 連動 | G-CITE / 附錄 A |
| C1 視覺兩層分離 | **整合 + 框架改寫** | **G1**(機制重用、rationale 改寫)、**A7**(前置依賴,提前)、C5(reset 不碰 offset) |
| C2 相位對齊 | 補盲區 | —(C1 後續) |
| R1c 監督替身 | 補盲區 | 依賴 C5、H16 |
| R2c escalation 加碼 | 補盲區 | 依賴 C1、R1c |
| R3c H16 升級 | **重新歸屬/排序** | **H16**(機制即既定修法) |
| M1 MAD 校準 | **範圍擴大** | **D1**(由測試缺口→效度項) |
| M2 亮度抑制 | 補盲區 | 依賴 M1 |
| G-CITE 引用更正 | 補盲區 | — |
| G-TRAIN 訓練對齊 | 補盲區 | — |
| G-DOC 文件矛盾 | 補盲區 | 連動 C4 |
| H-CAL/H-MULTI/H-DAMP 頭追蹤 | 補盲區 | 依賴 E3(schema 測試) |

---

## 5. 排程(與既有 Alpha-1/Alpha-2 交織)

**Alpha-1(安全/正確/留存資料完整性 — 對外送審與用戶測試前):**
- 既有資安載重鏈:**H1+H2+H3+H4+H8**(safety-floor 先於一切)
- 既有 **C5**(reset 不抹 offset)→ 同時是 C1/R1c/R2c 的前置
- **R3c**(= H16,留存資料原子寫入,提前)
- **P1**(顯示層個人化分數 — 讓「個人化壓力指數」主張可信的最小實作)

**Alpha-2(機制正名、分層、留存機制、效度、治理):**
- **A7 → C1 → C2**(視覺兩層分離,A7 提前到此鏈)
- **C-DOC + G-CITE + G-DOC + G-TRAIN**(信用/正確性,送審前批次完成)
- **R1c**(監督替身)
- **M1 → M2**(校準與亮度抑制)
- **H-CAL / H-MULTI / H-DAMP**(頭追蹤穩健性,依賴 E3)
- 既有 D1/D2/D3/A7/G1 等照常

**v1.0(load-bearing 變更,須 UVHS + 安全審查):**
- **P2**(引擎個人化觸發)
- **R2c**(escalation 加碼暴露)
- 既有 **A8**(多螢幕)

---

## 6. 非醫療宣稱紅線(整個留存敘事的前提)

SenseEase = 非醫療視覺舒適系統。神經可塑性/復健文獻**只能用於設計動機與留存機制的內部依據**。對外(ATCC、官網、UI、專家背書文案)必須遵守:

1. **不得宣稱**:治療、復健、驅動神經可塑性、改善前庭功能、減敏療效。
2. **可以說**:「依循漸進暴露(graded-exposure)原則設計」「以視覺舒適與症狀緩解為目標」,並保留既有的非醫療免責(safety-floor 的 disclaimer)。
3. **背書範圍對齊專業**:
   - **周邊衰減舒適層 / 視覺安全** → 眼科 陳韵臻(DES、眼睛安全、患者就醫路徑)。
   - **錨定/漸進暴露/感覺再加權層之機制合理性** → OT 葉淨維(習慣化/感覺衝突)。
   - 兩者不混淆:陳韵臻不背書前庭/神經機制,葉淨維不背書 DES 眼科宣稱。
4. **留存文案**(R1c 的鼓勵訊息)不得暗示治療效果;限於舒適與使用體驗。

---

## 7. 驗證清單(交付前)

- [ ] P1:同 Pressure 下 SENSITIVE 顯示分數 > RESILIENT;vestibular 用 50 當分母;Tier 著色不受影響。
- [ ] C1:SENSITIVE/RESILIENT grid 預設可分;`/grid off` 後 `GridVisibility=0` 且遮罩仍運作;前庭者預設開;OT 審閱分層定位。
- [ ] C2:壓力 ramp 時半徑與網格相位同步(Demo Mode 視覺)。
- [ ] R1c:里程碑觸發正確;鼓勵文案過 OT 臨床適切性 + §6 非醫療紅線。
- [ ] R3c/H16:模擬崩潰後 `_offset` 不歸零。
- [ ] M1:校準 CSV 提交;白皮書刻度由 inline-comment 升為有資料支撐。
- [ ] M2:純亮度漸變素材 OFI 不顯著上升;真實運動不受影響。
- [ ] G-CITE:§14 與 §1.2 引用對照 PDF/DOI 逐條正確(附錄 A)。
- [ ] G-TRAIN:訓練說明書與生產 envelope 逐欄一致;補覆寫區塊產生率指標。
- [ ] H-CAL/H-MULTI/H-DAMP:距離帶外降級、多臉回中心、抖動不掃邊界;E3 涵蓋任何新欄位。
- [ ] §6 紅線:對外所有文案(簡報、UI、背書)無治療/可塑性宣稱。

---

## 附錄 A — 引用更正表(G-CITE)

對照語料庫 PDF 與外部 DOI 查證結果:

| 位置 | 現況(錯誤) | 應更正為 | 證據 |
|---|---|---|---|
| `WHITEPAPER §14` #2 | "Chang, T.-P. & Hain, T.C. (2020). *Progressive Tolerance to Desensitization Protocol (TDP)...*" | **Chang, C.-P. & Hain, T.C. (2020). *Visual vertigo treatment through optokinetic stimulation with stationary anchoring.* Physiotherapy Research and Reports 4:1–4.** 第一作者為 Chih-Pei Chang(大氣科學);標題無「TDP」字樣;為 **4 頁理論提案、非臨床試驗** | 語料庫 PDF |
| `WHITEPAPER §14` #3 | "Pavlou, M. (2010). *...~55% dropout rate observed in supervised exposure regimens.*" | **Pavlou, M., Bronstein, A.M., & Davies, R.A. (2013). *Randomized Trial of Supervised Versus Unsupervised Optokinetic Exercise...* Neurorehabil Neural Repair 27(3):208–218.** 55% 為**非監督**組(監督組 10%);Pavlou 2010 是綜述(*J Neurol Phys Ther* 34(2):105-110) | 外部 DOI / 語料庫 |
| `WHITEPAPER §14` #4 | "Lukacova, I. et al. (2023, 2025). *VIMSSQ-short...*" | 語料庫的 norms 論文為 **Golding, J.F. & Keshavarz, B. (2025). *Norms and Correlations of the VIMSSQ-short.* Multisensory Research.** 若引用 Lukacova 需另附該文;勿張冠李戴 | 語料庫 PDF |
| `HABITUATION-PROFILE.md §1.2` | 重複上述 Chang & Hain「TDP」、Pavlou 2010 55%、VIMSSQ「Lukacova」 | 同上三項一併更正 | — |
| 機制描述(多處) | 「習慣化(habituation)」為主要機制 | 視覺性眩暈的主要機制為**感覺再加權/適應(sensory reweighting/adaptation)**;VOR 習慣化作用有限。用語改述或加註臨床對應詞 | 前庭復健共識文獻 |

---

*計畫結束。本文件補既有 backlog 的概念/臨床/留存/誠信盲區;與 ALPHA_BACKLOG(A–G)、ALPHA_SECURITY_BACKLOG(H1–H53)共用同一 Alpha cut。*
