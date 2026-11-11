using System;
using System.Reflection;
using HarmonyLib;

namespace WaterCalcMod
{
    /// <summary>挂到游戏的净水器上：抓到机器与「滤水容器」槽，面板据此直接读机器里那瓶水。</summary>
    public static class PurifierHook
    {
        public static float OpenedAt = -999f;
        public static object BottleInv;      // 净水器「滤水容器」槽（GameSlotInventory）
        public static object MachineItem;    // WaterPurifier() 造出来的机器物品（GameItem）
        public static object MachineWindow;  // 机器窗口（PixelWindow）
        public static int CreatedCount;
        public static int BeforePurity, BeforeVol;
        public static string LastMsg = "";
        private static ManualLogSource log;
        private static Harmony harmony;

        /// <summary>一台净水器 = 一个窗口 + 一个滤水容器槽 + 机器物品本体。</summary>
        public class Pair
        {
            public object Window;
            public object BottleInv;
            public object Machine;
        }

        public static readonly System.Collections.Generic.List<Pair> Pairs =
            new System.Collections.Generic.List<Pair>();

        /// <summary>玩家点选「按这台算」的配对下标（-1 = 没点过，按第一台可见窗口）。
        /// 之前这里不认玩家点选，每帧又被 SyncFromVisible 改回「第一台可见」，
        /// 表现成：选了 20 L 那台，接水量还是 750 那台锁着，只有把 750 的窗口关掉才对。</summary>
        public static int PickedIndex = -1;

        /// <summary>玩家最近一次查看（鼠标悬停/打开）的净水器机器。</summary>
        public static object ActiveMachine;

        public static void Install(ManualLogSource l)
        {
            log = l;
            if (harmony != null) return;
            harmony = new Harmony("dsh.watercalc.purifier");
            // 2026-12-10 起的能力检测只认「字面类型 + 字面成员」＝直接调用（不消耗能力）。
            // 所以每条补丁都在调用点写 typeof(游戏类型) + 字面方法名，重载交给 AccessTools 解析；
            // 不能再走「GameBridge.T("字符串") 再 GetMethod(变量名)」——那会被判成 reflection-external。
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MachinePurifier), "CreateMachineInventoryWindow"),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnWindowCreated")));
                log.LogInfo("[harmony] 已挂 MachinePurifier.CreateMachineInventoryWindow");
            }
            catch (Exception e) { log.LogWarning("[harmony] CreateMachineInventoryWindow -> " + e.Message); }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MachinePurifier), "WaterPurifier"),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnPurifierCreated")));
                log.LogInfo("[harmony] 已挂 MachinePurifier.WaterPurifier");
            }
            catch (Exception e) { log.LogWarning("[harmony] WaterPurifier -> " + e.Message); }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MachinePurifier), "PurifyContainer"),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnPurified")));
                log.LogInfo("[harmony] 已挂 MachinePurifier.PurifyContainer (postfix)");
            }
            catch (Exception e) { log.LogWarning("[harmony] PurifyContainer postfix -> " + e.Message); }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MachinePurifier), "PurifyContainer"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnPurifyPrefix")));
                log.LogInfo("[harmony] 已挂 MachinePurifier.PurifyContainer (prefix)");
            }
            catch (Exception e) { log.LogWarning("[harmony] PurifyContainer prefix -> " + e.Message); }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(PlayerStore), "EndNight"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnNightStart")));
                log.LogInfo("[harmony] 已挂 PlayerStore.EndNight (prefix)");
            }
            catch (Exception e) { log.LogWarning("[harmony] EndNight prefix -> " + e.Message); }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(PlayerStore), "EndNight"),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PurifierHook), "OnNightEnd")));
                log.LogInfo("[harmony] 已挂 PlayerStore.EndNight (postfix)");
            }
            catch (Exception e) { log.LogWarning("[harmony] EndNight postfix -> " + e.Message); }
        }

        /// <summary>Postfix of CreateMachineInventoryWindow：从返回的 5 元组里取 machine 窗口与滤水容器槽。</summary>
        public static void OnWindowCreated(System.ValueTuple<PixelWindow, GameSlotInventory, GameInventory, GameSlotInventory, GameSlotInventory> __result)
        {
            try
            {
                CreatedCount++;
                OpenedAt = UnityEngine.Time.realtimeSinceStartup;
                MachineWindow = __result.Item1;
                BottleInv = __result.Item4;
                Pairs.Add(new Pair { Window = MachineWindow, BottleInv = BottleInv, Machine = null });
                if (CreatedCount <= 5)
                    log.LogInfo("[hook] 抓到机器窗口 #" + CreatedCount + " inv=" + (BottleInv != null ? BottleInv.GetType().Name : "null") +
                                " win=" + (MachineWindow != null ? MachineWindow.GetType().Name : "null"));
                if (MachineWindow != null) PurifierUi.Attach(MachineWindow, BottleInv);
            }
            catch (Exception e) { log.LogWarning("[hook] OnWindowCreated " + e.Message); }
        }

        /// <summary>Postfix of WaterPurifier：拿到机器物品本身，并把「刚建的窗口」和这台机器配对。</summary>
        public static void OnPurifierCreated(object __result)
        {
            try
            {
                if (__result == null) return;
                MachineItem = __result;
                // CreateMachineInventoryWindow 在 WaterPurifier 内部先跑，所以「最后一台还没配对的窗口」就是它。
                for (int i = Pairs.Count - 1; i >= 0; i--)
                {
                    if (Pairs[i].Machine == null) { Pairs[i].Machine = __result; break; }
                }
            }
            catch (Exception e) { log.LogWarning("[hook] OnPurifierCreated " + e.Message); }
        }

        /// <summary>Prefix of CreateTooltip：玩家正在看哪台净水器（悬停或打开时游戏都会生成 tooltip）。</summary>
        public static void OnTooltip(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || __args[1] == null) return;
                var m = __args[1];
                if (!ReferenceEquals(m, ActiveMachine)) ActiveMachine = m;
                var p = PairOf(m);
                if (p != null)
                {
                    BottleInv = p.BottleInv;
                    MachineWindow = p.Window;
                    MachineItem = m;
                }
            }
            catch (Exception) { }
        }

        /// <summary>Postfix of CreateTooltip：把「一晚能不能到 99.00%」直接追加进游戏原版工具提示。</summary>
        public static void OnTooltipPost(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || __args[0] == null || __args[1] == null) return;
                string line = TooltipLine(__args[1]);
                log.LogInfo("[tip] 机器=" + GameBridge.NameOfItem(__args[1]) + " 配对=" + (PairOf(__args[1]) != null) +
                            " 行=" + (line == null ? "null" : line));
                if (string.IsNullOrEmpty(line)) return;
                AppendLine(__args[0], line);
            }
            catch (Exception e) { log.LogWarning("[tip] 失败 " + e.GetType().Name + " " + e.Message); }
        }

        private static void AppendLine(object builderObj, string text)
        {
            var builder = builderObj as RichTextBuilder;
            if (builder == null) { log.LogWarning("[tip] 第 1 参不是 RichTextBuilder"); return; }
            // 10 参签名（Cecil 实测）：AddLine(string, bool 换行, ColorPalette, bool×4, ColorPalette×3)
            builder.AddLine(text, true, (RenderHandler.ColorPalette)ColorValue,
                false, false, false, false,
                (RenderHandler.ColorPalette)(-1), (RenderHandler.ColorPalette)(-1), (RenderHandler.ColorPalette)(-1));
            log.LogInfo("[tip] 已追加");
        }

        // 参考工坊 mod（PurifierTomorrow）用的调色值；ColorPalette 是 enum
        private const int ColorValue = 12092762;

        /// <summary>给某台净水器算一句「一晚直出」结论；没有瓶就返回 null。</summary>
        public static string TooltipLine(object machine)
        {
            var p = PairOf(machine);
            if (p == null || p.BottleInv == null) return null;
            object bottle = null;
            try { bottle = GameBridge.Get(p.BottleInv, "childItem"); }
            catch (Exception) { }
            if (bottle == null) return null;
            int vol, pur;
            try
            {
                vol = GameBridge.IntOf(bottle, "GetTotalVolume");
                pur = GameBridge.IntOf(bottle, "GetWaterPurity");
            }
            catch (Exception) { return null; }
            if (vol <= 0 || pur <= 0)
                return "净水助手：这瓶还没扫过 —— 对水瓶用「水质扫描仪」扫一次，这里就会显示明晚能到多少";
            try
            {
                var on = WaterMath.PlanOneNight(vol, pur, 0, 0, 9900, 8);
                if (on == null) return null;
                if (!on.possible)
                    return "净水助手：一晚最多 " + (on.purity / 100.0).ToString("0.00") + "%（先兑水或换小瓶）";
                string how = on.asIs ? "原样跑 1 晚" : "先倒掉 " + (on.drainUl / 1000) + " mL（留 " + (on.keepUl / 1000) + " mL）";
                if (on.tablets > 0) how += " + " + on.tablets + " 片药片";
                if (on.useFilter) how += " + 过滤棉";
                if (on.useUv) how += " + 紫外";
                return "净水助手：" + how + " → " + (on.output / 1000) + " mL · " + (on.purity / 100.0).ToString("0.00") + "%";
            }
            catch (Exception) { return null; }
        }

        public static Pair PairOf(object machine)
        {
            if (machine == null) return null;
            for (int i = 0; i < Pairs.Count; i++)
                if (ReferenceEquals(Pairs[i].Machine, machine)) return Pairs[i];            return null;
        }

        /// <summary>Prefix of PurifyContainer：先记下处理前的纯度/体积。</summary>
        public static void OnPurifyPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || __args[1] == null) return;
                BeforePurity = GameBridge.IntOf(__args[1], "GetWaterPurity");
                BeforeVol = GameBridge.IntOf(__args[1], "GetTotalVolume");
            }
            catch (Exception) { }
        }

        /// <summary>Postfix of PurifyContainer：机器每晚跑完会调它，说明这瓶就是机器里的那瓶。</summary>
        public static void OnPurified(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                if (__args[0] != null) MachineItem = __args[0];
                if (__args[1] != null) Bottle = __args[1];
                OpenedAt = UnityEngine.Time.realtimeSinceStartup;
                if (__args[1] != null && log != null)
                {
                    int after = GameBridge.IntOf(__args[1], "GetWaterPurity");
                    int vol = GameBridge.IntOf(__args[1], "GetTotalVolume");
                    log.LogInfo("[purify] " + BeforeVol + "μL@" + BeforePurity + " → " + vol + "μL@" + after);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Prefix of PlayerStore.EndNight：记下入夜前机器里那瓶的水质。</summary>
        public static void OnNightStart()
        {
            try
            {
                var b = CurrentBottle();
                if (b == null) { NightBeforePurity = -1; return; }
                NightBeforePurity = GameBridge.IntOf(b, "GetWaterPurity");
                NightBeforeVol = GameBridge.IntOf(b, "GetTotalVolume");
                if (log != null) log.LogInfo("[night] 入夜 " + NightBeforeVol + "μL@" + NightBeforePurity);
            }
            catch (Exception) { }
        }

        /// <summary>Postfix of PlayerStore.EndNight：看这一夜机器里那瓶变成了什么。</summary>
        public static void OnNightEnd()
        {
            try
            {
                // 玩家 2026-11-11 改口径：过一晚**不再**解开「接水锁定」。
                // 原来的「过一晚就解锁」会让水质变好之后重算，把「1 晚 + 2 药片」的方案换成
                // 更便宜的「再多过一晚」——玩家要的是照原计划走完、稳稳拿到那一档品质，
                // 所以锁定只在「按模式键 / 玩家自己往容器里加倒水 / 再次接水」时解除。
                var b = CurrentBottle();
                if (b == null || NightBeforePurity < 0) return;
                int pur = GameBridge.IntOf(b, "GetWaterPurity");
                int vol = GameBridge.IntOf(b, "GetTotalVolume");
                if (log != null)
                    log.LogInfo("[night] 天亮 " + NightBeforeVol + "μL@" + NightBeforePurity + " → " + vol + "μL@" + pur);
            }
            catch (Exception) { }
        }

        public static int NightBeforePurity = -1, NightBeforeVol;

        /// <summary>机器里当前那瓶（GameItem），没有就 null。</summary>
        public static object Bottle;

        /// <summary>从滤水容器槽往上找「拥有它的那台机器」物品（GameItem）。</summary>
        public static object MachineOf(object inventory)
        {
            try
            {
                var gameItem = GameBridge.T("GameItem");
                if (gameItem == null) return null;
                object cur = inventory;
                for (int i = 0; i < 8 && cur != null; i++)
                {
                    var p = GameBridge.Get(cur, "parent");
                    if (p == null) return null;
                    if (gameItem.IsInstanceOfType(p)) return p;
                    cur = p;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static Pair lastVisiblePair;

        /// <summary>窗口是否正在显示（只读反射，开销很小）。</summary>
        public static bool WindowVisible(object win)
        {
            if (win == null) return false;
            try { return GameBridge.WinVisible(win); }
            catch (Exception) { return false; }
        }

        /// <summary>按「哪个窗口正显示着」来确定玩家打开的净水器（PixelWindow.IsVisible）。
        /// 玩家点选过哪台就优先用那台（PickedIndex），没点过才退回第一台可见窗口。</summary>
        public static void SyncFromVisible()
        {
            if (Pairs.Count == 0) return;

            // ① 玩家点选的那台只要还开着，就一直按它算 —— 不被「第一台可见」抢回去。
            if (PickedIndex >= 0 && PickedIndex < Pairs.Count)
            {
                var pp = Pairs[PickedIndex];
                if (pp != null && WindowVisible(pp.Window))
                {
                    ApplyPair(pp, PickedIndex);
                    return;
                }
            }

            for (int i = Pairs.Count - 1; i >= 0; i--)
            {
                var p = Pairs[i];
                if (p.Window == null) continue;
                bool vis = WindowVisible(p.Window);
                if (!vis) continue;
                if (!ReferenceEquals(p, lastVisiblePair))
                {
                    lastVisiblePair = p;
                    if (log != null)
                        log.LogInfo("[vis] 打开的净水器 = 配对#" + i +
                                    " 机器=" + (p.Machine != null ? p.Machine.GetType().Name : "null") +
                                    " 槽=" + (p.BottleInv != null ? "ok" : "null"));
                }
                ApplyPair(p, i);
                return;
            }
            lastVisiblePair = null;
        }

        /// <summary>把某个配对设为当前上下文（机器 / 滤水容器槽 / 窗口）。</summary>
        private static void ApplyPair(Pair p, int idx)
        {
            if (p == null) return;
            if (p.Machine == null && p.BottleInv != null) p.Machine = MachineOf(p.BottleInv);
            ActiveMachine = p.Machine;
            BottleInv = p.BottleInv;
            MachineWindow = p.Window;
            if (p.Machine != null) MachineItem = p.Machine;
            if (log != null && idx >= 0) log.LogInfo("[pick] 按配对#" + idx + " 算 槽=" + (p.BottleInv != null ? "ok" : "null"));
        }

        public static object CurrentBottle()
        {
            SyncFromVisible();
            // 优先用「玩家正在看的那台」的滤水容器槽
            if (ActiveMachine != null)
            {
                var p = PairOf(ActiveMachine);
                if (p != null && p.BottleInv != null)
                {
                    try
                    {
                        var ci0 = GameBridge.Get(p.BottleInv, "childItem");
                        if (ci0 != null) return ci0;
                    }
                    catch (Exception) { }
                }
            }
            if (BottleInv != null)
            {
                try
                {
                    var ci = GameBridge.Get(BottleInv, "childItem");
                    if (ci != null)
                    {
                        if (MachineItem == null) MachineItem = MachineOf(BottleInv);
                        return ci;
                    }
                }
                catch (Exception) { }
            }
            return Bottle;
        }

        /// <summary>自测：轮流 Show() 各配对的窗口，用来验证「开哪台读哪台」与加成读取。</summary>
        private static int debugIdx = -1;

        public static void DebugShowNext()
        {
            if (Pairs.Count == 0)
            {
                if (log != null) log.LogWarning("[test] 还没有任何净水器配对");
                return;
            }
            debugIdx = (debugIdx + 1) % Pairs.Count;
            var p = Pairs[debugIdx];
            bool ok = false;
            try
            {
                if (p.Window != null) { GameBridge.Call(p.Window, "Show"); ok = true; }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[test] Show 失败 " + e.Message);
            }
            if (log != null)
            {
                int pf = -1, qf = -1;
                try { if (p.Machine != null) pf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", p.Machine); }
                catch (Exception) { }
                try { if (p.Machine != null) qf = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", p.Machine); }
                catch (Exception) { }
                log.LogInfo("[test] Show 配对#" + debugIdx + " ok=" + ok
                    + " 机器=" + (p.Machine != null)
                    + " 槽=" + (p.BottleInv != null)
                    + " perf=" + pf + " qual=" + qf);
            }
        }

        /// <summary>ctl 用：直接把第 idx 台净水器的窗口显示出来（不注入鼠标键盘），返回一句话。</summary>
        public static string ShowPair(int idx)
        {
            if (idx < 0 || idx >= Pairs.Count) return "pair -> 越界 count=" + Pairs.Count;
            var p = Pairs[idx];
            if (p.Window == null) return "pair -> #" + idx + " 窗口为空";
            string res;
            try
            {
                GameBridge.Call(p.Window, "Show");
                res = "pair -> #" + idx + " shown vis=" + VisibleFlag(p);
            }
            catch (Exception e) { res = "pair -> #" + idx + " ERR " + e.GetType().Name + ": " + e.Message; }
            return res;
        }

        /// <summary>ctl 用：只收起第 idx 台窗口，用来验证「关掉某一台，只消失那一台的框、别的还在」。</summary>
        public static string HidePair(int idx)
        {
            if (idx < 0 || idx >= Pairs.Count) return "pairh -> 越界 count=" + Pairs.Count;
            var p = Pairs[idx];
            if (p.Window == null) return "pairh -> #" + idx + " 窗口为空";
            try
            {
                GameBridge.Call(p.Window, "Hide");
                return "pairh -> #" + idx + " hidden vis=" + VisibleFlag(p);
            }
            catch (Exception e) { return "pairh -> #" + idx + " ERR " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>ctl 用：把所有净水器窗口收起来，用来验证「关窗后描边消失」。</summary>
        public static string HidePairs()
        {
            int n = 0;
            for (int i = 0; i < Pairs.Count; i++)
            {
                var p = Pairs[i];
                if (p.Window == null) continue;
                try { GameBridge.Call(p.Window, "Hide"); n++; }
                catch (Exception) { }
            }
            return "pairh -> hidden=" + n;
        }

        private static string VisibleFlag(Pair p)
        {
            try
            {
                var r = GameBridge.WinVisible(p.Window);
                return ((r is bool) && (bool)r) ? "true" : "false";
            }
            catch (Exception) { return "?"; }
        }
    }
}

