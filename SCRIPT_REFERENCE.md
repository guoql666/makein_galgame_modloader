# 原流程文本定位参考

Release 根目录的 `Script/` 是原游戏正常剧情流程的作者参考副本，用于查找分支和台词补丁锚点。
Loader 运行时仍从游戏资源读取原流程；不要把整个 `Script/` 复制进自己的 Mod。

## 文件与场景路径

| Release 文件 | 流程中的 `scene` |
| --- | --- |
| `Script/Entry.txt` | `Script/Entry` |
| `Script/vol1.txt` | `Script/vol1` |
| `Script/vol2.txt` | `Script/vol2` |
| `Script/vol3.txt` | `Script/vol3` |

`.txt` 是方便打开导出文件的扩展名，不写进 `scene`。例如定位 `vol1.txt` 中的台词时使用：

```text
scene="Script/vol1"
```

`dialogue-index.tsv` 已把所有正常流程台词整理成表格。重要列为：

- `resourcePath`：直接作为 `scene`。
- `label`：直接作为 `label` / `afterLabel`。
- `dialogueOrdinal`：该 Label 内从 0 开始的台词序号，直接作为 `line`。
- `speaker`：脚本内部说话人，直接作为 `speaker`；不要使用 `displayName` 代替。
- `text`：完整原台词，直接作为 `expect` 或用于无序号匹配的 `text`。
- `id`：导出索引自身的检索标识，当前 Mod 语法不能用它替代上述定位字段。

`resource-index.tsv` 记录四个流程文件的资源路径、字节数和 SHA256，可用于确认参考文件版本。

## 定位一条原版台词

解析器提供两种定位方式，二选一。

### 按序号定位

最低必填定位字段是：

```text
scene + label + line
```

`line/dialogueOrdinal` 只统计该 Label 下的台词命令，不统计背景、音乐、人物和其他命令，并从 `0` 开始。
发布 Mod 时应使用以下五项完整写法：

```text
scene + label + line + speaker + expect
```

其中 `speaker + expect` 是校验字段。游戏更新导致序号处的说话人或原文变化时，Loader 会明确报错，
而不会把补丁静默应用到错误台词。

给原台词添加语音：

```text
@voice {
    scene="Script/vol1"
    label="1-1"
    line=3
    speaker="温水和彦"
    expect="「无论什么时候都能宠溺着男主的胡桃酱，最棒了」"
    source="@/assets/voice/vol1-1-1-003.ogg"
}
```

### 按完整原文定位

省略 `line` 时，最低必填定位字段是：

```text
scene + label + text
```

这里的 `text` 是 `expect` 的别名，必须与原文逐字符一致，并且在该 Label 内只能匹配一条台词。
建议同时填写 `speaker`，避免不同角色说出相同文本：

```text
@voice {
    scene="Script/vol1"
    label="1-1"
    speaker="旁白"
    text="我深切地注视着封面的胡桃酱"
    source="@/assets/voice/vol1-1-1-narration.ogg"
}
```

匹配不到或匹配多条时，Loader 会拒绝补丁；出现多条匹配就改用 `line`。

## 定位字段与修改内容

定位字段只负责找到原台词，修改内容还必须按声明类型提供：

| 声明 | 额外必填内容 |
| --- | --- |
| `@voice` | `source`，即 Mod 内音频资源 |
| `@text` | `value`，即替换后的文本 |
| `@dialogue` | `value`、`source` 至少一个，可同时填写 |
| `@branch` 的台词后锚点 | `scene + label + line`；`speaker + expect` 建议作为校验 |

这些声明的 `id` 默认都可省略。所有 `@` 声明必须写在流程文件第一条普通 DSL 语句之前。
