# 维护者交接 · 当前状态与坑

> 给下一个接手的人：这里记 NOT 在代码注释里的东西 —— 已验证的行为口径、发布流程、以及踩过的坑。
> 代码本身在 `src/`，玩法说明在根 `README.md`，上架在 `docs/workshop.md`。

## 一、当前状态（截止 2026-11-11）

| 项 | 状态 |
| --- | --- |
| 版本 | 0.3.1 / 游戏 0.46 |
| Release 构建 | ✅ `dist/WaterCalcMod/WaterCalcMod.dll` 278,528 B，`<Capabilities>` 空，`detected` 为空 |
| 能力检测（2026-12-10 新规） | ✅ 已通过（玩家用游戏内 `mod-scan` 实测加载成功、无三角警告） |
| 面板 / 方案 / 数字口径 | ✅ 玩家实测验收 |
| 工坊上架 | ⏳ 未上传（材料就绪，见 `docs/workshop.md`） |
| 中文字体警告 | ⚠️ 未根治（不影响功能） |
| 面板重建耗时 | ⚠️ 100–272 ms（可感知延迟） |

## 二、已定案的行为口径（**改代码前必须遵守**）

这些都是原作者与玩家逐轮打磨后定下的，改错了会被立刻发现：

1. **方案冻结**：方案算出即锁。倒水、灌水、非目标容器过夜、切模式**都不改方案**。
   - 唯一重算入口：**按模式键**（自定义/效率/性价比），以及**设置页里改动**（水源、目标、前置、后置、海德拉）。
   - 实现：`LockPlan/LockValid/LockModeSig/LockNeedsFull`，`SetMode()` 里 `UnlockPlan + DropPlanCache + RequestRebuild`。
2. **标题栏水滴图标 = 接水**，不是「算方案」。不要把 `OnFill` 改成重算。
3. **跨夜上限 = 1 晚**（`WaterPlan.NightCap`）。玩家明确否决过「两晚以上」方案；不要为了「更多方案」把它调大。
4. **数字口径**：
   - 显示 mL = `(µL + 999) / 1000`（向上取整），因为游戏内部整数 μL、界面是 0.1 mL 且显示时进一位。
   - 药片「满瓶才挤 15 mL、逐片结算」：容量裁剪只在容器真的装满时发生（`WaterPlan.CapOf`），否则每片会凭空挤掉 15 mL。
   - 过滤棉 3 L/份计耐久，紫外按工具不消耗耐久。
5. **海德拉**：1 耐久 = 100 mL，满 330 = 33 L，洗成 98.00% 基础水（化学 1.60% / 重金属 0.40%）。
   滑条语义：玩家没拖过 → 按背包真实耐久（不超过机器上限）；拖过 → 按拖值（0 = 不限）；无限道具 → 按机器上限。
6. **顶栏「效率(无视容器)」** = 按净水器 10 L 硬上限给方案，用来判断机器一晚真实吞吐。
7. **「无限道具」**：药片/滤棉不限、紫外不计耐久与成本、海德拉不限；但滑条只管真实道具。
8. **界面风格**：必须走游戏原生像素风（`UiSkin` 图元 + 原生窗口嵌入）。该环境下 `GUI.Window` 不渲染，别用 IMGUI 拼窗口。

## 三、发布流程（每步都能验）

```powershell
# 1. 发布版构建（出 dist\WaterCalcMod\）
.\tools\build.ps1

# 2. 断言：没有任何开发期字符串
grep -ac "Desktop|ctl\.|log\.txt|GetMembers" dist\WaterCalcMod\WaterCalcMod.dll   # 期望 0

# 3. 装到本地 Mods\ 里，进游戏 F8 跑：mod-scan watercalc_assistant
#    期望 declared 空 / detected 空 / 无 DEPRECATED；模组菜单无橙色三角

# 4. 上传工坊（文案与清单见 docs\workshop.md）
```

DEV 构建（`build.ps1 -Dev`）会写 `log.txt`、开 ctl 通道、声明两项能力 → **本地会显示三角警告，属预期**，千万别拿去上架。

## 四、踩过的坑（都是真事）

| 坑 | 现象 | 正解 |
| --- | --- | --- |
| 编译报 CS 错误 | 脚本仍把**旧 dll** 复制成新包，玩家看到旧行为 | `grep -c "error CS"` 必须为 0；别信「文件大小没变」 |
| `#endif` 位置 | `InputGate` 类名/花括号被预处理掉 → `CS1022` | `#endif` 必须放在类的闭合 `}` **之后** |
| csc 引用路径 | MSYS 里用反斜杠路径 → `CS0006 找不到元数据文件` | 用 `cygpath -m`（`D:/...` 正斜杠） |
| 自写能力扫描 | 漏了 `GetMembers`，自检通过但游戏 scanner 拒绝加载 | 只信游戏 `mod-scan`，或按官方成员清单逐个 grep |
| dll 被占用 | 覆盖成功但游戏仍跑旧程序集 | 必须**重启游戏**才会加载新 dll |
| 两版同装 | 本地版与工坊版同 ID，同时存在会冲突 | `Mods\` 里只留一个文件夹 |
| 面板位置 | `rectTransform.localPosition` 无效；`WinSetPos` 双写会翻倍飞出屏幕 | 只写 `rectTransform.position`（`WinPlace`），并用 `ForcePos` 在 Tick 里连顶若干帧（`Show()` 会把位置排回 `(0,1080)`） |
| 6 秒背包缓存 | 切「无限道具」后仍显示「过滤棉 2000」 | 缓存命中条件必须带上 `infinite` 口径 |
| 滑条优先 | 默认滑条 300 会把只持有 100 耐久的玩家推荐成 300 | `HydraAllowance(cap, own)`：没拖过就按真实耐久，且不超过机器上限 |
| `python` 不可用 | WindowsApps 的 python 桩返回 9009 | 改用 bash/PS 脚本或直接编辑文件 |

## 五、调试通道（DEV 专用）

DEV 构建会读一个外部命令文件（目录由环境变量 `WATERCALC_DEV_DIR` 指定；不设则用系统临时目录下的 `watercalc-dev`）：

```powershell
$env:WATERCALC_DEV_DIR = "C:\my\dev\ctl"   # 设完再启动游戏
```

```
ctl.txt        # 写一行命令，插件 0.25 s 内读走并删除
ctl.out        # 执行结果追加到这里
ctl.enable     # 存在才响应（删掉即关闭通道）
auto.enable    # 自动进店/自动读档的开关
log.txt        # 运行日志（>2 MB 自动清空），写在 mod 文件夹内
```

支持的探针（历史版本里用过）：`rust`/`gutter` 接水、`pre=`/`post=`/`mode=`/`tgt=` 改档、`plans`/`plan?`/`mix?`/`tcase?`/`filt?<mL>@<bp>`、`ceiling?`。具体命令字符串看 `CmdFile.cs` 的解析分支。

## 六、如果要从头理解算法

1. 先读 `docs/api-notes.md` 的「单位、术语」与「净水公式」两节 —— 那是 IL 级核对过的真值（工序、粒子、预算、上限）。
2. 再读 `src/WaterMath.cs`（单瓶液体的分子级运算）→ `src/WaterPlan.cs`（组合搜索/模式/跨夜）。
3. `src/PlanText.cs` 是纯文本渲染，可以脱离 Unity 单独编译做数值回归。
4. 注意：游戏里纯度单位是 **bp**（`9901` = 99.01%），体积单位是 **μL**，显示时才换算。
