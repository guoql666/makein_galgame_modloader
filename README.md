# Sunny Mod Loader

这是面向《败犬栖居的晴空日常》Windows 发行包的数据 Mod 底座。它通过 BepInEx 5 和 Harmony
加载，不修改游戏 EXE、`UnityPlayer.dll`、`Assembly-CSharp.dll` 或 Unity 资源包。
本项目包含使用AI生成内容。

元信息 Manifest v2 的机器可读定义见 [manifest.schema.json](manifest.schema.json)，流程格式见 [FLOW.md](FLOW.md)。

普通数据 Mod 作者请从 [MOD_AUTHORING.md](MOD_AUTHORING.md) 开始。指南包含从模板创建、选择原版台词锚点、
游戏内测试到生成 `.sunmod` 的完整流程；这类 Mod 不需要编译 Loader。

## 当前能力

- 安全安装根布局或单层包装目录的 `.sunmod` / `.zip`，并支持同 ID 更新、可恢复卸载和版本备份。
- 扫描 `Mods/<mod-id>/manifest.json`、额外目录和已配置 AppID 的 Steam 工坊订阅。
- 逐包校验兼容性、ID、SemVer、重复 ID、资源路径、扩展名和引用关系；坏包会隔离并显示在 F8 问题页。
- 自动发现 `story/**/*.txt` 和 `story/**/*.sunny`，由正式 `@` 声明定义稳定锚点、台词覆盖、批量配音、分支、CG 和资源覆盖。
- 在流程中使用 `@/` 引用 Mod 根资源、`./` 或 `../` 引用当前流程目录；`mod://` 仅供 Loader 内部使用。
- 在原剧情 Label 后或指定 Label 内第 N 条台词后添加 Mod 分支；选择待决时使用游戏原生
  `ScriptBlocker` 阻断点击、滚轮、Ctrl 快进和“跳到下一选项”。
- 以 `return` 返回父流程；返回快照可被对话历史重复使用，并持久化背景、BGM 和人物状态供读档后的降级恢复。
- 支持 `call "./scene.sunny"` 无条件调用子流程；`load`、`call`、branch option 的语义明确分离。
- 使用 Manifest 布尔设置控制分支显示；Manifest 只保存包元信息、兼容性、默认状态和用户设置。
  F8 中的启停、优先级和设置先暂存，剧情重载成功后才提交。
- 向 CG 图鉴追加外部图片；外部图片也可直接用于背景命令或通过 `@sprite` 注册为原版静态人物。
- 自定义流程可用 `bgm` / `stopbgm` 播放 Mod 自带音乐，原资源替换统一使用 `@replace`。
- 支持原游戏已有的背景过渡/移动/缩放、Spine 角色、角色动画和全屏视频能力。
- 流程可用 `effect flash` 播放无需资源文件的全屏闪白/闪色，支持透明度、时序、重复与阻塞/并行控制。
- 提供无交互启动诊断，可验证剧情能力以及安装、更新、回滚、卸载恢复和恶意归档拒绝矩阵。
- F8 管理器是非模态窗口：窗口内输入由管理器处理，窗口外点击、滚轮、自动播放和快进保持游戏原行为。

## 安装与构建

数据 Mod 作者无需运行下面的 Loader 构建命令。源码仓库可使用 `new-mod.ps1` 创建模板，并用
`pack-mod.ps1` 打包；分发包中的相同工具位于 `ModSDK/`，详细步骤见 [作者指南](MOD_AUTHORING.md)。

玩家安装：

1. 安装官方 [BepInEx 5.4.23.5 win_x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)，
   将其内容解压到游戏 EXE 所在目录。
2. 将 `SunnyModLoader-v1.0.0.zip` 的内容解压到同一目录；Release 不重复分发 BepInEx。
3. 启动游戏，按 `F8` 打开管理器。
4. 在“安装”页粘贴 `.sunmod` / `.zip` 路径，或把包放入 `Mods/Inbox` 后点击安装。
5. 新安装的 Mod 固定为禁用；在“Mod”页启用并点击“应用到当前剧情”。

若目标游戏目录已有其他代理加载器或不同版本的 BepInEx，请先备份并确认兼容性。Loader Release
只写入 `BepInEx/plugins`、`Mods` 和文档，不覆盖 `winhttp.dll` 或 BepInEx 核心文件。

更新使用相同入口。同 ID 的旧版本会完整移入 `Mods/.sunny/backup`，更新包不会遗留旧文件。
卸载只对 Sunny 安装器管理的本地包开放，内容会移入 `Mods/.sunny/trash`，可在 F8“恢复”页找回。
Steam 工坊和额外目录为外部托管来源，Loader 只负责启停，不删除其文件。

本项目验证使用官方 BepInEx `5.4.23.5 win_x64`。官方压缩包 SHA256：

```text
82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4
```

使用 .NET 8 SDK 构建并安装插件，`GameRoot` 指向合法安装的游戏目录：

```powershell
./build.ps1 -GameRoot "D:\Games\败犬栖居的晴空日常"
```

仅构建而不安装可追加 `-NoInstall`。也可以设置环境变量 `SUNNY_GAME_ROOT` 省略每次传参。

生成可分发目录、版本化 ZIP 和示例 `.sunmod`：

```powershell
./pack.ps1 -GameRoot "D:\Games\败犬栖居的晴空日常"
```

开发工作区也可直接安装示例目录：

```powershell
./install-example.ps1 -GameRoot "D:\Games\败犬栖居的晴空日常"

```

分发包中的标准归档示例为 `ModSDK/examples/org.example.sunny-demo.sunmod`。

## Mod 目录

```text
Mods/
├─ Inbox/
│  └─ downloaded-mod.sunmod
├─ org.example.my-mod/
│  ├─ manifest.json
│  ├─ story/
│  │  └─ extra-route.txt
│  └─ assets/
│     ├─ voice/
│     ├─ music/
│     ├─ sprites/
│     ├─ cg/
│     └─ video/
└─ .sunny/
   ├─ staging/
   ├─ backup/
   └─ trash/
```

归档必须恰好包含一个 `manifest.json`，并采用以下任一布局：归档根直接放 Manifest，或所有内容都位于
同一个包装目录中。安装后始终规范化为 `Mods/<manifest.id>/manifest.json`。

`id` 必须是最多 128 字符的小写 ASCII 反向域名式标识，例如 `org.example.my-mod`；`version`
必须是 SemVer 2.0。设置和流程声明 ID 只允许最多 64 字符的 ASCII 字母、数字、点、
下划线和连字符，并须以字母或数字开头、结尾。

Manifest 最小字段：

```json
{
  "schemaVersion": 2,
  "id": "org.example.my-mod",
  "name": "My Mod",
  "version": "0.1.0",
  "compatibility": {
    "loaderApi": 2,
    "gameBuilds": ["a3183ccb0bd148b085e61979ba356743"]
  }
}
```

`manifest.json` 不允许列出剧情文件或资源。Loader 自动扫描 `story` 下的 `.txt` 和 `.sunny`；
`branches/dialoguePatches/gallery/overlays` 等内容字段会被 Manifest v2 校验直接拒绝。

流程文件使用游戏实际支持的 DSL，并可在文件头加入正式 `@` 声明。普通 `//` 注释只供作者阅读，
不参与流程控制；旧式 `// @sunny` 会被拒绝：

```text
@branch {
    id="extra-route", scene="Script/vol1"
    label="1-1", line=36

    option { id="enter" text="进入支线", enter="start", repeat=true }
    option { id="continue" text="继续原剧情", continue=true, repeat=true }
}

@gallery {
    id="extra-cg", title="支线 CG"
    image="@/assets/cg/pic.png", unlockedByDefault=true
}

label start:
background "@/assets/cg/pic.png"
character 八奈见
show 八奈见 [time="0.25"]
旁白 "这是 Mod 剧情。" [voice="@/assets/voice/line.ogg"] [voiceVolume="1"]
hide 八奈见
return
```

`@/` 从 Mod 根目录引用，`./` 从当前流程目录引用；不带前缀的路径仍表示原游戏资源。
分支、`call`、`@text/@voice/@voices`、`bgm`、`@replace`、`@sprite`、CG、Spine 边界和资源路径的完整格式见
[FLOW.md](FLOW.md)。

源码仓库的剧情示例位于 `examples/org.example.sunny-demo/`，分发包中的归档位于
`ModSDK/examples/org.example.sunny-demo.sunmod`。语音控制由 Loader 内置，不再分发独立 Mod 包。

所有 `@` 声明均支持全局 `{}` 块，字段可由空格、换行或逗号分隔。`@branch` 一次声明多个
`option {}`；Loader 不会自动补继续按钮，作者必须用 `continue=true` 显式提供。

当前示例在 `Script/vol1` 的 `1-1` 内第 36 条台词后显示“一、进入 MOD / 二、继续原剧情”。进入后加载
`assets/cg/pic.png`，播放项目自行生成的 `assets/music/route.wav`，让两个外部 Sprite 与原版八奈见 Spine 同屏，并演示外部人物换装。
真实运行解析到的原版角色 Prefab 为 `SkeletonGraphic (Role_Bajiannai)`。A 剧情中另有“进入 B 剧情 / 留在 A 剧情”，
用于验证原剧情 -> A -> B -> A -> 原剧情的两层返回栈；B 还通过 `call` 调用公共 C 流程再返回。

## 内置 VoiceControl

`VoiceControl` 是 Loader 合成的内置 Mod，不占用 `Mods` 目录，也没有需要作者安装的 Manifest 或流程文件。
它会显示在 F8 Mod 页，可启用或禁用；没有路径、优先级和卸载操作。启用后在原游戏“系统设置”的声音区域
增加“语音音量”，并在“文本设置”的对话功能区域增加“换句停止语音”。原版“操作偏好”仍留在系统页。VoiceControl
启用时，默认音量为 `1`、自动停止为关闭：新台词有语音时始终替换旧语音；新台词无语音时允许旧语音继续。
开启停止选项后，每次推进都会先停止当前语音。VoiceControl 未启用时，Loader 回退到音量 `1` 和推进即停止，
保持旧版行为。

启用 VoiceControl 时，历史记录会在确实带有 `audiopath` 的台词右侧显示播放图标。点击后按当前语音音量
重放该句；无语音台词不显示按钮。历史项用场景和脚本位置回查应用补丁后的台词，因此原版语音、内联
`voice=`、`@voice/@voices` 和 Mod 流程语音均可重放，不需要作者增加额外声明。禁用 VoiceControl 会隐藏这些按钮。

数据 Mod 的 `voice`、内联 `voice=`、`@voice` 和 `@voices` 会自动使用这两项设置。需要自己播放语音的
BepInEx 插件可探测公开 API：

```csharp
if (SunnyModAudioControl.IsAvailable)
{
    float volume = SunnyModAudioControl.VoiceVolume;
    bool stopOnAdvance = SunnyModAudioControl.StopVoiceOnAdvance;
}

SunnyModAudioControl.SettingsChanged += RefreshVoicePlayback;
```

`ProviderModId` 是 Loader 内部的稳定提供者键；第三方插件应探测 `IsAvailable`，不要依赖或显示该 ID。
即使 VoiceControl 被禁用，音量和停止属性也会返回上述安全回退值。

## 工坊配置

若发行方或第三方工坊提供 Steam AppID，在 `BepInEx/config/qm.sunny.modloader.cfg` 中设置：

```ini
[Workshop]
SteamAppIds = 123456;789012
AdditionalRoots = D:\OtherWorkshop\SunnyMods
```

Loader 会从 Steam 注册表和 `libraryfolders.vdf` 找到所有库，再只扫描指定 AppID 下的数字
PublishedFileID 目录。若未配置可信 AppID，当前非 Steam 发行包不会猜标题或扫描其他游戏的工坊。
同一 Mod ID 出现多个副本时全部禁用，并在问题页列出冲突路径。工坊 PublishedFileID 改变时会被视为
新来源并强制禁用，原订阅的就地更新则保留玩家状态。

## 稳定性规则

- 台词锚点使用 `scene + afterLabel + dialogueOrdinal + expectedSpeaker/expectedText`；省略序号时完整原文必须唯一匹配。
- 锚点始终针对未修改的原文解析，再按优先级应用；高优先级修改同一字段时获胜。
- 文本和语音原位覆盖，不改变原版脚本指令数量。
- `@voices` 可共享场景、Label、音频目录和音量；`@replace kind="Audio"` 是全局资源替换，`bgm` 是当前流程播放命令。
- VoiceControl 只由 Loader 内置包提供；数据 Mod 的 `@audioControl` 声明会被拒绝，避免第三方覆盖玩家看到的内置开关。
- 同一运行位置的可见 option 会合并；继续原剧情只能由作者显式声明 `continue=true`，Loader 不自动添加。
- 普通 option 的 `story` 和 `call` 每进入一次都压入返回帧；`return` 只弹出一层。`call` 返回下一句，branch 返回锚点。
- Mod 选择待决时保留不可手动释放的 `ScriptBlocker`，并拦截 `Continue`、`Next`、快进状态与
  `SkipToNextOption`；鼠标滚轮和快捷操作也不能打开历史记录，已经打开的历史会被关闭，只有实际选项按钮能解除阻塞。
- F8 只在窗口矩形内用透明 UI Image 阻断底层射线；从窗口内开始的鼠标手势会捕获到释放帧，
  窗口内滚轮和文本框键盘输入会拦截 `CorePlayer.Continue/Next/Update`，窗口外起始手势不施加剧情门禁。
- 每次分支进入使用唯一返回帧 ID；内存快照不会在首次返回后消费，历史回滚可再次恢复相同快照。
- 返回帧 JSON 持久化进入前的背景、音乐和人物状态；对话面板等更完整状态仍优先使用进程内快照。
- 用户启用状态和优先级保存在 BepInEx 配置中，不写回工坊包。
- 新安装和新工坊来源不会自动启用；启用中的内容更新必须在 F8 中成功重载当前剧情后才能继续游戏。
- 解包采用同卷 staging 和独占锁，拒绝 Zip Slip、绝对/UNC/ADS 路径、设备名、大小写/NFC 碰撞、
  文件目录冲突、符号链接、junction/reparse、异常压缩比和超限归档。
- 默认限额为 2 GiB 归档、10000 条目、2 GiB 单文件、8 GiB 总输出、32 层目录、256 KiB Manifest。
- 引用资源另限文本 16 MiB、纹理 256 MiB、音频 512 MiB、视频 2 GiB；PNG/JPEG 最大边长 16384，
  总像素不超过 67108864。
- 包内资源路径会做规范化、越界和逐级重解析点检查；DLL、EXE 和脚本文件不能作为资源读取。
- Mod/设置/分支状态变量使用可读前缀加稳定哈希，避免 `a-b`、`a.b`、`a_b` 相互碰撞。

## 启动诊断

诊断默认关闭。下面的命令会启用当前 Mod，执行无交互验证，并在完成后退出游戏：

```powershell
$env:SUNNY_MODLOADER_VALIDATE = "1"
$env:SUNNY_MODLOADER_VALIDATE_INSTALLER = "1"
$env:SUNNY_MODLOADER_QUIT_AFTER_VALIDATE = "1"
./败犬栖居的晴空日常.exe
Remove-Item Env:SUNNY_MODLOADER_VALIDATE, Env:SUNNY_MODLOADER_VALIDATE_INSTALLER, Env:SUNNY_MODLOADER_QUIT_AFTER_VALIDATE
```

结果写入 `BepInEx/LogOutput.log`。成功日志会包含 base scene、Mod story、branch enter/return、
texture、voice 和 `Mod installer diagnostics passed`。安装器诊断会在系统临时目录运行，不改玩家 `Mods`。

## 当前限制

- 缺失或被禁用的 Mod 若正好是存档当前场景，尚未实现自动退回父剧情。
- F8 运行时重扫对游戏已缓存的非当前场景还没有完整的版本化失效机制；当前剧情会强制重载。
- 高级 Prefab、ParticleSystem、自定义 Shader 和后处理尚未接入 AssetBundle。
- 尚未提供只修改原剧情某一次 `music` 命令的场景级锚点；当前可全局 `@replace` 或在自定义流程中 `bgm`。
- 原版已配置的 Spine 角色、皮肤和动画可以直接调用；Mod 自带全新 Spine Prefab/模型尚未接入 AssetBundle 与配置注册。
- 直接恢复整份快照会停止旧语音，但不会自动重播快照中的当前句。
- 支线中存档并重启后返回可恢复脚本位置、背景、角色和音乐；对话面板的精确打印进度仍只在进程内完整恢复。
- 运行时变量表达式、通用 `if/else`、option `when` 与索引驱动的自动 `@voicepack` 尚未实现。
- v2 使用数据 Mod，不加载第三方 DLL。

## 许可证

Sunny Mod Loader 源码采用 [MIT License](LICENSE)。游戏、Unity、BepInEx 及其他运行时依赖不属于本项目；
依赖版本与来源见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
