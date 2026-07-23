# 第三方 Spine 人物制作

`@spine` 支持的是 Spine 2D 骨骼动画的 `SkeletonGraphic` Prefab，不是 FBX/GLTF 3D 模型。
它复用原版人物系统，因此 `show`、`hide`、`move`、`scale`、`rotate`、皮肤和 Spine 表情动画仍由游戏处理。

## 环境要求

- Unity 版本使用游戏实际版本：`6000.2.15f1`。
- 导入与游戏兼容的 `spine-unity` Runtime；Spine 导出版本必须与该 Runtime 匹配。
- Bundle 按目标平台分别构建。Windows、macOS 和 Android 的 Bundle 不能混用。
- 不要把第三方 C# DLL、脚本组件或自定义 MonoBehaviour 放入人物 Prefab。

## 制作 Prefab

1. 在 Unity 项目中导入 Spine 导出的 `.json` 或 `.skel`、`.atlas` 和贴图。
2. 使用 Spine Unity 的 `SkeletonGraphic` 创建 UI 骨骼对象。
3. 将 `SkeletonGraphic` 放在 Prefab 根节点，并设置 Skeleton Data、初始 skin、初始动画和材质。
4. 在 Skeleton Data 中确认所有会用于 `base` 或 `emotion` 的 skin/animation 名称。
5. 为 Prefab 设置 AssetBundle 名称并构建目标平台 Bundle。
6. 用 Bundle 内的资产名填写 `prefab`，不要填写本地磁盘路径。

建议先在一个最小测试场景中确认 Prefab 能正常播放、换 skin 和换动画，再放进 Mod。

## Mod 写法

```text
@spine {
    id="guestSpine"
    name="第三方动态角色"
    bundle="@/assets/spine/guest.windows.bundle"
    prefab="Assets/Spine/Guest.prefab"
    defaultEmotionAnimation="idle"

    emotion { id="normal", animation="idle" }
    emotion { id="smile", animation="smile" }
    emotion { id="angry", animation="angry" }
}

label start:
show $guestSpine [time="0.25"]
character $guestSpine [base="outfit_01"]
character $guestSpine [emotion="smile"]
move $guestSpine "(180,-220)" [wait="0.25"]
hide $guestSpine
return
```

`bundle` 可作为通用路径；需要跨平台分发时，改用 `windowsBundle`、`macosBundle`、
`androidBundle`。`base` 直接传 Spine skin 名称，`emotion` 的 `animation` 传 Spine 动画名称。
`animation` 命令属于原版外层 Animator 状态，不能用来替代 Spine 动画。

## Loader 校验

Loader 会检查：

- Bundle 文件头和大小；
- 当前平台选择的 Bundle；
- 指定 Prefab 是否存在且为 `GameObject`；
- 根节点是否有 `SkeletonGraphic`；
- Skeleton Data 是否可以初始化且至少有一个动画；
- `emotion` 映射的动画是否存在；
- 是否包含 Missing Script 或未允许的 MonoBehaviour。

校验失败时，Mod 仍会显示在 F8 的扫描问题中，但对应 Spine 不会注册为可用人物。
禁用、重新扫描或卸载 Mod 时，Loader 会销毁运行时配置并卸载 Bundle。
