# 发行包分析与 Mod 架构基线

## 发行包指纹

| 项目 | 结果 |
| --- | --- |
| 平台 | Windows x64 |
| Unity | `6000.2.15f1` |
| 脚本后端 | Mono, CLR `4.0.30319.42000` |
| `Assembly-CSharp.dll` | 未混淆，可直接读取类型与方法元数据 |
| Assembly SHA256 | `61C72DE89EB80A6CBBA01A225B778892B58CA49F7D27C8FD7FCF762231D9F5A8` |
| Assembly MVID | `de4005e4-11b9-4cdd-9ef3-62165fbd3259` |
| Build GUID | `a3183ccb0bd148b085e61979ba356743` |
| 主剧情 | `Entry -> vol1 -> vol2 -> vol3` |
| 剧本格式 | Resources 中的 UTF-8 文本 DSL，共发现 78 个 Label |
| 原生工坊/Mod SDK | 未发现 |

BepInEx `5.4.23.5 win_x64`、`netstandard2.1` 插件和全部 Harmony 目标已在真实发行包中启动验证。

## 关键运行面

| 需求 | 原游戏入口 | Loader 策略 | 当前验证 |
| --- | --- | --- | --- |
| 修改台词/注入语音 | `SceneScriptsReader`, `Dialogue`, `GalAssetLoader` | 支持 Label+序号或唯一原文锚点；`@voices` 批量生成语音补丁 | 批量补丁应用 1/0；程序化 WAV 语音以 52920 samples / 44100 Hz 解码 |
| 新分支/新剧情 | `CommandManager`, `CorePlayer`, `OptionPanel`, `SwitchScene` | 自动发现流程；分支组显式声明多个 option；`call` 无条件子流程；栈式返回 | branch 2 条、嵌套返回 1 条、call 往返 1 条全部通过 |
| 设置控制分支 | BepInEx Config, `CorePlayer.variables` | Manifest bool setting 控制流程声明的分支显示，并同步到剧情变量 | 首次配置正确生成 setting；示例支线可见性验证通过 |
| 全局 Mod 语音控制 | `UserPreferenceManager`, `SettingManager`, `PageMgr`, `BackLogPanel` | Loader 合成内置 VoiceControl 包，注入双设置页；历史项按脚本位置回查语音并提供重放按钮；公开 API 提供音量、推进停止策略和变更事件 | 内置注册、双页面归属、历史实图/播放、运行时启停和回退策略纳入诊断 |
| 新 CG/视觉表现 | `GalAssetLoader`, `CharacterIllustrationManager`, `CharacterPanel` | CG、背景、`@sprite` 静态人物、`bgm`、统一 `@replace` | CG、2 个 Sprite、BGM/语音均真实解码通过 |

## 已确认的 DSL 视觉能力

- `background`：支持 `None/Fade/Black` 过渡、时间、隐藏角色、初始移动和缩放。
- `background move` / `background scale`：支持 tween、等待和 Ease。
- 角色：`show/hide/character/move/scale/rotate/animation`；本体使用 Spine，不是 Live2D。
- 视频：本地 `mp4/webm/mov`，非循环视频可阻塞剧情，循环视频由 `endvideo` 停止。
- UI：`fullscreen/endfullscreen`、`showui/closeui`、`unlockcg`。

原版 Spine 角色由 `Resources/CharacterSpineConfigs` 中的 `CharacterSpineConfig` 绑定 Spine Prefab；现有
数据 Mod 可调用原角色的皮肤、表情动画和 Animator 状态，但不能用松散资源注册全新 Spine Prefab。
Loader 现可把松散 PNG/JPEG 注入 `CharacterConfig`，复用原版静态人物渲染、快照和头像链路；
仍没有任意 alpha/tint、camera shake、Prefab、ParticleSystem、Shader、Volume 或后处理命令。

## 数据 Mod 设计

Manifest v2 / Loader API 2 刻意不允许 Mod 包携带 DLL。Manifest 只保存元信息与用户设置；自动发现的流程 DSL 负责剧情、
分支、语音和资源引用。两者与资源都经过 Loader 解释，带来以下边界：

- 原版二进制和资源包保持不变，更新/卸载时只需移除补丁文件。
- 每个坏包独立失败，不会让 Chainloader 或其他 Mod 停止启动。
- 资源只允许留在包根目录内，并拒绝路径穿越、重解析点和可执行扩展名。
- Steam 工坊以可信 AppID 定位，额外工坊可用 `Workshop.AdditionalRoots`；两类外部包均只读加载。
- 冲突按用户优先级处理，数值越高越晚应用；同优先级按 Mod ID 稳定排序。
- 流程中的 `@/`、`./` 和 `../` 会在解析前展开为包内 URI，并在安装阶段检查类型、大小和越界。
- Release 将个人导出的正常流程参考复制为根目录 `Script/`，同时提供台词定位和资源哈希索引；该目录仅供
  作者查找原流程锚点，Loader 运行时仍从游戏资源读取场景，不会把参考文本装入 Mod。
- Manifest 与流程内容使用独立运行时模型；v1 Manifest、内容字段和作者手写 `mod://` URI 均直接拒绝。
- 流程声明使用通用块 AST；`@branch/@dialogue/@text/@voice/@voices/@sprite/@gallery/@replace` 都支持 `{}`，字段可由空格、
  换行或逗号分隔。`@branch` 顶层保存共同锚点，多个 `option {}` 展开为稳定运行时选项。未被脚本直接引用的
  声明 ID 可省略，并由场景锚点、流程目标或资源目标生成确定性内部 ID；Sprite、设置和 Mod ID 仍显式声明。
- 台词补丁可省略序号并按 Label 内唯一的原始 `speaker + text` 定位；`@voices` 复用场景、Label、目录和音量，
  再展开成普通 `DialoguePatchDefinition`，运行时不需要第二套配音逻辑。
- `bgm/stopbgm` 编译为原版音乐命令并展开 Mod URI；`@replace kind="Audio"` 才是全局原资源替换。
- VoiceControl 是 Loader 合成的内置 `ModPackage`，不依赖磁盘 Manifest；它向系统页声音区域与文本页对话功能区域
  分别注入控件，通过 `SunnyModAudioControl` 提供可探测的只读状态与变更事件，并按历史项的场景/脚本位置
  回查补丁后的 `audiopath`。历史存档模型无需增加 Loader 私有字段，旧历史也可按位置恢复重放能力。
- `load` 是尾调用式单向切换，`call` 是无条件压栈调用，branch option 是玩家选择后的压栈调用。
- 返回帧持久化背景、音乐和人物纯数据；同进程仍优先恢复完整 `GameStateSnapshot`。
- Loader 不自动生成“继续原剧情”；`continue=true`、目标 `story`、入口 Label、设置条件和重复策略都属于显式 option。
- 普通注释无语义；旧式 `// @sunny`、Loader API 3 旧 branch 写法、未知字段、重复 Label 和不存在的跨文件
  入口 Label 在安装阶段失败。

流程层现在按四个阶段组织：声明收集与通用块语法解析、别名归一化与语义模型生成、全部流程读取后的
跨文件 Label/资源校验、运行时注册。作者看到的 branch 组会在语义阶段展开为 `BranchOptionDefinition`；
运行时只处理扁平 option，因此 UI、条件判断和返回栈不需要理解源码嵌套结构。这个边界允许以后为其他
`@` 声明增加子块，同时避免把解析器细节泄漏进游戏补丁层。声明块深度、单组 option 数和运行时返回
栈均有硬上限，错误递归不会无限消耗调用栈或存档变量。

## 安装与工坊边界

玩家归档入口支持 `.sunmod` 和 `.zip`。安装器在写入活动 Mod 目录前依次执行：

1. 以 `Mods/.sunny/install.lock` 独占串行化文件操作，并预检 ZIP EOCD、中央目录大小和条目数。
2. 校验根 Manifest/单包装目录布局、Windows 路径、Unicode NFC+大小写碰撞、文件目录前缀冲突、
   Unix 文件类型和 DOS reparse 属性。
3. 在 `Mods/.sunny/staging/<guid>` 手工流式解压，以实际字节再次执行单文件/总量限制并核对 CRC32。
4. 使用与运行时相同的 `ManifestValidator` 校验 ID、SemVer、Build、引用和资源。
5. 更新时先把旧目录移入 `backup`，再将完整 staging 目录同卷重命名到规范目标；异常会回滚旧目录。
6. 卸载只移动带有效 `.sunny-installed.json` 的托管包到 `trash`，恢复前重新验证整棵目录和 Manifest。

默认拒绝超过 2 GiB 的归档、10000 条目、2 GiB 单文件、8 GiB 总输出、32 层目录、256 KiB Manifest，
以及超过 `200:1 + 1 MiB` 宽限的压缩项。包不能覆盖手动目录，也不能携带安装器保留的托管标记。

Steam AppID 信任顺序为显式 `Workshop.SteamAppIds`、`SteamAppId` 环境变量、`steam_appid.txt`、
游戏根与 `appmanifest` 安装目录的精确匹配。库来自显式 `SteamRoots`、Valve 注册表和
`libraryfolders.vdf`。探测不会按标题猜测，也不会扫描全部 AppID；从库根到 PublishedFileID 的每级
目录都拒绝重解析点。来源指纹包含 AppID/PublishedFileID，来源改变会强制禁用，同一订阅就地更新保留状态。

## 运行验证记录

最终候选运行了默认关闭的启动诊断，关键结果：

```text
Startup validation parsed base scene Script/vol1 with 3880 command(s).
Startup validation parsed Mod story mod://org.example.sunny-demo/story/called-scene.sunny with 4 command(s).
Startup validation parsed Mod story mod://org.example.sunny-demo/story/extra-route.txt with 23 command(s).
Startup validation parsed Mod story mod://org.example.sunny-demo/story/nested-route.sunny with 5 command(s).
Startup validation resolved existing Spine character 八奈见 to prefab SkeletonGraphic (Role_Bajiannai).
Startup validation dialogue patches completed: 1 passed, 0 failed.
Startup validation passed the Mod return-stack JSON round trip.
Startup validation passed branch enter/return org.example.sunny-demo:opening-demo-route.enter-demo at Script/vol1#52.
Startup validation passed branch enter/return org.example.sunny-demo:visit-nested-route.enter-b at .../extra-route.txt#16.
Startup validation branch round trips completed: 2 passed, 0 failed.
Startup validation passed nested branch base -> A -> B -> A -> base.
Startup validation nested branch round trips completed: 1 passed, 0 failed.
Startup validation call round trips completed: 1 passed, 0 failed.
Startup validation passed 2 external sprite character config(s).
Startup validation decoded Mod texture .../pic.png (1280x720).
Startup validation decoded Mod texture .../guest-a.png (600x900).
Startup validation decoded Mod texture .../guest-b.png (600x900).
Startup validation decoded Mod audio .../route.wav (176400 samples at 44100 Hz).
Startup validation decoded Mod audio .../voice.wav (52920 samples at 44100 Hz).
```

窗口级回归从“新的游戏”推进到锚点，确认两项选择、原版八奈见 Spine 立绘、5 条外部文本、CG、第三句语音资源绑定及
`return`。返回后家庭餐厅背景、八奈见立绘和下一句“啊啊，还是这么做了啊”均恢复。
进入示例支线的真实画面确认 `character/show 八奈见` 会在 Mod CG 上显示原版动态角色，而不只是通过文本解析。
F8 改为非模态窗口：窗口矩形内有局部射线遮挡与当帧推进门禁，窗口外不取得 `PlayerInput` authority、
不暂停播放器，也不改变自动播放或快进状态。真实窗口回归确认窗口内点击不推进、窗口外点击正常推进，
拖动后旧区域立即恢复游戏输入而新窗口区域继续阻断穿透。

另用损坏包验证隔离行为。Loader 精确拒绝 v1 Manifest、Manifest 内容字段、嵌套未知字段、语义注释、
流程未知字段、旧 branch 语法、正文后的声明、重复 Label 和缺失入口 Label，并继续发现和验证合法示例 Mod。

归档安装诊断另覆盖根/包装布局、新装、同 ID 更新清除旧文件、故障注入回滚、备份恢复、卸载恢复，
以及 Zip Slip、绝对路径、盘符/ADS、空组件、尾点、大小写/NFC 碰撞、文件目录冲突、Unix symlink、
DOS reparse、压缩比炸弹、超大 Manifest、超限纹理尺寸、深度和条目数超限。隔离测试结果为
`2 passed, 0 failed`，其中恶意归档矩阵包含 31 类拒绝子用例；外部 `@audioControl` 和 Loader 内置保留 ID
均被明确拒绝。

## VFX v2 路线

AssetBundle SDK 必须锁定以下环境，不能只写“Unity 6”：

- Unity `6000.2.15f1`
- `BuildTarget.StandaloneWindows64`
- URP/Core/ShaderGraph `17.2.0`
- D3D11 + D3D12，至少以发行包当前实际使用的 D3D11 做验收
- 单 Mod 单 Bundle，LZ4，无跨 Bundle 依赖

首版建议只支持 `GameObject Prefab + ParticleSystem + Animator + Material/Shader`。Bundle 不携带第三方 C#，
否则 Prefab 会出现 Missing Script。VFX Graph、RendererFeature 和全局后处理应继续后置。

## 尚未关闭的风险

1. Mod 存档的完整进度位于同名 `.sunny-mod.json` 辅助文件；只复制主 JSON 时仍能回到原版剧情，
   但不能恢复 Mod 内进度。
2. 运行时重扫会涉及 `CorePlayer` 已缓存场景，后续应加入 Loader revision 和全量 cache invalidation。
3. 快照直接恢复不会重新播放当前句语音。
4. 旧存档迁移会原样保留旧 JSON，并在所需 Mod 再次可用时通过原版读取器重建压缩历史；长期仍应为
   Loader 辅助存档定义独立于游戏私有 JSON 结构的迁移策略。
5. 当前锚点机制依赖 Label 唯一性，尚未提供重复 Label 的 occurrence 选择器。
6. AssetBundle VFX 与自定义 Spine Prefab 需要定义可序列化状态和配置注册，覆盖正常播放、逻辑快进、
   回滚、读档和禁用 Mod 的生命周期。
7. 当前更新采用同卷目录交换并保留完整 backup；若进程恰在两次重命名之间被强制终止，旧版本不会丢失，
   但仍可能需要从 F8 恢复页手工恢复，尚未实现持久事务日志的自动启动修复。
8. Loader ZIP 只分发插件与 SDK，不捆绑或覆盖 BepInEx、Doorstop 与代理 DLL；玩家必须先安装已验证的
   BepInEx `5.4.23.5 win_x64`。Loader 文件本身仍需由玩家手工覆盖或移除。
