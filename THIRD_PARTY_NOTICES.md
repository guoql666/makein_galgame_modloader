# Third-Party Notices

Sunny Mod Loader is built against the following third-party runtime components. They are not included in this
repository or in the release archive; players install the official BepInEx package separately.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| BepInEx | 5.4.23.5 | MIT | https://github.com/BepInEx/BepInEx/tree/v5.4.23.5 |
| HarmonyX | bundled by BepInEx 5.4.23.5 | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod | bundled by BepInEx 5.4.23.5 | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | bundled by BepInEx 5.4.23.5 | MIT | https://github.com/jbevain/cecil |
| Unity Doorstop | 4.5.0, bundled by BepInEx | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0 |

The game executable, Unity runtime, assets, and managed assemblies are not part of this project and are not
redistributed. They are referenced only from a user-supplied local game installation during compilation and testing.

Release archives may include a `Script/` author-reference directory generated from the target game's exported
normal-flow text. That extracted text is not covered by Sunny Mod Loader's MIT License; all rights in it remain with
the game's respective copyright holders. A distributor is responsible for confirming that they may redistribute it.
