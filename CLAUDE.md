@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app that follows the `winui-tray-app` skill. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, secrets or packaging. It ships x64 and ARM64 only, whatever AGENTS.md says about x86.

- Launch with `dotnet run` (see README.md); never register the package by hand.
- `App.xaml.cs` owns the tray icon, single instance, idle memory release and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/RadarFlyoutWindow`) hides on dismiss (`HidePopup`) and closes after a minute hidden (`CloseWindow`). Anything it starts, it must stop in those two.
- Network work runs only while the flyout is visible: `Controls/RadarAnimator` refreshes on `ActivateAsync` and stops on `Deactivate`.
- The Azure Maps key goes in `OAuth.resw` (gitignored) and is read through `Services/Secrets`. Nothing secret goes in `SettingsService`. User tokens, if one is ever needed, go in PasswordVault (see the skill).
- Keep README.md current when features change.
- Releases: bump `Identity/@Version`, run `build-msix-for-gh.ps1`, publish the draft without changing its tag, then run `build-msix-for-gh.ps1 -VerifyPublished`. The script is a copy of the `winui-tray-app` skill's; fix it there and recopy, never here.
