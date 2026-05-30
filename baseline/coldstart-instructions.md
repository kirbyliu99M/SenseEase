# Cold-Start Test Instructions

This test must be run manually by the user. Follow these steps:

1. Stop any running SenseEase process (check Task Manager)
2. Open Command Prompt or PowerShell and navigate to the publish-test directory
3. Run SenseEase.App.exe
4. Start a stopwatch the moment you press Enter
5. Stop the stopwatch when the dashboard window is fully responsive:
   - Taskbar icon appears
   - Main window is visible and responding to clicks
   - Tray icon is present in the system tray
6. Record the elapsed time (seconds) to `baseline/coldstart-seconds.txt` as a single integer line

This measurement will be captured at G0 review stage.
