# Contributing

## Scope

Sunny Mod Loader accepts data-only Mod packages. Contributions must not add game files, decompiled source,
credentials, personal save data, logs or proprietary assets to the repository.

## Build

Requirements:

- Windows PowerShell 5.1 or PowerShell 7
- .NET 8 SDK
- A legally installed copy of the target game
- Official BepInEx 5.4.23.5 installed in that game directory

```powershell
./build.ps1 -Configuration Release -GameRoot "D:\Games\败犬栖居的晴空日常" -NoInstall
```

Set `SUNNY_GAME_ROOT` to use the same game directory for subsequent commands. Game and Unity assemblies are local
compile-time references only and must never be committed.
Source ownership and dependency rules are documented in `src/SunnyModLoader/README.md`.

## Validation

Before opening a pull request:

1. Build with zero warnings and zero errors.
2. Run the startup and installer diagnostics documented in `README.md`.
3. Run `new-mod.ps1` and `pack-mod.ps1` against a temporary directory.
4. Inspect the final ZIP and confirm it contains no BepInEx core binaries, game files, logs or configuration files.

Keep changes scoped and document user-facing syntax or compatibility changes in `FLOW.md` and `CHANGELOG.md`.
`build.ps1` also enforces the 2000-line maximum for individual C# source files.
