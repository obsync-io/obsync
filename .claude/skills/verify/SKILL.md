# Verify: driving the Obsync WPF app

How to run and observe the real Obsync shell for verification, without touching the
user's data or their running instance.

## Hazards (learned the hard way)

- **Never launch `Obsync.App.exe` directly on a dev box.** `ObsyncPaths` resolves
  `%LOCALAPPDATA%\Obsync` via `Environment.GetFolderPath` (known folders — an env-var
  override of `LOCALAPPDATA` is IGNORED), so it opens the user's real database. The
  user often has their installed instance running against that same DB.
- **Never instantiate `Obsync.App.App` in a custom host.** The WPF `Application`
  constructor queues `OnStartup` onto the dispatcher; the first pump runs the REAL
  bootstrap and opens a second shell against the real `%LOCALAPPDATA%` DB (and runs
  startup hooks that WRITE to it). Use a plain `new Application()` and load a copy of
  App.xaml's `Application.Resources` (as a standalone `ResourceDictionary` with
  `assembly=Obsync.App` namespace qualifiers + a pack URI for Themes/Theme.xaml) via
  `XamlReader.Load`.
- **No mouse clicks / `SetForegroundWindow`.** Windows blocks focus stealing from
  background processes and the user's identical-looking Obsync window is often
  center-screen at the same coordinates — a synthesized click can land in THEIR app.

## Recipe

1. Small console/WinExe harness (net10.0-windows, UseWPF) referencing
   `src\Obsync.App\Obsync.App.csproj`:
   `Host.CreateDefaultBuilder().ConfigureServices(s => s.AddObsyncApp(dbPath, workspaces))`
   with paths under a scratch dir → `IDatabaseInitializer.InitializeAsync()` →
   plain `Application` + parsed resources → resolve `MainWindow` + `MainViewModel`,
   set DataContext, on `Loaded` call `viewModel.InitializeAsync()` (and optionally
   `ShowSectionAsync("Settings")`), then `app.Run(window)`.
2. Drive it headless-safely from PowerShell 5.1 (`-STA`) with UIAutomationClient:
   - `AutomationElement.FromHandle(proc.MainWindowHandle)`, `FindAll` text dump for
     assertions (works while occluded).
   - Buttons via `InvokePattern.Invoke()` (fires the bound command, no focus needed).
   - Screenshots via `PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */)` — but
     verify the pixels against the UIA text dump; with two same-titled windows
     stacked, screen-grab fallbacks can capture the wrong one.
3. Toasts render inside MainWindow's overlay — they appear in the window's own UIA
   tree and PrintWindow capture.
