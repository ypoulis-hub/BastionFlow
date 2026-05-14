# Contributing to BastionFlow

Thanks for considering a contribution! BastionFlow is small enough that you can usually go straight to a PR, but for non-trivial changes please open an issue first to discuss.

## Development setup

```powershell
winget install Microsoft.DotNet.SDK.8 JRSoftware.InnoSetup `
  --accept-package-agreements --accept-source-agreements

git clone https://github.com/ypoulis-hub/BastionFlow
cd BastionFlow
dotnet build
dotnet test
dotnet run --project src/BastionFlow.App
```

## Project layout

- `src/BastionFlow.Core/` — headless library (auth, Azure ARM, Bastion orchestration, cache). Fully testable; **no UI dependencies**.
- `src/BastionFlow.App/` — WPF UI. Views, view-models, theme, converters.
- `tests/BastionFlow.Core.Tests/` — xUnit + FluentAssertions.
- `tools/Generate-Icon.ps1` — regenerates `app.ico` after a logo change.
- `installer/` — Inno Setup script and build helper.

## Code style

- C# 12 (`net8.0-windows`), nullable enabled, warnings as errors.
- View-models inherit `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`; use `[ObservableProperty]` and `[RelayCommand]` source generators.
- Public APIs in `BastionFlow.Core` should be UI-agnostic (no `System.Windows.*`).
- Async methods: pass `CancellationToken`, name them `…Async`, use `ConfigureAwait(false)` in library code.

## What makes a good PR

- One logical change per PR.
- Tests for new behaviour in `BastionFlow.Core`.
- README updated if you add a user-visible feature.
- Don't reformat unrelated code — keeps diffs reviewable.

## Reporting bugs

Open an [issue](../../issues/new) and include:
- BastionFlow version (Help → About)
- Windows version + edition
- Output from the status bar tooltip (if an error appeared)
- Reproduction steps

If the bug involves an Entra / Bastion error code (`AADSTS…` or HTTP 4xx), include the full message — it's almost always the smoking gun.
