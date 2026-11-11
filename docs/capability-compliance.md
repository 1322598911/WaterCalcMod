# 能力声明与 2026-12-10 新规 · 合规说明

官方规则见游戏自带的 `ModdingGuide.zh-CN.md` / `APIChanges.zh-CN.md`。这里只记「本 mod 现在是什么状态、为什么、怎么复查」。

## 一、规则要点（2026-08 起生效的规则，2026-12-10 起强制执行）

| 能力 | 触发条件（摘要） | 2026-12-10 后 |
| --- | --- | --- |
| `files` | `File`/`FileStream`/`Directory`/`StreamReader|Writer`/`XDocument`/`ZipFile`/注册表/`AssetBundle.LoadFromFile`/截图落盘 | 工坊模组须**开发者验证**才加载 |
| `reflection-external` | `Type.GetType`、在**运行时计算出的类型**上 `GetMethod/GetField/GetProperty/GetMembers/InvokeMember`、`Assembly.GetType/GetTypes`、拼接名/拼接类型的 `AccessTools`·`Traverse`、`dynamic` | **一律拒绝** |
| `native` | `DllImport`、`unsafe`、`Marshal`、`UnsafeUtility` | **一律拒绝** |
| `dynamic-code` | `Assembly.Load`、`Reflection.Emit`、`Expression.Compile`、`BinaryFormatter` | **一律拒绝** |

两个容易被误判的细节（官方文档明写）：

- **字面类型 + 字面成员是免费的**：`typeof(MachinePurifier).GetMethod("CreateMachineInventoryWindow")`、`AccessTools.Method(typeof(string), "Concat")` 视为直接调用，不计能力。
- **拼接接收者要算能力**：`someObject.GetType().GetField("Item1")` —— 因为类型是运行时算出来的，判 `reflection-external`。
- `System.Type`、`System.Reflection.*`、Harmony 辅助类型**本身**仍计入 `reflection-external`（引用类型不等于用能力，但扫描器会看）。

## 二、本 mod 的现状：`detected` 为空

Release 构建（`tools/build.ps1` 不带 `-Dev`）满足：

- `<Capabilities></Capabilities>` 空 —— 不声明任何能力；
- 不写任何文件 —— `Log.cs` 的文件日志、`CmdFile` 的 ctl 通道、`AutoLoad`/`AutoJoin`/`MorningAuto`/`InputGate` 全部在 `#if DEV` 内，Release 里不存在；
- 访问游戏内部一律走官方 `ModReflect`（`GameBridge.T/Get/Set/Call`），这是加载器提供的按名查表接口，**不算** `reflection-external`；
- Harmony 补丁用字面 `typeof` + 字面成员名，且 `OnWindowCreated` 用**强类型签名**接值，不再对 `object` 结果反射取 `Item1/Item4`。

### 曾经的三处不合规点（已修，记录备查）

| # | 位置 | 原写法 | 现在 |
| --- | --- | --- | --- |
| 1 | `Plugin.TryRestoreCash()` | `GameBridge.T("PlayerStore")` 后 `GetProperty("Instance")`/`GetField("playerCash")` 并 `GetValue/SetValue` | 整段 `#if DEV`，仅本地测试用 |
| 2 | `PurifierHook` 的 `TryPatch/TryPatchPrefix` | `t.GetMethod(methodName, BindingFlags…)`，`t` 与 `methodName` 都是变量 | 每个补丁点写成字面 `AccessTools.Method(typeof(X), "字面名")`（6 处） |
| 3 | `PurifierHook.OnWindowCreated` | `__result.GetType().GetField("Item1"/"Item4")` | 改强类型参数 `System.ValueTuple<PixelWindow, GameSlotInventory, GameInventory, GameSlotInventory, GameSlotInventory>` 直接取 `.Item1/.Item4` |
| 4 | `PurifierUi.PosPublic()`（ctl 调试探针） | `panelWin.GetType()` 后 `t.GetMembers(Public|NonPublic|Instance)` 枚举成员名 | 整段 `#if DEV` |
| 5 | `InputGate` | `typeof(Input).GetMethod(名字变量, …)` | 整类 `#if DEV`（本来也没调用点） |

## 三、怎么复查（别靠肉眼看代码）

游戏内按 **F8** 打开控制台：

```
mod-scan watercalc_assistant
```

- 路径含空格会被拆成两个参数（`Probably Stolen`），所以用 ID，或给路径加引号。
- 输出里看三行：`declared:`、`detected:`、是否有 `DEPRECATED`。
- 判据：`declared` 覆盖 `detected`（空对空即通过），且**没有** `DEPRECATED` 行。

离线补充检查（不需要开游戏）：

```bash
# 发布版不该出现任何开发期字符串
grep -ac "Desktop"   dist/WaterCalcMod/WaterCalcMod.dll   # 0
grep -ac "ctl\."     dist/WaterCalcMod/WaterCalcMod.dll   # 0
grep -ac "log\.txt"  dist/WaterCalcMod/WaterCalcMod.dll   # 0
grep -ac "GetMembers" dist/WaterCalcMod/WaterCalcMod.dll  # 0（DEV 版应为 1）
```

> 教训：早期的自写扫描脚本（只查外部 `MemberRef`、正则也没覆盖 `GetMembers`）**漏过**一次，直到游戏自带 scanner 报
> `Undeclared capabilities: reflection-external — PurifierUi.PosPublic -> Type.GetMembers on a computed type` 才暴露。
> **只信游戏 scanner**，或按官方成员清单逐个名字 grep。
