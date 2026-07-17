# Changelog

## 1.1.2 - 2026-07-18

- Adopts the revised project README as the canonical release documentation.
- Keeps runtime behavior, Loader API 2, and Manifest schema 2 unchanged from R1.1.1.

## R1.1.1 - 2026-07-18

- Makes IDs optional for branches, options, dialogue and voice patches, voice groups, Gallery entries, and resource replacements.
- Generates deterministic internal IDs from declaration semantics while preserving explicit IDs for long-lived save and Gallery state.
- Keeps IDs mandatory only where scripts or configuration directly reference them, including Mods, settings, Sprites, and Sprite states.
- Adds the exported normal-flow scripts to Release packages as `Script/`, together with dialogue and resource indexes and exact patch-locator guidance.
- Routes Loader voice playback through the original `Master` AudioMixer group so the game master-volume setting applies without coupling voice to sound-effect or music volume.

## 1.1.0 - 2026-07-17

- Stores Mod-route progress in a Loader sidecar while keeping the primary save in the game's original JSON format.
- Loads the nearest valid original return point when the target Mod is disabled, missing, or unavailable.
- Preserves full Mod progress when the Loader and required Mods remain available.
- Migrates legacy `mod://` saves and cleans up sidecars when their save is overwritten or deleted.
- Adds startup diagnostics for nested fallback selection, enabled Mod restoration, and disabled Mod fallback.

## 1.0.0 - 2026-07-17

- First stable public release.
- Defines Loader API 2 and Manifest schema 2 for data-only Mods.
- Adds safe Mod installation, update, backup, restore and uninstall workflows.
- Adds flow branches, calls and returns, dialogue and asset overrides, custom BGM and voice playback.
- Adds built-in VoiceControl with game settings integration and backlog voice replay.
- Adds existing Spine character support, external PNG/JPEG sprites, CG entries and full-screen flash effects.
- Adds Steam Workshop and additional read-only package roots.
- Adds non-modal F8 management UI and pending-choice input guards.
