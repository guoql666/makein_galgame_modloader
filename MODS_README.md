# Mods

按 `F8` 打开 Sunny Mod Loader 管理器。可以直接粘贴 `.sunmod` / `.zip` 路径，或把归档放入
`Mods/Inbox` 后从安装页处理。新安装包固定为禁用，需由玩家明确启用并应用到当前剧情。

手动开发目录使用下列结构：

```text
Mods/
└─ org.example.my-mod/
   ├─ manifest.json
   ├─ story/
   └─ assets/
```

Manifest schema 2 只保存包元信息和用户设置，定义见 `ModSDK/manifest.schema.json`。剧情、分支、语音、
CG 和资源声明写入自动发现的 `story/*.txt` 或 `story/*.sunny`，语法见 `ModSDK/FLOW.md`。
当前 Loader API 为 2。分支必须在一个 `@branch` 中显式列出一个或多个 `option {}`；Loader 不会自动
添加“继续原剧情”。

全局语音音量、推进停止策略和历史语音重放由内置 `VoiceControl` 提供。Loader 会检查游戏 Build、
SemVer、流程声明、资源扩展名、包内路径和重解析点；验证失败或 ID 冲突会显示在 F8“问题”页。

Steam 工坊 AppID 和额外目录可在 `BepInEx/config/qm.sunny.modloader.cfg` 的 `[Workshop]` 中配置。
工坊与额外目录为只读来源，Loader 不会删除其中的文件。Sunny 数据 Mod 不加载 DLL 或可执行脚本。
