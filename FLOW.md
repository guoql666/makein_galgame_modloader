# Sunny 流程文件

Loader 会自动发现 Mod 包 `story` 目录下的 UTF-8 `.txt` 和 `.sunny` 文件。两种后缀行为完全相同，
作者可按习惯选择。Manifest 只描述包身份、兼容性、默认启用状态、用户设置和 Mod 关系；剧情、补丁和资源播放
都写在流程文件中。

## Mod 关系

Manifest 可使用 `dependencies` 和 `conflicts` 声明 Mod 之间的运行关系：

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

依赖必须存在、启用且满足版本范围；依赖链会在加载顺序中先加载，被依赖 Mod 失败会连带阻止依赖者。
显式冲突命中时双方均阻止进入运行时。版本范围支持精确版本、`=`、`>`、`>=`、`<`、`<=`，多个条件用
空格或逗号表示 AND，省略版本表示任意版本。缺失依赖、版本不符、循环依赖和显式冲突在 F8 问题页显示为错误。

覆盖冲突按字段和资源槽位判断，而不是按“同一句台词”笼统判断。只有同一锚点的文本对文本、语音对语音，
或同一资源类型与目标路径的 `@replace` 对 `@replace` 才会显示警告；文本、语音、人物动画之间互不冲突，
同一 Branch 位置的多个 option 也会正常合并。发生同一字段覆盖时，F8 会显示两个 Mod 和最终加载顺序中的生效者。

流程声明使用以 `@` 开头的正式语法。Loader 在把文本交给原游戏 DSL 前解析并移除这些声明。
普通 `//` 注释只供作者阅读，删除注释不会改变流程。旧式 `// @sunny ...` 会被明确拒绝。

所有 `@` 声明必须位于第一个 DSL 语句之前。字段可用空格、换行或逗号分隔；三者可混用。
字段值包含空格、逗号或花括号时必须使用双引号。

所有声明都支持 `{}`，开括号可与声明头同一行，也可另起一行。普通单行声明仍可用于没有子块的
`@text/@voice/@dialogue/@gallery/@overlay`：

```text
@gallery
{
    title="支线 CG"
    image="@/assets/cg/extra.png",
    unlockedByDefault=true
}
```

块内可写普通 `//` 注释。字段不能重复；未知字段、未知子块和未闭合花括号都会使整个 Mod 在扫描时失败。

## ID 规则

并非每个结构都需要作者命名。`@branch`、`option`、`@text/@voice/@dialogue`、`@voices/line`、
`@gallery` 和 `@replace/@overlay` 的 `id` 均可省略；Loader 会根据场景锚点、流程目标或资源目标生成
确定性的内部 ID。在解析后的场景和目标不变时，调整声明顺序或格式、修改选项显示文字或更换补丁源文件
不会影响由相同语义生成的 ID。

以下 ID 仍然必填：Manifest 的 Mod `id`、Manifest 设置 `id`、`@sprite` 的 `id`，以及 Sprite
`base/emotion` 的 `id`。这些名称会被配置项、`$spriteId` 或人物状态直接引用，无法匿名。

显式 `id` 主要用于给持久状态一个长期不变的名字。发布后若要修改非重复 branch 的锚点或 option 目标，
但希望旧存档继续识别“已经选择过”，应提前为它们填写固定 ID。Gallery 解锁键同理。两个匿名声明如果
具有完全相同的身份语义会被视为重复；确实需要并存时请显式命名。

当前没有通用的“按 ID 修改另一个声明”命令：原资源仍按逻辑路径替换，原台词仍按场景和台词锚点定位。
因此不需要上述稳定身份时，不应为了满足格式而编造 ID。

## 分支声明

`@branch` 表示一个选择组，顶层只描述共同锚点；每个 `option {}` 表示一个实际按钮。一个 branch
必须显式声明一个或多个 option，Loader 不会自动补“继续原剧情”。

在原剧情第 36 条台词后提供两个选项：

```text
@branch {
    scene="Script/vol1"
    label="1-1", line=36
    speaker="旁白"
    expect="原台词"

    option {
        text="进入支线"
        enter="start", condition="enableRoute", repeat=true
    }

    option {
        text="继续原剧情"
        continue=true, repeat=true
    }
}
```

顶层字段：

- `id`：可选的选择组稳定 ID；省略时按场景和锚点自动生成。
- `scene`：被拦截的原版或 Mod 流程。缺省时是当前声明文件，适合在 A 剧情内继续声明分支。
- `label/afterLabel`：锚点 Label。
- `line/dialogueOrdinal`：Label 内从 0 开始的台词序号；缺省时在 Label 后立即触发。
- `speaker/expectedSpeaker`、`expect/expectedText`：可选的原文校验，防止游戏更新后锚点漂移。

option 字段：

- `id`：可选的选项稳定 ID；省略时按目标流程、入口和设置条件自动生成。
- `text`：玩家看到的文字，必填。
- `story`：进入的 Mod 流程文件，使用 `@/` 或相对路径；缺省时进入声明所在文件。
- `enter/entryLabel`：目标流程入口 Label；缺省时从目标文件开头执行。
- `continue=true`：继续当前被拦截的剧情；不能同时写 `story` 或 `enter`。
- `condition/setting`、`invert/invertSetting`：按 Manifest bool setting 控制该选项是否显示。
- `repeat/repeatable`：是否可重复选择，缺省为 `false`。该状态属于单个 option，不属于整个 branch。

内部运行时 ID 为 `branchId.optionId`，两部分都可能由 Loader 自动生成。同一锚点可以有多个 branch，Loader 会按 Mod 优先级和声明顺序
合并所有当前可见 option。如果当前没有任何可见 option，原剧情不会被拦截。

没有 `continue=true` 时，只要仍有可见 option，玩家就必须选择作者给出的剧情入口；Loader 不会提供逃生按钮。

## 嵌套流程

A 剧情可调用 B，B 的 `return` 只返回 A；A 最后的 `return` 再返回原剧情：

```text
// 位于 A 文件头；scene 缺省，所以锚定当前 A 流程。
@branch {
    id="a-to-b"
    label="choose-b"

    option {
        id="visit-b", text="进入 B 剧情"
        story="./b.sunny", enter="start", repeat=true
    }

    option {
        id="stay-a", text="留在 A 剧情"
        continue=true, repeat=true
    }
}

label start:
旁白 "A 剧情开始。"

label choose-b:
旁白 "从 B 返回后会继续这里。"
return
```

`b.sunny`：

```text
label start:
旁白 "这里是 B。"
return
```

每次普通 option 进入目标流程都会压入一层返回帧，保存父场景、命令位置和运行时快照，因此支持
原剧情 -> A -> B -> A -> 原剧情，也支持更深层级。循环 A -> B -> A 指的是 B `return` 回到尚未结束的
A；不要在 B 中重新 `story="./a.sunny"`，后者会再压入新帧并形成真正的递归调用。

安全上限为：声明块嵌套 16 层、单个 branch 64 个 option、运行时返回栈 64 层。达到上限会拒绝声明
或停止继续压栈，避免错误递归耗尽运行时。

## 原版台词补丁

Release 的 `Script/` 目录提供 `Entry.txt`、`vol1.txt`、`vol2.txt`、`vol3.txt` 和 `dialogue-index.tsv`。
参考文件 `Script/vol1.txt` 对应声明中的 `scene="Script/vol1"`，不要把 `.txt` 写进场景路径。

按序号定位的最低字段为 `scene + label + line`；稳健写法是再提供 `speaker + expect` 校验原文。
不写 `line` 时必须提供 `text/expect`，并保证完整原文在该 Label 内唯一。索引的 `id` 不能作为定位字段。

同时修改原版台词和语音：

```text
@dialogue scene="Script/vol1" label="1-1" line=3 speaker="旁白" expect="原台词" value="新台词" source="@/assets/voice/new.ogg" volume=1
```

只修改文本可使用 `@text`：

```text
@text scene="Script/vol1" label="1-1" line=3 speaker="旁白" expect="原台词" value="新台词"
```

只添加或替换语音可使用 `@voice`：

```text
@voice scene="Script/vol1" label="1-1" line=3 speaker="旁白" expect="原台词" source="@/assets/voice/new.ogg" volume=0.9
```

如果原文在指定 Label 内唯一，可以省略 `line`：

```text
@voice scene="Script/vol1" label="1-1" speaker="八奈见" text="这句原台词只出现一次" source="@/assets/voice/001.ogg"
```

Loader 会使用未被其他 Mod 修改前的说话人和完整原文匹配。匹配 0 条会失败；匹配多条也会失败，并要求
补上从 0 开始的 `line`。单条 `@voice` 中 `text` 是 `expect` 的别名；推荐始终写
`speaker + text/expect`，重复台词再加 `line`。

大量配音可用 `@voices` 共享场景、Label、目录和默认音量：

```text
@voices {
    scene="Script/vol1"
    label="1-1"
    directory="@/assets/voice/vol1/1-1"
    volume=1

    line {
        speaker="八奈见"
        text="第一句原台词"
        file="bajian-001.ogg"
    }

    line {
        line=7
        speaker="小式"
        text="这句文本在当前 Label 中重复，所以同时写序号"
        file="xiaoshi-002.ogg"
        volume=0.9
    }

    line {
        speaker="八奈见"
        text="也可以不用 directory/file 组合"
        source="@/assets/shared/special.ogg"
    }
}
```

`@voices` 的 `scene` 缺省时是声明所在流程文件。顶层和每个 `line` 的 `id` 都可省略。每个 `line` 必须二选一：
`file` 相对于顶层 `directory`，或 `source` 使用完整 Mod 资源引用。顶层最多包含 256 条 line。

`@dialogue` 中的 `value/source` 分别是 `text/voice` 的简写。补丁只修改匹配台词的运行时参数，
不改原游戏文件，也不改变原脚本命令数量。当前不能修改说话人 ID，也没有专门的清除原语音指令。

## 图鉴与资源覆盖

追加 CG 图鉴：

```text
@gallery title="支线 CG" image="@/assets/cg/extra.png" thumbnail="@/assets/cg/extra-thumb.png" unlockedByDefault=true
```

覆盖原游戏逻辑资源：

```text
@overlay kind="Texture" target="BGD/association1.png" source="@/assets/bg/association1.png"
```

`kind` 可为 `Text`、`Texture`、`Audio` 或 `Video`。Manifest v2 不接受任何剧情或资源内容字段。

替换原背景 BGM 使用统一资源替换声明 `@replace`：

```text
@replace {
    kind="Audio"
    target="Music/School.mp3"
    source="@/assets/music/school-remix.ogg"
}
```

`target` 是原游戏 BGM 的逻辑路径；扩展名不影响匹配。该替换对所有引用同一原资源的位置生效。
若只想让 Mod 自己的剧情播放新 BGM，使用 `bgm` / `stopbgm`：

```text
bgm "@/assets/music/route.ogg" [volume="0.8"] [fadetime="1"]
stopbgm [fadetime="1"]
```

它们分别编译为原版 `music` / `stopmusic`，路径仍经过 Mod 包边界校验。`.ogg/.wav/.mp3/.aif/.aiff/.mod`
均可使用；同名 `_loopmeta.json` 可声明 `loopStartSample` 与 `loopEndSample`。

## 屏幕特效

`effect flash` 使用 Loader 生成的全屏颜色遮罩，不需要视频、动画文件或白色图片。默认在 `0.03` 秒内
闪到纯白，保持 `0.02` 秒，再用 `0.12` 秒淡出，并在特效结束前阻塞剧情：

```text
effect flash
```

完整参数：

```text
effect flash [color="#FFFFFF"] [alpha=1] [in=0.03] [hold=0.02] [out=0.12] [count=1] [gap=0.04] [wait=true]
```

- `color`：遮罩颜色，只接受 `#RRGGBB`；例如受击闪红使用 `#FF3030`。
- `alpha`：峰值透明度，范围 `0..1`。
- `in/hold/out`：淡入、保持和淡出秒数，分别允许 `0..10`。
- `count`：连续闪烁次数，范围 `1..16`；`gap` 是多次闪烁之间的间隔秒数。
- `wait=true`：特效播放完再继续剧情；`wait=false`：特效与后续命令并行。

单条命令总时长不能超过 30 秒。快进或瞬时跳过时不播放遮罩；切换流程、读档、历史回滚和 Loader
卸载都会取消尚未完成的特效并清理阻塞状态。遮罩不接收鼠标射线，不会形成额外可交互层。

其他颜色和重复次数仍属于同一个 `flash` 特效，例如两次半透明红闪：

```text
effect flash [color="#FF3030"] [alpha=0.65] [in=0.02] [hold=0] [out=0.08] [count=2] [gap=0.05] [wait=false]
```

## 第三方 Sprite

`@sprite` 把 Mod 自带 PNG/JPEG 注册为原版静态人物，因此存档、历史回滚、人物移动和多人同屏继续使用游戏的 `CharacterManager`：

```text
@sprite {
    id="guest"
    name="访客"
    source="@/assets/sprites/guest/default.png"
    defaultBase="default"
    width=420 height=700
    pivotX=0.5 pivotY=0
    portraitSize=320
    layer="front" order=10

    base { id="coat" source="@/assets/sprites/guest/coat.png" }
    emotion { id="smile" source="@/assets/sprites/guest/smile.png" }
}
```

使用时写 `$id`，Loader 会自动改成带 Mod 命名空间的内部人物名；不要求先写 `character`。`show` 会按需创建人物，
`character` 只负责切换底图或表情：

```text
show $guest [time="0.25"]
move $guest "(-180,-250)" [wait="0.3"]
$guest "显示名会自动使用 @sprite 的 name。"
character $guest [base="coat"] [emotion="smile"]
hide $guest
```

多个第三方 Sprite 与原版 Spine 人物可以同时显示。`base` 是互斥的整张底图/服装，`emotion` 是覆盖在底图上的透明表情层。
`layer=back|front` 控制相对原版人物的层级，`order` 控制同层第三方 Sprite 的顺序。所有第三方 Image 都禁用射线接收。

## 资源引用与跨文件流程

- `@/assets/...`：从当前 Mod 包根目录引用，推荐用于共享图片、音频和视频。
- `./next.txt`、`./next.sunny`、`../assets/...`：相对当前流程文件引用。
- `BGD/...`、`Music/...`：不带上述前缀时表示原游戏资源。
- `mod://...`：Loader 内部 URI，流程作者不能直接使用。

Loader 会在安装和启动扫描时展开并校验 `background`、`music`、`audio`、`video`、`load` 和语音资源。
路径不得逃出 Mod 包，也不能跨 Mod 引用。

```text
background "@/assets/cg/pic.png" [transition="black"] [time="0.5"]
bgm "@/assets/music/route.ogg" [volume="0.8"]
audio "./bell.ogg" [volume="0.5"]
video "@/assets/video/opening.webm"
load "./chapter-2.sunny"
call "./shared-scene.sunny" [enter="start"]
```

`load` 会切换到另一个流程文件，可用于把长剧情拆成多个 `.txt`/`.sunny`。它是单向流程转移，
不会压入返回帧。`call` 是无条件子流程调用，会压入完整返回帧；目标文件执行 `return` 后从 `call` 的下一句继续。
branch option 的 `story` 则是由玩家选择后调用目标流程。三者不应混用来表达同一种意图。
各文件可以通过原游戏剧情变量共享运行状态。

## 流程内语音

语音可直接绑定到台词：

```text
旁白 "这句台词会播放语音。" [voice="@/assets/voice/line.ogg"] [voiceVolume="0.9"]
```

也可用独立 `voice` 行绑定紧随其后的台词：

```text
voice "@/assets/voice/line.ogg" [volume="0.9"]
旁白 "这句台词会播放语音。"
```

`voice` 必须紧邻下一条台词。短音效继续使用原游戏 `audio` 指令。

## 原版设置页语音控制

VoiceControl 由 Loader 作为内置 Mod 提供，不需要数据 Mod 在 Manifest 或流程中声明。玩家可在 F8 Mod 页启停；
启用时，“语音音量”位于原版系统设置，“换句停止语音”位于文本设置。停止选项关闭时，推进到无语音台词
允许当前语音继续，有新语音时仍会立即替换；禁用内置功能时，Loader 使用音量 `1`、推进即停止的兼容回退策略。

`VoiceControl` 是 Loader 独占能力；数据 Mod 声明 `@audioControl` 会被当作未知流程声明拒绝。普通 Mod 只需使用
`voice`、内联 `voice=`、`@voice` 或 `@voices`，播放时会自动读取内置设置。
这些语音也会自动在对应历史记录条目中获得播放按钮；作者不需要为历史重放保存额外 ID 或元数据。
语音播放接入原版 AudioMixer 的 `Master` 分组：原版总音量、VoiceControl 语音音量和单句音量共同生效，
但原版音效音量与音乐音量不会控制语音。

## 动态人物与 Spine

游戏人物系统使用 Spine，而不是 Live2D。原游戏已配置角色可直接使用原 DSL 的
`character/show/hide/move/scale/rotate/animation` 命令。Spine 角色的 `base` 会切换皮肤，`emotion`
会解析为 Spine 表情动画，`animation` 会播放已有动画或 Animator 状态。

```text
character 八奈见
show 八奈见 [time="0.25"]
旁白 "这里会显示原版已经配置的八奈见 Spine 立绘。"
hide 八奈见
```

示例 Mod 会让两个 `@sprite` 静态人物与 `八奈见` 同屏，并确认后者能在 `CharacterSpineConfigs` 中解析到原版 Spine Prefab。
松散 PNG/JPEG 已可作为静态人物使用。第三方 Spine 使用 `@spine` 注册一个平台 AssetBundle 中的
`SkeletonGraphic` Prefab：

```text
@spine {
    id="guestSpine"
    name="第三方动态角色"
    bundle="@/assets/spine/guest.windows.bundle"
    prefab="Assets/Spine/Guest.prefab"
    defaultEmotionAnimation="idle"

    emotion { id="smile", animation="smile" }
    emotion { id="angry", animation="angry" }
}

label start:
show $guestSpine [time="0.25"]
character $guestSpine [base="outfit_01"]
character $guestSpine [emotion="smile"]
move $guestSpine "(180,-220)" [wait="0.25"]
animation $guestSpine "breathing"
hide $guestSpine
return
```

`bundle` 是通用回退路径，也可按平台写 `windowsBundle`、`macosBundle` 和 `androidBundle`。
`prefab` 必须是 Bundle 内的完整 Prefab 资产名，Prefab 根节点必须包含 `Spine.Unity.SkeletonGraphic`，
并且骨骼数据至少包含一个动画。`emotion` 把 Mod 里的语义名称映射到 Spine 动画名；`base` 直接使用
骨骼中已有的 skin 名称。`animation` 仍表示原版外层 Animator 状态，Spine 动画应通过 `emotion` 调用。

Loader 会校验 Bundle 头、目标平台、Prefab、SkeletonData、动画映射、Missing Script 和 MonoBehaviour。
Prefab 只允许 Spine Runtime 组件与原版 `SpineCharacterPhotoMark`，不会加载 Mod DLL 或任意脚本。
第三方 Spine 需要用与游戏相同的 Unity 版本和兼容的 `spine-unity` Runtime 构建；Windows、macOS、Android
需要分别构建 Bundle。真正的 FBX/GLTF 3D 模型不属于此语法支持范围。

## 返回原剧情

结束 Mod 支线时写：

```text
return
```

返回帧会恢复进入支线前的完整运行时快照。同一进程内从对话历史回滚后再次返回时可重复使用该快照；
持久返回帧还保存背景、BGM 和人物状态，供读档后的降级恢复使用。`call` 与 branch option 共用同一返回栈，
但 `call` 返回下一句，branch 返回原锚点并只跳过一次该分支拦截。
