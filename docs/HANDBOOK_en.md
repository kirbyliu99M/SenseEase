# SenseEase Handbook (Pre-Alpha Test Build)

Thanks for helping test SenseEase!

This handbook walks you through every feature so you know what each screen is for
and don't misread a behaviour during testing. The whole thing takes about 5 minutes
to read.
**Please only report something once it's genuinely a problem. A "but I thought it
should…" caused by unfamiliarity isn't a bug — walking through this handbook once
usually realigns expectations.**

---

## 1. What is this app?

SenseEase is a **screen visual-comfort aid**. Its goal is to reduce the visually
induced motion sickness (VIMS), eye strain, and discomfort caused by long screen
sessions and by rapid on-screen motion (FPV video, scrolling feeds, fast-paced games).

The core approach:

- **Continuously detects the optical-flow change on screen** (how intense the motion
  you're watching is)
- **Dynamically adjusts the FOV (field-of-view mask)** based on accumulated pressure,
  dampening the peripheral motion
- **An AI Assistant you can chat with** to help fine-tune comfort
- **Head tracking** moves the FOV mask with your head position so your focal point
  always lands in the most comfortable centre region

The whole thing runs **locally (offline)** — your camera frames, audio, and
conversations are never uploaded.

---

## 2. Interface tour (what each window is for)

After launch you'll see the items below. Get familiar with them first so nothing is
misread during testing.

| Surface | Where | What it does |
|---|---|---|
| **Main Console** | The main window that opens on launch | Switch tabs, view data, configure |
| **Dashboard tab** | First page of the main window | Live values, demo controls, event log |
| **Eye Protection tab** | A main-window tab | Detailed eye-care metrics |
| **AI Assistant tab** | A main-window tab | Chatbot (local Mistral 3B) |
| **Settings tab** | Last page of the main window | Toggle each feature on/off |
| **Top Bar** | A thin strip at the top of the screen | Shows the VIMS pressure level (grey/amber/red) |
| **Notification Card** | A small pop-up after clicking the Bar | Quick discomfort report, Power Save, Engage Protection |
| **Overlay (FOV mask)** | A translucent mask over the whole screen | "Focus toward centre" to relieve peripheral visual pressure |
| **Tray icon** | System tray, bottom-right | Shows current status + right-click menu |

⚠ **Note in particular:**

- **The Bar grows as the mouse approaches** — that's a "make it easy to click" design,
  **not head tracking following the mouse**.
- **Head tracking only uses the webcam**; it never reads the mouse position.
- If head tracking "does nothing", it's usually that the webcam isn't authorised, or
  head tracking isn't enabled — not that the code wired it to the mouse (it didn't).

---

## 3. Turn on the features you want to test (Settings tab)

### Step 1: Open the "Settings" tab

The last page of the main window.

### Step 2: Enable the options below

At minimum we suggest enabling:

- **☑ Enable Head Tracking**
  - Requires webcam permission. The first time you enable it Windows may ask whether
    to let SenseEase use the camera — **choose "Allow"**.
- **☑ Bar + Tray colour (BarTrayColorEnabled)** (off by default)
  - When on, the Bar and tray show grey/amber/red by VIMS pressure level.
- **(Tick any other options for the features you want to test.)**

### Step 3: Close Settings and **restart the app**

Features like head tracking need a restart to load (the sidecar only starts at launch).

> To restart: right-click the tray icon → "Quit" → reopen the app.

---

## 4. Test head tracking (Overlay "always-on" mode)

Once head tracking is enabled, the FOV mask moves with your face position. The easiest
way to see the effect is to **set the Overlay to always-on**.

### Steps

1. Launch the app; confirm the Bar appears at the top of the screen.
2. Click the Bar → the Notification Card pops up.
3. The card has an **"Engage Protection"** button — press it.
   → The Overlay appears and stays on even without any VIMS pressure.
4. **Move your head** (left/right, up/down).
   → The "clear centre region" of the FOV mask should follow your head.
5. If the mask moves = head tracking is working ✓
6. If the mask **stays fixed dead-centre** = the webcam isn't connected, or tracking
   isn't enabled. Check:
   - Whether "Enable Head Tracking" is ticked in Settings
   - Windows Settings → Privacy → Camera → is SenseEase allowed
   - Restart the app and retry

### Things to test alongside

- Turn head left → FOV centre moves left ✓
- Turn head right → centre moves right ✓
- Tilt head down → centre moves down ✓
- Move your face out of the camera frame → centre **returns to dead-centre** (an
  intentional safety behaviour)

---

## 5. After testing head tracking, **turn off Overlay always-on mode**

When you're done, leaving the Overlay on interferes with normal use (the whole screen
stays under a translucent mask). **Be sure to turn off always-on mode:**

- Option 1: Click the Bar → Notification Card → press **"Engage Protection"** again to
  switch back to automatic mode.
- Option 2: Right-click the tray icon → "Hide Overlay".

The Overlay then returns to its normal mode — **appearing automatically only when VIMS
pressure crosses the threshold**.

---

## 6. Test with Calibration / the demo video

Calibration runs an **FPV flight video** (113 MB, bundled in the installer) that
simulates intense on-screen motion, observes how uncomfortable you find it, and helps
tune your personal profile.

### Steps

1. Switch to the "Dashboard" tab.
2. Find the **"Calibration & Demo" button** (the blue primary button).
3. Press it; the FPV video starts in a new window.
4. **Watch the whole clip** (~1 minute). If anything feels uncomfortable, use the
   "Mild / Moderate / Noticeable / Severe" buttons on the Notification Card to report it.
5. After the video, the AI Assistant may proactively propose a profile change, e.g.
   "I suggest switching to the SENSITIVE profile."
6. **H11:** before any change is applied, a **confirmation card** shows a plain-language
   recap of your answers and exactly what will change (profile + protection strength).
   Nothing is applied until you press **Apply**; **Not now** keeps your current settings.

### What to expect

- During playback the Bar's pressure level rises (grey → amber → possibly red)
- If pressure is high enough, the FOV Overlay appears automatically to protect you
  (that's its main job)
- Afterwards the Bar drifts back down to grey

---

## 7. AI Assistant chat test

Switch to the "AI Assistant" tab to chat with the locally-running Mistral 3B model.

### Important notes (★)

- **First load takes 30–120 seconds** (NVIDIA GPU ~10 s; CPU/iGPU ~60–120 s). While
  loading, the window shows "Loading Ministral 3B…". **Please be patient — the app
  hasn't frozen.**
- When loading finishes, the status text changes to "Ministral 3B ready".
- On an Intel iGPU (e.g. Iris Xe) each reply takes 5–30 s — that's normal.
- On an NVIDIA GPU each reply takes about 1–2 s.

### Suggested messages to try

```
Hi, please introduce yourself
```

```
Looking at the screen makes me dizzy — can you help?
```

```
I'd like to switch to a more sensitive profile
```

```
Please lower the sensitivity a bit
```

The AI may:
- Give you advice
- Proactively propose a profile switch (shown on the Notification Card with
  "Accept / Later / Decline")
- Record your preferences

### Also try

- Press the "Clear" button at the bottom to wipe the conversation.
- After clearing you should see the welcome message reappear (by design). Clearing the
  chat also wipes the on-disk diagnostic trace (a privacy measure).

---

## 8. Real-world usage test

Please **leave the app running for 30–60 minutes** and use it as you normally browse /
work, for example:

- **Watching YouTube** (especially sports, gaming, FPV — high-motion footage)
- **Scrolling social feeds** (fast Twitter/IG feed scrolling)
- **Playing games or watching Twitch streams**
- **Reading documents for a long time** (test that the app sits still under a static
  screen and doesn't fidget)

### What to expect

- Fast motion → Bar gradually goes grey → amber → red → FOV Overlay appears automatically
- The screen settles → pressure eases back down → Overlay hides automatically
- The tray icon colour (if BarTrayColorEnabled is on in Settings) tracks the level
- Head tracking keeps following your head position

### What should NOT happen

- The app suddenly crashes / windows vanish
- The Overlay gets stuck on even after pressure has fallen
- The Bar or tray shows the wrong colour (e.g. grey while pressure is high)
- Video playback stutters badly
- An AI reply takes more than 3 minutes

If any of the above happens, **please report it with the logs below:**

```powershell
# Run in PowerShell — collects only the diagnostic files (no personal data)
& "$env:LOCALAPPDATA\Programs\SenseEase\scripts\collect-logs.ps1"
```

This creates a zip on your Desktop containing only crash logs, the diagnostic
folder (`%LOCALAPPDATA%\SenseEase\diag`), and the llama-server log. It
**does not** include your chat history, calibration answers, AI model trace,
comfort profile, or usage aggregates.

Send that zip along with your problem description.

---

**Optional: sharing the full data folder**

The test team may sometimes ask for the complete data folder to investigate a
specific issue. If so, run the command below — but **read the warning first**:

> **Privacy warning:** the full folder includes files that contain personal
> health information — your AI chat history, calibration answers (which may
> include details about vestibular conditions, migraines, or other health
> factors), raw AI model traces, and your personalised comfort profile. Only
> share this if you are comfortable with the test team seeing that content.
> The zip never leaves your machine unless you send it.

```powershell
# OPT-IN only — run this if the test team explicitly asks for the full folder
$out = "$env:USERPROFILE\Desktop\SenseEase-FULL-$((Get-Date -Format 'yyyyMMdd-HHmm')).zip"
Compress-Archive -Path "$env:LOCALAPPDATA\SenseEase\*" -DestinationPath $out -Force
"Full zip written to: $out"
```

---

## 9. Uninstalling (your data is yours)

When you uninstall SenseEase, the uninstaller asks whether to **also delete your
personal data** (settings, chat history, calibration, and logs under
`%LOCALAPPDATA%\SenseEase`).

- Choose **Yes** for a clean removal (recommended if you're done testing).
- Choose **No** to keep your data for a future reinstall.

A normal reinstall never deletes your data; only this explicit, opt-in prompt does.

---

## Thanks for testing 🙏

SenseEase aims to reduce screen-induced visual discomfort, and your time testing it
helps that goal enormously.

- See a bug, find the UX awkward, or want a new feature? **All feedback is welcome.**
- But **read this handbook first** — most "huh, why does it do that?" questions are
  usually answered here.
- Enjoy the testing, and we hope the tool genuinely makes your eyes a little more
  comfortable 😊

---

**Version**: v0.11.1 Pre-Alpha
**Reporting channel**: (your channel, e.g. email / GitHub issue / group chat)
**Date**: 2026-06-07
