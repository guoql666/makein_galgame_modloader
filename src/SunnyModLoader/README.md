# Source Architecture

Sunny Mod Loader is compiled as one BepInEx plugin assembly. The folders below are ownership boundaries inside that
assembly; types intentionally remain in the `SunnyModLoader` namespace so this reorganization does not change Harmony
discovery, reflection names, save formats, or the public audio-control API.

```text
SunnyModLoader/
├─ Core/          shared models, validation, resource policy, JSON and path utilities
├─ Flow/          flow models, parsing, semantic validation and DSL compilation
├─ Installation/  archive installation, recovery and Workshop discovery
├─ Runtime/       active Mod registry and feature services
├─ Integration/   narrow Harmony patches that connect the game to runtime services
├─ Plugin/        BepInEx entry point and F8 manager composition
├─ Diagnostics/   opt-in in-game integration diagnostics
└─ SunnyModLoader.csproj
```

Dependency direction should generally be:

```text
Plugin / Diagnostics
        ↓
Integration / Installation / Runtime
        ↓
Flow
        ↓
Core
```

Harmony patches should contain only argument adaptation and delegation. Game behavior belongs in a Runtime service;
flow syntax belongs in Flow; reusable validation and resource rules belong in Core. `RuntimeContentCoordinator` is the
single place that orders active-content rebuilds.

`FlowService` is a partial class split by compilation phase:

- `FlowModels.cs`: declarations and compiled package models.
- `FlowService.cs`: package discovery and per-file compilation pipeline.
- `FlowDeclarations.cs`: semantic handlers for `@branch`, `@voice`, `@sprite`, `@spine`, and related declarations.
- `FlowCommandCompiler.cs`: DSL command and resource rewriting.
- `FlowSyntaxParser.cs`: block/token parsing and shared scalar validation.

`build.ps1` rejects C# source files above 2000 lines. When a file approaches that limit, split it at a responsibility or
compilation-phase boundary instead of adding a generic helper bucket.
