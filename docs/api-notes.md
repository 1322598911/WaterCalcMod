> **归档说明**：本文是原作者逆向 `Assembly-CSharp.dll` 的成果笔记，路径里的 `<游戏目录>` / `<本仓库>` / `<用户目录>` 是脱敏占位符。
> 文中提到的 `_tools\dumps\*.txt`（原始 IL 文本导出）与第三方 mod 的反编译结果**未随仓库发布**（版权原因）；要复核请按每条的「复现命令」用 dnSpy/ILSpy 自行导出。
>

# 净水助手 · 游戏接口知识库（api-notes）

> 目标：把「Probably Stolen」里与水/净水/物品提示相关的**真实接口与真实算法**沉淀成可复核的知识，换会话也不丢。
> 程序集：``<游戏目录>`\Probably Stolen_Data\Managed\Assembly-CSharp.dll`（Unity 2021.3.45 / Mono，4,822,024 B）
> 第三方 mod：``<创意工坊目录>`\<id>\*.dll`（**不在** `Mods\` 下；`Mods\` 里目前只有我们自己的 `WaterCalcMod`）
> 原始 dump：`_tools\dumps\*.txt`（本文每条都标了对应文件与复现命令）

## 0. 单位、术语与置信度图例
- **体积单位是微升 μL**（字段名 `BUDGET_*_UL`，`GetTotalVolume` 直接读 tag 整数）；1 mL = 1000 μL。
- **纯度单位是 bp**：`9901` = 99.01%。显示用 `p/100`。
- 容器里每种液体各占一个 tag：`Liquid::SetIdentifier` 里 `currentPart = "CURRENT_PART_" + id.ToUpperInvariant()`，所以水的 tag 是 `CURRENT_PART_WATER`，取值用 `Liquid::GetCurrentPartFromId("water")`。
- 置信度：**确认** = IL 逐指令核对过；**推测** = 结构/常量已见但未完全闭环，文末「未决项」写清缺什么。

复现命令模板（本文所有 IL 都是它跑出来的）：
```powershell
powershell -ExecutionPolicy Bypass -File _tools/il_dump.ps1 -TypeFilter '^WaterHelper$' -Pattern '^GetTotalVolume$' -Mode dump
powershell -ExecutionPolicy Bypass -File _tools/il_dump.ps1 -Target '<mod dll>' -TypeFilter '' -Pattern '.' -Mode list
powershell -ExecutionPolicy Bypass -File _tools/methinfo.ps1 -TypeFilter '^(GameItemElement|OverlayHandler)$' -Pattern '^(GetTooltip|ShowTooltip)$'   # 取签名
powershell -ExecutionPolicy Bypass -File _tools/dumps/_find_refs.ps1 -CallName 'OnWaterPurificationTabletAdded'                                     # 找调用点
```

---

# 一、数值对齐（最高优先级：网页 vs mod vs 游戏真值）

## 1.1 网页 `净水计算器_V3.6_正式版.html` 的公式逐条抄录
文件在工作区根：``<本仓库>`\净水计算器_V3.6_正式版.html`（1453 行）。
**确认**（直接读源码，行号即证据）。

| 函数 | 行 | 公式（原文语义） |
|---|---|---|
| `KEYS` / `PSIZE` | 266-267 | 清除顺序 `physical,microbe,organic,mineral,heavy,chemical`；particleSize `100/50/40/30/12/2` |
| `DIST` m1 | 276,280 | `heavy1 organic2 microbe1.5 chemical3 physical1 mineral1.5` → 归一化成占比（和为 10） |
| `buildWater(v,p,dist)` | 290-308 | `v'=round(v)`；`frac = p>100? p/10000 : p>1? p/100 : p`；`water = min(v, floor(v*frac))`；`imp = v - water`；按 order `organic,microbe,physical,chemical,mineral,heavy` 逐个 `floor(imp*d[k])`，**零头全给第一个 key（organic）** |
| `purityOf(w)` | 309 | `floor(water*10000/v)` |
| `dirtBonus(pm)` | 312-320 | 折线 `(3750,20)(6250,15)(8000,10)(8850,5)(9400,2)(9800,1)`；`>=9800→1`，`<=3750→20`，其间线性插值 |
| `qualityMult(q)` | 321-324 | `>=200→0｜>=100→0.1｜>=75→0.2｜>=50→0.35｜>=25→0.65｜>=15→0.85｜else 1` |
| `capOf(q)` | 325-335 | **固定档表**：`>=200→10000｜>=100→9990｜>=75→9980｜>=50→9965｜>=25→9935｜>=15→9915｜else 9901` |
| `budgetOf(v,pm,perf)` | 336-339 | `floor((min(v,10000000)*3/1000 + 25000) * (max(0,dirtBonus(pm)+(perf)/100) * 1.25) * BUDGET_K)`，`BUDGET_K` 默认 1 |
| `purify(w,cfg)` | 341-358 | `cap=capOf(q)`；`allowed = floor(v*(10000-cap)/10000)`；`budget=budgetOf`；`removable = min(budget, contamOf(w)-allowed)`；按 `KEYS` 顺序清，`out.water += take`（**体积不变**）；`window.__cap=cap` |
| `filterTarget` / `FILTER_PER` | 359-362 | 物理 1500 μL/耐久、有机 1500、矿物 2000；`microplastic` 目标 0.2%、其他 0.16% |
| `runFilter(w,durA)` | 363-374 | 每个 key `steps=ceil(a/per)`，`take=min(steps, dur-used)`，`cut=min(a, take*per)`，**`total -= cut` 且 `out.v=total`（体积真的减少）** |
| `TAB_BUDGET` / `TAB_KEEP` | 378 | **每片共用预算 76,950 μL（15 mL×5.13）**；化学只清到 `v*0.7%` |
| `runTablet(w,count)` | 379-394 | 每片： `left=76950`；`floorC=floor(v*0.007)`；`cutC=max(0,min(left, chem-floorC))`；余下 `cutM=min(microbe,left)`；**`out.v = max(0, v-removed)`（体积减少）** |
| `runUv(w,uses)` | 395-401 | `microbe→0` 且 **`water += microbe`**（体积不变） |
| `runOnce(V,src,pre,post,cfg,rounds)` | 411-422 | 接水→`filter`(耐久 400)→`uv`→`purify` 循环 rounds→`tablet(post)` |
| `solveRun(src,capVol,pre,post,cfg,target,rounds)` | 434-452 | 60 段网格粗扫 + 24 次二分，达标集合里取**最大 V** |

## 1.2 游戏真值（IL 推出）
**确认**（`_tools/dumps/MachinePurifier_all.txt`、`WaterHelper_all.txt`）

- `MachinePurifier::GetMaxPurityCap(Int32 quality) → Int32`
  IL `0458`-`0467`：`ldc.i4 10000` → `ldc.r8 99` → `ldarg.0` → `call GetPurityMult` → `mul` → `call Math::Round` → `conv.i4` → `sub` → `ret`
  ⇒ **cap = 10000 − Math.Round(99 × GetPurityMult(quality))**（C# `Math.Round` = 银行家舍入，但 99×{1,0.85,0.65,0.35,0.2,0.1,0} 都不是 .5 中点，等价四舍五入）
  档位：`<15→9901`、`15-24→9916`、`25-49→9936`、`50-74→9965`、`75-99→9980`、`100-199→9990`、`>=200→10000`。
  **注意**：网页/mod 旧表是 `9915 / 9935`，比游戏真值**各低 1 bp**（玩家规则里「99.16 / 99.36」就是游戏真值）。mod 现在 `WaterMath.CapOf` 已改成 9936/9916（第 125-126 行），**cap 已经不是错误来源**。
- `MachinePurifier::GetPurityMult(Int32)` IL `0351`-`0383`：阈值链 `>=200→0`、`>=100→0.1`、`>=75→0.2`、`>=50→0.35`、`>=25→0.65`、`>=15→0.85`、else `1.0`（`ldc.r8` 逐档）→ 与网页 `qualityMult` **一致**。
- `MachinePurifier::GetDirtBonus(Int32)` IL `0385`-`0456`：读 `DIRT_BONUS_CURVE` 数组做相邻档线性插值 `b0 + (a0-pm)/(a0-a1)*(b1-b0)` → 与网页 `dirtBonus` **同构**（档值在 `.cctor` 里，见 `MachinePurifier_closure.txt`）。
- 净化只动「杂质 tag→water tag」，**总体积恒定**：`RemoveLargestFirst` IL `00B2`-`027D`（water += removed 的 `ModifyTag` 在 `027D`）；推导与逐指令证据见 `_tools\purify_truth_notes.md`。

## 1.3 三处结构性差异（这就是玩家看到两边对不上的原因）

| # | 项目 | 网页 / mod 现状 | 游戏真值（IL） | 影响 |
|---|---|---|---|---|
| A | **药片预算** | **单一共用预算 76,950 μL**，先化学后微生物 | **两份独立预算**：微生物 ≤ **75,000 μL**（floor 0）+ 化学 ≤ **10,000 μL**（floor 5 bp） | 网页把 76.95 mL 全砸在化学上；游戏化学最多只有 10 mL |
| B | **药片化学下限** | 清到 **v × 0.7%** 才停（`TAB_KEEP=0.007`，按总体积） | 清到 **水量 × 0.05%**（`floorPermille=5`，按 water 体积） | 网页化学几乎清不动小量化学，但一旦化学多就清得**远比游戏深**（0.7% 是 0.05% 的 14 倍） |
| C | **药片清掉的量去哪** | 体积减少（`out.v -= removed`） | `RemoveContaminantFromContainer` **只写污染物 tag、不回补水**（IL `00A2`-`00C6`），体积同样减少 | 方向一致（玩家实测 2000→1921 也证明体积确实减少）；差异只在「清多少」 |

补充（玩家反馈的「3 药/2 药上限都写 9000」）：网页 `TIERS`（284 行）里 `9000` 是**铁锈水档的纯度阈值**，`capOf` 永远不会返回 9000（只会是 9901/9915/9935/9965/9980/9990/10000）。所以表里那个 9000 是**水源档位标签**，不是容量上限。置信度：**推测**（未看到玩家那一屏的具体列名；要闭环需截那张表）。

## 1.4 一个可复核的手算样例（同一输入，两套模型分别算）

输入固定：`V = 6000 mL`、进水 `p = 9104 bp`、`DIST=m1`、`pre = 过滤棉(400 耐久)+紫外(1次)`、`perf = 85`、`quality = 100`、`post = 1 片`、`rounds = 1`。
（这正是「过滤+紫外+1 片药」那条口径；`9104` 是网页 `state.rustPurity` 的默认当日铁锈水。）

公共前段（两套模型算出来完全一样，因为过滤/紫外/净化的算法已核对一致）：
```
water  = floor(6,000,000 x 0.9104) = 5,462,400
imp    = 537,600  -> organic 107,520 / microbe 80,640 / physical 53,760 / chemical 161,280 / mineral 80,640 / heavy 53,760
过滤(400): physical 36 点清 53,760；organic 72 点清 107,520；mineral 41 点清 80,640   (共用 149 点 <= 400)
           v = 6,000,000 - 241,920 = 5,758,080   purity = 9486
紫外    : microbe 80,640 -> water，v 不变 -> water = 5,543,040   purity = 9626
净化    : dirt(9626) = 1.435 -> (1.435+0.85)x1.25 = 2.85625
          base = 5,758,080x3/1000 + 25,000 = 42,274.24 -> budget = 120,745
          allowed = floor(5,758,080x10/10000) = 5,758 -> removable = 120,745
          按 heavy 先(53,760) 再 chemical(66,985) 清完 -> 杂质剩 94,295
          v 仍 = 5,758,080   purity = 9836
```
到这里两套都一样（都是 9836）。分叉出现在**药片**：

| 模型 | 化学上限 | 实清 | 结果 v | 结果纯度 |
|---|---|---|---|---|
| 网页/mod（共用 76,950、化学 floor 0.7%） | `floor(5,758,080x0.007)=40,306` | `min(76,950, 94,295-40,306=53,989) = 53,989` | `5,704,091` | `floor(5,663,785x10000/5,704,091) = 9929` **99.29%** |
| 游戏真值（化学 10,000、floor 5 bp of water） | `floor(5,543,040x5/10000)=2,771` | `min(10,000, 94,295-2,771) = 10,000` | `5,748,080` | `floor(5,663,785x10000/5,748,080) = 9853` **98.53%** |

**结论（一句话）**：先紫外后药片时，游戏里**微生物预算（75 mL）已经没东西可清、直接作废**，药片只剩 10 mL 化学预算；网页却把 76.95 mL 全给化学，所以网页把「过滤+紫外+药片」这条线的药片收益放大了约 5 倍（53,989 vs 10,000 μL）。这解释了为什么网页敢报 6000 mL 接得住，而按游戏真值 6000 mL 一晚只到 98.53%。

> 说明：本节两个「结果」都是按上表公式算术得出的，任何人可用 `V=6000 / 9104 / m1 / perf=85 / quality=100` 复算；**但网页截图里的 `5688.4 mL · 99.22%` 无法用这组输入复现**（要复现需知道当时的 `todayRustwaterPurity`、quality、target、容器 cap），所以没有把它硬凑进表里（见「未决项 6」）。

## 1.5 该改哪一行（结论给 lead）
1. **`mod\WaterCalcMod\WaterMath.cs` 第 16 行 `TAB_BUDGET = 76950`** → 拆成两份独立预算：
   `TAB_MICROBE = 75000`（微生物，floor 0）、`TAB_CHEMICAL = 10000`（化学，floor 5 bp of **water** 体积）。
2. **第 18 行 `TAB_KEEP = 0.007`** → `TAB_CHEM_FLOOR_BP = 5`（即 0.05%），且分母从 `w.v`（总体积）改为 `w.WaterPart`（水量）。
3. **第 197-210 行循环体**：顺序改成**先微生物后化学**（游戏 `OnWaterPurificationTabletAdded` IL `0014`-`003A` 就是先 `microbe` 后 `chemical_contaminant`），每片两份预算各自独立、互不挪用。
4. **第 226 行 `w.v = max(0, w.v - removed)`** 保留（实测 2000→1921 证明体积确实减少），**不要再额外硬编码「每片扣 5 mL」** —— −5 mL 是「清掉多少化学」的结果，不是固定罚金。
5. 不要给 cap 再动刀：`CapOf` 的 9916/9936 已经是游戏真值。

**判定条件（可直接写成单测）**：单瓶、单次、无紫外：
- 若 `microbe_available >= 75,000`，则 `removed_microbe = 75,000`，否则 `= microbe_available`；
- `floorC = max(0, water) * 5 / 10000`；`removed_chem = min(10,000, max(0, chemical - floorC))`；
- `removed = removed_microbe + removed_chem`；`v_new = v - removed`；`water_new = water`（不加回）。
- 反例自检：2000 mL 铁锈水（`p≈91%`）一片后玩家实测 1921 mL ⇒ `removed ≈ 79,000`。按上式微生物满 75,000 + 化学 4,000 左右即 ≈ 79,000，**与实测吻合**；若用网页模型（共用 76,950 + 7% floor）会得到明显不同的 `removed`。

---

# 二、物品提示的生成链与挂点（Harmony）

## 2.1 GameItemElement::GetTooltip 系列
**确认**（`_tools/dumps/tooltip_signatures.txt`，由 `methinfo.ps1` 产出）
```
GameItemElement GetTooltipBasic()        -> RichTextBuilder   （Private，实例）
GameItemElement GetTooltip()             ->> RichText         （Public Virtual，实例，0 参）
GameItemElement GetTooltipAdvanced()     -> RichText          （Public Virtual，实例，0 参）
OverlayHandler ShowTooltip(Vector2 position, RichText richText, Int32 maxCharWidth)
OverlayHandler ShowTooltip(Vector2 position, Vector2 localTranslation, RichText richText, Int32 maxCharWidth)
OverlayHandler HideTooltip()
RichTextBuilder AddProgressBar(Single ratio, Int32 length, ColorPalette color, ColorPalette backgroundColor, Char[] style, String end, Boolean addFullLength, Boolean bold)
```
IL 全文在 `_tools/dumps/GameItemElement_GetTooltip.txt`。

## 2.2 WaterHelper::CreateWaterContainerTooltip —— 水质/容量行的原生构建点
**确认**（`_tools/dumps/WaterHelper_all.txt` 第 1078-1299 行）
签名（按 IL 的 ldarg.0/ldarg.1 用法）：`WaterHelper::CreateWaterContainerTooltip(RichTextBuilder builder, GameItem item)`，返回 void。

关键指令行：
```
0000 ldarg.1 / 0001 ldstr WINE_CONTAINER_TAG   / 0006 IsTag / 000B brfalse -> 000D ret      ; 酒瓶不画
000E ldarg.1 / 000F ldstr LIQUID_CONTAINER_TAG / 0014 IsTag / 0019 brfalse IL_0216 -> ret   ; 非液体容器直接 return
001E..002E  capacity = item.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt()
0031..005A  遍历 Liquid::Liquids 累加 currentPart 得 filled
0073 GRADUATED_CONTAINER_TAG 或 StartingPerk::IsExperiencedChemist 分支
008A..008F ratio = (float)filled / (float)capacity
0091..00A8 RichTextBuilder::AddProgressBar(ratio, 10, 4367018, 2378096, null, "", 1, 0)
00AE..0110 RichTextBuilder::AddLine("" + F0(filled/1000) + " ml / " + F0(capacity/1000) + " ml", 1, 4367018, 0,0,0,0, -1,-1,-1)
0119..0157 (非量筒分支) AddLine("..., Capacity: " + F0(cap/1000) + " ml", ...)
015D..0195 若 LAST_SCAN_RESULT_TAG 有值 -> AddLine(扫描结果, 1, 9672266, ...)
019B..0215 PlayerStore::devMode 为真时追加 [Debug] 三行（Real/Fake Condition、Liquid Base）
0216 ret
```
`AddLine` 的调用形状：`builder.AddLine(text, ldc.i4.1, color, ldc.i4.0 x4, ldc.i4.m1 x3)` = **10 个参数**；语义上后 4 个 0 与 3 个 −1 是「无图标/无链接/无内联样式」的默认值。参数名未取到（`methinfo` 那次执行器掉线）⇒ 形状**确认**、形参名**推测**。

## 2.3 「物品详情美化」SectionedTooltips 挂了哪些点
**确认**（`_tools/dumps/mod_SectionedTooltips_attrs.txt` 的自定义属性）
```
[HarmonyPatch(RichTextBuilder, "AddProgressBar")]        SectionedTooltips.ProgressBarPatch
[HarmonyPatch(GameItemElement, "GetTooltipBasic")]       TooltipBasicPatch   (Prefix Priority=800, Postfix Priority=0)
[HarmonyPatch(GameItemElement, "GetTooltip")]            GetTooltipPatch
[HarmonyPatch(GameItemElement, "GetTooltipAdvanced")]    GetTooltipAdvancedPatch
[HarmonyPatch(OverlayHandler, "ShowTooltip")]            ShowTooltipPatch     (属性里带 4 个参数实参，命中的是 3 参重载)
[HarmonyPatch(OverlayHandler, "HideTooltip")]            HideTooltipPatch
[HarmonyPatch(ModConfigWindow, "Open")]                  SettingsPanelPatch
```
⇒ 它同样是「在 `GameItemElement.GetTooltip*` 这一层接管排版」，并额外接管 `OverlayHandler.ShowTooltip` 来自己决定宽度/位置。

## 2.4 我们该挂哪（推荐顺序）
1. **首选：`WaterHelper::CreateWaterContainerTooltip` 的 Postfix**（`__0` = RichTextBuilder，`__1` = GameItem）。
   优点：拿到的是**构建器 + 目标物品**，不用做 RichText 手术；天然只对「液体容器」触发；行序可控（我们的行会天然落在原版容量条之后）。
2. 次选：`GameItemElement::GetTooltipAdvanced` 的 Postfix（返回 `RichText`，需要把原串重建/插入，风险高）。
3. 不要挂 `ShowTooltip`：那层是渲染，拿不到物品。

```csharp
// 最小可用补丁：往液体容器提示里追加一行「水质 / 液体」
using HarmonyLib;
using System;

[HarmonyPatch(typeof(WaterHelper), "CreateWaterContainerTooltip")]
static class WaterLineTooltipPatch
{
    // 游戏原生彩字常量：良好=4367018（与容量条同色）
    static void Postfix(object __0, GameItem __1)
    {
        try
        {
            if (__1 == null) return;
            if (!__1.IsTag("LIQUID_CONTAINER_TAG")) return;      // 原生同款开关
            int ml = WaterHelper.GetTotalVolume(__1) / 1000;      // μL -> mL（整数除法，见 3.3）
            int cap = WaterHelper.GetTotalCapacity(__1) / 1000;
            int bp = WaterHelper.GetWaterPurity(__1);             // bp，9901 = 99.01%
            if (cap <= 0) return;
            string line = string.Format("水质 {0:0.00}%  ·  液体 {1} / {2} ml", bp / 100.0, ml, cap);
            // AddLine(text, 1, color, 0,0,0,0, -1,-1,-1) —— 10 参，0/-1 是原生默认占位
            var addLine = __0.GetType().GetMethod("AddLine");
            addLine.Invoke(__0, new object[] { line, true, 4367018, 0, 0, 0, 0, -1, -1, -1 });
        }
        catch (Exception) { }
    }
}
```
> 反射调用 `AddLine` 是为了绕开「形参名/类型未取全」的坑；若确认签名后可改成强类型调用。`GameItem.IsTag`/`GetTotalVolume`/`GetWaterPurity` 都是实例/静态 public，可直接调。
> 风险：`CreateWaterContainerTooltip` 在 `WINE_CONTAINER_TAG` 或非液体容器时**提前 return**，Postfix 仍会跑 ⇒ 必须复刻同样的两个 `IsTag` 门（示例已做）。
> 兼容：SectionedTooltips 也挂这一层（`AddProgressBar` + `GetTooltipBasic`），我们挂更上游的 `CreateWaterContainerTooltip`，**不与之抢方法**；若两者同时生效，我们的行会作为「原版行」被它排版接管（这正是想要的效果）。

---

# 三、液体读写的真值

## 3.1 `WaterHelper::GetTotalVolume(GameItem) → Int32`
**确认**（`WaterHelper_all.txt` 1355-1382）
```
0002 ldsfld Liquid::Liquids -> 0050 GetTagReadonly(currentPart) -> 005A TagState::GetInt -> 005F add   ; 累加
0044 ldloc.0 / 0045 ret
```
⇒ 总体积 = **所有液体 tag 之和**（含杂质），不是「只有水」。

## 3.2 `WaterHelper::GetTotalCapacity(GameItem) → Int32`
**确认**（`WaterHelper_all.txt` 865-876）
```
0001 ldstr LIQUID_CONTAINER_TAG / 0006 IsTag / 000B brtrue / 000D ldc.i4.0 / 000E ret   ; 非液体容器 -> 0
000F..001A GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt() / 001F ret
```
⇒ **没有任何取整/上限换算**：就是 tag 里存的整数 μL，容器定义（工厂方法）写了多少就是多少。
`GetTotalCapacityML`（878-892）只是 `.../GetInt()` 后再 `ldc.i4 1000 / div`（**整数除法，向下取整**）。

## 3.3 `WaterHelper::GetFreeCapacity(GameItem) → Int32`
**确认**（823-864）：`capacity - Σ parts`。

## 3.4 `WaterHelper::GetWaterPurity(GameItem) → Int32`（bp）
**确认**（1301-1354）
```
0010 ldstr water / 0015 GetCurrentPartFromId / 001A GetTagReadonly / 001F GetInt / 0024 stloc.0     ; water
0032..0050 遍历累加得 total                                                                        ; total
0069 ldloc.1 / 006A ldc.i4.0 / 006B ble.s -> 007B ldc.i4.0 ret                                     ; total<=0 -> 0
006D ldloc.0 conv.i8 / 006F ldc.i4 10000 conv.i8 / 0075 mul / 0076 ldloc.1 conv.i8 / 0078 div / 0079 conv.i4 ret
```
⇒ `purity = (long)water * 10000 / total`，**long 运算、向下取整**，与网页 `floor(water*10000/v)` 完全一致。
⇒ 纯度**永远**是「水量/总体积」，所以清掉任何杂质（体积减少）都会抬高纯度；给水加体积会稀释。

## 3.5 `WaterHelper::TransferLiquid(GameItem source, GameItem target)`（void，2 参）
**确认**（`_tools/dumps/WaterHelper_all.txt` 482-659 / 旧档 `_tools/w_tl.txt`）
```
0000 IsEmpty(target)->stloc.0                        ; wasEmptyTarget
0008/0015 两边都必须 IsTag("LIQUID_CONTAINER_TAG")，否则 0012/0021 ret（静默失败）
0023..0032 targetCapacity
0035..0060 目标已占用 = Σ target parts
007A capacity - occupied = free；007E free<=0 -> ret
0083..00CC sourceTotal = Σ source parts；若 sourceTotal<=0 -> ret
00D3 Math.Min(free, sourceTotal) = amountToTransfer
00DD GetFilterItem(target) -> filter
00E5 GetLiquidParts(source) -> parts[]；00EB SplitProportionally(parts, amount)
00FC..01EA 逐液体：sourcePartAmount、amountToTransfer=amount[i]、amountToRemove=amount[i]
            015D HandleFilter(id, amountToTransfer, source, filter)  // 滤芯会削减转移量
            018A ChemicalHelper::HandleReceiveLiquidTemp(target, amount, source)
            01B8 ModifyTag(target.currentPart, b__0, true)   // 目标 +amount
            01DE ModifyTag(source.currentPart, b__1, true)   // 源  -amount
01FB WaterFeatureHelper::OnLiquidContainerPourInto(target, source, wasEmptyTarget)
0203 WaterFeatureHelper::OnLiquidContainerPourOutOf(source)
```
**结论**：转移是**逐液体按体积比例**搬运 ⇒ 合并后的纯度自然是**体积加权**，不需要额外公式：
```
purity_after = floor( (waterA+waterB) * 10000 / (vA+vB) )
```
证据：`SplitProportionally` 对每种液体单独算量、`HandleFilter` 可能削减其中某项（被滤掉的那部分不会进目标），其余原样相加。置信度：**确认**（结构）；`SplitProportionally` 的余数补齐细则**推测**（`remainders` 是 Int64 数组，看起来是最大余数法，未逐指令展开）。

## 3.6 `WaterHelper::AddWater(GameItem, Int32 grade, Int32 addAmount, Boolean useRange, Int32 lowPara, Int32 highPara, Boolean initWaterFeature)`
**确认（结构）/ 细节见既有笔记**：签名与 grade 区间表（0:(9990,9999) 1:(9900,9989) 2:(9800,9899) 3:(9600,9799) 4:(9000,9599) 5:(3500,8999)，默认 (3500,8999)）已由前一轮反编译核对并写在 `_tools\water_api_notes.md`「取水 · 直接造水」。
- `addAmount == -1` 才是「灌满」；`useRange=false` 时 grade∈{1,2,3} 走 `RollNearLowerEdge`（贴区间下沿）、{0,4,5} 走 `RNG::GetRandomInt`。
- `AddWater` 内部会调 `HandleFilter`（IL `0374`）⇒ 插着滤芯的容器一灌水就自动过滤、体积随之减少。
- **高/低参 lowPara/highPara 的作用是「把纯度钉死在某个数」，不参与体积**（纯度只由 water/total 决定）。
- 置信度：签名与封装**确认**；`lowPara/highPara` 与 `WaterFeature` 写入的逐指令尚未在本轮展开 ⇒ 细节**推测**（引用上一轮缓存结论）。

## 3.7 其它读取口（签名已知，语义本轮未展开）
`GetCurrentCapacityUL/ML`、`GetEstimatedVolume`、`IsFull`、`IsWet`、`RemoveMl`、`Remove`、`RemoveAndReturn`、`EmptyContainer`、`RemoveExcessLiquid`、`ShrinkProportionally`、`PurifyToBaseWater`、`GetContaminantAmount/Count/Percentage`。
证据文件：`_tools/dumps/WaterHelper_all.txt`（每个方法的完整 IL 都在）。**未展开 = 未取结论**，不要凭名字猜语义。

---

# 四、药片的真实行为（本轮最重要的取证）

## 4.1 两个入口方法的完整 IL
文件：`_tools/dumps/WaterHelper_all.txt` 第 1548-1665 行。

**变体 1 `WaterHelper::OnWaterPurificationTabletAdded(GameItem)`** —— **体积守恒**
```
0006 ldarg.0 / 0007 ldstr LIQUID_CONTAINER_TAG / 000C IsTag / 0011 brtrue / 0013 ret
0014..0022 amount1 = RemoveContaminantFromContainer(container, "microbe",             75,000, 0, -1)
002C..003A amount2 = RemoveContaminantFromContainer(container, "chemical_contaminant", 10,000, 5, -1)
0044..005A targetPartAmount = GetTagReadonly("CURRENT_PART_WATER").GetInt()
005F..0077 ModifyTag(water, b__0, false)          ; b__0: SetInt(targetPartAmount + amount1)
007D..0093 targetPartAmount = 重新读 water
0098..00B0 ModifyTag(water, b__1, false)          ; b__1: SetInt(targetPartAmount + amount2)
00B6..00B8 WaterFeatureHelper::UpdateWaterFeature(container, false)
00BD ret
```
lambda 本体（`WaterHelper_closure.txt` 487-510）：
```
b__0: ldarg.1 / Enable / ldarg.0 ldfld targetPartAmount / ldarg.0 ldfld amount1 / add / SetInt
b__1: ldarg.1 / Enable / ldarg.0 ldfld targetPartAmount / ldarg.0 ldfld amount2 / add / SetInt
```
⇒ **water += amount1 + amount2**，杂质 -= 同量 ⇒ 总体积不变。

**变体 2 `WaterHelper::OnWaterPurificationTabletAdded2(GameItem)`** —— **体积减少**
```
0014..0029 microbe = GetTagReadonly("CURRENT_PART_MICROBE").GetInt()
002A..0038 newMicrobialAmount = Mathf.Max(0, microbe - 50,000)
003D..005A ModifyTag(microbe, b__0)   ; b__0: SetInt(newMicrobialAmount) —— 只减，不回补水
005B..0070 chemical = GetTagReadonly("CURRENT_PART_CHEMICAL_CONTAMINANT").GetInt()
0071..0083 newChemicalAmount = Mathf.Max(500, chemical - 5,000)
0088..00A5 ModifyTag(chemical, b__1)  ; SetInt(newChemicalAmount)
00A6..00A8 UpdateWaterFeature
00AD ret
```
⇒ 减 50 mL 微生物 + 5 mL 化学（化学至少留 500 μL），**没有任何写回 water 的动作** ⇒ 体积减少。

## 4.2 关键判定：两个变体**都没有任何调用点**
**确认**（`_tools/dumps/call_tablet_v1.txt`、`call_tablet_v2.txt`，扫描全程序集所有方法的操作数）
```
# assembly 内 CALL OnWaterPurificationTabletAdded  -> 0 命中
# assembly 内 CALL OnWaterPurificationTabletAdded2 -> 0 命中
# 字符串 "TabletAdded" 在 #US 堆里 -> 0 命中（_tools/dumps/str_tabletadded.txt）
```
同时 `RemoveContaminantFromContainer` 的全部调用者只有 5 处（`call_removecontaminant.txt`）：
```
AquacultureHelper::RunRemediation / HydroponicHelper::HandleNutriment
WaterHelper::OnWaterPurificationTabletAdded @0022 @003A        <- 死方法内部
PlantWaterQuality::Purify
```
⇒ **这两个药片入口在发行版里是死代码**（没有 call/ldftn/字符串引用）。所以：
- 网页/mod 把「药片算法」直接绑到这两条方法上，**依据不成立**；
- 真正在跑的药片逻辑在别处（可能是道具侧内联实现），**本轮未定位到**（见「未决项 1」）。

## 4.3 `RemoveContaminantFromContainer` 的真语义
**确认**（`_tools/dumps/WaterHelper_all.txt` 2074-2164；旧档 `_tools/tablet_dump.txt`）
```
0014/002E 液体必须存在且 canBeRemovedByPurifier，否则 ldc.i4.0 ret
002F..0044 water 量（stloc.1）；004A..005B 当前污染物量（stloc.2）
005C..0071 base = (floorBaseVolume >= 0 ? floorBaseVolume : water)；floor = (long)base * floorPermille / 10000
0072..0078 若 current <= floor 则 floor = current
0079..008D newValue = current - amount；若 newValue < floor 则 newValue = floor
0092..009D 若 newValue < 0 则 0
00A2..00A9 delta = current - newValue        (留在栈上)
00AA..00BE ModifyTag(该污染物 part, Enable + SetInt(newValue), false)
00C4..00C6 WaterFeatureHelper::UpdateWaterFeature(container, false)
00CB ret                                      -> 返回 delta
```
⇒ 参数序（由 `ldarg.s`/`ldarg.3` 推断）：`(GameItem container, String liquidId, Int32 amount, Int32 floorPermille, Int32 floorBaseVolume)`，`floorBaseVolume=-1` 表示「以下限按水量算」。
⇒ **它只写污染物 tag、不回补水，返回值就是实清量**。这正是「药片后瓶子变少」的机制来源。

## 4.4 药片道具本体（`AmenitiesItemDirectory::WaterPurificationTablet`）
**确认**（`_tools/amenities_utf8.txt` 1081-1126）
```
0000..0010 Directory`1::Empty + SetSpriteAndShape("Items/items_amenities","water_tablet")
001B..003A name/shortDescription = LocHelper::GetLocalizedItem("item_water_tablet_name"/"desc")
004B/0056 SetGameItemType("HOUSEHOLD_GOOD") / ("WATER_PURIFICATION_SUPPLY")
0067 ldc.i4.6 conv.i8 / SetValue         ; 售价基数 6，与网页 COST.tablet=6 一致
0070 EnableTag("WATER_PURIFICATION_TABLET")
007D ldc.i4.1 / ldc.i4.1 / ChemicalHelper::InitChemItem      ; 化学物品
0085 SetChemContentML("chlorine", 15)                         ; 自带 15 mL 氯
0098 ret
```
⇒ 药片是「15 mL 氯的化学物品 + WATER_PURIFICATION_TABLET 标签」，工厂里**没有**任何 `ldftn` 指向净水回调。
补充：`WATER_PURIFICATION_TABLET` 这个字符串在全程序集只出现在**两个工厂方法**里（`str_water_tablet_tag.txt`）⇒ 也没有「按标签分派的处理器」能被找到。

## 4.5 每片净损失公式（给 mod 用）
```
removed_microbe = clamp(microbe_available, 0, 75,000)                      // μL
floorC          = floor(max(0, water_volume) * 5 / 10000)                  // 0.05% of water
removed_chem    = clamp(chemical - floorC, 0, 10,000)
removed         = removed_microbe + removed_chem
v_after         = v_before - removed          // 体积真的减少
water_after     = water_before                // 不回补水
```
- 玩家实测 `2000 mL 铁锈水 -> 1921 mL`（少 79 mL）：`75,000 + ~4,000 = ~79,000` ⇒ **与该式吻合**（化学剩余量决定尾数）。
- 玩家实测 `2000 mL 瓶 + 1 片 -> 1995 mL`（少 5 mL）：该瓶**已无微生物**（先过紫外/或水本身干净）时 `removed_microbe=0`，只剩化学 5,000 ⇒ **恰好 −5 mL**。
- ⇒ **「每片固定扣 5 mL」是错的口径**；正确口径是「扣 = 实际清掉的杂质体积」，上界 85 mL/片。
- 置信度：公式主体**确认**（常数 75,000 / 10,000 / floorPermille 5 与「不回补」都已 IL 证实）；「发行版实际就是按这组常数执行」为**推测**，唯一缺的是「真正被调用的入口在哪」（未决项 1）。

> 关于游戏内「溢出」：容器满时再加液体会走 `RemoveExcessLiquid`（`WaterHelper_all.txt` 893 起）把总量截回容量；药片路径本身不产生溢出（它只减不增）。玩家看到的 −5 mL **不是溢出**，是清掉了 5 mL 化学后的体积差。

---

# 四·附、单晚净化真值（一晚到底能清多少杂质）

> 直接回答玩家那条：**1650 mL / 91.31% 的瓶子，一晚（一次 `PurifyContainer`）就能到 99.01%**，而且**第二晚什么也不做**。如果模型说需要两晚，那是模型的问题，不是游戏的行为。

## 附.1 `MachinePurifier::GetDirtBonus(Int32 purityBp) → Double` 完整实现
**确认**（`_tools/dumps/MachinePurifier_all.txt` 385-456；曲线常量在同文件 `.cctor` 809-848）

分段表（`DIRT_BONUS_CURVE`，数组**从高纯度往低**存）——**与 mod 的 `px/py` 表逐项一致**：

| 纯度区间（bp） | 区间端点脏度 | 备注 |
|---|---|---|
| ≥ 9800（98.00%） | 1 | 直接返回 `curve[0].Item2`（IL `0000`-`0023`） |
| 9400 … 9800 | 2 … 1 | 线性插值 |
| 8850 … 9400 | 5 … 2 | 线性插值 |
| 8000 … 8850 | 10 … 5 | 线性插值 |
| 6250 … 8000 | 15 … 10 | 线性插值 |
| 3750 … 6250 | 20 … 15 | 线性插值 |
| ≤ 3750 | 20 | 循环走完返回末项 `Item2`（IL `0082`-`009A`） |

**插值方向（关键，别搞反）**：数组是降序存放，所以对落在 `(a1, a0]` 的点，
`t = (a0 − purity) / (a0 − a1)`，`dirt = b0 + t × (b1 − b0)`，其中 `a0/b0` 是**较高**档、`a1/b1` 是**较低**档。
IL 证据：`0028` 取 `curve[i-1]` 的 `Item1/Item2` → `stloc.1/stloc.2`（a0/b0）；`0042` 取 `curve[i]` → `stloc.3/V_4`（a1/b1）；
`005F ldloc.1 / 0060 ldarg.0 / 0061 sub` = **a0 − purity**；`0063 ldloc.1 / 0064 ldloc.3 / 0065 sub` = a0 − a1；`0067 div` = t；
`006A ldloc.2 / 006B V_5 / 006D V_4 / 006F ldloc.2 / 0070 sub / 0071 mul / 0072 add` = `b0 + t×(b1−b0)`；`0073 ret`。
**参数就是纯度（万分比），不是脏度**（方法入口 `0000 ldarg.0` 直接就是 `GetWaterPurity` 的返回值，见附.2 的 `0034`-`003A`）。

`9131` 的取值：落在 `(8850, 9400]` → `t = (9400−9131)/(9400−8850) = 269/550 = 0.48909` → `dirt = 2 + 0.48909×3 = **3.4672727**`。

## 附.2 `MachinePurifier::PurifyContainer(GameItem machine, GameItem container, Boolean ignoreBonus) → Int32`
**确认**（`MachinePurifier_all.txt` 191-264），一晚 = 调用**一次**（`<>c__DisplayClass8_0::<WaterPurifier>b__5` 里 `007A`-`008D` 只调一次，`removed<=0` 就不扣电，`009B ret`）。

```
0000 ldarg.1 LIQUID_CONTAINER_TAG IsTag / 000B brtrue / 000D ldc.i4.0 ret      ; 非液体容器 -> 0
0019 GetCurrentPerformanceBonus -> perf ; 0020 GetCurrentQualityBonus -> quality
002D ldarg.2 ignoreBonus / 002E brfalse ; 为真时 perf=0、quality=0（0030/0032）
0034 GetWaterPurity(container) -> 003A call GetDirtBonus -> stloc.2 (dirt)
0040..004D dirt += perf/100（conv.r8、ldc.r8 100、div）
004F..0064 若 dirt < 0 则 dirt = 0（bge.un）
0065..0070 dirt *= 1.25
0087 Math::Min(GetTotalVolume(container)【0027 压栈】, 10,000,000)   ← 长整型
0092..009C (long)min * 3 / 1000        ← 长整型除，先乘后除
009C..00A2 + 25,000                    ← 长整型
00A3 conv.r8 / 00A4 ldloc.2 / 00A5 mul / 00A6 conv.i8 -> amount        ; floor(base * dirt)
00A8..00B0 RemoveLargestFirst(container, GetMaxPurityCap(quality), amount)
00B5..00B7 WaterFeatureHelper::UpdateWaterFeature(container, false)
00BC ret                                ; 返回值 = RemoveLargestFirst 的结果（栈底保留）
```
⇒ **`amount = floor(( min(V,10,000,000）× 3 / 1000 + 25,000 ) × max(0, GetDirtBonus(purity) + perf/100) × 1.25)`**，单位 **μL 杂质**（不是纯度点数）。
⇒ `V` 必须是 **μL**：`V×3/1000` 这一项是「体积按 0.3% 折算」，1650 mL 时它只有 4,950 μL，占 `base` 的 16.5%；若把 V 当 mL 传进去这一项会塌成 4.95，`base` 几乎只剩 25,000 的平台项。

## 附.3 `MachinePurifier::RemoveLargestFirst(GameItem container, Int32 capBp, Int64 amount) → Int32`
**确认**（`MachinePurifier_all.txt` 469-726；逐指令还原另见 `_tools\purify_truth_notes.md`）

```
0006 GetTotalVolume -> V ; 000E 若 V<=0 -> 0 ; 0013 water part ; 001E water = tag ; 0036 若 water<=0 -> 0
003B..004C maxImp = (long)V * (10000 - cap) / 10000
004E..005A budget = Math::Min(amount, (V - water) - maxImp)          ← 只清「超出达标线」的部分
0061..0068 若 budget <= 0 -> 0
0069..0123 建 SortedDictionary<int, List<Liquid>>：
           跳过 identifier=="water"（008C-009B）、跳过 !canBeRemovedByPurifier（009D-00A4）、
           跳过当前 tag 量为 0（00A6-00B9）；
           key = (REMOVED_LAST.Contains(id) ? 1,000,000 : 0) − particleSize                （00BB-00DD）
0124 removed = 0
012C..0261 按 key 升序遍历桶：某桶 budget<=0 就跳去 027D；
           桶内 sum = Σ 该桶各液体量；take = Math::Min(sum, budget)；
           桶内分摊 takeI = take × amount_i / sum（最后一项 = take − taken）；
           ModifyTag(该液体 part, SetInt(amount_i − takeI))；removed += taken；budget -= taken
027D..0292 ModifyTag(water part, SetInt(water + removed))            ← **回补水，体积恒定**
0292 ret (int)removed
```
三个要点（回答提问）：
1. **`amount` 是「可移除杂质总量（μL）」，不是纯度点数**：它与 `(V − water) − maxImp` 直接 `Math::Min`（两者都是 μL），`removed` 也是 μL 累加，最后 `return (int)removed`。
   而且它会被 **`maxImp` 夹住**：一次调用最多清到「刚好达标」，多余的预算**直接丢弃**，不会留到第二晚。
2. **不是对所有污染物按比例**：先按 `key` 分桶（`particleSize` 大的先清；`mineral / chemical_contaminant / heavy_metal` 三个被 `REMOVED_LAST` 推到**最后**），**按桶逐个吃满**；只有**同一个桶内**才按各自储量等比分摊（最后一项吃掉余数）。
3. **清掉后回补水**：`027D` 把 `water + removed` 写回 water tag ⇒ **体积一分不变**（与药片那条「只减不回补」正好相反）。

## 附.4 反推验证：1650 mL / 91.31%（`pur = 9131`）、perf=0、quality=0（cap=9901）

| 步骤 | 算式 | 结果 |
|---|---|---|
| 水 / 杂质 | `water = floor(1,650,000 × 9131/10000)` | water **1,506,615**；杂质 **143,385** |
| 杂质分项（dist m1，零头进 organic） | organic 28,680 · microbe 21,507 · physical 14,338 · chemical 43,015 · mineral 21,507 · heavy 14,338 | Σ = 143,385 ✓ |
| `dirt` | `GetDirtBonus(9131) = 2 + 269/550×3` | **3.4672727** |
| `dirt`（+perf/100，×1.25） | `(3.4672727 + 0) × 1.25` | **4.3340909** |
| `base` | `1,650,000 × 3 / 1000 + 25,000 = 4,950 + 25,000` | **29,950** |
| `amount` | `floor(29,950 × 4.3340909)` | **129,806** |
| `maxImp` | `1,650,000 × 99 / 10000` | **16,335**（正好是 99.01% 允许的残留） |
| `budget` | `Min(129,806, 143,385 − 16,335)` | **127,050** |
| 逐桶清（bucket 顺序 physical→microbe→organic→mineral→heavy→chemical） | 14,338 / 21,507 / 28,680 / 21,507 / 14,338 全清（累计 100,370，余 26,680）→ chemical 再吃 26,680 | 累计 **127,050** |
| 清完各项残留 | physical 0 · microbe 0 · organic 0 · mineral 0 · heavy 0 · **chemical 16,335** | Σ = 16,335 = maxImp ✓ |
| 回补水 | `water = 1,506,615 + 127,050` | **1,633,665**（V 仍 1,650,000） |
| 纯度 | `floor(1,633,665 × 10000 / 1,650,000)` | **9901 = 99.01%** ✅ |

**第二晚**：纯度已 9901 ≥ 9800 ⇒ `dirt = 1.0`，`amount = 29,950×1.25 = 37,437`，但 `maxImp` 仍是 16,335、杂质已恰好等于 16,335 ⇒ `budget = Min(37,437, 0) = 0` ⇒ **removed = 0，什么都不做**（`0061`-`0068` 直接 `ret 0`）。
⇒ 玩家说「第二晚没意义」是对的；`PlanOneNight`/方案表应当报 **rounds = 1**。

## 附.5 结论：模型错在哪 / 该改哪一行
1. **脏度曲线没有错**：`WaterMath.DirtMul`（L93-108）的表与插值方向与 IL 完全一致，**不要动**。
2. **公式骨架也没有错**：`WaterMath.Budget`（L112-116）与 `Purify`（L143-163）都对；`Purify` 里的 `removable = Math.Min(budget, w.Impurity - allowed)`（L149）正是 IL 的 `Math::Min(amount, (V−water)−maxImp)`。
3. **真正可疑的是入参**，按这个顺序查（每一处都能让 1650 mL 从 9901 掉到 ~98%）：
   - **体积单位**：`Budget(v, …)` 的 `v` 必须是 **μL**。若某条路径传的是 mL，`v×3/1000` 项塌成 4.95，`amount ≈ 108,371`（而不是 129,806），一晚只剩 **97.87%** —— 这与「~98%」的观测吻合，是**第一嫌疑**。核对点：`WaterMath.Budget` 的所有 caller，尤其 `WaterPlan.Build(capMl * 1000L, …)` 之外的那些。
   - **`perf` 口径混用**：`PurifierUi.cs:280` 与 `:1161` 传 `1.0 + PerfPct/100.0`（25% 变 1.25 ⇒ `perf/100` 只剩 0.0125），而 `:1394`、`:1690` 传原始 `perf`（25 ⇒ 0.25）。`:1689` 的注释已经记录过这个坑。全项目必须统一为「传百分比原值」。注意本算例 `budget` 被 `maxImp` 夹住，所以这个错在 1650 mL 上**不显形**，但它会污染其它体积/目标档。
   - **cap 与 target 别混**：`cap` 是机器上限（9901），`target` 是用户目标（9900）；方案是否「一晚达成」要用 `purity >= target` 判，不要用 `purity >= cap`。
4. **判定条件（可直接写成断言）**：输入 `(v=1,650,000 μL, purity=9131, perf=0, quality=0)` 时，模型必须输出
   `dirt=3.4672727`、`amount=129,806`、`removed=127,050`、`purity_after=9901`、`rounds=1`。
   任何一项对不上，就顺着第 3 条的三个嫌疑点查——**不要再去改脏度曲线或 cap**。

---

# 五、容器与容量

- **容量没有取整/上限规则**：`GetTotalCapacity` 直接返回 `LIQUID_CONTAINER_CAPACITY` tag（**确认**，3.2）。容器容量由道具工厂方法写死（例：`AmenitiesItemDirectory::WaterPitcher` = 3 L，网页 `STD` 表 `bottle2=2,000,000 μL` 等与之一致）。
- **净化上限**（不是容量上限）：`cap = 10000 − Math.Round(99 × GetPurityMult(quality))`（**确认**，1.2），档位 9901/9916/9936/9965/9980/9990/10000。
- **同种水合并后的纯度**：`TransferLiquid` 逐液体按比例搬运 ⇒ 结果就是**体积加权**；公式见 3.5。**不是**「取较高者」也不是「按堆叠数平均」。
- **溢出**：只有「加液体」路径会产生超容，由 `RemoveExcessLiquid` 截回；净化/药片/紫外都是「总量不减或减少」，不会超容。

---

# 六、净水器「模式」

**确认（方法面无模式概念）**：`MachinePurifier` 的全部成员只有
```
WaterPurifier / PurifyContainer / CreateMachineInventoryWindow / CreateNote
GetPurityMult / GetDirtBonus / GetMaxPurityCap / RemoveLargestFirst / CreateTooltip / .ctor / .cctor
```
（`_tools/dumps/MachinePurifier_all.txt` 的方法头清单，11 个）
⇒ 游戏侧**没有**「效率 / 性价比」这类模式字段需要同步；mod 的 `Mode` 纯属界面层自算。机器的「性能加成 / 质量加成」是**模组物品**提供的外部参数，由 `PurifyContainer(machine, container, ignoreBonus)` 在调用时读取（模组加成读法见项目笔记：以玩家实际打开的净水器为准）。
置信度：方法名面**确认**；**字段层面**（是否有个 `mode`/`efficiency` 字段没被方法引用）未 dump，**推测为无**（要闭环需再跑一次字段清单）。

---

# 七、未决项（缺什么证据，别猜）
1. **药片的真实入口还没定位**：两个 `OnWaterPurificationTabletAdded*` 在全程序集无调用点。缺：一个「运行时证据」——建议在游戏里用 ctl 探针在丢药片前后各取一次 `GetTotalVolume/GetWaterPurity`，同时挂 `RemoveContaminantFromContainer` 的调用计数（Harmony Postfix 打日志）来确认常数到底是 75,000/10,000 还是 50,000/5,000。
2. **`RichTextBuilder.AddLine` 的形参名/类型**：本轮 `methinfo` 那次执行器掉线未取到。复现：`powershell -File _tools/methinfo.ps1 -TypeFilter '^RichTextBuilder$' -Pattern '^AddLine$'`。
3. **`SplitProportionally` 余数补齐细则**（最大余数法 vs 逐项 floor+尾巴）：IL 有待展开（`WaterHelper_all.txt` 345 行之后）。
4. **`AddWater` 的 lowPara/highPara 与 `WaterFeature` 写入**：引用上一轮结论，未在本次重新逐指令核对。
5. **`MachinePurifier` 字段清单**（Q6 闭环用）。
6. **网页 `5688.4 mL · 99.22%` 的输入条件**：缺当时的 `todayRustwaterPurity` / quality / target / 容器 cap；有这 4 个数就能用 `docs` 里的公式复算并判定是网页错还是 mod 错。

---

# 八、本文用到的 dump 与产物清单（`_tools\dumps\`）
| 文件 | 内容 | 生成方式 |
|---|---|---|
| `WaterHelper_all.txt` (143,512 B) | WaterHelper 全部 75 个方法 | `_run_all.ps1` |
| `WaterHelper_closure.txt` (26,383 B) | WaterHelper 全部 lambda | `_run_all.ps1` |
| `MachinePurifier_all.txt` / `_closure.txt` | 净水器全部方法与 lambda | `_run_all.ps1` |
| `Liquid_all.txt` / `WaterFeatureHelper_all.txt` | 液体定义 / 水质特征 | `_run_all.ps1` |
| `tip_candidates.txt` | Tooltip* 全部类型 | `_run_all.ps1` |
| `tablet_candidates.txt` | Tablet/Pill/Purif/Capsule 类型 | `_run_all.ps1` |
| `GameItemElement_GetTooltip.txt` | `GetTooltip*` 的 IL | 补跑 il_dump |
| `tooltip_signatures.txt` | 提示相关方法签名 | `methinfo.ps1` |
| `mod_SectionedTooltips_attrs/_il.txt` | 物品详情美化的 Harmony 挂点 | `_run_all.ps1` |
| `mod_Pen_pipette_attrs/_il.txt` | 移液枪 | `_run_all.ps1` |
| `mod_BetterLiquidScanner_*` / `mod_PurifierTomorrow_*` | 液体扫描仪 / 明日净水器 | `_run_all.ps1` |
| `call_tablet_v1/v2.txt`、`call_removecontaminant.txt`、`call_portable_purifier.txt` | 调用点扫描 | `_find_refs.ps1 -CallName` |
| `str_water_tablet_tag.txt`、`str_tabletadded.txt`、`str_purification.txt`、`str_tablet_lower.txt` | 字符串扫描 | `_find_refs.ps1 -Needle` |
| `GameItemElement_GetTooltip.txt` (27,203 B) | `GetTooltip*` 全 IL | 补跑 il_dump |

> 药片工厂 `AmenitiesItemDirectory::WaterPurificationTablet` 的证据在 **旧档** `_tools\amenities_utf8.txt` 第 1081-1126 行（UTF-8，可直接 grep），本次没有重新 dump成独立文件。
> 复现：`powershell -File _tools/il_dump.ps1 -TypeFilter 'AmenitiesItemDirectory' -Pattern '^WaterPurificationTablet$' -Mode dump`
