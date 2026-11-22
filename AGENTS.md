# Working on T

T is an SSH terminal client built with Avalonia 12 and FluentAvalonia (.NET 10). This file tells
contributors and their coding agents how the project is built and what a change must look like to
be merged. Read it before you write code.

## Commands

```
dotnet build T.slnx
dotnet test --project T.Tests/T.Tests.csproj
./lint.ps1          # checks XAML (XAML Styler) and C# (dotnet format); CI fails without it
./lint.ps1 -Fix     # applies the formatting
```

Run `./lint.ps1 -Fix` before every commit, not only at the end: formatting that piles up is harder
to review and fails CI. Build and test before you open a pull request, and say in the description
what you ran and what you could not test (for example: "not tried against a real SSH host").
`lint.ps1` is a PowerShell script; on Linux and macOS it needs `pwsh`. CI lints on Windows only.

To have Git check this for you, enable the pre-commit hook once per clone:

```
git config core.hooksPath .githooks
```

It rejects a commit when the lint fails (`git commit --no-verify` skips it for work in progress).

## Pull requests

- Base branch is `develop`. Commits must be signed.
- One topic per pull request. Leave out unrelated documentation, editor files and build scripts.
- Commit subjects: `feature: …`, `fix: …`, `ci: …` in lower case, imperative, in English.
- UI text, comments and documentation are in English.
- Releases: betas (`vX.Y.Z-beta`) are tagged on `develop`. Stable releases (`vX.Y.Z`) are tagged on
  `master` only, after `develop` was merged into it; the release workflow rejects anything else.
- New logic comes with tests. A change to the database schema bumps `SchemaVersion`, adds a
  migration and a test that opens a database of the previous version.

## Where code goes

| Project | Content |
| --- | --- |
| `T.Models` | Plain data and settings |
| `T.Abstractions` | Interfaces between the layers |
| `T.Terminal` | The terminal emulator. No UI, no SSH; everything is unit tested |
| `T.Services` | SSH, storage, parsers, remote scripts. No Avalonia types |
| `T.UI` | Views, view models, controls |
| `T.Tests` | xUnit v3 tests for `T.Terminal` and `T.Services` |

Logic that can be tested without a window belongs in `T.Services` or `T.Terminal`, not in a view
model or code-behind.

## Code

- MVVM with CommunityToolkit: `[ObservableProperty]`, `[RelayCommand]`. State and decisions live in
  view models. Code-behind is for what only the view can do (focus, pointer handling, layout
  measurements). No magic numbers for modes: use an enum.
- Bindings are compiled: set `x:DataType`. Use `ReflectionBinding` only where a compiled binding is
  not possible.
- A view model that a designer or preview should render has a parameterless constructor with
  sample data.
- Comments say why, not what. Public members of services have a short XML summary.
- A new setting needs the model (`T.Models`), `SettingsDialogViewModel` (load and apply) and the
  settings dialog.

## Design

The app should look like one Windows 11 application in light and dark theme.

- **Colours**: only theme resources (`{DynamicResource TextFillColorSecondaryBrush}`,
  `ControlStrokeColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`, …). Never `Brushes.Gray`
  or hex values. The only fixed colours are those of the terminal content itself.
- **Shared styles** in `T.UI/Styles/AppStyles.axaml`; use them before you invent a new look:
  `Border.Panel` (framed pane), `Button.tool` / `ToggleButton.tool` (flat icon buttons),
  `Border.toolSeparator`, `TextBlock.paneTitle` / `sectionHeader` / `hint`, `Ellipse.status`
  (state dot with `busy`, `connected`, `error`), `Button.activity`.
- **Icons**: `PathIcon` with a geometry from `T.UI/Resources/Icons.axaml` (Fluent System Icons,
  regular). Buttons in toolbars are icon buttons with a tooltip and `AutomationProperties.Name`,
  not text buttons. Do not use glyphs of the symbol font for small icons.
- **State** is shown with colour and text, never colour alone.
- **Numbers that change** (rates, counters, percentages) get a fixed width, right alignment and
  `FontFeatures="+tnum"`, so nothing next to them moves.
- **Dialogs**: `FAContentDialog` with a `UserControl` as content, like `SessionEditorDialog`:
  460px wide, `sectionHeader` groups, `FAInfoBar` at the top for the validation error, `FANumberBox`
  for ports. Use `DialogService.ConfirmAsync` before anything is deleted or a live connection is
  closed.
- **Status bar**: 26px high and already full. New actions go into a context menu, a flyout or the
  view they belong to. Do not repeat information the tab or another part of the window shows.
- **Session tabs**: styled in `Styles/SessionTabs.axaml`. The selected tab merges into the content
  below it; anything that changes the height of the tab strip or adds a border under it breaks that.
- **Empty states** explain in one sentence what the view is for and offer the first action.
- Check every UI change in the light and the dark theme, and in a narrow window.

## Terminal and SSH rules

- Never send input to the server that the user did not type or explicitly ask for. A button must
  not send Enter. `VirtualTerminal.PromptState` and `IsAlternateScreen` tell you whether a shell
  prompt or a full-screen program is active; without shell integration you do not know.
- Remote commands run through the user's login shell, which may be bash, fish or tcsh. Wrap scripts
  with `LinuxSystemMetrics.InPosixShell` (or one single-quoted `sh -c '…'` argument). Never put
  file names, process names or other remote data into a command line.
- Limit time and output of remote commands (`RunBoundedCommandAsync`) and treat their output as
  untrusted.
- Passwords and key passphrases are stored encrypted and are never logged or shown. Local listeners
  bind to 127.0.0.1.
- Work that polls the server stops when its view is not visible and when the connection ends.
