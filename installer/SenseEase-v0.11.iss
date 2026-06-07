; =============================================================================
; SenseEase v0.11 Pre-Alpha Installer
; Inno Setup 6.x script
;
; Generated: 2026-05-31
; Target:    Windows 10 build 19041 (May 2020) or later, x64-compatible
; Scope:     Per-user install (no UAC elevation prompt)
; Payload:   ~3.0 GB dist tree (includes 2 GB GGUF model + Vulkan llama.cpp
;            backend + PyInstaller-bundled head-tracker sidecar with MediaPipe)
;
; -----------------------------------------------------------------------------
; MAINTAINER NOTES
; -----------------------------------------------------------------------------
; 1. AppId is LOCKED FOREVER. Never regenerate. Inno Setup uses AppId as the
;    sole upgrade-detection key; changing it produces a parallel install
;    instead of upgrading in place.
; 2. AppVersion / AppVerName / OutputBaseFilename change every release.
; 3. The user-data directory %LOCALAPPDATA%\SenseEase\ is OUT OF SCOPE for
;    this installer. It is created/managed by the running app at first launch
;    and persisted across reinstalls/uninstalls. Do NOT add it to [Dirs],
;    [Files], or [UninstallDelete].
; 4. Inno Setup uses {{ }} in source to emit a literal { } in the compiled
;    output -- that is why the AppId GUID is wrapped in {{...} below.
; 5. To produce the installer:
;      & "C:\Users\kirby\AppData\Local\Programs\Inno Setup 6\ISCC.exe" `
;          "c:\Users\kirby\Desktop\SenseEase APP\installer\SenseEase-v0.11.iss"
; =============================================================================

#define MyAppName        "SenseEase"
#define MyAppVersion     "0.11.1"
#define MyAppVerName     "SenseEase 0.11.1"
#define MyAppPublisher   "ATCC A02_SenseEase"
#define MyAppExeName     "SenseEase.App.exe"
#define MyOutputName     "SenseEase-Setup-v0.11.1"

; Repo-relative paths -- the .iss file lives in <repo>\installer\, so go up one.
#define RepoRoot         SourcePath + ".."
#define DistRoot         RepoRoot + "\dist\v0.11-pre-alpha"
#define IconSource       RepoRoot + "\src\SenseEase.App\Resources\AppIcon.ico"

[Setup]
; ----- Identity (DO NOT CHANGE AppId across releases) -----
AppId={{DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppVerName}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

; ----- Install layout: per-user, no elevation -----
DefaultDirName={localappdata}\Programs\SenseEase
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; ----- OS / arch gating (matches net8.0-windows10.0.19041.0 TFM) -----
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; ----- UI -----
WizardStyle=modern
SetupIconFile={#IconSource}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppVerName}

; ----- Compression (LZMA2 ultra64 for the 2.9 GB payload) -----
; Note: GGUF is already quantized (Q4_K_M) so compresses poorly; expect ~10-15% reduction.
; Solid compression boosts ratio for the many small framework DLLs at the cost of slower install.
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; ----- Output -----
OutputDir={#SourcePath}
OutputBaseFilename={#MyOutputName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut is opt-in but default-checked, matching modern installer convention.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Single recursive entry pulls the whole published dist tree into {app}.
; ignoreversion          -- always overwrite (we don't rely on FileVersion stamps)
; recursesubdirs         -- include models\, runtimes\, locale subfolders
; createallsubdirs       -- preserve directory structure for runtimes\win-x64\native\cuda12\ etc.
; Excludes (H28)         -- never package debug symbols, doc-comment XML, stray logs, or
;                           the .NET createdump diagnostic tool; they bloat the installer
;                           and ship nothing the runtime needs.
Source: "{#DistRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml,*.log,createdump.exe"

[Icons]
; Start-menu shortcut (always created).
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
; Desktop shortcut (Tasks-gated).
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
; Uninstall entry in the Start menu group (useful for testers who want to wipe the install).
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Offer to launch immediately after install. Unchecked-by-default via "nowait postinstall skipifsilent".
; postinstall  -- shows the "Launch SenseEase" checkbox on the Finished page
; nowait       -- don't block the installer on the app's process
; skipifsilent -- don't auto-launch during /SILENT or /VERYSILENT installs (CI / scripted)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; INTENTIONALLY EMPTY.
; The uninstaller removes only files/folders that Setup itself created under {app}.
; The runtime data directory %LOCALAPPDATA%\SenseEase\ (settings.json, logs,
; sidecar JSON from head-tracking, soak reports, telemetry baselines) is owned
; by the running app, not by this installer, and MUST persist across reinstalls.
; Do not list {localappdata}\SenseEase here under any circumstances.

[Code]
// Reserved for future logic (e.g., GPU detection, CUDA12 capability check,
// MediaPipe model integrity verification). Left empty for v0.11 Pre-Alpha.
