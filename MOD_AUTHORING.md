# 从零编写 Sunny 数据 Mod

本文面向只编写剧情、文本、语音、图片、BGM 和 Sprite 的 Mod 作者。普通数据 Mod 不需要 Visual Studio、
.NET SDK 或 `build.ps1`。

## 1. 先区分 Mod 与 Loader

- 数据 Mod：`manifest.json + story + assets`，不需要编译。
- Loader 本体：`src/SunnyModLoader/**/*.cs`，只有修改这些 C# 文件时才运行 `build.ps1`。
- `pack.ps1` 发布整个 Loader 和 SDK，不是普通 Mod 的打包命令。
- 普通 Mod 使用 `pack-mod.ps1`。
- 第三方 Spine 动态人物请先阅读 [SPINE_AUTHORING.md](SPINE_AUTHORING.md)。

当前稳定版本：Loader `v1.2.0`、Loader API `2`、Manifest schema `2`。API 和 schema 是协议整数，
不是 Mod 的 SemVer。第一个测试版 Mod 建议从 `0.1.0` 开始，不要直接写 `1.0.0`。

## 2. 创建第一个目录

自动生成模板：

```powershell
./new-mod.ps1 -Id com.yourname.first-mod -Name "我的第一个 Mod"
```

生成位置为 `Mods/com.yourname.first-mod`。也可以手工建立：

```text
Mods/com.yourname.first-mod/
├─ manifest.json
├─ story/
│  └─ main.sunny
└─ assets/
   ├─ voice/
   ├─ music/
   ├─ images/
   ├─ sprites/
   └─ spine/
```

`id` 使用小写 ASCII 反向域名格式，例如 `com.yourname.first-mod`。发布后不要随意改变 ID。

## 3. 编写 Manifest

`manifest.json` 只保存元信息、用户设置和 Mod 关系，不放剧情、分支或资源声明：

```json
{
  "schemaVersion": 2,
  "id": "com.yourname.first-mod",
  "name": "我的第一个 Mod",
  "version": "0.1.0",
  "authors": ["Your Name"],
  "compatibility": {
    "loaderApi": 2,
    "gameBuilds": ["a3183ccb0bd148b085e61979ba356743"]
  },
  "defaults": {
    "enabled": false,
    "priority": 0
  }
}
```

可以在 Manifest 中声明运行依赖和显式冲突：

```json
{
  "dependencies": [
    { "id": "org.example.base-content", "version": ">=1.0.0 <2.0.0" }
  ],
  "conflicts": [
    { "id": "org.example.alternate-route", "version": "*" }
  ]
}
```

`dependencies` 中的 Mod 必须存在、启用并满足版本范围；否则当前 Mod 不会进入运行时。
`conflicts` 命中的两个 Mod 都不会进入运行时。版本范围支持精确版本、`=`、`>`、`>=`、`<`、`<=`，
多个条件用空格或逗号表示 AND；省略 `version` 等同于任意版本。关系错误会在 F8 问题页显示为错误。

预发布版本建议：`0.1.0` 为首个测试版，修 Bug 升到 `0.1.1`，增加一组新能力升到 `0.2.0`；
只有准备作出稳定兼容承诺时才使用 `1.0.0`。

## 4. 从原游戏脚本选择入口

Release 根目录的 `Script/` 提供正常剧情流程参考：`Entry.txt`、`vol1.txt`、`vol2.txt`、`vol3.txt`，以及
整理好的 `dialogue-index.tsv`。文件扩展名只用于打开参考文件，例如 `Script/vol1.txt` 在流程声明中仍写成
`scene="Script/vol1"`。

`dialogue-index.tsv` 每行已经给出定位所需信息：

| 索引列 | 流程字段 |
| --- | --- |
| `resourcePath` | `scene` |
| `label` | `label` |
| `dialogueOrdinal` | `line` |
| `speaker` | `speaker` |
| `text` | `expect` |

按序号定位时，解析器最低要求 `scene + label + line`；发布 Mod 应完整填写
`scene + label + line + speaker + expect`。`line` 从 `0` 开始，只统计当前 Label 下的台词命令。
`speaker` 必须使用索引中的内部说话人，不要使用 `displayName`。

不写 `line` 时，最低要求 `scene + label + text`，且完整原文必须在该 Label 内唯一；仍建议补上 `speaker`。
索引的 `id` 只是检索标识，当前语法不能用它代替 `scene/label/line`。完整说明和可复制示例见 Release 的
`Script/README.md`。

## 5. 编写一条可进入的支线

`story/main.sunny`：

```text
@branch {
    scene="Script/vol1"
    label="1-1"
    line=36
    speaker="旁白"
    expect="我拼死的祈祷没有传达到，她还是将吸管含入了口中"

    option {
        text="进入我的 Mod"
        enter="start"
        repeat=true
    }

    option {
        text="继续原剧情"
        continue=true
        repeat=true
    }
}

label start:
旁白 "这是我的第一句 Mod 剧情。"
return
```

Loader 不会自动生成“继续原剧情”。需要这个选项时必须显式声明 `continue=true`。`return` 返回进入支线前的位置；
`load` 是单向切换，不会自动返回。

这里没有写流程 `id`：Loader 会按场景、锚点、目标入口和条件生成稳定内部 ID。普通作者通常不需要看到它。
如果非重复选项已经发布，并且以后可能修改锚点或目标但仍希望旧存档沿用“已经选择过”的状态，可为
`@branch` 和对应 `option` 显式填写固定 `id`。Mod 自身的 Manifest `id`、设置 `id` 和 `$sprite` 使用的
Sprite `id` 仍然必填，因为它们会被配置或脚本直接引用。

所有 `@` 声明必须写在第一条普通 DSL 命令之前。普通 `//` 注释只解释代码，不参与流程控制。

## 6. 添加语音、BGM 和图片

将资源放入当前 Mod 的 `assets`，使用 `@/` 从 Mod 根目录引用：

```text
label start:
bgm "@/assets/music/route.ogg" [volume="0.8"] [fadetime="1"]
background "@/assets/images/background.png"
effect flash [in=0.03] [hold=0.02] [out=0.12]
旁白 "这句带语音。" [voice="@/assets/voice/line-001.ogg"] [voiceVolume="1"]
stopbgm [fadetime="1"]
return
```

常用路径：

- `@/assets/...`：当前 Mod 根目录。
- `./file.sunny`：当前流程文件所在目录。
- 无前缀的 `BGD/...`、`Music/...`：原游戏资源。
- 不允许手写 `mod://`、越出包根或跨 Mod 引用。

纯配音 Mod 可以只写 `@voice` / `@voices` 声明，不需要创建额外剧情。
`effect flash` 是 Loader 生成的全屏闪色，不需要资源文件；颜色、透明度、淡入/保持/淡出、重复次数和
是否等待均可调整，完整参数见 `FLOW.md` 的“屏幕特效”。

## 7. 在游戏中测试

1. 启动游戏并按 `F8`。
2. 点击重新扫描。
3. 在“问题”页确认没有错误。
4. 在“Mod”页启用新 Mod。
5. 应用到当前剧情。
6. 从锚点之前的存档推进，分别测试进入、继续、返回、历史回滚和读档。

已经越过锚点时，重新加载不会让剧情倒退并重新触发。正在 Mod 剧情中时，先 `return` 回原剧情再重载。

可选启动诊断：

```powershell
$env:SUNNY_MODLOADER_VALIDATE = "1"
$env:SUNNY_MODLOADER_QUIT_AFTER_VALIDATE = "1"
./败犬栖居的晴空日常.exe
Remove-Item Env:SUNNY_MODLOADER_VALIDATE, Env:SUNNY_MODLOADER_QUIT_AFTER_VALIDATE
```

日志位于 `BepInEx/LogOutput.log`。普通作者不需要开启 `SUNNY_MODLOADER_VALIDATE_INSTALLER`。

## 8. 打包为 .sunmod

```powershell
./pack-mod.ps1 -ModDirectory ./Mods/com.yourname.first-mod
```

默认输出 `Mods/com.yourname.first-mod-0.1.0.sunmod`。归档根必须直接包含 `manifest.json`、`story` 和
`assets`，不能压入第二个 Mod、多个 Manifest 或 `.sunny-installed.json`。

手工开发目录属于 unmanaged。用 F8 安装同 ID 的 `.sunmod` 前，先把 `Mods/com.yourname.first-mod`
移出 `Mods`，否则安装器会拒绝覆盖开发目录。通过安装器安装的新包默认禁用，需要玩家明确启用。

## 9. 常见错误

- 普通数据 Mod 运行了 `build.ps1` 或仓库级 `pack.ps1`。
- 把剧情、分支或资源字段写入 Manifest。
- `loaderApi` 不是当前值 `2`，或把它误写成 Mod 的 SemVer。
- `dialogueOrdinal` 当成从 `1` 开始。
- 使用显示名代替索引中的真实 `speaker`。
- 原文不唯一却省略 `line`。
- 依赖 Mod 缺失、未启用、版本不满足，或与已启用 Mod 命中 `conflicts`。
- 在普通 DSL 之后才写 `@branch` / `@voice` 声明。
- branch/call 子流程末尾忘记 `return`。
- 打包后 Manifest 不在归档根或唯一包装目录中。

完整语法参考见 [FLOW.md](FLOW.md)，机器可读 Manifest 规则见 [manifest.schema.json](manifest.schema.json)。
