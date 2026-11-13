# P35 · v11 派单 Prompt（F2 选型修正 + 补一层机位调试口）

> 来源：v10 三键调试口本地实测，见 `P35_v10_验收回传反馈_打光.md` §6。
> 主理人静态核对 + Play 实测结论；本 prompt 可直接粘贴到 Zcode「unity校园」对话。

## 背景
v10 落地的三个纯键盘调试口（F11 回路 / F12 补光总开关 / F2 教室灯）已静态核对通过，编译零 error、冻结常量未动。Play 实测两项卡点需修，另补一层机位用于断电应急视觉复核。

## 修复 1：F2 教室灯键取不到命令中心（bug，必修）

- 现象：Night F9 按 F2，Console 报 `[IndoorFill][调试] 教室灯键失败：命令中心未挂载`，灯未关。
- 根因：`IndoorFillLight.cs` 调试方法 `ToggleClassroomLamp()` 取的是 `TwinCommandCenter center = TwinCommandCenter.Instance;`。命令中心在场景里不预挂，`Instance` 要靠公开静态 `Ensure()`（`Command/TwinCommandCenter.cs:95`：Instance 为 null 时在 TwinRuntime 上 AddComponent 自建）。没开过 UI 设备面板时 `Instance` 仍是 null，调试键提前 return。
- 修法：把调试键这一处改为 `TwinCommandCenter.Ensure();`，与业务 UI 入口一致（对照 `UI/TwinControlRow.cs:197`、`UI/TwinInteractPrompt.cs:302`、`UI/TwinPcMenuDriver.cs:455` 均用 `Ensure()`）。
- 修后自查：twinId `Class2-1/Light/Main_01` 在 Registry 命中；`TrySend(..., CommandAction.SetPower, on?0f:1f, out reject)` 不被 reject；日志能打出「已下发关灯/开灯命令」。若锚点不在 Registry 或命令被拒，回传真实 reject 原因与正确 twinId，不要硬改 twinId 凑数。

## 修复 2：补一层固定机位调试口（验断电应急视觉）

- 背景：现有 F8=Corridor2（二层走廊）、F9=Class2-1（二层教室），都挂 Circuit-F2；F11 断的是 1F（Circuit-F1），二层机位看不到一层断电效果。一层 Corridor1 / 一层教室（Class1-2 或 Class1-3）没有固定机位，自动化走楼梯 + 右键转视角受限，到不了。
- 需求：在现有「验收调试键」同族闸块内，补两个一层机位键，传送口径复用现有 `TeleportToZone` / F8/F9 同款实现：
  1. 一层走廊 Corridor1 机位；
  2. 一层教室（Class1-2，若该 zone 锚点缺失再换 Class1-3）机位。
- 键位：先全库 grep 复核空闲（F1 目前空闲预留未用），回传写明最终键位占用表；不得抢 F3/F4/F5/F6/F7/F8/F9/F10/F11/F12 与数字 1~9。

## 边界红线（沿用 v10）
- 新键仅 Play 运行时生效，退出 Play 自动还原；零落盘，不改场景/预制体/资产/正式业务语义；不新增常驻 UI。
- 机位键只做传送，不改动相机/补光/业务逻辑；F2 仍走 `TwinCommandCenter.TrySend` 既有命令链路，不直接压灯、不改补光强度或冻结常量。
- 照明冻结常量零改动；调试代码集中在现有调试同族，便于验收后整体摘除。
- 改完 Ctrl+R 编译零 error、运行零 Exception。回传：每个键对应方法名 + 日志样例 + 最终键位占用表。

## 主理人本地复验项（不派 agent）
- 性能交叉开段：调换顺序再采一段补光开，与交叉关 52.51ms 对比，判定补光真实帧成本。
- 白天对照回归看一眼不回退。
