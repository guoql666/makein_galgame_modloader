# Sunny Mod Loader 1.0.0 验证记录

验证日期：2026-07-17
游戏 Build GUID：`a3183ccb0bd148b085e61979ba356743`

## 构建与启动

- Release 构建：`0 warnings / 0 errors`。
- 固定源码 `PathMap` 并关闭源码管理器注入后，开发目录与干净 GitHub 克隆生成的 DLL SHA256 一致：
  `FBBF308270612495CE6C5F5DB7A59074B3BC76E4ABE2AA1CB0574B8948176AFB`。
- 真实 Unity `6000.2.15f1` / Mono 诊断进程退出码：`0`；最终一轮沿用玩家保存的窗口模式，布局校验时实际画面为 `800x600`。
- 最终诊断启动：发现 2 个 Mod、启用 2 个、扫描问题 0 个；诊断结束后内置 `VoiceControl` 保持启用。
- 构建标识优先读取 Unity `Application.dataPath`；相邻目录中的无关名称不再误命中游戏 `*_Data`。
- 当前发行包没有可信 Steam AppID 时只记录 info，不扫描其他游戏工坊。

## 剧情与资源

- `Script/vol1`：解析 3880 条命令。
- 外部 A / B / 公共 C 剧情：分别解析 23 / 5 / 4 条命令。
- Manifest v2 / Loader API 2：元信息与运行时流程模型完全分离；2 个 branch 组展开为 4 个显式 option，并生成 1 个 CG、2 个 Sprite。
- 通用声明块支持空格、换行、逗号混合分隔；`@gallery` 的开括号与声明头分行也通过真实扫描。
- `.txt` 与 `.sunny` 流程后缀均通过安装扫描；普通注释不参与流程控制，旧式 `// @sunny` 和 API 3 单行 branch 被拒绝。
- `@text`、`@voice`、`@voices`、`@replace`、`@sprite`、`bgm/stopbgm` 与 `call` 均通过有效包安装校验。
- 省略 `line` 的唯一原文定位实际应用 1 条台词补丁，结果 `1 passed / 0 failed`。
- 资源简写：`@/assets/cg/pic.png`、内联 `voice` 与 `return` 均在解析前展开成功。
- 精确锚点：原剧情触发索引 `Script/vol1#52`；A 内部嵌套选择触发索引 `extra-route.txt#16`。
- 返回栈 JSON：通过。
- 单层分支进入/返回：通过 2 次，失败 0 次。
- 两层嵌套返回：`原剧情 -> A -> B -> A -> 原剧情` 通过 1 次，失败 0 次，返回栈深度按 `1 -> 2 -> 1 -> 0` 变化。
- 无条件 `call`：B -> C -> B 往返通过 1 次，失败 0 次，返回后从 `call` 下一句继续且不触发 branch bypass。
- Loader 不再自动添加“继续原剧情”；示例中的两个继续按钮均来自作者显式 `continue=true` option。
- CG：项目程序化生成的 `1280x720` PNG 解码通过，不含游戏或第三方美术资源。
- 音频：项目程序化生成的 `route.wav` BGM 以 `176400 samples / 44100 Hz` 解码，`voice.wav` 以
  `52920 samples / 44100 Hz` 解码，不含第三方音频素材。
- 既有真实窗口回归确认 A 依次显示 5 条文本；新增 B 含 2 条文本，其 B -> A -> 原剧情链路由自动诊断验证。
- 合法 1x1 JPEG、`.sunny` 流程和台词/语音语法糖包安装验证通过。

## 屏幕特效

- `effect flash` 已在真实 `1920x1080` 游戏客户区触发；遮罩保持阶段按 8 像素步长采样，近纯白像素占比为
  `100%`，淡出完成后恢复为 `2.77%`，随后流程正常继续到下一句。
- 实测使用延长保持时间抓取瞬时画面，完成后已恢复示例的正式快速时序
  `in=0.03 / hold=0.02 / out=0.12` 和原第 36 句入口。
- 启动诊断通过屏幕特效内部 URI 的完整编解码；安装器接受合法闪白语法，并拒绝错误颜色、未知属性和
  超过 30 秒的特效命令。
- 全屏遮罩使用不接收射线的 Overlay Canvas；阻塞模式结束后释放流程，快进、切换流程、读档、历史回滚和
  Loader 卸载均会取消活动特效并清除遮罩。

## 语音控制

- Loader 注册表会合成唯一的内置 `VoiceControl`，其资源根为空且不依赖 `Mods` 目录；外部 `@audioControl` 声明和保留 ID 包均被拒绝。
- 原版系统页声音区域成功注入“语音音量”，文本页对话功能区域成功注入“换句停止语音”；
  原版“操作偏好”未移动，配置各 1 份，开关选项恰好 2 个。
- 原版 Canvas 使用 `ScaleWithScreenSize`、参考分辨率 `1920x1080`、`matchWidthOrHeight=0`；新增控件沿用原控件锚点和实际行距，不依赖测试设备的绝对屏幕坐标。
- 最新代码的真实窗口截图覆盖 `800x600` 系统页与文本页；另在 `1280x720`、`1280x800` 运行完整几何诊断，均无标题、滑块、选项、Mask 或底部导航重叠。
- 手工页面保存原版控件的基线位置；设置页重开、UI 重建或 `Screen` 尺寸变化时按基线重算，禁用控制 Mod 时恢复原版位置，不再依赖永久“已布局”标记。
- 最终 `800x600` 启动诊断在临时分辨率应用后执行，确认 `placement=True`、无同层控件重叠、无 Mask 裁切且布局重复应用稳定；语音滑块和自动停止开关分别留在系统设置与文本设置。
- 可见窗口诊断结束前恢复原偏好值和实际窗口状态；第二次 `800x600` 系统/文本页回归前后，窗口模式、分辨率和 Unity Screenmanager 注册表值完全一致。
- 内置 `VoiceControl` 主动重建一次后 `rebuildStable=True`：同一定义不重复销毁控件，禁用时会恢复原版基线并清理注入绑定。
- 克隆原版滑块会在调用 `SettingSlider.Init` 前清除模板监听；打开系统设置后的诊断确认语音值保持 `1`，不会再被模板值 `0.25` 覆盖。
- 默认启用态公开 API：`provider=qm.sunny.voice-control, volume=1, stopOnAdvance=False, data=True, ui=True`。
- 临时禁用内置 `VoiceControl` 的启动验证：未注入控件，公开 API 回退为 `volume=1, stopOnAdvance=True`；随后已恢复启用。
- F8 仅切换内置 `VoiceControl` 时直接保存并重建音频服务，不限制当前是否处于 Mod 剧情，也不重载当前场景脚本；数据 Mod 改动仍沿用快照重载路径。
- 历史记录按 `sceneName + scriptIndexAfter` 回查应用 Mod 补丁后的 `audiopath`；原版语音、内联 `voice=`、`@voice/@voices` 与 Mod 流程语音共用同一重放链路。
- 真实历史面板中，5 条混合测试记录只有 3 条有语音项显示右侧播放按钮；按钮点击区域与原版 `Jump` 同为 `28.9x30.0`，在条目 `609.1x79.7` 内左右镜像对齐、无底框，正文保持原版宽度和换行。
- 真实点击历史播放按钮的既有回归通过；1.0.0 启动诊断进一步确认
  `mod://org.example.sunny-demo/assets/voice/voice.wav` 可按 VoiceControl 音量解码和播放。
- 有语音的新台词始终替换旧语音；无语音台词只在停止策略开启时终止当前语音；设置变化会即时刷新正在播放的音量。
- 旧 BepInEx `[Audio] VoiceVolume` 已停止使用并从当前配置清理，音量来源统一为原版设置页数据或公开 API 回退值。

## 动态人物

- 发行包使用 Spine (`spine-unity` / `spine-csharp`)，不是 Live2D。
- 原版 `CharacterSpineConfig` 从 `Resources/CharacterSpineConfigs` 加载并引用 Spine Prefab。
- 原版已配置角色可继续使用 `character/show/hide/move/scale/rotate/animation`，包括皮肤、表情动画和已有 Animator 状态。
- 示例流程使用 `character/show/hide 八奈见`；启动诊断将其解析到原版 Prefab
  `SkeletonGraphic (Role_Bajiannai)`。
- 真实画面进入示例支线后，八奈见 Spine 角色正常显示在 Mod CG 上；该验证不是仅检查脚本能否解析。
- 松散资源无法注册全新 Spine Prefab；该能力仍依赖后续 AssetBundle 与配置注入。
- `@sprite` 松散 PNG/JPEG 会注册为原版 `CharacterConfig`；2 个配置的底图映射、资源解码和命名空间均通过。
  启动场景未加载 `CharacterPanel`，因此 Prefab 实例化仍由进入剧情后的真实流程执行。
- 示例包含两个第三方 Sprite 与原版 Spine 同屏流程；`$id` 不要求预先写 `character`，后者仅用于切换 base/emotion。

## F8 窗口输入

- 管理器不取得 `PlayerInput` authority，不暂停播放器，也不停止自动播放或快进。
- 透明射线遮挡只覆盖可拖动的窗口矩形，不再覆盖全屏。
- 从窗口内开始的鼠标手势捕获到释放帧；窗口外开始后拖入的手势仍归游戏处理。
- 文本框只有取得明确键盘焦点时才拦截键盘输入，点击窗口外会释放该焦点。
- F8 关闭后在下一次 IMGUI 帧显式清除真实焦点；分辨率变化后重开会先按当前 `Screen` 归一化窗口矩形，再创建射线遮挡，不使用旧尺寸首帧。
- 启动诊断确认窗口内输入被拦截、窗口外输入与窗口内空闲状态均不受管理器门禁影响。
- 真实全屏回归：点击窗口覆盖的“新的游戏”没有穿透；点击窗口外“游戏设置”正常打开原游戏设置。
- 剧情中拖动窗口后，点击旧窗口区域可推进到下一句，点击新窗口内部保持该句不变。

## 分支阻塞与回滚

- Mod 选择肢使用不可手动释放的游戏原生 `ScriptBlocker`。
- 待选择状态下 `Continue`、`Next`、Ctrl 快进、瞬时跳过与“跳到下一选项”均被统一门禁阻断。
- 待选择状态下鼠标滚轮、快捷操作按钮和测试热键均不能打开历史记录；已经打开的历史面板会被主动关闭，历史回滚点击也被拒绝。真实 `BackLogPanel` 往返诊断结果为 `choiceGuard=True`。
- 启动诊断验证选择待决时不能恢复播放，释放实际选项后才能继续。
- 返回帧包含唯一 `frameId`，同一运行时快照连续查询两次均成功，不再在首次返回后消费。
- 返回帧 JSON 已覆盖 call 标记、背景、BGM 和人物状态的序列化往返。

## 作者工具

- 普通 `manifest + story + assets` 数据 Mod 不需要运行 Loader 的 `build.ps1`；从零指南为 `MOD_AUTHORING.md`。
- `new-mod.ps1` 已验证生成无 BOM UTF-8 Manifest、最小流程文件和 voice/music/images/sprites 资源目录。
- `pack-mod.ps1` 已验证根级 Manifest 归档、源/解包 SHA256 一致、重复输出拒绝与 `-Force` 覆盖。
- 两个脚本均使用 Manifest schema 同款反向域名 ID 和 SemVer 2.0 校验，并拒绝连续空域名段、大写 ID、空名称、错误 schema/API、嵌套第二 Manifest 与输出路径逃逸。
- 合法预发布版本 `1.2.3-alpha.1+build.5` 打包通过；所有恶意失败用例均未提前创建输出目录。
- 完整 Loader 包会把作者指南放在发行根和 `ModSDK/`，并在 `ModSDK/` 分发脚手架、单 Mod 打包器与最小模板。
- `build.ps1`、`pack.ps1` 和示例安装器支持显式 `GameRoot`；Windows PowerShell 5 不再依赖中文游戏目录
  字面量，而是唯一定位包含 `Assembly-CSharp.dll` 的 `*_Data/Managed`。
- Release ZIP 不捆绑 BepInEx、Doorstop、Harmony 或游戏程序集，只包含 Loader、SDK、MIT License 和
  第三方依赖说明。

## 安装器

安装器诊断结果：`2 passed / 0 failed`。

生命周期覆盖：外部路径安装、Inbox 安装、根/单包装布局、同 ID 更新清除旧文件、故障注入回滚、
备份恢复与清理、卸载/回收区恢复、非托管目录拒绝。

恶意归档拒绝矩阵共 34 类：

- `../` 与反斜杠 Zip Slip
- 绝对、盘符与 ADS 路径
- 空组件与尾点路径
- 大小写与 Unicode NFC 碰撞
- 文件/目录前缀冲突
- Unix symlink 与 DOS reparse
- 超压缩比
- 超大 Manifest
- 超限纹理尺寸
- 流程资源 `@/../` 越界
- Manifest v1、缺失 `schemaVersion`、错误 `loaderApi` 与 Manifest 内容字段
- Manifest 嵌套未知字段
- 语义注释、流程未知字段、无序号也无原文的语音锚点、API 3 旧 branch 语法与正文后的声明
- 外部 `@audioControl` 声明与 Loader 内置保留 Mod ID
- 重复 Label 与缺失入口 Label
- 超深路径
- 超条目数

## Steam 工坊

以本机真实 AppID `227300` 回归：

- `appworkshop_227300.acf` 已安装条目：13
- 数字内容目录：13
- `Discover()` 返回：13
- 漏报/额外返回：0 / 0
- `libraryfolders.vdf` / ACF 解析条目：40 / 153，未触发 100000 条安全上限

## 最终指纹

```text
C337F201515EF65624B1B7BC2D319C8060DAA9D9412DBA971176F9FB8AAF2BDC  SunnyModLoader.dll
61C72DE89EB80A6CBBA01A225B778892B58CA49F7D27C8FD7FCF762231D9F5A8  Assembly-CSharp.dll
9319F9537A6DE1BEEA53ECF75D3458F9B0712EE53CC841FA940485D1A7C25389  org.example.sunny-demo.sunmod
```

`Assembly-CSharp.dll` 与分析开始时一致，Loader 没有修改原版程序集。
