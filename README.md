# Annihilated-SaveAnyWhere

《Annihilated》的 BepInEx 6 插件：随时存档、读档，**战斗中也可以**。

游戏本身只在特定时机允许存档，而真结局对死亡次数要求很严。装了这个 mod 后，可以在战斗中任意一个回合存档，打崩了直接读回来重打这一回合。读档时还可以顺带把死亡/周目计数一起回退，失败的尝试不会影响结局判定。

## 功能

- **随时存读档**：剧情、探索、战斗中都能存档。mod 使用独立的存档槽，不会覆盖游戏自己的存档。
- **战斗中存档**：在回合开始选择指令时存档。读档后会重建敌人、血量与计量条、敌人下回合行动预告、手牌和指令按钮，可以接着打。
- **战斗检查点**：每场战斗开始时自动存一个检查点，可以随时从这场战斗的开头重打。
- **计数回退**（默认开启）：读档时把 `G_gameLoopCount` / `G_gameOverCount` 恢复到存档时的值。
- **读档确认**：读档前会弹出确认框，防止误触。

## 安装

在 [Releases](../../releases) 下载以下任一压缩包，**解压到游戏根目录**（`Annihilated.exe` 所在的文件夹）：

| 压缩包 | 适用情况 |
|---|---|
| `SaveAnywhere-x.y.z-with-BepInEx.zip` | **一键安装包**，内含 BepInEx 6 (be.793 IL2CPP x64)。没装过 BepInEx 选这个 |
| `SaveAnywhere-x.y.z.zip` | 只有 mod 本体（`BepInEx/plugins/SaveAnywhere.dll`），用于已装好 BepInEx 6 IL2CPP 的情况或更新 mod |

> 装好后第一次启动游戏时，BepInEx 要联网下载 Unity 基础库并生成 interop 程序集，需要等待几分钟，期间会有一个黑色控制台窗口，这是正常现象。之后再启动就很快了。
>
> 不想看到控制台窗口：把 `BepInEx/config/BepInEx.cfg` 里 `[Logging.Console]` 下的 `Enabled` 改成 `false`。

卸载：删除 `BepInEx/plugins/SaveAnywhere.dll` 即可。要完全移除 BepInEx，删除 `BepInEx`、`dotnet` 文件夹以及 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`changelog.txt`。

## 用法

| 按键 | 作用 |
|---|---|
| F5 | 存档到当前槽位。战斗中如果不在选指令阶段，会排队等到下次选指令时自动存档，再按一次 F5 取消 |
| F9 | 读取当前槽位（弹出确认框：再按 F9 或 Enter 确认，Esc 取消） |
| F10 | 读取最近一场战斗开始时的检查点（同样需要确认） |
| F6 / F7 | 切换到上一个 / 下一个槽位（默认 9 个） |
| F8 | 开关读档时的计数回退 |

### 注意事项

- **mod 存档是临时的**：每次启动游戏，以及用游戏自己的菜单存档或读档之后，全部 mod 槽位和战斗检查点都会被清空。重要进度请用游戏本身的存档保存。可以在配置里关闭这个行为（见下文）。
- 战斗中只能在**普通回合选指令时**存档。教程战斗、攻击条进行中、特殊连携指令等时机无法存档，F5 会排队等待。
- 读档失败时会提示原因，此时暂时禁止 F5 存档，避免把异常状态存进槽位。用游戏自己的存档或其他槽位成功读档一次即可恢复。
- 计数回退只在读取 mod 存档时进行，回退后的计数会写入游戏的全局存档（GlobalSave）。如果不希望 mod 改动全局存档，关闭 `RestoreGlobalCounters` 即可。

## 配置

第一次运行后生成 `BepInEx/config/local.annihilated.saveanywhere.cfg`，可以修改按键和以下选项（修改前先关闭游戏）：

| 选项 | 默认 | 说明 |
|---|---|---|
| `Counters.RestoreGlobalCounters` | `true` | 读档时回退死亡/周目计数 |
| `Slots.SlotCount` | `9` | mod 槽位数量 |
| `Slots.ClearOnGameSaveLoad` | `true` | 启动游戏和游戏自身存读档后清空 mod 槽位 |
| `Battle.AutoCheckpoint` | `true` | 每场战斗开始时自动存检查点 |

## 从源码编译

需要 .NET SDK 6.0 或更高版本：

```sh
dotnet build SaveAnywhere.csproj -c Release
```

`lib/` 中是编译所需的引用程序集。它们只保留类型签名，不含方法体，说明见 [lib/README.md](lib/README.md)。推送到 `main` 分支时，GitHub Actions 会自动编译并上传两种压缩包作为 artifact；推送 `v*` 标签时会创建 Release。标签名必须与 `Plugin.cs` 中的 `Version` 一致。
