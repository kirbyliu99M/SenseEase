# SenseEase — Build & Run

## 環境需求

| 項目 | 需求 |
|---|---|
| OS | Windows 10 / 11 |
| .NET SDK | .NET 8 SDK（LTS）— `net8.0-windows` 需要 |
| IDE | Visual Studio 2022 17.8+（建議）或 VS Code + C# Dev Kit |
| Git | 選用（版本控制） |

## 選項 A：Visual Studio 2022（建議）

安裝時於 Visual Studio Installer 勾選 workloads：

1. **.NET desktop development** — 必要（含 WPF、.NET 8 SDK）
2. **Desktop development with C++** — 給未來 Win32 / WGC interop
3. **Windows 11 SDK (10.0.22621 以上)** — 隨上述 workload 附帶
4. （選用）Windows App SDK / WinUI 3 元件
5. （選用）Git for Windows

步驟：

1. 開啟 `SenseEase.Windows.sln`。
2. 將 `SenseEase.App` 設為啟動專案（預設已是）。
3. `Build > Build Solution`（Ctrl+Shift+B）。
4. `Debug > Start`（F5）執行。
5. `Test > Run All Tests` 執行 xUnit 測試。

## 選項 B：.NET SDK + CLI（不需完整 VS）

WPF 可由 .NET 8 SDK 直接建置：

```powershell
# 於 repo 根目錄
dotnet build SenseEase.Windows.sln
dotnet run --project src\SenseEase.App
dotnet test
```

## 環境檢查清單

- [ ] `dotnet --info` 顯示 .NET 8 SDK
- [ ] `dotnet --list-sdks` 列出 8.x
- [ ] Visual Studio 版本 17.8 以上（若用選項 A）
- [ ] Windows SDK 已安裝
- [ ] Git 可用（`git --version`）— 選用
- [ ] `dotnet restore` / NuGet restore 正常（測試專案需還原 xunit 套件）
- [ ] 可建立 WPF / Console test 專案

## Acceptance Criteria

1. `dotnet build SenseEase.Windows.sln` → 0 error。
2. `dotnet run --project src\SenseEase.App` → 顯示 MainWindow。
3. 勾選「顯示 Overlay」→ overlay 顯示網格 + FOV 遮罩，且滑鼠可穿透。
4. Dashboard 顯示 OFI / Pressure / Mode / FOV radius / Mask active。
5. `dotnet test` → xUnit 測試全數通過。
