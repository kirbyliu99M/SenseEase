# SenseEase — 自動適配層規格 (Adaptive Fit Layer)

> **審查狀態 (2026-06-06):** 本檔為使用者提供之 v1 設計草案,已收進 repo 作為 SSOT。
> 經 codebase-grounded 審查(6-agent),**現行程式碼不支援本規格的三項基礎假設**,
> 故本檔標為 **DRAFT — 待 P0 rebase**。對應審查結論與分階段計劃見
> [`Alpha_1_MASTERPLAN.md` §13](Alpha_1_MASTERPLAN.md)。三項需先拍板的基礎落差:
> 1. **12 檔強度梯 (SetLevel/ResolveLevel/ResetShaderToLevelDefault) 不存在** —— 現行引擎為
>    `VimsTier` 1–10 + 3-band 舒適模型。本層須 rebase 至 VimsTier+3-band,或另建 12 檔子系統 + tier↔level mapper。
> 2. **50 組格點 deterministic selector 不存在**(淨新元件;`HabituationTrajectoryBuilder` 是唯讀敘述、
>    `HabituationOverrideProcessor` 是安全驗證器,皆非評分器)。
> 3. **M1(MAD→OFI 實測校準)未完成,且依現行 MasterPlan 屬 Alpha-2** —— 本層之「硬前置」與專案里程碑順序衝突。
> 另:`停用` 全關(含遮罩)與 `HEAD-TRACKING-PRIVACY §3.4`「前庭防護不可關」矛盾,須先解。
>
> 以下為使用者原文(未改動),作為 rebase 的依據。

---

**版本:** v1 — 2026-06-05
**狀態:** 規格草案(Q1–Q7 + 後續拍板均已收斂;待 coding agent 認領實作)
**依據:** `docs/TECHNICAL_WHITEPAPER.md`(Pre-Alpha v0.11.1)、`SenseEase-Intensity-Ladder-12-Levels.md`(12 檔 SSOT)、`SenseEase-Implementation-Improvement-Plan.md`(WS-P/M/R/G)
**一句話定位:** 把 **50 格點 → 12 檔 → 引擎參數 → 執行期讀數 → 5 級動態感**串成**一把共用的尺**(統一度量衡);AI 在格點上選最適並提案、UI 顯示 5 級並以 `+/-` 調檔、引擎執行參數——三者講同一種單位。自動適配**只在強度軸**運作,習慣化獨立,**使用者手動握把為定案後權威**。

> **本層不是新功能,是收斂。** 它疊在 12 檔手動之上:引擎用累積資料對一個策展配置庫評分、選最適、悄悄套用(鄰近)或經 chatbot 提案(跨段);使用者手動 `+/-` 或對話微調隨時覆寫一切。
>
> **硬前置:** 本層建立在校準過的 OFI 之上。**未完成 WS-M / M1(MAD→OFI 實測校準)前不得上線**——否則是拿未校準的尺去選最適,garbage-in。

---

## 0. 拍板紀錄(2026-06-05)

| # | 議題 | 決議 |
|---|---|---|
| Q1 | 定型強度 | **軟定型**:選定 active set,可被後續提案換掉(換組過 cooldown) |
| Q2 | cooldown 觸發 | **雙觸發**:使用者手動調整**重置**計時 + 換組後**進入**冷卻 |
| Q3 | 換組是否通知 | **不通知**(沉浸優先);鄰近換組悄悄做 |
| Q4 | 換組後檔位 | **對齊體感最近的檔**(覆寫原「跳中間值」,以使 Q3 沉浸成立) |
| Q5 | 換組是否清 chatbot delta | **是**,hard-set 清除,與換檔一致 |
| Q6 | 50 組結構 | **結構化格點**,只往鄰近組漸進移動 |
| Q7 | 習慣化是否進自動適配 | **否**。習慣化 profile 為獨立療程,照現行 3 尺度;自動適配只管強度軸 |
| 拍板-1 | 5 級動態感 | = **防護強度標籤**(對映 12 檔),跨級距才換,**會連動** |
| 拍板-2 | 「停用」語意 | **完全停掉、乾淨**:僅保留 APP 開啟,機能全停、資料暫停蒐集(連前庭防護也關;配套見 §7) |
| 靜默/提案分界 | — | **鄰近移動=悄悄做;跨段=chatbot 提案** |
| 停用資料 | — | **暫停蒐集、不歸零**;恢復後續算,回到停用前 active set 與檔位 |

---

## 1. 統一度量衡:一把尺的五個粒度

```
[最細] 50 組格點          模型工作集（自動適配在此移動，使用者不可見）
          │  每組 = 一條完整 12 檔曲線
[人控] 12 檔位            人的工作集（+/- 可調，chatbot 可微調）
          │  每檔 = 一組 Whitepaper 參數
[引擎] scale / 遮罩 / FOV / 網格 / 常駐微暗   執行參數
          │  執行期累積
[狀態] 壓力 P(t) / EffectiveTrigger / Tier / 顯示分數   同一把尺的讀數
          │  粗粒度對外標籤
[對外] 5 級動態感         極高 / 高 / 中 / 低 / 極低（使用者看到的唯一強度語言）
```

**向下貫穿**:模型在 50 格點選 → 落成 12 檔 → 展開引擎參數 → 執行期讀數 → 5 級動態感呈現。

### 1.1 12 檔 → 5 級動態感映射(拍板-1)

| 動態感(對外) | 對映檔位 | 語意 |
|---|---|---|
| 極高 | L1–L2 | 幾乎不介入,畫面最自然 |
| 高 | L3–L4 | 輕介入 |
| 中 | L5–L7 | 標準(預設 L5 落此) |
| 低 | L8–L10 | 強穩定 |
| 極低 | L11–L12 | 最大穩定,常駐遮罩 |

`+/-` 調的是底層 12 檔;**標籤只在跨越級距時改變**。

---

## 2. 50 組格點庫(強度軸,不含習慣化)

50 組沿兩軸張開:**強度帶 Intensity ~10**(對映 12 檔 scale/峰值遮罩/FOV 地板/網格)× **起落形狀 Response shape ~5**(漏積分器衰減 / pre-post-trigger 斜率)。**鄰近** = 曼哈頓距離 1;自動換組每次最多移 1 格;跨多格拆成多次或改提案。set #0 = 標準版(強度帶中位 × 起落形狀中位)。實際參數矩陣由 coding agent 從 12 檔 SSOT 推導、**待 M1 校準後**以實測錨定。

---

## 3. Deterministic 選擇器(第一版,非 LLM)

**引擎算、模型講、使用者拍。** ① 引擎蒐集統計(壓力分布 / spike 再發率 / calm 比例 / FMS) → ② 選擇器對「當前組相鄰格」逐一評分取 argmax(+ dwell hysteresis) → ③ 跨段才經 chatbot 敘述+提案 → ④ 使用者拍板 or 鄰近悄悄套用。
評分骨架:`score = w1·fit(壓力,強度帶) + w2·fit(spike,起落) + w3·fit(calm,強度帶) − w4·dist − w5·近期手動衝突`;候選 = 相鄰格;最高分未顯著超過維持現狀 → 不動(信心門檻,防過擬合;先在粗強度帶上有信心才動)。權重進 `SenseDefaults`(F1/F2 治理)。

---

## 4. 軟定型 + 雙觸發 cooldown 狀態機(Q1/Q2)

`Default(set#0, L5) → Settling(蒐集中) → Active(已定型) ⇄ Cooldown`。手動調整**重置**計時;換組後**進入** cooldown;拒絕一次提案 → cooldown **加倍**。軟定型:cooldown 過後可再評估、再鄰近移動。**不碰 `_offset`、寫入原子**(C5/H16)。時長進 `SenseDefaults`,預設保守。

---

## 5. 與習慣化軸(軸 B)的關係(Q7)

習慣化 profile(RESILIENT/TYPICAL/SENSITIVE)為**獨立療程**,照現行 3 尺度;**自動適配完全不碰習慣化**(不換 cadence、不改 strike limit、不動 `_offset`)。習慣化指派 = 校準時靜態指定,事後手動切換。兩軸正交。

---

## 6. 靜默換組 vs 提案換組(Q3/Q4/Q5)

**鄰近、同段內 = 悄悄換**:不通知、自動套用;換組後**對齊體感最近的檔**(Q4 = 在新組曲線上找與當前實際強度(含 chatbot 微調合成值)最接近的檔,使前後連續)。**跨段 = chatbot 提案**(對話 + Card,拍板才套用)。**換組 = hard-set**:套用新組時 re-seed 並清掉 chatbot 累積 delta(Q5);提案統一在 chatbot,不在主 bar 另設面板。

---

## 7. 「停用」語意 + 安全配套(拍板-2)

**語意**:完全停掉、乾淨——僅保留 APP 開啟,所有機能停擺(遮罩/sidecar/chatbot/自動適配),資料暫停蒐集。**資料**:暫停蒐集、不歸零;恢復回到停用前 active set 與檔位。
**安全配套(預設採用)**:因完全停用會關閉前庭防護、覆寫 `HEAD-TRACKING-PRIVACY §3.4` 精神,三項配套必須同行:
1. **知情(一次性)**:前庭使用者首次按「停用」時明確告知「這會關閉你的暈動防護」;之後 no-nag。
2. **可逆且顯眼**:停用中顯示「防護已停用 · 點此恢復」狀態,不可埋藏。
3. **文件一致**:更新 `HEAD-TRACKING-PRIVACY §3.4` 與 `SAFETY-PROVISIONS`,載明「完全停用會關閉前庭防護,屬使用者主動知情之選擇」。

---

## 8. 主 bar 行為契約(給 coding agent 當約束,非畫面)

| bar 元件 | 對應行為 | 約束 |
|---|---|---|
| 狀態行 | 顯示 **5 級動態感 + 防護狀態**(例:「動態感:中 · 防護中」) | 中性、非症狀;**不得**顯示跳動精準壓力 %;避免「暈眩」等症狀詞 |
| `+` / `−` | 12 檔 ±1;跨級距才改 5 級標籤 | 換檔 = **hard-set 清 chatbot delta**;有微調時給一次性瞬時回饋 |
| 對話框 / send | chatbot **細部微調**(疊在當前檔基線) | 受遮罩 0.20 地板;前庭守門;換組提案也在此 |
| 「停用」 | §7 完全停用 | 前庭:首次告知 + 顯眼可逆狀態;非省電模式 |
| 「設定」 | 進階 / WS-U;**自動適配開關放這** | 不在 bar 上 |
| `X` / 復原(↶) | 關閉 / 復原上一步 | 復原應能撤回最近一次手動調整 |

---

## 9. 安全護欄(全層適用)

- **前庭使用者**:只能往更保護方向提案,不得提議放鬆(沿用 `HabituationOverrideApplicator` 三攔截);強度下限 **L9**。
- **遮罩地板 0.20**(G1);**不碰 `_offset`**(C5)、**寫入原子**(H16)。
- **proposer not actor**:跨段須確認;鄰近悄悄換僅限同段且過信心門檻。
- **隱私**:全本地、單一使用者、無 egress;**非 collaborative filtering**。
- **停用**:§7 配套。

---

## 10. 非醫療紅線(§6)

5 級動態感、提案文案**不得宣稱**診斷/治療/神經可塑性/最適化;停在「依使用體驗自動建議舒適設定」。「動態感」(描述螢幕)優於任何描述使用者身體狀態的詞。

---

## 11. 與既有元件 / backlog 的接點

`HabituationTrajectoryBuilder`(重用為統計輸入)、propose-confirm Card / `ChatbotActionLog`(重用)、`HabituationProfileParams.ResolveLevel(1..12)`(依賴 12 檔 SSOT)、`IInferenceEngine.SetLevel`(重用)、WS-M/M1 校準(硬前置)、C5/H16(依賴)、WS-G/G-TRAIN(連動)、`SenseDefaults` F1/F2(權重/時長/門檻)、`HEAD-TRACKING-PRIVACY §3.4`/`SAFETY-PROVISIONS`(須更新)。

> **審查註:** 上列「重用」多項與現行程式碼不符(ResolveLevel/SetLevel/F1-F2 不存在;TrajectoryBuilder 非統計管線)。詳見 MasterPlan §13。

---

## 12. 第一版範圍 / 後續

**第一版(deterministic)**:§3 評分 + §4 狀態機 + §6 靜默/提案 + §7 停用 + 護欄。**後續(gated)**:升級為學習式選擇,需標註資料 + 訓練管線,且 gated on 真有標註資料。

---

*本檔與 `Alpha_1_MASTERPLAN.md` §13(審查結論 + 分階段計劃)、`SenseEase-Intensity-Ladder-12-Levels.md`、`SenseEase-Implementation-Improvement-Plan.md`、`ALPHA_BACKLOG.md`、`ALPHA_SECURITY_BACKLOG.md` 共用同一 Alpha cut。*
