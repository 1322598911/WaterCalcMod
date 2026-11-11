# 净水助手 · WaterCalcMod

> Probably Stolen（AppID **4348910**）的原生 mod（加载器 `IMod`，非 BepInEx）。
> 把「这瓶水接多少、跑几晚、出来多少、每升多少钱」直接算在游戏界面里，并给出推荐方案。

**当前版本**：0.3.1 ｜ **适配游戏版本**：0.46 ｜ **清单能力声明**：`<Capabilities>` 为空（不读写文件、不联网、不要求开发者验证）

---

## 这个仓库是什么

这是一个**已完成后交给社区维护**的 mod 仓库。原作者（`dsh`）不再投入开发，源码、构建脚本与全部逆向笔记都在这里，谁接手都可以直接编译、改动、重新发布。

- `src/` —— 25 个 C# 源文件（全部逻辑）
- `tools/` —— 构建脚本（bash / PowerShell 两种，直调 `csc.exe`，无 .NET SDK 依赖）
- `lib/` —— `0Harmony.dll`（MIT，编译与运行都需要）
- `manifest.dev.xml` / `manifest.release.xml` —— 两种清单（见下文「两种构建」）
- `dist/WaterCalcMod/` —— **可直接安装 / 可直接上架工坊**的成品（Release dll + 清单）
- `docs/` —— 上架资料、能力合规说明、维护者交接、游戏接口逆向笔记（43 KB，含 IL 级结论）
  - [`docs/workshop.md`](docs/workshop.md) 上架步骤与可直接粘贴的工坊文案
  - [`docs/capability-compliance.md`](docs/capability-compliance.md) 2026-12-10 能力新规与本 mod 的合规改造
  - [`docs/HANDOFF.md`](docs/HANDOFF.md) 维护者交接：已定案的行为口径、发布流程、踩坑清单
  - [`docs/api-notes.md`](docs/api-notes.md) + [`docs/API-INDEX.md`](docs/API-INDEX.md) 游戏接口真值知识库（净水公式、液体模型、官方 API）

不包含：游戏自带的 `Assembly-CSharp.dll` 等程序集（版权原因，必须自备游戏）、第三方 mod 的反编译 dump、开发期调试通道脚本。

---

## 安装（玩家）

1. 把 `dist/WaterCalcMod/` 整个文件夹拷到 `游戏安装目录\Mods\`（例如 `D:\steam\steamapps\common\Probably Stolen\Mods\WaterCalcMod\`）。
2. 进游戏 → 主菜单「模组」→ 选中「净水助手」→ **审核并启用**（本地 mod 需要开发者模式开关）。
3. 进游戏后按 **F7**（可在「模组」菜单的齿轮面板里改成别的键）打开面板。

> 本地 mod 与创意工坊版**同 ID，不能同时启用**，只能放一个。装了两个文件夹的话删掉一个。

## 玩法

| 位置 | 作用 |
| --- | --- |
| 净水器窗口标题栏 | 水滴图标＝**接水**（灌进容器）；旁边是「效率(无视容器)」快键 |
| 面板 `F7` | 接水方案 / 已有水方案 / 三种模式（自定义·效率·性价比）/ 前置后置 / 海德拉 / 设置页 |
| 手册书页 | 游戏原生书页式说明 |
| 模组菜单齿轮 | **面板快捷键**设置（官方 `ModConfig`，下拉选键，无需重启） |

面板里的数字与游戏内面板**逐位对齐**：水量用 μL 内部值向上取整显示（游戏最小显示单位 0.1 mL），药片按「满瓶才挤 15 mL、逐片结算」，过滤棉按 3 L/份计耐久，紫外灯按工具不消耗耐久。

---

## 构建

需要：Windows、**游戏本体**（提供 `Probably Stolen_Data\Managed` 程序集）、.NET Framework 4.x（自带 `csc.exe`）。**不需要** .NET SDK / Mono / BepInEx。

```powershell
# PowerShell（推荐）
.\tools\build.ps1                                    # 发布版 → dist\WaterCalcMod\
.\tools\build.ps1 -Dev                               # 开发版（带 ctl 调试通道）
.\tools\build.ps1 -GameDir "D:\steam\steamapps\common\Probably Stolen"
```

```bash
# Git Bash / MSYS
GAME_DIR='D:/steam/steamapps/common/Probably Stolen' tools/build.sh
tools/build.sh dev
```

脚本会：找游戏目录（`-GameDir` / `GAME_DIR` / 常见 Steam 路径）→ 从 `src/*.cs` 全量编译 → 拷贝对应清单到 `dist/WaterCalcMod/`。**出现 `error CS` 即视为失败，产物不可用**（脚本会以非 0 退出）。

依赖程序集清单（都在 `Managed\` 里）：`0Harmony`（优先用 `lib/` 的）、`UnityEngine` 系列、`Unity.TextMeshPro`、`Assembly-CSharp`、`netstandard`。

### 两种构建

| 构建 | 定义 | 清单 | 用途 |
| --- | --- | --- | --- |
| Release | 无 `DEV` | `manifest.release.xml`（**能力为空**） | 上架工坊 / 给玩家 |
| DEV | `/define:DEV` | `manifest.dev.xml`（声明 `files` + `reflection-external`） | 本地调试：ctl 命令通道、`log.txt`、自动读档、自动开店等 |

DEV 版带能力声明，所以**本地安装后模组菜单会显示橙色三角警告**，这是预期行为（仅本地，不上架）。DEV 版的 ctl 目录默认指向原作者机器上的路径，别的机器可用环境变量覆盖：

```powershell
$env:WATERCALC_DEV_DIR = "C:\my\dev\ctl"   # 再启动游戏
```

所有开发期代码都在 `#if DEV` 内，Release 里彻底不存在（可用 `grep -ac "Desktop\|ctl\.\|log.txt" dist/WaterCalcMod/WaterCalcMod.dll` 验证为 0）。

---

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `ModEntry.cs` | `IMod` 入口：Init/OnEnable/OnDisable，注册 Harmony、装热键 |
| `Plugin.cs` | 主 `MonoBehaviour`：Tick、热键、面板与窗口扫描调度 |
| `PurifierHook.cs` | Harmony 补丁：机器窗口创建、净水、结束夜晚、工具提示 |
| `PurifierUi.cs` | 全部 UI（约 5000 行）：面板、标题栏图标、设置页、滑条、书页 |
| `WaterMath.cs` | 液体模型：容积、纯度、药片、过滤棉、紫外、海德拉 |
| `WaterPlan.cs` | 方案求解：组合搜索、三种模式、锁方案逻辑、跨夜上限 |
| `PlanText.cs` | 把方案渲染成中文文本（与 UI 解耦，便于离线测试） |
| `WaterEconomy.cs` | 成本/每升价格核算 |
| `WaterBook.cs` | 游戏原生书页式手册 |
| `GameBridge.cs` | 访问游戏内部成员的门面：`ModReflect` 优先，旧反射全在 `#if DEV` |
| `Hotkey.cs` | 官方 `ModConfig` 快捷键条目（下拉选键）+ KeyCode 映射 + 轮询 |
| `ContextHook.cs` / `WaterPricePatch.cs` / `PurifierPatch.cs` | 小型补丁：右键菜单、水价、净水器提示 |
| `Log.cs` | 日志（Release 不写文件） |
| `MachineMarker.cs` / `MarkerOutline.cs` / `PixelIcons.cs` / `PanelBlocker.cs` | 世界空间标记、图标、输入拦截 |
| `SaveReader.cs` / `AutoLoad.cs` / `AutoJoin.cs` / `MorningAuto.cs` / `CmdFile.cs` / `InputGate.cs` | **全部 DEV 专用**（读档、自动进店、ctl 通道、输入闸） |

### 关键机制备忘（接手必读）

- **方案冻结语义**：方案一旦算出就锁住。倒水、灌水、过夜、切模式**都不改方案**；只有「改设置页（水源/目标/前置/后置/海德拉）」或**按模式键**才重算。锁定用 `LockPlan/LockValid/LockModeSig`，轻量锁遇到 full 请求会用 `LockNeedsFull` 重算一次。
- **跨夜上限** `WaterPlan.NightCap` 固定为 **1**：不允许推荐「两晚以上」的方案（原作者定案，玩家明确否决过多晚方案）。
- **数字口径**：显示 mL = `(µL + 999) / 1000` 向上取整；容量裁剪只在容器真的装满时发生（否则药片会凭空挤掉 15 mL）。
- **模式键**必须「按下即按该档重算并锁」，不是只换名字。
- **标题栏水滴键是接水**，不是重算；不要把它改成「取水/计算」。

---

## 创意工坊 / 2026-12-10 能力新规

游戏在 **2026-12-10** 起拒绝 `reflection-external`、`native`、`dynamic-code` 等能力（本地 `Mods` 文件夹不受限，只影响工坊分发）。本仓库的 Release 版已经做成 **`detected` 为空**：

- 所有游戏类型/成员访问改走官方 **`ModReflect`**（`ModReflect.FindType/Get/Set/Call/...`，按字符串查类型，官方允许，不计能力）；
- Harmony 补丁改**字面类型 + 字面成员**（`AccessTools.Method(typeof(MachinePurifier), "CreateMachineInventoryWindow")`），官方视为直接调用，免费；
- 唯一算 `reflection-external` 的写法（`GetType()` 拿到的运行时类型上再 `GetMembers`）只留在 `#if DEV` 的调试探针里；
- Release 不写文件，因此不需要 `files`；清单 `<Capabilities>` 留空。

自查方式（游戏内 F8 控制台）：

```
mod-scan watercalc_assistant
```

路径带空格时会被拆参数，请用 ID 或给路径加引号。判据：`declared` 与 `detected` 都为空、且没有 `DEPRECATED` 行。

详见 [`docs/capability-compliance.md`](docs/capability-compliance.md)。

---

## 已知问题 / 可以接着做

1. **面板重建偏慢**：`[rebuild]` 单次 109–272 ms，`[perf] tick` 最差 522 ms。求解器没有缓存剪枝，改动时注意「重建 → 重绘」都在主线程。
2. **中文缺字警告**：F8 控制台会被 TextMeshPro 的 `OfficeCodePro-Regular SDF` 缺字警告刷屏（面板中文靠 fallback 字体渲染）。不影响功能，但控制台很吵；要根治得给 TMP 加中文字体资产。
3. **手写 UI**：`PurifierUi.cs` 约 5000 行，是像素风 UI 的直接绘制代码，没有用 IMGUI（该环境下 `GUI.Window` 不渲染）。想大改 UI 建议先读 `UiSkin` 相关方法与 `ForcePos` 队列。
4. **离线自检**：`src/` 里的逻辑可脱离游戏编译测试（`PlanText` 与 UI 解耦）。原作者留过 `_probe/` 探针工程，未随仓库发布；需要时按 `WaterMath/WaterPlan` 的公开 API 自己写。
5. **数值口径**：手册页里的参考数字不随机器加成实时变化，属于已知的展示简化。

---

## 许可

MIT（见 [`LICENSE`](LICENSE)）。改之前建议先跑一遍 `tools/build.ps1` 确认本地能编译。
