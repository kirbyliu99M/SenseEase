# SenseEase Installer — Tier 3 Plan: WiX Burn + custom WPF Bootstrapper

> Goal: replace the generic Inno wizard with an installer whose chrome **matches the
> SenseEase app** (Segoe UI, indigo accent, light/dark, rounded/flat) by building the
> installer UI in WPF/XAML on top of WiX **Burn**.
> Status: PLAN ONLY (2026-06-09). Current shipping installer = `installer/SenseEase-v0.11.iss` (Inno 6).

---

## 1. Why this (and the honest cost)

Inno's wizard is a fixed Win32/VCL form: you can rebrand images + recolor via `[Code]`, but
you can't get a fully themed, dark-mode-capable, app-matching UI. **Burn** is WiX's
bootstrapper engine; it lets you supply a **custom Bootstrapper Application (BA)** whose UI you
render yourself — in our case a **WPF .NET app**, so it can reuse the SenseEase theme tokens
and look identical to the product.

Cost: this is a packaging-stack change, not a tweak. Budget **~2–4 focused weeks** for one
engineer including parity, the 3 GB payload, signing, and Inno→WiX migration testing. The
managed-BA hosting + 3 GB build are the two biggest risk areas.

---

## 2. Target architecture

```
SenseEase_Setup.exe   ← Burn BUNDLE (single signed .exe)
  ├── Bootstrapper Application (BA)  = SenseEase.Installer.Ui (WPF, .NET 8)   ← our custom UI
  │       talks to the Burn engine via WixToolset.Mba.Core (Detect/Plan/Apply + events)
  └── Chain:
        └── SenseEase.msi   ← WiX MSI that lays down the ~3 GB dist tree (per-user)
            (optionally preceded by an ExePackage that removes a prior Inno install)
```

Two build outputs: **the MSI** (the real payload/logic) and **the bundle .exe** (engine + our
WPF UI + the MSI). The WPF BA is the *only* thing the user sees.

---

## 3. Decisions to lock before coding

| # | Decision | Recommendation |
|---|----------|----------------|
| D1 | WiX major version | **WiX v5** (current; .NET tool: `dotnet tool install --global wix`). Note: the *managed-BA hosting* model changed across v3→v4→v5 — pin v5 and follow its `<BootstrapperApplication>` .NET-host docs exactly. |
| D2 | Payload packaging | 2 GB GGUF compresses ~10–15% (already Q4_K_M). Author MSI with embedded cab (`MediaTemplate EmbedCab="yes"`), then in the bundle decide **`Compressed="yes"`** (single ~3 GB self-contained .exe — simplest for testers) vs **`Compressed="no"`** (small stub + external payload, better if you later host a download). Start with `yes`. |
| D3 | Scope/elevation | **Per-user, no elevation** to match Inno (`PrivilegesRequired=lowest`). MSI `Scope="perUser"`; bundle stays un-elevated when every package is per-user. The WPF BA runs un-elevated. |
| D4 | Install dir | Keep `{localappdata}\Programs\SenseEase` (MSI `INSTALLFOLDER` = `LocalAppDataFolder\Programs\SenseEase`). |
| D5 | Upgrade keys | New **Bundle UpgradeCode** + **MSI UpgradeCode** (lock both forever, like the Inno AppId). Document them next to the Inno AppId. |
| D6 | Inno→WiX migration | The new installer is a *different tech* → it will NOT see the old Inno install as an upgrade. Detect the Inno install and remove it first (see §7). **Must-have**, else users get a double install. |
| D7 | Signing | Sign BA dll + MSI + the bundle engine (WiX `burn` detach/re-attach signing). Expect SmartScreen reputation to reset on the new identity/filename. |

---

## 4. New projects / repo layout

```
installer/
  wix/
    SenseEase.Package.wixproj      # the MSI (WiX v5 SDK-style project)
    Package.wxs                    # Package/Files/Shortcuts/Registry/CAs
    Bundle.wixproj                 # the Burn bundle
    Bundle.wxs                     # <Bundle> + <BootstrapperApplication> + <Chain>
  ui/
    SenseEase.Installer.Ui.csproj  # WPF .NET 8 BA (references WixToolset.Mba.Core)
    App.xaml / MainView.xaml ...   # the themed installer UI
  SenseEase-v0.11.iss              # (kept until cutover)
```

Reuse from the app: copy/share `Theme/SenseEaseTheme.xaml` (or the relevant tokens) into the BA
so colors/fonts match exactly. Keep the BA dependency-light (it loads *before* the app exists).

---

## 5. The WPF Bootstrapper Application

**Engine wiring (WixToolset.Mba.Core):** BA derives from `BootstrapperApplication`; lifecycle is
`Detect() → DetectComplete → Plan(action) → PlanComplete → Apply(hwnd)` with progress via
`ExecuteProgress` / `CacheAcquireProgress` and status via `ExecuteMsiMessage`,
`ExecutePackageComplete`, `ApplyComplete`. The WPF UI drives these on a dispatcher and reflects
state into the view model. Standard pattern: `Engine.Detect()` on load, map progress 0–100 to
the progress bar, handle `Error`/`Restart`/cancel.

**Pages (match SenseEase visual language):**
1. **Welcome** — logo, version, one primary CTA ("安裝 / Install"), license link if needed.
2. **Progress** — themed determinate bar (cache + execute phases), current-file/percent, Cancel.
3. **Finished** — success + "啟動 SenseEase / Launch" checkbox (→ launch the installed exe), or
   error state with the log path + Retry.
4. **Uninstall/Modify** — when run on an installed machine: Uninstall + the **opt-in "delete my
   data" prompt** (see §6).

**Must support:** light/dark (token-driven), zh-Hant + English (mirror the app — the installer
should switch like the app does), high-DPI, keyboard/AltF4/cancel, `/quiet`+`/passive` silent
modes (BA must honor `Display` = none/passive for CI).

---

## 6. Parity checklist (everything the Inno .iss does today)

- [ ] Per-user, no elevation (`PrivilegesRequired=lowest`).
- [ ] `MinVersion 10.0.19041`; `x64compatible` gate → MSI `LaunchCondition` / bundle conditions.
- [ ] Install whole `dist\v0.11-pre-alpha\*` → `{localappdata}\Programs\SenseEase`, excluding `*.pdb,*.xml,*.log,createdump.exe` (harvest filter).
- [ ] Start-menu shortcut (always) + desktop shortcut (opt-in, default-checked) → BA checkbox.
- [ ] Launch-on-finish (`nowait postinstall skipifsilent` equivalent).
- [ ] Autostart cleanup: remove `HKCU\...\Run` value `SenseEase` on uninstall; **never create it** (app owns it).
- [ ] **Preserve** `%LOCALAPPDATA%\SenseEase` across install/uninstall (do NOT author it as components).
- [ ] **Opt-in delete** of `%LOCALAPPDATA%\SenseEase` on uninstall, default Yes, bilingual prompt (port the Inno `CurUninstallStepChanged` logic to a BA prompt or a deferred custom action).
- [ ] App icon (`SetupIconFile`), ARP display name/icon (`UninstallDisplay*`).
- [ ] Locked upgrade keys (D5).

---

## 7. Inno → WiX migration (critical, don't skip)

The Inno installer registers `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{DB7DBFD5-...}_is1`.
The WiX bundle should, in **Detect**:
1. `util:RegistrySearch` that key → if present, the old version is installed.
2. Chain an `ExePackage` that runs Inno's `unins000.exe /VERYSILENT /NORESTART` **before** the MSI
   (so files don't collide), OR surface a "previous version found, it will be replaced" step in the BA.
3. Because user data lives in `%LOCALAPPDATA%\SenseEase` (untouched by either installer), the
   migration is data-safe — verify the Inno uninstaller's opt-in delete is **declined** during silent
   migration (run it with flags that keep data).

Test matrix: fresh install, upgrade-over-Inno, upgrade-over-WiX, uninstall (keep data), uninstall
(delete data), silent install, non-admin account.

---

## 8. Build & CI

- MSI: `wix build Package.wxs -ext WixToolset.Util.wixext -o SenseEase.msi`
- Bundle: `wix build Bundle.wxs -ext WixToolset.Bal.wixext -ext WixToolset.Util.wixext` (BA dll referenced).
- BA: normal `dotnet publish` of the WPF project (self-contained or framework-dependent — verify Burn's
  .NET host requirements for the BA runtime).
- Signing: sign BA dll → build MSI → sign MSI → build bundle → **detach engine, sign, re-attach, sign bundle**.
- 3 GB caveat: cab build is slow + memory-hungry; give CI a generous timeout and disk. The Trend Micro
  AV on the dev box (see project memory) will hammer build time — exclude the output dir from AV.

---

## 9. Phased rollout (de-risk the unknowns first)

- **Phase 0 — Spike (½–1 day):** WiX v5 Burn bundle + a *minimal* WPF BA that installs a 1-file
  per-user MSi un-elevated, shows a progress bar + Finish. Proves the managed-BA hosting + per-user
  + no-elevation path before investing in UI. **Highest-risk-first.**
- **Phase 1 — MSI parity:** author the real MSI (harvest the dist tree, shortcuts, registry, data
  preservation, opt-in delete, conditions). Validate against §6 with the Inno build as the oracle.
- **Phase 2 — WPF UI:** the themed pages (§5), zh/en, light/dark, silent modes.
- **Phase 3 — Migration + signing + CI:** §7 + §8, full test matrix, then cut over from Inno.

## 10. Risks

1. **Managed-BA hosting** is the classic WiX pain point and differs by version — pin v5, spike first.
2. **3 GB payload** → slow builds, big self-extracting exe, AV interference.
3. **Per-user Burn** quirks (most Burn samples are per-machine/elevated).
4. **Inno→WiX migration** double-install if Detect/remove is wrong.
5. **Signing/SmartScreen** reputation reset on the new bundle identity.

## 11. Lower-risk interim (recommended in parallel)

Tier-3 is weeks of work. To not block the current repackaging, ship **Inno Tier-1 branding**
(custom `WizardImageFile`/`WizardSmallImageFile`, `WizardSizePercent`, reworded copy, optional
zh-Hant language) now — it gets ~80% of the perceived polish at near-zero risk — and build the
WiX/WPF installer behind it, cutting over once Phase 3 passes the test matrix.
