> **归档说明**：本文是原作者逆向 `Assembly-CSharp.dll` 的成果笔记，路径里的 `<游戏目录>` / `<本仓库>` / `<用户目录>` 是脱敏占位符。
> 文中提到的 `_tools\dumps\*.txt`（原始 IL 文本导出）与第三方 mod 的反编译结果**未随仓库发布**（版权原因）；要复核请按每条的「复现命令」用 dnSpy/ILSpy 自行导出。
>

# API-INDEX · 净水助手 游戏接口知识库索引

**一句话**：`docs\api-notes.md` + 本索引 = 本项目的「游戏接口真值知识库」。原始 IL 在 `_tools\dumps\`。

## 新会话怎么用（3 步，别重新挖）
1. **先读本索引**（就这一页），确认要查的主题 → 拿到 `api-notes.md` 的锚点 + 复现命令。
2. **按需读 `docs\api-notes.md` 的对应小节**（grep 锚点即可，不必通读）。
3. **要复核就看 `_tools\dumps\*.txt`**（原始 IL，全部可 grep）；要重跑就用下面「复现命令」。
   - 不确定结论是新是旧时：`api-notes.md` 里每条都带 **置信度**（确认 / 推测）与 **未决项 7 条**，先看未决项再动手。

**环境前提（别再摸索）**
- 游戏程序集 ``<游戏目录>`\Probably Stolen_Data\Managed\Assembly-CSharp.dll`
- 反编译器 `_tools\il_dump.ps1`（`-Cecil` 默认 DseCecil.dll；`-Target` 默认上面那个 dll；`-TypeFilter` 类型正则；`-Pattern` 成员名正则；`-Mode list|dump`）
- 签名工具 `_tools\methinfo.ps1`；调用点/字符串扫描 `_tools\dumps\_find_refs.ps1`（`-CallName` / `-Needle`）；总dump脚本 `_tools\dumps\_run_all.ps1`
- 第三方 mod 在 **工坊目录**：``<创意工坊目录>`\<id>\`（**不在** `Mods\` 里；`Mods\` 只有我们自己的 `WaterCalcMod`）

## 主题 → 锚点 → 复现命令

| 主题 | `api-notes.md` 锚点 | 复现命令（一行） |
|---|---|---|
| 数值对齐：网页公式逐条（buildWater/purify/runFilter/runTablet/runUv/solveRun） | `## 1.1` | `grep -n "function (buildWater\|purify\|runFilter\|runTablet\|runUv\|runOnce\|solveRun\|capOf\|budgetOf)" 净水计算器_V3.6_正式版.html` |
| 数值对齐：游戏侧 cap/purityMult/dirtBonus | `## 1.2` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^MachinePurifier$' -Pattern '^(GetMaxPurityCap\|GetPurityMult\|GetDirtBonus)$' -Mode dump` |
| **单晚净化真值：`GetDirtBonus` 分段表与插值方向** | `## 附.1` | 同上（`GetDirtBonus` 的 IL 在 `MachinePurifier_all.txt` 385-456，曲线常量在 `.cctor` 809-848） |
| **单晚净化真值：`PurifyContainer` 一晚只调一次 + amount 公式** | `## 附.2` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^MachinePurifier$' -Pattern '^PurifyContainer$' -Mode dump` |
| **单晚净化真值：`RemoveLargestFirst` 的 amount 单位/分桶顺序/回补水** | `## 附.3` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^MachinePurifier$' -Pattern '^RemoveLargestFirst$' -Mode dump` |
| **单晚净化真值：1650 mL@91.31% 一晚=99.01% 的逐项反推** | `## 附.4` | 手算，无需执行；断言值见 `## 附.5` |
| **单晚净化真值：模型该改哪一行（单位/perf口径/cap vs target）** | `## 附.5` | 读 `mod\WaterCalcMod\WaterMath.cs` L112-116（Budget）、L143-163（Purify）、L93-108（DirtMul 不要动） |
| 数值对齐：三处结构性差异（预算/化学下限/体积） | `## 1.3` | 见 1.1 + 4.3；无需新 dump |
| 数值对齐：可复核手算样例（6000 mL / 9104 bp / perf85 / q100） | `## 1.4` | 手算，公式与中间量都在；对照 `docs\api-notes.md` 1.1/1.2 |
| 数值对齐：该改的代码行 + 判定条件 | `## 1.5` | 读 `mod\WaterCalcMod\WaterMath.cs` 第 16/18/197-226 行 |
| 物品提示链：`GameItemElement::GetTooltip*` 签名 | `## 2.1` | `powershell -File _tools/methinfo.ps1 -TypeFilter '^(GameItemElement\|OverlayHandler\|RichTextBuilder)$' -Pattern '^(GetTooltip\|ShowTooltip\|AddLine\|AddProgressBar)$'` |
| 物品提示链：原生水位/容量行构建点 `CreateWaterContainerTooltip` | `## 2.2` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^CreateWaterContainerTooltip$' -Mode dump` |
| 物品提示链：「物品详情美化」挂了哪些点 | `## 2.3` | `powershell -File _tools/dumps/_run_all.ps1`（产出 `mod_SectionedTooltips_attrs.txt`） |
| 物品提示链：推荐 Harmony 挂点 + C# 示例 | `## 2.4` | 无需 dump |
| 液体读：`GetTotalVolume` / `GetTotalCapacity` / `GetFreeCapacity` / `GetWaterPurity` | `## 3.1`-`## 3.4` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^(GetTotalVolume\|GetTotalCapacity\|GetFreeCapacity\|GetWaterPurity)$' -Mode dump` |
| 液体写：`TransferLiquid`（静默失败/只按比例搬/滤芯削减） | `## 3.5` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^TransferLiquid$' -Mode dump` |
| 液体写：`AddWater` 七参 + grade 区间表 | `## 3.6` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^AddWater$' -Mode dump`（细节另见 `_tools\water_api_notes.md`） |
| 液体：其余读取口（RemoveMl/Remove/Empty/IsFull… 未展开） | `## 3.7` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^Remove' -Mode dump` |
| 药片：变体 1 体积守恒 / 变体 2 体积减少 | `## 4.1` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^OnWaterPurificationTabletAdded' -Mode dump` |
| 药片：**两个入口都是死代码**（无调用点） | `## 4.2` | `powershell -File _tools/dumps/_find_refs.ps1 -CallName 'OnWaterPurificationTabletAdded'`（v2 同理） |
| 药片：`RemoveContaminantFromContainer` 只写污染物、返回实清量 | `## 4.3` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^RemoveContaminantFromContainer$' -Mode dump` |
| 药片：道具本体 = 15 mL 氯的化学物品 | `## 4.4` | `grep -n "WaterPurificationTablet" _tools\amenities_utf8.txt`（1081-1126 行） |
| 药片：每片净损失公式（75 mL 微 + 10 mL 化 / 下限 0.05% of water） | `## 4.5` | 无需 dump；实测对照 2000→1921 / 2000→1995 |
| 容器与容量：容量无取整、cap 档位、合并=体积加权、溢出只在加液路径 | `# 五、` | 见 3.1-3.5 + 1.2 |
| 净水器有无「模式」字段 | `# 六、` | `powershell -File _tools/il_dump.ps1 -TypeFilter '^MachinePurifier$' -Pattern '.' -Mode list` |
| 未决项（缺什么证据） | `# 七、` | — |
| dump 清单 | `# 八、` | `dir /b _tools\dumps\*.txt` |

## 第三方 mod 反向取证（照抄它们怎么接游戏）
| mod | 工坊 id | 关键产物 | 用途 |
|---|---|---|---|
| 物品详情美化 Better Item Tooltips | `3830180473` | `_tools/dumps/mod_SectionedTooltips_attrs.txt` / `_il.txt` | **Harmony 挂点直接照抄**：`GameItemElement.GetTooltip/GetTooltipBasic/GetTooltipAdvanced`、`OverlayHandler.ShowTooltip`、`RichTextBuilder.AddProgressBar` |
| Pen 的移液枪 | `3829409725` | `mod_Pen_pipette_attrs.txt` / `_il.txt` | 小体积精确取/倒液、容量与剂量随档保存 |
| 更好的液体扫描仪 | `3831472187` | `mod_BetterLiquidScanner_*` | 液体成分显示（读 tag 的现成做法） |
| 我寻思明天净水器能净化好 | `3833455936` | `mod_PurifierTomorrow_*` | 净水器窗口里追加两行（**内嵌原版窗口加行**的现成范式） |

复现：`powershell -File _tools/dumps/_run_all.ps1`（一次性产出上面全部 mod 与游戏侧 dump）

## 铁律（写在这里防止下一会话踩）
- **不要改 `mod\WaterCalcMod\*.cs`**：源码归 lead；本知识库只做取证与结论。
- 每条结论必须能被 IL 复核；拿不准就标 **推测** 并写清缺什么（见未决项），**不要编**。
- 网页与 mod 是**同一个模型**（mod 逐行对齐 V3.6），所以「网页错」通常意味着「mod 也错」；游戏真值只以 IL 为准。
- 药片相关：**别再绑 `OnWaterPurificationTabletAdded{,2}`** —— 它们在发行版里没有被任何代码引用（见 4.2）。
