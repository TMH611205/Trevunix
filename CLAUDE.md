# CLAUDE.md

Trevunix: Windows optimization and cleanup app. WPF on .NET 10, Windows only, runs elevated.

## Layout

- `src/Trevunix.Core`: all logic, no WPF. Each feature has its own folder (`DiskUsage/`, `Updates/`, `Settings/`, ...).
- `src/Trevunix`: WPF app. `ViewModels/` (CommunityToolkit.Mvvm), `Views/` (UserControls picked by DataTemplate in `MainWindow.xaml`), `Resources/Strings*.resx`.
- `tests/Trevunix.Core.Tests`: xUnit v3 tests for Core.

## Rules

- Original code only. This project is a clean rewrite; never copy or translate code from optimizerDuck or the pre-3.0 Trevunix (the `main` branch before the rewrite).
- UI text goes in `Resources/Strings.resx` (English) and `Strings.vi.resx` (Vietnamese), read with `{l:T Key}` in XAML or `Tr.Get`/`Tr.Format` in C#. No em dashes in UI text.
- Warnings fail the build (`Directory.Build.props`).
- Version lives in `Directory.Build.props`; CI appends the run number.

## Commands

- `dotnet build Trevunix.slnx -c Release`
- `dotnet run --project tests/Trevunix.Core.Tests -c Release`
- `csharpier format .` before committing.
- `build.cmd`: owner's local single-file `Trevunix.exe` at the repo root (git-ignored). Built with `LocalBuild=true`, so it never self-updates.

Repo: open source under GPL-3.0 at TMH611205/Trevunix (public), which holds the source and the releases the updater reads. The old private TMH611205/Trevunix-src is no longer used. Never commit secrets, tokens or personal files; everything pushed is public.

Releases: pushing to main only builds a test artifact. Publishing to users is manual (Actions > Release > Run workflow) and only when the owner says so. Before asking to release, list what changed since the last release (`git log` since the last release commit).

Commits: lowercase Conventional Commits (`feat(disk): ...`).
