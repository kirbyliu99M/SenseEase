# SenseEase Windows App — Architecture (Phase 1)

## 目標

驗證核心迴圈，而非完整產品：

```
Sensing → Inference → Rendering → Feedback
```

第一階段交付一個 **可 build / 可 run / 可測試 / 可擴充** 的 WPF 骨架。

## 專案目錄樹

```
SenseEase.Windows.sln
src/
  SenseEase.App/             WPF 桌面 App（net8.0-windows）
    App.xaml(.cs)            應用程式進入點
    MainWindow.xaml(.cs)     Dashboard 視窗 + overlay 生命週期管理
    Views/OverlayWindow.*    透明、topmost、click-through overlay prototype
    ViewModels/              MainViewModel（MVVM）、RelayCommand
    Interop/ClickThrough.cs  Win32 interop：WS_EX_TRANSPARENT click-through
  SenseEase.Core/            純抽象與模型（net8.0，不依賴任何專案）
    Models/                  SenseState、EventLogEntry、NluResult
    Abstractions/            五個服務 interface
    Inference/               LeakyIntegrator（真實 Phase-1 邏輯）
    Configuration/           SenseDefaults（草稿 §5 參數）
  SenseEase.Infrastructure/  mock / stub 實作（net8.0，依賴 Core）
    Mock/                    MockSensingProvider、MockInferenceEngine、MockNluService
    Storage/                 InMemoryEventLogStore
tests/
  SenseEase.Tests/           xUnit 單元測試
docs/                        架構 / 建置 / 除錯文件
samples/                     demo JSON 資料
```

## 模組邊界

| Module | Responsibility | Inputs | Outputs | Must not depend on |
|---|---|---|---|---|
| Sensing | 取得 OFI / 輸入事件（Phase 1：mock slider） | slider 值、cursor | `OpticalFlowSample` | Inference, Rendering, Feedback |
| Inference | Leaky Integrator、Noise Gate、Driver/Passenger、Mask 門檻 | OFI、mode | `SenseState`（Pressure / MaskActive / FOV%） | Rendering, Feedback, App |
| Rendering | FOV 三區遮罩、靜止錨點網格、overlay 視窗 | `SenseState` | 螢幕上的遮罩 | Sensing, Inference, Feedback |
| Feedback | NLU / 不適回饋（Phase 1：keyword matching） | 使用者文字 | `NluResult` | Rendering |
| Diagnostics | Event log、狀態追蹤 | 各模組事件 | `EventLogEntry` 列表 | App |
| Infrastructure | mock 實作、儲存 | 各 interface | 具體服務實例 | App |

**依賴方向**：`App → Infrastructure → Core`、`App → Core`、`Tests → Core (+Infrastructure)`。
`Core` 不依賴任何專案，僅含抽象與模型。

## Interfaces（`SenseEase.Core/Abstractions`）

| Interface | 用途 | Phase-1 實作 |
|---|---|---|
| `ISensingProvider` | OFI 取樣來源 | `MockSensingProvider`（slider 餵值） |
| `IInferenceEngine` | 壓力推論 | `MockInferenceEngine`（包真實 LeakyIntegrator） |
| `IOverlayRenderer` | FOV 遮罩渲染 | `OverlayWindow` |
| `INluService` | 回饋語意理解 | `MockNluService`（關鍵字比對） |
| `IEventLogStore` | 事件日誌儲存 | `InMemoryEventLogStore`（上限 20） |

## 保留給未來階段（Phase 1 僅 interface / stub）

WGC capture、DirectX 12 renderer、HP Look to Move SDK、HP IQ API、
Mistral 7B 推論、NPU / OpenVINO、OpenCV / Pixel-Diff 光流。

## 核心迴圈資料流

1. 使用者拖動 OFI slider → `MainViewModel.OpticalFlowIndex` → `MockSensingProvider.Push`。
2. `OpticalFlowSampled` 事件 → `MockInferenceEngine.Update(ofi, mode)`。
3. `LeakyIntegrator` 累積 Pressure（Noise Gate + 二次項 + 衰減）。
4. Pressure ≥ `TriggerPressure (100)` 且通過 cooldown → `MaskActive = true`、FOV shrink。
5. `StateChanged` → `OverlayWindow.ApplyState` 更新 FOV 遮罩與網格。
6. 狀態變化寫入 `EventLog`，顯示於 Dashboard。
