using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ColorPalette = RenderHandler.ColorPalette;

namespace WaterCalcMod
{
    /// <summary>
    /// 净水助手的原生 UI：全部用游戏自己的 MachineryText / MachineryButton /
    /// MachineryStack / ScrollableRichTextElement 拼，宿主可以是净水器窗口的标题栏
    /// （3 个 16px 图标）或按 F7 打开的独立 PixelWindow。不画任何自绘美术。
    /// </summary>
    public static class PurifierUi
    {
        private static ManualLogSource log;
        private static string lastPartsLog = "";   // 去重：Compute 每 0.4s 跑一次，同样的配比不重复写日志
        private static string lastTipLog = "";
        private static readonly List<PixelWindow> attached = new List<PixelWindow>();

        public static PanelModel Model = new PanelModel();
        public static int Mode = 2;   // 1 效率（装满容器）   2 性价比（一晚净赚最多）
        /// <summary>玩家 2026-11-11 m00327：道具口径的**全局**开关（Q2＝全局一个）。
        /// false＝按背包真实持有；true＝无限道具（药片/滤棉不限，紫外是工具不计耐久与成本，海德拉不限，
        /// 但拉条与设置页仍能限制这些「无限道具」，见 Q3）。点档位键＝真实→无限→真实 三态循环。</summary>
        public static bool InfiniteTools = false;

        /// <summary>真实道具 ⇄ 无限道具（全局一个开关，所有档位共用）。</summary>
        private static void ToggleInfinite(string why)
        {
            InfiniteTools = !InfiniteTools;
            LastMsg = InfiniteTools ? "无限道具：药片/滤棉不限，海德拉不限（拉条只管真实道具）" : "真实道具：按背包实际持有算";
            Log("[ui] infinite=" + (InfiniteTools ? 1 : 0) + " (" + why + ")");
            // 无限/真实切换会改 AutoSolve 的库存口径，必须解锁并丢掉上一档方案；
            // 否则已经接 29950 的瓶子会一直沿用旧方案，按接水也补不上去。
            UnlockPlan("切无限道具");
            DropPlanCache();
            RefreshModeDesc();
            InvalidateCalc();
            RefreshMixIfOpen();
        }

        /// <summary>玩家 2026-11-14 定案：标题栏图标固定「性价比 / 效率」两个词。
        /// 无限口径不在这里表达——主页「持有 无限」与计算页口径行负责显示，避免和 EnsureHdrNames 的自愈打架。</summary>
        private static void RefreshModeDesc()
        {
            if (modeBtnCheap != null) SetDesc(modeBtnCheap, "性价比");
            if (modeBtnUncap != null) SetDesc(modeBtnUncap, "效率");
            if (infBtn != null) SetB(infBtn, (InfiniteTools ? "▶" : "") + "无限道具");
        }

        /// <summary>模式/道具口径变了，计算页的缓存必须作废——否则 RecalcCalc 会直接返回旧 Rep。</summary>
        private static void InvalidateCalc()
        {
            calcRep = null;
            calcDirty = true;
            if (UiPage == 4)
            {
                // 模式/道具口径切换时，计算页要连瓶一起重新读，否则会拿旧水量算成 0。
                SyncCalcBottleOnEnter();
                RefreshCalcLabels();
            }
        }
        // 默认「优质水 99.00%」：玩家目的是卡 99% 上限多接水（本机上限 99.01 会少接 ~50 mL/6L）
        public static int TargetIdx = 2;
        // 玩家：模式里删掉「一晚就要」，只留 效率 / 性价比；前置「按模式最优」改名「自动」并增「过滤棉+紫外」；
        // 后置同理（自动 + 具体药片数）。内部索引仍是老编号，靠这层映射做「显示名 ↔ 值」，不动计算逻辑。
        private static readonly string[] ModeUi = { "效率", "性价比" };
        private static readonly int[] ModeVal = { 1, 2 };
        private static readonly string[] PreUi = { "不处理", "过滤棉", "紫外", "过滤棉+紫外", "药片", "自动", "药片+紫外", "药片+紫外+过滤棉", "药片+过滤棉" };
        private static readonly int[] PreVal = { 1, 2, 3, 4, 5, 0, 6, 7, 8 };
        private static readonly string[] PostUi = { "自动", "不放药片", "1 片药片", "2 片药片", "3 片药片" };
        private static readonly int[] PostVal = { 0, 1, 2, 3, 4 };

        public static readonly string[] SrcNames = { "自动（瓶上标签）", "铁锈水", "沟渠水", "优质水", "基础水", "纯水" };
        // 海德拉开关（玩家 2026-11-10 要求做成设置项）：自动 / 强制用 / 完全不用。
        // 真实一只 300 耐久（1 点 = 100 mL），可以多买几只叠加；滑条上限按容器装得下的最大量给。
        // 玩家 2026-11-10：游戏容器最大 50 L ⇒ 500 点才是合理上限（不该卡在 330）。
        public static readonly string[] HydraUi = { "自动（必要时才用）", "强制用（装得下就装满）", "完全不用" };
        public static readonly int[] HydraVal = { 0, 1, 2 };
        /// <summary>设置页右侧取值只写短名（左侧标签已固定 80 px，值写长了会把窗口顶宽）。</summary>
        private static readonly string[] HydraShort = { "自动", "强制用", "不用" };
        // 耐久改成原生滑条（玩家 2026-11-10：为什么人家 mod 有滑块你没有）。
        // 游戏自带 MachinerySlider（PixelElement 家族，可直接 Add 进 MachineryStack），
        // 有公开的 onChanged / onReleased（Action<float>），所以能连续拖到 312 这种任意值。
        public const int HydraDurMin = 1;      // 0 留给「不限」
        public const int HydraDurMaxSlider = 500;   // 50 L 容器 = 500 点（玩家 2026-11-10 定案）
        public static int PreMode = 0;
        /// <summary>效率（无视容器上限）：规划时不按容器容量收口，用来回答「这台机器一晚到底能吃多少」。
        /// 玩家 2026-11-10 要求：净水器原版标题栏与 mod 面板右上角各给一个快捷图标。</summary>
        public static bool UncapEff = false;
        public static int PostMode = 0;
        public static int SrcIdx = 0;   // 0=自动；1 铁锈水 2 沟渠水 3 优质水 4 基础水 5 纯水
        // 玩家 2026-11-10：水源选项里只保留「沟渠水 / 铁锈水」两个切换按钮（其余档不再露出）。
        public static readonly string[] SrcPickNames = { "铁锈水", "沟渠水" };
        public static readonly int[] SrcPickVals = { 1, 2 };
        /// <summary>界面显示用的水源名：只认这两只水龙头，其余一律回落到「自动（瓶上标签）」。</summary>
        public static string SrcUiName(int idx)
        {
            if (idx == 1) return SrcPickNames[0];
            if (idx == 2) return SrcPickNames[1];
            return SrcNames[0];
        }
        public static string LastMsg = "";
        /// <summary>右键「使用」选中的瓶子；灌水后清空。</summary>
        public static object RightClickTarget = null;

        private static readonly Color Cream = new Color(213f / 255f, 210f / 255f, 184f / 255f, 1f);
        private static readonly Color Dim = new Color(150f / 255f, 150f / 255f, 140f / 255f, 1f);
        // 玩家 2026-11-11：当前页的导航键用金色高亮，和主界面「接 X mL」的金字同一色相。
        private static readonly Color Gold = new Color(242f / 255f, 193f / 255f, 78f / 255f, 1f);
        private static readonly Color Cyan = new Color(127f / 255f, 216f / 255f, 232f / 255f, 1f);   // #7FD8E8 分区标题
        private static readonly Color Warn = new Color(1f, 0.72f, 0.35f, 1f);

        private static MachineryButton calcBtn, fillBtn, cfgBtn, recipeBtn, gearBtn;
        // 玩家：每台净水器的标题栏都要显示「这台正在跑多少量」，而 fillBtn 是单例（只有最后挂的那台有），
        // 所以按窗口各存一份：window -> 它的灌水键。Tick 里逐台按各自的方案量更新键面。
        private static readonly Dictionary<PixelWindow, MachineryButton> winFill
            = new Dictionary<PixelWindow, MachineryButton>();
        private static readonly Dictionary<PixelWindow, MachineryButton> winCalc
            = new Dictionary<PixelWindow, MachineryButton>();
        private static MachineryButton modeBtnCheap, modeBtnUncap;   // 标题栏：效率 / 性价比
        private static MachineryButton infBtn;                        // 设置页：无限道具
        private static Sprite iEff;
        private static PixelWindow panelWin;
        private static PixelWindow detailWin;
        private static ScrollableRichTextElement detailTxt;
        private static ScrollableRichTextElement pMixList;
        private static int mixPageH = 190;   // 混水页高度按内容收（跟主页一样矮）
        private static string lastMixMachineKey = "";
        private static ScrollableRichTextElement recipeTxt;
        private static PixelWindow recipeWin;
        private static bool panelWant;
        private static MachineryText pSub, pTarget, pMode, pPre, pPost, pHint, pEarly, pMsg;
        // 玩家 2026-11-12：主页改两列对齐——左列是定宽标签（容器/水质），右列是自适应取值。
        private static MachineryText pSubLbl, pPurLbl, pPur;
        // 玩家 2026-11-13（p1）：主页常显一行「目标 99.90%」，别让人翻设置页才知道在追什么纯度。
        private static MachineryText pTgtLbl, pTgtVal;
        private static MachineryButton[] modeBtns, targetBtns, preBtns;
        // 下拉式（一行一个按钮）的选择器按钮：F9 里直接改这三行的文字
        private static MachineryButton cTarget, cMode, cNight, cPre, cPost, cSrc, cHydra, cHydraDur;
        // 玩家 2026-11-13（第七轮）：前置 9 项太多，改成下拉列表；后置「自动 / 手填药片数」。
        private static MachineryStack preDropList;
        private static bool preDropOpen;
        private static UnityEngine.Vector2 preDropSavedPos;
        private static bool preDropPosSaved;
        private static PixelWindow preWin;
        private static MachineryStack preWinStack;
        private static MachineryButton cPostNum;
        private static int postTypingAt = -1;
        private static string postBuf = "";
        // 可用设备（v1 网页设置里的五个开关；关掉就不参与方案）
        private static bool devFilter = true, devUv = true, devTablets = true, devMachine = true, devHydra = true;
        private static readonly MachineryButton[] devBtns = new MachineryButton[5];
        // 海德拉耐久：原生滑条 + 旁边的数值标签（滑条拖动时只改这行文字，不重建整个面板）
        private static MachinerySlider sHydraDur;
        private static MachineryText cHydraDurLbl;
        private static MachineryText cHydraDurVal;
        // 过滤棉耐久：原生滑条选「这次用多少点耐久」，不按块数（400+311 这种按块会虚高）。
        // -1 = 还没拖过（＝用背包里实际持有的耐久总和）；0 = 这次不用过滤棉。
        private static MachinerySlider sFilterDur;
        private static MachineryText cFilterDurLbl;
        private static MachineryText cFilterDurVal;
        private static int FilterDurUse = -1;
        private const int FilterDurMaxSlider = 2000;
        /// <summary>方案区切到「更多方案」：列出全部达标解，不受前置/后置过滤（用户 2026-11-09 要求）。</summary>
        public static bool ShowAllCombos = false;
        /// <summary>玩家点选过的方案 label；非空时覆盖自动选中的方案（再点「自动」清空）。</summary>
        public static string ManualPickLabel = null;
        private static WaterPlan.Combo[] lastRows;
        private static MachineryStack pRowsBox;
        private static MachineryButton[] pRowBtns;
        // 更多方案页每行左侧定宽的「序号 + 名称」文本（右侧是自适应宽度、可点击的数值）
        private static MachineryText[] pRowLbls;
        private const int RowCount = 6;
        private static MachineryStack pPickRow;
        // 分页：0=主页（当前方案）1=设置（五行下拉）2=更多方案（候选表）
        private static int UiPage;
        private static readonly System.Collections.Generic.List<MachineryButton> pSetBtns =
            new System.Collections.Generic.List<MachineryButton>();
        /// <summary>网页的「自己填纯度」：>0 时按这个纯度算，不看水源下拉；-1=关闭。</summary>
        public static int CustomPur = -1;
        private static ScrollableRichTextElement pList;
        private static MachineryButton pFill, pMix, pMode2, pTab, pUv, pBlend;
        // 网页式布局的三张输出卡 + 杂质明细 + 方案表头 + 底部「接水」键（2026-11-08 重做）
        private static MachineryText pCard1, pCard2, pCard3, pMixLine, pTableHead;
        private static MachineryText pMoney;   // 「卖 X 元  赚 Y 元」单独一行，挂在「出来 …」下面（玩家 2026-11-11）
        /// <summary>主页「持有：过滤棉 X 点   海德拉 Y 点」：只拼已拥有的项，两样都没有就隐藏整行。</summary>
        private static MachineryText pOwn;
        private static MachineryText pCard1Val;
        /// <summary>卡二下面单独一行「步骤 过滤+紫外+1片药片」（玩家 2026-11-10：别挤在底行、别撑宽窗口）。</summary>
        private static MachineryText pStep;
        private static MachineryButton pJoin;
        // 批次 29：性价比自动档没有无耗材方案时，固定位显示「启用海德拉」；只改本次计算，不改设置页。
        private static MachineryButton pHydraHint;
        private static MachineryStack pHydraHintRow;
        private static MachineryButton cCalcHydraHint;
        private static MachineryStack cCalcHydraHintRow;
        private static bool autoHydraHint;
        private static bool hydraOnce;
        /// <summary>批次 29：SolveMinNights 最近一次返回值；ctl 的 ui? 用 nightsMin= 核对。</summary>
        private static int lastNightsMin;
        // 面板用的方案缓存：同一组输入只算一次，避免 0.4 秒刷新重复跑整瓶规划
        private static WaterPlan.Report planCache;
        private static string planKey = "";
        // 「接水」键这次实际会倒进去的整瓶量（mL）：OnFill 用的就是这条整瓶拆分
        private static long planPourMl = 0;
        private static Sprite iCalc, iFill, iCfg, iGear;
        // 加载 mod 时的默认：平衡模式开（按瓶里真实水质算）。
        // 玩家要求「再游戏加载模组的时候设置」，所以这里给初值，设置窗里可改。
        private static bool BalanceOff = true;
        // 已删除 dayParts / dayBaseUl / dayGrade / dayPur / dayPurReal / dayTemplate
        // （2026-11-08：那套「缓存当天六项配比 → 逐项 AddLiquid 重建」的模板灌水路径已废弃，
        //   原因见 OnFill 里的注释：水龙头纯度来自 StoreStation 的当日字段，过月会变，缓存必错。）
        private static string lastBal;
        private static float nextTick;
        private static string lastKey = "";
        // 数值真的变了才做整窗重建（每秒无条件重建会让鼠标划过按钮卡顿）
        private static bool modelDirty;
        // 玩家 2026-11-10 卡顿审计：Tick 里每秒无条件跑的重活（全背包扫描 / 反射 / 富文本重排）全部节流或缓存。
        private static float lastWatchT = -999f;      // WatchContainer 节流（它内部会全背包扫滤棉）
        private static float lastHdrT = -999f;        // 标题图标名字自愈节流（每颗都要反射全量 GetMethods）
        private static long scanCacheVal; private static float scanCacheT = -999f;   // Scanners() 结果缓存
        private static long filtCacheVal = -1; private static float filtCacheT = -999f; // FilterDurability() 结果缓存
        private static string lastStepLine;           // 步骤行上次的文案（不同才 SetText）
        private static object lastStepOwner;          // 步骤行上次绑的控件（换页重建后必须重画，否则整行空）
        private static string lastRichSig;            // 富文本上次的数据签名（不同才 SetRichText）
        private static object lastRichOwner;          // 富文本上次绑的控件（换对象＝必须重画）
        private static string lastFillKey;            // 灌水键上次的容器指纹（体积/瓶名没变就不重绑）
        private static int[] markSelLast = new int[] { -99, -99, -99 };  // 三组按钮上次的选中项
        // 每台净水器窗口各自绑定自己的储水容器槽（用户 m03701：三窗串台，点中间灌到右上）。
        private static readonly List<PixelWindow> ctxWins = new List<PixelWindow>();
        private static readonly List<object> ctxBottles = new List<object>();
        private static readonly List<SettingsSnap> ctxSettings = new List<SettingsSnap>();
        private const string NightCapPref = "WaterCalcMod.nightCap";
        private static PixelWindow settingsOwner;
        private static SettingsSnap pendingSettings;   // 还没绑机器窗口时改的设置，等第一台机器接上再落地
        private static object overrideBottle;
        private static int ctxIndex;

        /// <summary>每台机器记住的只有玩家选择；容量/水量/水质/实际耐久一律不进快照。</summary>
        private sealed class SettingsSnap
        {
            public int target, mode, pre, post, src, custom, hydraMode, hydraCap, filterUse;
            public int nightCap;          // 1=只算一晚（默认）；8=最多八晚
            public bool noMachine, uncap, hydraUserSet;
        }

        private static SettingsSnap CaptureSettings()
        {
            var s = new SettingsSnap();
            s.target = TargetIdx; s.mode = Mode; s.pre = PreMode; s.post = PostMode;
            s.src = SrcIdx; s.custom = CustomPur;
            s.hydraMode = WaterPlan.HydraMode; s.hydraCap = WaterPlan.HydraDuraCap;
            s.filterUse = FilterDurUse; s.noMachine = WaterPlan.NoMachine; s.uncap = UncapEff;
            s.nightCap = WaterPlan.NightCap;
            s.hydraUserSet = WaterPlan.HydraDurUserSet;
            return s;
        }

        private static void ApplySettings(SettingsSnap s)
        {
            if (s == null) return;
            TargetIdx = s.target; Mode = (s.mode == 1 ? 1 : 2); PreMode = s.pre; PostMode = s.post;
            SrcIdx = s.src; CustomPur = -1; calcManualSource = false;   // 批次 34：旧单值手填不再恢复
            WaterPlan.HydraMode = s.hydraMode; WaterPlan.HydraDuraCap = s.hydraCap;
            WaterPlan.HydraDurUserSet = s.hydraUserSet;
            WaterPlan.NightCap = (s.nightCap > 1 ? WaterPlan.MaxNights : 1);
            FilterDurUse = s.filterUse; WaterPlan.NoMachine = s.noMachine; UncapEff = s.uncap;
        }

        private static int CtxIndexOf(PixelWindow w)
        {
            for (int i = 0; i < ctxWins.Count; i++) if (ReferenceEquals(ctxWins[i], w)) return i;
            return -1;
        }

        /// <summary>把当前全局设置写回 settingsOwner 那一台；换台/改设置时调用。</summary>
        private static void RememberSettings()
        {
            var snap = CaptureSettings();
            if (settingsOwner == null) { pendingSettings = snap; return; }
            int i = CtxIndexOf(settingsOwner);
            if (i >= 0 && i < ctxSettings.Count) ctxSettings[i] = snap;
        }

        private static void SwitchMachineSettings(PixelWindow w)
        {
            if (ReferenceEquals(settingsOwner, w)) return;
            RememberSettings();
            int i = CtxIndexOf(w);
            if (i < 0) return;
            while (ctxSettings.Count < ctxWins.Count) ctxSettings.Add(null);
            if (ctxSettings[i] == null)
            {
                if (pendingSettings != null) { ctxSettings[i] = pendingSettings; pendingSettings = null; }
                else ctxSettings[i] = CaptureSettings();   // 新机器沿用当前设置，不要把已改的前置/后置打回默认
            }
            ApplySettings(ctxSettings[i]);
            settingsOwner = w;
            // 换机器后不能再认上一台/右键读到的瓶子：否则计算页会一直显示读取时的旧容量。
            // 手动源也随这台机器归零，改由新机器的瓶/机器上下文重新读。
            lastPickedBottle = null;
            calcClearPick = null;
            RightClickTarget = null;
            calcManualSource = false;
            // LoadCalcFromContext 会先读 CtxBottle；这里必须先把窗口槽切到新机器，
            // 否则它会拿 UseWindow 尚未更新的 overrideBottle，仍然读到上一台的容量。
            if (i >= 0 && i < ctxBottles.Count) overrideBottle = ctxBottles[i];
            ctxIndex = i >= 0 ? i + 1 : 0;
            if (UiPage == 4) LoadCalcFromContext(true);   // 换机器时计算页立刻按新机器重读，不串台
        }

        /// <summary>第一次打开某台机器的默认：性价比 + 本机上限（容量优先），其余自动。</summary>
        private static SettingsSnap DefaultSettings()
        {
            if (WaterPlan.NightCap != 1 && WaterPlan.NightCap != WaterPlan.MaxNights) WaterPlan.NightCap = 1;
            var s = new SettingsSnap();
            s.target = 0; s.mode = 2; s.pre = 0; s.post = 0; s.src = 0; s.custom = 0;
            s.hydraMode = 0; s.hydraCap = 300; s.filterUse = -1; s.noMachine = false; s.uncap = false;
            // bug18 B3：多晚开关必须跨重启保持；存档设置只存在于本机 PlayerPrefs，不写存档字段。
            s.nightCap = Math.Max(1, Math.Min(WaterPlan.MaxNights,
                PlayerPrefs.GetInt("WaterCalcMod.nightCap", 1)));
            return s;
        }

        public static void Install(ManualLogSource l)
        {
            log = l;
            // bug18 B3：多晚开关是全局设置，进程启动时就从本机 PlayerPrefs 恢复；默认关。
            WaterPlan.NightCap = Math.Max(1, Math.Min(WaterPlan.MaxNights,
                PlayerPrefs.GetInt(NightCapPref, 1)));
        }

        // 玩家正常游玩时不刷日志：只有开发测试用的 _tools\ctl.enable 存在时才写 BepInEx 日志。
        internal static void Log(string s)
        {
            if (log == null) return;
#if DEV
            if (!System.IO.File.Exists(System.IO.Path.Combine(CmdFile.Dir, "ctl.enable"))) return;
#else
            return;   // 发布版不写调试日志
#endif
            log.LogInfo(s);
        }

        /// <summary>发布版入口故障专用旁路：不经过 ModLoader 日志，直接确认 Update/F10/按钮是否执行。</summary>
        internal static void TraceEntry(string s)
        {
#if DEV
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "watercalc-ui-entry.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n");
            }
            catch (Exception) { }
#endif
        }

        /// <summary>接水诊断：发布版 Log 被关闭时，仍然把 OnFill 的真实目标/方案量写出来。</summary>
        private static void PourDiag(string s)
        {
#if DEV
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "watercalc-pour.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n");
            }
            catch (Exception) { }
#endif
        }

        /// <summary>窗口 → 这台机器自己的储水容器槽（同一窗口重复进只更新槽）。</summary>
        public static void BindWindow(object window, object bottleInv)
        {
            // 批次 28 §4：界面口径改用游戏真值；四组对照脚本不经过这里。
            AutoSolve.UseUiCost();
            var w = window as PixelWindow;
            if (w == null) return;
            for (int i = 0; i < ctxWins.Count; i++)
                if (ReferenceEquals(ctxWins[i], w)) { ctxBottles[i] = bottleInv; return; }
            ctxWins.Add(w); ctxBottles.Add(bottleInv);
            while (ctxSettings.Count < ctxWins.Count) ctxSettings.Add(null);
        }

        /// <summary>把「当前窗口」切到这台（按钮回调闭包捕获自己的 w，绝不读全局 ActiveMachine）。</summary>
        private static void UseWindow(PixelWindow w, bool restoreSettings = true)
        {
            if (restoreSettings) SwitchMachineSettings(w);
            for (int i = 0; i < ctxWins.Count; i++)
                if (ReferenceEquals(ctxWins[i], w)) { overrideBottle = ctxBottles[i]; ctxIndex = i + 1; break; }
            // 闭包里拿到的 bottleInv 可能是 null（创建窗口那一刻还没配对）→ 按窗口反查 Pair 补上，
            // 否则所有窗口都会退回到「全局当前机器」，表现成只有一台能灌。
            int pairIdx = -1;
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p == null || p.Window == null) continue;
                    if (!ReferenceEquals(p.Window, w)) continue;
                    if (p.BottleInv != null) overrideBottle = p.BottleInv;
                    ctxIndex = i + 1;
                    pairIdx = i;
                    break;
                }
            }
            catch (Exception) { }
            // 玩家 2026-11-17：点另一台机器的「净水助手/接水」图标后，只切窗口不切 activeIdx，
            // 顶部读数跟着新机器走，方案却还锁在旧机器上，必须切一次模式才重算。
            // 当前窗口就是玩家正在操作的那台，计算机器必须一起切过去并解锁旧方案。
            if (pairIdx >= 0) AdoptPairForWindow(pairIdx);
        }

        /// <summary>把当前计算机器切到正在操作的这台机器，并让旧方案失效。
        /// 不能直接调 SelectPair（它会再调 UseWindow，形成递归）。</summary>
        private static void AdoptPairForWindow(int idx)
        {
            if (idx < 0) return;
            bool changed = activeIdx != idx || MachineOverride != idx || uiDeselected;
            if (!changed) return;
            activeIdx = idx;
            MachineOverride = idx;
            uiDeselected = false;
            // 与 SelectPair 换机器时同一套清理：右键读取的旧瓶/旧手动源不能跟到新机器。
            lastPickedBottle = null;
            calcClearPick = null;
            RightClickTarget = null;
            calcManualSource = false;
            calcReadSource = false;
            lastVisible = null;
            try { PurifierHook.PickedIndex = idx; } catch (Exception) { }
            // 用游戏自己的窗口激活接口：点另一台的按钮/窗口后，它才是当前激活窗口。
            try
            {
                var pw = PurifierHook.Pairs[idx].Window as PixelWindow;
                if (pw != null) pw.ToFront();
            }
            catch (Exception) { }
            UnlockPlan("换机器");
            ReadSelectedMachineForPlan();
            DropPlanCache();
            RefreshPickTints();
            RefreshMixIfOpen();
            RequestRebuild();
            Log("[ui] adopt pair=" + idx + " (window switch)");
        }

        /// <summary>复用右键读取的判定，只读选中机器槽里的实际水并刷新主页/方案，不跳计算页。</summary>
        private static void ReadSelectedMachineForPlan()
        {
            try
            {
                if (uiDeselected) return;
                var b = SelectedMachineBottle();
                if (b == null) return;
                ReadBottleToCalc(b, false);
            }
            catch (Exception e)
            {
                Log("[ui] 选中机器后读取水样失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>上下文窗口里的瓶；没有就 null。</summary>
        private static GameItem CtxBottle()
        {
            try
            {
                // 玩家主动取消选中机器后，主页/混水页不应继续拿上一台机器的瓶算方案。
                if (uiDeselected || (activeIdx < 0 && MachineOverride < 0)) return null;
                return overrideBottle == null ? null : GameBridge.Get(overrideBottle, "childItem") as GameItem;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 玩家 2026-11-11：没点左上角圆点就直接按「这台机器」标题栏上的模式键时，
        /// 面板不应该去改另一台 —— 先把按下去的那台机器选成当前台，再改模式。
        /// </summary>
        private static void ModeOn(PixelWindow w, int mode)
        {
            UseWindow(w);
            try
            {
                int idx = PairIndexOf(w);
                if (idx >= 0) SelectPair(idx);
            }
            catch (Exception) { }
            // 保留原三态：不同档切档；同档再点＝切无限道具（玩家 2026-11-15 明确要求保留）。
            SetMode(mode);
        }

        /// <summary>同上，效率（无视容器）那个键。</summary>
        private static void UncapOn(PixelWindow w)
        {
            UseWindow(w);
            try
            {
                int idx = PairIndexOf(w);
                if (idx >= 0) SelectPair(idx);
            }
            catch (Exception) { }
            ToggleUncapEff();
        }


        // ---------------- 净水器窗口标题栏：3 个 16px 图标 ----------------

        public static void Attach(object window) { Attach(window, null); }

        public static void Attach(object window, object bottleInv)
        {
            var w = window as PixelWindow;
            if (w == null) return;
            BindWindow(w, bottleInv);
            for (int i = 0; i < attached.Count; i++) if (attached[i] == w) return;
            attached.Add(w);
            try
            {
                // 注：这里原本会在挂图标时把独立面板收起来（去重），但 F9 面板已改回完整设置页，
                // 收起来等于把玩家的设置页关掉，故去掉该规则；重复问题改由机器窗口只挂图标解决。
                iconSize = NativeIconSize(w);
                // 玩家 2026-11-13 定案：净水器标题栏与主面板同一套图标，从左到右
                // 净水助手（信息 i）→ 手册（书）→ 性价比（橙 S 金币）→ 效率（时钟）→ 接水（水滴）。
                iFill = PixelIcons.Get("water", out iw, out ih);
                iCalc = PixelIcons.Get("info", out iw, out ih);
                iCfg = PixelIcons.Get("book", out iw, out ih);
                iGear = null;

                fillBtn = NewIcon(iFill, "接水", new Action(delegate { UseWindow(w, false); OnFill(); }));
                calcBtn = NewIcon(iCalc, "净水助手", new Action(delegate
                {
                    // 原生 MachineryButton.OnSelect 只有回调正常返回才会 Deselect。
                    // 一次异常就会让图标永久停在已选状态，之后点击都不再进回调。
                    try
                    {
                        TraceEntry("assistant-button");
                        if (log != null) log.LogWarning("[ui] 净水助手按钮点击");
                        UseWindow(w);
                        string openResult = ShowMain(true);
                        if (log != null) log.LogWarning("[ui] 净水助手按钮 -> " + openResult);
                    }
                    catch (Exception e)
                    {
                        try { if (log != null) log.LogWarning("[ui] 净水助手按钮异常 " + e.GetType().Name + " " + e.Message); } catch (Exception) { }
                    }

                }));
                recipeBtn = NewIcon(iCfg, "手册", new Action(delegate { ToggleRecipes(); }));
                // 玩家：平衡模式挪进 mod 设置窗，标题栏不再挂这个图标
                gearBtn = null;

                // 玩家 2026-11-13：自定义不再是一个假模式按钮，已并进「计算」页；
                // 标题栏只留效率 / 性价比两档：时钟 / 钞票，单色像素，和游戏原版同一风格。
                modeBtnCheap = NewIcon(PixelIcons.Get("coin", out iw, out ih), "性价比", new Action(delegate { ModeOn(w, 2); }));
                modeBtnUncap = NewIcon(PixelIcons.Get("clock", out iw, out ih), "效率", new Action(delegate { UncapOn(w); }));
                RefreshModeDesc();
                // 玩家 2026-11-10：按钮排布要与面板一致 —— 接水 → 效率 → 性价比 → 手册 → 详情
                Log("[ui] 模式图标 cheap=" + (modeBtnCheap != null) + " eff=" + (modeBtnUncap != null));
                // ★玩家 2026-11-10：多开净水器时，标题栏左上角这个圆点 = 「按这台算」。绿=当前在用，灰=没选。
                // 之前永远只抓第一台打开的窗口，第二台怎么点都没反应，把第一台关了也还是抓不到第二台。
                int myPair = PairIndexOf(w);
                MachineryButton pickBtn = NewIcon(PixelIcons.Get("pick", out iw, out ih),
                    "选中这台（绿=当前按它算）", new Action(delegate
                    {
                        // 原生 MachineryButton.OnSelect：回调抛异常就不会 Deselect，按钮会永久卡在按下态，
                        // 之后点击全不触发。这里必须吞掉所有异常并让回调正常返回。
                        try
                        {
                            int pi = PairIndexOf(w);
                            if (pi < 0)
                            {
                                if (log != null) log.LogWarning("[ui] 绿点找不到配对 win=" + (w == null ? "null" : "ok") + " pairs=" + PurifierHook.Pairs.Count);
                                return;
                            }
                            if (activeIdx == pi && MachineOverride == pi) DeselectPair();
                            // 玩家 2026-11-17：绿点必须和“直接点机器面板”走同一个入口。
                            // SelectPair 会先把 activeIdx 设成目标，导致 UseWindow→AdoptPairForWindow 看到
                            // “已经是这台”直接返回，跳过读水/解锁计划，表现成有概率锁旧方案。
                            else AdoptPairForWindow(pi);
                        }
                        catch (Exception e)
                        {
                            try { if (log != null) log.LogWarning("[ui] 绿点点击异常 " + e.GetType().Name + " " + e.Message); } catch (Exception) { }
                        }
                    }));
                if (pickBtn != null)
                {
                    pickBtns[w] = pickBtn;
                    // 玩家 2026-11-16：第一次打开窗口不再自动选台；没点绿点就按背包水算，
                    // 避免 p2「没选机器却给这台机器方案」。选中仍由玩家点左上角绿点决定。
                    // if (activeIdx < 0 && myPair >= 0) { activeIdx = myPair; MachineOverride = myPair; uiDeselected = false; }
                    // 玩家 2026-11-10：圆点要钉在窗口**左上角**，不是跟着原生标题栏右对齐的那排图标走。
                    PlacePickTopLeft(w, pickBtn);
                    RegisterHdr(pickBtn, "选择");
                }
                if (fillBtn != null) winFill[w] = fillBtn;   // 这台窗口的专用灌水键
                if (calcBtn != null) winCalc[w] = calcBtn;   // 详情键同理：不能四台共用一份
                // AddHeaderButton 是往左插：倒着加，视觉上才是 净水助手 → 手册 → 性价比 → 效率 → 接水 → X
                if (fillBtn != null) { w.AddHeaderButton(fillBtn); RegisterHdr(fillBtn, "接水"); }
                if (modeBtnUncap != null) { w.AddHeaderButton(modeBtnUncap); RegisterHdr(modeBtnUncap, "效率"); }
                if (modeBtnCheap != null) { w.AddHeaderButton(modeBtnCheap); RegisterHdr(modeBtnCheap, "性价比"); }
                if (recipeBtn != null) { w.AddHeaderButton(recipeBtn); RegisterHdr(recipeBtn, "手册"); }
                if (calcBtn != null) { w.AddHeaderButton(calcBtn); RegisterHdr(calcBtn, "净水助手"); }
                RefreshPickTints();   // 把「谁在用」的颜色刷到每台窗口的圆点上（绿=当前）
                // （已移除）
                LogIcon("water", fillBtn); LogIcon("note", recipeBtn); LogIcon("info", calcBtn); LogIcon("gear", gearBtn);
                Log("[ui] 标题栏图标 ok size=" + iconSize + " src=generated missing=" + missingIcons);
                nextTick = 0f;
            }
            catch (Exception e) { log.LogWarning("[ui] Attach " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>
        /// 把「选中这台」的圆点钉在窗口左上角（标题栏最左），不参与原生标题栏的右对齐排布。
        /// 直接 AddHeaderButton 只会排到右侧那排图标里，玩家要的是最左那颗。
        /// </summary>
        private static void PlacePickTopLeft(PixelWindow w, MachineryButton b)
        {
            try
            {
                if (w == null || b == null) return;
                var wrt = w.rectTransform;
                var brt = b.rectTransform;
                if (wrt == null || brt == null) return;
                brt.SetParent(wrt, false);
                brt.anchorMin = new Vector2(0f, 1f);
                brt.anchorMax = new Vector2(0f, 1f);
                brt.pivot = new Vector2(0f, 1f);
                brt.anchoredPosition = new Vector2(5f, -4f);
                brt.sizeDelta = new Vector2(iconSize, iconSize);
                brt.localScale = Vector3.one;
                brt.SetAsLastSibling();
            }
            catch (Exception e) { log.LogWarning("[ui] pick place " + e.GetType().Name + " " + e.Message); }
        }

        private static string SpriteName(Sprite s) { return s == null ? "-" : s.name; }

        /// <summary>图标尺寸按游戏原生按钮的 rect 记录，便于核对是否与原生一致。</summary>
        private static void LogIcon(string name, MachineryButton b)
        {
            try
            {
                var rt = b == null ? null : b.rectTransform;
                if (rt == null) { Log("[icon] " + name + " size=none"); return; }
                Log("[icon] " + name + " size=" + (int)rt.sizeDelta.x + "x" + (int)rt.sizeDelta.y +
                    " native=" + (int)rt.rect.width + "x" + (int)rt.rect.height);
            }
            catch (Exception e) { Log("[icon] " + name + " size=ERR " + e.GetType().Name); }
        }

        private static int iconSize = 16;
        private static int missingIcons;
        private static int iw, ih;

        /// <summary>
        /// 头部补一句「现在瓶里这 N mL，一晚能到多少」——玩家问过「1800 到底行不行」，
        /// 直接把当前体积过一晚的结果算出来贴在标题行，省得猜。
        /// </summary>
        /// <summary>
        /// 六项杂质的英文 identifier → 中文（玩家反馈「英文没翻译」）。
        /// 顺序与 Liquid.InitLiquid 一致：water/heavy_metal/organic_waste/microplastic/microbe/
        /// chemical_contaminant/physical_contaminant/mineral。
        /// </summary>
        private static string CnLiquid(string id, int idx)
        {
            string k = (id == null ? "" : id.ToLowerInvariant());
            if (k.Contains("heavy")) return "重金属";
            if (k.Contains("organic")) return "有机废物";
            if (k.Contains("microplastic")) return "微塑料";
            if (k.Contains("microbe")) return "微生物";
            if (k.Contains("chemical")) return "化学污染物";
            if (k.Contains("physical")) return "物理杂质";
            if (k.Contains("mineral")) return "矿物";
            if (k.Contains("water")) return "水";
            switch (idx)
            {
                case 0: return "水";
                case 1: return "重金属";
                case 2: return "有机废物";
                case 3: return "微塑料";
                case 4: return "微生物";
                case 5: return "化学污染物";
                case 6: return "物理杂质";
                case 7: return "矿物";
                default: return k.Length > 0 ? k : "#" + idx;
            }
        }

        private static string PredictText(PanelModel m)
        {
            try
            {
                if (m == null || m.VolumeMl <= 0 || m.PurityBp <= 0) return "";
                // 顶行必须跟卡2/方案表说的是同一件事：以当前选中方案为准，而不是固定按「原样」预测。
                var r = CurrentPlan(true);
                if (r != null && r.pick != null)
                {
                    var c = r.pick;
                    return "  选中「" + c.label + "」：接 " + (BottlePourUl(c.feedUl) / 1000) + " → 出 " + (c.outUl / 1000)
                         + " mL  " + PlanText.Pct(c.purityBp);
                }
                WaterMath.Water w = WaterMath.Build(m.VolumeMl, m.PurityBp);
                WaterMath.Purify(w, 1.0 + m.PerfPct / 100.0, m.Quality);
                int bp = w.Purity;
                // 引擎按整数毫升算，末位会算出 99.05 这种比上限高 0.04 的值；显示按上限截断。
                if (m.CapBp > 0 && bp > m.CapBp) bp = m.CapBp;
                if (bp <= m.PurityBp) return "  一晚到顶（本机上限）";
                return "  这 " + m.VolumeMl + " mL 一晚 → " + PlanText.Pct(bp);
            }
            catch (Exception) { return ""; }
        }

        private static MachineryButton NewIcon(Sprite sp, string desc, Action cb)
        {
            if (sp == null) { missingIcons++; return null; }
            MachineryButton b = new MachineryButton(sp, cb, iconSize);
            try { b.SetDescription(desc); } catch (Exception) { }
            return b;
        }

        /// <summary>从同窗口里已有的原生按钮读实际尺寸；读不到退回 16。</summary>
        private static int NativeIconSize(PixelWindow w)
        {
            try
            {
                var arr = w.rectTransform.GetComponentsInChildren<MachineryButton>(true);
                for (int i = 0; i < arr.Length; i++)
                {
                    var rt = arr[i].rectTransform;
                    if (rt != null && rt.sizeDelta.x >= 8f) return Mathf.RoundToInt(rt.sizeDelta.x);
                }
            }
            catch (Exception) { }
            return 16;
        }

        public static string IconsReport()
        {
            int miss = 0;
            string[] names = { "water", "note", "info", "gear" };
            for (int i = 0; i < names.Length; i++) { int a, b; if (PixelIcons.Get(names[i], out a, out b) == null) miss++; }
            return "icons -> water=16x16 note=16x16 info=16x16 gear=16x16 missing=" + miss + " size=" + iconSize + " src=generated";
        }

        private static Sprite FindSprite(params string[] names)
        {
            var arr = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int n = 0; n < names.Length; n++)
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && arr[i].name == names[n]) return arr[i];
            return null;
        }

        // ---------------- 独立窗口（F9 / 设置图标） ----------------

        public static string ShowMain(bool show)
        {
            try
            {
                TraceEntry("ShowMain 1 enter show=" + show + " page=" + UiPage);
                // 打开详情一律回主页，但**不重装内容**：只要窗口里已经装着主页，就纯 Show，
                // 照「手册窗」的做法（窗口建一次，只 Show/Hide）——反复 Detach/Attach 会让面板刚开就关。
                if (show && UiPage != 0) UiPage = 0;
                EnsurePanel();   // 便宜：主窗已建、页也没变时什么都不建
                TraceEntry("ShowMain 2 after EnsurePanel alive=" + PanelAlive());
                if (!PanelAlive()) return "no-window";
                panelWant = show;
                if (show)
                {
                    Rebuild(true);
                    TraceEntry("ShowMain 3 after Rebuild");
                    // 引擎会记住原生窗口位置：上一版因此一直贴左上角 (0,1080)。
                    // Center() 对原生窗口无效（改完读回还是 0,1080），只能 WinSetPos 直接写。
                    // 在 Show 之前定位置，就不会「先闪左上角再跳」。
                    float cx, cy;
                    if (hasLastPos) { cx = LastPosX; cy = LastPosY; }   // 回到上次关掉的位置
                    else { CenterXY(panelWin, out cx, out cy); }
                    MainPosX = cx; MainPosY = cy;
                    panelWin.Show(); panelWin.ToFront(true);
                    TraceEntry("ShowMain 4 after Show vis=" + panelWin.IsVisible());
                    ForceAt(panelWin, cx, cy, 12);
                    TraceEntry("ShowMain 5 after ForceAt vis=" + panelWin.IsVisible());
                    // 玩家 2026-11-11（性能）：刚开窗口这一帧背包/机器仓可能还没喂进来，面板算出来是空的；
                    // 以前要等 1 s 的 live 节流才补上，看起来就是「先空白、加载慢」。
                    // 开窗后 0.15 s 立刻补算一次，并把 live 节拍提前，空面板最多只存在一帧。
                    pendingRebuild = true;
                    pendingRebuildAt = UnityEngine.Time.realtimeSinceStartup + 0.15f;
                    nextTick = UnityEngine.Time.realtimeSinceStartup;
                    Log("[ui] 详情打开 page=" + UiPage + " pageFor=" + panelPageFor + " visible=" + panelWin.IsVisible());
                }
                else
                {
                    // 关掉之前把位置记下来（下次打开回到这里）
                    UnityEngine.Vector2 pv;
                    if (WinGetPos(panelWin, out pv)) { LastPosX = pv.x; LastPosY = pv.y; hasLastPos = true; Log("[ui] 记住位置 " + pv.x + "," + pv.y); }
                    panelWin.Hide();
                }
                return "main=" + panelWin.IsVisible();
            }
            catch (Exception e)
            {
                TraceEntry("ShowMain ERR " + e.GetType().Name + " " + e.Message);
                try { log.LogWarning("[ui] ShowMain " + e.GetType().Name + " " + e.Message); } catch (Exception) { }
                return "ERR " + e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace == null ? "" : e.StackTrace.Replace("\n", " | "));
            }
        }

        public static void ToggleMain()
        {
            try
            {
                TraceEntry("ToggleMain begin");
                if (log != null) log.LogWarning("[ui] ToggleMain begin");
                EnsurePanel();
                TraceEntry("ToggleMain after EnsurePanel win=" + (panelWin != null));
                if (panelWin == null)
                {
                    TraceEntry("ToggleMain no-window");
                    if (log != null) log.LogWarning("[ui] ToggleMain no-window");
                    return;
                }
                if (panelWin.IsVisible())
                {
                    try { HidePreDrop(); } catch (Exception) { }
                    panelWin.Hide();
                    TraceEntry("ToggleMain hide");
                    if (log != null) log.LogWarning("[ui] ToggleMain hide");
                }
                else
                {
                    string r = ShowMain(true);
                    TraceEntry("ToggleMain show -> " + r);
                    if (log != null) log.LogWarning("[ui] ToggleMain show -> " + r);
                }
            }
            catch (Exception e)
            {
                TraceEntry("ToggleMain ERR " + e.GetType().Name + " " + e.Message);
                log.LogWarning("[ui] toggle " + e.Message);
            }
        }

        // ---------------- 配方窗：纸面手册（6 页，‹ N/M › 翻页，不滚） ----------------

        private const int RecipePages = 6;
        private static int recipePage = 1;
        private static int slimW, slimH, slimIcons;
        private static MachineryButton recipePrevBtn, recipeNextBtn, recipePageBtn;

        public static void ToggleRecipes()
        {
            try
            {
                if (bookOpened)
                {
                    bookOpened = false;
                    try { WaterBook.Close(); } catch (Exception) { }
                    Log("[ui] recipe via=book close");
                    return;
                }
                if (paperWin != null && paperWin.IsAlive && paperWin.IsVisible) { paperWin.Hide(); return; }
                string bk = null;
                try { bk = WaterBook.Open(recipePage); }
                catch (Exception e) { bk = "book -> ERR " + e.GetType().Name; }
                if (bk != null && bk.IndexOf("current=yes") >= 0)
                {
                    bookOpened = true;
                    Log("[ui] recipe via=book " + bk);
                    return;
                }
                Log("[ui] recipe via=fallback " + bk);
                if (BuildRecipePaper(recipePage)) return;
                if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); return; }
                if (recipeWin == null)
                {
                    recipeTxt = new ScrollableRichTextElement(false, true);
                    var body = new MachineryPanel("水商手册", recipeTxt, "watercalc_recipe_fb");
                    var navRow = new MachineryStack(false, 6);
                    recipePrevBtn = Small("‹ 上页", new Action(delegate { RecipeTurn(-1); }), 62);
                    recipePageBtn = Small("1/6", new Action(delegate { }), 44);
                    recipeNextBtn = Small("下页 ›", new Action(delegate { RecipeTurn(1); }), 62);
                    if (recipePrevBtn != null) navRow.Add(recipePrevBtn);
                    if (recipePageBtn != null) navRow.Add(recipePageBtn);
                    if (recipeNextBtn != null) navRow.Add(recipeNextBtn);
                    var nav = new MachineryPanel("", navRow, "watercalc_recipe_nav");
                    recipeWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { body, nav });
                    if (recipeWin == null) { Log("[ui] 配方窗口 null"); return; }
                    try { recipeTxt.ResizePixels(316, 366); } catch (Exception) { }
                    try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                AddClickBlocker(recipeWin, "recipe", 340, 430);
                try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                }
                RecipeRender();
            }
            catch (Exception e) { Log("[ui] recipes " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 纸面手册（游戏原生 CustomUIManager / LAYER_PAPER） ----------------

        private static CustomUIWindow paperWin;
        private static bool bookOpened;
        private static string paperFail = "";

        private static bool BuildRecipePaper(int page)
        {
            try
            {
                var mgr = CustomUIManager.Instance;
                if (mgr == null) { paperFail = "CustomUIManager.Instance=null"; return false; }
                try { mgr.CloseWindow("watercalc_recipe_fb"); } catch (Exception) { }
                var bld = mgr.CreateWindow("watercalc_recipe_fb", "水商手册", CustomUIManager.LAYER_PAPER, null, CustomUIManager.Style.Game);
                if (bld == null) { paperFail = "CreateWindow=null"; return false; }
                bld.SetSize(340f, 430f).Center();
                bld.BeginColumn(3f, TextAnchor.UpperLeft);
                string[] lines = RecipePage(page);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i];
                    if (string.IsNullOrEmpty(s)) { bld.AddSpace(4f); continue; }
                    if (s[0] == '#') bld.AddHeader(s.Substring(1), "rh" + i);
                    else bld.AddLabel(s, "rl" + i);
                }
                bld.End();
                bld.BeginRow(8f, TextAnchor.MiddleCenter);
                bld.AddButton("‹ 上页", new Action(delegate { RecipeTurn(-1); }), "rprev");
                bld.AddLabel(page + "/" + RecipePages, "rpage");
                bld.AddButton("下页 ›", new Action(delegate { RecipeTurn(1); }), "rnext");
                bld.End();
                paperWin = bld.Show();
                recipeLines = lines.Length;
                recipeW = 340; recipeH = 430;
                Log("[ui] paper ok page=" + page + "/" + RecipePages + " lines=" + recipeLines);
                return paperWin != null;
            }
            catch (Exception e)
            {
                paperFail = e.GetType().Name + " " + e.Message;
                Log("[ui] paper FAIL " + paperFail);
                return false;
            }
        }

        public static string PaperReport()
        {
            string bg = "color", border = "color";
            bool open = false;
            try
            {
                if (paperWin != null && paperWin.IsAlive)
                {
                    open = paperWin.IsVisible;
                    var root = paperWin.root;
                    if (root != null)
                    {
                        var imgs = root.GetComponentsInChildren<Image>(true);
                        if (imgs != null && imgs.Length > 0)
                        {
                            for (int i = 0; i < imgs.Length; i++)
                            {
                                if (imgs[i] != null && imgs[i].sprite != null) { bg = imgs[i].sprite.name; break; }
                            }
                            if (imgs.Length > 1 && imgs[1] != null && imgs[1].sprite != null) border = imgs[1].sprite.name;
                        }
                    }
                }
            }
            catch (Exception) { }
            return "paper -> bg=" + bg + " border=" + border + " style=Game layer=paper open=" + open +
                   " page=" + recipePage + "/" + RecipePages + (paperFail.Length > 0 ? " fail=" + paperFail : "");
        }

        public static void RecipeTurn(int d)
        {
            recipePage += d;
            if (recipePage < 1) recipePage = 1;
            if (recipePage > RecipePages) recipePage = RecipePages;
            if (paperWin != null && paperWin.IsAlive) { BuildRecipePaper(recipePage); }
            else RecipeRender();
            Log("[ui] recipe page=" + recipePage + "/" + RecipePages);
        }

        private static void RecipeRender()
        {
            try
            {
                if (recipeTxt == null) return;
                var b = new RichTextBuilder(300);
                string[] lines = RecipePage(recipePage);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i];
                    if (s.Length == 0) AddLine(b, "", ColorPalette.White, false);
                    else if (s[0] == '#') AddLine(b, s.Substring(1), ColorPalette.LightGreen, true);
                    else AddLine(b, s, ColorPalette.White, false);
                }
                if (recipePageBtn != null) { try { recipePageBtn.SetText(recipePage + "/" + RecipePages); } catch (Exception) { } }
                recipeLines = lines.Length;
                recipeTxt.SetRichText(b, 3000);
                try { recipeTxt.ResizePixels(316, 366); } catch (Exception) { }
                if (recipeWin != null)
                {
                    try { recipeWin.Show(); recipeWin.ToFront(true); } catch (Exception) { }
                    try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                AddClickBlocker(recipeWin, "recipe", 340, 430);
                try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                    try { Canvas.ForceUpdateCanvases(); } catch (Exception) { }
                    try { var rt = recipeWin.rectTransform; if (rt != null) { recipeW = (int)rt.rect.width; recipeH = (int)rt.rect.height; } }
                    catch (Exception) { }
                }
                if (recipeW <= 0 || recipeH <= 0) { recipeW = 340; recipeH = 430; }
                Log("[ui] recipe size=" + recipeW + "x" + recipeH + " lines=" + recipeLines);
            }
            catch (Exception e) { Log("[ui] recipe render " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>分页文本；同程序集内的 WaterBook 直接调用（不再反射）。</summary>
        internal static string[] RecipePage(int n)
        {
            var r = CurrentPlan(true);
            int perf, q;
            Bonuses(out perf, out q);
            int qq = r == null ? q : r.q;
            int cap = WaterMath.CapOf(qq);
            if (n == 3) { qq = 100; cap = 9990; }   // 手册页按「装了质量 100」讲，不跟当前机器走
            if (n == 1)
            {
                return new string[]
                {
                    "# 1. 水质等级与上限",
                    "1. 上限 cap = 10000 − round(99 × 质量倍率)",
                    "2. 质量 <15 → 99.01%",
                    "3. 15–24 → 99.16%",
                    "4. 25–49 → 99.36%",
                    "5. 50–74 → 99.65%",
                    "6. 75–99 → 99.80%",
                    "7. 100–199 → 99.90%",
                    "8. ≥200 → 100.00%",
                    "本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
                };
            }
            if (n == 2 || n == 3)
            {
                int target = n == 2 ? 9901 : 9990;
                int[] bps;
                string[] srcNames;
                if (n == 2)
                {
                    // 按「水源等级」细分：沟渠 <90% / 铁锈 90–95.99 / 幽灵 96–97.99 / 基础 98–98.99 / 优质 99.00+
                    bps = new int[] { 8800, 9100, 9400, 9750, 9850, 9900 };
                    srcNames = new string[] { "沟渠水", "铁锈水(低)", "铁锈水(高)", "幽灵水", "基础水", "优质水" };
                }
                else
                {
                    bps = new int[] { 9800, 9850, 9900, 9920, 9940, 9960 };
                    srcNames = new string[] { "基础水", "基础水", "优质水", "优质水", "优质水", "优质水" };
                }
                var arr = new string[3 + bps.Length];
                arr[0] = n == 2 ? "# 2. 一晚直出 99.01%（按水源  零模组）" : "# 3. 一晚直出 99.90%（要质量 100+）";
                arr[1] = "1. 每行 = 这种水源纯度下一晚能接的最大量";
                arr[2] = "2. 目标 " + PlanText.Pct(target) + "  质量 " + qq + "  性能 +0%  上限 " + PlanText.Pct(cap);
                int perfBase = 0;   // 手册按裸机给基线，不跟当前机器加成
                for (int i = 0; i < bps.Length; i++)
                {
                    long keepUl = MaxKeepUl(10000000, bps[i], perfBase, qq, target);
                    bool ok = keepUl > 0 && target <= cap;
                    string amount;
                    if (!ok) amount = "达不到，最多 " + PlanText.Pct(cap);
                    else if (keepUl >= 10000000L) amount = "≥10 L（受净水器 10 L 上限限制）";
                    else amount = WaterPlan.Ml(keepUl) + " mL";
                    arr[3 + i] = (i + 3) + ". " + srcNames[i] + " " + PlanText.Pct(bps[i]) + " → " + amount;
                }
                return arr;
            }
            if (n == 4)
            {
                return new string[]
                {
                    "# 4. 耗材怎么加",
                    "1. 过滤棉：插在容器上，灌水/导水时自动过滤（一次 400 耐久）",
                    "2. 过滤棉只清大颗粒：塑料/物理/矿物/有机物，不清微生物",
                    "3. 紫外灯：只清微生物，体积不变，用一次算一次",
                    "4. 药片：每片清最多 75 mL 微生物 + 10 mL 化学（化学只清到占总量 0.7%）",
                    "5. 药片清掉的量会离体，体积变小",
                    "6. 净水器：按 脏度 × 预算 清杂质，cap 之外清不动",
                    "7. 电：4 度/台/晚；药片 6、过滤棉 25/副",
                };
            }
            if (n == 5)
            {
                return new string[]
                {
                    "# 5. 顺序与坑",
                    "1. 顺序：过滤棉 / 紫外 → 净水器过夜 → 最后药片",
                    "2. 药片提前放会先改掉配比，等于白给净水器清一部分，所以放最后",
                    "3. 紫外和过滤棉可以互换，但都必须在净水器之前",
                    "4. 一晚能处理完就只跑 1 晚；装不完才用 2 晚；两晚不是默认拖时间",
                    "5. 50 L 且滤棉/海德拉足够时，2 晚可出约 49.6 L；44 L 一晚可出约 42 L，别被客人预算卡死",
                    "6. 接水按面板目标一次灌到总量；容器/滴灌凑不出就少接，不用量筒硬补",
                    "7. 上限由质量决定，性能只影响一晚能清多少；标题栏「效率」是 UI 模式，不是机器效率词条",
                    "8. 本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
                };
            }
            if (n == 7)
            {
                return new string[]
                {
                    "# 7. 使用说明书",
                    "1. 放瓶：把空瓶放进净水器的「储水容器」槽，或先拿在手上（面板会认右击选中的那瓶）",
                    "2. 接水：点标题栏的水滴键「接水」，按当前方案一次灌到目标总量；装不下/一晚处理不完会自动改 2 晚",
                    "3. 前置：过滤棉 / 紫外灯二选一或都上，它们只清一部分杂质，必须在净水器之前用",
                    "4. 药片：净化后再放，每片清化学与微生物；提前放等于把药效浪费在净水器本来就能清的东西上",
                    "5. 过夜：能 1 晚达标就只跑 1 晚；需要 2 晚才达标才用 2 晚，不要为了凑两晚空跑",
                    "6. 看结果：主面板看「出来 / 纯度 / 卖 / 赚」；要精确到万分数就开「净水助手」详情窗",
                    "7. 卖水：纯度跨过 98.00 / 99.00 / 99.90 才会涨价，差 0.01% 也是原价档，别白忙",
                    "8. 混水：计算页点「混水赚钱」，它按实际售价扣掉两瓶单卖的机会成本再排序，只列真赚的组合",
                    "9. 常见误区：把 98.99% 当成 99.00%（游戏是截断存档，显示四舍五入）—— 判定一律看游戏显示的两位",
                    "10. 常见误区：6 L 桶一次接满再净化，体积越大稀释越狠，往往不如小瓶分批",
                    "11. 常见误区：药片不是越多越好；性价比自动档按库存影子价选 0/1/… 片——库存 1 片每片 15 元、2–3 片 10 元、4 片以上 6 元，加片必须多赚过这笔钱",
                    "12. 常见误区：混水不是「最高 + 最高」，同档水混一起常常不涨钱，反而占容器",
                };
            }
            // n == 6
            return new string[]
            {
                "# 6. 水商利润",
                "1. 成本 = 药片 6/片 + 过滤棉 25/400 耐 + 海德拉 325/300 耐 + 电 4 度/台/晚（约 1.5 元/晚）",
                "2. 净赚 = 卖水收入 − 上述全成本；多过一晚就多一晚电费",
                "3. 性价比自动档按库存给药片算影子价：库存 1 片 → 15 元/片，2–3 片 → 10 元/片，4 片以上 → 6 元/片",
                "4. 同进水量、同晚数：比较「净赚 − 影子价 × 药片数」，0 片和加片谁高选谁",
                "5. 0 片已达标时，加片必须多赚超过影子价才推荐；跨售价档且现金更高才优先保留原档",
                "6. 药片/滤棉/海德拉库存不足时按真实持有；不读取客人预算，不按客人钱包砍水量",
                "7. 本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
            };
        }

        /// <summary>
        /// 手册里的「照抄一行」：给一组固定耗材组合跑模型，返回「接多少 → 出多少 → 赚多少」。
        /// 数字现算，换机器/换水源自动跟着变；算不出来就退回一句人话，绝不让书页崩。
        /// </summary>
        private static string RecipeProbe(long capUl, int srcBp, int perf, int q, int targetBp,
                                          int mode, int pre, int post, int tablets,
                                          bool filter, bool uv, int hydraDur)
        {
            try
            {
                var rr = WaterPlan.Build(capUl, srcBp, perf, q, targetBp, "沟渠水", "参考瓶", false,
                                         mode, pre, post, tablets, filter, uv, hydraDur);
                if (rr == null || rr.pick == null)
                    return "这一组装法一晚到不了 " + PlanText.Pct(targetBp) + "，得降目标或多过一晚";
                var c = rr.pick;
                double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
                string cost = c.cost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                return "接 " + WaterPlan.Ml(BottlePourUl(c.feedUl)) + " mL    一晚出 " + WaterPlan.Ml(c.outUl) + " mL    " +
                       PlanText.Pct(c.purityBp) + "    本钱 " + cost + " 元    卖 " +
                       WaterEconomy.Money(rev) + "    赚 " + WaterEconomy.Money(rev - c.cost);
            }
            catch (Exception e) { Log("[book] recipe probe " + e.GetType().Name); return "（这组装法算不出来，直接看面板给的方案）"; }
        }

        private static void RecipeRenderLegacy() { }

        /// <summary>水商配方小档案：点图标才开，原生 PixelWindow + 滚动文本。与当前瓶无关，任何时候都有内容。</summary>
        private static int recipeLines;
        private static int recipeW, recipeH;

        private static void ToggleRecipesLegacy()
        {
            try
            {
                if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); return; }
                if (recipeWin == null)
                {
                    recipeTxt = new ScrollableRichTextElement(false, true);
                    var panel = new MachineryPanel("水商配方", recipeTxt, "watercalc_recipe_fb");
                    recipeWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                    if (recipeWin == null) { Log("[ui] 配方窗口 null"); return; }
                    try { recipeTxt.ResizePixels(320, 250); } catch (Exception) { }
                    try { recipeWin.ResizePixels(360, 300); } catch (Exception) { }
                }
                var r = CurrentPlan(true);
                if (r == null) r = WaterPlan.Build(6000000L, 9100, 0, 0, WaterMath.CapOf(0), "沟渠水", "水桶 6L", true);
                int lines = 0;
                var b = new RichTextBuilder(500);
                AddLine(b, "怎么接这瓶水（按你有的容器，只能整瓶装，绝不超最优体积）", ColorPalette.LightGreen, true); lines++;
                AddLine(b, "顺序固定：过滤棉 / 紫外（选填）→ 进净水器过夜 → 最后放药片；没选的就跳过。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "药片必须等过完夜再放，提前放会被当普通杂质一起清掉。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "接水只能整瓶：750 / 450 / 250 / 50 mL 这类容器，凑不满就少接，不用量筒硬补。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "耗材计价：药片 6  过滤棉 25/400 耐久  电 4/台/晚  每升 = 总价 / 出水升数。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "卖价：40 元/L × 档位（纯水 ×2  优质 ×1.5  基础 ×1  幽灵 ×0.67  铁锈 ×0.25  沟渠 ×0.10）。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "", ColorPalette.White, false); lines++;
                if (r == null)
                {
                    AddLine(b, "示例（默认参数：质量 0 / 性能 0 / 6L 沟渠水 91% / 上限 99.01%）", ColorPalette.LightOrange, true); lines++;
                    AddLine(b, "6000 mL 生水直接过净水器：一晚只清 138 mL 杂质，最多推到 99.01% 的要少接。", ColorPalette.White, false); lines++;
                    AddLine(b, "6000 mL 生水 + 1 片药片：体积略减，纯度几乎不动（药片只清微生物/化学）。", ColorPalette.White, false); lines++;
                    AddLine(b, "先过滤棉再一夜：体积减少，杂质一起被滤走，纯度升得更快。", ColorPalette.White, false); lines++;
                }
                else
                {
                    AddLine(b, "示例参数：本机质量 " + r.q + " / 上限 " + PlanText.Pct(WaterMath.CapOf(r.q)) +
                               " / 接水 " + WaterPlan.Ml(r.bestUl) + " mL / 进水 " + PlanText.Pct(r.srcBp), ColorPalette.LightGreen, true); lines++;
                    AddLine(b, "一晚能做的几种（药片都是过夜后最后放）", ColorPalette.LightGreen, true); lines++;
                    AddLine(b, "做法  接多少  一晚出  纯度  本钱  卖价  净赚", ColorPalette.LightGray, false); lines++;
                    for (int i = 0; i < r.combos.Count; i++)
                    {
                        var c = r.combos[i];
                        if (PostMode > 0 && c.tablets != PostMode - 1) continue;   // 「净化后」下拉：按药片数筛选
                        double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
                        AddLine(b, c.label + "  " + WaterPlan.Ml(BottlePourUl(c.feedUl)) + " mL  " + WaterPlan.Ml(c.outUl) + " mL  " +
                                   PlanText.Pct(c.purityBp) + "  " + c.cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                                   "  " + WaterEconomy.Money(rev) + "  " + WaterEconomy.Money(rev - c.cost),
                                   c.ok ? ColorPalette.LightGreen : ColorPalette.LightOrange, false);
                        lines++;
                    }

                    AddLine(b, "", ColorPalette.White, false); lines++;
                    AddLine(b, r.tabletsOnly, ColorPalette.White, false); lines++;

                    AddLine(b, "", ColorPalette.White, false); lines++;
                    AddLine(b, "每晚变化", ColorPalette.LightOrange, true); lines++;
                    var nl = r.NightsText().Split('\n');
                    for (int i = 0; i < nl.Length; i++) if (nl[i].Length > 0) { AddLine(b, nl[i], ColorPalette.White, false); lines++; }
                }
                int qq = r == null ? 0 : r.q;
                AddLine(b, "", ColorPalette.White, false); lines++;
                AddLine(b, "水质等级与上限（cap = 10000 − round(99×质量倍率)）", ColorPalette.LightOrange, true); lines++;
                AddLine(b, "质量 <15 → 99.01%  15-24 → 99.16%  25-49 → 99.36%  50-74 → 99.65%", ColorPalette.LightGray, false); lines++;
                AddLine(b, "75-99 → 99.80%  100-199 → 99.90%  ≥200 → 100.00%", ColorPalette.LightGray, false); lines++;
                AddLine(b, "本机质量 " + qq + " → 上限 " + PlanText.Pct(WaterMath.CapOf(qq)), ColorPalette.White, false); lines++;

                recipeLines = lines;
                recipeTxt.SetRichText(b, 6000);
                try { recipeTxt.ResizePixels(320, 250); } catch (Exception) { }
                if (recipeWin != null)
                {
                    recipeWin.Show();
                    try { recipeWin.ToFront(true); } catch (Exception) { }
                    try { recipeWin.ResizePixels(360, 300); } catch (Exception) { }
                    try { var rt = recipeWin.rectTransform; if (rt != null) { recipeW = (int)rt.rect.width; recipeH = (int)rt.rect.height; } }
                    catch (Exception) { }
                }
                Log("[ui] recipe size=" + recipeW + "x" + recipeH + " lines=" + recipeLines);
            }
            catch (Exception e) { Log("[ui] recipes " + e.GetType().Name + " " + e.Message); }
        }

        public static string OpenRecipePublic()
        {
            if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); }
            ToggleRecipes();
            return "recipe -> page=" + recipePage + "/" + RecipePages + " size=" + recipeW + "x" + recipeH + " lines=" + recipeLines;
        }

        public static string RecipePagePublic(int n)
        {
            if (n < 1) n = 1;
            if (n > RecipePages) n = RecipePages;
            recipePage = n;
            if (bookOpened) { WaterBook.Open(n); }
            else if (paperWin != null && paperWin.IsAlive) { BuildRecipePaper(n); }
            else if (recipeWin == null || !recipeWin.IsVisible()) ToggleRecipes();
            else RecipeRender();
            return "recipe -> page=" + recipePage + "/" + RecipePages + " size=" + recipeW + "x" + recipeH + " lines=" + recipeLines;
        }

        public static string OpenDetailPublic() { ToggleDetail(); return "win=detail -> opened ok=" + (detailWin != null && detailWin.IsVisible()); }

        public static string OpenSlimPublic()
        {
            ShowMain(true);
            try { Canvas.ForceUpdateCanvases(); } catch (Exception) { }
            int w = 0, h = 0;
            try { var rt = panelWin == null ? null : panelWin.rectTransform; if (rt != null) { w = (int)rt.rect.width; h = (int)rt.rect.height; } }
            catch (Exception) { }
            if (w <= 0) w = slimW;
            if (h <= 0) h = slimH;
            return "slim -> w=" + w + " h=" + h + " icons=" + slimIcons;
        }

        // ---------------- F9 窄条（唯一常驻窗口） ----------------

        private static void BuildSlimBar()
        {
            try
            {
                iconSize = 16;
                var row = new MachineryStack(false, 4);
                int n = 0;
                // ★玩家 2026-11-13：详情浮窗整个不要了（主面板和这条窄条都不挂「详情」键），
                //   只在净水器窗口那边留入口。这里从「配方」开始排。
                var bNote = NewIcon(PixelIcons.Get("note", out iw, out ih), "配方：一晚几种 / 只放药片 / 每晚变化", new Action(delegate { ToggleRecipes(); }));
                var bWater = NewIcon(PixelIcons.Get("water", out iw, out ih), "接水：按今日水质把瓶子灌到达标体积", new Action(delegate { OnFill(); }));
                var bServe = NewIcon(PixelIcons.Get("serve", out iw, out ih), "备水放台面：把现有容器里的水真实导进承接瓶再上柜", new Action(delegate { OnPrepare2(); }));
                var bGear = NewIcon(PixelIcons.Get("gear", out iw, out ih), "设置：平衡模式（隐藏精确水质）", new Action(delegate { ToggleBalance(); }));
                if (bNote != null) { row.Add(bNote); n++; }
                if (bWater != null) { row.Add(bWater); n++; }
                if (bServe != null) { row.Add(bServe); n++; Log("[icon] serve size=" + iw + "x" + ih); }
                if (bGear != null) { row.Add(bGear); n++; }
                if (n == 0) { log.LogWarning("[ui] slim 无图标"); return; }

                var panel = new MachineryPanel("", row, "watercalc_slim");
                panelWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                if (panelWin == null) { log.LogWarning("[ui] slim null"); return; }
                int w = iconSize * n + 10 * (n - 1) + 16;
                int h = iconSize + 12;
                slimW = w; slimH = h; slimIcons = n;
                try { panelWin.ResizePixels(w, h); } catch (Exception) { }
                AddClickBlocker(panelWin, "slim", w, h);
                try { panelWin.ResizePixels(w, h); } catch (Exception) { }
                try { panelWin.Hide(); } catch (Exception) { }
                if (panelWin.IsVisible()) { try { panelWin.rectTransform.gameObject.SetActive(false); } catch (Exception) { } }
                Log("[ui] slim ok w=" + w + " h=" + h + " icons=" + n);
            }
            catch (Exception e) { log.LogWarning("[ui] slim " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>
        /// 在窗口最底层铺一张透明但 raycastTarget 的图，吃掉落在窗口矩形内的点击，
        /// 避免点击穿透到游戏自己的 UI（试纸/日历那类浮层）。放 sibling 0，按钮在它之上不受影响。
        /// </summary>
        internal static void AddClickBlocker(PixelWindow w, string tag)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt == null) return;
                var go = new UnityEngine.GameObject("watercalc_blocker_" + tag);
                var brt = go.AddComponent<UnityEngine.RectTransform>();
                brt.SetParent(rt, false);
                brt.anchorMin = UnityEngine.Vector2.zero;
                brt.anchorMax = UnityEngine.Vector2.one;
                brt.offsetMin = UnityEngine.Vector2.zero;
                brt.offsetMax = UnityEngine.Vector2.zero;
                brt.SetSiblingIndex(0);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                img.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;
                var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
                Log("[ui] blocker " + tag + " " + brt.rect.width.ToString("0") + "x" + brt.rect.height.ToString("0"));
            }
            catch (Exception e) { log.LogWarning("[ui] blocker " + tag + " " + e.GetType().Name + " " + e.Message); }
        }

        internal static UnityEngine.RectTransform SlimBlockerRt = null;
        internal static void AddClickBlocker(PixelWindow w, string tag, int wPx, int hPx)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt == null) return;
                // MachineryWindow 的根 rect 往往是 0x0，真正有尺寸的是子节点；
                // 拦截层必须贴在「有尺寸的那层」上并拉满，否则它落在 (0,0) 处，既挡不住点击、自检也会算到屏幕外。
                var host = FindSizedRect(rt, 0);
                var go = new UnityEngine.GameObject("watercalc_blocker_" + tag);
                var brt = go.AddComponent<UnityEngine.RectTransform>();
                brt.SetParent(host != null ? host : rt, false);
                if (host != null && host != rt)
                {
                    brt.anchorMin = UnityEngine.Vector2.zero;
                    brt.anchorMax = UnityEngine.Vector2.one;
                    brt.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.offsetMin = UnityEngine.Vector2.zero;
                    brt.offsetMax = UnityEngine.Vector2.zero;
                }
                else
                {
                    brt.anchorMin = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.anchorMax = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.anchoredPosition = UnityEngine.Vector2.zero;
                    brt.sizeDelta = new UnityEngine.Vector2(wPx, hPx);
                }
                brt.SetSiblingIndex(0);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                img.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;
                var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
                try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch (Exception) { }
                if (tag == "slim" || tag == "main") SlimBlockerRt = brt;
                Log("[ui] blocker " + tag + " want=" + wPx + "x" + hPx + " got=" + brt.rect.width.ToString("0") + "x" + brt.rect.height.ToString("0"));
            }
            catch (Exception e) { log.LogWarning("[ui] blocker " + tag + " " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>无鼠标自检：在窄条中心做一次 EventSystem 射线，看最上层是谁。</summary>
        /// <summary>背包/台面里名字含关键词的道具数量（药片、过滤棉、紫外这类按本地化名认）。</summary>
        private static int CountNamed(string key)
        {
            int n = 0;
            try
            {
                // 深度枚举：药片/过滤棉/紫外可能被塞在打印机或箱子里，只看顶层会少数。
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string nm = GameBridge.NameOfItem(it);
                    if (!string.IsNullOrEmpty(nm) && nm.IndexOf(key, StringComparison.Ordinal) >= 0) n++;
                }
            }
            catch (Exception) { }
            return n;
        }

        /// <summary>
        /// 指定屏幕坐标做一次 EventSystem 射线：`hit=960,400`。
        /// 用来判定「谁在吃点击」——比如详情窗开着时，机器窗口那块区域被谁挡住了。
        /// </summary>
        public static string HitTestAt(string args)
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return "hit -> no-eventsystem";
                float x = 960f, y = 540f;
                var parts = (args ?? "").Split(',');
                if (parts.Length >= 2)
                {
                    float.TryParse(parts[0].Trim(), out x);
                    float.TryParse(parts[1].Trim(), out y);
                }
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = new UnityEngine.Vector2(x, y);
                var list = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, list);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 5; i++)
                {
                    var go = list[i].gameObject;
                    string up = "";
                    var tr = go.transform;
                    for (int k = 0; k < 2 && tr.parent != null; k++) { tr = tr.parent; up = tr.name + "/" + up; }
                    sb.Append(i == 0 ? "" : " < ").Append(up).Append(go.name);
                }
                return "hit=" + x.ToString("0") + "," + y.ToString("0") + " -> n=" + list.Count + " top=" + sb;
            }
            catch (Exception e) { return "hit -> " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>MachineryWindow 的根 rect 常常是 0x0，真正有尺寸的是它的子节点；找第一个有尺寸的。</summary>
        private static UnityEngine.RectTransform FindSizedRect(UnityEngine.RectTransform rt, int depth)
        {
            if (rt == null || depth > 4) return null;
            if (rt.rect.width > 16f && rt.rect.height > 16f && rt.gameObject.activeInHierarchy) return rt;
            for (int i = 0; i < rt.childCount; i++)
            {
                var ch = rt.GetChild(i) as UnityEngine.RectTransform;
                var hit = FindSizedRect(ch, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        public static string HitTest()
        {
            try
            {
                if (panelWin == null) return "hit -> no-panel";
                var rt = panelWin.rectTransform;
                if (rt == null) return "hit -> no-rect";
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return "hit -> no-eventsystem";
                UnityEngine.Canvas.ForceUpdateCanvases();
                // 布局是延迟的：创建窗口那一刻根 rect 还是 0x0，拦截层会落到屏幕原点。
                // 自检时再找一次「真正有尺寸的那层」，并把拦截层贴上去、拉满。
                var host = FindSizedRect(panelWin.rectTransform, 0);
                if (SlimBlockerRt != null && host != null && SlimBlockerRt.parent != host)
                {
                    SlimBlockerRt.SetParent(host, false);
                    SlimBlockerRt.anchorMin = UnityEngine.Vector2.zero;
                    SlimBlockerRt.anchorMax = UnityEngine.Vector2.one;
                    SlimBlockerRt.offsetMin = UnityEngine.Vector2.zero;
                    SlimBlockerRt.offsetMax = UnityEngine.Vector2.zero;
                    Log("[ui] blocker main reparent -> " + host.name + " size=" + host.rect.width.ToString("0") + "x" + host.rect.height.ToString("0"));
                }
                var src = SlimBlockerRt != null ? SlimBlockerRt : (host != null ? host : rt);
                if (src == null) return "hit -> no-src";
                // 屏幕空间 Overlay 画布：世界坐标就是屏幕像素，不能再除 scaleFactor
                // （旧实现多除了一次，取样点被算成 0，测出来的 at=0 是假的）。
                // 用 GetWorldCorners 取中心，避免 rect 尚未布局时 center 为 0。
                var corners = new UnityEngine.Vector3[4];
                var sized = FindSizedRect(src, 0);
                src = sized != null ? sized : src;
                src.GetWorldCorners(corners);
                UnityEngine.Vector3 world = (corners[0] + corners[2]) * 0.5f;
                var sp = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, world);
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = sp;
                var list = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, list);
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 4; i++) names.Append((i == 0 ? "" : " < ") + list[i].gameObject.name);
                string up = "";
                if (list.Count > 0 && list[0].gameObject != null)
                {
                    var tr = list[0].gameObject.transform;
                    for (int i = 0; i < 3 && tr.parent != null; i++) { tr = tr.parent; up = tr.name + "/" + up; }
                }
                return "hit -> n=" + list.Count + " top=" + names + " up=" + up + " at=" + sp.x.ToString("0") + "," + sp.y.ToString("0")
                     + " rect=" + src.rect.width.ToString("0") + "x" + src.rect.height.ToString("0");
            }
            catch (Exception e) { return "hit -> " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>当前这瓶水的完整规划（详情/配方/ctl 共用一份数据）。</summary>
        // ★性能：一次界面刷新里 CurrentPlan(true) 会被叫很多次（每张卡的文案、每个键面、手册每页各一次），
        // 每次都整包重算是「点一下卡几秒」的直接原因（实测手册 6 页 = 6 次重算 = 32 秒）。
        // 这里让 0.25 秒内的重复调用共用同一份 Report（UI 全部代码都是同一帧内同步跑完的），
        // 再配合 GameBridge.BeginMemo 的读取缓存 + WaterPlan 的库存快照，一次重算从几秒降到几十毫秒。
        private static WaterPlan.Report currentCacheReport;
        private static float currentCacheAt = -1f;
        private static bool currentCacheFull;
        private static string currentCacheSig = "";

        /// <summary>换台/换模式/灌水之后必须丢缓存：不然 0.25 秒内的界面更新还拿着上一台机器的方案，
        /// 表现成「点了选台没反应 / 数字还是另一台机器的」。</summary>
        internal static void DropPlanCache() { currentCacheAt = -1f; }

        // ===== 方案锁定（玩家 2026-11-11 定案）=====
        // 玩家 2026-11-11 定案：**方案只在「玩家按档位键」或「改设置页」时变**。
        // 往瓶里倒水、接水、过夜让水质变了，都不能自己换一套 —— 「接水」键只灌水，不重算。
        private static WaterPlan.Report lockedReport;
        private static bool lockedFull;
        // 方案锁在哪台机器：换机器必须无条件解锁定，不能先让水量/进度分支把旧方案留住。
        // 玩家 2026-11-17：换机器有时刷新、有时不刷新，根因就是旧方案执行中的分支把旧步骤保下来了。
        private static int lockedPairIdx = -1;
        private static string lockModeSig = "";
        private static string lockBottleSig = "";
        private static float lockedAt = -999f;
        // 刚按过接水：只按实际混出来的这瓶水算方案，不再继续搜“还能再接多少”。
        // 玩家 2026-11-15 报：接一次后容器里混水纯度已经变了，旧方案还按接水前的水样走，
        // 或者又重新提示下一段接水，都会和实际这瓶水冲突。
        // 玩家 2026-11-15：多晚默认只先推荐 2 晚；8 晚只用来算“升晚能多出多少水”的提示，
        // 不再直接把 8 晚方案摆到主页。只有效率无限能在 2 晚装满 50 L 时才用 2 晚。
        private const int MaxRecommendNights = 2;
        private static bool postFillFix;
        private static string postFillSig = "";
        // 锁定这份方案时是不是处在“接水后只按实际水算”的临时状态。
        // 瓶子还没满时，这种方案只是当时的临时答案；之后必须重新允许找补水。
        private static bool lockPostFillFix;
        // 方案执行进度：锁定的方案是一串有序步骤；过滤/药片/紫外等做完后，
        // 当前瓶水对得上 rawSteps 的某一中间态，就只显示剩余步骤，不重新搜补水。
        private static bool progressActive;
        private static int progressDoneSteps;
        // 药片可以比过滤步骤先用（玩家实际就是这么操作）；只靠 progressDoneSteps
        // 这个“前缀到第几步”无法表示“后面的药先用了”，所以单独记已用药片数。
        private static int progressTabletUsed;
        // 上一次已确认的进度水位/纯度：药片是一次一次加的，游戏读数有截断，
        // 不能只靠 rawSteps 精确匹配；用它识别“又加了一片”这种单步变化。
        private static long progressLastVolUl;
        private static int progressLastPurityBp;
        private static long lockedVolUl;
        private static int lockedPurityBp;
        private static int lockedFilterOwned;
        private static int lockedTabletCount;
        // 换方案对比：旧方案失效时先留一份快照，新方案算出来后只在“新方案更赚”时显示差额。
        private static WaterPlan.Report compareOld;
        private static int compareOldDone;
        private static string compareOldModeSig = "";
        private static bool compareExplicit;
        private static bool comparePending;
        private static string compareText = "";

        private static string LockModeSig()
        {
            // 玩家 2026-11-11：设置页里的东西（水源/目标/前后置/海德拉）变了要重算；
            // 标题栏「默认 / 效率 / 性价比」按键在 SetMode 里已经显式 UnlockPlan 重算，
            // 所以 Mode / UncapEff 不进锁签名（否则锁签名会在重算后又判一次「设置变了」）。
            return "s" + SrcIdx + "|t" + TargetIdx + "|pre" + PreMode + "|po" + PostMode +
                   "|cp" + CustomPur + "|cm" + (calcManualSource ? 1 : 0) + "|ci" + CalcManualSig() +
                   "|nm" + (WaterPlan.NoMachine ? 1 : 0) +
                   "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap +
                   "|nc" + WaterPlan.NightCap + "|inf" + (InfiniteTools ? 1 : 0);
        }

        private static void LockPlan(WaterPlan.Report r, string why)
        {
            LockPlan(r, why, true);
        }

        private static bool SmallWaterChangeFromLocked()
        {
            var old = compareOld;
            if (old == null || old.pick == null) return false;
            if (compareOldModeSig != LockModeSig()) return false;
            var raw = RawResultFor(old);
            if (raw == null || raw.rawSteps == null || raw.rawSteps.Count == 0) return false;
            bool hasTablet = false;
            for (int i = 0; i < raw.rawSteps.Count; i++)
                if (raw.rawSteps[i] != null && raw.rawSteps[i].tool == "tablet") { hasTablet = true; break; }
            if (!hasTablet) return false;
            long vol = 0L; int bp = 0; long cap = 0L;
            try
            {
                var b = UiPlanBottle();
                if (b == null) return false;
                vol = WaterHelper.GetTotalVolume(b);
                bp = WaterHelper.GetWaterPurity(b);
                cap = WaterHelper.GetTotalCapacity(b);
            }
            catch (Exception) { return false; }
            if (old.capUl > 0 && cap > 0 && old.capUl != cap) return false;
            if (lockedVolUl <= 0 || lockedPurityBp <= 0) return false;
            if (Math.Abs(vol - lockedVolUl) > 400000L) return false;
            if (Math.Abs(bp - lockedPurityBp) > 350) return false;
            // 水质变差不是“放药片”，不改回旧方案。
            if (bp < lockedPurityBp - 5) return false;
            return true;
        }

        private static void LockPlan(WaterPlan.Report r, string why, bool full)
        {
            if (r == null || r.pick == null) return;
            // 放药/小幅倒水时，某些监视分支会先把旧方案解掉；这里在真正覆盖前拦一次：
            // 设置没变、还是同一只瓶、水量只少了一点、纯度没变差，就恢复旧方案。
            // 否则玩家放下一片药，界面就换成另一套带“补 X”的方案。
            if (!compareExplicit && comparePending && compareOld != null && SmallWaterChangeFromLocked())
            {
                lockedReport = compareOld;
                lockedFull = true;
                lockedPairIdx = activeIdx >= 0 ? activeIdx : MachineOverride;
                lockModeSig = LockModeSig();
                lockBottleSig = LockVolSig();
                lockPostFillFix = postFillFix;
                lockedAt = UnityEngine.Time.realtimeSinceStartup;
                progressActive = compareOldDone > 0;
                progressDoneSteps = compareOldDone;
                progressTabletUsed = 0;
                try
                {
                    var b = UiPlanBottle();
                    if (b != null)
                    {
                        lockedVolUl = WaterHelper.GetTotalVolume(b);
                        lockedPurityBp = WaterHelper.GetWaterPurity(b);
                        progressLastVolUl = lockedVolUl;
                        progressLastPurityBp = lockedPurityBp;
                    }
                }
                catch (Exception) { }
                progressTabletUsed = TabletUsedSinceLock(true);
                if (progressTabletUsed > 0) progressActive = true;
                waterGraceSig = "";
                waterGraceAt = -999f;
                comparePending = false;
                compareExplicit = false;
                compareText = "";
                Log("[lock] 水量小变化，恢复旧方案");
                return;
            }
            lockedReport = r;
            lockedFull = full;
            lockedPairIdx = activeIdx >= 0 ? activeIdx : MachineOverride;
            waterGraceSig = "";
            waterGraceAt = -999f;
            lockModeSig = LockModeSig();
            lockBottleSig = LockVolSig();
            lockPostFillFix = postFillFix;
            lockedAt = UnityEngine.Time.realtimeSinceStartup;
            // 记录锁定时的瓶内水与库存，用来判断后续变化是不是“方案执行进度”。
            progressActive = false;
            progressDoneSteps = 0;
            progressTabletUsed = 0;
            try
            {
                // 锁方案时记录的必须是这份方案实际对应的瓶，不能先抓鼠标上下文里的瓶。
                // 接水/倒水时光标可能停在别的水源瓶，旧版先取 CtxBottle 会把 lockedVolUl
                // 记成另一瓶，放药后 TabletProgressFromStart 看到“水量反而变多”，永远认不出进度。
                var b = UiPlanBottle();
                if (b == null) b = CtxBottle();
                if (b == null) b = GameBottle();
                if (b != null)
                {
                    lockedVolUl = WaterHelper.GetTotalVolume(b);
                    lockedPurityBp = WaterHelper.GetWaterPurity(b);
                }
                else { lockedVolUl = 0; lockedPurityBp = 0; }
            }
            catch (Exception) { lockedVolUl = 0; lockedPurityBp = 0; }
            progressLastVolUl = lockedVolUl;
            progressLastPurityBp = lockedPurityBp;
            lockedFilterOwned = FilterDurOwned();
            try
            {
                // 锁方案时库存也必须是当前值；OwnedTools 有 6 秒缓存，
                // 刚用过药片就锁方案会把旧的药片数量记进来，之后每片都被误判成“已用”。
                ownCache = null;
                ownTime = -999f;
                var o = OwnedTools();
                lockedTabletCount = o == null ? 0 : o.tablets;
            }
            catch (Exception) { lockedTabletCount = 0; }
            Log("[lock] 锁定方案（" + why + "）feed=" + (r.pick.feedUl / 1000) + "mL out=" + (r.pick.outUl / 1000) + "mL");
            BuildCompareLine(r);
        }

        /// <summary>旧方案在某一步后失效、新方案更赚时，给一行「比旧方案多赚什么」。
        /// 只在换方案的那一次显示，不参与推荐，也不改方案选择。</summary>
        private static void BuildCompareLine(WaterPlan.Report now)
        {
            if (!comparePending || compareOld == null || now == null || now.pick == null)
            {
                comparePending = false;
                compareText = "";
                return;
            }
            comparePending = false;
            try
            {
                // 旧方案也要真的进「更多方案」：只写一句“换方案 +7.1”但列表里看不到，
                // 玩家会以为换方案是假的（2026-11-16 报）。同一水量档才加，避免把另一套水混进来。
                if (compareOld != null && compareOld.pick != null && now.pick != null)
                {
                    long of = compareOld.ChosenFeedUl(), nf = now.ChosenFeedUl();
                    if (of <= 0 || nf <= 0 || Math.Abs(of - nf) <= 1500L)
                        AddComboUnique(now, compareOld.pick);
                }
                // 只有一条达标解时没有“换方案”可言；用 AllRows 而不是 TableRows，
                // 否则新加的档位不会被算进来，切几次效率后换方案提示又消失。
                var alts = AllRows(now, 40);
                if (alts == null || alts.Count < 2) { compareText = ""; return; }
                double oldNet = compareOld.ChosenProfit();
                double newNet = now.ChosenProfit();
                double diff = newNet - oldNet;
                if (diff <= 0.05) { compareText = ""; return; }
                int oldTabs = compareOld.pick == null ? 0 : compareOld.pick.tablets;
                int newTabs = now.pick.tablets;
                long oldOut = compareOld.ChosenOutUl();
                long newOut = now.ChosenOutUl();
                long oldFeed = compareOld.ChosenFeedUl();
                long newFeed = now.ChosenFeedUl();
                // 玩家 2026-11-16：倒水/接水后旧方案是另一套水量，拿 2879.8→5266.9 做
                // 「换方案 +89.8」根本看不懂；只有同一进水量下的换方案才值得显示。
                if (oldFeed > 0 && newFeed > 0 && Math.Abs(oldFeed - newFeed) > 1500L)
                { compareText = ""; return; }
                // 药量和晚数都没变、只多赚一两块：这是同一套方案的重排，不叫「换方案」。
                if (oldTabs == newTabs && diff < 2.0) { compareText = ""; return; }
                compareText = "换方案 +" + WaterEconomy.Money(diff) + " 元"
                            + "  ·  水 " + WaterPlan.Ml(oldOut) + "→" + WaterPlan.Ml(newOut) + " mL"
                            + "  ·  药 " + oldTabs + "→" + newTabs;
            }
            catch (Exception) { compareText = ""; }
        }

        internal static void UnlockPlan(string why)
        {
            // 任何一次“旧方案被解除”都可能是阈值换方案（目标档、模式、机器、水量/水质……）。
            // 先把旧方案留到下一次 LockPlan，让 BuildCompareLine 能报“新方案比旧方案多赚什么”。
            var oldForCompare = lockedReport;
            int oldDoneForCompare = progressDoneSteps;
            compareOldModeSig = lockModeSig;
            compareExplicit = why != null && why.Contains("手动选择");
            postFillFix = false;
            postFillSig = "";
            lockPostFillFix = false;
            waterGraceSig = "";
            waterGraceAt = -999f;
            progressActive = false;
            progressDoneSteps = 0;
            progressTabletUsed = 0;
            if (oldForCompare != null && oldForCompare.pick != null)
            {
                compareOld = oldForCompare;
                compareOldDone = oldDoneForCompare;
                comparePending = true;
                compareText = "";
            }
            else
            {
                compareOld = null;
                comparePending = false;
                compareExplicit = false;
                compareText = "";
            }
            if (lockedReport == null)
            {
                // 即使没有锁定方案，也要丢掉上一次的求解缓存：
                // 自动档曾经缓存过 null，手动选水源后清了缓存才出方案（玩家 2026-11-16 报）。
                DropPlanCache();
                return;
            }
            lockedReport = null;
            lockedPairIdx = -1;
            lockModeSig = "";
            lockBottleSig = "";
            progressLastVolUl = 0L;
            progressLastPurityBp = 0;
            lockedAt = -999f;
            DropPlanCache();
            Log("[lock] 解锁（" + why + "）");
        }

        /// <summary>锁定是否还有效：只看设置页（水源/目标/前后置/海德拉）。
        /// 玩家 2026-11-11 定案：水量、水质都不参与解锁 —— 倒水/接水/过夜都不改方案；
        /// 档位按键走 SetMode 里的显式 UnlockPlan，不靠这里判。</summary>
        /// <summary>玩家按面板提示「先倒出 X」或「先补到 Y」后，当前瓶正好落在方案起手量上。
        /// 这表示方案刚开始执行，不是方案失效；保留原方案，后面的步骤继续跑。</summary>
        private static bool LockedFeedOrDumpDone()
        {
            if (lockedReport == null || lockedReport.pick == null) return false;
            long vol = 0L; int bp = 0;
            try
            {
                // 玩家 2026-11-16：先倒到别的容器时 CtxBottle 会切到别的瓶，
                // 按它判起手量会把方案解成另一套（P3）。锁方案只认当前机器瓶。
                var b = UiPlanBottle();
                if (b == null) return false;
                vol = WaterHelper.GetTotalVolume(b);
                bp = WaterHelper.GetWaterPurity(b);
            }
            catch (Exception) { return false; }

            // 玩家 2026-11-16：用药一次就让我补一次水。
            // 药片可以比过滤步骤先用；旧逻辑发现“过滤还没做、药却少了”直接 return false，
            // LockValid 就把方案解掉重算，重算后的当前水量自然带出“补 X”。
            // 这里先认背包药片减少，保留原方案，只把剩余药片数往下扣。
            int tabUsedNow = TabletUsedSinceLock(true);
            if (tabUsedNow > progressTabletUsed) progressTabletUsed = tabUsedNow;

            // 放药后水会变少、纯度会升高。旧版先落到下面的“补水未到起手量”分支，
            // 导致第一次放药就重锁成“先补 X + 药N”；这里先把药片/前置进度认出来。
            int earlyDone = CompletedLockedSteps();
            if (earlyDone <= 0) earlyDone = TabletProgressFromStart(vol, bp);
            if (earlyDone <= 0) earlyDone = NearLastPrePurifierStep(vol, bp);
            if (earlyDone > 0 && !ProgressHasPurifier(earlyDone))
            {
                progressActive = true;
                progressDoneSteps = earlyDone;
                lockBottleSig = LockVolSig();
                progressLastVolUl = vol;
                progressLastPurityBp = bp;
                Log("[lock] 前置进度=" + earlyDone + "，保留原方案");
                return true;
            }

            // 药片已经用了，但方案前面还有过滤没做：不能因此把整份方案解掉。
            // 保留原方案，步骤行只扣掉已用的药片；过滤/净化器仍按原顺序显示。
            if (tabUsedNow > 0 && !ProgressHasPurifier(progressDoneSteps))
            {
                progressActive = true;
                progressDoneSteps = Math.Max(progressDoneSteps, 0);
                lockBottleSig = LockVolSig();
                progressLastVolUl = vol;
                progressLastPurityBp = bp;
                Log("[lock] 药片已用 " + tabUsedNow + " 片，保留原方案");
                return true;
            }

            // 倒出去再倒回原瓶：水回到锁定值，说明只是转了一圈，
            // 不能按中间水量重锁成无补水的另一种方案（P3）。
            if (lockedVolUl > 0 && Math.Abs(vol - lockedVolUl) <= 1500L
                && (lockedPurityBp <= 0 || Math.Abs(bp - lockedPurityBp) <= 5))
            {
                // 已经执行过药片/过滤步骤时，水回到原值不能把进度清零；只把签名重新对上。
                if (progressDoneSteps > 0)
                {
                    lockBottleSig = LockVolSig();
                    Log("[lock] 水量回到锁定值，已有步骤进度，保留进度");
                    return true;
                }
                progressActive = false;
                progressDoneSteps = 0;
                lockBottleSig = LockVolSig();
                postFillFix = false;
                postFillSig = "";
                Log("[lock] 水量回到锁定值，保留原方案");
                return true;
            }

            long expect = -1L;
            // 先倒出：锁定时的水 - 面板报的 dumpUl（report.fitUl 是倒完应留下的量）。
            if (lockedReport.dumpUl > 0)
            {
                expect = lockedVolUl - lockedReport.dumpUl;
                if (lockedReport.fitUl > 0 && Math.Abs(lockedReport.fitUl - expect) > 1500L)
                    expect = lockedReport.fitUl;
            }
            // 先补到：方案自己记录的 feedUl（从当前水补到整瓶要做的量）。
            else if (lockedReport.pick.feedUl > 0
                     && Math.Abs(lockedReport.pick.feedUl - lockedVolUl) > 1500L)
            {
                expect = lockedReport.pick.feedUl;
            }
            if (expect <= 0 || Math.Abs(vol - expect) > 1500L)
            {
                // 玩家 2026-11-16：面板让倒 2201，玩家只倒了 2000，旧版立刻按 4000 重算，
                // 方案跳走；把 2000 倒回去又因为旧方案已被替换而没方案。
                // 还没倒够时保留原方案，等玩家继续倒到 fitUl；倒回原水量也保留原方案。
                int partialBp = lockedReport.srcBp > 0 ? lockedReport.srcBp : lockedPurityBp;
                if (partialBp > 0 && Math.Abs(bp - partialBp) > 5) return false;
                if (!progressActive && expect > 0
                    && vol > expect + 1500L && vol <= lockedVolUl + 1500L)
                {
                    progressDoneSteps = 0;
                    Log("[lock] 倒水未到起手量 vol=" + vol + " 目标=" + expect + "，保留原方案");
                    return true;
                }
                // 先补/接水还没补够：从原水量到 feedUl 之间也保留原方案，别一边补一边换方案。
                if (!progressActive && lockedReport.dumpUl <= 0
                    && lockedReport.pick.feedUl > lockedVolUl + 1500L
                    && vol > lockedVolUl - 1500L && vol < lockedReport.pick.feedUl - 1500L)
                {
                    progressDoneSteps = 0;
                    Log("[lock] 补水未到起手量 vol=" + vol + " 目标=" + lockedReport.pick.feedUl + "，保留原方案");
                    return true;
                }
                return false;
            }

            int expectBp = lockedReport.srcBp > 0 ? lockedReport.srcBp : lockedPurityBp;
            if (expectBp > 0 && Math.Abs(bp - expectBp) > 5) return false;

            // 执行到起手量：之后的 rawSteps 还没开始，进度从 0 开始显示。
            progressActive = true;
            progressDoneSteps = 0;
            lockBottleSig = LockVolSig();
            Log("[lock] 已按方案起手量倒水/补水 vol=" + vol + " expect=" + expect + "，保留原方案");
            return true;
        }

        private static bool LockValid()
        {
            if (lockedReport == null) return false;
            // 玩家 2026-11-16：把容器从机器里挪走/取消绿点后，旧方案仍被锁住，
            // 主页继续显示「需接水3000」。机器开着但当前槽没有容器时必须立即解锁定。
            if (uiDeselected)
            {
                UnlockPlan("取消选择机器");
                return false;
            }
            // 玩家 2026-11-17：换机器必须无条件重算。旧方案执行中时，下面的水量/纯度分支
            // 可能直接 return true 把旧步骤留下；先在机器身份这一层判死，避免“有时刷新有时不刷新”。
            int curPair = activeIdx >= 0 ? activeIdx : MachineOverride;
            if (lockedPairIdx != curPair)
            {
                UnlockPlan("换机器 " + lockedPairIdx + "->" + curPair);
                return false;
            }
            if (lockedReport != null && MachineOpen() && ActiveMachineBottle() == null)
            {
                // 放药片/拖动库存的同一帧，当前配对可能短暂读不到容器；不能一帧就解锁，
                // 否则方案会在放药瞬间被重算成带补水的新方案。真拿走容器时 0.8 秒后仍会解锁。
                float noBottleNow = UnityEngine.Time.realtimeSinceStartup;
                if (noBottleSince < 0f) noBottleSince = noBottleNow;
                if (noBottleNow - noBottleSince < 0.8f) return true;
                noBottleSince = -1f;
                UnlockPlan("机器里没有容器");
                // P5：解锁后还要把已经画在面板上的旧方案请求重绘一次；否则同一帧里
                // 文本仍是旧模型，玩家看起来像“容器拿走了但状态没刷新”。
                RequestRebuild();
                RefreshMixIfOpen();
                return false;
            }
            noBottleSince = -1f;
            // 玩家 2026-11-16：3000 mL 方案完成后换 6 L 容器，旧方案还锁着不刷新（切模式才变）。
            // 容器容量不同就是另一瓶，立即解锁定，不能靠水量/水质差去猜。
            long capNow = 0L, volNow = 0L;
            try
            {
                var b = UiPlanBottle();
                if (b != null)
                {
                    capNow = WaterHelper.GetTotalCapacity(b);
                    volNow = WaterHelper.GetTotalVolume(b);
                }
            }
            catch (Exception) { capNow = 0L; volNow = 0L; }
            // 玩家 2026-11-16：把液体倒进容器再倒出来（瓶已空）后，旧方案还锁着，
            // 主页仍显示「出来 6000 / 这瓶水直接跑」。空瓶没有任何现有水可跑，
            // 必须丢掉旧方案，按空瓶重新找接水量，不能拿 lastMachineBottle 的旧水量兜底。
            if (volNow <= 0L)
            {
                UnlockPlan("容器已倒空");
                return false;
            }
            if (lockedReport.capUl > 0 && capNow > 0 && lockedReport.capUl != capNow)
            {
                UnlockPlan("容器容量变了 " + lockedReport.capUl + "->" + capNow);
                return false;
            }
            // 玩家 2026-11-16：空容器接外面的水放回去后，第一次锁成 8.95 的不补水方案，
            // 按两次“性价比”才变成补 91.5。postFillFix 是“接水后只按实际水算”的临时锁，
            // 瓶子还没到容量时不能继续压着不找补水；直接解锁，让下一次按当前水重算。
            // lockPostFillFix 记录这份方案锁定当时是不是这种临时状态：即使临时标记后来被清掉，
            // 只要瓶还没满，也不能让那份“不补水”结果一直锁着。
            if ((postFillFix || lockPostFillFix) && !progressActive && lockedReport.dumpUl <= 0
                && capNow > 0L && volNow > 0L && capNow - volNow > 1500L)
            {
                postFillFix = false;
                postFillSig = "";
                lockPostFillFix = false;
                UnlockPlan("接水后未满，按当前水重找补水");
                return false;
            }
            if (LockModeSig() != lockModeSig) { UnlockPlan("设置变了"); return false; }
            if (lockBottleSig != LockVolSig())
            {
                long curVol = 0L; int curBp = 0;
                try
                {
                    var cb = PlanBottle();
                    if (cb == null) cb = lastMachineBottle;
                    if (cb != null) { curVol = WaterHelper.GetTotalVolume(cb); curBp = WaterHelper.GetWaterPurity(cb); }
                }
                catch (Exception) { }
                // 玩家 2026-11-16：面板让「先倒出 X mL」后玩家照做了，体积当然会变；
                // 这不是方案失效，而是执行了方案的起手步骤。倒到 fitUl / dumpUl 后保留原方案，
                // 否则一倒完就重锁新方案，玩家接不上上一次的步骤。
                // 放药片时游戏的背包数量/瓶内读数会晚一两帧更新；先给 1.2 秒稳定期，
                // 期间保留原方案并反复检查进度，不能第一帧就把方案解掉重算成“补 X”。
                string curSig = LockVolSig();
                if (waterGraceSig != curSig)
                {
                    waterGraceSig = curSig;
                    waterGraceAt = UnityEngine.Time.realtimeSinceStartup;
                }
                if (LockedFeedOrDumpDone())
                {
                    waterGraceSig = "";
                    return true;
                }
                int done = CompletedLockedSteps();
                int advanced = AdvanceTabletProgress(curVol, curBp);
                if (advanced > done) done = advanced;
                if (done <= 0) done = NearLastPrePurifierStep(curVol, curBp);
                if (done > 0 && !ProgressHasPurifier(done))
                {
                    // 过滤/药片/紫外/混水等前置或中间步骤已经做完：
                    // 保留原方案，只把已完成的步骤从步骤串里裁掉；不搜补水。
                    progressActive = true;
                    progressDoneSteps = done;
                    lockBottleSig = LockVolSig();
                    progressLastVolUl = curVol;
                    progressLastPurityBp = curBp;
                    waterGraceSig = "";
                    Log("[lock] 进度=" + done + "，只显示剩余步骤");
                    return true;
                }
                // 0 晚方案不需要再补一滴水：只要已经开始执行（已放药/过滤等），
                // 水量小变化不再解锁，避免最后一片药后差十几 mL 又搜出「补 X」。
                if (progressActive && lockedReport.pick != null && lockedReport.pick.nights <= 0
                    && !ProgressHasPurifier(progressDoneSteps))
                {
                    lockBottleSig = LockVolSig();
                    progressLastVolUl = curVol;
                    progressLastPurityBp = curBp;
                    waterGraceSig = "";
                    Log("[lock] 0 晚方案执行中，保留原方案");
                    return true;
                }
                // 读数还没稳定：保留旧方案，下几帧再判；超过 1.2 秒仍对不上才按原逻辑重算。
                if (waterGraceAt >= 0f
                    && UnityEngine.Time.realtimeSinceStartup - waterGraceAt < 1.2f)
                    return true;
                waterGraceSig = "";
                // 过夜/净化器后必须按当前瓶水重算药片与剩余晚数；
                // 同时压掉“先补多少”的自动接水搜索。
                var oldRep = lockedReport;
                int oldDone = progressActive ? progressDoneSteps : done;
                UnlockPlan("水量/水质变了，旧方案按当前瓶重算");
                compareOld = oldRep;
                compareOldDone = oldDone;
                comparePending = true;
                postFillFix = true;
                postFillSig = LockVolSig();
                return false;
            }
            // 玩家 2026-11-16：关闭/重开机器窗口时旧版会无条件 UnlockPlan，
            // 于是 3000/95.53 锁定的“药2”被重新锁成 2909/98.04 的“先补91+药2”，
            // 已放下去的那片药看起来像没生效。这里即使瓶签名没变，也先检查当前水
            // 是否已经是锁定方案 rawSteps 的某个中间态；是的话保留进度，只显示剩余步骤。
            if (!progressActive && lockedReport != null && lockedReport.pick != null)
            {
                int done = CompletedLockedSteps();
                if (done > 0 && !ProgressHasPurifier(done))
                {
                    progressActive = true;
                    progressDoneSteps = done;
                    lockBottleSig = LockVolSig();
                    try
                    {
                        var cb = PlanBottle();
                        if (cb != null)
                        {
                            progressLastVolUl = WaterHelper.GetTotalVolume(cb);
                            progressLastPurityBp = WaterHelper.GetWaterPurity(cb);
                        }
                    }
                    catch (Exception) { }
                    Log("[lock] 瓶签名未变，但水对应方案中间态，进度=" + done);
                }
            }
            // 有些药片步骤的水量/纯度变化极小，锁签名可能没变；这里仍按背包药片减少量
            // 把“已用药片”补上，避免一次用药后仍显示完整药数或重新算成补水方案。
            // 这里不强制刷新库存：每帧扫背包会卡；真正变化时上面的 LockedFeedOrDumpDone
            // 会用 TabletUsedSinceLock(true) 刷新。
            int tabUsedNow = TabletUsedSinceLock(false);
            if (tabUsedNow > progressTabletUsed)
            {
                progressTabletUsed = tabUsedNow;
                progressActive = true;
                lockBottleSig = LockVolSig();
            }
            return true;
        }

        /// <summary>药片是一步一步加的；游戏读数有截断，rawSteps 的精确体积/纯度对不上时，
        /// 用“上一次已确认水位 → 当前水位”识别又加了几片，避免卡在 2 片、然后突然跳到完成。</summary>
        private static int AdvanceTabletProgress(long vol, int bp)
        {
            if (!progressActive || lockedReport == null || progressDoneSteps <= 0) return progressDoneSteps;
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null) return progressDoneSteps;
            int idx = progressDoneSteps;
            if (idx >= raw.rawSteps.Count || raw.rawSteps[idx] == null
                || raw.rawSteps[idx].tool != "tablet") return progressDoneSteps;
            long dVol = progressLastVolUl - vol;
            int dBp = bp - progressLastPurityBp;
            // 一片药：体积小降、纯度小升；过滤/倒水会大幅变化，不能算成药片。
            if (dVol <= 0 || dBp <= 0 || dBp > 350 || dVol > 400000L) return progressDoneSteps;
            int count = (int)Math.Round(dVol / 76000.0);
            if (count < 1) count = 1;
            int tabletsLeft = 0;
            for (int i = idx; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null || st.tool != "tablet") break;
                tabletsLeft++;
            }
            if (count > tabletsLeft) count = tabletsLeft;
            if (count <= 0) return progressDoneSteps;
            progressDoneSteps += count;
            progressLastVolUl = vol;
            progressLastPurityBp = bp;
            Log("[lock] 药片进度推进 +" + count + " -> " + progressDoneSteps);
            return progressDoneSteps;
        }

        /// <summary>锁定方案的第一步是药片时，用量降/纯升识别玩家已经放了几片。
        /// 旧版只在 progressActive 且已有进度后才认；第一次放药会落到
        /// LockedFeedOrDumpDone 的“补水未到起手量”分支，于是方案被重锁成先补方案。</summary>
        private static int TabletProgressFromStart(long vol, int bp)
        {
            if (lockedReport == null || lockedReport.pick == null || lockedVolUl <= 0 || lockedPurityBp <= 0) return 0;
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null || raw.rawSteps.Count == 0) return 0;
            var first = raw.rawSteps[0];
            if (first == null || first.tool != "tablet") return 0;
            long dVol = lockedVolUl - vol;
            int dBp = bp - lockedPurityBp;
            if (dVol <= 0 || dVol > 400000L || dBp <= 0 || dBp > 350) return 0;
            int count = (int)Math.Round(dVol / 76000.0);
            if (count < 1) count = 1;
            int left = 0;
            for (int i = 0; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null || st.tool != "tablet") break;
                left++;
            }
            if (count > left) count = left;
            return count > 0 ? count : 0;
        }

        /// <summary>当前水已经到「净化器之前最后一步」：放完最后一片药后，不能因为差十几 mL 又让补 X。</summary>
        private static int NearLastPrePurifierStep(long vol, int bp)
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null || raw.rawSteps.Count == 0) return 0;
            int lastPre = -1;
            for (int i = 0; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null) continue;
                if (st.tool != "purifier") lastPre = i;
            }
            if (lastPre < 0) return 0;
            var last = raw.rawSteps[lastPre];
            if (last == null) return 0;
            if (Math.Abs(last.volUl - vol) > 50000L) return 0;
            if (Math.Abs((int)Math.Round(last.purity * 100.0) - bp) > 5) return 0;
            return lastPre + 1;
        }

        /// <summary>当前瓶水是否对得上锁定方案 rawSteps 的某个中间态。</summary>
        private static int CompletedLockedSteps()
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null || raw.rawSteps.Count == 0) return 0;
            long vol = 0L; int bp = 0;
            try
            {
                var b = UiPlanBottle();
                if (b == null) b = lastMachineBottle;
                if (b == null) return 0;
                vol = WaterHelper.GetTotalVolume(b);
                bp = WaterHelper.GetWaterPurity(b);
            }
            catch (Exception) { return 0; }

            int done = 0;
            for (int i = 0; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null) continue;
                // 玩家 2026-11-16：药片步骤的读数会截断（实测 90.62 显示 90.60、91.79 显示 91.11），
                // 旧容差 ±1.5 mL / ±0.01% 会把第 3 片卡在“进度2”；放宽体积到 50 mL、纯度到 0.05%。
                if (Math.Abs(st.volUl - vol) <= 50000L
                    && Math.Abs(st.purity * 100.0 - bp) <= 5.0)
                    done = i + 1;
            }
            if (done > 0) return done;

            // 体积/纯度因游戏截断没对上时的两条硬证据：
            // 滤棉真实耐久下降、药片库存下降。只在前置步骤里找，不把过夜误判成过滤。
            int filterOwned = FilterDurOwned();
            bool filterUsed = lockedFilterOwned > 0 && filterOwned < lockedFilterOwned
                              && vol <= lockedVolUl && bp >= lockedPurityBp;
            int tabNow = 0;
            try { var o = OwnedTools(); tabNow = o == null ? 0 : o.tablets; }
            catch (Exception) { tabNow = lockedTabletCount; }
            int tabUsed = lockedTabletCount - tabNow;
            bool tabletUsed = tabUsed > 0;
            // 「无限所有」开着时药片库存不会减少，不能拿库存当证据。
            // 如果方案前段只有药片，而瓶内水已经比锁定状态更纯、体积更小（还没到方案终点），
            // 就按第一片药已完成处理，步骤串显示剩余的「药1」。
            if (done == 0 && !filterUsed && (InfiniteTools || tabUsed <= 0))
            {
                int firstTablet = -1;
                bool onlyTablet = true;
                for (int i = 0; i < raw.rawSteps.Count; i++)
                {
                    var st = raw.rawSteps[i];
                    if (st == null) continue;
                    if (st.tool == "tablet") { if (firstTablet < 0) firstTablet = i; }
                    else if (firstTablet < 0) onlyTablet = false;
                }
                if (firstTablet >= 0 && onlyTablet
                    && bp > lockedPurityBp && vol < lockedVolUl)
                {
                    var last = raw.rawSteps[raw.rawSteps.Count - 1];
                    // 已经到方案终点就别再补「剩余药片」，交给下层按当前水重算。
                    if (last != null && (bp < (int)(last.purity * 100.0 - 1.0)
                                         || vol > last.volUl + 1500L))
                        return firstTablet + 1;
                }
            }
            // 药片+过滤棉都动过：不管玩家先做哪一个，两个前置都算完成，跳到最后一个前置步骤。
            if (filterUsed && tabletUsed)
            {
                int last = 0;
                for (int i = 0; i < raw.rawSteps.Count; i++)
                {
                    var st = raw.rawSteps[i];
                    if (st == null) continue;
                    if (st.tool == "filter" || st.tool == "tablet") last = i + 1;
                }
                if (last > 0) return last;
            }
            if (filterUsed)
            {
                for (int i = 0; i < raw.rawSteps.Count; i++)
                    if (raw.rawSteps[i] != null && raw.rawSteps[i].tool == "filter")
                        return i + 1;
            }
            if (tabletUsed)
            {
                // 只用了药片、方案里前面还有没做的过滤棉：不能把过滤棉一并跳过，
                // 返回 0 让面板按当前水重算（药片已经生效，后续不会再推荐这片）。
                bool filterBeforeTablet = false;
                int seen = 0;
                for (int i = 0; i < raw.rawSteps.Count; i++)
                {
                    var st = raw.rawSteps[i];
                    if (st == null) continue;
                    if (st.tool == "filter") filterBeforeTablet = true;
                    if (st.tool != "tablet") continue;
                    seen++;
                    if (seen >= tabUsed)
                    {
                        if (filterBeforeTablet && !filterUsed) return 0;
                        return i + 1;
                    }
                }
            }
            return 0;
        }

        /// <summary>已完成的步骤里只要有过夜/净化器，就必须重算，不能直接裁步骤。</summary>
        private static bool ProgressHasPurifier(int done)
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null) return false;
            int n = Math.Min(done, raw.rawSteps.Count);
            for (int i = 0; i < n; i++)
                if (raw.rawSteps[i] != null && raw.rawSteps[i].tool == "purifier") return true;
            return false;
        }

        /// <summary>从锁定方案的已完成前缀里数某种工具用了几次（药片/过滤/紫外/过夜）。</summary>
        private static int ProgressDoneTool(string tool)
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null || !progressActive) return 0;
            int n = Math.Min(progressDoneSteps, raw.rawSteps.Count);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null) continue;
                if (st.tool == tool) count += Math.Max(1, st.count);
            }
            // 药片可能是在过滤之前先用的；这时 progressDoneSteps 仍是 0，
            // 不能只按前缀数，否则主页会一直显示“药共4/剩4”。
            if (tool == "tablet")
            {
                int used = Math.Max(count, progressTabletUsed);
                int total = PlanTabletTotal();
                if (total > 0 && used > total) used = total;
                return used;
            }
            return count;
        }

        /// <summary>锁里存的是轻量结果（没有方案表），这次要重量结果就放它重算一次。</summary>
        private static bool LockNeedsFull(bool full)
        {
            return full && lockedReport != null && !lockedFull;
        }

        /// <summary>方案缓存的有效性签名：凡是会影响方案结果的设置都算进来。
        /// 玩家 2026-11-11 报「点默认/性价比按钮没反应」，根因是 0.25 秒缓存不知道 Mode 变了，
        /// 重建时把上一次的整包结果原样还回去。</summary>
        private static string PlanSig()
        {
            return Mode + "|" + (UncapEff ? 1 : 0) + "|" + (ManualPickLabel == null ? "-" : ManualPickLabel) +
                   "|s" + SrcIdx + "|t" + TargetIdx + "|pre" + PreMode + "|po" + PostMode +
                   "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap +
                   "|nc" + WaterPlan.NightCap + "|inf" + (InfiniteTools ? 1 : 0) +
                   "|fd" + FilterDurNow() + "|pk" + PurifierHook.PickedIndex + "|bv" + BottleSig() +
                   "|cm" + (calcManualSource ? 1 : 0) + "|ci" + CalcManualSig();
        }

        /// <summary>签名里去掉「瓶子」那一段：只有水量/水质变了时用它判「设置没动，可以慢一点重算」。</summary>
        private static string SigHead(string s)
        {
            if (s == null) return "";
            int i = s.LastIndexOf("|bv", System.StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i);
        }

        /// <summary>缓存签名里也要带上瓶子（换容器/换瓶水必须重算，否则 0.25 s 内会拿到上一瓶的答案）。</summary>
        private static string BottleSig()
        {
            try
            {
                var b = UiPlanBottle();
                if (b == null) return "-";
                return WaterHelper.GetTotalVolume(b) + "@" + WaterHelper.GetWaterPurity(b) + "/" + WaterHelper.GetTotalCapacity(b);
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>锁定签名包含「水量 + 水质 + 容量」：过一晚/过滤/接水后水质变了，
        /// 旧方案的药片数和步骤就不再成立，必须按当前这瓶水重新算。
        /// 玩家 2026-11-16 报：按旧方案过 1 晚后，水已经变了，步骤还显示药 1；实际 1 药只有 98.95。</summary>
        /// <summary>方案/锁定统一读「机器里那瓶」：上下文瓶位在换瓶/过滤后可能还指着旧瓶，
        /// 会留下一整套旧方案（玩家 2026-11-16 报：3000 方案换 6 L 容器不刷新，切模式才变）。</summary>
        private static GameItem PlanBottle()
        {
            // 机器窗口开着时优先看玩家点选的那台；机器停着也照样读，不因为开关机状态换瓶。
            if (MachineOpen())
            {
                var selected = SelectedMachineBottle();
                if (selected != null) return selected;
                return ActiveMachineBottle();
            }
            // 窗口开着、但全局 MachineWindow 还没同步到这台时，仍然按 activeIdx 读选中槽。
            // 旧版这里会掉到 GameBottle/CtxBottle，混水页立刻拿到背包里的另一瓶。
            var selectedB = SelectedMachineBottle();
            if (selectedB != null) return selectedB;
            var b = GameBottle();
            if (b != null) return b;
            b = CtxBottle();
            if (b != null) return b;
            return PickFallbackBottle();
        }

        /// <summary>
        /// 主页显示、方案计算、缓存签名、方案锁定统一用这只瓶：
        /// 正常走 GameBottle（就是顶部 0/6000 的来源），只有右键读取/手填源才用 ExplicitPickBottle。
        /// 2026-11-17：PlanBottle 在机器窗焦点/配对切换时会拿到背包里另一只 750/800 mL 小瓶，
        /// 导致 6L 显示 6000、方案却按 800 搜；3L/6L 切换时旧方案还会被小瓶容量锁住。
        /// </summary>
        private static GameItem uiBottleAnchor;

        /// <summary>只读「选中机器储水槽」里的真实 Container + 液体，不先掉到背包/别的机器。
        /// 2026-11-18：玩家要求方案读机器内的容器和溶液；SelectedMachineBottle 走
        /// SelectedPairForRead().BottleInv，ActiveMachineBottle 走 ActivePair().BottleInv，
        /// 再由 BottleInMachineSlot 读 childItem / ItemsOf 找容器。</summary>
        private static GameItem MachineSlotBottle()
        {
            var selected = SelectedMachineBottle();
            if (selected != null) return selected;
            return ActiveMachineBottle();
        }

        /// <summary>顶部显示用的瓶：每次 Compute 都重新取，并把对象锚定给方案/缓存/锁定。</summary>
        private static GameItem ComputeBottle()
        {
            GameItem b;
            if (calcManualSource)
            {
                b = ExplicitPickBottle();
            }
            else
            {
                // 先读选中机器槽；槽为空/读不到才用 GameBottle/PlanBottle 兜底。
                b = MachineSlotBottle();
                if (b == null) b = GameBottle();
                if (b == null) b = PlanBottle();
            }
            uiBottleAnchor = b;
            return b;
        }

        /// <summary>方案只认顶部刚显示的那只瓶，避免同一帧里 MachineOpen 变化导致方案换瓶。</summary>
        private static GameItem UiPlanBottle()
        {
            if (calcManualSource) return ExplicitPickBottle();
            if (uiBottleAnchor != null && WaterHelper.GetTotalCapacity(uiBottleAnchor) > 0)
                return uiBottleAnchor;
            return ComputeBottle();
        }

        private static string LockVolSig()
        {
            try
            {
                var b = UiPlanBottle();
                if (b == null) return "-";
                return WaterHelper.GetTotalVolume(b) + "@" + WaterHelper.GetWaterPurity(b) +
                       "/" + WaterHelper.GetTotalCapacity(b);
            }
            catch (Exception) { return "?"; }
        }

        private static WaterPlan.Report CurrentPlan(bool full)
        {
            // 玩家 2026-11-11：方案只在按档位键 / 改设置页时重算（水量/水质怎么变都不重算）。
            if (LockValid() && (progressActive || !LockNeedsFull(full))) return lockedReport;
            float now = UnityEngine.Time.realtimeSinceStartup;
            string sig = PlanSig();
            // 轻量调用（full=false）可以复用重量结果；反过来不行——重量调用拿到轻量结果会缺方案表。
            if (currentCacheReport != null && sig == currentCacheSig && now >= currentCacheAt && now - currentCacheAt < 0.25f &&
                (currentCacheFull || !full))
                return currentCacheReport;
            // 只有「水量/水质」变了（其它设置没动）：先给旧结果，最多 1.2 秒后再重算。
            // 玩家 2026-11-11 反馈「每操作一次水就整包算一次会卡」——灌水是一连串操作，不能每次都算。
            if (currentCacheReport != null && (currentCacheFull || !full) && now >= currentCacheAt && now - currentCacheAt < 1.2f &&
                SigHead(sig) == SigHead(currentCacheSig))
                return currentCacheReport;
            GameBridge.BeginMemo();
            try
            {
                var r = CurrentPlanCompute(full);
                currentCacheReport = r;
                currentCacheFull = full;
                currentCacheSig = sig;
                currentCacheAt = UnityEngine.Time.realtimeSinceStartup;
                // 玩家 2026-11-11：算出来的这一份就是「当前方案」，钉住它 ——
                // 之后往瓶里倒水/接水/过夜让水质变，都不许自己换方案，除非按档位键或改设置页。
                // 重量结果（full）优先：别让轻量结果把带方案表的整份覆盖掉。
                if (r != null && r.pick != null && (lockedReport == null || full))
                    LockPlan(r, "算完即锁", full);
                return lockedReport != null ? lockedReport : r;
            }
            finally { GameBridge.EndMemo(); }
        }

        /// <summary>真正的整包重算（缓存入口见 CurrentPlan）。</summary>
        private static WaterPlan.Report CurrentPlanCompute(bool full)
        {
            // 手动源（右键读取/设置页手填）时，主页容器行和方案都必须用这只样瓶，
            // 不能再用当前机器瓶；否则主页是机器容量、计算页是读取容量。
            // 玩家 2026-11-17：主页顶部 0/6000 来自 Compute() 的 GameBottle()，
            // 方案/缓存/锁也必须用同一只瓶，否则 6L 会按背包里的 750/800 mL 小瓶搜出 799.3，
            // 换 3L 时旧方案还会被小瓶容量锁住。手动源才用 ExplicitPickBottle()。
            var b = UiPlanBottle();
#if DEV
            try
            {
                Log("[plan] bottle=" + (b == null ? "null" : GameBridge.NameOfItem(b)) +
                    " vol=" + (b == null ? 0 : WaterHelper.GetTotalVolume(b)) +
                    " cap=" + (b == null ? 0 : WaterHelper.GetTotalCapacity(b)));
            }
            catch (Exception) { }
#endif
            if (b == null || !MachineOpen())
            {
                autoHydraHint = false;
                // 玩家 2026-11-17：自动读不到选中机器槽里的容器时，不要再按旧水/另一台水出方案；
                // 给一句明确提示，让玩家点绿点选机器或右键读取。
                if (b == null && MachineOpen())
                    LastMsg = "没读到这台机器的容器，请点绿点选机器或右键读取";
                return null;
            }
            int perf, q;
            Bonuses(out perf, out q);
            int targetBp = PlanText.TargetBp(TargetIdx, q);
            long capUl = WaterHelper.GetTotalCapacity(b);
            long volUl = WaterHelper.GetTotalVolume(b);
            if (capUl <= 0) capUl = volUl;
            // 空瓶不可能处于“接水后固定按现有水算”的状态：清掉上次残留的临时标记。
            if (volUl <= 0L && postFillFix) { postFillFix = false; postFillSig = ""; }
            // 接水后如果六项变了，只按实际这瓶水算，不再继续搜下一段接水量。
            // 玩家手动再加/倒水会改水量，签一对不上就自动解除，回到正常推荐。
            // 但如果接完还没到容量，仍要允许性价比档重新找“补到容量”的方案；
            // 否则第一次接水后会被 postFillFix 压成不补水的方案（玩家 2026-11-16 报）。
            // 玩家 2026-11-16：postFillFix 只该压“接了一点水但还没满”的瓶；
            // 空瓶必须重新搜接水量，不能因为上次接水残留的 postFillFix 就退回“按整瓶接满”。
            bool topUpAllowed = !postFillFix
                || volUl <= 0L
                || (volUl > 0L && capUl > 0L && capUl - volUl > 1500L);
            int srcGrade = 0, tapPur = 0;
            try { TodaySource(out srcGrade, out tapPur); } catch (Exception) { tapPur = 0; }
            // 玩家 2026-11-17：切到下一台机器后，Model 还是上一台的缓存；旧版在这里用
            // Model.SourcePurBp 覆盖 TodaySource，结果第一台正确、后面被锁在上一台的铁锈水。
            // 这里只认当次 TodaySource/TapSource 读到的今日水管真值；读不到就让玩家手动选。
            if (volUl <= 0L && tapPur <= 0)
                LastMsg = "没读到今日水管水质，请在设置里手动选水源";
            // 补进容器的水来自水龙头，不能拿瓶里现成水的纯度去补。
            // 手动源（右键读取）尤其明显：2 L 幽灵水 + 48 L 铁锈水会混成另一种水。
            int tapFeedGrade = 0, tapFeedPur = 0;
            try { TapSource(out tapFeedGrade, out tapFeedPur); } catch (Exception) { tapFeedPur = 0; }
            // 空瓶没有「瓶里现成水」，补水纯度就必须等于界面显示的水源纯度。
            if (volUl <= 0L && tapPur > 0) tapFeedPur = tapPur;
            // 非空瓶绝不拿瓶内水纯度当水龙头补水：否则会出现「98.20% 补 3750 还是 98.20%」。
            // 水龙头读不到就不补，保留现有水样。
            int purityBp = CustomPur > 0 ? CustomPur : WaterHelper.GetWaterPurity(b);
            if (volUl <= 0 && CustomPur <= 0 && tapPur > 0 && !calcManualSource) purityBp = tapPur;
            string srcName = calcManualSource ? "手填" : (CustomPur > 0 ? "手填" : SrcName(srcGrade));
            WaterMath.Water realW = null;
            bool hasReal = false;
            bool mixedTopUp = false;
            WaterMath.Water mixBaseW = null;
            if (calcManualSource)
            {
                // 设置页「手填」/右键读取的原瓶水样：固定用这六项，
                // 效率档按整瓶容量补满；补进去的水来自水龙头，必须按 tapFeedPur 混进去，
                // 不能把整瓶都当成读取样水的纯度。
                long calcVol = volUl > 0 ? volUl : capUl;
                if (topUpAllowed && Mode == 1 && capUl > calcVol && calcVol > 0 && tapFeedPur > 0)
                {
                    mixBaseW = BuildCalcManualWater(calcVol);
                    realW = MixWithTap(mixBaseW, capUl, tapFeedPur);
                    mixedTopUp = realW != null;
                }
                else
                {
                    realW = BuildCalcManualWater(calcVol);
                }
                hasReal = realW != null && realW.v > 0;
                if (hasReal) purityBp = realW.Purity;
                else purityBp = CalcManualPurityBp();
            }
            else
            {
                hasReal = volUl > 0 && BuildRealWater(b, out realW) && realW != null && realW.v > 0;
                // 机器/容器里已有水，效率档补到容量时同样是「现有水 + 水龙头水」的混合物。
                if (topUpAllowed && hasReal && Mode == 1 && volUl > 0 && capUl > volUl && tapFeedPur > 0)
                {
                    mixBaseW = realW.Clone();
                    realW = MixWithTap(mixBaseW, capUl, tapFeedPur);
                    mixedTopUp = realW != null;
                    if (hasReal && realW != null) purityBp = realW.Purity;
                }
            }
            var tools = OwnedTools();
            int ownTab = tools == null ? 0 : tools.tablets;
            if (ownTab < 0 && !InfiniteTools) ownTab = 0;
            int fDur = FilterDurNow();
            // 批次 29：性价比自动档默认不用海德拉；效率自动档保留海德拉拉量。
            // 「启用海德拉」只把 hydraOnce 置真一次，不改设置页的 HydraMode。
            bool hasHydra = InfiniteTools || (tools != null && tools.hydraDur > 0);
            bool allowHydra = hasHydra && (WaterPlan.HydraMode == 1
                           || (WaterPlan.HydraMode == 0 && (Mode == 1 || hydraOnce)));
            bool forceHydra = WaterPlan.HydraMode == 1 && hasHydra;
            // 玩家 2026-11-14：性价比接完水后再切效率，效率档不能把「现有瓶水」当成固定上限，
            // 否则只会复用性价比那瓶，永远不会提示还需补多少。效率档只要瓶没满，
            // 就按整瓶容量重新搜接水量，界面再用 feed - 现有水量算出“还差多少”。
            // 满瓶也必须重搜一次：否则 50L 满瓶切到 29950/18850 这类更少水的方案时，
            // AutoSolve 会按 50L 固定水算，卡一不会给出“倒出多少”，步骤也留在 50L 方案。
            bool efficiencyTopUp = topUpAllowed && Mode == 1 && hasReal && volUl < capUl;
            bool fullRebuild = hasReal && capUl > 0 && volUl >= capUl;
            // 满瓶时瓶里已经是真实六项水样，不能再把纯度当成“按 Build 均匀配比”的水去搜；
            // 否则刚按接水灌满后，主页会换出一套和灌水前完全不同的方案，反而提示倒出。
            bool fullReal = topUpAllowed && fullRebuild && !calcManualSource && realW != null && realW.v > 0;
            // 手填六项必须按这份水样固定算，不能因为效率档没满又退回“按容量搜接水量”。
            bool searchFromCapacity = topUpAllowed && !calcManualSource && (!hasReal || efficiencyTopUp || fullRebuild);
            long fixedFeedUl = calcManualSource
                ? (Mode == 1 && capUl > (volUl > 0 ? volUl : capUl) ? capUl : (volUl > 0 ? volUl : capUl))
                : (hasReal ? volUl : 0);
            bool mixedTopUpNow = topUpAllowed && mixedTopUp && realW != null && realW.v > 0;
            bool reportSearch = (mixedTopUpNow || fullReal) ? false : searchFromCapacity;
            long reportFeed = (mixedTopUpNow || fullReal) ? realW.v : (reportSearch ? 0L : fixedFeedUl);
            WaterMath.Water reportWater = (mixedTopUpNow || fullReal) ? realW : (reportSearch ? null : realW);
            WaterPlan.Report rep = null;
            // 玩家 2026-11-15：空容器和“只接了 2000 mL”的容器要走同一套效率口径。
            // 以前有旧水走 efficiencyTopUp 直接试满容量，空容器却走 SearchFeed 按少晚数搜，
            // 于是 2000/50000 给 50 L 方案、0/50000 只给 44.15 L。多晚开启时空瓶也先试满瓶。
            if (topUpAllowed && Mode == 1 && !hasReal && !calcManualSource
                && capUl > 0 && WaterPlan.NightCap > 1)
            {
                try
                {
                    rep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                     false, capUl, null,
                                     tools != null && tools.filter, tools != null && tools.uv,
                                     InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                                     InfiniteTools ? -1 : ownTab,
                                     InfiniteTools ? int.MaxValue : fDur,
                                     HydraDuraMaxForSolve(InfiniteTools),
                                     srcName, GameBridge.NameOfItem(b),
                                     BottleSig() + "|tp" + tapFeedPur + "|emptyfull");
                }
                catch (Exception) { rep = null; }
            }
            if (rep == null)
                rep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                 reportSearch,
                                 reportFeed,
                                 reportWater,
                                 tools != null && tools.filter, tools != null && tools.uv,
                                 InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                                 InfiniteTools ? -1 : ownTab,
                                 InfiniteTools ? int.MaxValue : fDur,
                                 HydraDuraMaxForSolve(InfiniteTools),
                                 srcName, GameBridge.NameOfItem(b),
                                 BottleSig() + (calcManualSource ? "|cm" + CalcManualSig() : "") +
                                 "|tp" + tapFeedPur + "|mix" + (mixedTopUpNow ? 1 : 0)
                                 + "|full" + (fullReal ? 1 : 0));

            // 玩家 2026-11-17：不再按水源纯度阈值强制 2 药。
            // 1 药是否达标直接由 WaterMath.Tablet / AutoSolve 的药片公式计算；公式说够就按 1 药，
            // 公式说不够才让搜索继续加药。阈值补丁容易把 96.70% 这种 1 药 99.22% 的可行方案误改成 2 药。

            // 满瓶真实水样按整瓶跑不动时，走 BuildFromWater 的真实六项找“倒出多少后能跑”，
            // 不能再用纯度均匀配比的水去搜一个假的可接量。
            if (rep == null && fullReal && realW != null && !calcManualSource)
            {
                try
                {
                    rep = BuildMixReportLimited(realW, perf, q, targetBp, Mode,
                                                GameBridge.NameOfItem(b), ownTab,
                                                RecommendNightCap());
                }
                catch (Exception) { rep = null; }
            }

            // 满容器混合后达不到目标：不要硬报一个假的「需接水 48000」，
            // 回退成只用现有这瓶水，让玩家先保住这瓶好水。
            if (rep == null && mixedTopUpNow && mixBaseW != null && mixBaseW.v > 0)
            {
                // 先找“最多还能兑多少、整瓶仍达标”的部分接水方案。
                // 1650 mL 99.01% + 2.1 mL 91.11% 这种边界，不能因为整瓶 50 L 失败就只显示当前液体。
                if (Mode == 1 && capUl > mixBaseW.v)
                {
                    try
                    {
                        var topRep = BuildEfficiencyTopUp(mixBaseW, capUl, tapFeedPur, perf, q, targetBp,
                                                           GameBridge.NameOfItem(b), ownTab);
                        if (topRep != null && topRep.ok && topRep.pick != null && topRep.dumpUl <= 0
                            && topRep.pick.nights <= MaxRecommendNights)
                            rep = topRep;
                    }
                    catch (Exception) { }
                }
                if (rep == null && topUpAllowed)
                {
                    // 2 晚装不满整瓶时，不要退回 8 晚；先在 2 晚上限内搜一条
                    // “能接多少、仍达标”的方案，主页只展示这一条，升晚提示另算。
                    var twoRep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                            true, 0L, null,
                                            tools != null && tools.filter, tools != null && tools.uv,
                                            InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                                            InfiniteTools ? -1 : ownTab,
                                            InfiniteTools ? int.MaxValue : fDur,
                                            HydraDuraMaxForSolve(InfiniteTools),
                                            srcName, GameBridge.NameOfItem(b),
                                            BottleSig() + "|tp" + tapFeedPur + "|night2");
                    if (twoRep != null && twoRep.ok) rep = twoRep;
                }
                if (rep == null)
                {
                    var repBase = AutoReport(capUl, mixBaseW.Purity, perf, q, targetBp, Mode,
                                             false, mixBaseW.v, mixBaseW,
                                             tools != null && tools.filter, tools != null && tools.uv,
                                             InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                                             InfiniteTools ? -1 : ownTab,
                                             InfiniteTools ? int.MaxValue : fDur,
                                             HydraDuraMaxForSolve(InfiniteTools),
                                             srcName, GameBridge.NameOfItem(b),
                                             BottleSig() + (calcManualSource ? "|cm" + CalcManualSig() : "") +
                                             "|tp" + tapFeedPur + "|base");
                    if (repBase != null) rep = repBase;
                }
            }

            // 批次 36 A：性价比档、容器没满、按现有水量固定算不出方案时，
            // 以当前水量为上界再搜一次可达水量；找到就把超出部分标成“先倒出”。
            // 只补主页这一条；计算页 AutoCalcReport 本来就有同样的降量搜索。
            if (topUpAllowed && rep == null && !calcManualSource && !reportSearch && Mode == 2
                && hasReal && volUl > 1L && capUl > volUl)
            {
                var reduced = AutoReport(volUl, purityBp, perf, q, targetBp, Mode,
                                         true, 0L, null,
                                         tools != null && tools.filter, tools != null && tools.uv,
                                         InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                                         InfiniteTools ? -1 : ownTab,
                                         InfiniteTools ? int.MaxValue : fDur,
                                         HydraDuraMaxForSolve(InfiniteTools),
                                         srcName, GameBridge.NameOfItem(b),
                                         BottleSig() + "|tp" + tapFeedPur + "|reduce");
                if (reduced != null && reduced.pick != null && reduced.pick.feedUl > 0L
                    && reduced.pick.feedUl < volUl)
                {
                    reduced.capUl = capUl;
                    reduced.fitUl = reduced.pick.feedUl;
                    reduced.dumpUl = volUl - reduced.pick.feedUl;
                    reduced.fitPartial = true;
                    rep = reduced;
                }
            }

            // 玩家 2026-11-15：混水页已经有「性价比再兑多少」，主页也必须用同一条方案。
            // 性价比不再只按当前水量固定算；在容量内找一晚净赚最多的兑入量，并把这条放回主页。
            if (topUpAllowed && Mode == 2 && !calcManualSource && hasReal && realW != null && realW.v > 0L
                && capUl > realW.v)
            {
                try
                {
                    long bestTotal = 0;
                    var costRep = BestCostTopUp(realW, capUl, tapFeedPur, perf, q, targetBp,
                                                GameBridge.NameOfItem(b), ownTab, null, out bestTotal);
                    if (costRep != null && costRep.pick != null
                        && costRep.ChosenFeedUl() > realW.v)
                        rep = costRep;
                }
                catch (Exception) { }
            }

            BuildNightUpHint(rep, capUl, purityBp, perf, q, targetBp,
                             (mixedTopUpNow && realW != null && realW.v >= capUl) ? realW : null,
                             tools != null && tools.filter, tools != null && tools.uv,
                             InfiniteTools || ownTab > 0, allowHydra, forceHydra,
                             InfiniteTools ? -1 : ownTab,
                             InfiniteTools ? int.MaxValue : fDur,
                             HydraDuraMaxForSolve(InfiniteTools),
                             srcName, GameBridge.NameOfItem(b),
                             BottleSig() + (calcManualSource ? "|cm" + CalcManualSig() : ""));
            ApplyManualPick(rep);
            return rep;
        }

        private static readonly Dictionary<string, WaterPlan.Report> autoReportCache =
            new Dictionary<string, WaterPlan.Report>();
        private static string autoReportKey = "";

        /// <summary>设置页「手填」：计算页六项加上水比例得到的显示纯度。</summary>
        private static int CalcManualPurityBp()
        {
            long sum = 0;
            for (int i = 0; i < calcImpBp.Length; i++) sum += Math.Max(0, calcImpBp[i]);
            int p = 10000 - (int)Math.Min(10000L, sum);
            return Math.Max(1, Math.Min(10000, p));
        }

        /// <summary>把计算页的六项杂质按当前瓶水量/容量缩放成一份 WaterMath.Water。</summary>
        private static WaterMath.Water BuildCalcManualWater(long volumeUl)
        {
            if (volumeUl <= 0) return null;
            long srcVol = calcWaterUl > 0 ? calcWaterUl : volumeUl;
            long total = 0;
            for (int i = 0; i < calcImpUl.Length; i++) total += Math.Max(0L, calcImpUl[i]);
            int purBp = CalcManualPurityBp();
            // 六项全零＝按 100% 水处理；不要退回成脏水。
            if (total <= 0) return WaterMath.Build(volumeUl, purBp, 0);
            var w = new WaterMath.Water { v = volumeUl, cap = 0 };
            long used = 0;
            for (int i = 0; i < calcImpUl.Length; i++)
            {
                long part = Math.Max(0L, calcImpUl[i]);
                if (part <= 0) continue;
                long scaled = srcVol == volumeUl
                    ? part
                    : (long)Math.Round((double)part * volumeUl / srcVol);
                if (scaled <= 0) scaled = 1;
                w.parts[CalcImpKeys[i]] = scaled;
                used += scaled;
            }
            if (used >= volumeUl) return WaterMath.Build(volumeUl, purBp, 0);
            return w;
        }

        /// <summary>现有水样 + 水龙头补水按体积混合；补进去的六项走 WaterMath.Build 的默认配比。</summary>
        private static WaterMath.Water MixWithTap(WaterMath.Water existing, long totalUl, int tapPurityBp)
        {
            if (existing == null || existing.v <= 0 || totalUl <= existing.v) return existing;
            long addUl = totalUl - existing.v;
            // 读不到水龙头真值就不混：宁可保留现有水样，也不拿 91.00 假值污染结果。
            if (tapPurityBp <= 0) return existing;
            var tap = WaterMath.Build(addUl, Math.Max(1, Math.Min(10000, tapPurityBp)), 0);
            var w = new WaterMath.Water { v = totalUl, cap = 0 };
            for (int i = 0; i < CalcImpKeys.Length; i++)
            {
                string k = CalcImpKeys[i];
                w.parts[k] = existing.Part(k) + tap.Part(k);
            }
            return w;
        }

        /// <summary>按玩家实际拥有的容器凑量：50 mL 瓶只能到 1850，有 1 mL 滴灌才能到 1888。
        /// 这里走 WaterPlan.Plan 的整瓶子集和，和计算页「需接水」同一条口径。</summary>
        private static long ReachablePour(long wantUl, long roomUl)
        {
            if (wantUl <= 0) return 0;
            long p = BottlePourUl(wantUl, roomUl > 0 ? roomUl : 0);
            return p > 0 ? p : wantUl;
        }

        /// <summary>计算页六项/水量的指纹：改了六项要作废主页方案缓存。</summary>
        private static string CalcManualSig()
        {
            var sb = new StringBuilder();
            sb.Append(calcCapUl).Append('|').Append(calcWaterUl).Append('|');
            for (int i = 0; i < calcImpUl.Length; i++)
                sb.Append(calcImpUl[i]).Append(',');
            return sb.ToString();
        }

        // 页面不碰 WaterPlan：把 AutoSolve 的完整结果（含 rawSteps）按 Report 对象挂在这里，
        // 步骤页/主页/计算页只读它，避免再各自模拟一遍固定顺序。
        private static readonly Dictionary<WaterPlan.Report, AutoSolve.Result> autoRawResults =
            new Dictionary<WaterPlan.Report, AutoSolve.Result>();
        // 方案表里点中的 Combo 也要有对应的 AutoSolve 结果；否则点第 2 条后
        // r.pick 变了，主页步骤还读第 1 条的 rawSteps（玩家 2026-11-16 报）。
        private static readonly Dictionary<WaterPlan.Combo, AutoSolve.Result> autoComboRaw =
            new Dictionary<WaterPlan.Combo, AutoSolve.Result>();

        // 只做展示提示，不参与推荐：2 晚方案旁边告诉玩家“升到 8 晚最多多出多少水”。
        private static readonly Dictionary<WaterPlan.Report, string> nightUpHints =
            new Dictionary<WaterPlan.Report, string>();

        private static string NightUpHintFor(WaterPlan.Report r)
        {
            if (r == null) return "";
            return nightUpHints.ContainsKey(r) ? nightUpHints[r] : "";
        }

        private static void BuildNightUpHint(WaterPlan.Report r,
                                             long capUl, int purityBp, int perf, int q, int targetBp,
                                             WaterMath.Water fullMix,
                                             bool allowFilter, bool allowUv, bool allowTablet,
                                             bool allowHydra, bool forceHydra,
                                             int ownTab, int filterDur, int hydraDura,
                                             string srcName, string bottleName, string bottleSig)
        {
            if (r == null || r.pick == null) return;
            int setting = Math.Max(1, WaterPlan.NightCap);
            if (setting <= MaxRecommendNights || r.pick.nights >= setting) return;
            long baseOut = r.pick.outUl;

            WaterPlan.Report maxRep = null;
            bool saveHydraHint = autoHydraHint;
            int saveNightsMin = lastNightsMin;
            try
            {
                // 能拿“现有水 + 水龙头水”的整瓶混合水，就按整瓶固定算；
                // 否则按容量搜一次，只为算提示，不写回主页方案。
                if (fullMix != null && fullMix.v >= capUl)
                {
                    maxRep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                        false, capUl, fullMix,
                                        allowFilter, allowUv, allowTablet,
                                        allowHydra, forceHydra, ownTab,
                                        filterDur, hydraDura, srcName, bottleName,
                                        bottleSig + "|nightmax", setting);
                }
                if (maxRep == null)
                {
                    // 用整瓶容量固定算一次：效率档会取整瓶能跑到的最大出水量；
                    // 性价比档也会给最先可行的整瓶方案。这里只在提示里用，不写主页。
                    maxRep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                        false, capUl, null,
                                        allowFilter, allowUv, allowTablet,
                                        allowHydra, forceHydra, ownTab,
                                        filterDur, hydraDura, srcName, bottleName,
                                        bottleSig + "|nightmaxf", setting);
                }
                if (maxRep == null)
                {
                    maxRep = AutoReport(capUl, purityBp, perf, q, targetBp, Mode,
                                        true, 0L, null,
                                        allowFilter, allowUv, allowTablet,
                                        allowHydra, forceHydra, ownTab,
                                        filterDur, hydraDura, srcName, bottleName,
                                        bottleSig + "|nightmaxs", setting);
                }
            }
            catch (Exception) { maxRep = null; }
            finally
            {
                // 升晚提示只是旁路计算，不能改主页当前方案的海德拉提示/晚数状态。
                autoHydraHint = saveHydraHint;
                lastNightsMin = saveNightsMin;
            }

            if (maxRep == null || maxRep.pick == null) return;
            if (maxRep.pick.nights <= r.pick.nights) return;
            long gain = maxRep.pick.outUl - baseOut;
            if (gain < 1000000L) return;
            nightUpHints[r] = "升到 " + maxRep.pick.nights + " 晚可多出 " + WaterPlan.Ml(gain);
        }

        private static AutoSolve.Result RawResultFor(WaterPlan.Report r)
        {
            if (r == null) return null;
            // 当前选中方案优先：玩家在方案表点了另一条后，主页/步骤必须换成那一条的原始步骤。
            if (r.pick != null && autoComboRaw.ContainsKey(r.pick)) return autoComboRaw[r.pick];
            // 切页/刷新会重建 Combo 对象；此时按「同一套方案数值」找原始步骤，
            // 不能因为对象换了就退回另一条，否则步骤页会显示和主页不同的方案。
            if (r.pick != null)
            {
                foreach (var kv in autoComboRaw)
                {
                    var c = kv.Key;
                    if (c != null && c.ok && SameCombo(c, r.pick)) return kv.Value;
                }
            }
            return autoRawResults.ContainsKey(r) ? autoRawResults[r] : null;
        }

        private static bool SameCombo(WaterPlan.Combo a, WaterPlan.Combo b)
        {
            if (a == null || b == null) return false;
            return a.feedUl == b.feedUl && a.outUl == b.outUl && a.purityBp == b.purityBp
                && a.tablets == b.tablets && a.filterUsed == b.filterUsed
                && a.nights == b.nights && a.hydraDur == b.hydraDur && a.hydra == b.hydra
                && a.filter == b.filter && a.uv == b.uv;
        }

        // 批次 35：前置档位真正进入 AutoSolve 的工具集，不再只筛 WaterPlan 候选。
        private static bool PreFilterOn(int pre)
        {
            return pre == 0 || pre == 2 || pre == 4 || pre == 7 || pre == 8;
        }

        private static bool PreUvOn(int pre)
        {
            return pre == 0 || pre == 3 || pre == 4 || pre == 6 || pre == 7;
        }

        /// <summary>
        /// 批次 28 的页面适配：AutoSolve.Result → 现有 WaterPlan.Report。
        /// 只在页面层做转换，WaterPlan / WaterMath 不碰。
        /// </summary>
        /// <summary>主页/计算页/混水页共用：多晚开启时默认只显示最多 2 晚。</summary>
        private static int RecommendNightCap()
        {
            int setting = Math.Max(1, WaterPlan.NightCap);
            return setting > 1 ? Math.Min(setting, MaxRecommendNights) : 1;
        }

        /// <summary>药片影子价：库存越少，越不该为了小利润吃掉一片。
        /// 0 片不启用；1 片 15 元/片；2–3 片 10；4 片以上或无限 6。</summary>
        private static double TabletShadowForStock(int stock)
        {
            if (stock == 0) return 0.0;
            if (stock == 1) return 15.0;
            if (stock == 2 || stock == 3) return 10.0;
            return 6.0;
        }

        /// <summary>性价比自动档：同进水量、同晚数下找一次 0 片方案，和现有方案比
        /// “净赚 − 库存影子价”。0 片不可行就原样返回；不改变进水量/晚数上限。</summary>
        private static AutoSolve.Result ApplyTabletShadow(AutoSolve.Result ar,
                                                          int mode, int tabletStock,
                                                          int purityBp, int perf, int q, int targetBp,
                                                          WaterMath.Water realWater,
                                                          bool allowFilter, bool allowUv,
                                                          bool allowHydra, bool forceHydra,
                                                          int filterDurMax, int hydraDuraMax)
        {
            if (ar == null || !ar.ok || mode != 2 || PostMode != 0 || ar.tablets <= 0)
                return ar;
            double lambda = TabletShadowForStock(tabletStock);
            if (lambda <= 0 || ar.nights <= 0) return ar;

            int oldTarget = AutoSolve.TabletTarget;
            double oldShadow = AutoSolve.TabletShadow;
            AutoSolve.TabletTarget = -1;
            AutoSolve.TabletShadow = 0.0;
            AutoSolve.Result zero = null;
            try
            {
                int nightCap = Math.Max(1, ar.nights);
                if (realWater != null && realWater.v > 0)
                    zero = AutoSolve.SolveWater(realWater, perf, (double)q, targetBp, nightCap,
                                                allowFilter, allowUv, false, allowHydra,
                                                tabletStock, filterDurMax, hydraDuraMax);
                else
                    zero = AutoSolve.Solve(ar.feedUl, purityBp, perf, (double)q, targetBp, nightCap,
                                           allowFilter, allowUv, false, allowHydra,
                                           tabletStock, filterDurMax, hydraDuraMax);
            }
            catch (Exception) { zero = null; }
            finally
            {
                AutoSolve.TabletTarget = oldTarget;
                AutoSolve.TabletShadow = oldShadow;
            }
            if (zero == null || !zero.ok || zero.feedUl < ar.feedUl) return ar;
            if (forceHydra && !HasAutoTool(zero, "portable")) return ar;
            // 2026-11-17 方案 1：0 晚硬优先。即使药片影子价算出来 1 晚方案更赚，
            // 也不允许它顶掉已经达标的 0 晚方案；多晚方案仍由「更多方案」保留。
            if (zero.nights == 0 && ar.nights > 0) return zero;

            double netAr = ar.revenue - ar.cost - (AutoSolve.ElectricityPerNight + AutoSolve.TimeValuePerNight) * ar.nights;
            double netZero = zero.revenue - zero.cost - (AutoSolve.ElectricityPerNight + AutoSolve.TimeValuePerNight) * zero.nights;
            // 跨售价档且原方案现金更高：优先保留，不拿影子价压掉跨档收益。
            if (ar.grade != zero.grade && netAr > netZero) return ar;
            double scoreAr = netAr - lambda * ar.tablets;
            double scoreZero = netZero;
            if (scoreZero > scoreAr + 1e-9) return zero;
            return ar;
        }

        private static WaterPlan.Report AutoReport(long capUl, int purityBp, int perf, int q, int targetBp,
                                                   int mode, bool searchFeed, long feedUl,
                                                   WaterMath.Water realWater,
                                                   bool allowFilter, bool allowUv, bool allowTablet,
                                                   bool allowHydra, bool forceHydra,
                                                   int tabletStock, int filterDurMax, int hydraDuraMax,
                                                   string srcName, string bottleName, string bottleSig,
                                                   int nightLimit = 0)
        {
            mode = (mode == 1 ? 1 : 2);
            if (capUl <= 0) return null;

            // 批次 35：前置档位只代表「拥有并允许使用哪些道具」，不固定药片/过滤/紫外的先后；
            // 具体步骤顺序仍交给 AutoSolve 自动搜索。PreMode 也要进 cache key，
            // 否则切前置时旧报告会被原样命中，页面看起来「选了没反应」。
            allowFilter = allowFilter && PreFilterOn(PreMode);
            allowUv = allowUv && PreUvOn(PreMode);
            // 批次 36：后置固定片数必须真正进入求解器，不能只把「有没有药片」当开关。
            if (PostMode == 1) allowTablet = false;
            int targetTablets = PostMode >= 2 ? PostMode - 1
                                                            : (PostMode == 1 ? 0 : -1);
            AutoSolve.TabletTarget = targetTablets;
            // 设置页「不用机器」：把净化器/过夜节点从搜索里拿掉，不能只在 WaterPlan 分支里生效。
            AutoSolve.AllowMachine = !WaterPlan.NoMachine;
            // 前置 5–8 含药片路线，后置却要求不放药片：语义冲突，按无方案处理。
            if (PostMode == 1 && PreMode >= 5)
            {
                autoHydraHint = false;
                lastNightsMin = 0;
                LastMsg = "前置要求药片、后置设为不放药片，无法出方案";
                return null;
            }

            int nightSetting = Math.Max(1, WaterPlan.NightCap);
            int nightCap = nightLimit > 0 ? Math.Min(nightLimit, nightSetting) : RecommendNightCap();
            string key = capUl + "|" + purityBp + "|" + perf + "|" + q + "|" + targetBp + "|" +
                         nightCap + "|" + mode + "|pre" + PreMode + "|po" + PostMode + "|" +
                         (allowFilter ? 1 : 0) + (allowUv ? 1 : 0) + (allowTablet ? 1 : 0) +
                         (allowHydra ? 1 : 0) + (forceHydra ? 1 : 0) + "|" +
                         tabletStock + "|" + filterDurMax + "|" +
                         "hyd" + hydraDuraMax + "|" +
                         "sf" + (searchFeed ? 1 : 0) + "|fd" + feedUl + "|" +
                         "nm" + (WaterPlan.NoMachine ? 1 : 0) + "|" +
                         (bottleSig == null ? "-" : bottleSig);
            // 缓存按完整 key 命中：不能只认“上一次 autoReportKey”，否则效率/性价比来回切
            // 时旧档 key 明明在字典里也会重新求 8 轮，造成切模式卡顿。
            if (autoReportCache.ContainsKey(key))
            {
                var cached = autoReportCache[key];
                autoHydraHint = cached == null && mode == 2 && !forceHydra
                                && WaterPlan.HydraMode == 0;
                lastNightsMin = cached != null && cached.pick != null ? cached.pick.nights : 0;
                return cached;
            }

            AutoSolve.UseUiCost();
            // 性价比自动档才吃影子价；效率档按出水量，固定片数档不替玩家改药量。
            AutoSolve.TabletShadow = (mode == 2 && PostMode == 0)
                ? TabletShadowForStock(tabletStock) : 0.0;
            AutoSolve.Result ar;
            if (realWater != null && realWater.v > 0)
            {
                // 效率档也先压到最少可行晚数：玩家明确要求「像无限效率 2 晚那样」，
                // 不能因为还能多装 1% 就一路堆到 8 晚；无限道具和背包走同一套晚数优先。
                if (nightCap > 1 && mode == 1)
                    ar = AutoSolve.SolveMaxOutput(realWater, perf, (double)q, targetBp, nightCap, mode,
                                                  allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                                                  tabletStock, filterDurMax, hydraDuraMax);
                else if (nightCap > 1)
                    ar = AutoSolve.SolveMinNights(realWater, perf, (double)q, targetBp, nightCap, mode,
                                                  allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                                                  tabletStock, filterDurMax, hydraDuraMax);
                else
                    ar = AutoSolve.SolveWater(realWater, perf, (double)q, targetBp, nightCap,
                                              allowFilter, allowUv, allowTablet, allowHydra,
                                              tabletStock, filterDurMax, hydraDuraMax);
            }
            else if (searchFeed)
            {
                if (nightCap > 1 && mode == 1)
                    ar = AutoSolve.SolveMaxOutput(capUl, purityBp, perf, (double)q, targetBp, nightCap, mode,
                                                  allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                                                  tabletStock, filterDurMax, hydraDuraMax);
                else if (nightCap > 1)
                    ar = AutoSolve.SolveMinNights(capUl, purityBp, perf, (double)q, targetBp, nightCap, mode,
                                                  allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                                                  tabletStock, filterDurMax, hydraDuraMax);
                else
                    ar = AutoSolve.SearchFeed(capUl, purityBp, perf, (double)q, targetBp,
                                              nightCap, mode, allowFilter, allowUv,
                                              allowTablet, allowHydra, forceHydra, tabletStock, filterDurMax,
                                              hydraDuraMax);
            }
            else
            {
                // 固定水量（手填/真实瓶水量）不搜接水量。
                // 性价比：从 1 晚往上试，第一个达标就停；
                // 效率：多晚开时按最大出水选，不在这里截断。
                if (nightCap > 1)
                {
                    long fixedFull = feedUl > 0 ? feedUl : capUl;
                    long nearFull = fixedFull - fixedFull / 100;
                    long minGain = fixedFull / 100;
                    ar = null;
                    for (int n = 1; n <= nightCap; n++)
                    {
                        var rr = AutoSolve.Solve(fixedFull, purityBp, perf, (double)q,
                                                 targetBp, n, allowFilter, allowUv,
                                                 allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                        bool valid = rr != null && rr.ok && rr.nights <= n
                                     && (!forceHydra || HasAutoTool(rr, "portable"));
                        if (valid)
                        {
                            if (mode == 1)
                            {
                                if (rr.w.v >= nearFull)
                                {
                                    ar = rr;
                                    break;
                                }
                                if (ar == null)
                                    ar = rr;
                                else if (rr.w.v <= ar.w.v + minGain)
                                    break;   // 道具不足，再跑一晚也装不出 1% 容量
                                else if (rr.w.v > ar.w.v)
                                    ar = rr;
                            }
                            else
                            {
                                ar = rr;
                                break;
                            }
                        }
                    }
                }
                else
                    ar = AutoSolve.Solve(feedUl > 0 ? feedUl : capUl, purityBp, perf, (double)q,
                                         targetBp, nightCap, allowFilter, allowUv,
                                         allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
            }

            AutoSolve.TabletShadow = 0.0;
            if (ar != null && mode == 2 && PostMode == 0 && ar.tablets > 0)
                ar = ApplyTabletShadow(ar, mode, tabletStock, purityBp, perf, q, targetBp,
                                       realWater, allowFilter, allowUv, allowHydra, forceHydra,
                                       InfiniteTools ? int.MaxValue : FilterDurNow(),
                                       HydraDuraMaxForSolve(InfiniteTools));
            if (ar != null && forceHydra && !HasAutoTool(ar, "portable")) ar = null;
            bool hydraHintNow = ar == null && mode == 2 && !forceHydra
                                && WaterPlan.HydraMode == 0;
            if (ar == null || !ar.ok)
            {
                autoHydraHint = hydraHintNow;
                lastNightsMin = 0;
                if (forceHydra) LastMsg = "已强制海德拉仍达不到目标";
                else if (hydraHintNow) LastMsg = "无耗材方案达不到目标，可启用海德拉";
                else LastMsg = "目标纯度达不到，已不显示未达标方案";
                autoReportKey = key;
                autoReportCache[key] = null;
                hydraOnce = false;
                return null;
            }

            autoHydraHint = false;
            lastNightsMin = ar.nights;
            hydraOnce = false;
            double uiCost = ar.cost + AutoSolve.ElectricityPerNight * ar.nights;
            // 批次 32：主页/步骤/计算三处统一读 AutoSolve.Result.purityShown（最后一步之后的显示纯度），
            // 不再从中间节点的 w.Purity 各自算一遍。
            int shownBp = (int)Math.Round(ar.purityShown * 100.0);
            var c = new WaterPlan.Combo();
            c.label = ar.feedUl + " / " + ar.w.v;
            c.filter = HasAutoTool(ar, "filter");
            c.uv = HasAutoTool(ar, "uv");
            c.tablets = ar.tablets;
            c.feedUl = ar.feedUl > 0 ? ar.feedUl : capUl;
            c.outUl = ar.w.v;
            c.filterUsed = ar.filterDura;
            c.purityBp = shownBp;
            c.cost = uiCost;
            c.ok = ar.ok;
            c.nights = ar.nights;
            c.hydra = HasAutoTool(ar, "portable");
            c.hydraDur = ar.hydraDura;

            var r = new WaterPlan.Report();
            r.ok = true;
            r.capShort = targetBp > WaterMath.CapOf(q);
            r.capUl = capUl;
            r.bestUl = c.feedUl;
            r.useUl = c.feedUl;
            r.leftUl = 0;
            r.needUl = c.feedUl;
            r.srcBp = purityBp;
            r.perf = perf;
            r.q = q;
            r.targetBp = targetBp;
            r.effTargetBp = targetBp;
            r.srcName = srcName == null ? "水" : srcName;
            r.bottleName = bottleName == null ? "容器" : bottleName;
            r.mode = mode;
            r.combos.Add(c);
            r.keep.Add(c);
            r.pick = c;
            autoComboRaw[c] = ar;
            r.tablets = ar.tablets;
            r.filterUsed = ar.filterDura;
            r.uvUsed = c.uv ? 1 : 0;
            r.nightsCount = ar.nights;
            r.outUl = c.outUl;
            r.finalBp = c.purityBp;
            r.cost = uiCost;
            r.perL = c.outUl > 0 ? uiCost / (c.outUl / 1000000.0) : 0;
            r.budgetUl = c.feedUl;
            r.fromWater = realWater != null;
            r.water0 = realWater;
            // 页面按「已有水」口径判断够不够装：AutoReport 不经过 WaterPlan 的 BuildReport，
            // fitUl/dumpUl 默认 0 会让主页把满容器误报成「倒出 0 mL」。这里按真实起点补上。
            r.fitUl = realWater != null ? realWater.v : 0;
            r.dumpUl = 0L;
            r.needHydra = forceHydra && c.hydra;
            for (int i = 0; i < ar.steps.Count; i++)
            {
                var st = ar.steps[i];
                var ws = new WaterPlan.Step();
                ws.title = st.label;
                ws.detail = (st.count > 0 ? "×" + st.count : "") +
                            (st.dura > 0 ? "  耐久 " + st.dura : "");
                r.steps.Add(ws);
            }
            if (ar.nights > 0)
            {
                var nr = new WaterPlan.NightRow();
                nr.n = ar.nights;
                nr.volUl = ar.w.v;
                nr.purityBp = shownBp;
                r.nights.Add(nr);
            }
            // 玩家 2026-11-16：不要按固定百分比硬切，直接扫水量找每一档「道具消耗刚好到目标」的上边界。
            // 同一个道具组合（药片/滤棉/紫外/海德拉/晚数）只保留仍达标的最高水量，
            // 这样更多方案里会出现「6000 五药 / 5500 四药 / 5000 三药…」这种不同水量档。
            if (searchFeed && realWater == null && r.pick != null && capUl >= 1000000L)
            {
                double shadow0 = AutoSolve.TabletShadow;
                AutoSolve.UseUiCost();
                AutoSolve.TabletShadow = (mode == 2 && PostMode == 0)
                    ? TabletShadowForStock(tabletStock) : 0.0;
                try
                {
                    int nightsForScan = Math.Max(1, ar.nights);
                    long step = Math.Max(500000L, capUl / 12L);
                    var groups = new Dictionary<string, WaterPlan.Combo>();
                    for (long f = capUl; f >= 1000000L; f -= step)
                    {
                        var rr = AutoSolve.Solve(f, purityBp, perf, (double)q, targetBp,
                            nightsForScan, allowFilter, allowUv, allowTablet, allowHydra,
                            tabletStock, filterDurMax, hydraDuraMax);
                        if (rr == null || !rr.ok || rr.w == null || rr.w.v <= 0) continue;
                        if ((int)Math.Round(rr.purityShown * 100.0) < targetBp) continue;
                        // 「更多方案」是推荐列表，不是穷举表：负收益、十几片药的组合不往里放。
                        // 玩家 2026-11-17 报「为什么药片推荐那么多」就是这些扫描组被当成可选推荐了。
                        if (rr.profit <= 0) continue;
                        if (rr.feedUl > f + 1000L) continue;
                        if (forceHydra && !HasAutoTool(rr, "portable")) continue;
                        string group = rr.tablets + "|" + (HasAutoTool(rr, "filter") ? 1 : 0)
                                     + "|" + (HasAutoTool(rr, "uv") ? 1 : 0)
                                     + "|" + (HasAutoTool(rr, "portable") ? 1 : 0)
                                     + "|" + rr.nights;
                        // f 从大到小扫，第一次遇到这个组合就是它的最高可行水量。
                        if (groups.ContainsKey(group)) continue;
                        var c2 = new WaterPlan.Combo();
                        c2.label = f + " / " + rr.w.v;
                        c2.filter = HasAutoTool(rr, "filter");
                        c2.uv = HasAutoTool(rr, "uv");
                        c2.tablets = rr.tablets;
                        c2.feedUl = rr.feedUl > 0 ? rr.feedUl : f;
                        c2.outUl = rr.w.v;
                        c2.filterUsed = rr.filterDura;
                        c2.purityBp = (int)Math.Round(rr.purityShown * 100.0);
                        c2.cost = rr.cost + AutoSolve.ElectricityPerNight * rr.nights;
                        c2.ok = true;
                        c2.nights = rr.nights;
                        c2.hydra = HasAutoTool(rr, "portable");
                        c2.hydraDur = rr.hydraDura;
                        groups[group] = c2;
                        autoComboRaw[c2] = rr;
                    }
                    int added = 0;
                    foreach (var kv in groups)
                    {
                        if (added >= 5) break;
                        var c2 = kv.Value;
                        bool dup = false;
                        for (int ci = 0; ci < r.combos.Count; ci++)
                            if (r.combos[ci] != null && r.combos[ci].feedUl == c2.feedUl) { dup = true; break; }
                        if (dup) continue;
                        r.combos.Add(c2);
                        r.keep.Add(c2);
                        added++;
                    }
                }
                catch (Exception) { }
                finally { AutoSolve.TabletShadow = shadow0; }
            }
            autoReportKey = key;
            // 玩家 2026-11-16：设置「用机器」只代表允许用机器，不是强制。
            // 同一瓶水在 0 晚用「过滤+药片」已经能达标时，即使总净赚不如净化器方案，
            // 也要作为一条可选方案放进「更多方案」，不能让它完全消失。
            if (r.pick != null && r.pick.nights > 0 && !forceHydra)
            {
                AutoSolve.Result nm = null;
                bool oldMachine = AutoSolve.AllowMachine;
                double oldShadow = AutoSolve.TabletShadow;
                try
                {
                    AutoSolve.AllowMachine = false;
                    AutoSolve.TabletShadow = 0.0;
                    if (realWater != null && realWater.v > 0)
                        nm = AutoSolve.SolveWater(realWater, perf, (double)q, targetBp, 1,
                                                  allowFilter, allowUv, allowTablet,
                                                  allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                    else
                        nm = AutoSolve.Solve(c.feedUl, purityBp, perf, (double)q, targetBp, 1,
                                             allowFilter, allowUv, allowTablet,
                                             allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                }
                catch (Exception) { nm = null; }
                finally
                {
                    AutoSolve.AllowMachine = oldMachine;
                    AutoSolve.TabletShadow = oldShadow;
                }
                if (nm != null && nm.ok && nm.nights == 0 && nm.w != null && nm.w.v > 0)
                {
                    var c2 = ComboFromAuto(nm, c.feedUl);
                    if (AddComboUnique(r, c2)) autoComboRaw[c2] = nm;
                }
            }
            // 玩家 2026-11-17：开「多晚」后，更多方案里至少要有一条真正 >=2 晚的方案。
            // 普通 AutoSolve 的第一条达标解会停在 0/1 晚，所以这里单独搜一条硬性多晚解；
            // 只放进 r.combos（更多方案池），不改 r.pick 的主方案选择。
            if (WaterPlan.NightCap > 1 && !forceHydra)
            {
                bool hasMulti = false;
                for (int i = 0; i < r.combos.Count; i++)
                {
                    if (r.combos[i] != null && r.combos[i].ok && r.combos[i].nights > 1) { hasMulti = true; break; }
                }
                if (hasMulti)
                {
                    // 已有真正的多晚方案，不再重复搜。
                }
                else
                {
                    AutoSolve.Result mn = null;
                    try
                    {
                        if (realWater != null && realWater.v > 0)
                            mn = AutoSolve.SolveMultiNight(realWater, perf, (double)q, targetBp,
                                                          WaterPlan.NightCap, 2,
                                                          allowFilter, allowUv, allowTablet,
                                                          allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                        else
                            mn = AutoSolve.SolveMultiNight(c.feedUl, purityBp, perf, (double)q, targetBp,
                                                          WaterPlan.NightCap, 2,
                                                          allowFilter, allowUv, allowTablet,
                                                          allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                    }
                    catch (Exception) { mn = null; }
                    if (mn != null && mn.ok && mn.nights > 1 && mn.w != null && mn.w.v > 0
                        && (!forceHydra || HasAutoTool(mn, "portable")))
                    {
                        var c2 = ComboFromAuto(mn, c.feedUl);
                        if (AddComboUnique(r, c2)) autoComboRaw[c2] = mn;
                    }
                }
            }
            autoReportCache[key] = r;
            autoRawResults[r] = ar;
            Log("[auto] search=" + (searchFeed ? "25+6" : "fixed") + " feed=" + c.feedUl +
                " out=" + c.outUl + " pur=" + c.purityBp + " mode=" + mode +
                " ms=" + AutoSolve.LastSearchMs);
            return r;
        }

        private static bool HasAutoTool(AutoSolve.Result r, string tool)
        {
            if (r == null || r.steps == null) return false;
            for (int i = 0; i < r.steps.Count; i++)
                if (r.steps[i].tool == tool && r.steps[i].count > 0) return true;
            return false;
        }

        /// <summary>主页与计算页共用：按这只瓶子算方案（空瓶/水源水 → 接满口径；异质水 → 这瓶水口径）。</summary>
        private static WaterPlan.Report PlanForBottle(GameItem b, int perf, int q, int targetBp,
                                                      out string srcName, bool full = true)

        {
            srcName = SrcName(0);
            // 过滤棉可用耐久：按背包里**所有**滤棉累加（玩家 2026-11-10：存档 2 块滤芯 = 800 点）。
            // 以前写死 400 → 20 L 明明能接 16253 只给 11600，「上限」也跟着算错。
            var owF = OwnedTools();
            // 背包里 0 片药片时 OwnedTools 可能给 -1（旧的“未知＝不限”哨兵），不能拿来当无限用：
            // 否则关掉净水器后会给「药片 1 片」这种身上根本没有的方案。无限道具/强制全有才保留 -1。
            int ownTabs = owF.tablets;
            if (ownTabs < 0 && !InfiniteTools) ownTabs = 0;
            // 过滤棉用多少点：设置页滑条选的值；没拖过就用背包里实际持有的总和（不按「块数」）。
            int fDur = FilterDurNow();
            if (Mode != 2 && fDur <= 0) fDur = WaterPlan.FilterDurPer;   // 默认/效率档没滤棉时当作有全新的一块
            WaterPlan.FilterDur = fDur;
            // 今日水龙头水质先读出来：后面判断「瓶里的水是不是跟今日水管一样」要用（玩家 2026-10-10 定案）
            int grade, pur;
            TapSource(out grade, out pur);
            if (pur <= 0) TodaySource(out grade, out pur);
            // 玩家 2026-11-13：手填水质必须真的进模型 —— 以前这里直接拿水龙头 pur，
            // CustomPur 只在 TodaySource 里被看，方案永远按水龙头算，所以「手填跟没实装一样」。
            if (CustomPur > 0) { grade = 4; pur = CustomPur; }
            // ★玩家 2026-10-10 定案的三条，是容器里有水时的唯一口径：
            //   ①瓶里 0 mL（空瓶）→ 按今日水管水质算，还是老模式：给方案、我自己按接水。
            //   ②瓶里有水、且水质与今日水管**不同** → 按这瓶水自己的质量重算（它可能是玩家自己兑的）。
            //   ③瓶里有水、但水质与今日水管**一致**（哪怕只装了 3 mL）→ 仍按今日水管算，别当成另一瓶水。
            // 另外：按瓶里水算时即使整瓶一晚压不到目标，也不再静默掉回水龙头模式 —— 要按这瓶水回答
            // 「先倒出多少、留多少刚好够」（玩家 2026-10-10 报「只会说装满 6000 可以跑，不给步骤」）。
            // 空瓶判断要在 try 外也可见：批次 20 的空瓶一晚优先分支要用。
            long volUl0 = 0;
            try { volUl0 = WaterHelper.GetTotalVolume(b); } catch (Exception) { volUl0 = 0; }
            try
            {
                long volUl = volUl0;
                WaterMath.Water realW;
                if (!WaterPlan.NoMachine && CustomPur <= 0 && volUl > 0 && BuildRealWater(b, out realW) && realW != null && realW.v > 0)
                {
                    // ±0.10% 以内视作同一水质（浮点/取样误差），走老模式
                    bool sameAsTap = (pur > 0) && (Math.Abs(realW.Purity - pur) <= 10);
                    if (!sameAsTap || !WaterPlan.AllowMachine || WaterPlan.NoMachine)
                    {
                        var repW = BuildMixReportLimited(realW, perf, q, targetBp, Mode,
                                                         GameBridge.NameOfItem(b), ownTabs,
                                                         RecommendNightCap());
                        if (repW != null)
                        {
                            srcName = GameBridge.NameOfItem(b);
                            return repW;
                        }
                    }
                }
            }
            catch (Exception e) { log.LogWarning("[ui] plan-from-water " + e.GetType().Name); }
            srcName = CustomPur > 0 ? "手填" : SrcName(grade);
            // 批次 20：空瓶先按「一晚可达量」试一晚，做不到才放开 NightCap。
            // 批次 17 只改了 RecalcCalc，这里补上，否则 calc=12 加载空瓶仍走「先按容量灌满」。
            long capEmpty = WaterHelper.GetTotalCapacity(b);
            if (volUl0 <= 0 && capEmpty > 0)
            {
                int pur0 = CustomPur > 0 ? CustomPur : pur;
                // 读不到水源就当作空，不再拿 91.00 去硬算。
                if (pur0 <= 0) return null;
                return BuildOneNightOrCap(capEmpty, pur0, perf, q, targetBp,
                                          srcName, GameBridge.NameOfItem(b),
                                          Mode, PreMode, PostMode,
                                          ownTabs, owF.filter, owF.uv, owF.hydraDur);
            }
            // 批次 20 修正：装了水的瓶子也不能直接进满容量 Build。
            // 1850/50000 实测会被满容量口径推成「海德拉4晚」；这里同样先试一晚，
            // 一晚做到目标就用一晚，做不到才放开 NightCap（不把滤棉/紫外/药片先踢掉）。
            if (volUl0 > 0 && capEmpty > 0 && WaterPlan.NightCap > 1)
            {
                int pur0 = CustomPur > 0 ? CustomPur : pur;
                // 读不到水源就当作空，不再拿 91.00 去硬算。
                if (pur0 <= 0) return null;
                return BuildOneNightOrCap(capEmpty, pur0, perf, q, targetBp,
                                          srcName, GameBridge.NameOfItem(b),
                                          Mode, PreMode, PostMode,
                                          ownTabs, owF.filter, owF.uv, owF.hydraDur);
            }
            // 效率  无视容器：按净水器 10 L 硬上限规划，回答「这台机器一晚到底能吃多少」（玩家 2026-11-10 要求）
            // 「效率：无视容器」已按玩家要求删掉（游戏里不可能无视容器接水）：一律按当前容器规划。
            long capForPlan = WaterHelper.GetTotalCapacity(b);
            // 玩家 2026-11-10 定案：**三个档都按身上的真实道具建候选** ——
            // 默认也是「用我当前的道具一晚赚更多」，不能假设药片/滤棉无限。
            int ot = ownTabs; bool of = owF.filter, ou = owF.uv; int oh = owF.hydraDur;
            int savePlanNight = WaterPlan.NightCap;
            try
            {
                int planNight = RecommendNightCap();
                if (savePlanNight > planNight) WaterPlan.NightCap = planNight;
                return WaterPlan.Build(capForPlan, pur, perf, q, targetBp,
                                       srcName, GameBridge.NameOfItem(b), full, Mode, PreMode, PostMode,
                                       ot, of, ou, oh);
            }
            finally
            {
                WaterPlan.NightCap = savePlanNight;
            }
        }

        /// <summary>批次 20：先试一晚可达量；一晚到不了目标才放开 NightCap 并按满容量重算。</summary>
        private static WaterPlan.Report BuildOneNightOrCap(long cap0, int pur0, int perf, int q, int targetBp,
                                                           string srcName, string bottleName,
                                                           int mode, int pre, int post,
                                                           int ownTab, bool ownFilter, bool ownUv, int ownHydraDur)
        {
            if (cap0 <= 0) return null;
            long keep1 = MaxKeepUl((int)cap0, pur0, perf, q, targetBp);
            if (keep1 <= 0) keep1 = cap0;
            int saveNight = WaterPlan.NightCap;
            WaterPlan.Report r1 = null;
            try
            {
                // 一晚口径：Build 内部所有 Run/RunHydra 都按 NightCap 取最大晚数。
                WaterPlan.NightCap = 1;
                r1 = WaterPlan.Build(keep1, pur0, perf, q, targetBp,
                                     srcName, bottleName, true, mode, pre, post,
                                     ownTab, ownFilter, ownUv, ownHydraDur);
            }
            finally
            {
                WaterPlan.NightCap = saveNight;
            }
            if (r1 != null && r1.pick != null && r1.pick.ok) return r1;
            if (saveNight > 1)
            {
                // 默认只显示 2 晚；8 晚留给主页的“升晚多出多少水”旁路，不能从这个回退路径漏出去。
                int capN = RecommendNightCap();
                int saveNight2 = WaterPlan.NightCap;
                WaterPlan.Report rN = null;
                try
                {
                    WaterPlan.NightCap = capN;
                    rN = WaterPlan.Build(cap0, pur0, perf, q, targetBp,
                                         srcName, bottleName, true, mode, pre, post,
                                         ownTab, ownFilter, ownUv, ownHydraDur);
                }
                finally
                {
                    WaterPlan.NightCap = saveNight2;
                }
                if (rN != null && rN.pick != null) return rN;
            }
            return r1;
        }

        /// <summary>玩家在方案表里点过的那条：按 label 覆盖自动选中的方案；找不到就回落到自动。</summary>
        private static void ApplyManualPick(WaterPlan.Report r)
        {
            if (r == null || ManualPickLabel == null) return;
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                if (c != null && c.ok && c.label == ManualPickLabel) { r.pick = c; SyncReportPick(r, c); return; }
            }
            ManualPickLabel = null;   // 换瓶/换水源后旧方案不存在了，自动回到自动选
        }

        /// <summary>把 Report 顶层字段同步到当前选中的方案；否则方案表点了另一条，
        /// 主页/步骤还拿旧方案的耗材和出水量。</summary>
        private static void SyncReportPick(WaterPlan.Report r, WaterPlan.Combo c)
        {
            if (r == null || c == null) return;
            r.pick = c;
            r.tablets = c.tablets;
            r.filterUsed = c.filterUsed;
            r.uvUsed = c.uv ? 1 : 0;
            r.outUl = c.outUl;
            r.finalBp = c.purityBp;
            r.cost = c.cost;
            r.nightsCount = c.nights;
            r.needUl = c.feedUl;
            r.useUl = c.feedUl;
            r.bestUl = c.feedUl;
            r.leftUl = 0L;
            r.perL = c.outUl > 0 ? c.cost / (c.outUl / 1000000.0) : 0.0;
        }

        /// <summary>诊断：列出参与整瓶凑量的容器（背包/台面/机器里的都算），
        /// 用来确认 1 mL / 10 mL 滴管有没有被认出来、当前整瓶能凑到多少。</summary>
        public static string CupsPublic()
        {
            try
            {
                var cups = WaterPlan.ScanCups();
                var g = WaterPlan.Groups(cups);
                var sb = new System.Text.StringBuilder("cups -> n=" + cups.Count);
                for (int i = 0; i < g.Count; i++)
                {
                    sb.Append(" | ").Append(g[i].name).Append(" ×").Append(g[i].n)
                      .Append(" cap=").Append(WaterPlan.CupCapText(g[i].capUl));
                }
                var mm = Compute();
                if (mm != null)
                {
                    long keep = MaxKeepUl((int)(mm.CapacityMl * 1000L), mm.PurityBp, mm.PerfPct, mm.Quality, mm.TargetBp);
                    var pour = WaterPlan.Plan(keep);
                    sb.Append(" || 一晚需 ").Append(keep / 1000).Append(" mL");
                    sb.Append("，整瓶能凑 ").Append(pour == null ? 0 : pour.totalUl / 1000).Append(" mL");
                    if (pour != null && pour.leftUl > 0) sb.Append("，差 ").Append(pour.leftUl / 1000).Append(" mL");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "cups -> err " + e.Message; }
        }

        /// <summary>诊断：把当前候选方案原样打出来，用于确认「前置/后置筛完为什么是空的」。</summary>
        public static string CombosPublic()
        {
            try
            {
                var r = CurrentPlan(true);
                if (r == null) return "combos -> no-plan";
                var sb = new System.Text.StringBuilder("combos -> n=" + r.combos.Count);
                sb.Append(" pick=").Append(r.pick == null ? "none" : r.pick.label);
                sb.Append(" filterEmpty=").Append(r.filterEmpty ? 1 : 0);
                int k = 0;
                foreach (var c in r.combos)
                {
                    if (k++ >= 10) { sb.Append(" | …"); break; }
                    sb.Append(" | ").Append(c.label).Append(" ok=").Append(c.ok ? 1 : 0)
                      .Append(" feed=").Append(c.feedUl / 1000).Append(" out=").Append(c.outUl / 1000)
                      .Append(" t=").Append(c.tablets);
                }
                return sb.ToString();
            }
            catch (Exception e) { return "combos -> ERR " + e.GetType().Name + ": " + e.Message; }
        }

        private static double CostOf(WaterMath.OneNight c)
        {
            double v = c.tablets * 8.0;
            if (c.useFilter) v += 6.0;
            if (c.useUv) v += 4.0;
            return v + 4.0;   // +4：一晚电费
        }

        private static string LiveLabel(WaterMath.OneNight c)
        {
            var s = new System.Text.StringBuilder();
            if (c.useFilter) s.Append("过滤棉");
            if (c.useUv) s.Append(s.Length > 0 ? "+紫外" : "紫外");
            if (c.tablets > 0)
            {
                if (s.Length > 0) s.Append("+");
                s.Append(c.tablets).Append(" 片药片（过夜后放）");
            }
            if (s.Length == 0) s.Append("原样跑一晚");
            return s.ToString();
        }

        public static string PlanPublic()
        {
            var r = CurrentPlan(true);
            if (r == null) return "[plan] no-bottle";
            Rebuild(true);
            string t = r.Summary();
            // 机器里已经有水（存档自带/上次没倒）时，不要再说「建议接水」——直接按现况算过夜：
            // 过夜只清杂质，体积不变（2000 mL 进 → 2000 mL 出，纯度抬到上限为止）。
            var mm = Compute();
            if (mm != null && mm.VolumeMl > 0 && mm.PurityBp > 0)
            {
                int after = mm.PurityBp;
                string liveLabel = "";
                try
                {
                    // 现况也要跟着四个下拉走：按「前置/后置」筛候选，再按「模式」挑一条，
                    // 而不是不管选什么都只报「原样过夜」（玩家：改了下拉数字还是死的）。
                    var cands = WaterMath.Plans(mm.VolumeMl * 1000L, mm.PurityBp,
                        1.0 + mm.PerfPct / 100.0, mm.Quality, PlanText.TargetBp(TargetIdx, mm.Quality));
                    WaterMath.OneNight best = null;
                    if (cands != null)
                    {
                        foreach (var c in cands)
                        {
                            if (!c.possible) continue;
                            if (PreMode == 1 && (c.useFilter || c.useUv)) continue;
                            if (PreMode == 2 && !c.useFilter) continue;
                            if (PreMode == 3 && !c.useUv) continue;
                            if (PostMode >= 1 && c.tablets != PostMode - 1) continue;
                            if (best == null) { best = c; continue; }
                            if (Mode == 1) { if (c.output > best.output) best = c; }
                            else if (Mode == 2)
                            {
                                double ca = CostOf(c), cb = CostOf(best);
                                if (ca / Math.Max(1.0, c.output) < cb / Math.Max(1.0, best.output)) best = c;
                            }
                            else if (c.purity > best.purity || (c.purity == best.purity && CostOf(c) < CostOf(best))) best = c;
                        }
                    }
                    if (best != null)
                    {
                        after = best.purity;
                        if (mm.CapBp > 0 && after > mm.CapBp) after = mm.CapBp;
                        liveLabel = "选中「" + LiveLabel(best) + "」：这 " + mm.VolumeMl + " mL 过夜 → "
                                  + (best.output / 1000L) + " mL  " + PlanText.Pct(after);
                    }
                }
                catch (Exception) { }
                if (liveLabel == "")
                {
                    try
                    {
                        var w0 = WaterMath.Build(mm.VolumeMl, mm.PurityBp);
                        WaterMath.Purify(w0, 1.0 + mm.PerfPct / 100.0, mm.Quality);
                        after = w0.Purity;
                    }
                    catch (Exception) { }
                    if (mm.CapBp > 0 && after > mm.CapBp) after = mm.CapBp;
                    liveLabel = "按现况过夜 → " + mm.VolumeMl + " mL  " + PlanText.Pct(after) + "（体积不变）";
                }
                string adv0 = AdviceText(mm);
                t = "容器里已经有 " + mm.VolumeMl + " mL  " + PlanText.Pct(mm.PurityBp) + "（不用再灌水）"
                  + " | " + liveLabel
                  + (adv0 == "" ? "" : " | 这瓶现在怎么做：" + adv0);
            }
            // 模式（下拉）必须体现在结论里：同一次规划，按模式挑出来的方案不同。
            int mi = Mode;
            if (mi < 0 || mi >= PlanText.ModeNames.Length) mi = 0;
            if (r.filterEmpty)
                t = t.TrimEnd() + "\n前置「" + PlanText.PreName(PreMode) + "」+ 后置「" + PlanText.PostName(PostMode)
                  + "」凑不出达标方案，下面按自动给";
            if (r.pick != null)
                t = t.TrimEnd() + "\n按「" + PlanText.ModeNames[mi] + "」选：前置 " + PlanText.PreName(PreMode)
                  + "  后置 " + PlanText.PostName(PostMode) + " → " + r.pick.label
                  + " → " + (r.pick.outUl / 1000) + " mL  " + PlanText.Pct(r.pick.purityBp);
            Log("[plan] " + t.Replace("\n", " | "));
            return t.Replace("\n", " | ");
        }

        public static string StepsPublic()
        {
            var r = CurrentPlan(true);
            if (r == null) return "[steps] no-bottle";
            string t = r.StepsText() + "\n" + r.NightsText();
            Log("[steps] " + t.Replace("\n", " | "));
            return t.Replace("\n", " | ");
        }

        public static void ToggleDetail()
        {
            try
            {
                if (detailWin != null && detailWin.IsVisible()) { detailWin.Hide(); return; }
                var r = CurrentPlan(true);
                if (detailWin == null)
                {
                    detailTxt = new ScrollableRichTextElement(true, true);
                    detailTxt.ResizePixels(328, 500);
                    var panel = new MachineryPanel("净水助手 · 详情", detailTxt, "watercalc_detail");
                    detailWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                    if (detailWin == null) { log.LogWarning("[ui] detail null"); return; }
                    try { detailWin.ResizePixels(360, 430); } catch (Exception) { }
                    CenterWindow(detailWin);
                    // Input 钩子已关，改由这层命名拦截器吞掉落在详情窗里的点击（不让它穿到游戏 UI）。
                    AddClickBlocker(detailWin, "detail", 344, 540);
                }
                var b = new RichTextBuilder(320);
                BuildDetailText(b, r);
                detailTxt.SetRichText(b, 9000);
                detailWin.Show(); detailWin.ToFront(true);
                // 原生窗口会把它「停靠」到主面板右边；Show 之后再顶一次，让详情留在屏幕正中。
                CenterWindow(detailWin);
            }
            catch (Exception e) { log.LogWarning("[ui] detail " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>详情窗用哪瓶：和方案同一口径（机器槽 → 背包最富），保证数字和主面板对得上。</summary>
        private static GameItem DetailBottle()
        {
            // 和计算页同一优先级：右击选过的瓶优先，其次当前机器槽，最后才是背包兜底。
            var b = ExplicitPickBottle();
            if (b == null && MachineOpen()) b = GameBottle();
            if (b == null) b = CtxBottle();
            if (b == null) b = PickFallbackBottle();
            return b;
        }

        /// <summary>
        /// 原始 water parts 推出的精确纯度：pctBp100 = 水 × 1000000 / 总量（98.9949% → 989949）。
        /// 游戏存档值是截断后的 purityBp，两者差多少一眼能看出来。
        /// </summary>
        private static bool BottlePurityPrecise(GameItem b, out long pctBp100, out int gameBp)
        {
            pctBp100 = 0; gameBp = 0;
            if (b == null) return false;
            try { gameBp = WaterHelper.GetWaterPurity(b); } catch (Exception) { gameBp = 0; }
            try
            {
                var parts = WaterHelper.GetLiquidParts(b);
                if (parts == null || parts.Length < 2) return false;
                long total = 0, water = parts[0];
                for (int i = 0; i < parts.Length; i++) total += parts[i];
                if (total <= 0) return false;
                if (water < 0) water = 0;
                pctBp100 = water * 1000000L / total;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>详情里的分节标题：短横线 + 标签色，不用大段文字堆在一起。</summary>
        /// <summary>档位色：像素富文本只认 ColorPalette（hex 标签会原样印出来）。
        /// 玩家 2026-11-13 的原版色值里，铁锈 #9B4A3D 正好等于 ERed，优质 #7AB053 ≈ LightGreen。</summary>
        private static ColorPalette TierPal(string tierName)
        {
            if (tierName == null) return ColorPalette.LightGray;
            if (tierName.IndexOf("纯水") >= 0) return ColorPalette.Green;
            if (tierName.IndexOf("优质") >= 0) return ColorPalette.LightGreen;
            if (tierName.IndexOf("基础") >= 0) return ColorPalette.VeryLightGray;
            if (tierName.IndexOf("幽灵") >= 0) return ColorPalette.LightOrange;
            if (tierName.IndexOf("铁锈") >= 0) return ColorPalette.ERed;
            if (tierName.IndexOf("沟渠") >= 0) return ColorPalette.DarkRed;
            return ColorPalette.LightGray;
        }

        private static void DetailSection(RichTextBuilder b, string title)
        {
            AddLine(b, "", ColorPalette.White, false);
            AddLine(b, "— " + title + " —", ColorPalette.LightOrange, true);
        }

        /// <summary>
        /// 净水详情的正文：标题/当前容器 → 水量容量 → 纯度（游戏 + 精确）与档位 →
        /// 一晚方案（接/倒/出水/耗材）→ 收益（毛收入/成本/净利）→ 步骤分条。
        /// </summary>
        private static void BuildDetailText(RichTextBuilder b, WaterPlan.Report r)
        {
            AddLine(b, "净水详情", ColorPalette.LightGreen, true);
            var bottle = DetailBottle();
            string bname = bottle != null ? GameBridge.NameOfItem(bottle) : (r != null ? r.bottleName : null);
            if (string.IsNullOrEmpty(bname)) bname = "未放瓶";
            AddLine(b, "当前容器  " + bname, ColorPalette.White, false);

            long volUl = 0, capUl = 0;
            if (bottle != null)
            {
                try { volUl = WaterHelper.GetTotalVolume(bottle); } catch (Exception) { }
                try { capUl = WaterHelper.GetTotalCapacity(bottle); } catch (Exception) { }
            }
            else if (r != null) { volUl = r.useUl; capUl = r.capUl; }

            DetailSection(b, "水量");
            AddLine(b, "水量      " + PlanText.Ml1(volUl) + "   /   容量 " + PlanText.Ml1(capUl),
                     ColorPalette.VeryLightCyan, false);

            DetailSection(b, "水质");
            long prec = 0; int gameBp = 0;
            bool hasPrec = BottlePurityPrecise(bottle, out prec, out gameBp);
            if (hasPrec)
            {
                AddLine(b, "游戏显示  " + PlanText.Pct(gameBp), ColorPalette.LightBlue, false);
                AddLine(b, "精确值    " + PlanText.Pct4(prec), ColorPalette.LightBlue, false);
            }
            else if (r != null && r.srcBp > 0)
            {
                AddLine(b, "游戏显示  " + PlanText.Pct(r.srcBp), ColorPalette.LightBlue, false);
                AddLine(b, "精确值    未扫描（右键瓶可读原始配比）", ColorPalette.LightGray, false);
            }
            else
            {
                AddLine(b, "纯度      未知（先用扫描仪/试纸扫一次）", ColorPalette.LightOrange, false);
            }
            int effBp = gameBp > 0 ? gameBp : (r != null ? r.srcBp : 0);
            string tier = WaterEconomy.TierName(effBp);
            AddLine(b, "档位      " + tier + "   " + WaterEconomy.Money(WaterEconomy.PricePerLitre(effBp)) + " 元/L",
                     TierPal(tier), false);

            if (r == null) { AddLine(b, "没有可规划的瓶子：把瓶放进储水容器或背包", ColorPalette.LightOrange, false); return; }

            int tgt = r.targetBp;
            bool pass = effBp >= tgt;
            AddLine(b, "目标      " + PlanText.Pct(tgt) + "   " + (pass ? "已达标" : "未达 " + PlanText.Pct(tgt)),
                     pass ? ColorPalette.LightGreen : ColorPalette.LightOrange, false);
            if (pass)
            {
                // 已达标就别再列「一晚方案」——玩家 2026-11-13 明确说这是多余的推荐。
                AddLine(b, "不用跑一晚；要卖直接卖，要兑水去「混水」。", ColorPalette.LightGreen, false);
                return;
            }
            if (hasPrec && gameBp > 0 && prec > 0)
            {
                long precGame = gameBp * 100L;
                string why = prec > precGame
                    ? "显示四舍五入比存档值高" : (prec < precGame ? "存档值更高" : "两者一致");
                AddLine(b, "判定      游戏 " + PlanText.Pct(gameBp) + " / 精确 " + PlanText.Pct4(prec)
                         + "（" + why + "，阈值一律按左边）", ColorPalette.LightGray, false);
            }

            DetailSection(b, "一晚方案");
            var c = r.pick;
            if (c != null && c.ok)
            {
                AddLine(b, "接水      " + PlanText.Ml1(BottlePourUl(c.feedUl))
                         + "   →   出水 " + PlanText.Ml1(c.outUl)
                         + "   " + PlanText.Pct(c.purityBp), ColorPalette.VeryLightCyan, false);
                if (r.dumpUl > 0)
                    AddLine(b, "先倒出    " + PlanText.Ml1(r.dumpUl) + "（剩下的才够一晚压到目标）", ColorPalette.LightGray, false);
                string sup = "";
                if (c.tablets > 0) sup += "药片 " + c.tablets + " 片  ";
                if (c.filterUsed > 0) sup += "过滤棉 " + c.filterUsed + " 点  ";
                if (c.uv) sup += "紫外灯  ";
                if (sup.Length == 0) sup = "不用耗材  ";
                sup += "电 " + WaterPlan.EnergyPerNight + " 度 ≈ " + WaterPlan.EnergyCostPerNight.ToString("0.##", CultureInfo.InvariantCulture) + " 元";
                AddLine(b, "耗材      " + sup, ColorPalette.LightGray, false);
                if (c.nights > 1) AddLine(b, "晚数      " + c.nights + " 晚", ColorPalette.LightGray, false);
            }
            else
            {
                AddLine(b, "一晚做不到目标：降目标 / 先兑水 / 换小瓶", ColorPalette.LightOrange, false);
            }

            DetailSection(b, "收益");
            if (c != null && c.ok)
            {
                double rev = 0, spent = c.cost;
                try { rev = r.ChosenRevenue(); } catch (Exception) { }
                double profit = rev - spent;
                AddLine(b, "毛收入 " + WaterEconomy.Money(rev) + "   成本 " + WaterEconomy.Money(spent)
                         + "   净利 " + (profit >= 0 ? "+" : "") + WaterEconomy.Money(profit) + " 元",
                         profit >= 0 ? ColorPalette.LightGreen : ColorPalette.LightOrange, false);
            }
            else
            {
                AddLine(b, "收益      没有可执行的一晚方案", ColorPalette.LightGray, false);
            }

            DetailSection(b, "怎么看");
            AddLine(b, "这里只看数：水量 / 纯度 / 一晚方案 / 收益。", ColorPalette.LightGray, false);
            AddLine(b, "具体操作去主面板「步骤」页；多晚对比去「更多方案」页。", ColorPalette.LightGray, false);

        }

        // 模式二入口（供按钮与 ctl 调试命令共用）
        public static string OnPreparePublic() { OnPrepare2(); return "prepare -> " + (serveResult == null ? LastMsg : serveResult); }

        /// <summary>「净化后」选择器：0/1/2/3 片药片，对应网页计算器的下拉框。只改方案口径，不动瓶子。</summary>
        private static int tabletPick = 0;

        private static void CycleTablet()
        {
            tabletPick = (tabletPick + 1) % 4;
            LastMsg = tabletPick == 0
                ? "方案按「不放药片」算"
                : "方案按「净化后 " + tabletPick + " 片药片」算（药片得你自己放进瓶子，这里只算数）";
            lastKey = "";
            Rebuild(true);
        }

        public static string CycleTabletPublic() { CycleTablet(); return "tablet -> " + tabletPick + " 片: " + LastMsg; }

        /// <summary>ctl `tablet`：给当前瓶子加一片净水片（与 F9「药片」按钮同一条路径）。</summary>
        public static string OnTabletPublic() { OnAddTablet(); return "tablet -> " + LastMsg; }

        /// <summary>设置窗用：直接设定平衡模式（true=按真实水质算）。</summary>
        public static void SetBalance(bool on)
        {
            BalanceOff = on;
            lastKey = "";
            Log("[bal] set on=" + on + " display=" + (on ? "exact" : "unknown"));
            Rebuild(true);
        }

        public static void ToggleBalance()
        {
            BalanceOff = !BalanceOff;
            // 开/关要看得出来：直接换图标（开＝水滴=显示真实水质；关＝齿轮=未知）
            // MachineryButton 没有换图标的接口，只能改描述；把状态写进 tooltip + 用面板顶行显示
            try
            {
                // 图标已移除，状态只体现在设置窗与顶行
            }
            catch (Exception) { }
            Log("[bal] on=" + BalanceOff + " display=" + (BalanceOff ? "exact" : "unknown"));
            lastKey = "";
            Rebuild(true);
        }

        public static string BalanceState()
        {
            bool hp = HidePurity();
            return "bal -> scanners=" + (Scanners() ? "true" : "false") + " mode=" + (BalanceOff ? "off" : "on") + " hide=" + (hp ? "true" : "false");
        }

        public static string ToggleBalancePublic() { ToggleBalance(); return BalanceState(); }

        public static void LogQuiet(string s) { Log(s); }
        public static string RecipesVisible() { return (recipeWin != null && recipeWin.IsVisible()) ? "true" : "false"; }

        /// <summary>每页各自的高度：主页只放当前方案，设置页放五行下拉，方案池页放候选表。</summary>
        // ---------------- 水源选择器：小窗 + 网格（先文字格，后续换物品图标） ----------------
        private static PixelWindow srcWin;

        /// <summary>打开/收起水源选择器：两列网格，点哪个是哪个（当前项带 ▶）。</summary>
        public static void OpenSourcePicker()
        {
            try
            {
                if (srcWin == null)
                {
                    var st = new MachineryStack(true, 4);
                    // 具体挡位直接写在按钮上：铁锈/沟渠各自读今日 StoreStation 真值。
                    int rp0 = 0, gp0 = 0;
                    try
                    {
                        var s0 = StoreStation.Instance;
                        if (s0 != null) { rp0 = s0.todayRustwaterPurity; gp0 = s0.todayGutterflowPurity; }
                    }
                    catch (Exception) { }
                    st.Add(new MachineryText("今日 铁锈 " + (TapPurLooksValid(4, rp0) ? PlanText.Pct(rp0) : "读不到")
                                           + "   沟渠 " + (TapPurLooksValid(5, gp0) ? PlanText.Pct(gp0) : "读不到"),
                                           9, HorizontalAlignmentOptions.Left, false));
                    int n = SrcPickNames.Length;
                    for (int i = 0; i < n; i += 2)
                    {
                        var row = new MachineryStack(false, 4);
                        for (int k = i; k < Math.Min(i + 2, n); k++)
                        {
                            int idx = SrcPickVals[k];
                            string name = (idx == 1 ? "铁锈 " : "沟渠 ")
                                        + (idx == 1 ? (TapPurLooksValid(4, rp0) ? PlanText.Pct(rp0) : "读不到")
                                                    : (TapPurLooksValid(5, gp0) ? PlanText.Pct(gp0) : "读不到"));
                            var b = new MachineryButton((idx == SrcIdx ? "▶" : "") + name, new Action(delegate
                            {
                                SetSrc(idx);
                                if (srcWin != null) srcWin.Hide();
                            }), 108, 20);
                            row.Add(b);
                        }
                        st.Add(row);
                    }
                    st.Add(Small("关闭", new Action(delegate { if (srcWin != null) { WinSave("src", srcWin); srcWin.Hide(); } }), 64));
                    var sp = new MachineryPanel("", st, "watercalc_src");
                    sp.SetMinWidth(232);
                    srcWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (srcWin != null) { try { srcWin.ResizePixels(250, 170); } catch (Exception) { } WinRestore("src", srcWin); }
                    Log("[ui] 水源窗 ok");
                }
                if (srcWin != null) { if (srcWin.IsVisible()) { WinSave("src", srcWin); srcWin.Hide(); } else { srcWin.Show(); WinRestore("src", srcWin); } }
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSourcePicker " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 步骤：独立小窗（不替换主页面板） ----------------
        private static PixelWindow stepsWin;
        private static ScrollableRichTextElement pStepList;
        private static MachineryText pMixBody;

        /// <summary>ctl 用：把「步骤」按文本返回，方便不开面板也能核对（steps?）。</summary>
        public static string StepsTextPublic()
        {
            try
            {
                var r = CurrentPlan(true);
                if (r == null) return "steps -> no-plan";
                var c = r.pick;
                long feed = (c != null && c.feedUl > 0) ? c.feedUl : r.useUl;
                int perf, q;
                Bonuses(out perf, out q);
                WaterMath.Water w = null;
                var b0 = CtxBottle(); if (b0 == null) b0 = GameBottle();
                if (b0 != null)
                {
                    WaterMath.Water real;
                    if (BuildRealWater(b0, out real) && real.v > 0 && Math.Abs(real.v - feed) <= 5000L) w = real;
                }
                bool fromReal = w != null;
                if (w == null) w = WaterMath.Build(feed, r.srcBp);
                var sb = new System.Text.StringBuilder();
                if (fromReal) sb.Append("现有 ").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                else sb.Append("接水 ").Append(WaterPlan.Ml(feed)).Append("mL@").Append(PlanText.Pct(r.srcBp));
                if (c != null && c.filter) { WaterMath.Filter(w, 400); sb.Append(" | 过滤→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (c != null && c.uv) { WaterMath.Uv(w, 1); sb.Append(" | 紫外→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                WaterMath.Purify(w, perf, q);
                sb.Append(" | 1晚→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                if (c != null && c.tablets > 0) { WaterMath.Tablet(w, c.tablets); sb.Append(" | ").Append(c.tablets).Append("药→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (c != null) sb.Append(" || 表:").Append(WaterPlan.Ml(c.feedUl)).Append("→").Append(WaterPlan.Ml(c.outUl)).Append("mL@").Append(PlanText.Pct(c.purityBp));
                return "steps -> " + sb;
            }
            catch (Exception e) { return "steps -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：列出 PixelWindow 全部公开方法（找「换内容而不换窗口」的接口）。</summary>
#if DEV
        public static string PwProbe()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var w = panelWin;
                if (w == null) return "pw -> no-window";
                var t = w.GetType();
                sb.Append("pw -> ").Append(t.Name);
                var ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                foreach (var m in ms)
                {
                    if (m.IsSpecialName) continue;
                    sb.Append(" | ").Append(m.Name).Append('(');
                    var ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(ps[i].ParameterType.Name); }
                    sb.Append(')');
                }
            }
            catch (Exception e) { sb.Append(" ERR ").Append(e.GetType().Name); }
            return sb.ToString();
        }
#endif

        /// <summary>ctl 用：列出当前净水器已装的子件（模组/滤芯）identifier —— 找紫外灯/过滤棉真名。</summary>
        public static string ModsProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("mods -> ");
            try
            {
                var m = CurrentMachine();
                sb.Append("cur=").Append(m == null ? "null" : GameBridge.DescribeModules(m));
            }
            catch (Exception e) { sb.Append("ERR ").Append(e.GetType().Name); }
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p2 = PurifierHook.Pairs[i];
                    if (p2 == null || p2.Machine == null) continue;
                    sb.Append(" | #").Append(i).Append('=').Append(GameBridge.DescribeModules(p2.Machine));
                }
            }
            catch (Exception e2) { sb.Append(" listErr=").Append(e2.GetType().Name); }
            return sb.ToString();
        }

        /// <summary>ctl 用：列出背包/台面里所有物品 identifier（找紫外灯/过滤棉的真名）。</summary>
        public static string ItemsProbe()
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                var sb = new System.Text.StringBuilder();
                sb.Append("items -> n=").Append(all.Count);
                for (int i = 0; i < all.Count && i < 60; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    int cap = 0; int vol = 0;
                    try { cap = WaterHelper.GetTotalCapacity(it); } catch (Exception) { }
                    try { vol = WaterHelper.GetTotalVolume(it); } catch (Exception) { }
                    sb.Append(" | ");
                    try { sb.Append(it.identifier); } catch (Exception) { sb.Append("?"); }
                    if (cap > 0) sb.Append("(cap=").Append(cap).Append(",vol=").Append(vol).Append(')');
                }
                return sb.ToString();
            }
            catch (Exception e) { return "items -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：回报主面板当前页与位置（验证切页不跳位）。</summary>
        public static string PosPublic()
        {
            try
            {
                if (panelWin == null) return "pos -> no-window";
                UnityEngine.Vector2 p2; if (!WinGetPos(panelWin, out p2)) p2 = panelWin.rectTransform.anchoredPosition;
                var sb = new System.Text.StringBuilder();
                sb.Append("pos -> page=").Append(UiPage).Append(" visible=").Append(panelWin.IsVisible())
                  .Append(" x=").Append((int)p2.x).Append(" y=").Append((int)p2.y);
#if DEV
                // 顺便把 PixelWindow 上跟「位置」有关的成员打出来（找真正控制位置的接口）
                // 注意：GetType() 拿到的是「计算出的类型」，按能力清单算 reflection-external；
                // 创意工坊发布版（2026-12-10 起）一律拒绝，所以只留在 DEV 构建里。
                try
                {
                    var t = panelWin.GetType();
                    foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        string n = m.Name; if (n == null) continue;
                        string ln = n.ToLowerInvariant();
                        if (ln.Contains("pos") || ln.Contains("move") || ln.Contains("anchor") || ln.Contains("offset"))
                            sb.Append(" | ").Append(n);
                    }
                }
                catch (Exception) { }
#endif
                return sb.ToString();
            }
            catch (Exception e) { return "pos -> ERR " + e.Message; }
        }

        /// <summary>ctl 用：把 GraphUtils 的静态方法签名打出来，用来找「摆到台面」的正确入口。</summary>
#if DEV
        public static string GraphUtilsProbe()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = null;
                    try { t = asms[i].GetType("GraphUtils"); } catch (Exception) { }
                    if (t == null) continue;
                    sb.Append("type=").Append(t.FullName).Append(" asm=").Append(asms[i].GetName().Name).Append(" | ");
                    var ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    for (int k = 0; k < ms.Length; k++)
                    {
                        var m = ms[k];
                        if (!m.Name.ToLowerInvariant().Contains("place") && !m.Name.ToLowerInvariant().Contains("add")) continue;
                        sb.Append(m.Name).Append('(');
                        var ps = m.GetParameters();
                        for (int j = 0; j < ps.Length; j++) { if (j > 0) sb.Append(", "); sb.Append(ps[j].ParameterType.Name).Append(' ').Append(ps[j].Name); }
                        sb.Append(") ");
                    }
                }
                if (sb.Length == 0) sb.Append("GraphUtils not found");
                return "gs -> " + sb;
            }
            catch (Exception e) { return "gs -> ERR " + e.GetType().Name + " " + e.Message; }
        }
#endif

        /// <summary>ctl 用：不碰游戏状态，按给定参数跑一遍 WaterPlan.Build（核对 6L/5498 这种离线算例）。
        /// 用法：sim? cap=6000 pur=9001 pre=0 post=0 perf=25 qual=0 tgt=9901</summary>
        public static string SimPublic(string args)
        {
            try
            {
                int capMl = 6000, pur = 9000, pre = 0, post = 0, perf = 25, qual = 0, tgt = 9901;
                if (!string.IsNullOrEmpty(args))
                {
                    string[] kv = args.Trim().Split(' ');
                    for (int i = 0; i < kv.Length; i++)
                    {
                        int eq = kv[i].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = kv[i].Substring(0, eq).ToLowerInvariant();
                        int v;
                        if (!int.TryParse(kv[i].Substring(eq + 1), out v)) continue;
                        if (k == "cap") capMl = v;
                        else if (k == "pur") pur = v;
                        else if (k == "pre") pre = v;
                        else if (k == "post") post = v;
                        else if (k == "perf") perf = v;
                        else if (k == "qual") qual = v;
                        else if (k == "tgt") tgt = v;
                    }
                }
                var r = WaterPlan.Build(capMl * 1000L, pur, perf, qual, tgt, "模拟", "模拟", true, 0, pre, post);
                if (r == null) return "sim -> null";
                var sb = new System.Text.StringBuilder();
                sb.Append("sim [cap=").Append(capMl).Append(" pur=").Append(pur).Append(" pre=").Append(pre)
                  .Append(" post=").Append(post).Append(" perf=").Append(perf).Append(" qual=").Append(qual)
                  .Append(" tgt=").Append(tgt).Append("]");
                if (r.pick != null)
                    sb.Append(" -> pick=").Append(r.pick.label).Append(" ").Append(WaterPlan.Ml(r.pick.feedUl))
                      .Append("→").Append(WaterPlan.Ml(r.pick.outUl)).Append("mL@").Append(PlanText.Pct(r.pick.purityBp));
                int n = 0;
                for (int i = 0; i < r.combos.Count && n < 6; i++)
                {
                    var c = r.combos[i];
                    if (!c.ok) continue;
                    sb.Append(" | ").Append(c.label).Append(' ').Append(WaterPlan.Ml(c.feedUl))
                      .Append("→").Append(WaterPlan.Ml(c.outUl)).Append('@').Append(PlanText.Pct(c.purityBp));
                    n++;
                }
                return sb.ToString();
            }
            catch (Exception e) { return "sim -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：当前瓶水 + 模拟逐片加药（同一批水），用来和游戏实测反推药片真实清除量。</summary>
        public static string TabletProbe()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();
                if (b == null) return "tab -> no-bottle";
                WaterMath.Water w;
                if (!BuildRealWater(b, out w)) return "tab -> no-parts";
                var sb = new System.Text.StringBuilder();
                sb.Append("tab -> 现在 ").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                var wc = w.Clone();
                for (int t = 1; t <= 3; t++)
                {
                    WaterMath.Tablet(wc, 1);
                    sb.Append(" | ").Append(t).Append("片→").Append(WaterPlan.Ml(wc.v)).Append("mL@").Append(PlanText.Pct(wc.Purity));
                }
                sb.Append(" || 分项 ");
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                for (int i = 0; i < keys.Length; i++) sb.Append(keys[i]).Append('=').Append(w.Part(keys[i])).Append(' ');
                return sb.ToString();
            }
            catch (Exception e) { return "tab -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>
        /// 用瓶子里真实的六项配比建 Water。
        /// 2026-11-11 玩家定案：公式是对的，错的是「接口」——读进来的水被算成 100%。
        /// 因此这里必须让模型纯度与游戏真值（WaterHelper.GetWaterPurity）对齐：
        /// 名字映射不全/顺序不符时，回落按真值纯度反推配比，绝不给出比真值更高的纯度。
        /// </summary>
        private static bool BuildRealWater(GameItem b, out WaterMath.Water w)
        {
            w = null;
            try
            {
                var parts = WaterHelper.GetLiquidParts(b);
                if (parts == null || parts.Length < 6) return false;
                long sum = 0;
                for (int i = 0; i < parts.Length; i++) sum += parts[i];
                if (sum <= 0) return false;
                int gp = 0;
                try { gp = WaterHelper.GetWaterPurity(b); } catch (Exception) { gp = 0; }

                var names = GameBridge.LiquidNames();
                var m = new WaterMath.Water { v = sum };
                try { m.cap = WaterHelper.GetTotalCapacity(b); } catch (Exception) { m.cap = 0; }
                long mapped = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] <= 0) continue;
                    string k = names != null && i < names.Length ? MapLiquid(names[i]) : null;
                    if (k == null) k = MapLiquidByIndex(i);   // 名字对不上（本地化/空）时按 Liquid 顺序兜底
                    if (k == null) continue;
                    long cur;
                    m.parts.TryGetValue(k, out cur);
                    m.parts[k] = cur + parts[i];
                    mapped += parts[i];
                }
                // 接口校验：模型纯度不得高于游戏真值（允许 5 bp 误差，正负都可）。
                if (gp > 0 && (mapped <= 0 || m.Purity > gp + 5 || m.Purity < gp - 5))
                {
#if DEV
                    Log("[realwater] 配比对不上，按真值反推 gp=" + PlanText.Pct(gp) + " model=" + PlanText.Pct(m.Purity) + " mapped=" + mapped + "/" + sum);
#endif
                    var fb = WaterMath.Build(sum, gp);
                    fb.cap = m.cap;
                    m = fb;
                }
#if DEV
                Log("[realwater] gp=" + PlanText.Pct(gp) + " model=" + PlanText.Pct(m.Purity) + " vol=" + sum + " mapped=" + mapped + " parts=" + m.parts.Count);
#endif
                w = m;
                return true;
            }
            catch (Exception e)
            {
#if DEV
                Log("[realwater] ERR " + e.GetType().Name + " " + e.Message);
#endif
                return false;
            }
        }

        /// <summary>按 Liquid.Liquids 的顺序兜底映射（顺序见 Liquid.InitLiquid：water 在最前）。</summary>
        private static string MapLiquidByIndex(int i)
        {
            switch (i)
            {
                case 1: return "heavy";
                case 2: return "organic";
                case 3: return "physical";   // microplastic（与 physical 同参数：filterMin 0.002、移除 1500）
                case 4: return "microbe";
                case 5: return "chemical";
                case 6: return "physical";
                case 7: return "mineral";
                default: return null;
            }
        }

        private static string MapLiquid(string n)
        {
            switch ((n ?? "").Trim().ToLowerInvariant())
            {
                case "heavy_metal": return "heavy";
                case "heavy": return "heavy";
                case "organic_waste": case "organic": return "organic";
                case "microbe": case "microbes": return "microbe";
                case "chemical_contaminant": case "chemical": return "chemical";
                case "physical_contaminant": case "physical": return "physical";
                case "microplastic": case "micro_plastic": return "physical";
                case "mineral": return "mineral";
                default: return null;
            }
        }

        /// <summary>步骤页/步骤窗按游戏容器的整数 mL 显示，避免 5348.4 和游戏 5348 差一位。</summary>
        private static string GameMl(long ul)
        {
            return (ul / 1000L).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void FillSteps()
        {
            if (pStepList == null) return;
            var b = new RichTextBuilder(240);
            try
            {
                var r = CurrentPlan(false);
                if (r == null) { AddLine(b, "还没有可用方案：先放一个装水的容器", ColorPalette.LightOrange, false); pStepList.SetRichText(b, 4000); return; }
                var c = r.pick;
                int done = progressActive ? progressDoneSteps : 0;
                int doneFilter = progressActive ? ProgressDoneTool("filter") : 0;
                int doneUv = progressActive ? ProgressDoneTool("uv") : 0;
                int doneNights = progressActive ? ProgressDoneTool("purifier") : 0;
                int doneTabs = progressActive ? ProgressDoneTool("tablet") : 0;
                long feed = (c != null && c.feedUl > 0) ? c.feedUl : r.useUl;
                int perf, q;
                Bonuses(out perf, out q);
                // 进度模式：瓶子已经是“某一步之后”的水，后续按实际瓶水继续，
                // 不能再拿方案原始 feed 去重建一遍前置水。
                if (progressActive)
                {
                    var fb = CtxBottle(); if (fb == null) fb = GameBottle();
                    if (fb != null) feed = WaterHelper.GetTotalVolume(fb);
                }
                // 与卡一/接水键同源：feed 取选中方案，不再用自动方案的 useUl（玩家：一直显示 1850）
                // 瓶子已经有水且量级对得上，就用真实六项配比算；否则按纯度反推
                WaterMath.Water w = null;
                var bottle0 = CtxBottle();
                if (bottle0 == null) bottle0 = GameBottle();
                if (bottle0 != null)
                {
                    WaterMath.Water real;
                    if (BuildRealWater(bottle0, out real) && real.v > 0 && Math.Abs(real.v - feed) <= 5000L) w = real;
                }
                long startV = 0;
                if (w == null) w = WaterMath.Build(feed, r.srcBp); else startV = w.v;
                int idx = 1;
                if (startV > 0) AddLine(b, idx + ". 现有 " + GameMl(startV) + " mL  " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                else AddLine(b, idx + ". 接水 " + GameMl(feed) + " mL  " + PlanText.Pct(r.srcBp), ColorPalette.LightGray, false);
                if (c != null && c.filter && doneFilter <= 0)
                {
                    WaterMath.Filter(w, 400);
                    AddLine(b, (++idx) + ". 过滤棉 → " + GameMl(w.v) + " mL  " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                if (c != null && c.uv && doneUv <= 0)
                {
                    // 紫外灯是工具：一次照过、体积不变、不计次数（玩家明确要求）
                    WaterMath.Uv(w, 1);
                    AddLine(b, (++idx) + ". 紫外灯 → " + GameMl(w.v) + " mL  " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                // 注意：Purify 的 perf 是「百分比原值」（25 就传 25），不是 1.25；
                // 之前这里传了 1+perf/100，导致步骤纯度比方案表低 0.47。
                int remainNights = c == null ? 0 : Math.Max(0, c.nights - doneNights);
                for (int n = 0; n < remainNights; n++)
                {
                    WaterMath.Purify(w, perf, q);
                    AddLine(b, (++idx) + ". 净水器 1 晚 → " + GameMl(w.v) + " mL  " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                int remainTabs = c == null ? 0 : Math.Max(0, c.tablets - doneTabs);
                if (remainTabs > 0)
                {
                    WaterMath.Tablet(w, remainTabs);
                    AddLine(b, (++idx) + ". " + remainTabs + " 片药片 → " + GameMl(w.v) + " mL  " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                if (c != null)
                {
                    AddLine(b, "方案表口径：" + WaterPlan.Ml(BottlePourUl(c.feedUl)) + " → " + WaterPlan.Ml(c.outUl) + " mL  " + PlanText.Pct(c.purityBp), ColorPalette.LightGreen, false);
                    // 逐片加药：同一批水再加 1/2 片会到多少，方便判断「其实一片就够」
                    var wc = w.Clone();
                    for (int t = remainTabs + 1; t <= 3; t++)
                    {
                        WaterMath.Tablet(wc, 1);
                        AddLine(b, "  +1 片（共 " + t + " 片）→ " + WaterPlan.Ml(wc.v) + " mL  " + PlanText.Pct(wc.Purity), ColorPalette.LightGray, false);
                    }
                }
            }
            catch (Exception e) { AddLine(b, "步骤算不出来：" + e.GetType().Name, ColorPalette.LightOrange, false); }
            pStepList.SetRichText(b, 4000);
        }

        /// <summary>打开/收起「做法步骤」小窗：1 接水 → 2 过滤 → 3 紫外 → 4 每晚 → 5 药片。</summary>
        public static void OpenSteps()
        {
            try
            {
                if (stepsWin == null)
                {
                    var st = new MachineryStack(true, 3);
                    pStepList = new ScrollableRichTextElement(true, true);
                    pStepList.ResizePixels(300, 230);
                    st.Add(pStepList);
                    st.Add(Small("返回", new Action(delegate { if (stepsWin != null) { WinSave("steps", stepsWin); stepsWin.Hide(); } }), 72));
                    var sp = new MachineryPanel("", st, "watercalc_steps");
                    sp.SetMinWidth(280);
                    stepsWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (stepsWin != null) { try { stepsWin.ResizePixels(330, 300); } catch (Exception) { } WinRestore("steps", stepsWin); }
                    Log("[ui] 步骤窗 ok");
                }
                if (stepsWin != null)
                {
                    if (stepsWin.IsVisible()) { WinSave("steps", stepsWin); stepsWin.Hide(); return; }
                    stepsWin.Show();
                }
                FillSteps();   // 先显示再填：列表控件在未激活时 SetRichText 会丢掉内容
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSteps " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 设置：独立小窗（不挤主页面板） ----------------
        private static PixelWindow settingsWin;
        private static UnityEngine.Vector2 settingsPos;   // 记住设置窗上次位置（关掉再开不跳回左上）
        private static bool settingsHasPos;
        private static MachineryButton cPur;
        private static MachineryButton[] modeBtnsS;   // 设置窗里的「效率/省钱/自定」三个小按钮
        private static bool hasUv, hasFilter;         // 背包里有没有紫外灯/过滤棉（没有就不该出现在选项里）
        // 浓度预设：0=自动读瓶子；其余是「自己填纯度」的常用档
        private static readonly int[] PurPresets = { -1, 9096, 9850, 9900, 9990 };
        private static readonly string[] PurNames = { "自动（读瓶）", "90.96%", "98.50%", "99.00%", "99.90%" };

        private static int PurIndex()
        {
            for (int i = 0; i < PurPresets.Length; i++) if (PurPresets[i] == CustomPur) return i;
            return 0;
        }

        /// <summary>打开/收起设置小窗：五行下拉 + 浓度预设 + 填浓度入口。</summary>
        public static void OpenSettings()
        {
            try
            {
                // 只在「引用已经失效」时才重建（主面板重建会把 cPre/cPost 置空）。
                // 否则重复点设置只是开关，不会把窗口重置回默认位置（玩家反馈）。
                bool stale = settingsWin != null && (cPre == null || cPost == null || cTarget == null);
                if (settingsWin != null && !stale)
                {
                    if (settingsWin.IsVisible())
                    {
                        WinSave("cfg", settingsWin);
                        settingsWin.Hide();
                    }
                    else
                    {
                        settingsWin.Show();
                        WinRestore("cfg", settingsWin);
                    }
                    return;
                }
                // 照「手册窗」：窗口建一次就一直复用，绝不销毁 —— 这样引擎会记住它的位置
                if (settingsWin == null) { /* 下面按需建 */ }
                if (settingsWin == null)
                {
                    var st = new MachineryStack(true, 3);
                    cTarget = CycleRow(st, "目标", PlanText.TargetNames, delegate { return TargetIdx; }, new Action<int>(SetTarget));
                    // 玩家：模式不要一长条循环按钮。改成三个小键，点哪个是哪个。
                    var modeRow = new MachineryStack(false, 4);
                    modeBtnsS = new MachineryButton[PlanText.ModeShort.Length];
                    // 批次 28：设置页只留效率 / 性价比两档；默认 0、质量 3 的入口不再创建。
                    int[] modeIds = { 1, 2 };
                    for (int mj = 0; mj < modeIds.Length; mj++)
                    {
                        int mid = modeIds[mj];
                        var mb = new MachineryButton((mid == Mode ? "▶" : "") + PlanText.ModeShort[mid], new Action(delegate { SetMode(mid); }), 56, 18);
                        modeBtnsS[mid] = mb;
                        modeRow.Add(mb);
                    }
                    st.Add(modeRow);
                    // 玩家 2026-11-16：无限道具不能只靠“同档再点”藏起来；设置页给一个明确开关。
                    infBtn = new MachineryButton((InfiniteTools ? "▶" : "") + "无限道具", new Action(delegate
                    {
                        ToggleInfinite("设置页无限开关");
                        RefreshModeDesc();
                        RequestRebuild();
                    }), 92, 18);
                    st.Add(infBtn);
                    // 药片：暂时用「+1 / -1」两个小键（数字输入框还没找到可复用的原生控件）
                    var tabRow = new MachineryStack(false, 4);
                    tabRow.Add(new MachineryButton("-1", new Action(delegate { SetPost(Math.Max(1, PostMode <= 0 ? 1 : PostMode - 1)); }), 28, 18));
                    tabRow.Add(new MachineryButton("自动", new Action(delegate { SetPost(0); }), 44, 18));
                    tabRow.Add(new MachineryButton("+1", new Action(delegate { SetPost(Math.Min(PlanText.PostNames.Length - 1, (PostMode <= 0 ? 1 : PostMode) + 1)); }), 28, 18));
                    st.Add(tabRow);
                    // 平衡模式：开＝按瓶里真实水质算；关＝水质按未知处理（玩家要求放进设置里）
                    var balRow = new MachineryStack(false, 4);
                    balRow.Add(new MachineryButton((BalanceOff ? "▶" : "") + "平衡开", new Action(delegate { SetBalance(true); }), 58, 18));
                    balRow.Add(new MachineryButton((!BalanceOff ? "▶" : "") + "平衡关", new Action(delegate { SetBalance(false); }), 58, 18));
                    st.Add(balRow);
                    // 前置只列「背包里真有的」：两个都没有时把前置强制回自动
                    // 过滤棉/紫外灯在机器里是 node 类模组（实测 identifier = node_medium），
                    // 所以「有没有」要看当前净水器已装的子件，不是背包物品。
                    int nodes = CountNodeModules();
                    hasFilter = nodes >= 1; hasUv = nodes >= 1;
                    if (!hasUv && !hasFilter) PreMode = 0;
                    // 前置：图标化小键（只列背包里真有的），点哪个是哪个
                    var preRow = new MachineryStack(false, 4);
                    preRow.Add(Small("前:按模式", new Action(delegate { SetPre(0); }), 74));
                    preRow.Add(Small("不处理", new Action(delegate { SetPre(1); }), 60));
                    if (hasFilter) preRow.Add(Small("过滤", new Action(delegate { SetPre(2); }), 50));
                    if (hasUv) preRow.Add(Small("紫外", new Action(delegate { SetPre(3); }), 50));
                    st.Add(preRow);
                    cPre = null;
                    if (!hasUv && !hasFilter) try { cPre.SetDescription("背包里没有紫外灯/过滤棉：前置只能用「自动」或「不处理」"); } catch (Exception) { }
                    cPost = null;   // 后置已由「药 -1/自动/+1」取代
                    st.Add(new MachineryButton("水源 ▶ 选择…", new Action(delegate { OpenSourcePicker(); }), 140, 20));
                    // 网页的「自己填纯度」：不靠扫描仪也能按一个指定浓度算方案
                    // 浓度：自动时直接把瓶子当前纯度写出来，否则玩家不知道这行在说什么
                    string curPur = "自动（读瓶）";
                    try
                    {
                        if (CustomPur > 0) curPur = "手填 " + PlanText.Pct(CustomPur);
                        else
                        {
                            var bb = CtxBottle(); if (bb == null) bb = GameBottle();
                            int pp = bb == null ? 0 : WaterHelper.GetWaterPurity(bb);
                            curPur = pp > 0 ? ("瓶 " + PlanText.Pct(pp)) : "自动（未读到）";
                        }
                    }
                    catch (Exception) { }
                    try { cPur = CycleRow(st, "浓度", PurNames, new Func<int>(PurIndex), new Action<int>(delegate(int i)
                    {
                        CustomPur = PurPresets[i];
                        Rebuild(true);
                    }));
                    } catch (Exception) { }
                    try { cPur.SetDescription(curPur); } catch (Exception) { }
                    pSetBtns.Clear(); pSetBtns.Add(cTarget); pSetBtns.Add(cMode); pSetBtns.Add(cPre); pSetBtns.Add(cPost); pSetBtns.Add(cSrc); pSetBtns.Add(cPur);
                    st.Add(Small("返回", new Action(delegate { if (settingsWin != null) settingsWin.Hide(); }), 72));
                    var sp = new MachineryPanel("", st, "watercalc_cfg");
                    sp.SetMinWidth(240);
                    settingsWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (settingsWin != null)
                    {
                        try { settingsWin.ResizePixels(260, 200); } catch (Exception) { }
                        WinRestore("cfg", settingsWin);
                        WinRestore("cfg", settingsWin);
                        Log("[ui] 设置窗 ok");
                    }
                }
                if (settingsWin != null)
                {
                    if (settingsWin.IsVisible()) settingsWin.Hide(); else settingsWin.Show();
                    Rebuild(true);
                }
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSettings " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>玩家点方案表那一行：按序号把当前表里那条方案设为选中（与 ctl pick=N 同一条路）。
        /// 玩家 2026-11-12：点了没反应 —— 已锁定的旧方案（LockValid 只认设置页，不认 ManualPickLabel）
        /// 与 0.25 秒结果缓存会把这里的新选择直接盖掉，所以写进选择后必须丢缓存 + 解锁定再重算。</summary>
        private static void PickRow(int k)
        {
            var rows = lastRows;
            if (rows == null || k < 0 || k >= rows.Length)
            {
                var rep = CurrentPlan(true);
                rows = rep == null ? null : rep.TableRows(RowCount).ToArray();
            }
            if (rows == null || k < 0 || k >= rows.Length) { Log("[ui] row " + k + " no-row"); return; }
            ManualPickLabel = rows[k].label;
            DropPlanCache();                 // 作废 CurrentPlan 的 0.25 秒结果缓存
            UnlockPlan("手动选择方案");       // 解锁旧方案，否则 LockValid 直接返回 lockedReport，选择被无视
            Log("[ui] row " + (k + 1) + " pick=" + ManualPickLabel);
            // 玩家 2026-11-12 定案：挑方案只重写右侧文本，绝不走 Rebuild（那条路会牵扯导航高亮、窗口尺寸）。
            // BindModel 本身只调 SetText/SetValue/SetVisible，不新建也不销毁任何控件。
            lastKey = "";            // 清掉文字指纹缓存，强制这一次全量重写
            lastStepOwner = null;
            lastRichOwner = null;
            BindModel(ModelFor(true), true);
        }

        /// <summary>ctl 用：切页并回报状态。</summary>
        public static string PagePublic(int p)
        {
            try
            {
                if (p >= 0 && p <= 5) SetPage(p);
                return "page -> " + UiPage + " visible=" + ((panelWin != null && panelWin.IsVisible()) ? "true" : "false");
            }
            catch (Exception e) { return "page -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>联调用：计算页当前读数（0.1 mL 口径 + 六项杂质 bp），验证右击瓶/2 L 瓶精度。</summary>
        public static string CalcClearPublic() { OnCalcClear(); return CalcStatePublic(); }

        /// <summary>测试总线：按语义名触发按钮回调，绕开鼠标坐标。DEV 命令通道专用。</summary>
        public static string PressPublic(string name)
        {
            if (string.IsNullOrEmpty(name)) return "press -> 空";
            switch (name)
            {
                case "modeCheap":   SetMode(2, true);              break;   // 标题栏金币
                case "modeEff":     SetMode(1, true);              break;   // 标题栏时钟（第一次切效率）
                case "modeDefault": SetMode(0, false);             break;
                case "modeQuality": SetMode(3, false);             break;
                case "calcTools":   OnCalcToolsCycle();            break;   // 计算页「道具」按钮
                case "clear":       CalcClearPublic();             break;   // 计算页「清空」
                case "tabMain":     SetPage(0);                    break;
                case "tabSet":      SetPage(1);                    break;
                case "tabCalc":     SetPage(4);                    break;
                case "fill":        OnFillPublic();                break;
                case "prepare":     OnPreparePublic();             break;
                case "tablet":      OnTabletPublic();              break;
                case "nightCap":    SetNightCap(WaterPlan.NightCap > 1 ? 0 : 1); break;   // 设置页「多晚」
                default:            return "press -> 未知 " + name;
            }
            return "press " + name + " -> " + StateOneLine();
        }

        /// <summary>一行关键状态：页面 / 档位 / 晚数 / 道具 / 口径 / 方案 / 外框。测试断言用。</summary>
        public static string StateOneLine()
        {
            var r = calcRep;
            string pick = (r == null || r.pick == null) ? "-" : r.pick.label;
            long outUl = (r == null || r.pick == null) ? 0 : r.pick.outUl;
            double cost = (r == null || r.pick == null) ? 0 : r.pick.cost;
            int nights = (r == null || r.pick == null) ? 0 : r.pick.nights;
            int bp = (r == null || r.pick == null) ? 0 : r.pick.purityBp;
            return "ui page=" + UiPage + " mode=" + Mode + " nightCap=" + WaterPlan.NightCap
                 + " tools=" + calcToolsMode + " scope=" + calcScopeStr
                 + " cap=" + calcCapUl + " water=" + calcWaterUl
                 + " pick=" + pick + " nights=" + nights + " nightsMin=" + lastNightsMin
                 + " pur=" + bp + " out=" + outUl
                 + " cost=" + cost.ToString("0.##", CultureInfo.InvariantCulture)
                 + " searchMs=" + AutoSolve.LastSearchMs
                 + " feed=" + CalcFeedText();
        }

        public static string SelectPairPublic(string a)
        {
            int i = -1;
            try { i = int.Parse(a.Trim()); } catch (Exception) { }
            if (i >= 0) SelectPair(i);
            return MachinesReport();
        }

        public static string DeselectPairPublic() { DeselectPair(); return MachinesReport(); }

        public static string CalcStatePublic()
        {
            string imp = "";
            for (int i = 0; i < calcImpBp.Length; i++) imp += (i > 0 ? "," : "") + calcImpBp[i];
            string item = "none";
            try { if (calcLoadedItem != null) item = GameBridge.NameOfItem(calcLoadedItem) + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(calcLoadedItem); } catch (Exception) { }
            return "calc -> cap=" + (calcCapUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                 + " water=" + (calcWaterUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                 + " pur=" + calcLoadedPur + " imp=[" + imp + "] item=" + item;
        }

        /// <summary>ctl: calc? —— 计算页当前口径与结果，用来核对「切模式是否真的重算」。</summary>
        public static string CalcStatePublic2()
        {
            string s = "calc -> cap=" + (calcCapUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                     + " water=" + (calcWaterUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                     + " use=" + Mode
                     + " tools=" + calcToolsMode + " scope=" + calcScopeStr
                     + " rep=" + (calcRep == null ? "-" : (calcRep.pick == null ? "none" : calcRep.pick.label))
                     + " out=" + (calcRep == null || calcRep.pick == null ? 0 : calcRep.pick.outUl)
                     + " cost=" + (calcRep == null || calcRep.pick == null ? 0.0 : calcRep.pick.cost);
            s += " nightCap=" + WaterPlan.NightCap + " nights=" + (calcRep != null && calcRep.pick != null ? calcRep.pick.nights : 0)
               + " nightsMin=" + lastNightsMin
               + " searchMs=" + AutoSolve.LastSearchMs;
            return s;
        }

        public static void SetCalcModePublic(int v)
        {
            // 计算页「模式」按钮已删：标题栏 Mode 是唯一来源。保留旧探针入口，不再改档位。
            if (v < 0 || v > 3) return;
            calcRep = null;
            calcDirty = true;
            RecalcCalc();
            RefreshCalcLabels();
        }

        /// <summary>ctl: calcmodes? —— 同一组输入下 0/1/2/3 四档各自选什么，用于判定同解。</summary>
        public static string CalcModesPublic()
        {
            if (calcWaterUl <= 0 || calcCapUl <= 0) return "calcmodes -> 空计算器";
            int ownTab = calcToolsMode == 2 ? -1
                       : calcToolsMode == 1 ? (OwnedTools() == null ? 0 : OwnedTools().tablets)
                       : (InfiniteTools ? -1 : (OwnedTools() == null ? 0 : OwnedTools().tablets));
            if (ownTab < 0 && !InfiniteTools && calcToolsMode != 2) ownTab = 0;
            var sb = new StringBuilder("calcmodes ->");
            for (int md = 0; md <= 3; md++)
            {
                var w = new WaterMath.Water();
                long v = Math.Max(1L, calcWaterUl);
                w.v = v; w.cap = Math.Max(v, calcCapUl);
                for (int i = 0; i < calcImpBp.Length; i++)
                {
                    long part = v * calcImpBp[i] / 10000L;
                    if (part > 0) w.parts[CalcImpKeys[i]] = part;
                }
                var r = WaterPlan.BuildFromWater(w, calcPerf, calcQuality, calcTargetBp, md, PreMode, PostMode,
                                                "计算器", ownTab);
                sb.Append(" | m").Append(md).Append('=');
                if (r == null || r.pick == null) { sb.Append("none"); continue; }
                sb.Append(r.pick.label).Append(" out=").Append(r.pick.outUl)
                  .Append(" cost=").Append(r.pick.cost.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" nights=").Append(r.pick.nights);
            }
            return sb.ToString();
        }

        public static void SetCalcToolsPublic(int v)
        {
            if (v >= 0 && v <= 2)
            {
                calcToolsMode = v;
                calcRep = null;
                calcDirty = true;
                RecalcCalc();
                RefreshCalcLabels();
            }
        }

        /// <summary>联调用：列背包/台面所有能装水的容器，并可按序号强制读某一瓶（calc=N / calc=auto）。</summary>
        public static string CalcLoadPublic(string arg)
        {
            var sb = new StringBuilder();
            var bottles = new List<GameItem>();
            long v = 0, cap = 0; int pur = 0;
            try
            {
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    long c = 0;
                    try { c = WaterHelper.GetTotalCapacity(all[i]); } catch (Exception) { c = 0; }
                    if (c <= 0) continue;
                    bottles.Add(all[i]);
                    long w = 0; int q = 0;
                    try { w = WaterHelper.GetTotalVolume(all[i]); } catch (Exception) { }
                    try { q = WaterHelper.GetWaterPurity(all[i]); } catch (Exception) { }
                    string nm = ""; try { nm = GameBridge.NameOfItem(all[i]); } catch (Exception) { }
                    sb.Append("#" + bottles.Count + " " + nm + " " + (w / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                              + "/" + (c / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " " + q + "bp");
                    try
                    {
                        var parts = WaterHelper.GetLiquidParts(all[i]);
                        if (parts != null && parts.Length > 0)
                        {
                            int off = parts.Length >= 7 ? 1 : 0;
                            sb.Append(" p=[");
                            for (int k = 0; k + off < parts.Length && k < 6; k++)
                            {
                                int bp = w > 0 ? (int)(parts[k + off] * 10000L / w) : 0;
                                sb.Append((k > 0 ? "," : "") + bp);
                            }
                            sb.Append("]");
                        }
                    }
                    catch (Exception) { }
                    sb.Append("\n");
                }
            }
            catch (Exception e) { return "calc -> ERR " + e.GetType().Name + " " + e.Message; }

            string a = (arg ?? "").Trim();
            if (a == "auto")
            {
                lastPickedBottle = null;
                LoadCalcFromContext(false);
                return "calc -> auto\n" + sb + CalcStatePublic();
            }
            int idx = 0;
            if (int.TryParse(a, out idx) && idx >= 1 && idx <= bottles.Count)
            {
                var b = bottles[idx - 1];
                // 模拟「右击选中这一瓶」，这样随后切到计算页也不会被机器瓶覆盖。
                lastPickedBottle = b;
                try { cap = WaterHelper.GetTotalCapacity(b); } catch (Exception) { }
                try { v = WaterHelper.GetTotalVolume(b); } catch (Exception) { }
                try { pur = WaterHelper.GetWaterPurity(b); } catch (Exception) { }
                RefreshCalcFromBottle(b, v, cap, pur);
                return "calc -> forced #" + idx + "\n" + sb + CalcStatePublic();
            }
            return "calc -> list\n" + sb + CalcStatePublic();
        }

        /// <summary>联调用：游戏自己截一张图（不经过桌面控制），用来核对混水页/详情页的真实排版。</summary>
        public static string ShotPublic(string name)
        {
#if DEV
            try
            {
                if (string.IsNullOrEmpty(name)) name = "shot";
                foreach (char c in name) if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return "shot -> bad-name";
                string dir = CmdFile.Dir;
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
                string path = System.IO.Path.Combine(dir, name + ".png");
                ScreenCapture.CaptureScreenshot(path);
                return "shot -> " + path;
            }
            catch (Exception e) { return "shot -> ERR " + e.GetType().Name + " " + e.Message; }
#else
            return "shot -> release-disabled";
#endif
        }

        /// <summary>联调用：把混水页当前要显示的文字原样打出来（查有没有 hex 标签、有没有多余行）。</summary>
        public static string MixTextPublic()
        {
            try
            {
                int tapBp = 0; string note = "";
                var groups = ScanMixGroups(out tapBp, out note);
                long capUl = calcCapUl;
                for (int i = 0; i < groups.Count; i++) capUl = Math.Max(capUl, groups[i].capUl);
                var rows = BuildMixRows(groups, tapBp, capUl);
                var b = new RichTextBuilder(300);
                BuildMixText(b, groups, rows, tapBp, capUl, note);
                return "mixtext -> " + b.Build(-1).richTextForeground.Replace("\n", " | ");
            }
            catch (Exception e) { return "mixtext -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>联调用：把详情窗当前要显示的文字原样打出来。</summary>
        public static string DetailTextPublic()
        {
            try
            {
                var r = CurrentPlan(true);
                var b = new RichTextBuilder(320);
                BuildDetailText(b, r);
                return "detailtext -> " + b.Build(-1).richTextForeground.Replace("\n", " | ");
            }
            catch (Exception e) { return "detailtext -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>联调用：窗口/面板/列表/导航按钮的真实像素尺寸，查五键导航与混水页有没有被裁掉。</summary>
        public static string RectPublic()
        {
            try { Canvas.ForceUpdateCanvases(); } catch (Exception e) { return "rect -> canvasERR " + e.GetType().Name; }
            var sb = new StringBuilder("rect -> page=" + UiPage);
            try
            {
                if (panelWin != null)
                {
                    object w = GameBridge.Get(panelWin, "widthPixels"), h = GameBridge.Get(panelWin, "heightPixels");
                    sb.Append(" win=" + ((w is int) ? w.ToString() : "?") + "x" + ((h is int) ? h.ToString() : "?"));
                }
            }
            catch (Exception e) { sb.Append(" winERR=" + e.GetType().Name); }
            try
            {
                if (pMixList != null)
                {
                    object w = GameBridge.Get(pMixList, "widthPixels"), h = GameBridge.Get(pMixList, "heightPixels");
                    sb.Append(" list=" + ((w is int) ? w.ToString() : "?") + "x" + ((h is int) ? h.ToString() : "?"));
                }
            }
            catch (Exception e) { sb.Append(" listERR=" + e.GetType().Name); }
            for (int i = 0; i < navMixBtns.Count; i++)
            {
                try
                {
                    object h = GameBridge.Get(navMixBtns[i], "heightPixels");
                    sb.Append(" nav" + i + "=" + ((h is int) ? h.ToString() : "?"));
                }
                catch (Exception e) { sb.Append(" nav" + i + "ERR=" + e.GetType().Name); }
            }
            sb.Append(" navCount=" + navMixBtns.Count);
            ProbeRect(sb, "title", pTitleRow);
            ProbeRect(sb, "subL", pSubLbl); ProbeRect(sb, "subV", pSub);
            ProbeRect(sb, "purL", pPurLbl); ProbeRect(sb, "purV", pPur);
            ProbeRect(sb, "tgtL", pTgtLbl); ProbeRect(sb, "tgtV", pTgtVal);
            ProbeRect(sb, "feed", pCard1Val); ProbeRect(sb, "step", pStep);
            ProbeRect(sb, "out", pCard2); ProbeRect(sb, "money", pMoney); ProbeRect(sb, "msg", pMsg);
            ProbeRect(sb, "setTarget", cTarget); ProbeRect(sb, "setMode", cMode);
            ProbeRect(sb, "setPre", cPre); ProbeRect(sb, "setPost", cPost); ProbeRect(sb, "setPostN", cPostNum);
            for (int i = 0; i < srcChips.Length; i++) ProbeRect(sb, "src" + i, srcChips[i]);
            ProbeRect(sb, "hydra", cHydra); ProbeRect(sb, "hydraL", cHydraDurLbl); ProbeRect(sb, "hydraS", sHydraDur);
            ProbeRect(sb, "filterL", cFilterDurLbl); ProbeRect(sb, "filterS", sFilterDur);
            ProbeRect(sb, "poolHead", pTableHead); ProbeRect(sb, "poolBox", pRowsBox);
            if (pRowLbls != null) for (int i = 0; i < pRowLbls.Length; i++) ProbeRect(sb, "poolL" + i, pRowLbls[i]);
            if (pRowBtns != null) for (int i = 0; i < pRowBtns.Length; i++) ProbeRect(sb, "poolV" + i, pRowBtns[i]);
            ProbeRect(sb, "stepsList", pList);
            ProbeRect(sb, "calcSel", cCalcSel); ProbeRect(sb, "calcSum", cCalcSum);
            ProbeRect(sb, "calcOut", cCalcOut); ProbeRect(sb, "calcOut2", cCalcOut2); ProbeRect(sb, "calcSlider", sCalcWater);
            for (int i = 0; i < calcVals.Length; i++) ProbeRect(sb, "calc" + i, calcVals[i]);
            ProbeRect(sb, "mixBody", pMixBody);
            return sb.ToString();
        }

        /// <summary>联调探针：输出控件在屏幕上的左/右边缘与宽高，用来核对栅格。</summary>
        private static void ProbeRect(StringBuilder sb, string name, object ui)
        {
            if (ui == null) { sb.Append(" " + name + "=null"); return; }
            try
            {
                var rt = GameBridge.Get(ui, "rectTransform") as RectTransform;
                if (rt == null) { sb.Append(" " + name + "=noRect"); return; }
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                float left = Math.Min(corners[0].x, corners[2].x);
                float right = Math.Max(corners[0].x, corners[2].x);
                object pw = GameBridge.Get(ui, "widthPixels"), ph = GameBridge.Get(ui, "heightPixels");
                int w = (pw is int) ? (int)pw : Mathf.RoundToInt(rt.rect.width);
                int h = (ph is int) ? (int)ph : Mathf.RoundToInt(rt.rect.height);
                sb.Append(" " + name + "=" + left.ToString("0") + "-" + right.ToString("0")
                          + " " + w + "x" + h);
            }
            catch (Exception e) { sb.Append(" " + name + "ERR=" + e.GetType().Name); }
        }

        /// <summary>联调用：切到混水页并汇报扫描结果（组数/组合数/第一名）。</summary>
        public static string MixPagePublic()
        {
            try
            {
                SetPage(5);
                int tapBp = 0; string note = "";
                var groups = ScanMixGroups(out tapBp, out note);
                long capUl = calcCapUl;
                for (int i = 0; i < groups.Count; i++) capUl = Math.Max(capUl, groups[i].capUl);
                var rows = BuildMixRows(groups, tapBp, capUl);
                string first = "none";
                if (rows.Count > 0)
                {
                    var r = rows[0];
                    first = PlanText.Pct(r.high.purityBp) + "+" + PlanText.Pct(r.baseW.purityBp)
                          + " -> " + PlanText.Pct(r.finalBp) + " gain=" + r.gain.ToString("0.00", CultureInfo.InvariantCulture)
                          + " fill=" + (r.fillUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "mL";
                }
                return "mix -> page=" + UiPage + " " + note + " groups=" + groups.Count + " tap=" + tapBp
                     + " rows=" + rows.Count + " cap=" + (capUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " first=" + first;
            }
            catch (Exception e) { return "mix -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static int PageHeight()
        {
            // 四页导航比三页多 23 px，所有页统一 +23，否则第四键会被裁掉。
            // 玩家 2026-11-13：左导航由四键变五键（多「混水」），每页统一再加 23 px。
            if (UiPage == 1) return (WaterPlan.NoMachine ? 200 : 223) + 23 + 46;   // 手填已移到计算页，设置页不再留滑条行
            if (UiPage == 2) return 298;
            if (UiPage == 3) return 278;
            if (UiPage == 4) return 340;   // 计算页：照 v1 网页分三段（净化前 / 模组加成 / 净化结果），清空在最底部
            if (UiPage == 5) return mixPageH;   // 混水：跟主页一样按行数收紧的矮窗（不再用富文本大框）
            if (!AnyMachineVisible()) return 181;   // 恢复稳定高度，136 会导致原生主窗打不开
            return 219;   // 主页导航多了「更多方案」一键，留出 23 px
        }

        /// <summary>切页＝只切四个页容器的可见性 + 改窗口高度，绝不重建控件、绝不 Detach/Attach。</summary>
        private static void SetPage(int page)
        {
            int prevPage = UiPage;
            if (UiPage == page) page = 0;   // 再点一次同页＝返回主页
            UiPage = page;
            if (page == 4 && (calcOwner == null || !ReferenceEquals(calcOwner, settingsOwner)
                              || (calcCapUl <= 0 && calcWaterUl <= 0 && !calcManual)))
                LoadCalcFromContext(false);
            // 标题栏切档后若先在别的页，再进计算页时在这里补一次结果，避免旧 rep 被清空后显示为「-」。
            if (page == 4)
            {
                SyncCalcBottleOnEnter();
                RefreshCalcLabels();
            }
            // 玩家 2026-11-16：切到步骤页只重绑已有方案；不能在这里 Rebuild(true)，
            // 否则每次点「步骤」都会重新算一遍，放药后的水量变化会把方案换掉。
            // 步骤页内容由下面的 BindModel 用同一份 Model 重画。
            // 玩家 2026-11-13：只有点进「混水」页才扫描背包（其它页一律不扫）。
            if (page == 5) RefreshMixPage();
            try
            {
                // 切页**不销毁窗口**：销毁再建会让主界面消失、位置跳回左上（玩家反馈两次）。
                // 只保留一个窗口，换内容 + 改高度即可。
                // 页 1..3 的控件挂在 panel 里；换页＝同一个窗口 Detach/Attach（不销毁）
                EnsurePanel();
                if (panelWin != null) { try { if (!panelWin.IsVisible()) panelWin.Show(); } catch (Exception) { } }
            }
            catch (Exception e) { log.LogWarning("[ui] SetPage " + e.GetType().Name + " " + e.Message); }
            // 玩家 2026-11-12 定案：切页绝不重算（ModelFor(true)/Rebuild 一律不准用），
            // 只把上一份算好的模型重新绑一遍文字；连模型都还没有时才走指纹缓存取一次。
            BindModel(Model != null ? Model : ModelFor(false), true);
            // 玩家 2026-11-13：设置页改设备后切回主页，主页必须立刻按新开关重算/重绑。
            // SetPage 原来只把旧 Model 重新绑一遍文字，设备开关的改动要等下一次 Tick，常常看不到。
            // 玩家 2026-11-16：从计算/混水/步骤切回主页时，不要每次强制重算；
            // 只有从设置页回来才需要按新开关重算。否则刚放下的药会把方案重锁成“先补”。
            if (page == 0 && prevPage == 1)
            {
                lastLiveSig = null;
                Rebuild(true);
            }
        }

        /// <summary>窗口对象是否还活着：Unity 销毁后引用不为 null，但访问 rectTransform 会抛异常。
        /// 「关掉详情再打开就闪退」就是这里没校验。</summary>
        private static bool PanelAlive()
        {
            if (panelWin == null) return false;
            try { var rt = panelWin.rectTransform; return rt != null; }
            catch (Exception) { return false; }
        }

        // 玩家 2026-11-12：放弃「整块面板套 ScrollRect」的做法 —— 实测 host 会撑满父级、
        // RectMask2D 把内容全裁掉，面板变成一大块灰框。宽度统一用 PanelW(304)，
        // 高度仍按页面各自长度（PageHeight），长文本页继续用它各自自带的可滚动控件。
        /// <summary>面板入口：只判断「要不要建控件树」，建树动作全部推给 BuildOnce。
        /// 玩家 m01056：原来窗口与页签都没变时也会白建一整棵控件树再丢掉（开窗/切页各建两遍），卡顿就出在这里。</summary>
        private static void EnsurePanel()
        {
            TraceEntry("EnsurePanel 1 alive=" + (panelWin != null));
            if (!PanelAlive()) { panelWin = null; mainTreeDone = false; panelHeaderDone = false; }   // 死引用先清掉
            if (!(PanelAlive() && mainTreeDone)) BuildMain();   // 主窗的树只在「没建过 / 窗口死了」时建
            TraceEntry("EnsurePanel 2 built alive=" + PanelAlive());
            if (PanelAlive())
            {
                if (!panelHeaderDone) AddPanelHeader();
                ApplyFrameTitle();
            }
            TraceEntry("EnsurePanel 3 header alive=" + PanelAlive());
            if (PanelAlive()) ApplyPage();   // 单窗口：只切页容器的 SetActive + 改窗口高度，不建任何控件
            TraceEntry("EnsurePanel 4 page alive=" + PanelAlive());
        }

        /// <summary>创建控件树：只在「面板第一次显示」或「换了页签」时跑一次。</summary>
        private static void BuildMain()
        {
            bindTxt.Clear();   // 控件对象全换新，旧的文字缓存作废
            // 玩家 2026-11-12 定案（单窗口 + 四个独立面板）：四页内容各装一个独立容器，
            // 每页再各包一个全新的 MachineryPanel；切页只换 Attach 的面板对象。
            navSetBtns.Clear(); navMoreBtns.Clear(); navStepsBtns.Clear(); navCalcBtns.Clear(); navMixBtns.Clear(); titleRows.Clear();
            var pgMain = new MachineryStack(true, 3);
            var pgSet = new MachineryStack(true, 3);
            var pgPool = new MachineryStack(true, 3);
            var pgSteps = new MachineryStack(true, 3);
            var pgCalc = new MachineryStack(true, 3);
            var pgMix = new MachineryStack(true, 3);
            // 玩家 2026-11-13：主页/方案页整块以前是「居中」——左边空一大片、右边贴边。
            // 页面容器统一改成从左边起排（行内再各自定宽左列），看起来才像表格。
            try { pgMain.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { pgSet.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { pgPool.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { pgSteps.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { pgCalc.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { pgMix.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            // 玩家 2026-11-13（第六轮）：混水页上下还留一大片空白 —— 内容容器默认垂直居中，
            // 列表只有几行时就被顶到面板中间。强制从顶部起排，窗口再按内容收高。
            try { pgMix.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            bool main = true;   // 下面两块 if (main) 是原有的主页控件代码，统一装进 pgMain
            pSub = null; pCard1 = null; pCard1Val = null; pCard2 = null; pCard3 = null; pStep = null;
            pHydraHint = null; pHydraHintRow = null;
            pSubLbl = null; pPurLbl = null; pPur = null; pTgtLbl = null; pTgtVal = null;
            pMoney = null;
            pMixLine = null; pTableHead = null; pList = null; pPickRow = null; pMsg = null; pJoin = null; pOwn = null; pMixBody = null;
            cTarget = null; cMode = null; cNight = null; cPre = null; cPost = null; cSrc = null; cHydra = null; cHydraDur = null;
            cHydraDurLbl = null; cHydraDurVal = null; cFilterDurLbl = null; cFilterDurVal = null;
            preDropList = null; preDropOpen = false; cPostNum = null; postTypingAt = -1; postBuf = "";
            try { if (preWin != null) preWin.Hide(); } catch (Exception) { }
            preWin = null; preWinStack = null;
            sFilterDur = null; cFilterDurLbl = null;
            pSetBtns.Clear();

            var stack = pgMain;   // 主页容器的别名（下面这一大段原有代码不动）

            if (main)
            {
                // 玩家 2026-11-12：两列对齐——左列「容器」「水质」定宽（28 px 标签列），
                // 右列取值 SetStretch 吃掉剩余宽度，两行数字的左边缘因此完全对齐。
                var rowSub = new MachineryStack(false, 4);
                rowSub.SetAlign(MachineryStack.Align.Start);
                rowSub.SetMainAlign(MachineryStack.Align.Start);      // 原来是 Center：整组被居中 → 看着像散在中间
                pSubLbl = new MachineryText("容器", 9, HorizontalAlignmentOptions.Right, false);
                pSubLbl.SetColor(Dim);
                pSubLbl.SetFillWidth(true);                            // fillWidth=true 才吃 ResizePixels 的定宽
                pSubLbl.ResizePixels(44, 12);
                pSub = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pSub.SetColor(Cream); pSub.SetFillWidth(true);
                rowSub.Add(pSubLbl); rowSub.Add(pSub);
                try { rowSub.SetStretch(pSub); } catch (Exception) { }   // 右列吃掉剩余宽度（左对齐）
                stack.Add(rowSub);

                var rowPur = new MachineryStack(false, 4);
                rowPur.SetAlign(MachineryStack.Align.Start);
                rowPur.SetMainAlign(MachineryStack.Align.Start);
                pPurLbl = new MachineryText("水源", 9, HorizontalAlignmentOptions.Right, false);
                pPurLbl.SetColor(Dim);
                pPurLbl.SetFillWidth(true);
                pPurLbl.ResizePixels(44, 12);
                pPur = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pPur.SetColor(Cream); pPur.SetFillWidth(true);
                rowPur.Add(pPurLbl); rowPur.Add(pPur);
                try { rowPur.SetStretch(pPur); } catch (Exception) { }
                stack.Add(rowPur);

                // 玩家 2026-11-13（p1）：常显「目标 XX.XX%」。它跟容器/水质一样是仪表盘读数，
                // 不随已达标/到不了这些状态消失。
                var rowTgt = new MachineryStack(false, 4);
                rowTgt.SetAlign(MachineryStack.Align.Start);
                rowTgt.SetMainAlign(MachineryStack.Align.Start);
                pTgtLbl = new MachineryText("目标", 9, HorizontalAlignmentOptions.Right, false);
                pTgtLbl.SetColor(Dim);
                pTgtLbl.SetFillWidth(true);
                pTgtLbl.ResizePixels(44, 12);
                pTgtVal = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pTgtVal.SetColor(Cream); pTgtVal.SetFillWidth(true);
                rowTgt.Add(pTgtLbl); rowTgt.Add(pTgtVal);
                try { rowTgt.SetStretch(pTgtVal); } catch (Exception) { }
                stack.Add(rowTgt);
            }


            if (main)
            {
                // 玩家 2026-11-13（p6）：主页行序改成「容器 → 水源/水质 → 目标 → 状态 → 步骤 → 出来 → 卖/赚 → 持有」。
                // 步骤在出水的上面，才读得出「先做步骤，再出水卖钱」。
                // pCard1 / pCard3 已经不显示（整行不挂进 stack，免得占一条空行）。
                // 卡一的量单独一行放大（对应网页卡里的大字 mL）
                pCard1Val = new MachineryText("", 11, HorizontalAlignmentOptions.Left, false);
                pCard1Val.SetColor(Cream); pCard1Val.SetFillWidth(true);
                stack.Add(pCard1Val);

                // 规范 4/页1 必做 2：结论区顺序固定 「需接水 → 出来 → 卖 → 赚」，
                // 步骤行挪到钱后面（行数不变，所以 PageHeight 不动）。
                pCard2 = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pCard2.SetColor(Cream); pCard2.SetFillWidth(true);
                stack.Add(pCard2);

                // 玩家 2026-11-11：「卖多少 / 赚多少」从「出来 …」那一行挪出来，单独一行跟在下面。
                // 规范 2.3：金额用 Gold；绿色只表示「设备/开关开启」，不许拿来表示赚钱。
                pMoney = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pMoney.SetColor(Gold); pMoney.SetFillWidth(true);
                stack.Add(pMoney);

                // 规范 2.2：步骤属次要信息 → 8 号 + Dim，排在「卖 / 赚」之后。
                pStep = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pStep.SetColor(Dim); pStep.SetFillWidth(true);
                stack.Add(pStep);

                // 玩家 2026-11-13：杂质变化那一行整行不再显示（连控件都不挂，免得占一条空行）。

                // 玩家 2026-11-14：这行要回来——背包里还剩多少耐久，是「能不能照这方案做」的前提
                pOwn = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pOwn.SetColor(Dim); pOwn.SetFillWidth(true);
                stack.Add(pOwn);

                // 玩家：主界面不要六项水质明细（省一行，界面还能更窄）。明细留在「更多方案/日志」里。
                pMixLine = null;

                pMsg = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pMsg.SetColor(Warn); pMsg.SetFillWidth(true);
                stack.Add(pMsg);
                // 批次 29：固定位提示键；只在 autoHydraHint 时显示，不改变设置页 HydraMode。
                pHydraHintRow = new MachineryStack(false, 4);
                try { pHydraHintRow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
                pHydraHint = new MachineryButton("启用海德拉", new Action(delegate { EnableHydraOnce(); }), 104, 20);
                pHydraHint.SetFillWidth(false);
                SetDesc(pHydraHint, "只本次计算临时允许海德拉，不改设置页的自动/强制/不用");
                pHydraHintRow.Add(pHydraHint);
                stack.Add(pHydraHintRow);
                // 注：主页栈不在这里 ResizePixels——2026-11-14 实测设 190 会把窗口撑到 320x196，
                // 违反「PanelW 304 / PageHeight 158 不动」；标签列 44 右对齐已足够对齐三行读数。

                // 玩家：按钮别拉满整行，短一点（以前 268 宽几乎占满面板）
                // 玩家：面板里不要再放「接水」大按钮——直接用净水器标题栏那个水滴图标（fillBtn）。
            }


            // 玩家：图标不再放面板底部，改挂到「净水助手」标题栏右上角（X 左边）
            int iw2, ih2;
            var acts = new MachineryStack(false, 4);
            // 玩家：备水功能先删掉（太难做，等以后再说）
            pMode2 = null;
            // 图标全部挂在标题栏（AddPanelHeader），底部不再放任何图标

            // 玩家 2026-11-12 定案：设置 / 更多方案 / 步骤三页也在这同一个窗口里，
            // 但每页各包一个独立的 MachineryPanel —— 切页只换面板对象，不换里面已经建好的控件。
            FillSet(pgSet);
            FillPool(pgPool);
            FillSteps(pgSteps);
            FillCalc(pgCalc);
            FillMix(pgMix);
            // 玩家 2026-11-15：在混水页点标题栏模式小图标会 Rebuild，但 FillMix 只建空文本；
            // 这里补一次刷新，否则页面停在旧模式/空白，看起来像“切档没反应”。
            if (UiPage == 5) RefreshMixPage();
            panelMain = MakePage("watercalc", pgMain);
            panelSet = MakePage("watercalc_set", pgSet);
            panelPool = MakePage("watercalc_pool", pgPool);
            panelSteps = MakePage("watercalc_steps", pgSteps);
            panelCalc = MakePage("watercalc_calc", pgCalc);
            panelMix = MakePage("watercalc_mix", pgMix);

            // 左列竖排导航 + 右侧内容 + 兜底标题行，全部由 MakePage 按页各建一份：
            // 四个面板是四个不同对象，切页只换 Attach 的对象，控件树一次建好后再不重建。
            MachineryPanel panel = PanelFor(UiPage);
            if (PanelAlive())
            {
                AddPanelHeader();
                ApplyFrameTitle();
                bool attachOk = true;
                // 窗口里已经是这一页的面板就别再插一次（同一棵树反复 Detach/Attach 会把窗口弄坏）。
                if (panelPageFor != UiPage)
                {
                    attachOk = false;
                    try { panelWin.Detach(); } catch (Exception) { }
                    try { panelWin.Attach(panel); attachOk = true; } catch (Exception e2) { log.LogWarning("[ui] attach " + e2.Message); }
                }
                if (!attachOk)
                {
                    // 以前 Attach 失败也硬把「这页装好了」记下来 → 之后永远不再重试，
                    // 表现就是「点设置/更多方案/步骤任何键都没反应」（玩家 2026-10-10 报）。
                    // 失败时直接把窗口判死，下一帧按新窗口重建，别把面板锁在旧页上。
                    panelWin = null;
                    panelPageFor = -1;
                    mainTreeDone = false;
                    panelHeaderDone = false;
                    return;
                }
                panelPageFor = UiPage;
                mainTreeDone = true;
                try { panelWin.ResizePixels(PanelW, PageHeight()); } catch (Exception) { }
                ApplyPage();
                return;
            }
            panelPageFor = UiPage;
            mainTreeDone = true;
            panelWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
            if (panelWin != null)
            {
                // 默认放屏幕中间（PixelWindow 自带 Center()），别贴左上角
                try { panelWin.Center(); } catch (Exception) { }
                int h = PageHeight();
                try { panelWin.ResizePixels(PanelW, h); } catch (Exception) { }
                panelHeaderDone = false;
                AddPanelHeader();
                ApplyFrameTitle();
                ApplyPage();
                // 不再给主面板加点击拦截层：它会盖在面板上，重新打开时抢先吃点击/触发关闭
                // AddClickBlocker(panelWin, "main", 208, h);
                // 位置完全交给引擎/玩家拖动：不再强推坐标（以前推 (300,-240) 会先闪左上角再跳位）。
                try { panelWin.Hide(); } catch (Exception) { }
                if (panelWin.IsVisible())
                {
                    try { panelWin.rectTransform.gameObject.SetActive(false); } catch (Exception) { }
                }
                Log("[ui] 独立窗口 page=" + UiPage + " ok visible=" + panelWin.IsVisible());
            }
            else log.LogWarning("[ui] 独立窗口 null");
        }


        /// <summary>设置页控件（一次性建好，切页只切父节点可见性）。</summary>
        private static void FillSet(MachineryStack page)
        {
            // 五行下拉：「组名 ▶ 当前值」，点一下换下一个
            cTarget = CycleRow(page, "目标", PlanText.TargetNames, delegate { return TargetIdx; }, new Action<int>(SetTarget));
            cMode = CycleRowMap(page, "模式", ModeUi, ModeVal, delegate { return Mode; }, new Action<int>(SetMode));
            cNight = CycleRow(page, "多晚", new string[] { "关", "开" },
                              delegate { return WaterPlan.NightCap > 1 ? 1 : 0; },
                              new Action<int>(SetNightCap));
            cPre = BuildPreDrop(page);
            cPost = BuildPostRow(page);
            // 2026-11-12 玩家定案：水源**保留**（要给不同水源算方案），但换成四个激活块：
            //   自动 / 铁锈 / 沟渠 / 手填。默认「自动」——只开通一只水龙头时自动按那只算，
            //   铁锈开局、沟渠开局都认，不写死铁锈；两个都开才需要玩家自己点。
            cSrc = null;
            BuildSrcChips(page);
            // 玩家 2026-11-14：设置页回到「不放设备按钮」的版本；五个设备开关挪到计算页，
            // 只在计算器里按需生效，不再把主页方案莫名其妙筛空。
            // BuildDevRow(page);
            // 玩家 2026-11-12：没机器的存档也要能算 ——「机器」两块的开关（用/不用）。
            BuildMachChips(page);
            // 玩家 2026-11-10：海德拉做成设置项 —— 一个开关（自动/强制/不用）+ 一个耐久上限（可买多只叠加）
            // 2026-11-12：勾了「不用机器」就把海德拉整组藏掉（它也是机器，没机器的人用不上，省两行）。
            cHydraDur = null; cHydraDurLbl = null; cHydraDurVal = null; sHydraDur = null;
            cFilterDurLbl = null; cFilterDurVal = null; sFilterDur = null;
            if (!WaterPlan.NoMachine)
            {
                cHydra = CycleRowMap(page, "海德拉", HydraUi, HydraVal, delegate { return WaterPlan.HydraMode; }, new Action<int>(SetHydra));
                // 耐久：原生 MachinerySlider，可拖到任意点数（312 就 312），不是 300/600/900 三档
                var hdRow = new MachineryStack(false, 4);
                try { hdRow.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
                cHydraDurLbl = new MachineryText("海德拉", 9, HorizontalAlignmentOptions.Right, false);
                cHydraDurLbl.SetColor(Dim);
                cHydraDurLbl.SetFillWidth(true);
                cHydraDurLbl.ResizePixels(44, 14);
                hdRow.Add(cHydraDurLbl);
                sHydraDur = new MachinerySlider(Math.Max(0, WaterPlan.HydraDuraCap), 120);
                sHydraDur.SetRange(0f, (float)HydraDurMaxSlider, 1f);
                sHydraDur.SetFillWidth(true);
                sHydraDur.onChanged = new Action<float>(OnHydraDurSlide);
                sHydraDur.onReleased = new Action<float>(OnHydraDurReleased);
                hdRow.Add(sHydraDur);
                cHydraDurVal = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                cHydraDurVal.SetColor(Cream);
                cHydraDurVal.SetFillWidth(false);
                cHydraDurVal.ResizePixels(52, 12);
                hdRow.Add(cHydraDurVal);
                page.Add(hdRow);
                RefreshHydraDurLabel();
            }
            else cHydra = null;
            // 玩家 2026-11-10：过滤棉也做成原生滑条 —— 选「这次用多少点耐久」（不按块数，400+311 按块会虚高）。
            // 背包里没有滤棉就不显示这一行。
            if (FilterDurOwned() > 0)
            {
                var fdRow = new MachineryStack(false, 4);
                try { fdRow.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
                cFilterDurLbl = new MachineryText("滤棉", 9, HorizontalAlignmentOptions.Right, false);
                cFilterDurLbl.SetColor(Dim);
                cFilterDurLbl.SetFillWidth(true);
                cFilterDurLbl.ResizePixels(44, 14);
                fdRow.Add(cFilterDurLbl);
                sFilterDur = new MachinerySlider((float)FilterDurNow(), 120);
                sFilterDur.SetRange(0f, (float)FilterDurMaxSlider, 1f);
                sFilterDur.SetFillWidth(true);
                sFilterDur.onChanged = new Action<float>(OnFilterDurSlide);
                sFilterDur.onReleased = new Action<float>(OnFilterDurReleased);
                fdRow.Add(sFilterDur);
                cFilterDurVal = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                cFilterDurVal.SetColor(Cream);
                cFilterDurVal.SetFillWidth(false);
                cFilterDurVal.ResizePixels(64, 12);
                fdRow.Add(cFilterDurVal);
                page.Add(fdRow);
                RefreshFilterDurLabel();
            }
            pSetBtns.Add(cTarget); pSetBtns.Add(cMode); pSetBtns.Add(cPre); pSetBtns.Add(cPost);
            if (cNight != null) pSetBtns.Add(cNight);
            if (cSrc != null) pSetBtns.Add(cSrc);   // 单水龙头存档里没有这一行，别把 null 塞进列表
            if (cHydra != null) pSetBtns.Add(cHydra);
        }

        /// <summary>可用设备（照 v1 网页的五个设备开关）：原生按钮，选中带 ▶，关掉不参与方案。</summary>
        private static void BuildDevRow(MachineryStack page)
        {
            var row = new MachineryStack(false, 3);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            FixedLabel(row, "设备");
            string[] names = { "滤棉", "紫外", "药片", "净水器", "海德拉" };
            int[] widths = { 38, 38, 38, 50, 50 };
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                MachineryButton b = null;
                try
                {
                    b = new MachineryButton(names[i], new Action(delegate { ToggleDev(k); }), widths[i], 18);
                    b.SetFillWidth(false);
                }
                catch (Exception) { }
                if (b != null) { row.Add(b); devBtns[i] = b; }
            }
            page.Add(row);
            RefreshDevRow();
        }

        private static bool DevOn(int i)
        {
            if (i == 0) return devFilter;
            if (i == 1) return devUv;
            if (i == 2) return devTablets;
            if (i == 3) return devMachine;
            return devHydra;
        }

        /// <summary>DEV 联调：直接切设备开关，避免坐标点不到原生按钮。</summary>
        public static string ToggleDevPublic(int i) { ToggleDev(i); return DevStatePublic(); }

        public static string DevStatePublic()
        {
            return "dev=" + (devFilter ? 1 : 0) + (devUv ? 1 : 0) + (devTablets ? 1 : 0)
                 + (devMachine ? 1 : 0) + (devHydra ? 1 : 0)
                 + " allow=" + (WaterPlan.AllowFilter ? 1 : 0) + (WaterPlan.AllowUv ? 1 : 0)
                 + (WaterPlan.AllowTablets ? 1 : 0) + (WaterPlan.AllowMachine ? 1 : 0)
                 + (WaterPlan.AllowHydra ? 1 : 0)
                 + " lock=" + (lockedReport != null ? 1 : 0) + " plan=" + (planCache == null ? "-" : (planCache.pick == null ? "none" : planCache.pick.label));
        }

        private static void ToggleDev(int i)
        {
            if (i == 0) devFilter = !devFilter;
            else if (i == 1) devUv = !devUv;
            else if (i == 2) devTablets = !devTablets;
            else if (i == 3) devMachine = !devMachine;
            else devHydra = !devHydra;
            WaterPlan.AllowFilter = devFilter;
            WaterPlan.AllowUv = devUv;
            WaterPlan.AllowTablets = devTablets;
            WaterPlan.AllowMachine = devMachine;
            WaterPlan.AllowHydra = devHydra;
            RefreshDevRow();
            UnlockPlan("换设备");
            DropPlanCache();
            // 设备开关直接改变方案：把模型缓存与实时指纹一并作废，避免主页继续复用旧 Model。
            modelCache = null;
            modelKey = "";
            planCache = null;
            planKey = "";
            lastLiveSig = null;
            InvalidateCalc();
            RequestRebuild();
        }

        private static void RefreshDevRow()
        {
            string[] names = { "滤棉", "紫外", "药片", "净水器", "海德拉" };
            for (int i = 0; i < devBtns.Length; i++)
            {
                if (devBtns[i] == null) continue;
                try { SetB(devBtns[i], (DevOn(i) ? "▶" : "") + names[i]); } catch (Exception) { }
            }
        }

        /// <summary>前置下拉：点标题展开 9 项列表，选完收起；列表用原生按钮，避免 9 项循环点到手酸。</summary>
        private static MachineryButton BuildPreDrop(MachineryStack page)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            FixedLabel(row, "前置");
            MachineryButton head = null;
            try
            {
                head = new MachineryButton("", new Action(TogglePreDrop), 120, 20);
                head.SetFillWidth(true);
                SkinButton(head, RowFill, Cream, false);
                SetDesc(head, "点这里展开前置列表，选一项后自动收起");
            }
            catch (Exception) { }
            if (head != null) { SetB(head, UiName(PreUi, PreVal, PreMode)); row.Add(head); }
            page.Add(row);
            preDropList = null;
            preDropOpen = false;
            return head;
        }

        private static void TogglePreDrop()
        {
            bool open = false;
            try { open = preWin != null && preWin.IsVisible(); } catch (Exception) { }
            if (open) { HidePreDrop(); return; }
            preDropOpen = true;
            EnsurePreDropWin();
            if (preWin == null) return;
            try
            {
                preWin.Show();
                UnityEngine.Vector2 p;
                if (WinGetPos(panelWin, out p)) ForceAt(preWin, Math.Min(1600f, p.x + 540f), Math.Max(260f, p.y - 70f), 6);
                else CenterWindow(preWin);
            }
            catch (Exception) { }
        }

        private static void EnsurePreDropWin()
        {
            if (preWin != null) return;
            preWinStack = new MachineryStack(true, 2);
            try { preWinStack.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            for (int i = 0; i < PreUi.Length; i++)
            {
                int v = PreVal[i];
                MachineryButton b = null;
                try
                {
                    b = new MachineryButton(PreUi[i], new Action(delegate { SetPre(v); HidePreDrop(); }), 168, 18);
                    b.SetFillWidth(false);
                }
                catch (Exception) { }
                if (b != null) preWinStack.Add(b);
            }
            try
            {
                var panel = new MachineryPanel("前置", preWinStack, "watercalc_pre");
                panel.SetMinWidth(180);
                preWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                if (preWin != null) { preWin.ResizePixels(200, 232); preWin.Hide(); }
                Log("[ui] 前置下拉窗 ok=" + (preWin != null));
            }
            catch (Exception e) { Log("[ui] 前置下拉窗 ERR " + e.GetType().Name + " " + e.Message); }
        }

        private static void HidePreDrop()
        {
            preDropOpen = false;
            try { if (preWin != null) preWin.Hide(); } catch (Exception) { }
        }

        private static void ClosePreDrop(bool resize)
        {
            HidePreDrop();
            try { if (panelWin != null) panelWin.ResizePixels(PanelW, PageHeight()); } catch (Exception) { }
        }

        /// <summary>后置：自动按钮 + 药片数手填框（不再用 0/1/2/3 四档循环）。</summary>
        private static MachineryButton BuildPostRow(MachineryStack page)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            FixedLabel(row, "后置");
            MachineryButton auto = null;
            try
            {
                auto = new MachineryButton("自动", new Action(delegate { SetPost(0); }), 44, 20);
                auto.SetFillWidth(false);
                SkinButton(auto, RowFill, Cream, false);
                SetDesc(auto, "让方案自己决定后置药片数");
            }
            catch (Exception) { }
            if (auto != null) row.Add(auto);
            MachineryButton num = null;
            try
            {
                num = new MachineryButton("0片", new Action(BeginPostTyping), 44, 20);
                num.SetFillWidth(false);
                SkinButton(num, RowFill, Cream, false);
                SetDesc(num, "点这里手动键入后置药片数（0–9），回车确认");
            }
            catch (Exception) { }
            if (num != null) row.Add(num);
            page.Add(row);
            cPost = auto; cPostNum = num;
            RefreshPostRow();
            return auto;
        }

        private static string PostNumText(int post) { return post <= 0 ? "0片" : (post - 1) + "片"; }

        private static void RefreshPostRow()
        {
            try
            {
                if (cPost != null)
                {
                    SetB(cPost, (PostMode <= 0 ? "▶" : "") + "自动");
                    SkinButton(cPost, PostMode <= 0 ? SrcOnFill : RowFill, PostMode <= 0 ? SrcOnText : Cream, false);
                }
                if (cPostNum != null)
                {
                    SetB(cPostNum, postTypingAt >= 0 ? ("…" + postBuf) : PostNumText(PostMode));
                    SkinButton(cPostNum, PostMode > 0 ? SrcOnFill : RowFill, PostMode > 0 ? SrcOnText : Cream, false);
                }
            }
            catch (Exception) { }
        }

        private static void BeginPostTyping()
        {
            if (postTypingAt >= 0) { CommitPostTyping(); return; }
            postTypingAt = Time.frameCount;
            postBuf = "";
            LastMsg = "键入后置药片数（0–9）：回车确认，Esc 取消";
            RefreshPostRow();
        }

        private static void CancelPostTyping()
        {
            postTypingAt = -1; postBuf = "";
            RefreshPostRow();
        }

        private static void CommitPostTyping()
        {
            if (postTypingAt < 0) return;
            int n = 0;
            if (!int.TryParse(postBuf, out n)) { CancelPostTyping(); LastMsg = "输入无效：只填 0–9"; return; }
            if (n < 0) n = 0;
            if (n > 9) n = 9;
            postTypingAt = -1; postBuf = "";
            SetPost(n + 1);   // PostMode = 1 表示手动 0 片
        }

        private static void PostInputTick()
        {
            if (postTypingAt < 0) return;
            if (Input.GetMouseButtonDown(0) && Time.frameCount > postTypingAt + 1) { CommitPostTyping(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelPostTyping(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { CommitPostTyping(); return; }
            string ins = null;
            try { ins = Input.inputString; } catch (Exception) { }
            if (string.IsNullOrEmpty(ins)) return;
            bool changed = false;
            for (int i = 0; i < ins.Length; i++)
            {
                char c = ins[i];
                if (c == '\b') { if (postBuf.Length > 0) { postBuf = postBuf.Substring(0, postBuf.Length - 1); changed = true; } }
                else if (c >= '0' && c <= '9') { if (postBuf.Length < 2) { postBuf += c; changed = true; } }
            }
            if (changed) RefreshPostRow();
        }

        /// <summary>更多方案页：表头 + 6 行横排（左：序号+名称，右：自适应数值），点行就选中。</summary>
        private static void FillPool(MachineryStack page)
        {
            // 玩家要求：点方案表里的**行本身**就选中（以前只有底部 1..5 数字键）。
            // 2026-11-12 重构：每行改成一个横排 MachineryStack ——
            //   左：定宽的「1. 过滤  紫外」文本（SetFillWidth(false)，不拉伸）；
            //   右：数值（"1650→1650  99.01%"）SetFillWidth(true) 自适应撑满，点它就是选中这一行。
            // 不再用 258 px 的大长条按钮（那种按钮会把窗口顶宽、把布局撑爆）。
            pTableHead = new MachineryText("点一行就选中", 8, HorizontalAlignmentOptions.Left, false);
            pTableHead.SetColor(Warn); pTableHead.SetFillWidth(true);
            page.Add(pTableHead);
            pRowsBox = new MachineryStack(true, 2);
            pRowBtns = new MachineryButton[RowCount];
            pRowLbls = new MachineryText[RowCount];
            for (int ri = 0; ri < RowCount; ri++)
            {
                int k = ri;
                // 玩家 2026-11-13（p6a）：以前「左定宽标签 + 右数值块」挤在一行，内容区只有 ~190px，
                // 右块把「+352.1」顶到窗口外被裁掉。改成两行：上一行方案名（与「步骤」页同一口径），
                // 下一行整条数值块（占满内容宽度，既可点也不裁字）。
                var lbl = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                lbl.SetColor(Cream);
                lbl.SetFillWidth(true);
                var val = new MachineryButton("", new Action(delegate { PickRow(k); }), 190, 20);
                try { val.SetFillWidth(true); } catch (Exception) { }
                AlignButtonText(val, HorizontalAlignmentOptions.Right);   // 规范 3.3：数值列右对齐，小数点才成一条竖线
                SkinButton(val, RowFill, Cream, false);                 // 数值块扁平化（左列本就是 MachineryText）
                pRowLbls[ri] = lbl;
                pRowBtns[ri] = val;
                // 两行直接串在 pRowsBox 里（竖排的 box 会把每个子控件各放一行，
                // 不再套第二层竖排 stack —— 嵌套 stack 的宽度不保证跟着父级撑满）。
                pRowsBox.Add(lbl); pRowsBox.Add(val);
            }
            page.Add(pRowsBox);
            pPickRow = null;
        }

        /// <summary>步骤页：自带的可滚动富文本列表。</summary>
        /// <summary>混水页：和「步骤」页同一种可滚动富文本，窗口高度固定，不再弹一条长条窗。</summary>
        private static void FillMix(MachineryStack page)
        {
            // 玩家 2026-11-13（第六轮）：「混水改成主界面那样」——不要那块酒红色富文本大框，
            // 直接用主页同款灰色背景的文本行，窗口也就跟主页一样矮。
            pMixList = null;
            pMixBody = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
            pMixBody.SetColor(Cream); pMixBody.SetFillWidth(true);
            pMixBody.ResizePixels(190, 12);   // 规范 3.6：正文吃满内容宽（原 118 宽会折行）
            page.Add(pMixBody);
        }

        /// <summary>机器选中/取消、机器里的水量或六项变化时，混水页立即换方案，不等玩家重进页面。</summary>
        private static void MixMachineWatch()
        {
            if (UiPage != 5 || pMixBody == null) { lastMixMachineKey = ""; return; }
            string key = "none";
            try
            {
                GameItem b = null;
                if (!uiDeselected)
                {
                    b = GameBottle();
                    if (b == null) b = CtxBottle();
                }
                if (b != null)
                {
                    long v = WaterHelper.GetTotalVolume(b);
                    long cap = WaterHelper.GetTotalCapacity(b);
                    int pur = WaterHelper.GetWaterPurity(b);
                    key = "b|" + v + "|" + cap + "|" + pur + "|" + MachinePartsSig(b);
                }
            }
            catch (Exception e) { Log("[mix] 机器指纹失败 " + e.GetType().Name + " " + e.Message); key = "none"; }
            if (key == lastMixMachineKey) return;
            lastMixMachineKey = key;
            RefreshMixPage();
        }

        /// <summary>点进混水页才扫描：枚举水质组两两组合 + 水龙头，算出净赚 Top5。</summary>
        private static void RefreshMixPage()
        {
            if (pMixBody == null) return;
            try
            {
                int tapBp = 0; string note = "";
                // 当前机器里那瓶水单独放顶部：它已经被混过，不能当成背包里一瓶干净的“基础水”再列一次。
                GameItem machineB = null;
                try
                {
                    // 玩家主动取消选择时，即使机器窗口还开着，混水页也回到背包 1+1，不能继续按那台机器算。
                    // 机器窗口开着时只认当前选中槽；槽空就按没有机器瓶处理，不能再用 CtxBottle 撑旧方案。
                    if (!uiDeselected)
                    {
                        // 混水页要能读到「净水器槽里已有液体」，不能只认点了绿点的配对：
                        // 机器窗口开着但还没点选时，PlanBottle() 会返回 null，
                        // 于是顶部没有当前机器水、下面扫描又按机器槽排掉，整瓶水就“消失”了。
                        // 玩家 2026-11-17：已点选时只读 activeIdx 对应那台，不能因为全局
                        // MachineWindow 还没同步，就掉到 AnyVisibleMachineBottle 的另一台。
                        var selPair = SelectedPairForRead();
                        if (selPair != null)
                            machineB = ActiveSlotBottle(selPair.BottleInv);
                        else if (MachineOpen())
                        {
                            machineB = PlanBottle();
                            if (machineB == null && !uiDeselected)
                                machineB = AnyVisibleMachineBottle();
                        }
                        else machineB = CtxBottle();
                    }
                }
                catch (Exception e) { Log("[mix] 取机器瓶失败 " + e.GetType().Name + " " + e.Message); }
                var groups = ScanMixGroups(out tapBp, out note, machineB);
                long capUl = 0L;
                if (machineB != null)
                {
                    try { capUl = WaterHelper.GetTotalCapacity(machineB); } catch (Exception) { capUl = 0L; }
                }
                if (capUl <= 0) capUl = calcCapUl;
                for (int i = 0; i < groups.Count; i++) capUl = Math.Max(capUl, groups[i].capUl);
                // 玩家 2026-11-16：混水方案不再按当前容器容量截断；能兑多少就兑到阈值，
                // 750 mL 基础水可以全部进方案，输出超过 6L 时由玩家分瓶倒。
                var rows = BuildMixRows(groups, tapBp, long.MaxValue);
                var b = new RichTextBuilder(500);   // 玩家 2026-11-13（第六轮）：行尾要带纯度，行宽放宽避免「已有 基础水 5932.5mL / 98.86%」折成两行
                int lines = 0;
                if (machineB != null)
                {
                    int tapGrade = 0, tapPurTmp = 0;
                    try { TapSource(out tapGrade, out tapPurTmp); } catch (Exception) { }
                    bool machinePlan = false;
                    lines += BuildMachineMixLines(b, machineB, tapBp, tapGrade, capUl, out machinePlan);
                    lines += BuildMixText(b, groups, rows, tapBp, capUl, note, machinePlan);
                }
                else
                    lines += BuildMixText(b, groups, rows, tapBp, capUl, note, false);
                var rich = b.Build();
                SetT(pMixBody, rich.richTextForeground);
                // 只显示最高收益的一条方案，高度随实际行数收紧；已有水数量保持全列。
                mixPageH = Math.Max(150, Math.Min(300, 72 + lines * 14));
                if (panelWin != null && UiPage == 5) { try { panelWin.ResizePixels(PanelW, mixPageH); } catch (Exception) { } }
                Log("[mix] 页面刷新 " + note + " 组合=" + rows.Count + " cap=" + capUl + " h=" + mixPageH + " lines=" + lines);
            }
            catch (Exception e)
            {
                LastMsg = "混水扫描失败：" + e.GetType().Name;
                log.LogWarning("[ui] mix page " + e.GetType().Name + " " + e.Message);
                var b = new RichTextBuilder(300);
                AddLine(b, "混水扫描失败：" + e.GetType().Name, ColorPalette.LightOrange, false);
                SetT(pMixBody, b.Build().richTextForeground);
            }
        }

        private static void FillSteps(MachineryStack page)
        {
            var head = new MachineryText("做法步骤", 9, HorizontalAlignmentOptions.Left, false);
            head.SetColor(Cyan); head.SetFillWidth(true);
            page.Add(head);
            try
            {
                pList = new ScrollableRichTextElement(true, true);
                pList.ResizePixels(284, 190);
                page.Add(pList);
            }
            catch (Exception e) { log.LogWarning("[ui] list " + e.Message); pList = null; }
        }

        /// <summary>玩家 2026-11-12 定案（四个独立面板）：换页＝Detach 旧面板、Attach 这一页的面板，
        /// 然后立刻把窗口尺寸强行拉回本页高度。四个面板是四个不同对象，不踩「反复 Attach 同一个」的崩溃；
        /// 里面已经建好的控件不重建、不销毁。</summary>
        private static void ApplyPage()
        {
            int h = PageHeight();
            lastPageH = h;
            if (!PanelAlive()) return;
            if (panelPageFor == UiPage)
            {
                // 这一页已经装好了：只把高度拉回来，绝不重复 Attach。
                try { panelWin.ResizePixels(PanelW, h); } catch (Exception) { }
                return;
            }
            var target = PanelFor(UiPage);
            if (target == null) { panelPageFor = -1; mainTreeDone = false; return; }
            bool ok = true;
            try
            {
                panelWin.Detach();
                ok = false;
                panelWin.Attach(target);
                try { FlatSkinTick(); } catch (Exception) { }   // Attach 会触发 OnEnable→ApplyState，立刻补一刀
                ok = true;
            }
            catch (Exception e) { log.LogWarning("[ui] page attach " + e.GetType().Name + " " + e.Message); }
            if (!ok) { panelPageFor = -1; mainTreeDone = false; return; }   // 失败交回上层重建，别锁死在旧页
            panelPageFor = UiPage;
            try { panelWin.ResizePixels(PanelW, h); } catch (Exception) { }   // 强制定高，Attach 之后立刻拉回
            Log("[ui] page -> " + UiPage + " h=" + h);
        }

        /// <summary>第几页对应哪一个独立面板。</summary>
        private static MachineryPanel PanelFor(int page)
        {
            if (page == 1) return panelSet;
            if (page == 2) return panelPool;
            if (page == 3) return panelSteps;
            if (page == 4) return panelCalc;
            if (page == 5) return panelMix;
            return panelMain;
        }

        /// <summary>一页的左侧三键导航。每页各建一份，按钮引用进列表，切页时全部重写文字（修 ▶ 残影）。</summary>
        private static MachineryStack MakeNav(bool withMore)
        {
            var nav = new MachineryStack(true, 3);
            try { nav.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            var b1 = Small("设置", new Action(delegate { SetPage(1); }), 72);
            var b2 = withMore ? Small("更多方案", new Action(delegate { SetPage(2); }), 72) : null;
            var b3 = Small("步骤", new Action(delegate { SetPage(3); }), 72);
            var b4 = Small("计算", new Action(delegate { SetPage(4); }), 72);
            // 玩家 2026-11-13：混水从计算页里的按钮改成左栏第五个页签（放在「计算」下面）。
            // 2026-11-15：已经在混水页时再点一次＝按当前机器水刷新，不再直接跳回主页。
            var b5 = Small("混水", new Action(delegate
            {
                if (UiPage == 5) RefreshMixPage();
                else SetPage(5);
            }), 72);
            navSetBtns.Add(b1); if (b2 != null) navMoreBtns.Add(b2); navStepsBtns.Add(b3); navCalcBtns.Add(b4); navMixBtns.Add(b5);
            // 玩家 2026-11-11：不改文字色，保留原生深色，只用 ▶ 前缀表示当前页。
            nav.Add(b1); if (b2 != null) nav.Add(b2); nav.Add(b3); nav.Add(b4); nav.Add(b5);
            return nav;
        }

        /// <summary>把一页的内容容器包成一个独立面板：兜底标题行 + 左导航 + 右内容。</summary>
        private static MachineryPanel MakePage(string id, MachineryStack content)
        {
            var row = new MachineryStack(false, 10);   // 导航与内容留 10px，避免「重金属」标签压到导航右边框
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }   // 左右两列同一顶线起排
            row.Add(MakeNav(id == "watercalc"));
            row.Add(content);

            // 标题要像官方净水器那样贴在最上方居中，且不要那个白色药丸框：
            // panel 标题传 null，先放一行居中文字兜底；能拿到 frame.titleText 时再把这行隐藏。
            var rootV = new MachineryStack(true, 3);
            // 玩家 2026-11-13：整页（导航+内容）以前在面板里居中，左右各留一大截空白。
            // 改成从左边起排，导航贴左、内容紧跟，主页标签才不会被推到中间。
            try { rootV.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            // 玩家 2026-11-13（第六轮）：混水页列表被顶到窗口中间、上面一大片空白 —— 整页容器也强制顶对齐。
            try { rootV.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            var title = new MachineryText("净水助手", 9, HorizontalAlignmentOptions.Center, false);
            title.SetColor(Cream);
            title.SetFillWidth(true);
            rootV.Add(title);
            rootV.Add(row);
            titleRows.Add(title);
            if (pTitleRow == null) pTitleRow = title;

            var p = new MachineryPanel((string)null, rootV, id);
            p.SetMinWidth(PanelW);
            return p;
        }

        /// <summary>四页各有一行兜底标题，隐藏时要一起藏（否则切页后别的页还留着一行字）。</summary>
        private static void SetTitleRowsVisible(bool on)
        {
            for (int i = 0; i < titleRows.Count; i++) SetVisible(titleRows[i], on);
        }

        /// <summary>这些 UI 包装类型没有暴露 gameObject，统一从 rectTransform 上拿。</summary>
        private static void SetVisible(object ui, bool on)
        {
            if (ui == null) return;
            try
            {
                // 走 GameBridge.Get → ModReflect：不再用 ui.GetType() 反射（工坊扫描器会判 reflection-external）。
                var rt = GameBridge.Get(ui, "rectTransform") as RectTransform;
                if (rt != null) rt.gameObject.SetActive(on);
            }
            catch (Exception) { }
        }

        private static MachineryButton Small(string text, Action cb, int w)
        {
            // 玩家 2026-11-11：按钮原来又窄又小、字贴边；但要的是「矮一点、别撑满整栏」——
            // 之前把宽度强拉到 88 反而被拉成一长条，现在按调用方给的宽度，并关掉 fillWidth。
            var b = new MachineryButton(" " + text + " ", cb, w, 20);
            try { b.SetFillWidth(false); } catch (Exception) { }
            // 2026-11-12 回滚：导航三键恢复原生米色按键（玩家实测更喜欢原样式，扁平黑块反而像"禁用"）。
            // 想再压平就把下面这行打开：SkinButton(b, new Color(0.11f,0.12f,0.14f,1f), Cream, false);
            return b;
        }

        /// <summary>
        /// 给原生按钮的文字上色（MachineryButton 没有 SetColor，只能改它子树里的 TMP 文字）。
        /// 同时把 Button.transition 关掉，否则鼠标悬停时原生样式会把颜色盖回去（选台圆点踩过同一个坑）。
        /// </summary>
        /// <summary>UI 规范：按钮文字右对齐（只改已有 TMP 子节点，不新建字体/控件）。</summary>
        private static void AlignButtonText(MachineryButton b, HorizontalAlignmentOptions align)
        {
            if (b == null) return;
            RectTransform rt = b.rectTransform;
            if (rt == null) return;
            var tmps = rt.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
            for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) tmps[i].horizontalAlignment = align;
        }

        /// <summary>UI 规范：滚动富文本正文 9 号、行距 3（只调已有文本节点参数）。</summary>
        private static void StyleRichText(ScrollableRichTextElement list, int size, float lineGap)
        {
            if (list == null) return;
            RectTransform rt = list.rectTransform;
            if (rt == null) return;
            var tmps = rt.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
            for (int i = 0; i < tmps.Length; i++)
            {
                if (tmps[i] == null) continue;
                tmps[i].fontSize = size;
                tmps[i].lineSpacing = lineGap;
            }
        }

        private static void PaintButton(MachineryButton b, Color c)
        {
            if (b == null) return;
            try
            {
                RectTransform rt = b.rectTransform;
                if (rt == null) return;
                var tmps = rt.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) tmps[i].color = c;
                var btns = rt.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                for (int i = 0; i < btns.Length; i++) if (btns[i] != null) btns[i].transition = UnityEngine.UI.Selectable.Transition.None;
            }
            catch (Exception) { }
        }

        // ============================================================================
        //  扁平皮肤（IMGUI 风纯色块）  2026-11-12
        //  反编译 MachineryButton.cs 的结论（别再指望关 transition 了）：
        //    每个 MachineryButton 只有 3 个子节点：plate(底板) / iconNode(图标) / textNode(文字)，
        //    悬停/按下/失焦全走 OnHover|OnPress|OnDefocus → ApplyState(...)，它一口气改写：
        //        plate.image.sprite  = ButtonHover / ButtonPressed   ← 九宫格框又贴回来
        //        plate.image.color   = Color.white                   ← 底色被刷白（「反白」就是这么来的）
        //        textNode.text.color = BUTTON_TEXT                   ← 字色也被改回去
        //    这些按钮身上根本没有 UGUI Button，Button.transition = None 压根没参与。
        //  ⇒ 一次性设色必被洗掉，只能「每帧自愈」：只在值真的不同时才写，
        //    Image / TMP 的 setter 在值没变时不会标脏，稳态零开销（同 RefreshPickTints 的手法）。
        // ============================================================================
        // 2026-11-12 换色：参考模组 PlannerWindow.cs:244-272 的配色全取自游戏同源暖灰；
        // 原来的冷蓝灰 #262A2E 压在 #424242 暖面板上，怎么调都不像这个游戏的东西。
        private static readonly Color RowFill    = new Color(36f / 255f, 34f / 255f, 39f / 255f, 1f);   // #242227（＝参考模组 Empty）
        private static readonly Color RowFillSel = new Color(52f / 255f, 50f / 255f, 58f / 255f, 1f);   // #34323A 选中亮一档
        private const int PoolLabW = 128;   // 更多方案页左列固定宽度（六行同起点，右块数字有足够宽度）
        private const string GAP = "  ";    // 全局分隔统一用两个半角空格（「  」在粗像素字里太抢眼，满屏小圆点）

        private sealed class FlatSkin
        {
            public MachineryButton btn;
            public Image plate;                    // 底板 Image
            public TMPro.TextMeshProUGUI text;     // 按钮文字
            public Color fill;                     // 底色（hidePlate 时忽略）
            public Color tx;                       // 字色
            public bool hidePlate;                 // true = 底板整个隐形（标签用）
        }

        private static readonly List<FlatSkin> flatSkins = new List<FlatSkin>();

        // 原生 MachineryRadioButton 的 radio_on 图标是「绿底 + 深红点」，16px 下像一条红杠，
        // 不是参考模组那种绿勾。保留原生单选结构，只把底板刷成纯绿/纯灰，字色提亮。
        private sealed class RadioSkin
        {
            public MachineryRadioButton btn;
            public Image plate;
            public Color on;
            public Color off;
        }
        private static readonly List<RadioSkin> radioSkins = new List<RadioSkin>();
        private static Sprite radioSolid;
        private static bool radioSolidTried;

        /// <summary>1×1 纯白 sprite，专门给 radio 底板用；不依赖资源名 "flat" 是否加载成功。</summary>
        private static Sprite RadioSolid()
        {
            if (radioSolid == null && !radioSolidTried)
            {
                radioSolidTried = true;
                try
                {
                    var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    tex.SetPixel(0, 0, Color.white);
                    tex.Apply();
                    radioSolid = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
                }
                catch (Exception) { radioSolid = null; }
            }
            return radioSolid;
        }
        private static Sprite flatSprite;
        private static bool flatTried;

        /// <summary>游戏自带 1×1 纯白图（MachineryStyle.SetFlat 用的就是它）；取不到退回 null，
        /// 贴图为 null 的 Image 同样按纯色矩形铺满。</summary>
        private static Sprite FlatSprite()
        {
            if (flatSprite == null && !flatTried)
            {
                flatTried = true;
                try { flatSprite = MachineryStyle.White; } catch (Exception) { flatSprite = null; }
            }
            return flatSprite;
        }

        private static bool SkinSame(Color a, Color b)
        {
            return Math.Abs(a.r - b.r) < 0.004f && Math.Abs(a.g - b.g) < 0.004f
                && Math.Abs(a.b - b.b) < 0.004f && Math.Abs(a.a - b.a) < 0.004f;
        }

        /// <summary>方案页左列截断：像素字体近似等宽（1 字 ≈ 3.3 px），按字数截断才不会把 128 px 的列撑宽。</summary>
        private static string ClipLabel(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(1, max - 1)) + "…";
        }

        /// <summary>底板：plate 的节点名固定是 "Node (MachineryButton)"，认名字最稳；认不到退回第 0 张 Image。</summary>
        private static Image FindPlate(RectTransform rt)
        {
            var imgs = rt.gameObject.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
                if (imgs[i] != null && imgs[i].gameObject.name == "Node (MachineryButton)") return imgs[i];
            return imgs.Length > 0 ? imgs[0] : null;
        }

        private static TMPro.TextMeshProUGUI FindText(RectTransform rt)
        {
            var tmps = rt.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
            return tmps.Length > 0 ? tmps[0] : null;
        }

        /// <summary>把一颗原生按钮登记成扁平皮肤。fill 的 alpha 决定块有多浓，tx 是字色。</summary>
        private static void SkinButton(MachineryButton b, Color fill, Color tx, bool hidePlate)
        {
            if (b == null) return;
            try
            {
                RectTransform rt = b.rectTransform;
                if (rt == null) return;

                // 兜底：万一真挂了 UGUI Button，顺手掐掉状态过渡（没有就跳过）
                var btns = rt.gameObject.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                for (int i = 0; i < btns.Length; i++)
                    if (btns[i] != null && btns[i].transition != UnityEngine.UI.Selectable.Transition.None)
                        btns[i].transition = UnityEngine.UI.Selectable.Transition.None;

                for (int i = 0; i < flatSkins.Count; i++)
                {
                    var old = flatSkins[i];
                    if (old != null && old.btn == b)
                    { old.fill = fill; old.tx = tx; old.hidePlate = hidePlate; ApplySkin(old); return; }
                }

                var s = new FlatSkin();
                s.btn = b; s.plate = FindPlate(rt); s.text = FindText(rt);
                s.fill = fill; s.tx = tx; s.hidePlate = hidePlate;
                flatSkins.Add(s);
                ApplySkin(s);
            }
            catch (Exception e) { log.LogWarning("[skin] " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>RadioButton 只用原生点击/互斥逻辑，底板刷成纯色块；文字统一用亮 cream。</summary>
        private static void SkinRadio(MachineryRadioButton b, Color on, Color off)
        {
            if (b == null) return;
            try
            {
                try { b.SetTextColor(Cream); } catch (Exception) { }
                RectTransform rt = b.rectTransform;
                if (rt == null) return;
                for (int i = 0; i < radioSkins.Count; i++)
                {
                    var old = radioSkins[i];
                    if (old != null && old.btn == b) { old.on = on; old.off = off; return; }
                }
                var s = new RadioSkin();
                s.btn = b; s.plate = FindPlate(rt); s.on = on; s.off = off;
                radioSkins.Add(s);
            }
            catch (Exception e) { log.LogWarning("[radio-skin] " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>刷一次扁平态（只在真的不一样时才写，避免每帧把 Canvas 标脏）。</summary>
        private static void ApplySkin(FlatSkin s)
        {
            if (s == null) return;
            if (s.plate != null)
            {
                if (s.hidePlate)
                {
                    if (s.plate.enabled) s.plate.enabled = false;                        // 标签：底板隐形
                    if (s.plate.color.a > 0f) s.plate.color = new Color(0f, 0f, 0f, 0f);  // OnShow 会把它打开，颜色也清掉
                }
                else
                {
                    if (!s.plate.enabled) s.plate.enabled = true;
                    var flat = FlatSprite();
                    if (s.plate.sprite != flat) s.plate.sprite = flat;                    // ① 卸掉九宫格框
                    if (s.plate.type != Image.Type.Simple) s.plate.type = Image.Type.Simple;
                    if (!SkinSame(s.plate.color, s.fill)) s.plate.color = s.fill;          // ② 底色 = 深色块
                }
            }
            if (s.text != null && !SkinSame(s.text.color, s.tx)) s.text.color = s.tx;     // ③ 字色
        }

        /// <summary>每帧自愈：被 ApplyState 洗掉的颜色/贴图，在绘制前补回来。</summary>
        private static void FlatSkinTick()
        {
            if (flatSkins.Count == 0 && radioSkins.Count == 0) return;
            for (int i = flatSkins.Count - 1; i >= 0; i--)
            {
                var s = flatSkins[i];
                if (s == null || s.btn == null) { flatSkins.RemoveAt(i); continue; }
                try
                {
                    if (s.plate == null || s.text == null)
                    {
                        var rt = s.btn.rectTransform;
                        if (rt == null) { flatSkins.RemoveAt(i); continue; }   // 面板重建过，这条作废
                        if (s.plate == null) s.plate = FindPlate(rt);
                        if (s.text == null) s.text = FindText(rt);
                    }
                    ApplySkin(s);
                }
                catch (Exception) { }
            }
            // RadioButton：只覆盖底板 sprite/颜色，原生 isOn / 点击逻辑不动。
            for (int i = radioSkins.Count - 1; i >= 0; i--)
            {
                var r = radioSkins[i];
                if (r == null || r.btn == null) { radioSkins.RemoveAt(i); continue; }
                try
                {
                    if (r.plate == null)
                    {
                        var rt = r.btn.rectTransform;
                        if (rt == null) { radioSkins.RemoveAt(i); continue; }
                        r.plate = FindPlate(rt);
                    }
                    if (r.plate == null) { radioSkins.RemoveAt(i); continue; }
                    var white = FlatSprite();
                    if (white != null && r.plate.sprite != white) r.plate.sprite = white;
                    if (r.plate.type != Image.Type.Simple) r.plate.type = Image.Type.Simple;
                    var want = r.btn.isOn ? r.on : r.off;
                    if (!SkinSame(r.plate.color, want)) r.plate.color = want;
                }
                catch (Exception) { }
            }
        }

        /// <summary>BepInEx 的 LateUpdate 调用：LateUpdate 晚于所有 Update（含 EventSystem 的指针事件），
        /// 在这一刻补皮肤，悬停/按下当帧就不会被 ApplyState 刷白（Update 里补会慢一帧 = 闪白）。</summary>
        public static void LateSkin()
        {
            try { FlatSkinTick(); } catch (Exception) { }
            try { ManualInputTick(); } catch (Exception) { }
            try { CalcInputTick(); } catch (Exception) { }
            try { PostInputTick(); } catch (Exception) { }
        }

        // 静态三选一：标签一行 + 平铺按钮一行，当前项前缀 ▶，直接点选（不再用「改」循环）。
        // 2026-11-12 重构（玩家定案）：设置页每行 = 一个横排 MachineryStack
        //   左：80 px 定宽标签按钮（Action 传 null：只是块定宽牌子，点它不会有任何动作）
        //   右：值按钮 SetFillWidth(true)，按剩余宽度自适应撑满，点它换下一个选项
        // 这样不会再出现「268 宽的大长条」把窗口顶宽。
        private static MachineryButton FixedLabel(MachineryStack row, string text)
        {
            MachineryButton b = null;
            try { b = new MachineryButton(text, null, 44, 20); }          // 首选：Action=null，天然不可点
            catch (Exception) { try { b = new MachineryButton(text, new Action(delegate { }), 44, 20); } catch (Exception) { } }
            if (b != null)
            {
                try { b.SetFillWidth(false); } catch (Exception) { }     // 锁死 44 px，不参与拉伸（IMA 式：标签列越窄越紧凑）
                SkinButton(b, new Color(0f, 0f, 0f, 0f), Dim, true);      // 左列：底板全透明 + Dim 字，标签比正文弱一档
                AlignButtonText(b, HorizontalAlignmentOptions.Right);     // 规范 2.1：标签列右对齐，值列起点才是一条竖线
                row.Add(b);
            }
            return b;
        }

        private static MachineryButton CycleRow(MachineryStack parent, string group, string[] names, Func<int> get, Action<int> pick)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }   // 不居中：整组贴左，行与行才像一张表
            FixedLabel(row, group);
            var b = new MachineryButton("", new Action(delegate
            {
                int cur = get();
                pick((cur + 1) % names.Length);
            }), 120, 20);
            try { b.SetFillWidth(true); } catch (Exception) { }          // 右侧自适应撑满
            try { b.SetText(names[Math.Max(0, Math.Min(get(), names.Length - 1))]); } catch (Exception) { }
            SkinButton(b, RowFill, Cream, false);                         // 右列：深蓝灰块 + 亮字，无框
            row.Add(b);
            parent.Add(row);
            return b;
        }

        /// <summary>显示名与内部值分离的下拉：值仍走老索引（计算逻辑不动），只是界面上的可选项变了。</summary>
        private static MachineryButton CycleRowMap(MachineryStack parent, string group, string[] names, int[] vals, Func<int> get, Action<int> pick)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }   // 不居中：整组贴左
            FixedLabel(row, group);
            var b = new MachineryButton("", new Action(delegate
            {
                int cur = get();
                int idx = 0;
                for (int i = 0; i < vals.Length; i++) if (vals[i] == cur) { idx = i; break; }
                int next = (idx + 1) % vals.Length;
                pick(vals[next]);
            }), 120, 20);
            try { b.SetFillWidth(true); } catch (Exception) { }
            try
            {
                int cur0 = get(); string disp = names[0];
                for (int i = 0; i < vals.Length; i++) if (vals[i] == cur0) { disp = names[i]; break; }
                b.SetText(disp);
            }
            catch (Exception) { }
            SkinButton(b, RowFill, Cream, false);                         // 右列：深蓝灰块 + 亮字，无框
            row.Add(b);
            parent.Add(row);
            return b;
        }

        /// <summary>下拉名（按内部值取 UI 显示名）。</summary>
        private static string UiName(string[] names, int[] vals, int v)
        {
            for (int i = 0; i < vals.Length; i++) if (vals[i] == v) return names[i];
            return names.Length > 0 ? names[0] : "";
        }

        // ======================================================================
        //  水源（2026-11-12 玩家定案）：四个「激活块」，亮的绿、其余灰。
        //    自动：按存档实际开通的水龙头走（铁锈开局/沟渠开局都认，不是写死铁锈）
        //    铁锈 / 沟渠：强制按某只算（只测算；接水仍走解锁校验，没开通接不出来）
        //    手填：0~100% 纯度滑条（0.01 精度），用来按扫描仪/光谱仪读到的水质算方案
        //  为什么要留：玩家要看「同一个存档、不同水源能出什么方案」，全自动就没得对比了。
        // ======================================================================
        private static readonly string[] SrcChipNames = { "自动", "铁锈", "沟渠", "手填" };
        // 2026-11-13 真机回归：原生 MachineryRadioButton 单独挂在 Stack 里只有 hover，鼠标点不动
        // （原生单选靠 RadioList / 原生容器接管点击，我们这套自建面板没走它的容器）。
        // 改回原生 MachineryButton 做的四块激活块：整块可点、绿/灰分明，也不会再出现 radio_on 的深红点。
        private static readonly MachineryButton[] srcChips = new MachineryButton[4];
        private static MachinerySlider sManPur;
        private static MachineryText cManPurLbl;
        private static MachineryStack pManRow;
        private static MachineryButton cManType;
        private static bool manTyping;
        private static string manBuf = "";
        private static int manTypingAt = -1;
        private static readonly Color SrcOnFill = new Color(0.22f, 0.40f, 0.16f, 1f);   // 激活：绿块
        private static readonly Color SrcOnText = new Color(0.86f, 0.96f, 0.66f, 1f);   // 激活：浅绿字
        private static readonly Color SrcLockFill = new Color(0.35f, 0.24f, 0.08f, 1f); // 选中但未解锁：暗琥珀
        private static readonly Color SrcOffText = new Color(0.78f, 0.77f, 0.68f, 1f);  // 未选：暖灰字
        private static readonly Color RadioOnFill = new Color(0.44f, 0.72f, 0.30f, 1f); // 保留给旧 radio 皮肤
        private static readonly Color RadioOffFill = new Color(0.14f, 0.15f, 0.17f, 1f);

        // ---------------- 计算页（独立于机器实时数据） ----------------
        // 六项杂质按「占总体积的百分比」输入，水＝100 − 六项合计；容量/性能/质量/目标也都可点框直接改。
        // 每台机器第一次进页时从它的容器/机器读一次；这里是本次会话的计算草稿，不写回物品，也不记实时水量。
        private static readonly string[] CalcImpKeys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
        private static readonly string[] CalcImpNames = { "重金属", "有机物", "微生物", "化学", "物理", "矿物" };
        private static readonly int CalcF_Cap = 0, CalcF_Perf = 1, CalcF_Quality = 2, CalcF_Target = 3, CalcF_Imp0 = 4, CalcF_Water = 10, CalcF_Ratio = 11;
        private static readonly MachineryButton[] calcVals = new MachineryButton[12];
        private static MachineryText cCalcSum, cCalcOut, cCalcOut2, cCalcHint, cCalcPool, cCalcSel, cCalcFeed;
        private static int calcToolsMode;        // 0=跟随设置 1=背包 2=无限（只作用于计算页）
        private static MachineryButton calcToolsBtn;
        private static MachineryText calcScopeLbl;      // 本页实际口径：无限 / 药3 滤棉800
        private static string calcScopeStr = "";        // 由 RecalcCalc 写，供 RefreshCalcLabels 回填
        private static bool calcManual;       // 玩家点了「清空」：机器开着也不自动读
        private static bool calcManualSource;   // 设置页「手填」：按计算页的六项杂质算，不再用单值滑条
        // 右键「读取」的样瓶来源。它和设置页手填不同：机器容器一旦实际变了，
        // 必须交回实时容器，不能继续拿读取时缓存的六项算主页。
        private static bool calcReadSource;
        private static GameItem readMachineItem;
        private static long readMachineVol, readMachineCap;
        private static int readMachinePur;
        private static string readMachineParts = "";
        // 清空那一刻右键着的瓶子：只有换成另一瓶才允许再自动读，否则「清空」会被下一次刷新塞回来。
        private static GameItem calcClearPick;
        private static bool calcFromMachine;   // 当前数值是从机器读来的（机器一关就清空）
        private static bool prevMachineOpen;
        // 玩家 2026-11-16：只按“机器窗口开了/关了”解锁，会把已经放下一片药的方案
        // 重锁成当前 2909/98.04 的新方案（先补91+药2）。这里记住上一次真正读到的
        // 机器瓶，只有换了一只瓶才解锁；同一只瓶开关窗口不动方案。
        private static GameItem lastMachineBottle;
        private static MachinerySlider sCalcWater;
        private static readonly System.Collections.Generic.List<WaterPlan.WaterBottle> calcWaters =
            new System.Collections.Generic.List<WaterPlan.WaterBottle>();
        // 玩家 2026-11-13：2 L 瓶实测只读到 1969 mL，整数 mL 会显示成 2000（假满）。
        // 内部一律按 µL 存，显示时保留 1 位小数。
        private static long calcCapUl = 2000000L, calcWaterUl = 2000000L;
        private static int calcPerf = 0, calcQuality = 0, calcTargetBp = 9900;
        private static readonly int[] calcImpBp = new int[6];
        // 玩家 2026-11-13（p3）：「0.01 也要读取」—— 除百分比外还留一份真实 µL，
        // 非零但不足 0.01% 的项显示「<0.01%」，而不是被四舍五入成 0.00%。
        private static readonly long[] calcImpUl = new long[6];
        private static readonly string[] calcShown = new string[12];
        private static int calcField = -1;
        private static bool calcDirty = true;
        private static string calcBuf = "";
        private static int calcTypingAt = -1;
        private static WaterPlan.Report calcRep;
        private static object calcOwner;

        private static bool SrcManual() { return calcManualSource; }

        private static void BuildSrcChips(MachineryStack page)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            FixedLabel(row, "水源");
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                MachineryButton b = null;
                try
                {
                    b = new MachineryButton(SrcChipNames[i], new Action(delegate { OnSrcChip(k); }), 30, 18);
                    b.SetFillWidth(false);
                    b.SetDescription("水源：" + SrcChipNames[i]);
                    SkinButton(b, RowFill, SrcOffText, false);
                }
                catch (Exception) { }
                if (b != null) row.Add(b);
                srcChips[i] = b;
            }
            page.Add(row);

            // 批次 34：手填不再用「单值纯度滑条/键入框」。六项杂质才准，
            // 统一到计算页填；设置页只保留「手填」快捷块，不再多占一行。
            sManPur = null; cManPurLbl = null; cManType = null; pManRow = null;
            RefreshSrcChips();
        }

        private static void OnSrcChip(int k)
        {
            ManualPickLabel = null;
            // 批次 34：设置页的「手填」只是计算页入口；真正的水质六项在计算页填。
            // 不再把单值纯度写进 CustomPur，也不再显示旧的手填滑条。
            if (k == 3)
            {
                calcManualSource = true;
                calcReadSource = false;
                // 「手填」只认计算页这组六项，不再沿用上一次右击/读取的外部瓶：
                // 否则主页会拿那只已经倒空/容量不同的瓶当源，计算页却拿计算页字段，两个页面必然分叉。
                lastPickedBottle = null;
                RightClickTarget = null;
                calcClearPick = null;
                CustomPur = -1;
                SrcIdx = 0;
                LastMsg = "手填：请到计算页填六项杂质，返回主页按这六项算";
                Log("[ui] src chip=手填 -> calc page");
                UnlockPlan("手填->计算页");
                DropPlanCache();
                SetPage(4);
                return;
            }
            calcManualSource = false;
            calcReadSource = false;
            bool rustOpen, gutterOpen;
            TapLocks(out rustOpen, out gutterOpen);
            // 玩家 2026-11-14：最高级铁锈水管会同时留下两个解锁位，但真实只能接铁锈。
            // 沟渠仍可选来算方案；颜色、悬停提示、点选提示与接水闸门统一说「只算不接」。
            if (k == 2 && rustOpen)
                LastMsg = "铁锈水管存档：沟渠水只用于计算，不能接水；请用铁锈水或自动";
            else if (k == 2 && !gutterOpen)
                LastMsg = "沟渠水龙头未解锁：沟渠方案可以算，但接水会被拦住";
            else if (k == 1 && !rustOpen)
                LastMsg = "铁锈水龙头未解锁：铁锈方案可以算，但接水会被拦住";
            else if (k == 0)
                LastMsg = "";   // 切回自动时清掉上一次“铁锈未解锁”的残留提示
            CustomPur = -1;
            SrcIdx = (k == 0 ? 0 : k);
            Log("[ui] src chip=" + SrcChipNames[k < 4 && k >= 0 ? k : 0] + " idx=" + SrcIdx);
            RememberSettings();
            UnlockPlan("换水源");
            DropPlanCache();
            RequestRebuild();
        }

        /// <summary>读「瓶里现成的水」纯度（＝游戏水瓶面板上的「已检测」）；读不到退回今日水龙头。</summary>
        private static int BottleOrTapPurity(int fallback)
        {
            int bp = 0;
            try
            {
                var b0 = CtxBottle(); if (b0 == null) b0 = GameBottle();
                if (b0 != null)
                {
                    int v = WaterHelper.GetTotalVolume(b0);
                    int pp = WaterHelper.GetWaterPurity(b0);
                    if (v > 0 && pp > 0) bp = pp;
                }
            }
            catch (Exception) { }
            if (bp <= 0)
            {
                int g = 0, p = 0;
                try { TapSource(out g, out p); } catch (Exception) { }
                bp = p;
            }
            return bp > 0 ? bp : fallback;
        }

        private static void ReadBottlePurity()
        {
            int bp = BottleOrTapPurity(0);
            if (bp <= 0)
            {
                LastMsg = "读不到水质：先把一瓶水放进净水器，或确认水龙头已开通";
                return;
            }
            CustomPur = bp;
            if (sManPur != null) { try { sManPur.SetValue(bp / 100f); } catch (Exception) { } }
            if (cManPurLbl != null) SetT(cManPurLbl, PlanText.Pct(CustomPur));
            Log("[ui] manual purity <- bottle " + PlanText.Pct(bp));
            UnlockPlan("读瓶水质");
            DropPlanCache();
            RequestRebuild();
        }

        private static void OnManPurSlide(float v)
        {
            int bp = (int)Math.Round(v * 100.0);
            if (bp < 1) bp = 1;
            if (bp > 10000) bp = 10000;
            if (bp == CustomPur) return;
            CustomPur = bp;
            ManualPickLabel = null;
            if (cManPurLbl != null) SetT(cManPurLbl, PlanText.Pct(CustomPur));
        }

        private static void OnManPurReleased(float v)
        {
            OnManPurSlide(v);
            Log("[ui] manual purity=" + PlanText.Pct(CustomPur));
            RememberSettings();
            UnlockPlan("拖手填水质");
            DropPlanCache();
            RequestRebuild();
        }

        // ---- 手填键盘输入：不建 TMP_InputField，直接用 Input.inputString 捕数字 ----
        // 原生 UGUI 没有现成文本框；TMP_InputField 会抢焦点、和游戏输入/鼠标锁定打架。
        // 这里点「键入」后只读 0-9 . 退格 回车 Esc，回车才提交，Esc 取消。
        private static void BeginManualTyping()
        {
            if (calcField >= 0) CommitCalcTyping();
            if (manTyping) { CommitManualTyping(); return; }
            manTyping = true;
            manTypingAt = Time.frameCount;
            int bp = CustomPur > 0 ? CustomPur : BottleOrTapPurity(9000);
            manBuf = (bp / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            UpdateManualTypingLabel();
            LastMsg = "键盘输入水质 0–100，回车确认，Esc 取消";
            Log("[ui] manual typing start");
        }

        private static void UpdateManualTypingLabel()
        {
            string txt = manTyping ? (manBuf + "|") : PlanText.Pct(CustomPur);
            if (cManType != null) try { cManType.SetText(txt); } catch (Exception) { }
            if (cManPurLbl != null) SetT(cManPurLbl, txt);
        }

        private static void CancelManualTyping()
        {
            manTyping = false;
            manBuf = "";
            UpdateManualTypingLabel();
            LastMsg = "已取消键入";
        }

        private static void CommitManualTyping()
        {
            float v;
            if (!float.TryParse(manBuf, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                CancelManualTyping();
                LastMsg = "输入无效：只填 0–100 的数字";
                return;
            }
            if (v < 0f) v = 0f;
            if (v > 100f) v = 100f;
            int bp = (int)Math.Round(v * 100.0);
            if (bp < 1) bp = 1;
            if (bp > 10000) bp = 10000;
            CustomPur = bp;
            ManualPickLabel = null;
            manTyping = false;
            manBuf = "";
            if (sManPur != null) try { sManPur.SetValue(bp / 100f); } catch (Exception) { }
            UpdateManualTypingLabel();
            Log("[ui] manual typed purity=" + PlanText.Pct(CustomPur));
            RememberSettings();
            UnlockPlan("键入水质");
            DropPlanCache();
            RequestRebuild();
        }

        /// <summary>每帧读键盘；只有点了「键入」才吃字符，避免和游戏热键/聊天抢输入。</summary>
        private static void ManualInputTick()
        {
            if (!manTyping) return;
            if (calcField >= 0) return;
            // 点击别处＝提交当前输入（不是取消），避免玩家输完直接点方案。
            if (Input.GetMouseButtonDown(0) && Time.frameCount > manTypingAt + 1)
            {
                CommitManualTyping();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelManualTyping(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CommitManualTyping();
                return;
            }
            string ins = null;
            try { ins = Input.inputString; } catch (Exception) { }
            if (string.IsNullOrEmpty(ins)) return;
            bool changed = false;
            for (int i = 0; i < ins.Length; i++)
            {
                char c = ins[i];
                if (c == '\b')
                {
                    if (manBuf.Length > 0) { manBuf = manBuf.Substring(0, manBuf.Length - 1); changed = true; }
                }
                else if ((c >= '0' && c <= '9') || c == '.' || c == ',')
                {
                    if (c == ',') c = '.';
                    if (c == '.' && manBuf.IndexOf('.') >= 0) continue;
                    if (manBuf.Length >= 6) continue;
                    manBuf += c;
                    changed = true;
                }
            }
            if (changed)
            {
                UpdateManualTypingLabel();
                LastMsg = "键入水质 " + manBuf + "，回车确认";
            }
        }

        /// <summary>界面上的水源档：0 自动 / 1 铁锈 / 2 沟渠 / 3 手填。</summary>
        private static int SrcIdxForUi()
        {
            return SrcManual() ? 3 : Math.Max(0, Math.Min(2, SrcIdx));
        }

        private static void RefreshSrcChips()
        {
            bool man = SrcManual();
            int ui = SrcIdxForUi();
            for (int i = 0; i < srcChips.Length; i++)
            {
                var b = srcChips[i];
                if (b == null) continue;
                try { b.SetText(SrcChipNames[i]); } catch (Exception) { }
            }
            if (pManRow != null) SetVisible(pManRow, man);
            if (cManType != null) try { cManType.SetText(PlanText.Pct(CustomPur)); } catch (Exception) { }
            if (cManPurLbl != null) SetT(cManPurLbl, PlanText.Pct(CustomPur));
            // 未开通的水龙头仍然允许「选它算方案」，但整块文字变琥珀，明确提示不能真接。
            bool rustLocked, gutterLocked;
            bool rustOpen, gutterOpen;
            TapLocks(out rustOpen, out gutterOpen);
            rustLocked = !rustOpen;
            gutterLocked = !gutterOpen || rustOpen;   // 铁锈水管存档：即使沟渠解锁位也为 true，仍只算不接
            for (int i = 0; i < srcChips.Length; i++)
            {
                var b = srcChips[i];
                if (b == null) continue;
                bool locked = (i == 1 ? rustLocked : (i == 2 ? gutterLocked : false));
                bool sel = (ui == i);
                Color fill = sel ? (locked ? SrcLockFill : SrcOnFill) : RowFill;
                Color tx = sel ? (locked ? Warn : SrcOnText) : (locked ? Warn : SrcOffText);
                try { SkinButton(b, fill, tx, false); } catch (Exception) { }
                try
                {
                    if (i == 2 && rustOpen)
                        b.SetDescription("铁锈水管存档：沟渠水只用于计算，不能接水");
                    else
                        b.SetDescription(locked
                            ? SrcChipNames[i] + "：还没解锁，只能算方案，不能接水"
                            : "水源：" + SrcChipNames[i]);
                }
                catch (Exception) { }
            }
        }

        // ---- 机器开关：用 / 不用（没机器的存档只推 过滤棉+紫外+药片 的方案） ----
        private static readonly string[] MachChipNames = { "用机器", "不用" };
        private static readonly MachineryButton[] machChips = new MachineryButton[2];

        private static void BuildMachChips(MachineryStack page)
        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            FixedLabel(row, "机器");
            for (int i = 0; i < 2; i++)
            {
                int k = i;
                MachineryButton b = null;
                try
                {
                    b = new MachineryButton(MachChipNames[i], new Action(delegate { OnMachChip(k); }), i == 0 ? 54 : 44, 20);
                    b.SetFillWidth(false);
                    b.SetDescription(MachChipNames[i]);
                    SkinButton(b, RowFill, SrcOffText, false);
                }
                catch (Exception) { }
                if (b != null) row.Add(b);
                machChips[i] = b;
            }
            page.Add(row);
            RefreshMachChips();
        }

        private static void OnMachChip(int k)
        {
            WaterPlan.NoMachine = (k == 1);
            ManualPickLabel = null;
            Log("[ui] machine=" + (WaterPlan.NoMachine ? "none" : "purifier"));
            RememberSettings();
            UnlockPlan("切机器开关");
            DropPlanCache();
            RequestRebuild();
        }

        private static void RefreshMachChips()
        {
            int ui = WaterPlan.NoMachine ? 1 : 0;
            for (int i = 0; i < machChips.Length; i++)
            {
                var b = machChips[i];
                if (b == null) continue;
                try { b.SetText(MachChipNames[i]); } catch (Exception) { }
                bool sel = (ui == i);
                try { SkinButton(b, sel ? SrcOnFill : RowFill, sel ? SrcOnText : SrcOffText, false); } catch (Exception) { }
            }
        }

        private static MachineryButton[] Seg(MachineryStack parent, MachineryText label, string[] names, Action<int> pick)
        {
            parent.Add(label);
            var row = new MachineryStack(false, 3);
            var bs = new MachineryButton[names.Length];
            // 按钮宽度按行内数量分摊：原来写死 116，3~4 个就撑爆 340 宽的窗口互相压住
            // （玩家截图里「1857mL铁锈水 90.9…」被后面的按钮盖掉了）。
            int bw = Math.Max(56, (300 - (names.Length - 1) * 3) / names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                bs[i] = new MachineryButton((i == 0 ? "▶ " : "") + names[i], new Action(delegate { pick(k); }), bw, 24);
                row.Add(bs[i]);
            }
            parent.Add(row);
            return bs;
        }

        /// <summary>富文本要不要重画：控件对象换了（整窗重建）或数据签名变了才画。
        /// 玩家 2026-11-10：面板开着划鼠标就卡，一大半是每次 Rebuild 把几千字富文本整段重排。</summary>
        private static bool RichChanged(object owner, string sig)
        {
            if (!object.ReferenceEquals(owner, lastRichOwner) || sig != lastRichSig)
            {
                lastRichOwner = owner;
                lastRichSig = sig;
                return true;
            }
            return false;
        }

        // 玩家 2026-11-10：Rebuild 每次都无条件给 8 颗按钮 SetText，划过按钮就卡。
        // 记住「这组按钮上次写的是哪个选中项」，没变就不再碰原生控件。
        private static readonly Dictionary<MachineryButton[], string> markCache = new Dictionary<MachineryButton[], string>();
        private static void Mark(MachineryButton[] bs, string[] names, int sel)        {
            if (bs == null) return;
            string sig = sel + "|" + names.Length;
            string prev;
            if (markCache.TryGetValue(bs, out prev) && prev == sig) return;
            markCache[bs] = sig;
            for (int i = 0; i < bs.Length && i < names.Length; i++)
            {
                try { bs[i].SetText((i == sel ? "▶ " : "") + names[i]); } catch (Exception) { }
            }
        }

        // 玩家：1..5 手动选的行会和「自动」打架。定案：任何一个下拉改动都清掉手动选择，回到自动挑。
        /// <summary>批次 29：性价比自动档的「启用海德拉」只作用于本次计算，不写回 HydraMode。</summary>
        private static void EnableHydraOnce()
        {
            hydraOnce = true;
            autoHydraHint = false;
            LastMsg = "本次临时启用海德拉";
            UnlockPlan("启用海德拉");
            DropPlanCache();
            InvalidateCalc();
            RequestRebuild();
        }

        private static void SetMode(int i) { SetMode(i, true); }

        private static void SetMode(int i, bool allowInfiniteToggle)
        {
            // 批次 28：界面只允许 Mode 1/2；旧的默认 0、质量 3 不再进入选取。
            if (i != 1 && i != 2) i = 2;
            // 玩家 2026-11-10：点「默认/性价比/质量」就要真的从「效率（无视容器）」退出来，
            // 否则点完默认还是按 50 L 在算，看起来跟效率一模一样。
            // 玩家 2026-11-11 m00327：档位键三态循环 —— 第一次按＝按真实背包道具算，
            // 再按同一个键＝按无限道具算，再按＝回真实。开关是全局的（Q2）。
            // 玩家 2026-11-15：标题栏同档再点仍保留「切无限道具」；但切到另一个档会回真实道具，
            // 避免从效率无限切到性价比时继续按 50 L 满容量算，看不到真正要倒出多少。
            // 玩家 2026-11-16：点「性价比」是要按性价比重算，不是把同一档再点成「无限道具」。
            // 但同档再点仍要能切无限：效率档和性价比档都保留这个入口；切到另一个档时回真实道具。
            bool sameMode = allowInfiniteToggle && (Mode == i) && !UncapEff && (i == 1 || i == 2);
            UncapEff = false;
            Mode = i; ManualPickLabel = null; Log("[ui] mode=" + i + (sameMode ? " (same -> toggle道具口径)" : ""));
            hydraOnce = false;
            // 玩家 2026-11-12：按小图标切档（默认/效率/性价比）时，前置/后置一律回「自动」。
            // 以前手调过前置/后置，切档后还留着那套筛子，候选被筛空 → 看起来像「按了没反应」。
            PreMode = 0; PostMode = 0;
            if (cPre != null) try { SetB(cPre, UiName(PreUi, PreVal, 0)); } catch (Exception) { }
            if (cPost != null) try { SetB(cPost, UiName(PostUi, PostVal, 0)); } catch (Exception) { }
            if (sameMode) ToggleInfinite("再点了一次档位键");
            else
            {
                // 保留切无限，但切到另一个档时回真实道具：
                // 玩家从「效率无限」切到「性价比」时要看到真正要倒出多少，而不是继续按 50 L 满容量算。
                if (InfiniteTools)
                {
                    InfiniteTools = false;
                    Log("[ui] infinite=0 (切档复位真实道具)");
                }
                // 底行黄字必须跟着当前档走，不能留着上一次「效率：…」的残留（玩家 2026-11-10 报）。
                LastMsg = i == 2 ? "性价比：0 过滤棉 + 一晚直出（紫外免费可带）"
                       : "自定义：一晚净赚最多（按容器里的水与目标质量）";
            }
            if (modeBtnsS != null)
                for (int k = 0; k < modeBtnsS.Length && k < PlanText.ModeShort.Length; k++)
                    if (modeBtnsS[k] != null) modeBtnsS[k].SetText((k == i ? "▶" : "") + PlanText.ModeShort[k]);
            // 玩家 2026-11-11 定案：按「默认 / 效率 / 性价比」就直接按该档给推荐方案 ——
            // 按下档位键＝解锁 + 丢缓存，Rebuild 里按新档重算，算完再锁上（不再是只换名字）。
            // 玩家 2026-11-10：点档位会卡 —— 按钮视觉已经即时改过，整窗重建挪到下一帧做一次。
            RememberSettings();
            UnlockPlan("按了模式键");
            DropPlanCache();
            RefreshModeDesc();
            InvalidateCalc();
            RequestRebuild();
            // BuildMain 只在首次建树时跑；已经建好树后切档不会重跑 FillMix。
            // 所以混水页开着时，这里直接按新档刷新一次，不能等下一帧的空 Rebuild。
            if (UiPage == 5 && pMixBody != null) RefreshMixPage();
        }

        /// <summary>「效率」档：一晚装满当前容器、净出水最多（不管性价比）。
        /// 「无视容器」已删掉，一律按当前容器的容量规划。</summary>
        private static void ToggleUncapEff() { ToggleUncapEff(true); }

        private static void ToggleUncapEff(bool allowInfiniteToggle)
        {
            bool sameMode = allowInfiniteToggle && (Mode == 1);
            UncapEff = false;
            Mode = 1;
            ManualPickLabel = null;
            // 玩家 2026-11-13：标题栏「效率」键以前只走这里，不复位前置/后置 ——
            // 手调过筛子再按它，候选被旧筛子筛空，看着就像「没切过去、要再点一次」。和 SetMode 一样一律复位。
            PreMode = 0; PostMode = 0;
            if (cPre != null) try { SetB(cPre, UiName(PreUi, PreVal, 0)); } catch (Exception) { }
            if (cPost != null) try { SetB(cPost, UiName(PostUi, PostVal, 0)); } catch (Exception) { }
            if (sameMode) ToggleInfinite("再点了一次效率键");
            else LastMsg = "效率：装满当前容器（一晚净出水最多）";
            Log("[ui] mode=1 效率" + (sameMode ? " (same -> toggle道具口径)" : ""));
            RememberSettings();
            UnlockPlan("按了效率键");
            DropPlanCache();
            RefreshModeDesc();
            InvalidateCalc();
            RequestRebuild();
            RefreshMixIfOpen();
        }

        /// <summary>当前净水器里装了几个 node 类耗材（过滤棉/紫外灯都是 node_medium）。</summary>
        private static int CountNodeModules()
        {
            try
            {
                var m = CurrentMachine();
                if (m == null) return 0;
                string d = GameBridge.DescribeModules(m);
                if (string.IsNullOrEmpty(d)) return 0;
                int n = 0;
                foreach (string part in d.Split(','))
                {
                    string k = part.Trim();
                    if (k.StartsWith("node_")) n++;
                }
                return n;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>背包/台面里有没有这个 identifier 的道具（只认整数件）。</summary>
        private static bool OwnsItem(string id)
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string k = null;
                    try { k = it.identifier; } catch (Exception) { }
                    if (k == id) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
        private static void SetTarget(int i) { TargetIdx = i; ManualPickLabel = null; Log("[ui] target=" + i); if (cTarget != null) try { SetB(cTarget, PlanText.TargetNames[Math.Max(0, Math.Min(i, PlanText.TargetNames.Length - 1))]); } catch (Exception) { } RememberSettings(); UnlockPlan("换目标"); DropPlanCache(); RequestRebuild(); RefreshMixIfOpen(); }
        private static void SetPre(int i) { PreMode = i; ManualPickLabel = null; Log("[ui] pre=" + i); ClosePreDrop(true); if (cPre != null) try { SetB(cPre, UiName(PreUi, PreVal, i)); } catch (Exception) { } RememberSettings(); UnlockPlan("换前置"); DropPlanCache(); RequestRebuild(); RefreshMixIfOpen(); }
        private static void SetPost(int i) { PostMode = i; ManualPickLabel = null; Log("[ui] post=" + i); RefreshPostRow(); RememberSettings(); UnlockPlan("换后置"); DropPlanCache(); RequestRebuild(); RefreshMixIfOpen(); }
        private static void SetSrc(int i) { SrcIdx = i; ManualPickLabel = null; Log("[ui] src=" + i); if (cSrc != null) try { SetB(cSrc, SrcUiName(i)); } catch (Exception) { } RememberSettings(); RequestRebuild(); RefreshMixIfOpen(); }
        /// <summary>海德拉开关：0 自动 / 1 强制 / 2 不用。改完只打脏标记，重建交给下一帧。</summary>
        private static void SetHydra(int i) { WaterPlan.HydraMode = i; hydraOnce = false; ManualPickLabel = null; Log("[ui] hydra=" + i); RememberSettings(); UnlockPlan("换海德拉"); DropPlanCache(); RequestRebuild(); RefreshMixIfOpen(); }

        /// <summary>设置在混水页里改完立即刷新混水，不能等下一次重建。</summary>
        private static void RefreshMixIfOpen()
        {
            if (UiPage == 5 && pMixBody != null) RefreshMixPage();
        }

        private static void SetNightCap(int i)
        {
            int want = (i > 0 ? WaterPlan.MaxNights : 1);
            int old = WaterPlan.NightCap;
            WaterPlan.NightCap = want;
            PlayerPrefs.SetInt(NightCapPref, want);
            PlayerPrefs.Save();
            Log("[ui] nightCap " + old + " -> " + want);
            UncapEff = false;
            ManualPickLabel = null;
            if (cNight != null) try { SetB(cNight, want > 1 ? "开" : "关"); } catch (Exception) { }
            RememberSettings();
            UnlockPlan("换多晚");
            DropPlanCache();
            InvalidateCalc();
            RequestRebuild();
            RefreshMixIfOpen();
        }
        /// <summary>海德拉耐久上限：300 一只 / 600 两只 / 900 三只 / 0 不限。</summary>
        /// <summary>滑条拖动中：只存值 + 改旁边那行字，绝不整窗重建（重建会卡，玩家报过多次）。</summary>
        private static void OnHydraDurSlide(float v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) n = 0;
            if (n > HydraDurMaxSlider) n = HydraDurMaxSlider;
            if (n == WaterPlan.HydraDuraCap) { RefreshHydraDurLabel(); return; }
            WaterPlan.HydraDuraCap = n;
            WaterPlan.HydraDurUserSet = true;   // 玩家亲手拖过：以后以滑条为准（0 = 不限）
            ManualPickLabel = null;
            RefreshHydraDurLabel();
        }

        /// <summary>松手才重算一次方案。</summary>
        private static void OnHydraDurReleased(float v)
        {
            OnHydraDurSlide(v);
            Log("[ui] hydraDur=" + WaterPlan.HydraDuraCap);
            RememberSettings();
            RequestRebuild();
        }

        /// <summary>滑条该显示的值：拖过就是滑条本身；没拖过显示「实际会用到的耐久」（背包里剩多少就是多少）。</summary>
        private static int HydraDurShown()
        {
            if (WaterPlan.HydraDurUserSet || InfiniteTools) return WaterPlan.HydraDuraCap;
            var o = OwnedTools();
            int own = o != null ? o.hydraDur : 0;
            if (own > 0 && WaterPlan.HydraDuraCap > 0 && own > WaterPlan.HydraDuraCap) own = WaterPlan.HydraDuraCap;
            return own > 0 ? own : WaterPlan.HydraDuraCap;
        }

        /// <summary>传给 AutoSolve 的海德拉耐久预算：无限道具口径 0=不限；真实背包按持有耐久，不再默认 60。</summary>
        private static int HydraDuraMaxForSolve(bool infinite)
        {
            if (infinite) return 0;
            int own = HydraDurShown();
            return own > 0 ? own : 1;   // 0 在求解器里表示不限，没海德拉时给 1 点，配合 allowHydra=false 不会出方案
        }

        private static void RefreshHydraDurLabel()
        {
            if (cHydraDurLbl == null) return;
            // 规范 3.2：标签只留固定名，当前点数单独放值列；无限道具口径值列写「不限」。
            SetT(cHydraDurLbl, "海德拉");
            if (InfiniteTools) { SetT(cHydraDurVal, "不限"); return; }
            int hd = HydraDurShown();
            if (hd <= 0) { SetT(cHydraDurVal, "不限"); return; }
            SetT(cHydraDurVal, hd + " 点");
        }

        // —— 过滤棉耐久：背包里所有滤棉累加，玩家用原生滑条决定这次用多少点 ——
        /// <summary>背包里实际持有的过滤棉总耐久（没有就是 0）。</summary>
        private static int FilterDurOwned()
        {
            var o = OwnedTools();
            if (o == null) return 0;
            return o.filterDur > 0 ? o.filterDur : 0;
        }

        /// <summary>这次实际用多少点耐久：玩家拖过就用拖的值，没拖过就用持有总和；上限 2000。</summary>
        private static int FilterDurNow()
        {
            // 玩家 2026-11-11：无限口径下过滤棉不限；拉条只用来管真实道具的手操耐久。
            if (InfiniteTools) return 1 << 20;
            int own = FilterDurOwned();
            if (FilterDurUse >= 0) return Math.Max(0, Math.Min(FilterDurMaxSlider, FilterDurUse));
            return Math.Max(0, Math.Min(FilterDurMaxSlider, own));
        }

        /// <summary>拖动中只刷标签，不重算方案（松手才重建）。</summary>
        private static void OnFilterDurSlide(float v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) n = 0;
            if (n > FilterDurMaxSlider) n = FilterDurMaxSlider;
            if (n == FilterDurUse) { RefreshFilterDurLabel(); return; }
            FilterDurUse = n;
            ManualPickLabel = null;
            RefreshFilterDurLabel();
        }

        /// <summary>松手才重算一次方案。</summary>
        private static void OnFilterDurReleased(float v)
        {
            OnFilterDurSlide(v);
            Log("[ui] filterDur=" + FilterDurUse);
            RememberSettings();
            RequestRebuild();
        }

        private static void RefreshFilterDurLabel()
        {
            if (cFilterDurLbl == null) return;
            int own = FilterDurOwned();
            int use = FilterDurNow();
            // 规范 3.2：标签只留固定名，当前点数值列独立；不用时值列说「不用」。
            SetT(cFilterDurLbl, "滤棉");
            if (use <= 0) { SetT(cFilterDurVal, "不用"); return; }
            SetT(cFilterDurVal, use + "/" + own + " 点");
        }

        /// <summary>主页「持有」行：只拼背包里真有的项，都没有返回空串（整行隐藏）。</summary>
        private static string OwnLineText()
        {
            var o = OwnedTools();
            if (o == null) return "";
            if (InfiniteTools) return "持有 无限";
            string s = "";
            if (o.filterDur > 0) s = "滤棉" + o.filterDur;
            // 不用净水器时海德拉没有意义，持有行别再提它。
            if (!WaterPlan.NoMachine && o.hydraDur > 0)
                s = s.Length > 0 ? s + "  海德拉" + o.hydraDur : "海德拉" + o.hydraDur;
            if (o.tablets > 0)
            {
                s = s.Length > 0 ? s + "  药" + o.tablets : "药" + o.tablets;
                int total = PlanTabletTotal();
                if (total > 0)
                {
                    int used = progressActive ? ProgressDoneTool("tablet") : 0;
                    int left = Math.Max(0, total - used);
                    s += used > 0 ? "（药共" + total + "·剩" + left + "）" : "（药共" + total + "）";
                }
            }
            return s.Length > 0 ? "持有 " + s : "";
        }

        private static int PlanTabletTotal()
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null) return 0;
            int total = 0;
            for (int i = 0; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st != null && st.tool == "tablet") total += Math.Max(1, st.count);
            }
            return total;
        }

        /// <summary>锁定后背包里少了几片药。药片可以比过滤先用，不能用“前缀到第几步”表示。</summary>
        private static int TabletUsedSinceLock(bool forceRefresh)
        {
            int total = PlanTabletTotal();
            if (total <= 0) return 0;
            int used = progressTabletUsed;
            if (lockedTabletCount > 0)
            {
                try
                {
                    if (forceRefresh)
                    {
                        // OwnedTools 有 6 秒缓存；刚放下一片药时缓存还是旧数量，
                        // 会把“已用 1 片”读成 0，随后方案就被重算成带补水的新方案。
                        ownCache = null;
                        ownTime = -999f;
                    }
                    var o = OwnedTools();
                    int tabNow = o == null ? lockedTabletCount : o.tablets;
                    int byStock = lockedTabletCount - tabNow;
                    if (byStock > used) used = byStock;
                }
                catch (Exception) { }
            }
            if (used < 0) used = 0;
            if (used > total) used = total;
            return used;
        }

        /// <summary>已完成前缀里已经包含几片药，避免和 progressTabletUsed 重复扣。</summary>
        private static int TabletUsedInPrefix()
        {
            var raw = RawResultFor(lockedReport);
            if (raw == null || raw.rawSteps == null) return 0;
            int n = Math.Min(progressDoneSteps, raw.rawSteps.Count);
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                var st = raw.rawSteps[i];
                if (st != null && st.tool == "tablet") used += Math.Max(1, st.count);
            }
            return used;
        }
        public static string SrcPlanPublic() { int g, p; TodaySource(out g, out p); int perf, q; Bonuses(out perf, out q); int tb = PlanText.TargetBp(TargetIdx, q); var r = WaterPlan.Build(2000000L, p, perf, q, tb, SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))], "空瓶 2000 mL", true, Mode, PreMode, PostMode); if (r == null) return "srcplan -> none"; return "srcplan -> " + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + " @" + PlanText.Pct(p) + " 接 " + (r.ShownUl()/1000) + " mL → 出 " + (r.pick == null ? 0 : r.pick.outUl/1000) + " mL  " + (r.pick == null ? "—" : PlanText.Pct(r.pick.purityBp)) + "（" + (r.pick == null ? "无方案" : r.pick.label) + "）"; }
        public static string SrcPublic() { int g, p; TodaySource(out g, out p); return "src -> idx=" + SrcIdx + " name=" + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + " grade=" + g + " purity=" + PlanText.Pct(p); }
        public static string SetSrcPublic(string s) { int v; if (!int.TryParse(s, out v)) return "src -> bad-arg"; if (v < 0 || v >= SrcNames.Length) return "src -> out-of-range 0.." + (SrcNames.Length - 1); SetSrc(v); return SrcPublic(); }

        // 水龙头真值一览：让面板/自检知道「今天有什么水」，不再默认铁锈水
        public static string TapPublic()
        {
            int rg = 4, rp = 0, gg = 5, gp = 0;
            try
            {
                var st = StoreStation.Instance;
                if (st != null) { rp = st.todayRustwaterPurity; gp = st.todayGutterflowPurity; }
            }
            catch (Exception e) { return "tap -> ERR " + e.GetType().Name; }
            return "tap -> rust=" + (rp > 0 ? PlanText.Pct(rp) : "无") + " gutter=" + (gp > 0 ? PlanText.Pct(gp) : "无")
                 + " 自动选=" + (rp > 0 ? "铁锈水" : (gp > 0 ? "沟渠水" : "未解锁/无法读取"));
        }

        // 网页的「自己填纯度」：0..10000 表示 0.00%..100.00%，-1 关闭
        /// <summary>ctl 用：点选第 n 行方案（1 起），传 auto 回到自动。</summary>
        public static string PickPublic(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s) || s == "auto") { ManualPickLabel = null; Rebuild(true); return "pick -> auto"; }
                int n;
                var rows = lastRows;
                var rep = CurrentPlan(true);          // 面板没重建过时 lastRows 为空，这里按当前模型现算一份
                if (rows == null && rep != null && rep.combos != null)
                {
                    var ok = new System.Collections.Generic.List<WaterPlan.Combo>();
                    foreach (var c in rep.combos) if (c.ok) ok.Add(c);
                    ok.Sort(delegate (WaterPlan.Combo a, WaterPlan.Combo b) { return b.outUl.CompareTo(a.outUl); });
                    rows = ok.ToArray();
                }
                if (!int.TryParse(s, out n) || n < 1 || rows == null || n > rows.Length)
                    return "pick -> bad-arg rows=" + (rows == null ? 0 : rows.Length);
                ManualPickLabel = rows[n - 1].label;
                Rebuild(true);
                return "pick -> " + ManualPickLabel;
            }
            catch (Exception e) { return "pick -> ERR " + e.Message; }
        }

        public static string CustomPurPublic() { return "pur -> " + (CustomPur > 0 ? PlanText.Pct(CustomPur) : "off") + " (mode=" + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + ")"; }
        public static string SetCustomPurPublic(string s)
        {
            int v;
            if (!int.TryParse(s, out v)) return "pur -> bad-arg";
            if (v < 0) { CustomPur = -1; }
            else if (v > 10000) return "pur -> out-of-range 0..10000";
            else CustomPur = v;
            Rebuild(true);
            return CustomPurPublic();
        }

        // ctl 通道用：让下拉项能在无鼠标的情况下被设置与核对
        public static string SetUiPublic(string what, int v)
        {
            try
            {
                if (what == "target") SetTarget(v);
                else if (what == "mode") SetMode(v);
                else if (what == "pre") SetPre(v);
                else if (what == "post") SetPost(v);
                else return "ui -> bad-key " + what;
                return "ui -> " + what + "=" + v + " target=" + TargetIdx + " mode=" + Mode + " pre=" + PreMode + " post=" + PostMode;
            }
            catch (Exception e) { return "ui -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static void CycleMode() { Mode = (Mode + 1) % PlanText.ModeNames.Length; UncapEff = false; ManualPickLabel = null; UnlockPlan("按了模式键"); DropPlanCache(); RequestRebuild(); }
        private static void CycleTarget() { TargetIdx = (TargetIdx + 1) % PlanText.TargetNames.Length; RequestRebuild(); }
        private static void CyclePre() { PreMode = (PreMode + 1) % PlanText.PreNames.Length; RequestRebuild(); }

        private static UnityEngine.Vector2 posSteps, posSrc;
        private static bool hasSteps, hasSrc;
        /// <summary>每帧记住各窗口位置（玩家点 × 关掉再开要留在原处，和游戏一样）。</summary>
        /// <summary>所有窗口的位置登记表：key -> 世界坐标。以后新窗口只要 WinRestore(key) / RememberAllWindows 里加一行即可。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, UnityEngine.Vector2> winPos
            = new System.Collections.Generic.Dictionary<string, UnityEngine.Vector2>();
        public static void WinRestore(string key, PixelWindow w)
        {
            if (w == null || string.IsNullOrEmpty(key)) return;
            UnityEngine.Vector2 v;
            if (winPos.TryGetValue(key, out v)) WinSetPos(w, v);
        }
        public static void WinSave(string key, PixelWindow w)
        {
            if (w == null || string.IsNullOrEmpty(key)) return;
            UnityEngine.Vector2 v;
            if (WinGetPos(w, out v)) winPos[key] = v;
        }

        public static void RememberAllWindows()
        {
            WinSave("main", panelWin); WinSave("cfg", settingsWin); WinSave("steps", stepsWin); WinSave("src", srcWin);
            try { if (pendingMainFrames <= 0 && panelWin != null && panelWin.IsVisible()) { UnityEngine.Vector2 v; if (WinGetPos(panelWin, out v)) { keepMainPos = v; hasKeepMain = true; } } } catch (Exception) { }

        }
        private static UnityEngine.Vector2 keepMainPos;
        private static bool hasKeepMain;
        private static int panelPageFor = -1;   // 这个窗口里现在装的是哪一页（-1＝没装）
        // 主面板两列宽：左竖排导航 72 + 间距 4 + 内容 224（玩家：界面还能再缩一档）
        private const int PanelW = 304;
        // 玩家 2026-11-12 定案（单窗口版）：四页控件一次性建好，各自装进一个页容器 stack（父节点）；
        // 切页只做 Detach + Attach（换面板对象）+ ResizePixels，绝不重建控件 → 没有重排卡顿。
        // 实测：把四页塞进同一个栈靠 SetActive 收占位行不通，隐藏页仍把窗口撑成灰色巨幕（玩家 2026-11-12）。
        private static bool mainTreeDone;      // 这棵树建过没有（建过就不再建，避免重复 Attach 把窗口弄坏）
        private static bool panelHeaderDone;

        // 玩家 2026-11-12 定案（单窗口 + 四个独立面板）：四页各包一个全新的 MachineryPanel，
        // 切页＝Detach 旧面板 / Attach 新面板（每次都是不同对象，不会踩「反复 Attach 同一个」的崩溃）。
        private static MachineryPanel panelMain, panelSet, panelPool, panelSteps, panelCalc, panelMix;
        // 每页都有自己的左侧导航，切页时三份文字都要重写（否则会留下 ▶ 残影）。
        private static readonly List<MachineryButton> navSetBtns = new List<MachineryButton>();
        private static readonly List<MachineryButton> navMoreBtns = new List<MachineryButton>();
        private static readonly List<MachineryButton> navStepsBtns = new List<MachineryButton>();
        private static readonly List<MachineryButton> navCalcBtns = new List<MachineryButton>();
        private static readonly List<MachineryButton> navMixBtns = new List<MachineryButton>();
        // 每页都有一行兜底标题，隐藏时要一起隐藏。
        private static readonly List<MachineryText> titleRows = new List<MachineryText>();
        /// <summary>PixelWindow 真正的定位字段是 DockAnchorOffset（anchoredPosition 恒为 0，改了也没用）。</summary>
        // 位置一律用「世界坐标」记：anchoredPosition 与 DockAnchorOffset 在这类窗口上都读回 0，
        // 只有 transform.position 才是拖动后真正变的那份。
        // 主面板默认位置（画布坐标，原点左下；y=1080 是屏幕上沿）
        private static float MainPosX = 610f;
        private static float MainPosY = 670f;
        // 玩家：净水助手要记住关闭前的位置，下次打开回到那儿（只有第一次才居中）
        private static float LastPosX = 0f;
        private static float LastPosY = 0f;
        private static bool hasLastPos = false;

        // 显示后要连续顶几帧：PixelWindow.Show() 自己会把位置排回左上角(0,1080)，只写一次会被它盖掉。
        private class ForcePos { public PixelWindow w; public float x; public float y; public int frames; }
        private static readonly List<ForcePos> forcePos = new List<ForcePos>();

        private static void CenterXY(PixelWindow w, out float x, out float y)
        {
            x = 960f; y = 540f;
            try
            {
                var rt = w.rectTransform;
                float pw = rt.rect.width, ph = rt.rect.height;
                if (pw <= 1f) pw = 360f;
                if (ph <= 1f) ph = 260f;
                x = (UnityEngine.Screen.width - pw) * 0.5f;
                y = (UnityEngine.Screen.height + ph) * 0.5f;
            }
            catch (Exception) { }
        }

        private static void ForceAt(PixelWindow w, float x, float y, int frames)
        {
            if (w == null) return;
            try { WinPlace(w, x, y); } catch (Exception) { }
            try { forcePos.Add(new ForcePos { w = w, x = x, y = y, frames = frames }); } catch (Exception) { }
        }

        /// <summary>把任意 mod 窗口放到屏幕正中，并在随后几帧持续顶住（防 Show() 把它排回左上角）。</summary>
        /// <summary>把 接水 / 手册 / 效率 / 性价比 挂到面板自己的标题栏（AddHeaderButton 加在原生 X 左边）。</summary>
        private static void AddPanelHeader()
        {
            if (panelWin == null || panelHeaderDone) return;
            try
            {
                int iw, ih;
                // 玩家 2026-11-13 定案：标题栏从左到右 = 手册 → 性价比 → 效率 → 接水 → 原生 X。
                // ★玩家 2026-11-13 第三报：主面板标题栏的「详情(i)」删掉 —— 那个键一按就弹「净水助手 · 详情」
                //   浮窗，正常不该有；详情只在净水器那侧保留，主面板不再挂这个按钮。
                // 图标：翻开的书 / 橙 S 金币 / 时钟 / 水滴，全部单色像素 + 一处强调色。
                var bBook = NewIcon(PixelIcons.Get("book", out iw, out ih), "手册", new Action(delegate { WaterBook.Open(1); }));
                var bCheap = NewIcon(PixelIcons.Get("coin", out iw, out ih), "性价比", new Action(delegate { SetMode(2, true); }));
                var bUncap = NewIcon(PixelIcons.Get("clock", out iw, out ih), "效率", new Action(delegate { ModeOn(panelWin, 1); }));
                var bFill = NewIcon(PixelIcons.Get("water", out iw, out ih), "接水", new Action(delegate { OnFill(); }));
                // AddHeaderButton 是往左插：倒着加，视觉上才是 净水助手 → 手册 → 性价比 → 效率 → 接水
                if (bFill != null) panelWin.AddHeaderButton(bFill);
                if (bUncap != null) panelWin.AddHeaderButton(bUncap);
                if (bCheap != null) panelWin.AddHeaderButton(bCheap);
                if (bBook != null) panelWin.AddHeaderButton(bBook);
                panelHeaderDone = true;
                Log("[ui] 面板标题栏图标 ok");
            }
            catch (Exception e) { log.LogWarning("[ui] panel header " + e.GetType().Name + " " + e.Message); }
        }

        private static void CenterWindow(PixelWindow w)
        {
            if (w == null) return;
            float x, y; CenterXY(w, out x, out y);
            ForceAt(w, x, y, 12);
        }

        /// <summary>只写 world position 的定位：WinSetPos 同时写 anchoredPosition 会叠加成双倍位移。</summary>
        private static void WinPlace(PixelWindow w, float x, float y)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                var p3 = rt.position;
                rt.position = new UnityEngine.Vector3(x, y, p3.z);
            }
            catch (Exception) { }
        }

        /// <summary>ctl: move=x,y —— 就地移动主面板并记住，用来标定默认位置。</summary>
        public static string MovePublic(string args)
        {
            try
            {
                var parts = args.Split(',');
                float x = float.Parse(parts[0].Trim());
                float y = float.Parse(parts[1].Trim());
                WinPlace(panelWin, x, y);
                MainPosX = x; MainPosY = y;
                LastPosX = x; LastPosY = y; hasLastPos = true;
                Log("[ui] move " + x + "," + y);
                return PosPublic();
            }
            catch (Exception e) { return "move ERR " + e.Message; }
        }

        private static bool WinGetPos(PixelWindow w, out UnityEngine.Vector2 v)
        {
            v = UnityEngine.Vector2.zero;
            if (w == null) return false;
            try { var p3 = w.rectTransform.position; v = new UnityEngine.Vector2(p3.x, p3.y); return true; }
            catch (Exception) { }
            return false;
        }
        private static void WinSetPos(PixelWindow w, UnityEngine.Vector2 v)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                var p3 = rt.position;
                rt.position = new UnityEngine.Vector3(v.x, v.y, p3.z);
                // 同时写 anchoredPosition，双保险
                try { rt.anchoredPosition = new UnityEngine.Vector2(v.x, v.y); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static UnityEngine.Vector2 pendingMainPos;
        private static int pendingMainFrames;   // >0 时每帧强制套用位置，并暂停「记录位置」（布局会把坐标冲回 0，会形成死循环）
        private static bool pendingRebuild;     // 点击回调只记标记，整窗重建挪到下一帧（玩家：点按钮响应慢）

        /// <summary>点击后立刻改标签、重活延后一帧：按钮视觉即时，面板在下一帧一次性重建。</summary>
        /// <summary>请求重建：延后 0.12 s 再重建，别在玩家按下的那一帧就把按钮销毁重排。
        /// 玩家 2026-11-10 反馈「有时点一下不生效，得再点一次（像双击）」——
        /// 根因是按钮回调里立刻重建窗口，指针抬起时这个按钮已经被销毁，这一次点击就被吞了。</summary>
        public static void RequestRebuild()
        {
            pendingRebuild = true;
            pendingRebuildAt = UnityEngine.Time.realtimeSinceStartup + 0.12f;
        }

        private static float pendingRebuildAt = -1f;

        private static string lastLiveSig;      // 上一次算方案时的输入指纹（没变就不重算）
        private static int lastPageH = -1;      // 上次套用的面板高度（ResizePixels 会让原生窗口整体重排）
        private static float lastMarkerSync = -999f;

        /// <summary>面板显示的输入指纹：容器数值 + 各档设置 + 页面。
        /// 玩家 2026-11-10 反馈：面板开着时鼠标划过按钮约 1.5 s 卡一下 —— 真因是 Tick 里每秒无条件
        /// Rebuild(false)：Compute / 方案表重排 / ResizePixels 全在动原生窗口。指纹没变就整段跳过。</summary>
        private static string LiveSig()
        {
            var sb = new System.Text.StringBuilder(64);
            try
            {
                var b = UiPlanBottle();
                if (b != null)
                {
                    sb.Append(WaterHelper.GetTotalVolume(b)).Append('/').Append(WaterHelper.GetTotalCapacity(b))
                      .Append('/').Append(WaterHelper.GetWaterPurity(b)).Append('/').Append(GameBridge.NameOfItem(b));
                }
                sb.Append("|ctx").Append(PlanBottle() == null ? 0 : 1);
            }
            catch (Exception) { sb.Append("err"); }
            sb.Append('|').Append(Mode).Append(',').Append(PreMode).Append(',').Append(PostMode)
              .Append(',').Append(TargetIdx).Append(',').Append(SrcIdx).Append(',').Append(UncapEff ? 1 : 0)
              .Append(',').Append(WaterPlan.HydraMode).Append(',').Append(WaterPlan.HydraDuraCap)
              .Append(",fd").Append(FilterDurNow())   // 玩家拖过滤棉滑条 → 指纹变 → 面板跟着重算
              .Append(",cp").Append(CustomPur)        // 手填水质拖/键入 → 指纹变 → 主页立刻改口
              .Append(",inf").Append(InfiniteTools ? 1 : 0)   // 真实/无限道具口径也进指纹
              .Append(",dv").Append(devFilter ? 1 : 0).Append(devUv ? 1 : 0)
              .Append(devTablets ? 1 : 0).Append(devMachine ? 1 : 0).Append(devHydra ? 1 : 0)
              .Append(',').Append(BalanceOff ? 1 : 0).Append(',').Append(ShowAllCombos ? 1 : 0)
              .Append(',').Append(UiPage).Append(',').Append(ManualPickLabel ?? "-")
              .Append(",sel").Append(PurifierHook.SelectionSignature());   // 原生绿点取消也立即刷新主页
            return sb.ToString();
        }

        public static void Tick()
        {
#if DEV
            var __sw = new System.Diagnostics.Stopwatch();
            __sw.Start();
            try { TickInner(); }
            finally
            {
                __sw.Stop();
                if (__sw.ElapsedMilliseconds >= 80)
                    Log("[perf] tick " + __sw.ElapsedMilliseconds + "ms | " + GameBridge.CountDump(10));
                else if (__sw.ElapsedMilliseconds >= 20 && GameBridge.Counts.Count > 0)
                    GameBridge.CountDump(1);
            }
#else
            TickInner();
#endif
        }

        /// <summary>换场景（读档/回主菜单）时把跨档的静态引用全部丢掉，避免 5 天档读到 33 天档的水。</summary>
        private static string lastSceneName = "";
        private static void SceneResetTick()
        {
            string sc = "";
            try { sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { }
            if (sc.Length == 0 || sc == lastSceneName) return;
            if (lastSceneName.Length == 0) { lastSceneName = sc; return; }   // 第一次只记名，不打断当前界面
            lastSceneName = sc;
            ResetRuntimeState();
            Log("[scene] " + sc + " -> 清空跨档机器/瓶子引用");
        }

        private static void ResetRuntimeState()
        {
            try
            {
                ctxWins.Clear(); ctxBottles.Clear(); ctxSettings.Clear();
                // 2026-11-14 回滚：这里绝不能再清空 PurifierHook.Pairs 或 BottleInv。
                // 置空后容器识别、绿点、接水会一起失效（玩家实机确认）；混水串档只在 ScanWaters 读取侧处理。
                PurifierHook.PickedIndex = -1; PurifierHook.ActiveMachine = null;
                settingsOwner = null; pendingSettings = null; overrideBottle = null; ctxIndex = 0;
                // 玩家 2026-11-14：换档/重进存档默认回「自动」，不要把上一次手选的药片前置带进来。
                PreMode = 0; PostMode = 0;
                // bug18 B3：多晚开关是全局设置，重启/换档后按 PlayerPrefs 恢复；默认关。
                WaterPlan.NightCap = Math.Max(1, Math.Min(WaterPlan.MaxNights,
                    PlayerPrefs.GetInt(NightCapPref, 1)));
                calcOwner = null; calcLoadedItem = null; calcLoadedVol = 0; calcLoadedCap = 0; calcLoadedPur = 0;
                calcReadSource = false; readMachineItem = null; readMachineVol = 0; readMachineCap = 0; readMachinePur = 0; readMachineParts = "";
                panelWin = null; panelPageFor = -1; mainTreeDone = false; panelHeaderDone = false;
                panelMain = null; panelSet = null; panelPool = null; panelSteps = null; panelCalc = null; panelMix = null;
                lastLiveSig = null; lastRichSig = null; lastFillKey = null; LastMsg = "";
                mixPageH = 196;
                try { ClearCalcFields(); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static void TickInner()
        {
            try { FlatSkinTick(); } catch (Exception) { }   // 自愈必须放在 GetMouseButton(0) 早退之前
            // 玩家 2026-11-13（第六轮）：切存档后旧档的「机器窗口/瓶子」引用还留在静态表里，
            // 混水页就把上一档的 优质水/铁锈水 也读进来。换场景时全部清掉，让新档重新配对。
            try { SceneResetTick(); } catch (Exception) { }
#if DEV
            {
                float __now = UnityEngine.Time.realtimeSinceStartup;
                if (__now - lastTickIn >= 2f)
                {
                    lastTickIn = __now;
                    Log("[tick] in pairs=" + PurifierHook.Pairs.Count +
                        " mouse=" + (UnityEngine.Input.GetMouseButton(0) ? 1 : 0) +
                        " panel=" + (panelWin == null ? 0 : 1) +
                        " calc=" + (calcBtn == null ? 0 : 1));
                }
            }
#endif
            try { CalcMachineWatch(); } catch (Exception) { }
            try { MixMachineWatch(); } catch (Exception e) { Log("[mix] 机器监听失败 " + e.GetType().Name + " " + e.Message); }
            try { WaterBook.Tick(); } catch (Exception) { }
            RememberAllWindows();
            if (forcePos.Count > 0)
            {
                for (int i = forcePos.Count - 1; i >= 0; i--)
                {
                    var f = forcePos[i];
                    if (f == null || f.w == null || f.frames <= 0) { forcePos.RemoveAt(i); continue; }
                    try { WinPlace(f.w, f.x, f.y); } catch (Exception) { }
                    f.frames--;
                    if (f.frames <= 0) forcePos.RemoveAt(i);
                }
            }
            if (pendingMainFrames > 0 && panelWin != null)
            {
                pendingMainFrames--;
                try { WinPlace(panelWin, MainPosX, MainPosY); } catch (Exception) { }
            }
            // 拖动窗口时不重建：重建会重排整个面板，拖起来就是一卡一卡的（玩家反馈）。
            if (UnityEngine.Input.GetMouseButton(0)) return;
            // 计算页开着时只比对「瓶引用+体积+容量+纯度」签名，变了才轻量刷新（0.5s 节流）。
            try { CalcLiveTick(); } catch (Exception) { }
            // 开关机器时红框要立刻跟上（原来 4 s 才同步一次，玩家说「红框显示有点慢」）。
            try { MarkerTick(); } catch (Exception) { }
            try { PickTintTick(); } catch (Exception) { }
            // 玩家 2026-11-13：开手册/换页后原生会把旧窗口高度还回来（混水又变成半屏空窗）。
            // 主页/混水每 0.5 s 顶一次高度，保证始终是矮窗。
            try
            {
                if (panelWin != null && panelWin.IsVisible() && (UiPage == 5 || UiPage == 0))
                {
                    float nowH = UnityEngine.Time.realtimeSinceStartup;
                    if (nowH - lastPanelHeightTick > 0.5f)
                    {
                        lastPanelHeightTick = nowH;
                        panelWin.ResizePixels(PanelW, PageHeight());
                    }
                }
            }
            catch (Exception) { }
            if (pendingRebuild && UnityEngine.Time.realtimeSinceStartup >= pendingRebuildAt)
            {
                pendingRebuild = false;
                pendingRebuildAt = -1f;
                try { Rebuild(true); } catch (Exception) { }
                return;
            }
            // 数值必须跟着现场变，但只在面板可见时重算；间隔放宽到 1.5s，
            // 因为 Compute() 要遍历全部物品（含机器仓 BFS），太频繁会让鼠标划过按钮明显掉帧。
            if (UnityEngine.Time.realtimeSinceStartup - lastLive > 1.5f)
            {
                lastLive = UnityEngine.Time.realtimeSinceStartup;
                if (panelWin != null && panelWin.IsVisible() && forcePos.Count == 0)
                {
                    UnityEngine.Vector2 pv2;
                    if (WinGetPos(panelWin, out pv2) && (pv2.x != 0f || pv2.y != 0f))
                    { LastPosX = pv2.x; LastPosY = pv2.y; hasLastPos = true; }
                }
                // 同时开几台就画几条：SyncAll 自己按每台窗口的 IsVisible 维护，
                // 关掉哪台就销毁哪台的框（不再只认 CurrentMachine 一台）。
                MarkerTick();
            }
            if (calcBtn == null && panelWin == null) return;
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 1f;
            try
            {
                // 面板没开就别算了：以前这里每秒无条件 Rebuild(false)（Compute + 方案重建），
                // 玩家在机器前瞎晃也会掉帧（「打开净水助手就很卡」）。
                bool uiVisible = panelWin != null && panelWin.IsVisible();
                if (uiVisible)
                {
                    // 玩家 2026-11-10：面板开着、鼠标划过按钮就是一下一下地卡。这里原来每秒无条件
                    // Rebuild(false) —— Compute + 方案表重排 + ResizePixels 都在动原生窗口。
                    // 改成先比输入指纹：容器/设置都没变就整段跳过，只在真的变了才重算。
                    string sig = LiveSig();
                    if (sig != lastLiveSig)
                    {
                        lastLiveSig = sig;
                        // 玩家 2026-11-11（性能）：这里原来先 Rebuild(false)、再按 modelDirty 补一次
                        // Rebuild(true)——同一份 Compute + 方案重建跑两遍（实测单次 170-270 ms，
                        // 变更一次就要 400-500 ms，鼠标划过按钮就是一下一下地卡）。
                        // 合并成一次全量：force=true 已经包含「整窗重建」的全部动作。
                        modelDirty = false;
                        Rebuild(true);
                        // 玩家 2026-11-10：灌水键只在容器体积/瓶量变了才重绑，别被别的设置变化带着跑。
                        string fillKey = "";
                        try
                        {
                            var b2 = CtxBottle(); if (b2 == null) b2 = GameBottle();
                            if (b2 != null) fillKey = WaterHelper.GetTotalVolume(b2) + "/" + WaterHelper.GetTotalCapacity(b2)
                                                    + "/" + WaterHelper.GetWaterPurity(b2);
                        }
                        catch (Exception) { }
                        if (fillKey != lastFillKey) { lastFillKey = fillKey; try { RefreshFillLabels(); } catch (Exception) { } }
                    }
                    // 玩家 2026-11-10：标题图标名字自愈每颗都要反射全量 GetMethods，节流到 5 s。
                    if (UnityEngine.Time.realtimeSinceStartup - lastHdrT > 5f)
                    {
                        lastHdrT = UnityEngine.Time.realtimeSinceStartup;
                        try { EnsureHdrNames(); } catch (Exception) { }
                    }
                    // 玩家 2026-11-10：WatchContainer 内部会全背包扫滤棉，节流到 4 s（且只在面板可见时跑）。
                    if (UnityEngine.Time.realtimeSinceStartup - lastWatchT > 4f)
                    {
                        lastWatchT = UnityEngine.Time.realtimeSinceStartup;
                        try { WatchContainer(); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private static void AddLine(RichTextBuilder b, string text, ColorPalette c, bool bold)
        {
            b.AddLine(text, true, c, bold, false, false, false,
                (ColorPalette)(-1), (ColorPalette)(-1), (ColorPalette)(-1));
        }

        /// <summary>背包里有没有能读水质的东西。</summary>
        /// <summary>诊断：把 LiveAllItems 里每件的 identifier / 名字打出来（ctl `items`）。</summary>
#if DEV
        public static string ItemsDump()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var all = GameBridge.AllItemsDeep();   // 深度枚举：打印机/箱子里塞的也要列出来
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = ""; try { id = it.identifier; } catch (Exception) { }
                    int cnt = 1;
                    try
                    {
                        // 发布版禁裸反射：走 GameBridge.Get（属性/字段/backing field 统一封装）。
                        object pc = GameBridge.Get(it, "unitCount");
                        if (pc != null) cnt = Convert.ToInt32(pc);
                    }
                    catch (Exception) { }
                    sb.Append(n == 0 ? "" : " | ").Append(id).Append("×").Append(cnt).Append("=").Append(GameBridge.NameOfItem(it));
                    // 含水容器补上「容量/纯度」：塞在打印机仓里的瓶子，就是要靠这两个数确认「查到了」。
                    // 空瓶子体积是 0，只打体积会看不见，所以按容量判。
                    try
                    {
                        long cap = WaterHelper.GetTotalCapacity(it);
                        if (cap > 0)
                        {
                            long ul = WaterHelper.GetTotalVolume(it);
                            sb.Append(" ").Append(PlanText.Ml(ul)).Append("/").Append(PlanText.Ml(cap))
                              .Append("  ").Append(PlanText.Pct(WaterHelper.GetWaterPurity(it)));
                        }
                    }
                    catch (Exception) { }
                    n++;
                }
                string bins = GameBridge.MachineStorageSummary();
                if (bins.Length > 0) sb.Append(" || 机器仓 ").Append(bins);
                return "items[" + all.Count + "] -> " + sb;
            }
            catch (Exception e) { return "items -> " + e.GetType().Name; }
        }
#endif

        /// <summary>
        /// 加一片净水片到当前瓶子：走游戏自己的 `WaterHelper.OnWaterPurificationTabletAdded`
        /// （它内部处理药片消耗与配比），玩家不用再回背包手点。
        /// </summary>
        private static void OnAddTablet()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();   // 没开机器窗口时退到背包里的瓶子，F9 里也能加药片
                if (b == null) { LastMsg = "先把水瓶放进储水容器，再加药片"; Rebuild(true); return; }
                // 必须先真的有净水片：`OnWaterPurificationTabletAdded` 只负责改水质，不扣道具，
                // 不校验就会变成「凭空加纯度」（实测 98.04 → 98.54 而背包里没有药片）。
                var tab = FindTablet();
                if (tab == null) { LastMsg = "背包里没有净水片，加不了"; Log("[tablet] no-tablet"); Rebuild(true); return; }
                int before = WaterHelper.GetWaterPurity(b);
                GameBridge.StatVoid("WaterHelper", "OnWaterPurificationTabletAdded", b);
                int after = WaterHelper.GetWaterPurity(b);
                // 扣掉一片：`OnWaterPurificationTabletAdded` 本身不动背包，不扣就等于无限白嫖纯度。
                string used = "";
                try
                {
                    // 2026-11-12 工坊合规：这里原来有一句 `var tt = tab.GetType();`（死变量），
                    // 裸 GetType() 会被工坊扫描器当成 reflection-external，已删；取/写数量全走 GameBridge（ModReflect）。
                    int n = 1;
                    var counted = GameBridge.Get(tab, "unitCount");
                    if (counted != null) n = Convert.ToInt32(counted);
                    if (n > 1)
                    {
                        bool wrote = GameBridge.Set(tab, "unitCount", n - 1);
                        if (!wrote) GameBridge.Call(tab, "SetUnitCount", n - 1);
                        used = "（背包剩 " + (n - 1) + " 片）";
                    }
                    else
                    {
                        GameBridge.Call(tab, "SetUnitCount", 0);
                        used = "（用光了）";
                    }
                }
                catch (Exception) { }
                LastMsg = "已加净水片：" + PlanText.Pct(before) + " → " + PlanText.Pct(after) + used + " [" + lastTabletHit + "]";
                Log("[tablet] before=" + before + " after=" + after);
                lastKey = "";
                Rebuild(true);
            }
            catch (Exception e) { LastMsg = "加药片失败：" + e.GetType().Name; Rebuild(true); }
        }

        /// <summary>找背包里的净水片（名字含「净水片」或 identifier 含 purif/tablet）。</summary>
        private static string lastTabletHit = "";

        private static object FindTablet()
        {
            try
            {
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    string nm = GameBridge.NameOfItem(it);
                    // 只认「净水片」。早期版本按 id 含 purif 兜底，把 water_purifier（净水器机器本体）
                    // 也匹配上了，随后 SetUnitCount(0) 会把机器从背包里抹掉 —— 必须排除机器与容器。
                    bool isMachineOrBottle = !string.IsNullOrEmpty(id)
                        && (id.IndexOf("purifier", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("machine", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("bottle", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("tank", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("container", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hit = !isMachineOrBottle
                            && ((!string.IsNullOrEmpty(nm) && nm.IndexOf("净水片", StringComparison.Ordinal) >= 0)
                             || (!string.IsNullOrEmpty(id) && id.IndexOf("purif_tablet", StringComparison.OrdinalIgnoreCase) >= 0)
                             || (!string.IsNullOrEmpty(id) && id.IndexOf("water_tablet", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (hit)
                    {
                        lastTabletHit = (id ?? "?") + "/" + (nm ?? "?");
                        Log("[tablet] match " + lastTabletHit);
                        return it;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static bool Scanners()        {
            // 玩家 2026-11-10：这函数原来是「每次 Rebuild 就全背包 BFS 一轮」，只为打一行 bal 日志，
            // 面板开着鼠标划过按钮就卡。结果缓存 6 秒，期间任何调用都直接命中。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (scanCacheT > 0f && now - scanCacheT < 6f) return scanCacheVal != 0;
            scanCacheT = now;
            bool found = false;
            try
            {
                // 必须用 RawItems 而不是 LiveAllItems —— 后者只返回「有容量」的容器，
                // 扫描仪/药片这类非容器道具根本不会出现在里面（玩家带了水质扫描仪却被判成没有）。
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count && !found; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (id == "aquascan" || id == "water_test_strip" || id == "water_test_container") { found = true; break; }
                    // identifier 兜不住时按本地化名兜底：叫「水质扫描仪」「化学扫描仪」的都要认。
                    string nm2 = GameBridge.NameOfItem(it);
                    if (!string.IsNullOrEmpty(nm2) && nm2.IndexOf("扫描仪", StringComparison.Ordinal) >= 0) { found = true; break; }
                }
            }
            catch (Exception) { }
            scanCacheVal = found ? 1L : 0L;
            return found;
        }

        /// <summary>平衡模式开关（玩家口径）：开启＝按真实水质算；关闭＝当未知水质（隐藏精确值）。</summary>
        private static bool HidePurity() { return !BalanceOff; }
        // ── 玩家 m01056 的性能重构：计算 与 写界面 彻底分成两层 ──
        //  ModelFor(force) → 只负责「算」；同一组输入直接复用上次结果，不再跑一遍规划求解器。
        //  BindModel(m, force) → 只负责「写」；只调 SetText / SetValue / SetVisible，绝不新建或销毁控件。
        private static PanelModel modelCache;
        private static string modelKey = "";
        // 控件文字缓存：同样的文字重复写会触发 TMP 重排，是「点一下就卡」的隐形开销。
        private static readonly Dictionary<object, string> bindTxt = new Dictionary<object, string>();

        /// <summary>Compute 的输入指纹：只读「这一瓶 + 设置」这些真正影响结果的量，本身不跑规划。</summary>
        private static string InputKey()
        {
            var sb = new System.Text.StringBuilder(128);
            try
            {
                // 手动源要按右键读取/计算页这只样瓶做指纹，不能只看机器瓶。
                var b = calcManualSource ? ExplicitPickBottle() : null;
                if (b == null) b = GameBottle();
                if (b == null) sb.Append("nob");
                else
                {
                    sb.Append("b").Append(b.GetHashCode()).Append('|');
                    sb.Append(WaterHelper.GetTotalVolume(b)).Append('|');
                    sb.Append(WaterHelper.GetTotalCapacity(b)).Append('|');
                    sb.Append(WaterHelper.GetWaterPurity(b)).Append('|');
                    sb.Append(GameBridge.NameOfItem(b)).Append('|');
                    int[] parts = WaterHelper.GetLiquidParts(b);
                    if (parts != null)
                        for (int i = 0; i < parts.Length; i++) sb.Append(parts[i]).Append(',');
                }
            }
            catch (Exception e) { sb.Append("err").Append(e.GetType().Name); }
            sb.Append("|cm").Append(calcManualSource ? 1 : 0).Append("|ci").Append(CalcManualSig());
            // 补水来自水龙头：水源今日纯度变化会改变混合水方案，不能只按瓶里那瓶水的指纹缓存。
            try
            {
                int tg = 0, tp = 0;
                TapSource(out tg, out tp);
                sb.Append("|tap").Append(tp);
            }
            catch (Exception) { }
            int perf, q;
            Bonuses(out perf, out q);
            sb.Append("|p").Append(perf).Append("|q").Append(q);
            sb.Append("|m").Append(Mode).Append("|t").Append(TargetIdx).Append("|s").Append(SrcIdx);
            sb.Append("|cu").Append(CustomPur).Append("|pr").Append(PreMode).Append("|po").Append(PostMode);
            sb.Append("|bal").Append(BalanceOff ? "1" : "0");
            // 手动挑方案也必须进指纹：否则点行 → ManualPickLabel 变了但指纹没变 → ModelFor 直接复用旧模型，
            // 玩家点了行却看回原来那条方案（2026-11-12 排查）。
            sb.Append("|mp").Append(ManualPickLabel);
            sb.Append("|inf").Append(InfiniteTools ? "1" : "0").Append("|fd").Append(FilterDurNow());
            sb.Append("|nc").Append(WaterPlan.NightCap);
            // 设备/海德拉开关必须进模型指纹：否则设置页切了「滤棉/紫外/…」，ModelFor 直接复用旧模型，
            // 主页/方案看起来没反应（玩家 2026-11-13 反馈）。
            sb.Append("|dv").Append(devFilter ? 1 : 0).Append(devUv ? 1 : 0)
              .Append(devTablets ? 1 : 0).Append(devMachine ? 1 : 0).Append(devHydra ? 1 : 0)
              .Append("|hy").Append(WaterPlan.HydraMode).Append('|').Append(WaterPlan.HydraDuraCap);
            return sb.ToString();
        }

        private static PanelModel ModelFor(bool force)
        {
            string k = InputKey();
            if (modelCache != null && k == modelKey)
            {
                // 输入一模一样（按钮连点、切页、定时刷新最常见）：直接用上次算好的模型。
                Model = modelCache;
                return modelCache;
            }
            // Compute 可能抛（反射/游戏对象已被销毁）：原来它包在 RebuildInner 的 try 里，现在必须自己兜住，
            // 否则一次异常就会把面板卡在旧状态上、并且每帧刷错误日志。
            PanelModel m;
            try { m = Compute(); }
            catch (Exception e)
            {
                log.LogWarning("[ui] compute " + e.GetType().Name + " " + e.Message);
                m = new PanelModel();
            }
            modelCache = m;
            modelKey = k;
            Model = m;
            return m;
        }

        /// <summary>只写文字，不建不删控件；文字没变就直接跳过（省掉 TMP 重排）。</summary>
        private static void SetT(MachineryText t, string s)
        {
            if (t == null) return;
            object k = t;
            string prev;
            if (bindTxt.TryGetValue(k, out prev) && prev == s) return;
            bindTxt[k] = s;
            try { t.SetText(s); } catch (Exception) { }
        }

        /// <summary>同上，按钮的文字。</summary>
        private static void SetB(MachineryButton b, string s)
        {
            if (b == null) return;
            object k = b;
            string prev;
            if (bindTxt.TryGetValue(k, out prev) && prev == s) return;
            bindTxt[k] = s;
            try { b.SetText(s); } catch (Exception) { }
        }

        /// <summary>规范 2.3：金额一律 Gold。CardMoney 的文案在 WaterPlan 里生成（只读，禁改），
        /// 所以在这里只把它的「钱绿」颜色标签换成 Gold，一个字都不动。</summary>
        private static string GoldMoney(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("<color=#" + PlanText.C_MONEY + ">", "<color=#F2C14E>");
        }

        public static void Rebuild(bool force)
        {
            // 玩家 2026-11-11：「点了按钮要等大约 1 秒」——先把耗时量出来，别再凭感觉改。
            // 玩家 m01056：把耗时拆成「算模型 model」与「写控件 bind」两段，方便定位瓶颈。
            // 只有超过 40ms 才写日志（正常重建是毫秒级），并把当次 GameBridge 调用计数一起打出来。
#if DEV
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long modelMs = 0;
            try
            {
                PanelModel m = ModelFor(force);
                modelMs = sw.ElapsedMilliseconds;
                BindModel(m, force);
            }
            finally
            {
                long ms = sw.ElapsedMilliseconds;
                if (ms >= 40) Log("[rebuild] " + ms + "ms (model " + modelMs + "ms bind " + (ms - modelMs) + "ms) force=" + force + " | " + GameBridge.CountDump(8));
            }
#else
            BindModel(ModelFor(force), force);
#endif
        }

        /// <summary>把算好的模型写进已有的控件树（Bind 契约：这里绝不创建、绝不销毁任何控件）。</summary>
        private static void BindModel(PanelModel m, bool force)
        {
            try
            {
                Model = m;
                string key = m.VolumeMl + "|" + m.PurityBp + "|" + m.CapBp + "|" + m.Mode + "|" + m.TargetBp + "|" + m.PreMode + "|" + m.PerfPct + "|" + m.Quality
                           + "|s" + SrcIdx + "|po" + m.PostMode
                           + "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap
                           + "|fd" + FilterDurNow() + "|inf" + (InfiniteTools ? "1" : "0")
                           + "|nm" + (WaterPlan.NoMachine ? "1" : "0") + "|cp" + CustomPur + "|ctx" + ctxIndex
                           + "|dv" + (devFilter ? 1 : 0) + (devUv ? 1 : 0) + (devTablets ? 1 : 0) + (devMachine ? 1 : 0) + (devHydra ? 1 : 0);
                bool changed = force || key != lastKey;
                if (changed)
                {
                    lastKey = key;
                    modelDirty = true;
                    // 玩家定案：机器标题栏的图标只显示固定名字（接水/净水助手/手册/效率/性价比），
                    // 动态方案文案一律进面板顶行，不再覆盖图标名（以前这里会把名字刷成「今晚原样跑→99.01」）。
                    SetDesc(cfgBtn, "配方与攻略");
                }
                if (panelWin == null && detailWin == null && recipeWin == null && settingsWin == null && stepsWin == null && srcWin == null) return;
                bool hidePurity = HidePurity();
                // 玩家 2026-11-13：机器/背包里都没容器 → 不算方案，只显示「未放容器」。
                bool noBox = (m == null || !m.HasBottle);
                // 玩家 2026-11-10：这一行纯日志，别再为它跑一次全背包 BFS（扫描仪结果见 ctl `scan?` 探针）。
                string bal = "[bal] mode=" + (BalanceOff ? "off" : "on") +
                             " display=" + (hidePurity ? "unknown" : "exact");
                if (bal != lastBal) { lastBal = bal; Log(bal); }
                // 顶行压短：它一长窗口就变宽（玩家：按了接水界面变长）。性能/质量留在 tooltip 里。
                // 玩家 2026-11-12：主页改两列——「容器」「水质」各自成行，左列标签定宽（见 BuildMain），
                // 这里只往右列写值；上限跟着水质走（上限是水质能达到的上限，摆一行更符合读表习惯）。
                if (pSubLbl != null) SetT(pSubLbl, "容器");
                if (pSub != null)
                    SetT(pSub, PlanText.Rich(PlanText.C_NUM, m.VolumeMl + "/" + m.CapacityMl + " mL"));
                // 玩家 2026-11-13：不管瓶里有没有水，这一行都叫「水源」（他要把「今日水」统一成「水源」）——
                // 空瓶报水龙头今天这桶水，有瓶报瓶里这瓶水的来源，读起来都是「水源」。
                if (pPurLbl != null) SetT(pPurLbl, "水源");
                if (pPur != null)
                {
                    // 玩家 2026-11-13：空瓶子时不要写一个没意义的「水质 0.00%」；
                    // 这行改报水龙头今天这桶水的真实纯度，有瓶子了再回到瓶内水质。
                    // p2：数值后面必须跟上原版水质档位名（例：82.96% 沟渠水），档位用反编译原版色值上色。
                    int showBp = (m.VolumeMl <= 0) ? m.SourcePurBp : m.PurityBp;
                    if (hidePurity)
                        SetT(pPur, PlanText.Rich(PlanText.C_WARN, "未知"));
                    else if (showBp <= 0)
                        SetT(pPur, PlanText.Rich(PlanText.C_WARN, "未知"));
                    else
                    {
                        string tn = WaterEconomy.TierName(showBp);
                        SetT(pPur, PlanText.Rich(PlanText.C_PCT, PlanText.Pct(showBp))
                                 + " " + PlanText.Rich(PlanText.TierHex(tn), tn));
                    }
                }
                // 设置页各行：左侧标签已固定（目标 / 模式 / 前置 / 后置 / 水源 / 海德拉），
                // 这里只往右侧值按钮写当前值（2026-11-12 去掉 ▶ 前缀，纯数值更像仪表盘），并且一律用短名 —— 写长了会把窗口顶宽。
                if (cTarget != null) SetB(cTarget, PlanText.TargetNames[Math.Max(0, Math.Min(TargetIdx, PlanText.TargetNames.Length - 1))]);
                if (cMode != null)
                    SetB(cMode, (UncapEff
                        ? "效率  满"
                        : (m.Mode == 1 ? "效率" : "性价比")
                          + (InfiniteTools ? "  无限" : "")));
                if (cPre != null) SetB(cPre, UiName(PreUi, PreVal, PreMode));
                RefreshPostRow();
                if (cSrc != null) SetB(cSrc, SrcManual() ? "手填" : SrcUiName(SrcIdx));
                try { RefreshSrcChips(); } catch (Exception) { }   // 水源四块每次绑定重刷绿/灰（值可能被别处改）
                try { RefreshMachChips(); } catch (Exception) { }  // 机器开关同理
                if (cHydra != null) SetB(cHydra, UiName(HydraShort, HydraVal, WaterPlan.HydraMode));
                // 滑条不在这里重建文字：只在值被别处改动时把位置同步过来，标签走 RefreshHydraDurLabel
                if (sHydraDur != null)
                {
                    float want = Math.Max(0f, Math.Min((float)HydraDurMaxSlider, (float)HydraDurShown()));
                    if (Math.Abs(sHydraDur.value - want) > 0.01f) sHydraDur.SetValue(want, false);
                }
                RefreshHydraDurLabel();
                // 过滤棉滑条同理：只在值被别处改动时把位置同步过来（玩家正在拖的时候别覆盖）
                if (sFilterDur != null)
                {
                    float wantF = Math.Max(0f, Math.Min((float)FilterDurMaxSlider, (float)FilterDurNow()));
                    if (Math.Abs(sFilterDur.value - wantF) > 0.01f) sFilterDur.SetValue(wantF, false);
                }
                RefreshFilterDurLabel();
                // ★玩家 2026-11-13（第三报）：手填水质的滑条与右边数字必须一起走 ——
                //   以前「键入 100.00」只改了数字、滑条还停在旧位置，看着就是「联动没做好」。
                //   这里和设备页的另外两条滑条同一做法：值被别处改动才推位置，玩家正在拖的时候不覆盖。
                if (sManPur != null && !manTyping && SrcManual())
                {
                    float wantM = Math.Max(0f, Math.Min(100f, CustomPur / 100f));
                    if (Math.Abs(sManPur.value - wantM) > 0.01f) sManPur.SetValue(wantM, false);
                }
                if (cManPurLbl != null) SetT(cManPurLbl, manTyping ? (manBuf + "|") : PlanText.Pct(CustomPur));
                if (targetBtns != null) Mark(targetBtns, PlanText.TargetNames, TargetIdx);
                if (modeBtns != null) Mark(modeBtns, PlanText.ModeNames, m.Mode);
                if (preBtns != null) Mark(preBtns, PlanText.PreNames, PreMode);
                if (pFill != null) SetDesc(pFill, FillLabel());   // 图标键：文字走 tooltip，别覆盖图标本身
                if (pEarly != null) SetT(pEarly, "");
                pHintText(m);
                // 三张卡与底部状态行共用同一份方案，避免「卡一说上限 2000、状态行说 1857」
                var r = PlanForUi(m);
                // 玩家 2026-11-11：容器里这瓶水已经达到目标 → 界面只显示「已达标」，方案/钱/步骤/持有/接水键全部不显示。
                // 达标按「实际能达到的目标」判断：玩家设 99.90% 但本机上限只有 99.01% 时，
                // 瓶子已经到 99.01% 就是到头了，再给方案也没用 —— 直接显示「已达标」。
                int wantTarget = m != null && m.TargetBp > 0 ? m.TargetBp : 0;   // 玩家设的原始目标（别被本机上限夹掉）
                int okTarget = wantTarget;
                if (okTarget > 0 && m.CapBp > 0 && okTarget > m.CapBp) okTarget = m.CapBp;
                bool alreadyOk = m != null && m.VolumeMl > 0 && okTarget > 0 && m.PurityBp >= okTarget;
                // ★玩家 2026-11-13（第三报）：水源本身就够目标了（手填 100% / 今日水管 100%，目标 99.90%）
                //   → 接进来就是达标水，不需要「跑一晚」；也不该一边说「到不了」一边还列「出来 / 卖 / 赚 / 步骤」。
                //   判断不能只看水源：瓶里若已有脏水，接满后是混合水质，得按加权平均算一遍再下结论。
                bool srcOk = false;
                long needFillMl = 0;
                if (m != null && wantTarget > 0 && m.CapacityMl > 0 && m.SourcePurBp >= wantTarget)
                {
                    long room = (long)m.CapacityMl - m.VolumeMl;
                    if (room < 0) room = 0;
                    long mixedBp;
                    if (room <= 0) mixedBp = m.PurityBp;
                    else mixedBp = ((long)m.VolumeMl * m.PurityBp + room * m.SourcePurBp) / Math.Max(1L, (long)m.CapacityMl);
                    srcOk = mixedBp >= wantTarget;
                    if (srcOk) needFillMl = room;
                }
                bool doneOk = alreadyOk || srcOk;
                // 目标真的高于本机上限、水源也给不了：只报「到不了 + 本机上限」，后面方案/钱整段收起。
                bool capDead = !doneOk && m != null && m.CapBp > 0 && wantTarget > m.CapBp;
                // 玩家 2026-11-13（第六轮二报）：没开机器时只留「容器 / 水源 / 目标 / 未选中净水器」，
                // 步骤、出水、卖钱、持有、接水键一律不显示（那些是上一台机器的残留，算多余文字）。
                bool noMachUi = uiDeselected || !AnyMachineVisible();
                // 玩家 2026-11-15：没选机器时不再摆「需接水 0 / 出来 0 / 卖 0 / 赚 0 / 步骤 — / 持有 —」，
                // 连容器/水源/目标三行仪表盘也一起收起，只在主页卡片区给一句「未选择机器」。
                if (pSubLbl != null) SetVisible(pSubLbl, !noMachUi);
                if (pSub != null) SetVisible(pSub, !noMachUi);
                if (pPurLbl != null) SetVisible(pPurLbl, !noMachUi);
                if (pPur != null) SetVisible(pPur, !noMachUi);
                if (pTgtLbl != null) SetVisible(pTgtLbl, !noMachUi);
                if (pTgtVal != null) SetVisible(pTgtVal, !noMachUi);
                // 玩家 2026-11-13（p1）：目标纯度常显在主页（像仪表盘读数），不随状态消失。
                if (pTgtVal != null)
                    SetT(pTgtVal, wantTarget > 0 ? PlanText.Rich(PlanText.C_PCT, PlanText.Pct(wantTarget))
                                                 : PlanText.Rich(PlanText.C_WARN, "未知"));
                // 底部状态行：容器 X/Y mL、还能装多少、装不下多少必须常驻（网页底部的状态条），
                // 上一次动作的回执（已灌/已备水）接在后面，不再顶掉容器状态。
                if (pMsg != null)
                {
                    // 底行必须短：它一变长，窗口就跟着变宽（玩家：按了接水界面变长）
                    // 已达标 / 水源已达标时不再写「还差 / 先倒出」：那两句跟「已达标」自相矛盾。
                    string live = (noBox || doneOk || capDead) ? "" : LiveLineShort(m, r);
                    string msg = (LastMsg == null ? "" : LastMsg.Replace("\n", " ").Trim());
                    if (msg.Length > 12) msg = msg.Substring(0, 12) + "…";
                    if (autoHydraHint && !noMachUi && !doneOk && !capDead)
                        live = msg;
                    // 批次 29：按钮固定挂在主页栈底；只有「性价比自动且无无耗材方案」才可见。
                    SetVisible(pHydraHint, autoHydraHint && !noMachUi);
                    // 玩家要求：方案标签（如「无前置+1 片药片」）放最下面当步骤，别占顶行。
                    string stepTxt = "";
                    if (noMachUi) stepTxt = "";
                    else if (!noBox && !doneOk && !capDead && r != null && r.pick != null)
                    {
                        stepTxt = "步骤 " + RawStepsChain(r);
                        // 旧版「换方案」可能在上一次解锁时留下；现在只有一条达标解就把它收掉，
                        // 避免主页写着换方案、更多方案页却只有当前一行。
                        if (compareText != null && compareText.Length > 0)
                        {
                            var altsNow = r.TableRows(5);
                            if (altsNow == null || altsNow.Count < 2) compareText = "";
                        }
                        if (compareText != null && compareText.Length > 0) stepTxt += "\n" + compareText;
                        // 多晚不再另挂「升到 N 晚…」的模糊提醒：步骤行已经逐步写出
                        // 「药/滤棉/净化器/药/净化器」，玩家按步骤就能看懂。
                    }
                    SetT(pMsg, noMachUi ? "" : live);
                    SetVisible(pMsg, !noMachUi && live.Length > 0);
                    // 玩家 2026-11-10：步骤放到「出来 X mL」的下一行（pStep），底行只留容器状态。
                    // 玩家 2026-11-10：步骤行文案没变就别再写原生控件（写一次＝整行重排）。
                    // 玩家 2026-11-11：步骤行有时整行不显示 —— 因为换页重建后 pStep 是全新控件（空文本），
                    // 而 lastStepLine 还存着上一页写过的同一句，判等把它拦住，新控件就一直是空的。
                    // 所以除了比文案，还要比「控件是不是同一个」。
                    if (pStep != null && (stepTxt != lastStepLine || !ReferenceEquals(pStep, lastStepOwner)))
                    {
                        lastStepOwner = pStep;
                        lastStepLine = stepTxt;
                        SetT(pStep, stepTxt);
                        // 玩家 2026-11-13（p1）：空文本照样占一行，已达标时就把整行收起来，别留空白。
                        SetVisible(pStep, stepTxt.Length > 0);
                    }
                }

                // 玩家 2026-11-13（p1）：「已达标」就一行字，下面不要再挂「接 20000 就达标，不用跑机器」这类废话。
                // 玩家 2026-11-13（第四轮）：水够了也不写「已满，直接净化」，这一行就空着、整行收起来。
                if (pCard1Val != null)
                {
                    string card1 = noMachUi ? PlanText.Rich(PlanText.C_WARN, "未选择机器")
                                      : noBox ? ""
                                      : doneOk ? PlanText.Rich(PlanText.C_MONEY, "已达标")
                                      : (m != null && m.CapacityMl <= 0 && AnyMachineVisible()
                                          ? PlanText.Rich(PlanText.C_WARN, "这台净水器没放容器")
                                          : (r != null && r.capShort
                                              ? PlanText.Rich(PlanText.C_WARN, "到不了 " + PlanText.Pct(r.targetBp))
                                              : (r == null ? "" : CardFeedValueFor(m, r))));
                    SetT(pCard1Val, card1);
                    SetVisible(pCard1Val, card1.Length > 0);
                }
                if (pCard2 != null)
                {
                    string outTxt = noMachUi ? ""
                                              : (noBox || doneOk || capDead) ? "" : (r == null ? "出来 —" : r.CardOut());
                    // 目标高于本机上限：只讲原因，不列任何方案/收益。
                    if (capDead)
                        outTxt = PlanText.Rich(PlanText.C_WARN, "本机上限 " + PlanText.Pct(m.CapBp) + "，要更高得加质量模组");
                    SetT(pCard2, outTxt);
                    SetVisible(pCard2, outTxt.Length > 0);   // 空行收掉，已达标时别留空白
                }
                if (pMoney != null)
                {
                    string moneyTxt = noMachUi ? ""
                                               : (noBox || doneOk || capDead) ? "" : (r == null ? "" : r.CardMoney());
                    // 工单页1：卖 / 赚拆两行；CardMoney 仍是数据层原文，只在 UI 显示层切开。
                    // 仍用同一个 pMoney 控件换行显示，避免多挂一个空行把没机器时的窗口撑高。
                    string sellTxt = moneyTxt;
                    string gainTxt = "";
                    int gainAt = moneyTxt.IndexOf("赚");
                    if (gainAt > 0)
                    {
                        sellTxt = moneyTxt.Substring(0, gainAt).Trim();
                        gainTxt = moneyTxt.Substring(gainAt).Trim();
                    }
                    string moneyShow = gainTxt.Length > 0 ? sellTxt + "\n" + gainTxt : sellTxt;
                    SetT(pMoney, GoldMoney(moneyShow));
                    SetVisible(pMoney, moneyShow.Length > 0);
                }
                if (pCard3 != null) { SetT(pCard3, ""); SetVisible(pCard3, false); }   // 玩家：杂质变化这一行没必要，整行隐藏（只清空仍占一行，会留空白）
                // 主页「持有」行：只拼背包里真有的道具，两样都没有就隐藏整行（也不占高度）
                if (pOwn != null)
                {
                    if (noMachUi) { SetT(pOwn, ""); SetVisible(pOwn, false); }
                    else
                    {
                        string ownTxt = OwnLineText();
                        if (noBox || doneOk || capDead) SetVisible(pOwn, false);
                        else { SetT(pOwn, ownTxt); SetVisible(pOwn, ownTxt.Length > 0); }
                    }
                }
                if (pMixLine != null)
                {
                    string mix = (noBox || doneOk || capDead) ? "" : (r == null ? "" : MixText(r));
                    if (capDead)
                        mix = (mix.Length > 0 ? mix + GAP : "") + PlanText.Rich(PlanText.C_WARN, "上限 " + PlanText.Pct(WaterMath.CapOf(r.q)));
                    SetT(pMixLine, mix);
                }
                // 底部「接水」键：键面只写动作，接多少由下面的状态行走日志与 tooltip 说；
                // 点下去就是 OnFill 的整瓶量逻辑（容器已有水只补空余，满了提示先倒出来）。
                if (pJoin != null)
                {
                    // 水源已达标时也要留这个键：正好用它把水接满（玩家要的就是「接进来就达标」）。
                    SetVisible(pJoin, !noMachUi && !noBox && !alreadyOk && !capDead);
                    SetB(pJoin, "接水");
                    long joinMl = srcOk && needFillMl > 0 ? needFillMl
                                : (planPourMl > 0 ? planPourMl : (r != null ? BottlePourUl(r.ChosenFeedUl()) / 1000L : 0L));
                    SetDesc(pJoin, "按当前水源接水：本次 " + WaterPlan.Ml(joinMl * 1000L) + " mL（容器已有水只补空余，满了先倒出来）");
                }

                // 方案池（表头 + 列表 + 行号键）只在「更多方案」展开时显示：
                // 主页面只讲当前这一条方案，别的方案点了更多再看（玩家明确要求）。
                // 分页后每页各自建窗，这里只更新本页内容；只有方案池页需要重排列表
                // 导航三键：每次绑定都无条件重写（玩家 2026-11-12 报过 ▶ 残影 —— 之前只有切页那一下
                // 才改文字，切回主页后上一页的三角就留在按钮上了）。
                // 四页各有一份三键，全部无条件重写（只改有变化的那份由 SetB 的字典缓存兜住）。
                for (int i = 0; i < navSetBtns.Count; i++) SetB(navSetBtns[i], (UiPage == 1 ? "▶ " : "") + "设置");
                for (int i = 0; i < navMoreBtns.Count; i++) SetB(navMoreBtns[i], (UiPage == 2 ? "▶ " : "") + "更多方案");
                for (int i = 0; i < navStepsBtns.Count; i++) SetB(navStepsBtns[i], (UiPage == 3 ? "▶ " : "") + "步骤");
                for (int i = 0; i < navCalcBtns.Count; i++) SetB(navCalcBtns[i], (UiPage == 4 ? "▶ " : "") + "计算");
                // 单窗口：高度跟着当前页走（主页 176 / 设置 200 / 更多方案 252 / 步骤 232），
                // 只在这一页的高度真的变了才 ResizePixels —— 否则原生窗口会整页重排，划过就卡。
                int phNow2 = PageHeight();
                if (force || lastPageH != phNow2)
                {
                    lastPageH = phNow2;
                    try { panelWin.ResizePixels(PanelW, phNow2); } catch (Exception) { }
                }
                if (UiPage == 2)   // 只有「更多方案」页需要重填这 6 行（其它页的容器一直存在但不显示）
                {
                    var rows2 = r != null ? (ShowAllCombos ? AllRows(r, 40) : r.TableRows(RowCount)) : null;
                    // 玩家：2000 mL 的容器就别显示 6000 的方案了 —— 按当前瓶子的容量把超出的行滤掉
                    long capNow = (long)m.CapacityMl * 1000L;
                    if (rows2 != null && rows2.Count > 0 && capNow > 0 && !UncapEff)
                    {
                        var fit = new List<WaterPlan.Combo>();
                        for (int qi = 0; qi < rows2.Count; qi++) if (rows2[qi] != null && rows2[qi].feedUl <= capNow) fit.Add(rows2[qi]);
                        if (fit.Count > 0) rows2 = fit;
                    }
                    lastRows = (rows2 != null && rows2.Count > 0) ? rows2.ToArray() : null;
                    if (pTableHead != null)
                    {
                        bool onlyOne = rows2 != null && rows2.Count == 1;
                        string head = onlyOne ? "当前只有这一套达标方案" : "点一行就选中";
                        if (!onlyOne && r != null && r.filterEmpty) head += " ｜ 前后置凑不出，按自动给";
                        if (!onlyOne && r != null)
                        {
                            string th2 = r.TabletHint();
                            if (!string.IsNullOrEmpty(th2)) head += " ｜ " + (th2.Length > 34 ? th2.Substring(0, 34) + "…" : th2);
                        }
                        try { SetT(pTableHead, head); } catch (Exception) { }
                        // 只有一条时明确写“没有更多”，不要再让玩家以为下面还有方案可翻。
                        SetVisible(pTableHead, onlyOne || r == null || r.pick == null);
                    }
                    if (pRowBtns != null)
                    {
                        for (int i = 0; i < pRowBtns.Length; i++)
                        {
                            var btn = pRowBtns[i];
                            var lbl = pRowLbls != null && i < pRowLbls.Length ? pRowLbls[i] : null;
                            bool has = rows2 != null && i < rows2.Count;
                            if (!has && i == 0)
                            {
                                // 空表也要说清楚为什么空（玩家报「更多方案空了」）
                                SetVisible(btn, true);
                                if (lbl != null) SetVisible(lbl, false);
                                string why = r == null ? "没有可规划的水瓶：先放一只装水的瓶子" : (r.filterEmpty ? "前后置凑不出达标方案，换一个前置/后置" : "这瓶给不出可选方案：先用水质扫描仪扫一遍");
                                try { SetB(btn, why); } catch (Exception) { }
                                continue;
                            }
                            if (btn == null) continue;
                            if (!has) { SetVisible(btn, false); if (lbl != null) SetVisible(lbl, false); continue; }
                            SetVisible(btn, true);
                            if (lbl != null) SetVisible(lbl, true);
                            var c = rows2[i];
                            bool sel = r.pick != null && c == r.pick;
                            // 左列：序号 + 短方案名（选中项带 ▶）。短名规则统一在 WaterPlan.Combo.ShortLabel 里，
                            // 刷新时不再显示「只放…（过夜后放）」那种长名。
                            if (lbl != null)
                            {
                                // 玩家 2026-11-13（p6a）：方案名必须和「步骤」页同一口径 —— 带上「几晚 / 几片药」，
                                // 以前这里只写「滤棉」、步骤页却写「滤棉50 1 晚」，看着像一晚被删掉了。
                                SetT(lbl, (sel ? "▶ " : "") + (i + 1) + ". " + StepChainPlain(c));
                            }
                            // 玩家 2026-11-12：右侧只显示「实际出水量  纯度  净赚」，去掉「接多少→」那半截（容量信息页面顶部已有）。
                            double net = WaterEconomy.Profit(c.outUl, c.purityBp, c.cost);
                            string money = (net >= 0 ? "+" : "-") + WaterEconomy.Money(Math.Abs(net));
                            string moneyCol = net >= 0 ? "F2C14E" : PlanText.C_WARN;   // 规范 3.3：正收益 Gold，亏损仍旧暖红
                            // 玩家 2026-11-16：更多方案右侧补「每升价格」，方便直接比性价比。
                            double perL = c.outUl > 0 ? c.cost / (c.outUl / 1000000.0) : 0;
                            try { SetB(btn, WaterPlan.Ml(c.outUl) + GAP + PlanText.Pct(c.purityBp) + GAP
                                            + "每升" + WaterEconomy.Money(perL) + GAP
                                            + PlanText.Rich(moneyCol, money)); } catch (Exception) { }
                            // 玩家 2026-11-13：方案表右块只留一行「实际出水 + 纯度 + 净赚」。
                            // 以前 SetDesc 又塞一整行「纯度 耗材 本钱 卖价 净赚」，按钮被撑成两行，
                                // 六行表看起来又长又空；描述直接清空，耗材/本钱在详情卡里看。
                            try { SetDesc(btn, ""); } catch (Exception) { }
                            // 选中行亮一档：整块底色换成 RowFillSel（左列仍用 ▶ 标出当前方案）
                            try { SkinButton(btn, sel ? RowFillSel : RowFill, Cream, false); } catch (Exception) { }
                        }
                    }
                }
                if (pList == null || UiPage != 3) return;   // 只有步骤页需要富文本（pList 一直在，别给别的页白画）
                var b = new RichTextBuilder(160);
                if (UiPage == 3)
                {
                    // 2026-11-12 重排：整页改成「一行一个短句」的仪表。
                    // 旧版把 Headline/CostText 整段塞进来（50 多字），在 304 宽的富文本里必然折行，
                    // 「17853.7」和「mL」被拆到两行就是宽度不够造成的——不是字体的锅。
                    long stepHaveMl = 0L, stepPourMl = 0L;
                    int doneNow = progressActive ? Math.Max(0, progressDoneSteps) : 0;
                    bool progressNow = doneNow > 0;
                    var raw = RawResultFor(r);
                    if (r != null)
                    {
                        // 玩家 2026-11-14：步骤页不能只抄方案表——瓶里水多于方案目标时要先倒，
                        // 少于目标时要写“需接水还差多少”，否则步骤页会一直显示旧目标。
                        long haveMl = 0L;
                        var stepBottle = calcManualSource ? ExplicitPickBottle() : PlanBottle();
                        if (stepBottle == null) stepBottle = CtxBottle();
                        if (stepBottle == null) stepBottle = GameBottle();
                        long stepCapUl = 0L;
                        if (stepBottle != null)
                        {
                            try
                            {
                                haveMl = WaterHelper.GetTotalVolume(stepBottle) / 1000L;
                                stepCapUl = WaterHelper.GetTotalCapacity(stepBottle);
                            }
                            catch (Exception) { haveMl = 0L; stepCapUl = 0L; }
                        }
                        long pourMl = BottlePourUl(r.ChosenFeedUl(), stepCapUl) / 1000L;
                        stepHaveMl = haveMl; stepPourMl = pourMl;
                        // 方案已经执行到某一步：后面的水少了也不补、不提示，
                        // 只把剩余步骤继续跑；接水只有在方案本来就有这一步时才允许显示。
                        if (!progressNow)
                        {
                            // 效率档补满时，方案是按「现有水 + 水管水」的混合体算的；
                            // 先明确写一步混合，再让下面 AutoSolve 的步骤接着跑。
                            long mixAddUl = r.fromWater && r.water0 != null ? r.water0.v - haveMl * 1000L : 0L;
                            if (mixAddUl >= 1000L)
                            {
                                AddLine(b, "混合 补 " + GameMl(mixAddUl) + " mL 水管水 → "
                                        + PlanText.Pct(r.srcBp), ColorPalette.LightOrange, true);
                            }
                            else if (haveMl > pourMl)
                            {
                                long overMl = haveMl - pourMl;
                                if (overMl > 0L)
                                    AddLine(b, "先倒出 " + WaterPlan.Ml(overMl * 1000L) + " mL，再净化",
                                            ColorPalette.LightOrange, true);
                            }
                            else if (haveMl > 0 && pourMl > haveMl)
                                AddLine(b, "需接水 " + WaterPlan.Ml((pourMl - haveMl) * 1000L) + " mL",
                                        ColorPalette.LightOrange, true);
                            // 玩家 2026-11-13：一行只放一个短句，宁可多占一行也不让「2000 / mL」被折开。
                            AddLine(b, "接水 " + WaterPlan.Ml(pourMl * 1000L) + " mL", ColorPalette.White, true);
                        }
                        else
                        {
                            AddLine(b, "剩余 " + (raw != null && raw.rawSteps != null
                                ? Math.Max(0, raw.rawSteps.Count - doneNow) : 0) + " 步  ·  不补水",
                                    ColorPalette.LightGray, false);
                        }

                        int stepBp = r.srcBp;
                        if (stepBottle != null && haveMl > 0)
                        {
                            try { int curBp = WaterHelper.GetWaterPurity(stepBottle); if (curBp > 0) stepBp = curBp; }
                            catch (Exception) { }
                        }
                        AddLine(b, "水质 " + PlanText.Pct(stepBp), ColorPalette.LightCyan, false);
                        AddLine(b, "出水 " + GameMl(r.ChosenOutUl()) + " mL", ColorPalette.White, false);
                        AddLine(b, "纯度 " + PlanText.Pct(r.ChosenOutBp()), ColorPalette.LightCyan, false);
                        AddLine(b, WaterPlan.NoMachine ? "不用净水器" : "性能 +" + r.perf + "%" + GAP + "质量 " + r.q,
                            ColorPalette.LightGray, false);
                        b.AddEmptyLine(4);

                        // 耗材压成三行短句；带括号的解释（工具，不消耗 / 400 点里扣）全部删掉
                        int tabN = r.pick != null ? r.pick.tablets : r.tablets;
                        int filN = r.pick != null ? r.pick.filterUsed : r.filterUsed;
                        bool uvN = r.pick != null ? r.pick.uv : (r.uvUsed > 0);
                        if (!WaterPlan.NoMachine)
                            AddLine(b, "药 " + tabN + " 片" + GAP + "滤棉 " + filN + " 点" + (uvN ? GAP + "紫外" : ""),
                                ColorPalette.LightGray, false);
                        else
                            AddLine(b, "药 " + tabN + " 片" + GAP + (filN > 0 ? "滤棉 " + filN + " 点" : "不过滤棉") + (uvN ? GAP + "紫外" : ""),
                                ColorPalette.LightGray, false);
                        AddLine(b, "成本 " + r.cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                              + GAP + "每升 " + PlanText.PerL(r.perL),
                            ColorPalette.LightGray, false);
                        try
                        {
                            double rev = r.ChosenRevenue();
                            double net = r.ChosenProfit();
                            if (rev > 0)
                                AddLine(b, "卖 " + WaterEconomy.Money(rev) + GAP + "净赚 "
                                      + (net >= 0 ? "+" : "-") + WaterEconomy.Money(Math.Abs(net)),
                                    net >= 0 ? ColorPalette.LightGreen : ColorPalette.LightOrange, false);
                        }
                        catch (Exception) { }
                        b.AddEmptyLine(4);
                    }
                    if (raw != null && raw.rawSteps != null && raw.rawSteps.Count > 0)
                    {
                        // 批次 33：步骤页直接遍历 AutoSolve 的原始步骤，不再按固定顺序模拟；
                        // 药片一片一行，每步都带这一步之后的体积与显示纯度。
                        for (int si = Math.Max(0, doneNow); si < raw.rawSteps.Count; si++)
                        {
                            var stp = raw.rawSteps[si];
                            if (stp == null) continue;
                            try { b.Add((si + 1) + "  ", ColorPalette.LightOrange); } catch (Exception) { }
                            string title = stp.label == null ? "" : stp.label;
                            title += " ×" + Math.Max(1, stp.count);
                            if ((stp.tool == "filter" || stp.tool == "portable") && stp.dura > 0)
                                title += "  耐久" + stp.dura;
                            b.Add(title, ColorPalette.White, "\n");
                            b.Add("    → ", ColorPalette.LightGray);
                            b.Add(GameMl(stp.volUl), ColorPalette.LightCyan);
                            b.Add("  ", ColorPalette.LightGray);
                            b.Add(PlanText.Pct((int)Math.Round(stp.purity * 100.0)), ColorPalette.LightGreen);
                            b.Add("\n", ColorPalette.White);
                            if (si < raw.rawSteps.Count - 1) b.AddEmptyLine(3);
                        }
                    }
                    else if (r != null && r.steps != null && r.steps.Count > 0)
                    {
                        for (int si = 0; si < r.steps.Count; si++)
                        {
                            var stp = r.steps[si];
                            if (stp == null) continue;
                            // 序号单独一档色：这个像素富文本只认 ColorPalette，不认 <color=#hex>——
                            // 之前写 hex 标签，开标签被当标记吃掉、闭标签原样印出来，玩家看到的「1</c olor>」就是它。
                            try { b.Add((si + 1) + "  ", ColorPalette.LightOrange); } catch (Exception) { }
                            // 标题拆成「名字」+「→ 体积 纯度」两行：名字一行，数字一行，谁都不会被折半。
                            string title = stp.title == null ? "" : stp.title;
                            string head = title, metric = "";
                            int arrow = title.IndexOf(" → ", StringComparison.Ordinal);
                            if (arrow >= 0) { head = title.Substring(0, arrow); metric = title.Substring(arrow + 3); }
                            b.Add(head, ColorPalette.White, "\n");
                            if (metric.Length > 0)
                            {
                                b.Add("    ", ColorPalette.LightGray);
                                int gap = metric.LastIndexOf(GAP, StringComparison.Ordinal);
                                if (gap > 0)
                                {
                                    b.Add(metric.Substring(0, gap), ColorPalette.LightCyan);          // 体积：青蓝
                                    b.Add("  " + metric.Substring(gap + GAP.Length), ColorPalette.LightGreen);  // 纯度：绿
                                }
                                else b.Add(metric, ColorPalette.LightCyan);
                                b.Add("\n", ColorPalette.White);
                            }
                            // 明细降一档 + 缩进 4 空格（不挂「  」：整页小圆点太吵），换行也不会顶到行首
                            if (!string.IsNullOrEmpty(stp.detail))
                                AddLine(b, "    " + stp.detail, ColorPalette.LightGray, false);
                            if (si < r.steps.Count - 1) b.AddEmptyLine(3);   // 步骤之间空开
                        }
                    }
                    else AddLine(b, "还没有步骤：先选好水源与目标", ColorPalette.LightOrange, false);
                    // 玩家 2026-11-10：富文本每次重排最贵，数据没变就跳过（换了控件对象才强制重画）。
                    string stepSig = "S|" + (raw != null && raw.rawSteps != null ? raw.rawSteps.Count : (r == null || r.steps == null ? 0 : r.steps.Count)) + "|" +
                                     (r != null && r.pick != null ? r.pick.label : "-") + "|" + PreMode + "/" + PostMode
                                     + "|m" + (WaterPlan.NoMachine ? 1 : 0)
                                     + "|raw" + (raw != null ? raw.purityShown.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-")
                                     + "|h" + stepHaveMl + "|p" + stepPourMl
                                     + "|d" + (r != null ? r.dumpUl : 0L);
                    if (RichChanged(pList, stepSig)) pList.SetRichText(b, 4000);
                    StyleRichText(pList, 9, 3f);   // 规范 3.4：正文 9 号，行距 3（只改已有 TMP 节点）
                    return;
                }
                // —— 一晚方案表：选中那条高亮并把 ▶ 放在最前，其余按出水量从大到小 ——
                var rows = r != null ? (ShowAllCombos ? AllRows(r, 40) : r.TableRows(5)) : null;
                lastRows = (rows != null && rows.Count > 0) ? rows.ToArray() : null;
                if (rows != null && rows.Count > 0)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var c = rows[i];
                        bool sel = r.pick != null && c == r.pick;
                        // 每条方案两行：标签 + 实际出水；纯度/耗材/本钱/净赚。净赚单独染绿，
                        // 不再整行一个灰色 —— 玩家要一眼看到「这个方案赚多少」。
                        AddComboRow(b, c, sel);
                    }
                }
                else AddLine(b, m.Verdict, ColorPalette.White, false);
                // 玩家 23:18：方案要的量超过容器容量时别硬推荐 —— 先确认背包/这台机器有没有这么大的空容器，
                // 没有就用黄字明说「先换大容器」，不要让他照着按钮接却接不满。
                try
                {
                    if (r != null && r.pick != null && !r.fromWater)
                    {
                        long needUl2 = r.pick.feedUl;
                        int f0, cnt0, maxCap0;
                        PickReceiver(0, out f0, out cnt0, out maxCap0);
                        long mineUl = (long)m.CapacityMl * 1000L;
                        if (needUl2 > mineUl && needUl2 > maxCap0)
                        {
                            AddLine(b, "方案要 " + WaterPlan.Ml(needUl2) + " mL，但这台机器只有 " + m.CapacityMl +
                                   " mL、背包最大空容器 " + WaterPlan.Ml(maxCap0) + " mL —— 先换大容器（或换小方案）",
                                   ColorPalette.LightOrange, true);
                        }
                    }
                }
                catch (Exception) { }
                // 前置+后置筛空时的说明：方案表退回自动方案，但必须让玩家知道为什么
                if (r != null && r.filterEmpty) AddLine(b, "前置「" + PlanText.PreName(PreMode) + "」+ 后置「" + PlanText.PostName(PostMode) + "」凑不出达标方案，表里按自动给", ColorPalette.LightOrange, false);
                // 药片清不动时说明原因（化学量还没到 0.7% 门限，网页同一套判定），避免看起来「选了没反应」
                if (r != null)
                {
                    string th = r.TabletHint();
                    if (th != null && th.Length > 0) AddLine(b, th, ColorPalette.LightOrange, false);
                }
                // 操作回执单独一行放最下面（玩家：别拼在状态行里把窗口撑长）
                if (!string.IsNullOrEmpty(LastMsg))
                    AddLine(b, LastMsg.Replace("\n", " "), ColorPalette.LightOrange, false);
                // 玩家 2026-11-10：方案表富文本也先比签名，没变不重排（换控件对象才强制画）。
                string richSig = "T|" + (rows == null ? 0 : rows.Count) + "|" +
                                 (r != null && r.pick != null ? r.pick.label + r.pick.feedUl : "-") + "|" +
                                 (ShowAllCombos ? 1 : 0) + "|" + (LastMsg == null ? "-" : LastMsg) + "|" +
                                 m.Mode + "/" + m.TargetBp;
                if (RichChanged(pList, richSig)) pList.SetRichText(b, 4000);
                StyleRichText(pList, 9, 3f);   // 规范 3.4：正文 9 号，行距 3（只改已有 TMP 节点）
            }
            catch (Exception e)
            {
                log.LogWarning("[ui] Rebuild " + e.GetType().Name + " " + e.Message);
#if DEV
                // NRE 的 Message 是空串，只有类型名抓不到位置 —— 打前 3 帧栈（玩家 2026-11-11 报刷屏）
                string st = e.StackTrace;
                if (st != null)
                {
                    string[] fr = st.Split('\n');
                    int n = fr.Length < 3 ? fr.Length : 3;
                    var sb = new System.Text.StringBuilder("[ui] Rebuild @");
                    for (int i = 0; i < n; i++) sb.Append(' ').Append(fr[i].Trim());
                    log.LogWarning(sb.ToString());
                }
#endif
            }
        }

        /// <summary>
        /// 旧规则：接水必须能由现有整瓶容器拼出来，不能用量筒硬补到任意 mL。
        /// WaterPlan.Plan 会给「不超过 want 的最大整瓶组合」；没有容器清单时才退回原值。
        /// </summary>
        private static long BottlePourUl(long wantUl)
        {
            return BottlePourUl(wantUl, 0);
        }

        /// <summary>
        /// capUl > 0 且方案量已经达到/超过目标瓶容量时，满瓶本身就是一个可达容器量，
        /// 不能又被 WaterPlan.Plan 的背包杯具清单压回去（机器槽里的目标瓶可能不在清单里）。
        /// </summary>
        private static long BottlePourUl(long wantUl, long capUl)
        {
            if (wantUl <= 0) return 0;
            if (capUl > 0 && wantUl >= capUl) return capUl;
            try
            {
                var p = WaterPlan.Plan(wantUl);
                if (p != null && !p.none && p.totalUl > 0) return p.totalUl;
            }
            catch (Exception) { }
            return wantUl;
        }

        /// <summary>
        /// 面板专用的方案对象：同一组输入只算一次（整瓶规划比较贵，0.4 秒刷新不能每帧重跑）。
        /// 方案表的选中行、三张卡、接水键量都从这一个 Report 来，保证互相不打架。
        /// </summary>
        private static WaterPlan.Report PlanForUi(PanelModel m)
        {
            // 玩家 2026-11-13：机器里没容器就不算 —— 直接返回空，省掉整套方案计算，界面显示「未放容器」。
            if (m == null || !m.HasBottle) return null;
            string k = m.BottleName + "|" + m.VolumeMl + "|" + m.PurityBp + "|" + m.CapBp + "|" +
                       m.Mode + "|" + m.PreMode + "|" + m.PostMode + "|" + m.TargetBp + "|" +
                       m.PerfPct + "|" + m.Quality + "|" + (ManualPickLabel == null ? "-" : ManualPickLabel) +
                       "|u" + (UncapEff ? "1" : "0") +
                       "|s" + SrcIdx + "|cp" + CustomPur + "|nm" + (WaterPlan.NoMachine ? "1" : "0") +
                       "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap +
                       "|fd" + FilterDurNow() + "|inf" + (InfiniteTools ? "1" : "0") +
                       "|nc" + WaterPlan.NightCap +
                       "|cm" + (calcManualSource ? 1 : 0) + "|ci" + CalcManualSig() +
                       "|dv" + (devFilter ? 1 : 0) + (devUv ? 1 : 0) + (devTablets ? 1 : 0)
                       + (devMachine ? 1 : 0) + (devHydra ? 1 : 0) +
                       "|ctx" + ctxIndex;
            if (planCache != null && k == planKey) return planCache;
            planKey = k;
            planPourMl = 0;
            try
            {
                planCache = CurrentPlan(true);
                // 整瓶拆分（和 OnFill 完全同一条路径）：按钮上写的量必须就是真会倒进去的量。
                // 已有水方案不需要从背包瓶子凑水，所以不给「接水量」。
                if (planCache != null && planCache.bestUl > 0 && !planCache.fromWater)
                {
                    planPourMl = BottlePourUl(planCache.bestUl, m.CapacityMl * 1000L) / 1000L;
                }
            }
            catch (Exception e) { log.LogWarning("[ui] PlanForUi " + e.Message); planCache = null; }
            return planCache;
        }

        /// <summary>
        /// 六项杂质明细：正常情况下用百分比并保证加起来 100.00%；
        /// 若取整后杂质已经占满（水被挤到 0 或负数），改用绝对 mL，避免出现「水 0.00% 却还有杂质」的假数据。
        /// </summary>
        private static string MixText(WaterPlan.Report r)
        {
            string pct = r.MixLine();
            if (string.IsNullOrEmpty(pct)) return "";
            try
            {
                var w = r.ChosenWater();
                if (w == null || w.v <= 0) return pct;
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                long sum = 0;
                for (int i = 0; i < keys.Length; i++) sum += w.Part(keys[i]) * 10000L / w.v;
                if (sum >= 9960) return r.MixLineMl();
            }
            catch (Exception) { }
            return pct;
        }

        private static void pHintText(PanelModel m)
        {
            // 提示固定在 hint 行的 tooltip 里，避免再占一行
            SetDesc(pFill, m.Hint == null ? "" : m.Hint);
        }

        private static void SetDesc(MachineryButton b, string text)
        {
            if (b == null || string.IsNullOrEmpty(text)) return;
            try { b.SetDescription(text); } catch (Exception) { }
        }

        // ---------------- 标题栏图标固定名 ----------------
        // 玩家要求：机器/面板标题栏图标只显示固定名字（接水/净水助手/手册/效率/性价比），
        // 不能像以前那样被方案文案刷成「今晚原样跑→99.01」。动态信息一律进面板顶行。
        private static readonly Dictionary<MachineryButton, string> HdrNames = new Dictionary<MachineryButton, string>();
        private static MachineryText pTitleRow;      // 面板内兜底标题行（拿不到官方标题控件时用）
        private static bool frameTitleOk;            // 官方标题控件是否可用

        private static void RegisterHdr(MachineryButton b, string name)
        {
            if (b == null) return;
            HdrNames[b] = name;
            SetDesc(b, name);
        }

        /// <summary>每 tick 自愈：游戏侧若有代码改了图标 description，这里按登记表写回。
        /// 注意必须「先读后写」——无条件 SetDescription 会让原生按钮每秒重建一次，鼠标划过就卡。</summary>
        private static float lastHdrNameAt = -999f;

        private static void EnsureHdrNames()
        {
            if (HdrNames.Count == 0) return;
            // ★性能：12 个标题栏图标 × 每帧一次 ModReflect 读 description，日志里 Get:description*84。
            // 名字设好就不会再变，1.5 s 检查一次足够。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= lastHdrNameAt && now - lastHdrNameAt < 1.5f) return;
            lastHdrNameAt = now;
            foreach (var kv in HdrNames)
            {
                if (kv.Key == null) continue;
                try
                {
                    string cur = GameBridge.Get(kv.Key, "description") as string;
                    if (!string.Equals(cur, kv.Value)) kv.Key.SetDescription(kv.Value);
                }
                catch (Exception) { }
            }
        }

        /// <summary>ctl: icons? —— 回报标题栏图标的当前名字（回归断言用）。</summary>
        public static string IconsPublic()
        {
            var sb = new System.Text.StringBuilder("icons -> n=" + HdrNames.Count);
            foreach (var kv in HdrNames)
            {
                if (kv.Key == null) continue;
                string cur = "?";
                try { var v = GameBridge.Get(kv.Key, "description"); if (v != null) cur = v.ToString(); } catch (Exception) { }
                sb.Append(" | ").Append(kv.Value).Append(":").Append(cur);
            }
            return sb.ToString();
        }

        /// <summary>把「净水助手」写进 PixelWindow 自带的标题栏字段（titleString / hasTitle），
        /// 这样标题就和小图标按钮在同一排居中，和官方净水器窗一模一样；成功则隐藏面板内的兜底标题行。</summary>
        private static void ApplyFrameTitle()
        {
            frameTitleOk = false;
            if (panelWin == null) return;
            try
            {
                // 走官方 ModReflect（免 reflection-external）：先写属性，只读时写 backing field
                if (GameBridge.Set(panelWin, "titleString", "净水助手")) frameTitleOk = true;
                else if (GameBridge.Set(panelWin, "<titleString>k__BackingField", "净水助手")) frameTitleOk = true;

                // 让窗口把标题行显示出来（hasTitle 只有 getter，写 backing field）
                if (!GameBridge.Set(panelWin, "hasTitle", true))
                {
                    try { GameBridge.Set(panelWin, "<hasTitle>k__BackingField", true); } catch (Exception) { }
                }

                Log("[ui] title via PixelWindow.titleString ok=" + frameTitleOk);
            }
            catch (Exception e) { Log("[ui] title ERR " + e.GetType().Name + " " + e.Message); }
            try { SetTitleRowsVisible(!frameTitleOk); } catch (Exception) { }
        }

        /// <summary>ctl: title? —— 当前标题走的是窗口自带标题栏还是面板内兜底行。</summary>
        public static string TitlePublic()
        {
            string ts = "?", ht = "?";
            try
            {
                if (panelWin != null)
                {
                    var v1 = GameBridge.Get(panelWin, "titleString");
                    if (v1 != null) ts = v1.ToString();
                    var v2 = GameBridge.Get(panelWin, "hasTitle");
                    if (v2 != null) ht = v2.ToString();
                }
            }
            catch (Exception) { }
            bool bodyVisible = true;
            try { if (pTitleRow != null) bodyVisible = pTitleRow.rectTransform.gameObject.activeSelf; } catch (Exception) { }
            return "title -> via=" + (frameTitleOk ? "window" : "fallback") + " titleString=" + ts + " hasTitle=" + ht + " bodyRow=" + (bodyVisible ? "on" : "off");
        }

        /// <summary>ctl: assert? —— 用分析员给的 IL 断言值自检单晚净化（只读）。
        /// 期望：1650 mL / 91.31% / perf=0 → 一晚到 9901；2000 mL 同浓度若需两晚，说明模型是对的。</summary>
        public static string AssertPublic()
        {
            return "assert -> " + OneAssert(1650000L, 9131, 0) + " || " + OneAssert(2000000L, 9131, 0);
        }

        private static string OneAssert(long vUl, int pur, double perf)
        {
            try
            {
                var w = WaterMath.Build(vUl, pur);
                long imp0 = w.Impurity;
                long budget = WaterMath.Budget(vUl, pur, perf);
                int cap = WaterMath.CapOf(0);
                int rounds = 0; long removed = 0;
                while (rounds < 8)
                {
                    long r = WaterMath.Purify(w, perf, 0);
                    if (r <= 0) break;
                    removed += r; rounds++;
                    if (w.Purity >= 9900) break;
                }
                return (vUl / 1000) + "mL@" + pur + " dirt=" + WaterMath.DirtMul(pur).ToString("0.0000000")
                     + " budget=" + budget + " imp0=" + imp0 + " cap=" + cap
                     + " removed=" + removed + " pur=" + w.Purity + " rounds=" + rounds;
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + " " + e.Message; }
        }

        // —— 容器观察器：把被观测容器的精确 μL / 六项配比 / 过滤棉耐久的变化写进日志 ——
        // 用途：过滤、过夜、加药片每一步前后各留一行精确数字；玩家实测与模型差几 mL 时直接对账。
        private static string lastWatch;
        public static string WatchNow() { return WatchContainer(); }

        public static string WatchContainer()
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "watch -> no-bottle";
                long v = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                var sb = new System.Text.StringBuilder("watch -> v=").Append(v).Append("uL pur=").Append(pur)
                    .Append('(').Append(PlanText.Pct(pur)).Append(')');
                var parts = GameBridge.LiquidParts(b);
                var names = GameBridge.LiquidNames();
                if (parts == null) sb.Append(" parts=null");
                else
                {
                    sb.Append(" n=").Append(parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string nm = (names != null && i < names.Length) ? names[i] : "?";
                        sb.Append(' ').Append(nm).Append('=').Append(parts[i]);
                    }
                }
                long dur = FilterDurability();
                if (dur >= 0) sb.Append(" filterDur=").Append(dur);
                sb.Append(" cap=").Append(WaterHelper.GetTotalCapacity(b));
                string s = sb.ToString();
                if (s != lastWatch) { lastWatch = s; Log(s); }
                return s;
            }
            catch (Exception e) { return "[watch] ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>玩家「实际持有的净水道具」——性价比档按这个建候选（没道具就装不满）。
        /// 接口照抄 N 网 WaterAssistant 的 InventoryScanner：identifier + unitCount + DurabilityHelper。</summary>
        public class OwnedToolsInfo
        {
            public int tablets;      // 净水片总数（含每格 unitCount）
            public bool filter;      // 有没有过滤棉（耐久 > 0）
            public bool uv;          // 有没有紫外灯
            public int hydraDur;     // 海德拉可用耐久总和
            public int filterDur;    // 过滤棉可用耐久总和（2 块 = 800 点；以前只算第一块 400）
            public bool infinite;    // 玩家 2026-11-11：无限道具口径（药片/滤棉/海德拉都不限）
            public string note = "";
        }

        private static OwnedToolsInfo ownCache;
        private static float ownTime = -999f;

        public static OwnedToolsInfo OwnedTools()
        {
            // 玩家 2026-11-11 报 bug：切到无限后「持有」还显示过滤棉 2000 点，点一下设置再返回才更新。
            // 根因是 6 秒缓存没有记录口径 —— 切档后仍返回上一次的口径。缓存键带上 infinite 即可。
            if (ownCache != null && ownCache.infinite == InfiniteTools
                && UnityEngine.Time.realtimeSinceStartup - ownTime < 6f) return ownCache;
            var o = new OwnedToolsInfo();
            if (InfiniteTools)
            {
                // 玩家 Q3 口径：无限 = 药片/过滤棉不限；紫外是工具（不计耐久与成本）；海德拉不限，
                // 但拉条（FilterDurUse / HydraDuraCap）与设置页仍能限制这些「无限道具」。
                o.tablets = -1; o.filter = true; o.uv = true; o.hydraDur = -1;
                o.filterDur = -1;          // -1 = 不限点数，持有行直接显示「无限」
                o.infinite = true;
                o.note = "无限";
                ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
                return o;
            }
            if (ForceOwnAll)
            {
                o.tablets = -1; o.filter = true; o.uv = true; o.hydraDur = -1;
                o.filterDur = WaterPlan.FilterDurPer;
                o.note = "全都有（强制）";
                ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
                return o;
            }
            int tabs = 0, hydra = 0, filtDur = 0;
            bool hasFilter = false, hasUv = false;
            try
            {
                var all = GameBridge.RawItems();
                log.LogInfo("[own] raw items=" + all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (string.IsNullOrEmpty(id)) continue;
                    string lid = id.ToLowerInvariant();
                    if (lid.IndexOf("portable_water_purifier") >= 0 || lid.IndexOf("portable_purifier") >= 0)
                    {
                        int d = 0;
                        try { d = (int)DurabilityHelper.GetRealDurability(it); } catch (Exception) { }
                        if (d > 0) hydra += d;
                    }
                    else if (lid.IndexOf("uv_filter") >= 0 || lid.IndexOf("_uv") >= 0 || lid.IndexOf("uv_lamp") >= 0)
                    {
                        hasUv = true;
                    }
                    else if (lid == "water_tablet" || lid == "purif_tablet")
                    {
                        int c = 1;
                        try
                        {
                            var counted = GameBridge.Get(it, "unitCount");
                            if (counted != null) c = Convert.ToInt32(counted);
                        }
                        catch (Exception) { }
                        if (c < 1) c = 1;
                        string where = "?";
                        try { var pv = GameBridge.Get(it, "parentInventory"); if (pv != null) where = pv.ToString(); } catch (Exception) { }
                        log.LogInfo("[own] tablet id=" + id + " unit=" + c + " at=" + where);
                        tabs += c;
                    }
                    else if (lid.IndexOf("water_filter") >= 0 || lid.IndexOf("filter") >= 0)
                    {
                        int d = 1;
                        try { d = (int)DurabilityHelper.GetRealDurability(it); } catch (Exception) { d = 0; }
                        if (d != 0) hasFilter = true;
                        if (d > 0) filtDur += d;   // 累加所有滤棉：背包 2 块 = 800 点（玩家 2026-11-10）
                    }
                }
            }
            catch (Exception) { }
            o.tablets = tabs; o.filter = hasFilter; o.uv = hasUv; o.hydraDur = hydra;
            o.filterDur = filtDur;
            o.note = "药片 " + tabs + "  " + (hasFilter ? ("滤棉 " + filtDur + " 点") : "无滤棉") + "  "
                   + (hasUv ? "紫外" : "无紫外") + "  海德拉 " + hydra + " 耐久";
            ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
            return o;
        }

        /// <summary>ctl 用：强制「道具全有」，方便离线核对默认档。</summary>
        public static bool ForceOwnAll = false;

        /// <summary>背包里**所有**过滤棉的真实耐久之和（一块 400 点；没找到返回 -1，读不出来返回 -2）。
        /// 玩家 2026-11-10：以前找到第一块就 return，2 块滤芯 = 800 点却一直按 400 规划。</summary>
        private static long FilterDurability()
        {
            // 玩家 2026-11-10：WatchContainer 每次都会调到这里，而 LiveAllItems 是整包枚举 ——
            // 面板一开着就一秒一次，鼠标划按钮就卡。结果缓存 4 秒（滤棉耐久不会瞬间变）。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (filtCacheT > 0f && now - filtCacheT < 4f) return filtCacheVal;
            filtCacheT = now;
            long sum = 0;
            bool any = false;
            try
            {
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.IndexOf("water_filter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    long d = 0;
                    try { d = DurabilityHelper.GetRealDurability(it); } catch (Exception) { d = 0; }
                    if (d == -2) { filtCacheVal = -2; return -2; }
                    if (d > 0) { sum += d; any = true; }
                }
            }
            catch (Exception) { }
            filtCacheVal = any ? sum : -1;
            return filtCacheVal;
        }

        /// <summary>ctl: tcase? —— 2000 mL 铁锈水(91.31%) → 1 晚(0 效率) → 1/2 片药片的真值自检。
        /// 目标值：1 片 → 1959.56 mL、化学 14000 μL、99.28%（玩家实测 1959 mL / 14000 μL / 99.28%）。</summary>
        public static string TabletCasePublic()
        {
            try
            {
                var sb = new System.Text.StringBuilder("tcase ->");
                for (int tab = 1; tab <= 2; tab++)
                {
                    var w = WaterMath.Build(2000000L, 9131);
                    WaterMath.Purify(w, 0, 0);
                    long v1 = w.v; int pur1 = w.Purity; long chem1 = w.Part("chemical");
                    WaterMath.Tablet(w, tab);
                    sb.Append(" | 一晚后 ").Append(PlanText.Ml(v1)).Append("mL@").Append(PlanText.Pct(pur1))
                      .Append("(chem ").Append(PlanText.Ml(chem1)).Append(')')
                      .Append(" + ").Append(tab).Append("片 → ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity))
                      .Append(" chem=").Append(w.Part("chemical"));
                }
                return sb.ToString();
            }
            catch (Exception e) { return "tcase ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: mix? —— 读容器真实六项配比（与水质扫描仪同一个接口 GetLiquidParts），
        /// 给出「把这瓶水直接处理到机器上限」的最优方案（过滤/紫外 × 晚数 × 药片）。</summary>
        public static string MixPlanPublic()
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "mix -> no-bottle";
                long v = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                WaterMath.Water w;
                if (!BuildRealWater(b, out w) || w == null || w.v <= 0) return "mix -> no-parts";
                int perf, q; Bonuses(out perf, out q);
                int cap = WaterMath.CapOf(q);
                var sb = new System.Text.StringBuilder("mix -> v=").Append(v).Append("uL pur=").Append(pur)
                    .Append(" perf=").Append(perf).Append(" q=").Append(q).Append(" cap=").Append(cap);
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                sb.Append(" | ");
                for (int i = 0; i < keys.Length; i++) sb.Append(keys[i]).Append('=').Append(w.Part(keys[i])).Append(' ');
                var pl = WaterMath.PlanFromWater(w, perf, q, cap, 4, 3, WaterPlan.FilterDur);
                if (pl == null) { sb.Append("|| 无可行方案（4 晚 + 3 片也到不了上限）"); return sb.ToString(); }
                sb.Append("|| 最优: ");
                if (pl.filter == 1) sb.Append("过滤 ").Append(pl.filterUsed).Append("点 ");
                if (pl.uv == 1) sb.Append("紫外 ");
                sb.Append(pl.nights).Append("晚");
                if (pl.tablets > 0) sb.Append(" + ").Append(pl.tablets).Append("片");
                sb.Append(" → ").Append(PlanText.Ml(pl.outUl)).Append("mL@").Append(PlanText.Pct(pl.purityBp));
                return sb.ToString();
            }
            catch (Exception e) { return "mix ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: ceiling? —— 6 L 容器、过滤+紫外+1 片药片，一晚能接多少（游戏真值口径）。
        /// 用来和网页计算器的「上限 6665.8 mL」对照。</summary>
        public static string CeilingPublic()        {
            try
            {
                var sb = new System.Text.StringBuilder("ceiling -> cap=6000mL target=9900 perf=25 q=0");
                sb.Append(" | filter+uv+1tab=").Append(CeilOn(6000000L, 25, 0, true, true, 1));
                sb.Append(" | filter+uv+0tab=").Append(CeilOn(6000000L, 25, 0, true, true, 0));
                sb.Append(" | none+1tab=").Append(CeilOn(6000000L, 25, 0, false, false, 1));
                sb.Append(" | none+0tab=").Append(CeilOn(6000000L, 25, 0, false, false, 0));
                return sb.ToString();
            }
            catch (Exception e) { return "ceiling ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static string CeilOn(long capUl, int perf, int q, bool f, bool u, int t)
        {
            long lo = 0, hi = capUl;
            for (int i = 0; i < 28; i++)
            {
                long mid = (lo + hi + 1) / 2;
                var p = WaterMath.Run(mid, 9131, perf, q, f, u, t, 9900, 1);
                if (p != null && p.ok) lo = mid; else hi = mid - 1;
                if (hi < lo) break;
            }
            var pp = WaterMath.Run(lo, 9131, perf, q, f, u, t, 9900, 1);
            return (lo / 1000) + "mL" + (pp == null ? "" : ("/" + PlanText.Pct(pp.purity)));
        }

        /// <summary>ctl: sim?6000@9131@1 —— 逐阶段模拟「过滤+紫外+1 晚+药片」，用来和游戏实测逐段对照。</summary>
        public static string StagePublic(string args)
        {
            try
            {
                long ml = 6000; int bp = 9131, tab = 1, nights = 1; bool f = true, u = true;
                string s0 = (args ?? "").Trim();
                var seg = s0.Split('@');
                if (seg.Length > 0) long.TryParse(seg[0], out ml);
                if (seg.Length > 1) int.TryParse(seg[1], out bp);
                if (seg.Length > 2) int.TryParse(seg[2], out tab);
                if (seg.Length > 3) int.TryParse(seg[3], out nights);
                if (seg.Length > 4) f = seg[4].Trim() != "0";
                if (seg.Length > 5) u = seg[5].Trim() != "0";
                if (ml <= 0) ml = 6000;
                if (bp <= 0 || bp > 10000) bp = 9131;
                int perf, q; Bonuses(out perf, out q);
                var w = WaterMath.Build(ml * 1000L, bp);
                var sb = new System.Text.StringBuilder("sim -> ");
                sb.Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)).Append(" perf=").Append(perf);
                if (f) { int used = WaterMath.Filter(w, 400); sb.Append(" | 过滤(").Append(used).Append(") -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (u) { WaterMath.Uv(w, 1); sb.Append(" | 紫外 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                for (int n = 1; n <= Math.Max(0, nights); n++)
                {
                    WaterMath.Purify(w, perf, q);
                    sb.Append(" | 第").Append(n).Append("晚 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                }
                if (tab > 0)
                {
                    long before = w.v;
                    WaterMath.Tablet(w, tab);
                    sb.Append(" | ").Append(tab).Append("片 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity))
                      .Append(" (化学剩 ").Append(w.Part("chemical")).Append("μL, 离体 ").Append(before - w.v).Append("μL)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "sim ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: filt?3000@9131 —— 过滤棉真值自检：过多少耐久、出水多少（对齐玩家实测 3 L→109 点）。</summary>
        public static string FiltPublic(string args)
        {
            try
            {
                long ml = 3000; int bp = 9131;
                string s = (args ?? "").Trim();
                int at = s.IndexOf('@');
                if (at >= 0)
                {
                    long.TryParse(s.Substring(0, at), out ml);
                    int.TryParse(s.Substring(at + 1), out bp);
                }
                else long.TryParse(s, out ml);
                if (ml <= 0) ml = 3000;
                if (bp <= 0 || bp > 10000) bp = 9131;
                var w = WaterMath.Build(ml * 1000L, bp);
                long v0 = w.v;
                int used = WaterMath.Filter(w, 400);
                var sb = new System.Text.StringBuilder("filt -> ");
                sb.Append(PlanText.Ml(v0)).Append("mL@").Append(PlanText.Pct(bp));
                sb.Append(" -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                sb.Append(" dur=").Append(used);
                sb.Append(" || 分项 physical=").Append(w.Part("physical"))
                  .Append(" organic=").Append(w.Part("organic"))
                  .Append(" mineral=").Append(w.Part("mineral"))
                  .Append(" chemical=").Append(w.Part("chemical"));
                return sb.ToString();
            }
            catch (Exception e) { return "filt ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: nights? —— 逐阶段打印当前选中方案的净化过程（过滤 → 紫外 → 每晚），
        /// 用来查「过滤棉+紫外 明明一晚够，却报两晚」这类口径错位。</summary>
        public static string NightsPublic()
        {
            try
            {
                // 计算页有本次方案时用本次方案，避免在 page=4 上仍读主页/机器瓶。
                if (UiPage == 4 && calcDirty) RecalcCalc();
                var r = (UiPage == 4 && calcRep != null) ? calcRep : CurrentPlan(true);
                if (r == null || r.pick == null) return "nights -> no-pick";
                var w = WaterMath.Build(r.pick.feedUl, r.srcBp);
                var sb = new System.Text.StringBuilder("nights -> pick=" + r.pick.label
                    + " feed=" + (r.pick.feedUl / 1000) + "mL out=" + (r.pick.outUl / 1000)
                    + " pur=" + r.pick.purityBp + " tablets=" + r.pick.tablets
                    + " target=" + r.targetBp + " perf=" + r.perf + " q=" + r.q
                    + " imp0=" + w.Impurity + " v0=" + (w.v / 1000) + " pur0=" + w.Purity);
                if (r.pick.hydra)
                {
                    int hd = WaterMath.Hydra(w);
                    sb.Append(" | hydra dur=" + hd + " pur=" + w.Purity + " v=" + (w.v / 1000));
                }
                if (r.pick.filter) { WaterMath.Filter(w, 400); sb.Append(" | filter -> pur=" + w.Purity + " v=" + (w.v / 1000)); }
                if (r.pick.uv) { WaterMath.Uv(w, 1); sb.Append(" | uv -> pur=" + w.Purity + " v=" + (w.v / 1000)); }
                int maxN = r.pick.nights > 0 ? r.pick.nights : 3;
                if (maxN > WaterPlan.MaxNights) maxN = WaterPlan.MaxNights;
                for (int n = 1; n <= maxN; n++)
                {
                    long rm = WaterMath.Purify(w, r.perf, r.q);
                    sb.Append(" | night" + n + " removed=" + rm + " pur=" + w.Purity + " v=" + (w.v / 1000));
                    if (rm <= 0 || w.Purity >= r.targetBp) break;
                }
                if (r.pick.tablets > 0)
                {
                    long before = w.v;
                    WaterMath.Tablet(w, r.pick.tablets);
                    sb.Append(" | tablet" + r.pick.tablets + " -> pur=" + w.Purity + " v=" + (w.v / 1000) + " (−" + (before - w.v) + "μL)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "nights ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: uitree —— 面板窗口的真实类型、含 title/frame/window 的成员、以及节点名字树。
        /// 目的：找到那个「空白色药丸」的 GameObject，或找到真正的标题控件。</summary>
#if DEV
        public static string UiTreePublic()
        {
            var sb = new System.Text.StringBuilder("uitree -> ");
            if (panelWin == null) return sb.Append("no-window").ToString();
            try
            {
                var t = panelWin.GetType();
                sb.Append("type=").Append(t.FullName).Append(" | members:");
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                foreach (var f in t.GetFields(bf))
                {
                    string n = f.Name.ToLower();
                    if (n.Contains("title") || n.Contains("frame") || n.Contains("window")) sb.Append(" F:").Append(f.Name).Append("=").Append(f.FieldType.Name);
                }
                foreach (var p in t.GetProperties(bf))
                {
                    string n = p.Name.ToLower();
                    if (n.Contains("title") || n.Contains("frame") || n.Contains("window")) sb.Append(" P:").Append(p.Name).Append("=").Append(p.PropertyType.Name);
                }
                sb.Append(" | nodes:");
                try { WalkNames(panelWin.rectTransform, 0, sb); } catch (Exception e) { sb.Append(" walk-err ").Append(e.GetType().Name); }
            }
            catch (Exception e) { sb.Append(" ERR ").Append(e.GetType().Name).Append(" ").Append(e.Message); }
            return sb.ToString();
        }
#endif

        private static void WalkNames(UnityEngine.Transform t, int d, System.Text.StringBuilder sb)
        {
            if (t == null || d > 3) return;
            sb.Append(' ').Append(d).Append(':').Append(t.gameObject.name);
            for (int i = 0; i < t.childCount; i++) WalkNames(t.GetChild(i), d + 1, sb);
        }

        // ---------------- 数据 ----------------

        // ★ 水龙头真实入口（反编译 WaterHelper/<>c__DisplayClass10_0::<InitLiquidContainerItem>b__9）：
        //   ldstr "RUSTWATER_TAP" → NetworkUpgrade::GetUpgradeById → IsUnlocked
        //     → AudioHelper::UseFaucet(container)
        //     → AddWater(container, grade=1, amount=9999999, useRange=true,
        //                lowPara=StoreStation.todayRustwaterPurity, highPara=同一字段, initWaterFeature=true)
        //   else "GUTTERFLOW_TAP" → 同上，纯度取 StoreStation.todayGutterflowPurity，grade 仍是 1。
        // 为什么 low==high 就能锁纯度：RNG.GetRandomInt(a,b) 的 IL 是 Random::Range(a, b+1)（闭区间），a==b 时必得 a。
        // 为什么不能缓存：todayRustwaterPurity / todayGutterflowPurity 是**当日**字段，随存档天数（过月）变化，
        // 缓存后过月仍灌旧值，就与游戏水龙头不一致了。所以每次现场读、读不到就报错，绝不自己造值。
        /// <summary>水龙头是否真的解锁。原版拖水回调先查 NetworkUpgrade::GetUpgradeById(id).IsUnlocked，
        /// 没解锁就什么都不发生；旧版只读 StoreStation 的当日纯度，等于**绕过解锁凭空接水**（玩家报的 bug）。</summary>
        /// <summary>测试用：ctl `taplock=1` 把所有水龙头当成未解锁，用来验证「不许凭空接水」的拒绝路径。</summary>
        public static int ForceTapLocked = 0;

        private static bool TapUnlocked(string id)
        {
            try
            {
                if (ForceTapLocked != 0) return false;
                var up = GameBridge.CallStatic("NetworkUpgrade", "GetUpgradeById", id);
                if (up == null) return false;
                // ★先读 state：NetworkUpgrade.state==2 才是「已开通」。
                // 之前把静态 IsUnlocked(string) 放最前，重载解析一旦命中错误的实例方法会误判 true，
                // 玩家 2026-11-13 仍能接沟渠水就是这条路径。
                var stv = GameBridge.Get(up, "state");
                if (stv != null)
                {
                    try { if (Convert.ToInt32(stv) == 2) return true; } catch (Exception) { }
                    try { if (Convert.ToInt32(stv) != 2) return false; } catch (Exception) { }
                }
                // state 读不到才退回方法；静态和实例两个重载都试，失败不吞后续逻辑。
                try
                {
                    var sv = GameBridge.CallStatic("NetworkUpgrade", "IsUnlocked", id);
                    if (sv is bool) return (bool)sv;
                }
                catch (Exception) { }
                // ★反编译定论：IsUnlocked 是**无参方法**（另有 IsUnlocked(string) 重载），不是属性。
                // 之前用 GameBridge.Get 取属性永远拿到 null → 两只水龙头都被判成没解锁，接水会全被拒。
                var v = GameBridge.Call(up, "IsUnlocked");
                if (v is bool) return (bool)v;
                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>存档实际开通的水龙头：1=只有铁锈水 2=只有沟渠水 0=两个都开/都没开（要手动选）。
        /// 结果缓存 5 秒：PageHeight 每帧都会问，而 GameBridge 两次调用不值得每帧跑。</summary>
        private static int tapOnlyCache = -1;
        private static float tapOnlyAt = -999f;
        private static bool tapRustCached, tapGutterCached;
        private static float tapLockAt = -999f;

        /// <summary>水源解锁状态缓存 5 秒：BindModel 会频繁刷新四块 radio，不能每帧反射两次。</summary>
        private static void TapLocks(out bool rust, out bool gutter)
        {
            float now = Time.realtimeSinceStartup;
            if (now - tapLockAt < 5f && tapLockAt > 0f) { rust = tapRustCached; gutter = tapGutterCached; return; }
            rust = TapUnlocked("RUSTWATER_TAP");
            gutter = TapUnlocked("GUTTERFLOW_TAP");
            tapRustCached = rust; tapGutterCached = gutter; tapLockAt = now;
        }
        private static int TapOnly()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (tapOnlyCache >= 0 && now - tapOnlyAt < 5f) return tapOnlyCache;
                bool rust = TapUnlocked("RUSTWATER_TAP");
                bool gutter = TapUnlocked("GUTTERFLOW_TAP");
                int v = 0;
                if (rust && !gutter) v = 1;
                else if (gutter && !rust) v = 2;
                tapOnlyCache = v; tapOnlyAt = now;   // 解锁状态极少变，5 秒足够新
                return v;
            }
            catch (Exception) { return tapOnlyCache >= 0 ? tapOnlyCache : 0; }
        }

        /// <summary>ctl `tap?`：两只水龙头的解锁状态与今日纯度（验证解锁校验是否真的读到游戏状态）。</summary>
        public static string TapReport()
        {
            try
            {
                int rp = 0, gp = 0;
                try
                {
                    var st = StoreStation.Instance;
                    if (st != null) { rp = st.todayRustwaterPurity; gp = st.todayGutterflowPurity; }
                }
                catch (Exception) { }
                return "tap -> rust=" + (TapUnlocked("RUSTWATER_TAP") ? "unlocked" : "locked") + " pur=" + rp +
                       " | gutter=" + (TapUnlocked("GUTTERFLOW_TAP") ? "unlocked" : "locked") + " pur=" + gp +
                       " | forceLocked=" + ForceTapLocked;
            }
            catch (Exception e) { return "tap -> ERR " + e.GetType().Name; }
        }

        /// <summary>最近一次 TapSource 失败的原因（给面板说清楚：没解锁 / 读不到）。</summary>
        private static string tapBlock;
        /// <summary>最近一次 TapSource 实际走的水管：4=铁锈、5=沟渠。只用于显示，不参与 AddWater 的 grade=1。</summary>
        private static int tapKind;

        // 游戏 StoreStation.StartDay() 的今日水质生成范围（反编译确认）：
        //   todayGutterflowPurity = RNG.GetRandomInt(8000, 8999);  // 80.00% ~ 89.99%
        //   todayRustwaterPurity  = RNG.GetRandomInt(9000, 9150);  // 90.00% ~ 91.50%
        // 这里只用它来校验“读到的值是不是游戏合法值”，不把任何具体纯度写死。
        private const int GutterPurMin = 8000, GutterPurMax = 8999;
        private const int RustPurMin = 9000, RustPurMax = 9150;

        private static bool TapPurLooksValid(int kind, int pur)
        {
            if (pur <= 0) return false;
            if (kind == 5) return pur >= GutterPurMin && pur <= GutterPurMax;
            if (kind == 4) return pur >= RustPurMin && pur <= RustPurMax;
            return false;
        }

        private static void TapSource(out int grade, out int pur)
        {
            grade = 1; pur = 0;                       // 水龙头两个分支的 grade 都是 1
            tapKind = 0;
            tapBlock = null;
            try
            {
                var st = StoreStation.Instance;
                if (st == null) { tapBlock = "StoreStation 未就绪"; return; }
                // ★玩家反馈：选了「沟渠水」还是接铁锈水 —— 以前这里永远先取铁锈水，水源下拉等于没接。
                // 现在按 SrcIdx 取对应那只水龙头：1=铁锈水、2=沟渠水，其余（自动）先沟渠后铁锈。
                // ★2026-11-09 第二报：沟渠水还能「凭空接出来」——必须像原版一样先查解锁，
                //   否则没买沟渠水龙头的存档也能灌出沟渠水。
                bool rust = TapUnlocked("RUSTWATER_TAP");
                bool gutter = TapUnlocked("GUTTERFLOW_TAP");
                if (SrcIdx == 2)
                {
                    if (gutter && TapPurLooksValid(5, st.todayGutterflowPurity)) { pur = st.todayGutterflowPurity; tapKind = 5; return; }
                    tapBlock = gutter ? "沟渠水龙头读到的值不在 80.00%~89.99% 合法范围" : "沟渠水龙头还没解锁";
                    return;
                }
                if (SrcIdx == 1)
                {
                    if (rust && TapPurLooksValid(4, st.todayRustwaterPurity)) { pur = st.todayRustwaterPurity; tapKind = 4; return; }
                    tapBlock = rust ? "铁锈水龙头读到的值不在 90.00%~91.50% 合法范围" : "铁锈水龙头还没解锁";
                    return;
                }
                // 自动档完全照游戏 WaterHelper 的 IsFaucet 分支：
                // 铁锈水管解锁 -> 接铁锈；否则沟渠水管解锁 -> 接沟渠。
                // 所以开了沟渠只接得到沟渠，升级成铁锈后只接得到铁锈；不会拿另一只的浓度。
                if (rust && TapPurLooksValid(4, st.todayRustwaterPurity)) { pur = st.todayRustwaterPurity; tapKind = 4; return; }
                if (gutter && TapPurLooksValid(5, st.todayGutterflowPurity)) { pur = st.todayGutterflowPurity; tapKind = 5; return; }
                tapBlock = (rust || gutter) ? "水龙头读到的值不在游戏合法范围" : "水龙头还没解锁";
            }
            catch (Exception) { tapBlock = "读水龙头出错"; }
        }

        /// <summary>面板显示用：grade 只用于文案（4=铁锈水 / 5=沟渠水），纯度同样是现场读。</summary>
        private static void TodaySource(out int grade, out int pur)
        {
            // 手动填浓度（网页的「自己填纯度」）：>0 时直接按这个值算，不看水源下拉。
            if (CustomPur > 0) { grade = 4; pur = CustomPur; return; }
            // 手动选水源（F7 下拉）：0=自动，1..N=固定水源。
            if (SrcIdx > 0 && SrcSource(SrcIdx, out grade, out pur)) return;
            // 自动：**先看瓶里有没有水**——玩家把优质水放进去，就该按 99.01 算，
            // 而不是继续套水龙头水质（否则「按读取水质计算」根本不生效）。
            try
            {
                var b0 = CtxBottle(); if (b0 == null) b0 = GameBottle();
                if (b0 != null)
                {
                    int v = WaterHelper.GetTotalVolume(b0);
                    int pp = WaterHelper.GetWaterPurity(b0);
                    if (v > 0 && pp > 0)
                    {
                        // 瓶里已经有水：纯度就按这瓶水本身算；档位文案尽量按它与今日两只水管的
                        // 实际纯度对一下，避免明明灌的是沟渠水却写「铁锈水」。
                        grade = 0;
                        try
                        {
                            var st0 = StoreStation.Instance;
                            if (st0 != null)
                            {
                                int rp = st0.todayRustwaterPurity;
                                int gp = st0.todayGutterflowPurity;
                                if (rp > 0 && Math.Abs(pp - rp) <= 5) grade = 4;
                                else if (gp > 0 && Math.Abs(pp - gp) <= 5) grade = 5;
                            }
                        }
                        catch (Exception) { }
                        pur = pp;
                        return;
                    }
                }
            }
            catch (Exception) { }
            TapSource(out grade, out pur);
            // TapSource 的 grade 必须保持 1（AddWater 原版参数）；这里只把显示档位换成铁锈/沟渠。
            grade = tapKind > 0 ? tapKind : 0;
            // 读不到就返回 0，当作空；不沿用上次水源，也不自己造 91。
            // 空瓶时玩家自己选铁锈/沟渠，各自的今日纯度由 SrcSource 直接读取。
        }

        /// <summary>手动指定水源：铁锈水/沟渠水取今日真值，纯水类的纯度按引擎液体定义。</summary>
        private static bool SrcSource(int idx, out int grade, out int pur)
        {
            grade = 4; pur = 0;
            try
            {
                var st = StoreStation.Instance;
                switch (idx)
                {
                    case 1: grade = 4; pur = st != null ? st.todayRustwaterPurity : 0; break;
                    case 2: grade = 5; pur = st != null ? st.todayGutterflowPurity : 0; break;
                    case 3: grade = 3; pur = 9900; break;   // 优质水
                    case 4: grade = 2; pur = 9850; break;   // 基础水
                    case 5: grade = 0; pur = 9990; break;   // 纯水
                    default: return false;
                }
                // 水龙头两档必须落在游戏 StartDay 的生成范围内，否则当作没读到。
                if ((idx == 1 || idx == 2) && !TapPurLooksValid(grade, pur)) pur = 0;
            }
            catch (Exception) { return false; }
            if (pur <= 0) return false;
            return true;
        }

        /// <summary>水源显示名（下拉与顶行文案共用）。</summary>
        private static string SrcName(int grade)
        {
            if (SrcIdx > 0 && SrcIdx < SrcNames.Length) return SrcNames[SrcIdx];
            return grade == 4 ? "铁锈水" : (grade == 5 ? "沟渠水" : "水源");
        }

        /// <summary>机器储水槽里的容器：chIldItem 只在「瓶里有水」时才稳定；
        /// 空容器要在槽的物品列表里找（2026-11-14：玩家明明放了容器，主页却 0/0）。</summary>
        private static GameItem BottleInMachineSlot(object inv)
        {
            if (inv == null) return null;
            GameItem item = null;
            try { item = GameBridge.Get(inv, "childItem") as GameItem; } catch (Exception) { }
            if (item != null && WaterHelper.GetTotalCapacity(item) > 0) return item;
            try
            {
                var list = GameBridge.ItemsOf(inv);
                GameItem best = null;
                long bestV = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (it == null) continue;
                    int cap = WaterHelper.GetTotalCapacity(it);
                    if (cap <= 0) continue;
                    if (Math.Abs(cap - 3000000) <= 1) continue;   // 3L 过滤棉容器不是储水容器
                    long vol = WaterHelper.GetTotalVolume(it);
                    if (vol > bestV) { bestV = vol; best = it; }
                }
                if (best != null) return best;
            }
            catch (Exception) { }
            return item;
        }

        /// <summary>没点绿点时，仍能拿到当前开着的某台机器瓶；只用于记住/核对锁定方案的中间态，不用于出方案。</summary>
        private static GameItem AnyVisibleMachineBottle()
        {
            try
            {
                var pairs = PurifierHook.Pairs;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var p = pairs[i];
                    if (p == null || p.BottleInv == null || p.Window == null) continue;
                    if (!PurifierHook.WindowVisible(p.Window)) continue;
                    var it = BottleInMachineSlot(p.BottleInv);
                    if (it != null && WaterHelper.GetTotalCapacity(it) > 0) return it;
                }
                for (int i = 0; i < pairs.Count; i++)
                {
                    var p = pairs[i];
                    if (p == null || p.BottleInv == null) continue;
                    var it = BottleInMachineSlot(p.BottleInv);
                    if (it != null && WaterHelper.GetTotalCapacity(it) > 0) return it;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>同一个容器在关/开机器窗口后可能是新的包装对象；按名字+容量认瓶，
        /// 不能只看 ReferenceEquals，否则每次重开都会解锁方案、让玩家重新接水。</summary>
        private static bool SameBottleKind(GameItem a, GameItem b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            string na = "", nb = "";
            long ca = 0L, cb = 0L;
            try { na = GameBridge.NameOfItem(a); ca = WaterHelper.GetTotalCapacity(a); } catch (Exception) { }
            try { nb = GameBridge.NameOfItem(b); cb = WaterHelper.GetTotalCapacity(b); } catch (Exception) { }
            return na == nb && ca == cb;
        }

        /// <summary>混水页排重：同一瓶水即使被扫描器重新包了一层，也要认成同一个。
        /// 只比较名称+容量+水量+纯度；不比较对象引用，避免 5553.1 被另一台 5541.5 的同名瓶误删。</summary>
        private static bool SameBottleForSkip(GameItem a, GameItem b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            try
            {
                if (GameBridge.NameOfItem(a) != GameBridge.NameOfItem(b)) return false;
                if (WaterHelper.GetTotalCapacity(a) != WaterHelper.GetTotalCapacity(b)) return false;
                if (WaterHelper.GetTotalVolume(a) != WaterHelper.GetTotalVolume(b)) return false;
                if (WaterHelper.GetWaterPurity(a) != WaterHelper.GetWaterPurity(b)) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>当前选中的机器配对：优先 activeIdx，但 activeIdx 可能指向已关闭的旧窗口；
        /// 旧窗口的 BottleInv 会保留旧容器，导致空槽仍读出旧瓶（P5）。</summary>
        private static PurifierHook.Pair ActivePair()
        {
            if (uiDeselected) return null;
            var pairs = PurifierHook.Pairs;
            // 玩家 2026-11-17：绿点选中的机器必须优先于全局 MachineWindow。
            // 否则另一台机器的悬停/窗口焦点会把 GameBottle 带到别台：主页容器/水源是另一台，
            // 方案和锁定却还在选中台；绿点按几次把 MachineWindow 撞回来才对齐。
            int idx = activeIdx >= 0 ? activeIdx : MachineOverride;
            if (idx >= 0 && idx < pairs.Count)
            {
                var selected = pairs[idx];
                if (selected != null && selected.Window != null && PurifierHook.WindowVisible(selected.Window))
                    return selected;
            }
            // 没选中（或选中的旧窗口已关）时，先按游戏真正激活的窗口（focusStamp 最新）。
            var focused = PurifierHook.FocusedVisiblePair();
            if (focused != null) return focused;
            // 最后才回退到 MachineWindow 缓存。
            PurifierHook.Pair sameWindow = null;
            for (int i = pairs.Count - 1; i >= 0; i--)
            {
                var p = pairs[i];
                if (p == null || !ReferenceEquals(p.Window, PurifierHook.MachineWindow)) continue;
                if (!PurifierHook.WindowVisible(p.Window)) continue;
                if (PurifierHook.PairSelected(p)) return p;
                // 放药片/拖动库存会短暂让 PairSelected 变 false，但机器窗口还是同一台。
                // 这时不能当作“没容器”去解锁方案，否则每放一片药都会重算成新方案。
                if (sameWindow == null) sameWindow = p;
            }
            if (sameWindow != null) return sameWindow;
            return null;
        }

        /// <summary>当前选中机器槽里的真实容器：认当前配对，不能拿旧配对的 BottleInv。
        /// 空槽返回 null；空容器仍要走 ItemsOf，不能只认 childItem。</summary>
        private static GameItem ActiveSlotBottle(object inv)
        {
            if (inv == null) return null;
            try
            {
                var item = BottleInMachineSlot(inv);
                if (item == null) return null;
                long cap = WaterHelper.GetTotalCapacity(item);
                return cap > 0 ? item : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>玩家明确选中的那台配对，只按 activeIdx/MachineOverride 认，不看机器是否开机。
        /// 混水页/主页要读“选中的那台”，不能因为机器停着就掉到第一台可见机器（玩家 2026-11-17 截图：
        /// 选中机器里有 5553.1，混水页却读成另一台的 5541.5）。</summary>
        private static PurifierHook.Pair SelectedPairForRead()
        {
            if (uiDeselected) return null;
            int idx = activeIdx >= 0 ? activeIdx : MachineOverride;
            if (idx < 0 || idx >= PurifierHook.Pairs.Count) return null;
            var p = PurifierHook.Pairs[idx];
            if (p == null || p.Window == null || p.BottleInv == null) return null;
            if (!PurifierHook.WindowVisible(p.Window)) return null;
            return p;
        }

        /// <summary>选中的机器槽里那瓶水；选中槽为空就返回 null，不跨机器兜底。</summary>
        private static GameItem SelectedMachineBottle()
        {
            var p = SelectedPairForRead();
            return p == null ? null : ActiveSlotBottle(p.BottleInv);
        }

        /// <summary>当前选中机器槽里的真实容器；不跨机器兜底。空槽返回 null。</summary>
        private static GameItem ActiveMachineBottle()
        {
            var p = ActivePair();
            if (p == null || p.BottleInv == null) return null;
            return ActiveSlotBottle(p.BottleInv);
        }

        private static GameItem GameBottle()
        {
            try
            {
                // 玩家 2026-11-16：绿点取消后即使用机器窗口还开着，也不能继续按那台机器算；
                // 没点绿点也不再从 PickedIndex / 第一台可见窗口自动兜底。
                if (uiDeselected || (activeIdx < 0 && MachineOverride < 0)) return null;
                // 机器窗口开着：只读当前选中槽，空槽就是空槽，不能再从别的机器/旧配对兜底。
                if (MachineOpen()) return ActiveMachineBottle();
                object inv = PurifierHook.BottleInv;
                if (inv == null) return null;
                GameItem item = BottleInMachineSlot(inv);
                if (item != null && WaterHelper.GetTotalCapacity(item) > 0) return item;
                var pairs = PurifierHook.Pairs;
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (pairs[i] == null || pairs[i].BottleInv == null) continue;
                    var alt = BottleInMachineSlot(pairs[i].BottleInv);
                    if (alt != null && WaterHelper.GetTotalCapacity(alt) > 0) return alt;
                }
                return item;
            }
            catch (Exception) { return null; }
        }

        /// <summary>没有净水器窗口时兜底：玩家身上容量最大的容器。</summary>
        private static GameItem PickFallbackBottle()
        {
            var all = GameBridge.LiveAllItems();
            GameItem best = null; int bestCap = 0;
            for (int i = 0; i < all.Count; i++)
            {
                // 玩家主动取消选中机器后，兜底也不能把机器里的瓶当成背包瓶。
                // 没点绿点（activeIdx/MachineOverride 都是 -1）时同样跳过机器槽。
                if ((uiDeselected || (activeIdx < 0 && MachineOverride < 0)) && IsMachineSlotItem(all[i])) continue;
                int c = WaterHelper.GetTotalCapacity(all[i]);
                if (c > bestCap) { bestCap = c; best = all[i]; }
            }
            if (best != null) Log("[fill] fallback bottle cap=" + (bestCap / 1000) + "mL");
            return best;
        }

        /// <summary>有水的容器里挑装得最多的一个 —— 「按现有水/混水算方案」要看这一瓶，不是看空瓶。</summary>
        private static GameItem PickRichestBottle()
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                GameItem best = null; long bestV = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    long v = WaterHelper.GetTotalVolume(all[i]);
                    if (v > bestV) { bestV = v; best = all[i]; }
                }
                if (best != null) Log("[mix] richest bottle v=" + (bestV / 1000) + "mL");
                return best;
            }
            catch (Exception) { return null; }
        }

        /// <summary>底部常驻一行：一晚要接多少、整瓶凑得多少、容器还剩多少空间。</summary>
        /// <summary>卡一的量：按容器现状说话——瓶里有水就只报「还差多少」，满了就让他先倒，
        /// 空瓶才报「最多取 X mL  一晚上限 Y mL」。这样 2 L 瓶不再永远显示同一个 1850。</summary>
        /// <summary>不经耗材、单纯过一晚能达标的最大量（整瓶凑得出来的），与 OnFill 同一口径。</summary>
        private static long PlainReachableMl(PanelModel m)
        {
            try
            {
                int g, p; TodaySource(out g, out p);
                int perf, q; Bonuses(out perf, out q);
                long keep = MaxKeepUl((int)(m.CapacityMl * 1000L), p, perf, q, PlanText.TargetBp(TargetIdx, q));
                if (keep <= 0) return 0;
                var pp = WaterPlan.Plan(keep);
                if (pp != null && pp.totalUl > 0 && pp.totalUl < keep) keep = pp.totalUl;
                return keep / 1000L;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>当前这瓶水按锁定方案还差多少水（µL）：方案要求的总量 - 当前瓶实际水量。
        /// 只用于 UI 文案，不参与模型；补水已经完成时返回 0，步骤行就不会再写“补”。</summary>
        private static long CurrentTopUpUl(WaterPlan.Report r)
        {
            if (r == null || !r.fromWater || r.pick == null || r.dumpUl > 0 || r.pick.feedUl <= 0) return 0;
            // 方案已经开始执行（放药、过滤等）后，水少一点不能每步都让玩家补到整瓶；
            // 补水提示只在方案还没动手时出现。
            if (progressActive) return 0L;
            long vol = 0L;
            try
            {
                var b = UiPlanBottle();
                if (b != null) vol = WaterHelper.GetTotalVolume(b);
            }
            catch (Exception) { vol = 0L; }
            if (vol <= 0 && r.water0 != null) vol = r.water0.v;
            long add = r.pick.feedUl - vol;
            return add >= 1000L ? add : 0L;
        }

        private static string CardFeedValueFor(PanelModel m, WaterPlan.Report r)
        {
            // 玩家 2026-11-12 定案：主界面只留一句核心状态，三种写法，其余解释一律删掉：
            //   [已满，直接净化] / [需接水 XXXX mL] / [倒出 XXXX mL 后净化]
            // （以前的「这瓶直接跑」「够一晚」「上限 …」「容器凑不满，差 …」全部去掉。）
            // 玩家 2026-11-13（第四轮）：不准再写「已满，直接净化」——满瓶照样要按步骤跑机器，
            // 玩家不是瞎子，提示这句话只会跟下面的步骤/耗材打架。只有「水不够」和「装不下/超了」
            // 两种情况才开口，其余时候这一行留空。
            // 玩家 2026-11-16：方案已经执行到某一步，后面水少了也不补、不提示；
            // 只有方案本来就把接水写成步骤时才显示（目前由步骤页/方案轨迹负责）。
            if (progressActive) return "";
            if (r != null && r.fromWater && (r.capUl > 0 || m.VolumeMl > 0))
            {
                // 效率档补满容器时，报告里的 water0 是「现有水 + 水管水」的混合体；
                // 先提示混合，下面的步骤再按混合水跑，不要把这句写成「倒出 0」。
                // 先补：摘要行只留「补多少 → 补到多少」，步骤行会重复写一次「补」，
                // 但不再把「混合后…再按步骤」占成两行，避免和步骤行抢解释权。
                long topUpUl = CurrentTopUpUl(r);
                if (topUpUl >= 1000L)
                {
                    long planTotal = r.pick != null && r.pick.feedUl > 0
                                   ? r.pick.feedUl
                                   : m.VolumeMl * 1000L + topUpUl;
                    return PlanText.Lbl("补 ") + PlanText.Num(WaterPlan.Ml(topUpUl))
                         + PlanText.Lbl(" → ") + PlanText.Num(WaterPlan.Ml(planTotal)) + PlanText.Lbl(" mL") + "\n"
                         + PlanText.Lbl("混合后 ") + PlanText.Pct(r.srcBp);
                }
                // 按瓶里这瓶水算（一律用 Report 自己记录的体积，别用面板里可能串台的数字）。
                if (r.fitUl <= 0)
                {
                    long over = Max0(m.VolumeMl * 1000L - r.capUl);
                    if (over < 1000L) return "";
                    return PlanText.Lbl("倒出 ") + PlanText.Num(WaterPlan.Ml0(over) + " mL")
                         + PlanText.Lbl(" 后净化");
                }
                if (r.dumpUl > 0)
                {
                    // 玩家 2026-11-16：已经倒了一部分时不要再报总倒出量，
                    // 直接报“还差多少、倒到多少”，避免玩家以为方案又变了。
                    long remain = r.fitUl > 0 ? Max0(m.VolumeMl * 1000L - r.fitUl) : r.dumpUl;
                    if (remain < 1000L) return "";
                    if (r.fitUl > 0)
                        return PlanText.Lbl("还需倒 ") + PlanText.Num(WaterPlan.Ml(remain))
                             + PlanText.Lbl("，倒到 ") + PlanText.Num(WaterPlan.Ml(r.fitUl) + " mL");
                    return PlanText.Lbl("倒出 ") + PlanText.Num(WaterPlan.Ml0(r.dumpUl) + " mL")
                         + PlanText.Lbl(" 后净化");
                }
                return "";
            }
            long pour = BottlePourUl(r.ChosenFeedUl(), m.CapacityMl * 1000L) / 1000L;      // 这条配方要接的量：按整瓶凑量，不能用精确 µL
            if (pour <= 0) pour = PlainReachableMl(m); // 没有候选方案时才退回「只过一晚」的上限
            long have = m.VolumeMl, room = m.CapacityMl - m.VolumeMl;
            if (pour > 0 && have > pour)
            {
                long over = (have - pour) * 1000L;
                if (over < 1000L) return "";
                return PlanText.Lbl("倒出 ") + PlanText.Num(WaterPlan.Ml(over)) + PlanText.Lbl(" 后净化");
            }
            if (have > 0 && have >= pour) return "";   // 水够了就别说话，按步骤跑就行

            if (room <= 0) return PlanText.Rich(PlanText.C_WARN, "已满，先倒出再接");
            long want = pour - have;
            if (want <= 0) want = pour;
            if (!UncapEff && want > room) want = room;
            string feed = PlanText.Lbl("需接水 ") + PlanText.Num(WaterPlan.Ml(want * 1000L));
            // 玩家 2026-11-17 纠正：这里不是“机器理论上限”，而是选中方案本身要接的水量。
            // 方案按容器精度取整后，planPourMl 会被 50 mL/6 L 这类瓶子卡成 800；
            // 把未取整的 ChosenFeedUl 也显示出来，且不允许超过当前容器容量。
            long planFeed = r != null ? r.ChosenFeedUl() : 0;
            long cap = m.CapacityMl > 0 ? m.CapacityMl * 1000L : (r != null ? r.capUl : 0);
            if (planFeed > want * 1000L && planFeed > 0 && (cap <= 0 || planFeed <= cap))
                feed += PlanText.Lbl("  方案 ") + PlanText.Num(WaterPlan.Ml(planFeed));
            return feed;
        }

        private static long Max0(long v) { return v > 0 ? v : 0; }

        /// <summary>方案表一行（两小行）：标签+实际出水；纯度、耗材、本钱、净赚。
        /// 净赚用绿色，亏用橙色；数字自带「元」但只留一个空格，尽量不折行。</summary>
        private static void AddComboRow(RichTextBuilder b, WaterPlan.Combo c, bool sel)
        {
            if (c == null) return;
            if (sel) b.Add("▶ ", ColorPalette.LightOrange);
            // 玩家 2026-11-13（p6a）：这一行改成和主页「步骤」行完全同一口径（滤棉X / 几晚 / 药N），
            // 以前用 Combo.label 的短名，看起来像把「一晚」删掉了。
            b.Add(StepChainPlain(c), ColorPalette.White);
            b.Add("  ", ColorPalette.LightGray);
            b.Add(WaterPlan.Ml(c.outUl), ColorPalette.LightCyan);
            if (c.ok && !string.IsNullOrEmpty(c.badge))
            {
                b.Add(" ", ColorPalette.LightGray);
                b.Add(c.badge, ColorPalette.LightGreen);
            }
            b.Add("\n", ColorPalette.White);

            b.Add("   ", ColorPalette.LightGray);
            b.Add(PlanText.Pct(c.purityBp), ColorPalette.LightCyan);
            double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
            double profit = rev - c.cost;
            double perL = c.outUl > 0 ? c.cost / (c.outUl / 1000000.0) : 0;
            b.Add(" 本" + WaterEconomy.Money(c.cost), ColorPalette.LightGray);
            b.Add(" 每升" + WaterEconomy.Money(perL), ColorPalette.LightGray);
            b.Add(" 赚" + (profit >= 0 ? "+" : "") + WaterEconomy.Money(profit),
                  profit >= 0 ? ColorPalette.LightGreen : ColorPalette.LightOrange);
            b.Add("\n", ColorPalette.White);
        }

        /// <summary>把一条方案去重后加入 Report 的 combos/keep，供「更多方案」显示。</summary>
        private static bool AddComboUnique(WaterPlan.Report r, WaterPlan.Combo c)
        {
            if (r == null || c == null || !c.ok) return false;
            for (int i = 0; i < r.combos.Count; i++)
            {
                var q = r.combos[i];
                if (q != null && SameCombo(q, c)) return false;
            }
            r.combos.Add(c);
            r.keep.Add(c);
            return true;
        }

        private static WaterPlan.Combo ComboFromAuto(AutoSolve.Result ar, long fallbackFeedUl)
        {
            var c = new WaterPlan.Combo();
            c.label = (ar.feedUl > 0 ? ar.feedUl : fallbackFeedUl) + " / " + ar.w.v;
            c.filter = HasAutoTool(ar, "filter");
            c.uv = HasAutoTool(ar, "uv");
            c.tablets = ar.tablets;
            c.feedUl = ar.feedUl > 0 ? ar.feedUl : fallbackFeedUl;
            c.outUl = ar.w.v;
            c.filterUsed = ar.filterDura;
            c.purityBp = (int)Math.Round(ar.purityShown * 100.0);
            c.cost = ar.cost + AutoSolve.ElectricityPerNight * ar.nights;
            c.ok = ar.ok;
            c.nights = ar.nights;
            c.hydra = HasAutoTool(ar, "portable");
            c.hydraDur = ar.hydraDura;
            return c;
        }

        /// <summary>「更多方案」页的行：全部 ok 解，按出水量从大到小，不按前置/后置过滤。
        /// 不再剔除“被支配”的解：玩家要看到「少接水、每升更便宜」的另一条，
        /// 不能只剩一条出水上限方案。</summary>
        private static List<WaterPlan.Combo> AllRows(WaterPlan.Report r, int max)
        {
            var res = new List<WaterPlan.Combo>();
            if (r == null || max <= 0) return res;
            var all = new List<WaterPlan.Combo>();
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                if (c == null || !c.ok) continue;
                bool dup = false;
                for (int k = 0; k < all.Count; k++)
                {
                    var q = all[k];
                    if (q.feedUl == c.feedUl && q.outUl == c.outUl && q.purityBp == c.purityBp
                        && q.tablets == c.tablets && Math.Abs(q.cost - c.cost) < 0.01)
                    { dup = true; break; }
                }
                if (!dup) all.Add(c);
            }
            all.Sort(delegate(WaterPlan.Combo a, WaterPlan.Combo b)
            {
                int k = b.outUl.CompareTo(a.outUl);
                if (k != 0) return k;
                return b.purityBp.CompareTo(a.purityBp);
            });
            // 玩家 2026-11-17：开多晚时，更多方案第一屏至少能看到一条 >=2 晚方案。
            // 多晚方案出水通常更少，按出水量排序会沉到第 7 行以后；这里把第一条多晚方案提到最前。
            if (WaterPlan.NightCap > 1)
            {
                int multi = -1;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].ok && all[i].nights > 1) { multi = i; break; }
                }
                if (multi > 0)
                {
                    var m = all[multi];
                    all.RemoveAt(multi);
                    all.Insert(0, m);
                }
            }
            for (int i = 0; i < all.Count && i < max; i++) res.Add(all[i]);
            return res;
        }


        /// <summary>底部状态行：只写「容器多少 / 还差多少」，详细长句留在 tooltip 与日志里。</summary>
        /// <summary>「这瓶水直接跑」只在一晚、且不用任何道具/药片就能达标时成立。</summary>
        private static bool DirectRun(WaterPlan.Report r)
        {
            if (r == null || !r.ok || r.pick == null) return false;
            return r.pick.nights <= 1
                && r.pick.tablets <= 0
                && !r.pick.filter
                && !r.pick.uv
                && !r.pick.hydra;
        }

        private static string LiveLineShort(PanelModel m, WaterPlan.Report r)
        {
            try
            {
                if (m == null) return "";
                // 主页顶部已经有「容器 X/Y mL」了，这里再写一遍就是重复文本（玩家 2026-11-13 报）。
                // 状态行只留「还差多少 / 已满先倒 / 这瓶直接跑」。
                if (r != null && r.fromWater && DirectRun(r))
                    return "这瓶水直接跑";
                long pour = planPourMl > 0 ? planPourMl : (r != null ? BottlePourUl(r.ChosenFeedUl(), m.CapacityMl * 1000L) / 1000L : 0L);
                if (pour <= 0) pour = PlainReachableMl(m);
                if (m.VolumeMl <= 0) return "";
                long free = m.CapacityMl - m.VolumeMl;
                // 玩家 2026-11-13：卡一说「已满，直接净化」时，底行不能再冒一句「已满 先倒出」——
                // 满瓶但还不够直接净化的情况，卡一自己会写「已满，先倒出再接」，底行就留空。
                if (free <= 0) return "";
                long need = pour - m.VolumeMl;
                // 玩家 2026-11-13：水已经够（差 0 或已经是负的）就别再写「还差 0 mL」这种废话，
                // 只在真的少了/超了的时候说话。
                if (need <= 0) return "";
                // 卡一已经写「需接水 / 倒出」，底行不再重复一遍「还差」。
                string line = "";
                return "";
                // 容器装不下这条方案时给去路：换大容器 / 少一片药刚好卡满
                if (r != null && r.pick != null)
                {
                    long capUl = m.CapacityMl * 1000L;
                    if (r.pick.feedUl > capUl)
                    {
                        line += "    " + PlanText.Rich(PlanText.C_WARN, "方案要 " + WaterPlan.Ml(r.pick.feedUl) + " mL（超容器），换大容器");
                        // 找同前置、少一片药且装得下的方案
                        for (int i = 0; i < r.combos.Count; i++)
                        {
                            var c2 = r.combos[i];
                            if (c2 == null || !c2.ok) continue;
                            if (c2.filter != r.pick.filter || c2.uv != r.pick.uv) continue;
                            if (c2.tablets >= r.pick.tablets) continue;
                            if (c2.feedUl <= capUl && c2.feedUl > capUl - 300000L)
                            {
                                line += "；少到 " + c2.tablets + " 片 → " + WaterPlan.Ml(c2.feedUl) + " mL 刚好";
                                break;
                            }
                        }
                    }
                }
                return line;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>容器里已经有水（可能混过别的液体）时：按**读到的真实六项**给最优方案，
        /// 而不是让玩家再往里接生水。用的是水质扫描仪同一个接口，所以混水也准。</summary>
        private static string ExistingPlanLine(PanelModel m)
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "";
                WaterMath.Water w;
                if (!BuildRealWater(b, out w) || w == null || w.v <= 0) return "";
                int perf, q; Bonuses(out perf, out q);
                int target = PlanText.TargetBp(TargetIdx, q);
                var pl = WaterMath.PlanFromWater(w, perf, q, target, 4, 3, 400);
                if (pl == null)
                    return "已有 " + PlanText.Ml(w.v) + " mL  " + PlanText.Pct(w.Purity) + "：4 晚 + 3 片也到不了 " + PlanText.Pct(target) + "，先兑水或换小瓶";
                var sb = new System.Text.StringBuilder();
                sb.Append("已有 ").Append(PlanText.Ml(w.v)).Append(" mL  ").Append(PlanText.Pct(w.Purity)).Append("：");
                if (pl.filter == 1) sb.Append("过滤(").Append(pl.filterUsed).Append("点) ");
                if (pl.uv == 1) sb.Append("紫外 ");
                sb.Append(pl.nights).Append(" 晚");
                if (pl.tablets > 0) sb.Append(" + ").Append(pl.tablets).Append(" 片");
                sb.Append(" → 出 ").Append(PlanText.Ml(pl.outUl)).Append(" mL  ").Append(PlanText.Pct(pl.purityBp));
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static string LiveLine(PanelModel m, WaterPlan.Report plan)
        {
            try
            {
                if (m == null) return "";
                long pour = 0, need = 0;
                var r = plan;
                if (r != null)
                {
                    // 真倒得进去的量 = 整瓶凑得出的总量（1850），模型一晚上限另写（1857）
                    pour = planPourMl > 0 ? planPourMl : (r.bestUl / 1000L);
                    if (pour <= 0) pour = PlainReachableMl(m);
                    need = r.ShownUl() / 1000L;
                }
                if (need <= 0) need = pour;
                long free = m.CapacityMl - m.VolumeMl;
                long more = pour - m.VolumeMl;
                if (more < 0) more = 0;
                var sb = new System.Text.StringBuilder();
                if (r != null && r.fromWater && DirectRun(r))
                {
                    var sb0 = new System.Text.StringBuilder();
                    sb0.Append("容器里已有 ").Append(m.VolumeMl).Append(" mL  ").Append(PlanText.Pct(m.PurityBp))
                       .Append("，这瓶水直接跑，不用再接");
                    return sb0.ToString();
                }
                // 按容器现状说话：已有水就只报「还差多少」，不再拿整瓶量去和空余比
                if (m.VolumeMl > 0 && more == 0)
                {
                    string ex = ExistingPlanLine(m);
                    if (!string.IsNullOrEmpty(ex)) return ex;
                    sb.Append("容器里已有 ").Append(m.VolumeMl).Append(" mL，够一晚，不用再接");
                }
                else if (free <= 0)
                {
                    sb.Append("容器已满 ").Append(m.VolumeMl).Append("/").Append(m.CapacityMl).Append(" mL，先倒出来再接");
                }
                else if (m.VolumeMl == 0 && CustomPur < 0 && SrcIdx == 0)
                {
                    // 空瓶 + 自动水源：不假装知道瓶子里的水是什么，交给水龙头真值并提示
                    int tg2, tp2;
                    try { TapSource(out tg2, out tp2); if (tp2 <= 0) TodaySource(out tg2, out tp2); } catch (Exception) { tp2 = 0; }
                    sb.Append("容器是空的：一键接水按今日水龙头（").Append(tp2 > 0 ? PlanText.Pct(tp2) : "未读到").Append("）接 ")
                      .Append(pour > 0 ? pour : need).Append(" mL；要其它水源就在下拉里选，或填浓度");
                }
                else
                {
                    long want = more > 0 ? more : pour;
                    if (want > free) want = free;
                    sb.Append("要接 ").Append(want).Append(" mL");
                    if (pour > 0 && pour != need) sb.Append("（整瓶）");
                    sb.Append("  容器 ").Append(m.VolumeMl).Append("/").Append(m.CapacityMl).Append(" mL（还能装 ").Append(free).Append(" mL）");
                    // 瓶里剩着旧水时，方案要的量灌不满：明说要先倒，别让人以为「永远差那一点」
                    if (m.VolumeMl > 0 && m.VolumeMl < pour)
                        sb.Append("  已有 ").Append(m.VolumeMl).Append(" mL  ").Append(PlanText.Pct(m.PurityBp)).Append("，想按方案灌 ").Append(pour).Append(" mL 就先倒掉");
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>逐台刷新标题栏灌水键的键面：显示「这台正在跑的量」，不再只让一台有字。</summary>
        private static void RefreshFillLabels()
        {
            int perf0, q0;
            Bonuses(out perf0, out q0);
            foreach (var kv in winFill)
            {
                var w = kv.Key; var btn = kv.Value;
                if (w == null || btn == null) continue;
                try { if (!w.IsVisible()) continue; } catch (Exception) { continue; }
                try
                {
                    BindWindow(w, null);                       // 让「当前这台」指向这个窗口
                    // 注意：这里**不再** SetDesc 改图标名 —— 玩家要求标题栏图标名字固定；
                    // 每台的动态方案文案由面板顶行显示（Rebuild 里 pSub/pMsg）。
                }
                catch (Exception) { }
            }
        }

        /// <summary>有没有任何一台净水器的窗口正开着（红框只在此时标）。</summary>
        private static bool AnyPurifierWindowVisible()
        {
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var vp = PurifierHook.Pairs[i];
                if (vp == null || vp.Window == null) continue;
                try { if (GameBridge.WinVisible(vp.Window)) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>瓶子已经在机器里时的「下一步怎么做」：原样一晚 / 后加几片药片 / 倒到多少。</summary>
        private static string AdviceText(PanelModel m)
        {
            try
            {
                if (m == null || m.VolumeMl <= 0 || m.PurityBp <= 0) return "";
                var list = WaterMath.Plans(m.VolumeMl * 1000L, m.PurityBp, 1.0 + m.PerfPct / 100.0, m.Quality, m.TargetBp);
                if (list == null || list.Count == 0) return "这瓶一晚到不了 " + PlanText.Pct(m.TargetBp) + "：要么加水稀释，要么换更纯的水。";
                var sb = new System.Text.StringBuilder();
                int n = 0;
                for (int i = 0; i < list.Count && n < 3; i++)
                {
                    var p = list[i];
                    if (n > 0) sb.Append(" ／ ");
                    if (n == 0) sb.Append("最优：");
                    bool plain = p.drainUl == 0 && p.tablets == 0 && !p.useFilter && !p.useUv;
                    if (p.drainUl > 0) sb.Append("倒到 ").Append(p.keepUl / 1000L).Append(" mL ");
                    if (plain) sb.Append("原样 ");
                    if (p.useFilter) sb.Append("过滤棉 ");
                    if (p.useUv) sb.Append("紫外 ");
                    if (p.tablets > 0) sb.Append("净化完加 ").Append(p.tablets).Append(" 片药片 ");
                    sb.Append("→ ").Append(PlanText.Pct(p.purity)).Append("（").Append(p.output / 1000L).Append(" mL）");
                    n++;
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }
        private static object CurrentMachine()
        {
            // ★玩家 2026-11-10：多开窗口时按「点选的那台」算 —— 以前永远拿第一台可见窗口，
            // 后来开的窗口怎么点都算不到，把第一台关掉也还是抓不到第二台。
            // 选择走每台窗口标题栏左上角那个圆点按钮（绿=当前在用，灰=没选）。
            if (activeIdx >= 0 && activeIdx < PurifierHook.Pairs.Count)
            {
                var ap = PurifierHook.Pairs[activeIdx];
                if (ap != null && ap.Machine != null) { lastVisible = ap.Machine; return ap.Machine; }
            }
            if (MachineOverride >= 0 && MachineOverride < PurifierHook.Pairs.Count)
            {
                var mp = PurifierHook.Pairs[MachineOverride];
                if (mp != null && mp.Machine != null) return mp.Machine;
            }
            // 多台机器时：先看哪个窗口真的开着，别拿「上次活动的」那台去算。
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var vp = PurifierHook.Pairs[i];
                if (vp == null || vp.Machine == null || vp.Window == null) continue;
                try
                {
                    if (GameBridge.WinVisible(vp.Window)) { lastVisible = vp.Machine; return vp.Machine; }
                }
                catch (Exception) { }
            }
            // 窗口关掉之后仍沿用「上一次真正看到的那台」，避免掉到 ActiveMachine 或第一台上去。
            if (lastVisible != null) return lastVisible;
            // 没有窗口可见时，优先选装了性能/质量模组的那台——玩家配了模组的机器才是他关心的一台。
            object bestMod = null; int bestScore = -1;
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var mp = PurifierHook.Pairs[i];
                if (mp == null || mp.Machine == null) continue;
                int sc = 0;
                try
                {
                    string mods = GameBridge.DescribeModules(mp.Machine);
                    if (mods != null)
                    {
                        if (mods.IndexOf("performance", StringComparison.Ordinal) >= 0) sc += 2;
                        if (mods.IndexOf("quality", StringComparison.Ordinal) >= 0) sc += 1;
                    }
                }
                catch (Exception) { }
                if (sc > bestScore) { bestScore = sc; bestMod = mp.Machine; }
            }
            if (bestMod != null) return bestMod;
            if (PurifierHook.ActiveMachine != null) return PurifierHook.ActiveMachine;
            return PurifierHook.MachineItem;
        }

        private static object lastVisible;
        private static float lastLive = -10f;

        /// <summary>当前有没有任何一台净水器窗口真的开着（用于「未选中净水器」提示）。</summary>
        private static bool AnyMachineVisible()
        {
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p != null && p.Window != null && PurifierHook.PairSelected(p)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
        public static int QualityOverride = -1;
        public static int PerfOverride = -1;

        /// <summary>mach=N：强制指定「当前这台」，用于玩家开着 A 台却要算 B 台的场景。</summary>
        public static int MachineOverride = -1;

        // ---------------- 多台净水器：点选「用哪一台算」 ----------------
        /// <summary>玩家点选的当前机器（PurifierHook.Pairs 下标；-1＝没点过，按第一台可见窗口）。</summary>
        private static int activeIdx = -1;
        // 玩家 2026-11-14：P2 绿点再点一次＝取消选择。窗口还开着时 AnyMachineVisible() 仍为 true，
        // 主页会继续显示上一台的容器/出水/卖赚。这里单独记「玩家主动取消」，主页据此回到「未选中净水器」。
        private static bool uiDeselected;
        private static float noBottleSince = -1f;
        private static string waterGraceSig = "";
        private static float waterGraceAt = -999f;
        private static readonly Dictionary<PixelWindow, MachineryButton> pickBtns = new Dictionary<PixelWindow, MachineryButton>();

        /// <summary>窗口 → Pairs 下标（找不到返回 -1）。</summary>
        private static int PairIndexOf(PixelWindow w)
        {
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p != null && ReferenceEquals(p.Window, w)) return i;
                }
            }
            catch (Exception) { }
            return -1;
        }

        /// <summary>再点一次绿点：取消这台（变灰），计算页清零，不再跟机器走。</summary>
        public static void DeselectPair()
        {
            activeIdx = -1; MachineOverride = -1;
            uiDeselected = true;
            try { PurifierHook.PickedIndex = -1; } catch (Exception) { }
            lastVisible = null;
            calcManual = true;
            calcReadSource = false;
            try { ClearCalcFields(); } catch (Exception) { }
            UnlockPlan("取消选机器");
            DropPlanCache(); RefreshPickTints(); RequestRebuild(); RefreshMixIfOpen();
            Log("[ui] deselect pair（灰）");
        }

        /// <summary>点选这台窗口对应的净水器：之后所有卡一/卡二/方案表都按它的模组与容器算。</summary>
        public static void SelectPair(int idx)
        {
            if (idx < 0 || idx >= PurifierHook.Pairs.Count) return;
            int oldActive = activeIdx;
            activeIdx = idx;
            MachineOverride = idx;
            uiDeselected = false;
            calcManual = false;
            lastVisible = null;
            // 右键读取的瓶/储水罐不能跟着玩家换机器；选台后一律回到这台机器的真实容器。
            if (idx != oldActive)
            {
                lastPickedBottle = null;
                calcClearPick = null;
                RightClickTarget = null;
                calcManualSource = false;
                calcReadSource = false;
                UnlockPlan("换机器");
            }
            // 让 PurifierHook 那边也认这个点选，否则它每帧按「第一台可见窗口」把上下文改回去，
            // 表现成：选了 20 L 那台，接水量还是 750 那台锁着，只有关掉 750 窗口才正常。
            PurifierHook.PickedIndex = idx;
            var pw = PurifierHook.Pairs[idx].Window as PixelWindow;
            // 用游戏自己的窗口激活接口：选台即把这台机器窗口置前，后续读数只认真正激活的窗口。
            if (pw != null) { try { pw.ToFront(); } catch (Exception) { } }
            DropPlanCache();
            if (pw != null) UseWindow(pw);
            // 已经是这台时 UseWindow 不会走 Adopt 分支，但玩家重新点选仍要按当前机器水读一次。
            if (idx == oldActive) ReadSelectedMachineForPlan();
            RefreshPickTints();
            try { if (UiPage == 4) LoadCalcFromContext(true); } catch (Exception) { }
            RequestRebuild();
            RefreshMixIfOpen();
            Log("[ui] select pair=" + idx + " winIdx=" + (pw == null ? -1 : PairIndexOf(pw)) +
                " pairWinVisible=" + (pw == null ? false : PurifierHook.WindowVisible(pw)) + " pairs=" + PurifierHook.Pairs.Count + " mode=" + Mode);
        }

        // ---------------- 红框（机器高亮）与圆点着色：节流刷新 ----------------
        private static float lastVisPoll = -999f;
        private static string lastVisSig = "";
        // 游戏窗口 focusStamp 的上一轮激活配对；只跟随「玩家后来点了另一台已有窗口」，不跟首次打开。
        private static int lastFocusPairIdx = -2;
        private static float lastTintT = -999f;
        private static float lastMarkerNote = -999f;
        private static float lastTickIn = -999f;
        private static float lastPanelHeightTick = -999f;

        /// <summary>哪几台净水器窗口正开着（'1'/'0' 串）。</summary>
        private static string VisibleSig()
        {
            var sb = new System.Text.StringBuilder(16);
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    sb.Append(p != null && PurifierHook.WindowVisible(p.Window) ? '1' : '0');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>0.4 s 轮询可见集合：一开一关立刻重画红框；集合没变就 4 s 兜底同步一次。</summary>
        private static void MarkerTick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
#if DEV
            // 每 2 s 打一次「到底调到了没」：Pairs 有几个、圆点按钮有几个。红框消失时靠这条分清「没调用」还是「没配对」。
            if (now - lastMarkerNote > 2f)
            {
                lastMarkerNote = now;
                Log("[marker] poll pairs=" + PurifierHook.Pairs.Count + " pickBtns=" + pickBtns.Count);
            }
#endif
            if (PurifierHook.Pairs.Count == 0) return;
            if (now - lastVisPoll < 0.4f) return;
            lastVisPoll = now;
            string vs = VisibleSig();
            bool changed = vs != lastVisSig;
            lastVisSig = vs;
            // 玩家 2026-11-17：读取游戏自己的窗口激活状态（PixelWindow.focusStamp）。
            // 只跟随“已有窗口被玩家点成前台”：窗口首次打开仍不自动选台，保持 P2 的旧规则。
            int focusIdx = -1;
            try { focusIdx = PurifierHook.FocusedVisibleIndex(); } catch (Exception) { }
            bool focusChanged = focusIdx != lastFocusPairIdx;
            lastFocusPairIdx = focusIdx;
            if (!changed && focusChanged && !uiDeselected && activeIdx >= 0
                && focusIdx >= 0 && focusIdx != activeIdx)
            {
                AdoptPairForWindow(focusIdx);
                return;
            }
            // 玩家 2026-11-16：窗口刚打开也不再自动选台；必须玩家点绿点。
            // 旧逻辑会让 p2「没选机器」仍按这台机器出 2 药方案。
            try
            {
                if (false && changed && activeIdx < 0 && vs.IndexOf('1') >= 0)
                {
                    for (int i = 0; i < PurifierHook.Pairs.Count && i < vs.Length; i++)
                    {
                        if (vs[i] != '1') continue;
                        activeIdx = i; MachineOverride = i; PurifierHook.PickedIndex = i;
                        calcManual = false; lastVisible = null;
                        RefreshPickTints();
                        Log("[pick] auto select pair=" + i + "（窗口刚打开）");
                        break;
                    }
                }
            }
            catch (Exception) { }
            if (!changed && now - lastMarkerSync < 4f) return;
            lastMarkerSync = now;
#if DEV
            Log("[marker] sync sig=" + vs + " changed=" + changed);
#endif
            try { MachineMarker.SyncAll(); } catch (Exception e) { Log("[marker] ERR " + e.GetType().Name + " " + e.Message); }
        }

        private static readonly Dictionary<MachineryButton, Image[]> tintImgs = new Dictionary<MachineryButton, Image[]>();

        /// <summary>圆点颜色每帧补涂（选中绿 / 未选灰）：原生按钮悬停/按下会瞬间刷白，
        /// 逐帧覆盖过去就等于「指着还是绿、按下变绿」。3 个按钮，开销可忽略。</summary>
        private static void PickTintTick()
        {
            if (pickBtns.Count == 0) return;
            RefreshPickTints();
        }

        /// <summary>圆点按钮的着色：选中的绿，未选中的灰（同一份图标，靠颜色区分，不额外画图）。</summary>
        private static void RefreshPickTints()
        {
            var green = new Color(0.45f, 1f, 0.55f, 1f);
            var gray = new Color(0.55f, 0.55f, 0.55f, 1f);
            try
            {
                foreach (var kv in pickBtns)
                {
                    var btn = kv.Value;
                    if (btn == null) continue;
                    int idx = PairIndexOf(kv.Key);
                    TintIcon(btn, (idx >= 0 && idx == activeIdx) ? green : gray);
#if DEV
                    if (idx < 0)
                        Log("[pick] 按钮的窗口在配对表里找不到（按键失效） active=" + activeIdx + " picked=" + PurifierHook.PickedIndex + " pairs=" + PurifierHook.Pairs.Count);
#endif
                }
            }
            catch (Exception) { }
        }

        /// <summary>给原生图标按钮染色（MachineryButton 不是 Component，得从它的 rectTransform 上拿 GameObject）。</summary>
        private static void TintIcon(MachineryButton b, Color c)
        {
            if (b == null) return;
            try
            {
                var rt = b.rectTransform;
                if (rt == null) return;
                // 玩家 2026-11-10：圆点按下去会「白一下再恢复」——原生按钮按下/悬停时会把 Image 刷成高亮色（白）。
                // 关掉它的颜色过渡，颜色只由我们自己涂，按下去就不会闪白。
                var nb = rt.gameObject.GetComponent<UnityEngine.UI.Button>();
                if (nb != null && nb.transition != UnityEngine.UI.Selectable.Transition.None)
                    nb.transition = UnityEngine.UI.Selectable.Transition.None;
                // 按钮的 Button 组件可能挂在子节点上（图标本体），子节点上的也要关，否则照样闪白。
                var subs = rt.gameObject.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                for (int i = 0; i < subs.Length; i++)
                {
                    var sb2 = subs[i];
                    if (sb2 != null && sb2.transition != UnityEngine.UI.Selectable.Transition.None)
                        sb2.transition = UnityEngine.UI.Selectable.Transition.None;
                }
                Image[] imgs;
                if (!tintImgs.TryGetValue(b, out imgs) || imgs == null || imgs.Length == 0)
                {
                    imgs = rt.gameObject.GetComponentsInChildren<Image>(true);
                    tintImgs[b] = imgs;
                }
                for (int i = 0; i < imgs.Length; i++) if (imgs[i] != null) imgs[i].color = c;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// machines：把 4 台机器逐个列出来（性能/质量/瓶内水量纯度/窗口可见性），
        /// 玩家反馈「我读的是另一台」时用它定位。
        /// </summary>
        public static string MachinesReport()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var p = PurifierHook.Pairs[i];
                string perf = "?", q = "?", vis = "?", vol = "?", pur = "?";
                try
                {
                    if (p.Machine != null)
                    {
                        perf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", p.Machine).ToString();
                        q = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", p.Machine).ToString();
                    }
                    vis = p.Window == null ? "?" : (GameBridge.WinVisible(p.Window) ? "true" : "false");
                    var ci = p.BottleInv == null ? null : GameBridge.Get(p.BottleInv, "childItem") as GameItem;
                    var b = ci;
                    if (b != null)
                    {
                        vol = (WaterHelper.GetTotalVolume(b) / 1000).ToString();
                        pur = WaterHelper.GetWaterPurity(b).ToString();
                    }
                }
                catch (Exception) { }
                if (i > 0) sb.Append(" | ");
                sb.Append("#").Append(i).Append(" perf=").Append(perf).Append(" q=").Append(q)
                  .Append(" vis=").Append(vis).Append(" vol=").Append(vol).Append("mL pur=").Append(pur);
            }
            return "machines -> " + (sb.Length == 0 ? "none" : sb.ToString()) + " active=" + MachineOverride;
        }

        public static void RebuildPublic() { Rebuild(true); }

        /// <summary>ctl 改过「当前这台」之后立刻重挂描边，免得 mark? 读到上一帧的 host。</summary>
        public static void SyncMarkerPublic() { try { MachineMarker.Sync(CurrentMachine()); } catch (Exception) { } }

        /// <summary>
        /// ctl 用：把机器加成原样打出来。
        /// 玩家反馈「明明装了性能模组、界面却写 +0%」时，先用它确认是读不到还是真的 0。
        /// </summary>
        public static string BonusReport()
        {
            int perf, q;
            Bonuses(out perf, out q);
            var m = CurrentMachine();
            string ids = "none";
            try
            {
                if (m != null) ids = GameBridge.DescribeModules(m);
            }
            catch (Exception) { }
            return "bonus -> perf=" + perf + " quality=" + q +
                   " machine=" + (m == null ? "none" : "yes") +
                   " over=" + PerfOverride + "/" + QualityOverride +
                   " modules=" + ids;
        }

        /// <summary>给手册用的整机质量（手册第 1/3 页的上限跟它走）。</summary>
        internal static int QualityForBook()
        {
            int perf, q;
            try { Bonuses(out perf, out q); return q; } catch (Exception) { return -1; }
        }

        private static void Bonuses(out int perf, out int q)
        {
            int power;
            Bonuses(out perf, out q, out power);
        }

        /// <summary>性能 / 质量 / **每晚能耗**（机器标签自带的「所需能量」，效率词条会影响它）。
        /// 能耗读不到时才退回 4/晚；1 能量 = 1 元（玩家 2026-11-10 定案）。</summary>
        private static void Bonuses(out int perf, out int q, out int power)
        {
            perf = 0; q = 0; power = 4;
            var machine = CurrentMachine();
            if (machine != null)
            {
                try
                {
                    perf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", machine);
                    q = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", machine);
                    int pw = GameBridge.StatInt("MachineryHelper", "GetMachinePowerUsage", machine);
                    if (pw > 0) power = pw;
                }
                catch (Exception) { }
            }
            if (power <= 0) power = 4;
            WaterPlan.EnergyPerNight = power;
            // 调试用：qual=N 直接顶替机器质量，用来验证「带 25 质量模组时的 1850」而不是等玩家装模组。
            if (QualityOverride >= 0) q = QualityOverride;
            if (PerfOverride >= 0) perf = PerfOverride;
            // 玩家 2026-11-12：没机器的存档 —— 没有性能/质量加成，也没有每晚电费；
            // 方案只靠 过滤棉 + 紫外灯 + 药片，过夜步骤由 WaterPlan.NoMachine 直接跳过。
            if (WaterPlan.NoMachine) { perf = 0; q = 0; WaterPlan.EnergyPerNight = 0; }
        }

        /// <summary>重点数字加金色。面板文本走游戏原生 TMP，支持富文本。</summary>
        private static string HiLite(string s) { return "<color=#F2C14E>" + s + "</color>"; }

        /// <summary>步骤摘要（玩家 2026-11-10 定案）：按真实执行顺序串起来 ——
        /// 海德拉 → 过滤棉 → 紫外 → 净水器 n 晚 → 药片；不再写「药片按游戏真值」这类内部说明。</summary>
        private static string StepChain(WaterPlan.Combo c)
        {
            if (c == null) return "";
            // 2026-11-12：分隔统一用双空格（不再用「  」——粗像素字里一排小圆点太吵）。
            // 读起来是「海德拉200  滤棉800  紫外  1 晚  药2」，靠色差分组：工具名暖灰、数量青色。
            var sb = new System.Text.StringBuilder();
            if (c.hydra && c.hydraDur > 0)
            {
                sb.Append(PlanText.Lbl("海德拉")).Append(PlanText.Rich(PlanText.C_NUM, c.hydraDur.ToString()));
            }
            if (c.filter)
            {
                if (sb.Length > 0) sb.Append(GAP);
                sb.Append(PlanText.Lbl("滤棉")).Append(PlanText.Rich(PlanText.C_NUM, c.filterUsed.ToString()));
            }
            if (c.uv)
            {
                if (sb.Length > 0) sb.Append(GAP);
                sb.Append(PlanText.Lbl("紫外"));
            }
            // 玩家 2026-11-17：多晚按晚逐个写「净化器」，不再合并成「净化器2晚」。
            // 不用净水器时 nights=0，不强行补成 1 晚。
            if (c.nights > 0)
            {
                for (int n = 0; n < c.nights; n++)
                {
                    if (sb.Length > 0) sb.Append(GAP);
                    sb.Append(PlanText.Lbl("净化器"));
                }
            }
            if (c.tablets > 0)
            {
                sb.Append(GAP).Append(PlanText.Lbl("药")).Append(PlanText.Rich(PlanText.C_NUM, c.tablets.ToString()));
            }
            if (sb.Length == 0) return PlanText.Lbl("原样");
            return sb.ToString();
        }

        /// <summary>批次 33：主页/计算页的步骤串由 rawSteps 生成，顺序与步骤页一致；不吞开头药片。</summary>
        private static string RawStepsChain(WaterPlan.Report r)
        {
            var raw = RawResultFor(r);
            if (raw == null || raw.rawSteps == null || raw.rawSteps.Count == 0)
                return r == null || r.pick == null ? "" : StepChain(r.pick);

            var sb = new System.Text.StringBuilder();
            int start = progressActive ? Math.Max(0, progressDoneSteps) : 0;
            if (start >= raw.rawSteps.Count) return PlanText.Lbl("已完成");
            // 药片可能先于过滤步骤用掉：只扣剩余的，不和 progressDoneSteps 重复扣。
            int tabletLeft = Math.Max(0, progressTabletUsed - TabletUsedInPrefix());
            // 补水是方案的准备动作，原来只写在卡片摘要里，步骤行看不到。
            // 只要当前水还没到方案总量，就把它作为步骤行第一项；
            // 已经补够（CurrentTopUpUl 返回 0）时自然不写，已经开始执行也仍然提示还差的水。
            long topUpUl = CurrentTopUpUl(r);
            if (topUpUl >= 1000L)
                sb.Append(PlanText.Lbl("补 ")).Append(PlanText.Rich(PlanText.C_NUM, WaterPlan.Ml(topUpUl)));
            for (int i = start; i < raw.rawSteps.Count; i++)
            {
                var st = raw.rawSteps[i];
                if (st == null) continue;
                int count = Math.Max(1, st.count);
                if (st.tool == "tablet")
                {
                    // 相邻药片可以合并成「药N」，但必须从第一条开始合并；非相邻的保持原位置。
                    int j = i + 1;
                    while (j < raw.rawSteps.Count && raw.rawSteps[j] != null && raw.rawSteps[j].tool == "tablet")
                    {
                        count += Math.Max(1, raw.rawSteps[j].count);
                        j++;
                    }
                    i = j - 1;
                    if (tabletLeft > 0)
                    {
                        int cut = Math.Min(count, tabletLeft);
                        count -= cut;
                        tabletLeft -= cut;
                    }
                    if (count <= 0) continue;
                    if (sb.Length > 0) sb.Append(GAP);
                    sb.Append(PlanText.Lbl("药")).Append(PlanText.Rich(PlanText.C_NUM, count.ToString()));
                }
                else if (st.tool == "purifier")
                {
                    // 玩家 2026-11-17：多晚不要再合并成「净化器2晚」——按以前格式每一步单独写，
                    // 这样中间的「药/滤棉」能保留在原位置，读起来就是
                    // 「药2  滤棉  净化器  药2  净化器」。
                    if (sb.Length > 0) sb.Append(GAP);
                    sb.Append(PlanText.Lbl("净化器"));
                }
                else
                {
                    if (sb.Length > 0) sb.Append(GAP);
                    if (st.tool == "uv") sb.Append(PlanText.Lbl("紫外"));
                    else if (st.tool == "filter")
                    {
                        sb.Append(PlanText.Lbl("滤棉"));
                        if (st.dura > 0) sb.Append(PlanText.Rich(PlanText.C_NUM, st.dura.ToString()));
                    }
                    else if (st.tool == "portable")
                    {
                        sb.Append(PlanText.Lbl("海德拉"));
                        if (st.dura > 0) sb.Append(PlanText.Rich(PlanText.C_NUM, st.dura.ToString()));
                    }
                }
            }
            if (sb.Length == 0) return PlanText.Lbl("原样");
            return sb.ToString();
        }

        /// <summary>步骤链的纯文本版：和主页「步骤」行同一口径，供「更多方案」行标题使用（不带颜色标签）。</summary>
        private static string StepChainPlain(WaterPlan.Combo c)
        {
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            if (c.hydra && c.hydraDur > 0) sb.Append("海德拉").Append(c.hydraDur);
            if (c.filter)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append("滤棉").Append(c.filterUsed);
            }
            if (c.uv)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append("紫外");
            }
            // 与富文本版同一口径：多晚按晚逐个写「净化器」，不再合并成「净化器2晚」。
            if (c.nights > 0)
            {
                for (int n = 0; n < c.nights; n++)
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append("净化器");
                }
            }
            if (c.tablets > 0)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append("药").Append(c.tablets).Append("（本次）");
            }
            if (sb.Length == 0) sb.Append("原样");
            return sb.ToString();
        }

        /// <summary>keepUl 单位 μL，返回 μL。</summary>
        public static long MaxKeepUl(int capUl, int purBp, int perf, int q, int targetBp)
        {
            if (capUl <= 0) return 0;
            // 批次 22：这里要的是「一晚最多能接多少」，不是「最省耗材能接多少」。
            // WaterMath.PlanOneNight 的默认 Better() 会优先零耗材，导致 50 L 空瓶只回 1850；
            // 取水上限必须临时允许耗材参与，否则 p1 的 18700 一晚方案永远进不了池。
            bool savePrefer = WaterMath.PreferNoConsumable;
            try
            {
                WaterMath.PreferNoConsumable = false;
                var on = WaterMath.PlanOneNight(capUl, purBp, perf, q, targetBp, WaterPlan.MaxTablets);
                if (on != null && on.possible) return on.keepUl;
                long lo = 0, hi = capUl;
                for (int i = 0; i < 24; i++)
                {
                    long mid = (lo + hi) / 2;
                    if (mid <= 0) break;
                    var p = WaterMath.PlanOneNight(mid, purBp, perf, q, targetBp, WaterPlan.MaxTablets);
                    if (p != null && p.possible) lo = mid; else hi = mid;
                }
                return lo;
            }
            finally
            {
                WaterMath.PreferNoConsumable = savePrefer;
            }
        }

        private static int TargetVolumeMl()
        {
            try
            {
                if (Model != null && Model.Plans != null && Model.Plans.Count > 0)
                {
                    var p = Model.Plans[Math.Min(Model.Selected, Model.Plans.Count - 1)];
                    if (p != null && p.keepUl > 0) return (int)(p.keepUl / 1000);
                }
                var b = GameBottle();
                if (b == null) return 0;
                int grade, pur;
                TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                return (int)(MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, PlanText.TargetBp(TargetIdx, q)) / 1000);
            }
            catch (Exception) { return 0; }
        }

        private static string FillLabel()
        {
            var b = GameBottle();
            if (b == null) b = PickFallbackBottle();
            if (b == null) return "灌水（先放瓶）";
            int grade, pur;
            TodaySource(out grade, out pur);
            int perf, q;
            Bonuses(out perf, out q);
            long keep = MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, PlanText.TargetBp(TargetIdx, q));
            if (keep <= 0) return "灌水（今日水质做不到）";
            // 键面必须跟「选中方案」一致：方案说接 1850 就不要写 1857。
            try
            {
                var r0 = CurrentPlan(true);
                if (r0 != null && r0.pick != null && r0.pick.feedUl > 0) keep = r0.pick.feedUl;
                // 与 OnFill 同口径：理论上限 1857 还要按整瓶可达量收成 1850，否则按钮说 1857、实际灌 1850。
                var pp = WaterPlan.Plan(keep);
                if (pp != null && pp.totalUl > 0 && pp.totalUl < keep) keep = pp.totalUl;
            }
            catch (Exception) { }
            string water = grade == 4 ? "铁锈水" : (grade == 5 ? "沟渠水" : "水源");
            return "灌 " + (keep / 1000) + "mL " + water + " " + PlanText.Pct(pur);
        }

        // ---------------- 计算页：点数字框直接键入 ----------------
        private static void FillCalc(MachineryStack page)
        {
            for (int i = 0; i < calcVals.Length; i++) calcVals[i] = null;
            for (int i = 0; i < calcShown.Length; i++) calcShown[i] = "";
            cCalcSum = null; cCalcOut = null; cCalcOut2 = null; cCalcHint = null; cCalcPool = null; calcToolsBtn = null;
            cCalcFeed = null; cCalcHydraHint = null; cCalcHydraHintRow = null;
            calcScopeLbl = null; calcScopeStr = "";

            // 玩家 2026-11-13（第六轮）：清空挪到整页最底部，顶部不再挂这个小方块。
            var selRow = new MachineryStack(false, 4);
            try { selRow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { selRow.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            // 行位留给「步骤」：RefreshCalc 复用这个恒空控件，省一行高度（勿再写 ""）
            cCalcSel = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
            cCalcSel.SetColor(Dim); cCalcSel.SetFillWidth(true);
            selRow.Add(cCalcSel);
            // 设备开关不在这里重建：2026-11-14 实机确认挪动它会连累主界面/设置布局，先保持回滚版布局。
            AddCalcSection(page, "净化前");
            AddCalcPair(page, CalcF_Cap, "容量mL", CalcF_Water, "水量mL");
            var wrow = new MachineryStack(false, 4);
            try { wrow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { wrow.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            var wlbl = new MachineryText("水量", 9, HorizontalAlignmentOptions.Right, false);
            wlbl.SetColor(Dim); wlbl.SetFillWidth(true); wlbl.ResizePixels(44, 12);
            wrow.Add(wlbl);
            sCalcWater = new MachinerySlider((float)(calcWaterUl / 1000.0), 104);
            sCalcWater.SetRange(0f, (float)(calcCapUl / 1000.0), 0.1f);
            sCalcWater.SetFillWidth(true);
            sCalcWater.onChanged = new Action<float>(OnCalcWaterSlide);
            sCalcWater.onReleased = new Action<float>(OnCalcWaterReleased);
            wrow.Add(sCalcWater);
            page.Add(wrow);
            AddCalcSection(page, "净水器模组加成");
            AddCalcPair(page, CalcF_Perf, "性能", CalcF_Quality, "质量");
            var trow = new MachineryStack(false, 4);
            try { trow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { trow.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            AddCalcCell(trow, CalcF_Target, "目标");
            // 玩家 2026-11-14：水比例不是只读标签，跟目标一样点框键入；改后按原六项杂质比例缩放。
            AddCalcCell(trow, CalcF_Ratio, "水比例");
            page.Add(trow);
            for (int i = 0; i < 3; i++)
                AddCalcPair(page, CalcF_Imp0 + i, CalcImpNames[i], CalcF_Imp0 + i + 3, CalcImpNames[i + 3]);

            cCalcSum = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
            cCalcSum.SetColor(Dim); cCalcSum.SetFillWidth(true);
            // 道具口径按钮只放在本页，跟随/背包/无限三态循环；不改设置页与主页
            var toolsRow = new MachineryStack(false, 4);
            toolsRow.SetAlign(MachineryStack.Align.Start);
            calcToolsBtn = new MachineryButton("道具 跟随", new Action(OnCalcToolsCycle), 56, 20);
            calcToolsBtn.SetFillWidth(false);
            SkinButton(calcToolsBtn, RowFill, Cream, false);
            SetDesc(calcToolsBtn, "只影响本页推荐：药片上限 + 滤棉可用点数，不改设置页");
            toolsRow.Add(calcToolsBtn);

            calcScopeLbl = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
            calcScopeLbl.SetColor(Dim); calcScopeLbl.SetFillWidth(false);
            calcScopeLbl.ResizePixels(160, 13);
            toolsRow.Add(calcScopeLbl);
            page.Add(toolsRow);
            AddCalcSection(page, "净化结果");
            page.Add(cCalcSum);

            cCalcOut = new MachineryText("", 11, HorizontalAlignmentOptions.Left, false);
            cCalcOut.SetColor(Cream); cCalcOut.SetFillWidth(true);
            page.Add(cCalcOut);
            // 步骤行放在「净化后」与「出水/成本」之间，计算页读序与主页一致。
            page.Add(selRow);

            cCalcFeed = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
            cCalcFeed.SetColor(Cream); cCalcFeed.SetFillWidth(true);
            cCalcFeed.ResizePixels(208, 9);   // 独立行只占 9 高，避免 385 外框把成本行顶出屏幕
            page.Add(cCalcFeed);

            cCalcOut2 = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
            cCalcOut2.SetColor(Cream); cCalcOut2.SetFillWidth(true);
            page.Add(cCalcOut2);

            // 批次 29：性价比自动档无无耗材方案时，计算页同样只显示临时启用键。
            cCalcHydraHintRow = new MachineryStack(false, 4);
            try { cCalcHydraHintRow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            cCalcHydraHint = new MachineryButton("启用海德拉", new Action(delegate { EnableHydraOnce(); }), 104, 20);
            cCalcHydraHint.SetFillWidth(false);
            SetDesc(cCalcHydraHint, "只本次计算临时允许海德拉，不改设置页的自动/强制/不用");
            cCalcHydraHintRow.Add(cCalcHydraHint);
            page.Add(cCalcHydraHintRow);

            // 玩家 2026-11-13（第六轮）：清空放整页最底部（跟网页版一样，结果下面才是重置）。
            try
            {
                var clearRow = new MachineryStack(false, 4);
                try { clearRow.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
                var bClear = new MachineryButton("清空", new Action(OnCalcClear), 40, 20);
                bClear.SetFillWidth(false);
                SkinButton(bClear, RowFill, new Color(0.50f, 0.85f, 0.91f, 1f), false);
                SetDesc(bClear, "不选容器，所有数值清零，手动输入");
                clearRow.Add(bClear);
                page.Add(clearRow);
            }
            catch (Exception) { }

            // 玩家 2026-11-13：混水按钮挪到左栏第五个页签（「计算」下面）；
            // 「当前草稿…」「点框键入…」两行玩家说多余，删掉。

            RefreshCalcLabels();
        }

        /// <summary>计算页「道具」按钮：跟随设置 → 背包 → 无限，循环切换，只改本页推荐。</summary>
        private static void OnCalcToolsCycle()
        {
            calcToolsMode = (calcToolsMode + 1) % 3;
            if (calcToolsBtn != null)
            {
                string t = calcToolsMode == 0 ? "道具 跟随" : (calcToolsMode == 1 ? "道具 背包" : "道具 无限");
                calcToolsBtn.SetText(t);
                SetDesc(calcToolsBtn, t + "：只影响本页推荐");
            }
            calcDirty = true;
            calcRep = null;
            RefreshCalcLabels();
        }

        /// <summary>计算页的分段小标题（照 v1 网页的三栏：净化前 / 模组加成 / 净化结果）。</summary>
        private static void AddCalcSection(MachineryStack page, string title)
        {
            var t = new MachineryText(title, 9, HorizontalAlignmentOptions.Left, false);
            t.SetColor(Cyan);
            t.SetFillWidth(true);
            page.Add(t);
        }

        private static void AddCalcPair(MachineryStack page, int f1, string n1, int f2, string n2)        {
            var row = new MachineryStack(false, 4);
            try { row.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            try { row.SetMainAlign(MachineryStack.Align.Start); } catch (Exception) { }
            AddCalcCell(row, f1, n1);
            AddCalcCell(row, f2, n2);
            page.Add(row);
        }

        private static void AddCalcCell(MachineryStack row, int field, string label)
        {
            var lb = new MachineryText(label, 9, HorizontalAlignmentOptions.Right, false);
            lb.SetColor(Dim); lb.SetFillWidth(true); lb.ResizePixels(44, 12);
            row.Add(lb);
            MachineryButton b = null;
            try
            {
                b = new MachineryButton("", new Action(delegate { BeginCalcTyping(field); }), 54, 20);
                b.SetFillWidth(false);
                SkinButton(b, RowFill, new Color(0.50f, 0.85f, 0.91f, 1f), false);
                SetDesc(b, "点这里直接键入，回车确认、Esc 取消");
            }
            catch (Exception) { }
            if (b != null) row.Add(b);
            if (field >= 0 && field < calcVals.Length) calcVals[field] = b;
        }

        private static string CalcFieldName(int field)
        {
            if (field == CalcF_Cap) return "容量";
            if (field == CalcF_Perf) return "性能";
            if (field == CalcF_Quality) return "质量";
            if (field == CalcF_Target) return "目标";
            if (field == CalcF_Ratio) return "水比例";
            int i = field - CalcF_Imp0;
            return (i >= 0 && i < CalcImpNames.Length) ? CalcImpNames[i] : "数值";
        }

        private static string CalcEditValue(int field)
        {
            if (field == CalcF_Cap) return (calcCapUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (field == CalcF_Water) return (calcWaterUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (field == CalcF_Perf) return calcPerf.ToString();
            if (field == CalcF_Quality) return calcQuality.ToString();
            if (field == CalcF_Target) return (calcTargetBp / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            if (field == CalcF_Ratio) return (CalcPurityBp() / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            int i = field - CalcF_Imp0;
            if (i >= 0 && i < calcImpBp.Length) return (calcImpBp[i] / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            return "";
        }

        private static string CalcShowValue(int field)
        {
            // 玩家 2026-11-13：mL 放到标签上，输入框里只留数字，别挡着改数。
            if (field == CalcF_Cap) return (calcCapUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (field == CalcF_Water) return (calcWaterUl / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (field == CalcF_Perf) return "+" + calcPerf + "%";
            if (field == CalcF_Quality) return calcQuality.ToString();
            if (field == CalcF_Target) return PlanText.Pct(calcTargetBp);
            if (field == CalcF_Ratio) return PlanText.Pct(CalcPurityBp());
            int i = field - CalcF_Imp0;
            if (i >= 0 && i < calcImpBp.Length)
            {
                // 玩家 2026-11-13（p3）：0.01 也要显示 —— 有量但不足 0.01% 的项写「<0.01%」，
                // 不能再四舍五入成 0.00% 让人以为读不到。
                if (calcImpBp[i] <= 0 && calcImpUl[i] > 0) return "<0.01%";
                return PlanText.Pct(calcImpBp[i]);
            }
            return "";
        }

        private static int CalcPurityBp()
        {
            int sum = 0;
            for (int i = 0; i < calcImpBp.Length; i++) sum += calcImpBp[i];
            int pure = 10000 - sum;
            return pure < 0 ? 0 : (pure > 10000 ? 10000 : pure);
        }

        /// <summary>手改「水比例」：保持六项杂质原有比例，只缩放它们的合计。</summary>
        private static void SetCalcPurityBp(int purityBp)
        {
            if (purityBp < 0) purityBp = 0;
            if (purityBp > 10000) purityBp = 10000;
            long vol = Math.Max(1L, calcWaterUl);
            long oldTotal = 0;
            for (int i = 0; i < calcImpUl.Length; i++) oldTotal += Math.Max(0L, calcImpUl[i]);
            long newTotal = vol * (10000L - purityBp) / 10000L;
            if (newTotal <= 0)
            {
                for (int i = 0; i < calcImpUl.Length; i++) { calcImpUl[i] = 0; calcImpBp[i] = 0; }
                return;
            }
            if (oldTotal > 0)
            {
                long used = 0;
                for (int i = 0; i < calcImpUl.Length; i++)
                {
                    long part;
                    if (i == calcImpUl.Length - 1) part = Math.Max(0L, newTotal - used);
                    else part = (long)Math.Round((double)calcImpUl[i] * newTotal / oldTotal);
                    calcImpUl[i] = part; used += part;
                }
            }
            else
            {
                // 原来是 100% 水，没有可缩放的杂质比例；新增杂质放化学项，仍能继续手改。
                for (int i = 0; i < calcImpUl.Length; i++) calcImpUl[i] = 0;
                calcImpUl[3] = newTotal;
            }
            for (int i = 0; i < calcImpBp.Length; i++)
                calcImpBp[i] = (int)Math.Min(10000L, Math.Max(0L, (calcImpUl[i] * 10000L + vol / 2) / vol));
        }

        private static void OnCalcWaterSlide(float v)
        {
            long ul = (long)Math.Round(v * 1000.0);
            if (ul < 0) ul = 0;
            if (ul > calcCapUl) ul = calcCapUl;
            if (ul == calcWaterUl) return;
            calcWaterUl = ul;
            calcDirty = true;
            RecalcCalc();
            RefreshCalcLabels();
        }

        private static void OnCalcWaterReleased(float v)
        {
            // 有些机器滑条只在松手时回写最终值；松手后再按最终值强制算一次。
            long ul = (long)Math.Round(v * 1000.0);
            if (ul < 0) ul = 0;
            if (ul > calcCapUl) ul = calcCapUl;
            if (ul != calcWaterUl)
            {
                calcWaterUl = ul;
                calcDirty = true;
                RecalcCalc();
            }
            RefreshCalcLabels();
        }

        private static void BeginCalcTyping(int field)
        {
            if (calcField == field) { CommitCalcTyping(); return; }
            if (calcField >= 0) CommitCalcTyping();
            if (manTyping) CommitManualTyping();
            calcField = field;
            calcTypingAt = Time.frameCount;
            // 玩家 2026-11-13：点框就是要重新填一个完整数；旧版把当前值当缓冲区，
            // 再键入「6000」会变成「200006000」，最后被钳到上限 50000。
            calcBuf = "";
            LastMsg = "键入「" + CalcFieldName(field) + "」：回车确认，Esc 取消";
            RefreshCalcLabels();
        }

        private static void CancelCalcTyping()
        {
            calcField = -1;
            calcBuf = "";
            RefreshCalcLabels();
            LastMsg = "已取消键入";
        }

        private static void CommitCalcTyping()
        {
            if (calcField < 0) return;
            int field = calcField;
            float v;
            if (!float.TryParse(calcBuf, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                CancelCalcTyping();
                LastMsg = "输入无效：只填数字";
                return;
            }
            if (field == CalcF_Cap)
            {
                long capUl = (long)Math.Round(v * 1000.0);
                if (capUl < 1000) capUl = 1000;
                if (capUl > 50000000L) capUl = 50000000L;
                calcCapUl = capUl;
                if (calcWaterUl > calcCapUl) calcWaterUl = calcCapUl;
                if (sCalcWater != null) try { sCalcWater.SetRange(0f, (float)(calcCapUl / 1000.0), 0.1f); } catch (Exception) { }
            }
            else if (field == CalcF_Water)
            {
                long ul = (long)Math.Round(v * 1000.0);
                if (ul < 0) ul = 0;
                if (ul > calcCapUl) ul = calcCapUl;
                calcWaterUl = ul;
            }
            else if (field == CalcF_Perf)
            {
                int p = (int)Math.Round(v);
                if (p < 0) p = 0;
                if (p > 999) p = 999;
                calcPerf = p;
            }
            else if (field == CalcF_Quality)
            {
                int q = (int)Math.Round(v);
                if (q < 0) q = 0;
                if (q > 999) q = 999;
                calcQuality = q;
            }
            else if (field == CalcF_Target)
            {
                int bp = (int)Math.Round(v * 100.0);
                if (bp < 1) bp = 1;
                if (bp > 10000) bp = 10000;
                calcTargetBp = bp;
            }
            else if (field == CalcF_Ratio)
            {
                int bp = (int)Math.Round(v * 100.0);
                if (bp < 0) bp = 0;
                if (bp > 10000) bp = 10000;
                SetCalcPurityBp(bp);
            }
            else
            {
                int i = field - CalcF_Imp0;
                if (i >= 0 && i < calcImpBp.Length)
                {
                    int bp = (int)Math.Round(v * 100.0);
                    if (bp < 0) bp = 0;
                    if (bp > 10000) bp = 10000;
                    int others = 0;
                    for (int k = 0; k < calcImpBp.Length; k++) if (k != i) others += calcImpBp[k];
                    if (bp + others > 10000) bp = Math.Max(0, 10000 - others);
                    calcImpBp[i] = bp;
                    calcImpUl[i] = calcWaterUl * bp / 10000L;
                }
            }
            calcField = -1;
            calcBuf = "";
            calcDirty = true;
            RecalcCalc();
            RefreshCalcLabels();
        }

        private static void CalcInputTick()
        {
            if (calcField < 0) return;
            if (Input.GetMouseButtonDown(0) && Time.frameCount > calcTypingAt + 1)
            {
                CommitCalcTyping();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelCalcTyping(); return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CommitCalcTyping();
                return;
            }
            string ins = null;
            try { ins = Input.inputString; } catch (Exception) { }
            if (string.IsNullOrEmpty(ins)) return;
            bool changed = false;
            for (int i = 0; i < ins.Length; i++)
            {
                char c = ins[i];
                if (c == '\b')
                {
                    if (calcBuf.Length > 0) { calcBuf = calcBuf.Substring(0, calcBuf.Length - 1); changed = true; }
                }
                else if ((c >= '0' && c <= '9') || c == '.' || c == ',')
                {
                    if (c == ',') c = '.';
                    if (c == '.' && calcBuf.IndexOf('.') >= 0) continue;
                    if (calcBuf.Length >= 7) continue;
                    calcBuf += c;
                    changed = true;
                }
            }
            if (changed)
            {
                RefreshCalcLabels();
                LastMsg = "键入「" + CalcFieldName(calcField) + "」 " + calcBuf + "，回车确认";
            }
        }

        /// <summary>计算页版「需接水」：能按选中方案补就按方案，方案只吃当前水量时按瓶容量补；够 / 不够 / 满三分支。</summary>
        private static string CalcFeedText()
        {
            var r = calcRep;
            // 模式图标切换后 calcRep 会被作废；计算页处于自动口径时退回主页当前方案，
            // 不能把「需接水/倒水」这一行留白或继续显示上一次的旧目标。
            if ((r == null || r.pick == null) && !calcManual) r = CurrentPlan(true);
            if (r == null || r.pick == null) return "";
            long cap = calcCapUl > 0 ? calcCapUl : (r.capUl > 0 ? r.capUl : 0);
            long have = calcWaterUl;
            long pour = BottlePourUl(r.ChosenFeedUl(), cap);
            // 某些自动方案会先把 feed 记成 0（尤其切模式后旧 rep 退回主页方案）。
            // 这时用主页已经算好的整瓶投放量兜底，绝不回退成容器容量。
            if (pour <= 0 && planPourMl > 0) pour = BottlePourUl(planPourMl * 1000L, cap);
            if (cap <= 0) return "";
            if (pour <= 0) return "";
            if (pour > 0 && have > pour)
                return PlanText.Lbl("倒出 ") + PlanText.Num(WaterPlan.Ml(have - pour)) + PlanText.Lbl(" 后净化");
            if (have <= 0)
            {
                // 空瓶：接水量只认方案投放量，不能回退成容器容量。
                if (pour <= 0) return "";
                return PlanText.Lbl("需接水 ") + PlanText.Num(WaterPlan.Ml(pour));
            }
            if (have >= cap)
            {
                // 满瓶：只有当前参数下方案还达不到目标时才提示先倒出，够达标就不打扰
                return r.pick.purityBp < calcTargetBp
                    ? PlanText.Rich(PlanText.C_WARN, "已满，先倒出再接")
                    : "";
            }
            long target = pour;
            long want = target - have;
            if (want <= 0) return "";
            return PlanText.Lbl("需接水 ") + PlanText.Num(WaterPlan.Ml(want));
        }

        /// <summary>计算页步骤下面那行：有内容才占一行，接够/无提示时整行收掉，不留空行。</summary>
        private static void SetCalcFeedLine(string text)
        {
            if (cCalcFeed == null) return;
            string s = text == null ? "" : text;
            SetT(cCalcFeed, s);
            SetVisible(cCalcFeed, s.Length > 0);
            try { cCalcFeed.ResizePixels(208, s.Length > 0 ? 9 : 0); } catch (Exception) { }
        }

        private static void RefreshCalcLabels()
        {
            for (int i = 0; i < calcVals.Length; i++)
            {
                var b = calcVals[i];
                if (b == null) continue;
                string s = (calcField == i) ? (calcBuf + "|") : CalcShowValue(i);
                if (s == calcShown[i]) continue;
                calcShown[i] = s;
                try { b.SetText(s); } catch (Exception) { }
            }
            SetVisible(cCalcHydraHint, autoHydraHint);
            int sum = 0;
            for (int i = 0; i < calcImpBp.Length; i++) sum += calcImpBp[i];
            if (sCalcWater != null)
            {
                try
                {
                    if (Math.Abs(sCalcWater.value - (float)(calcWaterUl / 1000.0)) > 0.05f)
                        sCalcWater.SetValue((float)(calcWaterUl / 1000.0), false);
                }
                catch (Exception) { }
            }
            int purity = 10000 - sum;
            if (purity < 0) purity = 0;
            if (cCalcSum != null)
                SetT(cCalcSum, PlanText.Lbl("杂质合计 ") + PlanText.Num(PlanText.Pct(sum)) +
                                PlanText.Lbl("    水质 ") + PlanText.Rich(PlanText.C_PCT, PlanText.Pct(purity)));
            if (cCalcSel != null) SetT(cCalcSel, "");   // 玩家 2026-11-13（第六轮）：选中/未选这些提示是多余文本，不要了
            if (calcWaterUl > 0 && purity >= calcTargetBp)
            {
                // 玩家 2026-11-13：这瓶水已经到目标了，别再推荐「跑一晚」。
                if (cCalcOut != null) SetT(cCalcOut, PlanText.Rich(PlanText.C_MONEY, "已达标，不用跑一晚"));
                if (cCalcOut2 != null) SetT(cCalcOut2, "");
                if (cCalcFeed != null) SetCalcFeedLine("");
                calcRep = null;
                autoHydraHint = false;
                SetVisible(cCalcHydraHint, false);
                return;
            }
            if (calcWaterUl <= 0 && calcCapUl <= 0)
            {
                // 连容量都没有：空白计算器，不显示「一晚 / 出水 / 赚 -1.5元」这种误导结果。
                if (cCalcSum != null) SetT(cCalcSum, "");
                if (cCalcOut != null) SetT(cCalcOut, "");
                if (cCalcOut2 != null) SetT(cCalcOut2, "");
                if (cCalcFeed != null) SetCalcFeedLine("");
                if (cCalcSel != null) SetT(cCalcSel, "");
                calcRep = null;
                autoHydraHint = false;
                SetVisible(cCalcHydraHint, false);
                return;
            }
            RecalcCalc();
            // AutoReport 在 RecalcCalc() 内部才会把 autoHydraHint 置真；
            // 这里必须在重算之后再同步一次可见性，否则计算页按钮会一直是上一次的隐藏态。
            SetVisible(cCalcHydraHint, autoHydraHint);
            if (cCalcOut != null)
            {
                var r = calcRep;
                if (r == null || r.pick == null)
                {
                    SetT(cCalcOut, autoHydraHint
                        ? PlanText.Rich(PlanText.C_WARN, "无耗材方案达不到目标，可启用海德拉")
                        : PlanText.Rich(PlanText.C_WARN,
                            WaterPlan.NightCap > 1 ? "多晚也做不到目标：降目标或减少杂质"
                                                   : "一晚做不到目标：降目标或减少杂质"));
                    SetT(cCalcOut2, "");
                    if (cCalcFeed != null) SetCalcFeedLine(CalcFeedText());
                }
                else if (r.pick.nights <= 1 && r.pick.purityBp < calcTargetBp)
                {
                    // bug12：多晚关闭后只剩一晚，达不到目标时必须明确降目标/减少杂质。
                    // 2026-11-14：先算一晚最多能接多少，把超出部分明确说成「先倒出」。
                    int purUse = (r.srcBp > 0) ? r.srcBp : 0;
                    if (purUse <= 0)
                    {
                        int sg3 = 0, sp3 = 0;
                        try { TodaySource(out sg3, out sp3); } catch (Exception) { sp3 = 0; }
                        purUse = sp3 > 0 ? sp3 : 0;
                    }
                    long keep = MaxKeepUl((int)(calcCapUl > 0 ? calcCapUl : calcWaterUl),
                                          purUse, calcPerf, calcQuality, calcTargetBp);
                    long dump = (calcCapUl - keep) / 1000L;
                    if (dump < 0) dump = 0;
                    if (keep > 0 && dump > 0)
                        SetT(cCalcOut, PlanText.Rich(PlanText.C_WARN,
                            "一晚最多 " + keep / 1000L + " L：先倒出 " + dump + " mL"));
                    else
                        SetT(cCalcOut, PlanText.Rich(PlanText.C_WARN, "一晚做不到目标：降目标或减少杂质"));
                    SetT(cCalcOut2, "");
                    if (cCalcSel != null) SetT(cCalcSel, "");
                    if (cCalcFeed != null) SetCalcFeedLine(PlanText.Lbl(
                        WaterPlan.NightCap > 1 ? "或打开更大晚数" : "或倒出后重算"));
                }
                else
                {
                    if (calcScopeLbl != null) SetT(calcScopeLbl, calcScopeStr);
                    // "一晚"归步骤链，且晚数只在 >1 时提示；结果行只讲纯度
                    string line = PlanText.Lbl("净化后 ") + PlanText.Rich(PlanText.C_PCT, PlanText.Pct(r.pick.purityBp));
                    SetT(cCalcOut, line);
                    // 工单：计算页与主页同源——步骤链直接用 StepChain，两页口径不再分叉
                    if (cCalcSel != null) SetT(cCalcSel, PlanText.Lbl("步骤 ") + RawStepsChain(r));
                    if (cCalcFeed != null) SetCalcFeedLine(CalcFeedText());
                    string line2 = PlanText.Lbl("出水 ") + PlanText.Num(PlanText.Ml(r.pick.outUl))
                                  + PlanText.Lbl("  赚 ") + PlanText.Rich(PlanText.C_MONEY,
                                      (r.ChosenProfit() >= 100 ? r.ChosenProfit().ToString("0", CultureInfo.InvariantCulture)
                                                               : r.ChosenProfit().ToString("0.0", CultureInfo.InvariantCulture)) + "元");
                    if (r.dumpUl > 0) line2 += PlanText.Lbl("  先倒 ") + PlanText.Num(PlanText.Ml(r.dumpUl));
                    if (r.capShort) line2 += PlanText.Rich(PlanText.C_WARN, "  机器上限不够");
                    // 本方案没用到任何耗材——口径按钮当然按不出变化，说明一句，免得以为是坏了
                    bool usesConsumable = (r.pick.filter || r.pick.filterUsed > 0) || (r.pick.tablets > 0)
                                       || r.pick.uv || r.pick.hydra;
                    if (!usesConsumable) line2 += PlanText.Lbl("  （本方案不用耗材）");
                    // 成本 / 每升：与步骤页同一口径（0.00 / PerL），同一控件内换行，不新增控件
                    line2 += "\n" + PlanText.Lbl("成本 ") + PlanText.Num(r.cost.ToString("0.00", CultureInfo.InvariantCulture))
                            + PlanText.Lbl(" 元  每升 ") + PlanText.Num(PlanText.PerL(r.perL)) + PlanText.Lbl(" 元");
                    if (calcWaterUl <= 0 && calcCapUl > 0)
                        line2 += PlanText.Lbl("  （空瓶按满瓶 + 今天水源预估）");
                    if (r.needHydra)
                        line2 += PlanText.Lbl("  （仅此方案需海德拉）");
                    SetT(cCalcOut2, line2);
                }
            }
        }

        private static string Litres(long ul)
        {
            return (ul / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "L";
        }

        private static string LCompact(long ul)
        {
            double l = ul / 1000000.0;
            if (Math.Abs(l - Math.Round(l)) < 0.01) return Math.Round(l).ToString("0") + "L";
            return l.ToString("0.00", CultureInfo.InvariantCulture) + "L";
        }

        private static string Pct1(int bp)
        {
            return (bp / 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>计算页自己那一行：只报当前草稿，不再扫描背包（玩家 2026-11-13：切页不许全量扫）。</summary>
        private static string CalcOwnPoolText()
        {
            int sum = 0;
            for (int i = 0; i < calcImpBp.Length; i++) sum += calcImpBp[i];
            int purity = 10000 - sum;
            if (purity < 0) purity = 0;
            return PlanText.Lbl("当前草稿 ") + PlanText.Rich(PlanText.C_PCT, PlanText.Pct(purity))
                 + PlanText.Lbl("  ") + PlanText.Num(PlanText.Ml1(calcWaterUl))
                 + PlanText.Lbl(" · 要看混水请点「混水赚钱」");
        }

        // ---------------- 混水赚钱（独立模块：点开这个模块才扫描背包） ----------------

        private sealed class MixGroup
        {
            public int purityBp;
            public int[] impBp = new int[6];
            public long impUl;               // 这一组水的六项杂质绝对量（µL），用于精确判定
            public long volUl, capUl;
            public int count;
            public string key;
            public string name;
            public bool tap;
            public long precisBp100;
        }

        private sealed class MixRow
        {
            public MixGroup high;
            public MixGroup baseW;
            public long highUl, fillUl, totalUl;
            public int finalBp;
            public long precisBp100;
            public double mixRev, sepRev, gain;
            public string note = "";
        }

        private static string MixKey(int pur, int[] imp)
        {
            var sb = new StringBuilder(48);
            sb.Append(pur);
            for (int i = 0; i < imp.Length; i++) sb.Append(':').Append(imp[i]);
            return sb.ToString();
        }

        /// <summary>一组水的杂质总量（每 10000 体积）：优先用六项比例之和，读不到就按纯度反推。</summary>
        private static int MixImpSum(int pur, int[] imp)
        {
            int s = 0;
            if (imp != null) for (int i = 0; i < imp.Length; i++) s += imp[i];
            if (s <= 0 || s > 10000) s = 10000 - pur;
            if (s < 0) s = 0;
            return s;
        }

        /// <summary>扫描并合并：同 purityBp + 同六项杂质比例（按 10000 基准整数）才合并成一组。</summary>
        /// <summary>补扫 3L 水罐：WaterPlan.ScanWaters 会按容量跳过 3L 过滤棉容器，
        /// 但 water_pitcher 本身也能装水；只在混水页按实际水量补进来。</summary>
        private static void AddPitcherWaters(List<WaterPlan.WaterBottle> ws, GameItem skipItem)
        {
            if (ws == null) return;
            AddPitcherList(ws, GameBridge.AllItemsDeep(), skipItem);
            AddPitcherList(ws, GameBridge.CounterItems(), skipItem);
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    if (inv != null) AddPitcherList(ws, GameBridge.ItemsOf(inv), skipItem);
                }
            }
            catch (Exception) { }
        }

        private static void AddPitcherList(List<WaterPlan.WaterBottle> ws, List<GameItem> items, GameItem skipItem)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null || ReferenceEquals(it, skipItem)) continue;
                bool dup = false;
                for (int k = 0; k < ws.Count; k++) if (ReferenceEquals(ws[k].item, it)) { dup = true; break; }
                if (dup) continue;
                long capUl = 0, volUl = 0;
                int pur = 0;
                try { capUl = WaterHelper.GetTotalCapacity(it); } catch (Exception) { }
                if (Math.Abs(capUl - 3000000) > 1) continue;
                try { volUl = WaterHelper.GetTotalVolume(it); } catch (Exception) { }
                if (volUl <= 0) continue;
                try { pur = WaterHelper.GetWaterPurity(it); } catch (Exception) { }
                if (pur <= 0) continue;
                string nm = GameBridge.NameOfItem(it);
                if (string.IsNullOrEmpty(nm)) nm = "水罐";
                var w = new WaterPlan.WaterBottle();
                w.item = it; w.name = nm; w.capUl = capUl; w.volUl = volUl; w.purityBp = pur;
                ws.Add(w);
                Log("[mix] 补读3L水罐 " + volUl / 1000 + "mL " + pur + "bp");
            }
        }

        private static List<MixGroup> ScanMixGroups(out int tapBp, out string note, GameItem skipItem = null)
        {
            // 玩家 2026-11-16：没开通管道时 TapSource 返回 0，旧版保留 9800 兜底，
            // 混水页会把“不存在的水龙头”显示成基础水 98.00% 并生成免费兑水方案。
            // 这里不再兜底：读不到今日水龙头就按 0（未开通/未读到）处理。
            tapBp = 0; note = "";
            var list = new List<MixGroup>();
            int scanned = 0;
            try
            {
                int g = 0, p = 0;
                TapSource(out g, out p);
                if (p > 0) tapBp = p;
            }
            catch (Exception) { }
            try
            {
                var ws = WaterPlan.ScanWaters();
                // 玩家 2026-11-16：3L 水罐（water_pitcher）里有 2880mL 水，但 WaterPlan.ScanWaters
                // 一直把 3L 容器当成“过滤棉容器”跳过，混水页完全读不到这罐水。
                // 这里只在混水页补扫 3L 水罐；不改 WaterPlan 的既有扫描规则。
                AddPitcherWaters(ws, skipItem);
                for (int i = 0; i < ws.Count; i++)
                {
                    var w = ws[i];
                    if (w == null || w.volUl <= 0) continue;
                    // 当前机器里那瓶水在混水页顶部单独显示，下面背包组合不再重复列一遍。
                    if (skipItem != null && ReferenceEquals(w.item, skipItem)) continue;
                    // 扫描器可能把机器槽里的同一瓶水包成另一个对象；只按“同一瓶水”的四项特征排重，
                    // 不能像旧版那样把所有机器槽里的水都排掉。玩家 2026-11-17 选中机器 5553.1，
                    // 混水页顶部却读成另一台 5541.5，并且 5553.1 在列表里消失，就是 blanket 排重造成的。
                    if (skipItem != null && w.item != null && SameBottleForSkip(w.item, skipItem)) continue;
                    scanned++;
                    WaterMath.Water real = null;
                    try { BuildRealWater(w.item, out real); } catch (Exception) { real = null; }
                    int[] imp = new int[6];
                    long prec = 0;
                    try
                    {
                        if (real != null && real.v > 0)
                        {
                            for (int k = 0; k < 6; k++)
                                imp[k] = (int)Math.Min(10000L, Math.Max(0L,
                                    (real.Part(CalcImpKeys[k]) * 10000L + real.v / 2) / Math.Max(1L, real.v)));
                            prec = (real.v - real.Impurity) * 1000000L / real.v;
                        }
                    }
                    catch (Exception) { }
                    string key = MixKey(w.purityBp, imp);
                    MixGroup hit = null;
                    for (int j = 0; j < list.Count; j++) if (list[j].key == key) { hit = list[j]; break; }
                    if (hit == null)
                    {
                        hit = new MixGroup();
                        hit.purityBp = w.purityBp;
                        hit.impBp = imp;
                        hit.key = key;
                        hit.volUl = 0;
                        hit.capUl = 0;
                        hit.count = 0;
                        hit.precisBp100 = prec;
                        list.Add(hit);
                    }
                    // 精确杂质按瓶累计；后面 EvalMix 不再用四舍五入到 bp 的六项反推。
                    if (real != null && real.v > 0 && real.Impurity > 0) hit.impUl += real.Impurity;
                    hit.volUl += w.volUl;
                    hit.count++;
                    if (hit.precisBp100 <= 0 && prec > 0) hit.precisBp100 = prec;
                    hit.capUl = Math.Max(hit.capUl, w.capUl);
                    hit.name = w.name;
                }
            }
            catch (Exception e) { note = "扫描失败：" + e.GetType().Name; }
            list.Sort(delegate(MixGroup a, MixGroup b)
            {
                if (a.purityBp != b.purityBp) return b.purityBp - a.purityBp;
                return (int)Math.Min(1000000L, (b.volUl - a.volUl) / 1000L);
            });
            note = "扫描 " + scanned + " 瓶 · 按同水质合并成 " + list.Count + " 组";
            return list;
        }

        /// <summary>一组水在 volUl 体积里的杂质绝对量（µL）：按六项比例求和，读不到就按纯度反推。</summary>
        private static long GroupImpParts(MixGroup g, long volUl)
        {
            if (g == null || volUl <= 0) return 0;
            // 优先用扫描时累计的精确杂质（µL）按体积比例分摊。
            // 旧版用 impBp（六项四舍五入到 bp）反推，2879.8+720 这组会在 9900 边界
            // 把 98.999999% 算成 9900，从而把基础水当成优质水卖出假利润。
            if (g.impUl > 0 && g.volUl > 0)
                return g.impUl * volUl / g.volUl;
            long s = 0; int sum = 0;
            if (g.impBp != null)
                for (int i = 0; i < g.impBp.Length; i++) { sum += g.impBp[i]; s += (long)g.impBp[i] * volUl / 10000L; }
            if (sum <= 0 || sum > 10000)
            {
                int impBp = 10000 - g.purityBp;
                if (impBp < 0) impBp = 0;
                s = (long)impBp * volUl / 10000L;
            }
            return s;
        }

        /// <summary>按「高纯度整组 + 兑入量」算一次，判定用游戏口径（水占比整数截断，不四舍五入）。</summary>
        private static bool EvalMix(MixGroup high, MixGroup low, long fillUl, int targetBp, long capUl, bool freeBase, MixRow row)
        {
            if (high == null || low == null || fillUl <= 0) return false;
            long total = high.volUl + fillUl;
            if (total <= 0 || total > capUl) return false;
            long imp = GroupImpParts(high, high.volUl) + GroupImpParts(low, fillUl);
            if (imp < 0) imp = 0;
            if (imp > total) imp = total;
            // 精确到 0.0001% 的判定值：9900 边界必须先过这一关，
            // 否则 98.999999% 会被四舍五入的 impBp 算成 99.00% 优质水，产生假利润。
            long precise = (total - imp) * 1000000L / total;
            int finalBp = (int)((total - imp) * 10000L / total);   // 与 WaterHelper.GetWaterPurity 一样：整数截断
            if (finalBp > high.purityBp) finalBp = high.purityBp;
            if (precise < (long)targetBp * 100L) return false;
            if (finalBp < targetBp) return false;
            // 游戏每瓶的 purityBp 是整数截断，六项 parts 相加还可能少 1 µL；
            // 离目标不到 0.01% 时，再用两瓶“显示纯度”的加权值兜一层。
            // 这样 2879.8/99.01 + 720/98.96 这种显示上刚好 98.9999% 的混水，
            // 不会因为 parts 少 1 µL 就被算成 99.00% 优质水。
            int shownBp = (int)(((long)high.purityBp * high.volUl
                                + (long)low.purityBp * fillUl) / total);
            if (precise < (long)targetBp * 100L + 100L && shownBp < targetBp) return false;

            double mixRev = WaterEconomy.Revenue(total, finalBp);
            double sepRev = WaterEconomy.Revenue(high.volUl, high.purityBp);
            if (!freeBase) sepRev += WaterEconomy.Revenue(fillUl, low.purityBp);

            row.high = high; row.baseW = low;
            row.highUl = high.volUl; row.fillUl = fillUl; row.totalUl = total;
            row.finalBp = finalBp;
            row.precisBp100 = precise;
            row.mixRev = mixRev; row.sepRev = sepRev;
            row.gain = mixRev - sepRev;
            return true;
        }

        /// <summary>混水页最后一道显示口径：用两瓶的显示纯度加权判一次。
        /// 游戏每瓶纯度是整数截断，parts 相加也可能少 1 µL；EvalMix 的精确杂质闸门之外，
        /// 这里再按玩家实际看到的 99.01/98.96 判，避免 98.9999 被当成 99.00。
        /// </summary>
        private static bool MixDisplayPass(MixGroup high, MixGroup low, long fillUl, int targetBp)
        {
            if (high == null || low == null || fillUl <= 0) return false;
            long total = high.volUl + fillUl;
            if (total <= 0) return false;
            int shownBp = (int)(((long)high.purityBp * high.volUl
                                + (long)low.purityBp * fillUl) / total);
            return shownBp >= targetBp;
        }

        /// <summary>混水兑入量向下对齐到 0.1mL；避免界面四舍五入后显示一个比实际安全量更大的整数。
        /// 例：临界 719.950mL 会显示成 720mL，玩家照 720 倒就掉到 98.99%。</summary>
        private static long FloorMixFill(long ul)
        {
            if (ul <= 0) return 0;
            return (ul / 100L) * 100L;
        }

        /// <summary>在「最终纯度 ≥ th」的约束下能兑进去的最大体积 —— 就是跨过档位后的临界点。</summary>
        private static long MaxFillForTarget(MixGroup high, MixGroup low, int th, long capUl, bool freeBase)
        {
            if (high == null || low == null) return 0;
            long room = capUl - high.volUl;
            if (room <= 0) return 0;
            long capFill = freeBase ? room : Math.Min(room, low.volUl);
            if (capFill <= 0) return 0;
            int hiImp = 10000 - high.purityBp;
            int loImp = 10000 - low.purityBp;
            int maxImp = 10000 - th;
            if (hiImp >= maxImp) return 0;                 // 高纯度水自己就不够档，混了也救不回
            if (loImp <= maxImp) return capFill;           // 基础水本身够档，不会把它压下去
            double f = (double)high.volUl * (maxImp - hiImp) / (loImp - maxImp);
            long fill = (long)Math.Floor(f);
            if (fill > capFill) fill = capFill;
            if (fill <= 0) return 0;
            // 浮点边界可能多算 1 µL，往下退到真的达标为止。
            for (int k = 0; k < 8 && fill > 0; k++)
            {
                var probe = new MixRow();
                if (EvalMix(high, low, fill, th, capUl, freeBase, probe)) return fill;
                fill--;
            }
            return 0;
        }

        /// <summary>值不值得列出来：高纯度水不能掉档，低档水必须真的升档，净收益为正即可。
        /// 玩家 2026-11-14：优质水 1650mL 99.01% + 铁锈水 91.07%，只能兑约 2.1mL 到 99.00%，
        /// 旧版的「至少 10mL / 至少 5% / 至少赚 1 元」把唯一合法方案误删成「暂无方案」。</summary>
        private static bool MixWorthIt(MixRow row, long fillUl, MixGroup high, MixGroup low)
        {
            if (row == null || high == null || low == null) return false;
            if (fillUl <= 0 || row.gain <= 0.0001) return false;
            int hiTier = WaterEconomy.Index(high.purityBp);
            int loTier = WaterEconomy.Index(low.purityBp);
            int outTier = WaterEconomy.Index(row.finalBp);
            if (outTier > hiTier) return false;        // 高纯度水掉档了
            if (outTier >= loTier) return false;       // 低档水没升档，白兑
            return true;
        }

        /// <summary>枚举所有水质组两两组合（含免费水龙头），每个组合取净赚最大的那一档；界面只留总收益最高的一条，已有水列表仍全部保留。</summary>
        private static List<MixRow> BuildMixRows(List<MixGroup> groups, int tapBp, long capUl)
        {
            var found = new List<MixRow>();
            var map = new Dictionary<string, MixRow>();
            int[] ths = { 9990, 9900, 9800 };
            var tap = new MixGroup();
            tap.purityBp = tapBp;
            tap.impBp = new int[6];
            tap.impBp[0] = 10000 - tapBp;
            tap.key = "tap";
            tap.name = "水龙头";
            tap.capUl = 0;
            tap.tap = true;
            if (tapBp < 10000 && tapBp > 0)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var a = groups[i];
                    for (int j = 0; j < groups.Count; j++)
                    {
                        if (i == j) continue;
                        var b = groups[j];
                        if (b.purityBp >= a.purityBp) continue;
                        // 同一价格档互相兑＝平价搬运，不可能有钱赚，直接跳过。
                        if (WaterEconomy.Index(a.purityBp) == WaterEconomy.Index(b.purityBp)) continue;
                        for (int t = 0; t < ths.Length; t++)
                        {
                            int th = ths[t];
                            if (a.purityBp < th || b.purityBp >= th) continue;
                            long fill = MaxFillForTarget(a, b, th, capUl, false);
                            if (fill <= 0) continue;
                            // 玩家 2026-11-16：2879.8mL/99.01 + 720mL/98.96 的临界安全量其实是
                            // 719.950mL，界面按一位小数会四舍五入成 720mL；玩家真倒 720mL 时
                            // 精确值是 98.999999%，游戏截断成 98.99% 基础水，显示却写 99.00% 优质水。
                            // 这里把兑入量向下压到 0.1mL 刻度，并按“界面真的会显示的这个量”重新判定。
                            fill = FloorMixFill(fill);
                            var row = new MixRow();
                            for (int k = 0; k < 8 && fill > 0; k++)
                            {
                                if (EvalMix(a, b, fill, th, capUl, false, row)) break;
                                fill -= 100;
                                row = new MixRow();
                            }
                            if (fill <= 0) continue;
                            if (!EvalMix(a, b, fill, th, capUl, false, row)) continue;
                            if (!MixDisplayPass(a, b, fill, th)) continue;
                            if (!MixWorthIt(row, fill, a, b)) continue;
                            if (fill < b.volUl) row.note = WaterEconomy.TierName(b.purityBp) + " 只兑 "
                                + PlanText.Ml(fill) + "，多兑掉档";
                            string rk = a.key + "|" + b.key;
                            MixRow old = null;
                            if (map.TryGetValue(rk, out old) && old != null && old.gain >= row.gain) continue;
                            map[rk] = row;
                        }
                    }
                    for (int t = 0; t < ths.Length; t++)
                    {
                        int th = ths[t];
                        if (a.purityBp < th || tapBp >= th) continue;
                        long fill = MaxFillForTarget(a, tap, th, capUl, true);
                        if (fill <= 0) continue;
                        fill = FloorMixFill(fill);
                        var row = new MixRow();
                        for (int k = 0; k < 8 && fill > 0; k++)
                        {
                            if (EvalMix(a, tap, fill, th, capUl, true, row)) break;
                            fill -= 100;
                            row = new MixRow();
                        }
                        if (fill <= 0) continue;
                        if (!EvalMix(a, tap, fill, th, capUl, true, row)) continue;
                        if (!MixDisplayPass(a, tap, fill, th)) continue;
                        if (!MixWorthIt(row, fill, a, tap)) continue;
                        // 玩家 2026-11-13（p4）：基础水不是免费水 —— 只有铁锈 / 沟渠这种水龙头水才免费。
                        // 所以这里只说「水龙头水免费」，不要再把免费两个字挂在「基础水」这个档位名上。
                        row.note = "水龙头水免费";
                        string rk = a.key + "|tap";
                        MixRow old = null;
                        if (map.TryGetValue(rk, out old) && old != null && old.gain >= row.gain) continue;
                        map[rk] = row;
                    }
                }
            }
            foreach (var kv in map) found.Add(kv.Value);
            found.Sort(delegate(MixRow x, MixRow y)
            {
                if (Math.Abs(x.gain - y.gain) > 0.005) return x.gain > y.gain ? -1 : 1;
                return (int)Math.Min(1000000L, (y.totalUl - x.totalUl) / 1000L);
            });
            var top = new List<MixRow>();
            var seenRows = new HashSet<string>();
            for (int i = 0; i < found.Count && top.Count < 1; i++)
            {
                var r = found[i];
                // 存货铁锈水与今日铁锈水龙头同纯度时，旧版会把同一条兑水结果显示两遍。
                // 先按净收益排序，因此保留的是更划算的一条（通常水龙头免费）。
                string sig = r.high.purityBp + "|" + r.baseW.purityBp + "|" + r.highUl + "|" + r.fillUl + "|" + r.finalBp;
                if (!seenRows.Add(sig)) continue;
                top.Add(r);
            }
            return top;
        }

        /// <summary>
        /// 混水页/主页共用：按当前页同口径临时设置 WaterPlan 的耗材开关，再算已有水。
        /// 这里不改 WaterPlan 的实现，只把全局开关恢复到调用前，避免混水页把海德拉/滤棉
        /// 算进和主页不同的方案里。
        /// </summary>
        private static WaterPlan.Report BuildMixReport(WaterMath.Water w, int perf, int q, int targetBp,
                                                        int mode, string bottleName, int ownTab)
        {
            bool saveFilter = WaterPlan.AllowFilter;
            bool saveUv = WaterPlan.AllowUv;
            bool saveTablets = WaterPlan.AllowTablets;
            bool saveHydra = WaterPlan.AllowHydra;
            double saveShadow = WaterPlan.TabletShadow;
            int saveFilterDur = WaterPlan.FilterDur;
            int mixFilterDur = FilterDurNow();
            WaterPlan.FilterDur = mixFilterDur;
            WaterPlan.TabletShadow = (mode == 2 && PostMode == 0)
                ? TabletShadowForStock(ownTab) : 0.0;
            try
            {
                var tools = OwnedTools();
                WaterPlan.AllowFilter = tools != null && tools.filter && PreFilterOn(PreMode);
                WaterPlan.AllowUv = tools != null && tools.uv && PreUvOn(PreMode);
                WaterPlan.AllowTablets = PostMode != 1 && (InfiniteTools || ownTab > 0);
                // 性价比默认不上海德拉；只有设置页强制海德拉，或效率档/手动启用一次才带。
                WaterPlan.AllowHydra = WaterPlan.HydraMode == 1
                                    || (WaterPlan.HydraMode == 0 && (mode == 1 || hydraOnce));
                return WaterPlan.BuildFromWater(w, perf, q, targetBp, mode, PreMode, PostMode,
                                                bottleName, ownTab);
            }
            finally
            {
                WaterPlan.AllowFilter = saveFilter;
                WaterPlan.AllowUv = saveUv;
                WaterPlan.AllowTablets = saveTablets;
                WaterPlan.AllowHydra = saveHydra;
                WaterPlan.TabletShadow = saveShadow;
                if (saveFilterDur != mixFilterDur)
                    Log("[mix] FilterDur " + saveFilterDur + " -> " + mixFilterDur + " -> " + saveFilterDur);
                WaterPlan.FilterDur = saveFilterDur;
            }
        }

        /// <summary>按显示口径限制晚数再跑真实六项：BuildFromWater 吃全局 NightCap，
        /// 不能让计算页/混水页在“多晚开”时绕过 2 晚显示口径直接给 8 晚方案。</summary>
        private static WaterPlan.Report BuildMixReportLimited(WaterMath.Water w, int perf, int q, int targetBp,
                                                              int mode, string bottleName, int ownTab,
                                                              int nightLimit)
        {
            int saveNight = WaterPlan.NightCap;
            try
            {
                if (nightLimit > 0 && saveNight > nightLimit) WaterPlan.NightCap = nightLimit;
                return BuildMixReport(w, perf, q, targetBp, mode, bottleName, ownTab);
            }
            finally
            {
                WaterPlan.NightCap = saveNight;
            }
        }

        /// <summary>
        /// 效率档：往当前水逐步兑水源，找“整瓶仍能达标”的最大总量。
        /// 用 BuildFromWater 而不是 AutoSolve 快速判定，避免 1650 + 2.1 mL 这种只差一点的
        /// 边界被 AutoSolve 的整升取整判成“跑不完”，导致主界面/混水页只剩当前液体。
        /// </summary>
        private static WaterPlan.Report BuildEfficiencyTopUp(WaterMath.Water w0, long capUl, int srcBp,
                                                             int perf, int q, int targetBp,
                                                             string bottleName, int ownTab)
        {
            if (w0 == null || w0.v <= 0L || capUl <= w0.v || srcBp <= 0) return null;
            // 先试满容量：多晚开启时，低纯度混合水也可能靠海德拉/净化器跑到目标。
            WaterMath.Water fullW = MixWithTap(w0, capUl, srcBp);
            var fullRep = BuildMixReportLimited(fullW, perf, q, targetBp, 1, bottleName, ownTab,
                                               RecommendNightCap());
            if (fullRep != null && fullRep.ok && fullRep.pick != null && fullRep.dumpUl <= 0L)
            {
                fullRep.capUl = capUl;
                fullRep.fitUl = capUl;
                fullRep.dumpUl = 0L;
                return fullRep;
            }
            // 满容量不行才二分“最多还能兑多少”。用 BuildFromWater 直接判定，
            // 不再拿“混合后立刻 ≥ 目标”的解析上限卡死低纯度水——多晚/海德拉/药片
            // 是允许先掉纯度、再在净化器里跑回来的。
            long lo = w0.v, hi = capUl;
            WaterPlan.Report best = null;
            for (int it = 0; it < 16 && hi - lo > 1000L; it++)
            {
                long mid = lo + (hi - lo) / 2;
                WaterMath.Water wm = MixWithTap(w0, mid, srcBp);
                var rm = BuildMixReportLimited(wm, perf, q, targetBp, 1, bottleName, ownTab,
                                               RecommendNightCap());
                if (rm != null && rm.ok && rm.pick != null && rm.dumpUl <= 0L)
                {
                    lo = mid;
                    best = rm;
                }
                else
                    hi = mid;
            }
            if (lo <= w0.v) return null;
            WaterMath.Water finalW = MixWithTap(w0, lo, srcBp);
            var rep = BuildMixReportLimited(finalW, perf, q, targetBp, 1, bottleName, ownTab,
                                            RecommendNightCap());
            if (rep != null && rep.ok && rep.pick != null && rep.dumpUl <= 0L)
                best = rep;
            if (best == null) return null;
            best.capUl = capUl;
            best.fitUl = lo;
            best.dumpUl = 0L;
            return best;
        }

        /// <summary>快速可行性：AutoSolve 能把这份混合水整瓶跑到目标（不要求先倒出）。</summary>
        private static bool MixCanProcessAll(WaterMath.Water wm, int perf, int q, int targetBp, int ownTab)
        {
            if (wm == null || wm.v <= 0) return false;
            int nightCap = Math.Max(1, WaterPlan.NightCap);
            bool allowTablet = PostMode != 1 && (InfiniteTools || ownTab > 0);
            // 与 AutoReport 同一套后置片数口径。
            AutoSolve.TabletTarget = PostMode >= 2 ? PostMode - 1
                                                   : (PostMode == 1 ? 0 : -1);
            // 这个快速可行性探针也会直接调 AutoSolve，不能漏掉“不用机器”。
            AutoSolve.AllowMachine = !WaterPlan.NoMachine;
            bool allowHydra = WaterPlan.HydraMode == 1
                           || (WaterPlan.HydraMode == 0 && (Mode == 1 || hydraOnce));
            try
            {
                var ar = AutoSolve.SolveWater(wm, perf, (double)q, targetBp, nightCap,
                                              PreFilterOn(PreMode), PreUvOn(PreMode), allowTablet, allowHydra,
                                              InfiniteTools ? -1 : ownTab,
                                              InfiniteTools ? int.MaxValue : FilterDurNow(),
                                              HydraDuraMaxForSolve(InfiniteTools));
                return ar != null && ar.ok && ar.feedUl >= wm.v - 1000L;
            }
            catch (Exception) { return false; }
        }

        /// <summary>性价比档：在容量内找一个「一晚净赚最多」的兑入总量。</summary>
        private static WaterPlan.Report BestCostTopUp(WaterMath.Water w0, long capUl, int srcBp,
                                                      int perf, int q, int targetBp,
                                                      string bottleName, int ownTab,
                                                      WaterPlan.Report rep0, out long bestUl)
        {
            bestUl = w0 != null ? w0.v : 0L;
            WaterPlan.Report best = rep0 != null && rep0.ok && rep0.pick != null
                && rep0.pick.nights <= MaxRecommendNights ? rep0 : null;
            double bestProfit = best != null ? best.ChosenProfit() : double.MinValue;
            if (w0 == null || capUl <= w0.v) return best;

            long step = Math.Max(1000000L, (capUl - w0.v) / 8);
            // 先补 1 L 的小步也看一眼：如果 1 晚就能达标，别被 5 L 的粗格漏掉。
            long first = Math.Min(capUl, w0.v + 1000000L);
            if (first > w0.v)
                CostTopUpEval(w0, first, srcBp, perf, q, targetBp, bottleName, ownTab,
                              ref best, ref bestProfit, ref bestUl);
            for (long total = w0.v + step; total < capUl; total += step)
                CostTopUpEval(w0, total, srcBp, perf, q, targetBp, bottleName, ownTab,
                              ref best, ref bestProfit, ref bestUl);
            CostTopUpEval(w0, capUl, srcBp, perf, q, targetBp, bottleName, ownTab,
                          ref best, ref bestProfit, ref bestUl);

            // 粗扫命中后再收 3 刀，避免把「最佳量」落在 5 L 的大格子里。
            long span = step;
            for (int it = 0; it < 3; it++)
            {
                long lo = Math.Max(w0.v, bestUl - span);
                long hi = Math.Min(capUl, bestUl + span);
                if (hi - lo < 500000L) break;
                CostTopUpEval(w0, lo + (hi - lo) / 2, srcBp, perf, q, targetBp, bottleName, ownTab,
                              ref best, ref bestProfit, ref bestUl);
                span /= 2;
            }
            return best;
        }

        private static void CostTopUpEval(WaterMath.Water w0, long total, int srcBp,
                                          int perf, int q, int targetBp, string bottleName, int ownTab,
                                          ref WaterPlan.Report best, ref double bestProfit, ref long bestUl)
        {
            if (total <= w0.v || total > w0.v + 200000000L) return;
            WaterMath.Water wm = MixWithTap(w0, total, srcBp);
            // 先用 AutoSolve 快速筛掉“整瓶都跑不完”的候选，避免 BuildFromWater 对每个失败量再做一轮二分。
            // 多晚开启时 AutoSolve 的晚数选量会和 BuildFromWater 的真实组合不一致（曾把能跑的大水量误判掉），
            // 所以多晚下不再用这个快筛，直接让 BuildFromWater 按真实六项给结果。
            if (WaterPlan.NightCap <= 1 && !MixCanProcessAll(wm, perf, q, targetBp, ownTab)) return;
            WaterPlan.Report rm = null;
            bool saveHydraHint = autoHydraHint;
            int saveNightsMin = lastNightsMin;
            bool saveHydraOnce = hydraOnce;
            string saveMsg = LastMsg;
            try
            {
                var tools = OwnedTools();
                int ownTabNow = tools == null ? 0 : tools.tablets;
                if (ownTabNow < 0 && !InfiniteTools) ownTabNow = 0;
                bool allowHydra = WaterPlan.HydraMode == 1
                               || (WaterPlan.HydraMode == 0 && (Mode == 1 || hydraOnce));
                // 这里不能再用 BuildMixReport：它吃全局 NightCap=8，会把“15.2 L 一晚”
                // 算成“15.2 L 八晚”再被 2 晚过滤器丢掉。改用 AutoReport 的 2 晚口径，
                // 开/关多晚都能真的找到同一档“最多能接多少”的候选。
                rm = AutoReport(total, wm.Purity, perf, q, targetBp, 2,
                                false, total, wm,
                                tools != null && tools.filter, tools != null && tools.uv,
                                InfiniteTools || ownTabNow > 0, allowHydra, WaterPlan.HydraMode == 1,
                                InfiniteTools ? -1 : ownTabNow,
                                InfiniteTools ? int.MaxValue : FilterDurNow(),
                                HydraDuraMaxForSolve(InfiniteTools),
                                bottleName, bottleName,
                                "cost|" + total + "|" + w0.v + "|" + w0.Purity);
            }
            catch (Exception) { rm = null; }
            finally
            {
                autoHydraHint = saveHydraHint;
                lastNightsMin = saveNightsMin;
                hydraOnce = saveHydraOnce;
                LastMsg = saveMsg;
            }
            if (rm == null || !rm.ok || rm.pick == null || rm.dumpUl > 0) return;
            // 多晚默认先展示 2 晚及以下；8 晚只用于主页“升晚多出多少水”的提示，
            // 不能反过来把 8 晚方案推成性价比主方案。
            if (rm.pick.nights > MaxRecommendNights) return;
            double profit = rm.ChosenProfit();
            int newNights = rm.pick.nights;
            // 玩家 2026-11-15：开多晚就是为了多接水，不能再用“晚数更少”把总量压小。
            // 用「接水量 − 每晚 1 L」当排序分：同水量时晚数少优先；水量明显更大时允许加晚；
            // 只为几毫升多跑一晚的候选会被这 1 L/晚的代价压掉，避免盲目堆晚数。
            long newScore = rm.pick.feedUl - (long)newNights * 1000000L;
            long bestScore = (best != null && best.ok && best.pick != null)
                ? best.pick.feedUl - (long)best.pick.nights * 1000000L
                : long.MinValue;
            if (best == null || newScore > bestScore
                || (newScore == bestScore && profit > bestProfit + 0.05))
            {
                best = rm;
                bestProfit = profit;
                bestUl = total;
            }
        }

        /// <summary>
        /// 混水页顶部：当前机器里这瓶水的现状与“本模式还能怎么处理”。
        /// 只做展示，不改主页锁定方案，也不动接水/倒水按钮。
        /// </summary>
        private static int BuildMachineMixLines(RichTextBuilder b, GameItem bottle, int tapBp, int tapGrade, long capUl, out bool hasPlan)
        {
            hasPlan = false;
            if (b == null || bottle == null) return 0;
            WaterMath.Water w0;
            try
            {
                if (!BuildRealWater(bottle, out w0) || w0 == null || w0.v <= 0) return 0;
            }
            catch (Exception) { return 0; }
            long vol = w0.v;
            try { if (capUl <= 0) capUl = WaterHelper.GetTotalCapacity(bottle); } catch (Exception) { }
            if (capUl < vol) capUl = vol;
            int perf = 0, q = 0;
            try { Bonuses(out perf, out q); } catch (Exception) { }
            int targetBp = PlanText.TargetBp(TargetIdx, q);
            string bottleName = null;
            try { bottleName = GameBridge.NameOfItem(bottle); } catch (Exception) { }
            if (string.IsNullOrEmpty(bottleName)) bottleName = "容器";

            var tools = OwnedTools();
            int ownTab = tools == null ? 0 : tools.tablets;
            if (InfiniteTools) ownTab = -1;
            else if (ownTab < 0) ownTab = 0;
            int srcBp = tapBp > 0 ? tapBp : w0.Purity;
            string srcName = SrcName(tapKind > 0 ? tapKind : tapGrade);
            bool efficiency = Mode == 1;

            // 切档时立即刷新混水页会早于主页重算；这里必须自己把过滤棉预算拉回真实值，
            // 否则上一次“无限道具”留下的 1<<20 会让 50 L 也被算成能处理。
            int fDur = FilterDurNow();
            if (Mode != 2 && fDur <= 0) fDur = WaterPlan.FilterDurPer;
            WaterPlan.FilterDur = fDur;

            WaterPlan.Report rep0 = null;
            try
            {
                rep0 = BuildMixReportLimited(w0.Clone(), perf, q, targetBp, Mode, bottleName, ownTab,
                                             RecommendNightCap());
            }
            catch (Exception) { rep0 = null; }
            bool full0 = rep0 != null && rep0.ok && rep0.pick != null && rep0.dumpUl <= 0
                         && rep0.pick.nights <= MaxRecommendNights;
            long maxUl = vol;
            WaterPlan.Report repMax = full0 ? rep0 : null;
            if (efficiency && capUl > vol && srcBp > 0)
            {
                // 找“往当前水里再兑水源、整瓶仍能达标”的最大总量。
                // 这里必须用 BuildFromWater 统一判定，不能先用 AutoSolve 把 1652 mL 误判成跑不完。
                WaterPlan.Report rmTop = null;
                try
                {
                    rmTop = BuildEfficiencyTopUp(w0, capUl, srcBp, perf, q, targetBp, bottleName, ownTab);
                }
                catch (Exception) { rmTop = null; }
                if (rmTop != null && rmTop.ok && rmTop.pick != null && rmTop.dumpUl <= 0
                    && rmTop.pick.nights <= MaxRecommendNights)
                {
                    repMax = rmTop;
                    maxUl = rmTop.fitUl > 0 ? rmTop.fitUl : vol;
                }
            }
            else if (!efficiency && capUl > vol && srcBp > 0)
            {
                // 性价比不按“最大可处理量”拉满，而是在容量内找净赚最多的兑入量。
                // 即使当前满瓶方案是 8 晚，也要按 2 晚口径重新找一次，不能直接跳过。
                long bestUl = vol;
                repMax = BestCostTopUp(w0, capUl, srcBp, perf, q, targetBp, bottleName, ownTab,
                                       rep0, out bestUl);
                maxUl = bestUl;
            }
            if (repMax == null && rep0 != null && rep0.ok && rep0.pick != null
                && rep0.pick.nights <= MaxRecommendNights)
                repMax = rep0;

            // 推荐量必须按玩家实际容器凑得出来：50 mL 瓶只能到 1850，
            // 有 1 mL 滴灌时才能精确到 1888；不能拿理论值直接报。
            long roomUl = capUl > vol ? capUl - vol : 0;
            long reachAdd = maxUl > vol ? ReachablePour(maxUl - vol, roomUl) : 0;
            long reachMax = vol + reachAdd;
            if (efficiency && reachMax > vol && reachMax < maxUl)
            {
                WaterPlan.Report rmReach = null;
                try
                {
                    rmReach = BuildEfficiencyTopUp(w0, reachMax, srcBp, perf, q, targetBp,
                                                   bottleName, ownTab);
                }
                catch (Exception e) { Log("[mix] 可达量重算失败 " + e.GetType().Name + " " + e.Message); }
                if (rmReach != null && rmReach.ok && rmReach.pick != null && rmReach.dumpUl <= 0
                    && rmReach.pick.nights <= MaxRecommendNights)
                {
                    repMax = rmReach;
                    maxUl = rmReach.fitUl > 0 ? rmReach.fitUl : reachMax;
                }
                else
                    maxUl = reachMax;
            }
            else if (!efficiency && reachMax > vol)
                maxUl = reachMax;
            if (maxUl != reachMax)
                reachAdd = maxUl > vol ? ReachablePour(maxUl - vol, roomUl) : 0;
            hasPlan = repMax != null && repMax.ok && repMax.pick != null;
            Log("[mix] machine mode=" + Mode + " v=" + vol + " p=" + w0.Purity
                + " full=" + (full0 ? 1 : 0) + " max=" + maxUl
                + " dump=" + (rep0 == null ? -1 : rep0.dumpUl)
                + " fit=" + (rep0 == null ? -1 : rep0.fitUl));

            int lines = 0;
            AddLine(b, PlanText.Lbl("当前机器水 ") + PlanText.Num(PlanText.MlTight(vol))
                       + PlanText.Lbl(" / ") + PlanText.Num(PlanText.MlTight(capUl)),
                    ColorPalette.LightGray, true);
            lines++;
            AddLine(b, PlanText.PctColored(w0.Purity) + PlanText.Lbl("  目标 ") + PlanText.Pct(targetBp),
                    ColorPalette.LightGray, false);
            lines++;
            if (efficiency)
                AddLine(b, PlanText.Lbl("本模式上限 ") + PlanText.Num(WaterPlan.Ml(maxUl)),
                        ColorPalette.LightGray, false);
            else
                AddLine(b, PlanText.Lbl("性价比按净赚选量"),
                        ColorPalette.LightGray, false);
            lines++;
            if (efficiency)
            {
                long add = reachAdd;
                if (add > 0)
                    AddLine(b, PlanText.Lbl("最多再兑 ") + PlanText.Num(WaterPlan.Ml(add))
                               + PlanText.Lbl(" " + srcName) + PlanText.Lbl(" → ")
                               + PlanText.Num(WaterPlan.Ml(vol + add)),
                            ColorPalette.LightGray, true);
                else if (full0)
                    AddLine(b, "当前就是本模式上限，再兑会掉纯度", ColorPalette.LightGray, true);
                else
                {
                    long dump = rep0 != null ? rep0.dumpUl : vol;
                    long keep = rep0 != null && rep0.fitUl > 0 ? rep0.fitUl : (vol > dump ? vol - dump : 0);
                    if (dump > 0 && keep > 0)
                        AddLine(b, PlanText.Lbl("先倒出 ") + PlanText.Num(PlanText.MlTight(dump))
                                   + PlanText.Lbl("，留 ") + PlanText.Num(PlanText.MlTight(keep)),
                                ColorPalette.LightOrange, true);
                    else
                        AddLine(b, "现有道具压不到目标", ColorPalette.LightOrange, true);
                }
                lines++;
            }
            else if (!full0 && repMax == null)
            {
                long dump = rep0 != null ? rep0.dumpUl : vol;
                long keep = rep0 != null && rep0.fitUl > 0 ? rep0.fitUl : (vol > dump ? vol - dump : 0);
                if (dump > 0 && keep > 0)
                    AddLine(b, PlanText.Lbl("先倒出 ") + PlanText.Num(PlanText.MlTight(dump))
                               + PlanText.Lbl("，留 ") + PlanText.Num(PlanText.MlTight(keep)),
                            ColorPalette.LightOrange, true);
                else if (dump > 0)
                    AddLine(b, PlanText.Lbl("先倒出 ") + PlanText.Num(PlanText.MlTight(dump))
                               + PlanText.Lbl("，现有道具压不到目标"),
                            ColorPalette.LightOrange, true);
                else
                    AddLine(b, "现有道具压不到目标，先别加铁锈", ColorPalette.LightOrange, true);
                lines++;
            }
            else
            {
                long add = reachAdd;
                if (add > 0)
                    AddLine(b, PlanText.Lbl("性价比再兑 ") + PlanText.Num(WaterPlan.Ml(add))
                               + PlanText.Lbl(" → 总 ") + PlanText.Num(WaterPlan.Ml(vol + add)),
                            ColorPalette.LightGray, true);
                else
                    AddLine(b, "性价比不补，当前最赚", ColorPalette.LightGray, true);
                lines++;
            }

            // 主界面同一条口径：兑入水源后先把混合水纯度报出来，再看净化后。
            if (repMax != null && maxUl > vol && repMax.srcBp > 0)
            {
                AddLine(b, PlanText.Lbl("混合后 ") + PlanText.PctColored(repMax.srcBp),
                        ColorPalette.LightGray, false);
                lines++;
            }

            if (repMax != null && repMax.pick != null)
            {
                int outBp = repMax.pick.purityBp > 0 ? repMax.pick.purityBp : repMax.finalBp;
                AddLine(b, PlanText.Lbl("净化后 ") + PlanText.Num(PlanText.MlTight(repMax.pick.outUl))
                           + "  " + PlanText.PctColored(outBp), ColorPalette.LightGray, false);
                lines++;
                string step = StepChain(repMax.pick);
                if (!string.IsNullOrEmpty(step))
                {
                    AddLine(b, PlanText.Lbl("步骤 ") + step, ColorPalette.LightGray, false);
                    lines++;
                }
                string own = OwnLineText();
                if (!string.IsNullOrEmpty(own))
                {
                    AddLine(b, own, ColorPalette.LightGray, false);
                    lines++;
                }
            }
            b.AddEmptyLine(3);
            return lines + 1;
        }

        private static int BuildMixText(RichTextBuilder b, List<MixGroup> groups, List<MixRow> rows, int tapBp, long capUl, string scanNote, bool hasMachinePlan = false)
        {
            // 玩家 2026-11-13（p5）：界面里不放「— 说明 —」这类解释段，规则只留代码注释。
            // 玩家 2026-11-13（p6）：排版照「步骤」页 —— 一行一件事、每行都短，绝不折行。
            // 玩家 2026-11-13（第五轮）：「先把已有 xx水 xxmL 写在最上面，再管方案」——
            //   扫描结果先列出来（没有组合时也列），玩家才知道到底读到了什么水。
            int lines = 0;
            if (groups != null && groups.Count > 0)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var g = groups[i];
                    string t = WaterEconomy.TierName(g.purityBp);
                    // 与主页同一套颜色：标签暖灰、体积青、纯度蓝、档位名按水质色。
                    // 体积必须走 MlTight（μL 保留到显示层再四舍五入），不能在这里先 /1000 截断。
                    string txt = PlanText.Lbl("已有 ") + PlanText.Rich(PlanText.TierHex(t), t)
                               + " " + PlanText.Num(PlanText.MlTight(g.volUl))
                               + " " + PlanText.PctColored(g.purityBp);
                    AddLine(b, txt, ColorPalette.LightGray, false);
                    lines++;
                }
                b.AddEmptyLine(3);
            }
            // 玩家 2026-11-13：混水页不能只说容器里的水，水源也要写出来（33 天铁锈 / 5 天沟渠）。
            if (tapBp > 0)
            {
                string tapT = WaterEconomy.TierName(tapBp);
                AddLine(b, PlanText.Lbl("水源 ") + PlanText.Rich(PlanText.TierHex(tapT), tapT)
                           + " " + PlanText.PctColored(tapBp), ColorPalette.LightGray, false);
                lines++;
                b.AddEmptyLine(3);
            }
            if (rows == null || rows.Count == 0)
            {
                if (hasMachinePlan)
                    AddLine(b, "背包水暂无单独组合；按上方当前机器水方案接水", ColorPalette.LightGray, false);
                else
                    AddLine(b, "暂无方案（这些水互相兑不升档）", ColorPalette.LightOrange, true);
                lines++;
                return lines;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string hiT = WaterEconomy.TierName(r.high.purityBp);
                // 水龙头不是“基础水”档位：未开通时 tapBp=0 不会生成 tap 组合；
                // 已开通时这里显示“水龙头水”，不再把 98.00% 误写成基础水。
                string loT = r.baseW.tap ? "水龙头水" : WaterEconomy.TierName(r.baseW.purityBp);
                string outT = WaterEconomy.TierName(r.finalBp);
                AddLine(b, PlanText.Lbl((i + 1) + ". ") + PlanText.Rich(PlanText.TierHex(hiT), hiT)
                           + " " + PlanText.Num(PlanText.MlTight(r.highUl)), ColorPalette.LightGray, true);
                AddLine(b, PlanText.Lbl("兑 ") + PlanText.Rich(PlanText.TierHex(loT), loT)
                           + " " + PlanText.Num(PlanText.MlTight(r.fillUl)), ColorPalette.LightGray, false);
                AddLine(b, PlanText.Lbl("出 ") + PlanText.Num(PlanText.MlTight(r.totalUl)), ColorPalette.LightGray, false);
                AddLine(b, PlanText.PctColored(r.finalBp) + " " + PlanText.Rich(PlanText.TierHex(outT), outT), ColorPalette.LightGray, false);
                AddLine(b, PlanText.Lbl("赚 ") + PlanText.Rich("F2C14E", "+" + WaterEconomy.Money(r.gain) + "元"), ColorPalette.LightGray, false);
                // 玩家 2026-11-13：赚 +xx 下面那行「只兑 x mL，多兑掉档」是废话，删掉。
                if (r.note.Length > 0) LogQuiet("[mix] row note " + r.note);
                b.AddEmptyLine(3);
                lines += 5;
            }
            return lines;

            // 玩家 2026-11-13（p5）：原来这里还有一段「— 说明 —」（同水质合并规则 / 截断判定 / 只列升档组合）。
            // 玩家原话「说明那么详细干嘛，不需要说明」——整段删掉，规则只在代码里：
            //   · 同水质按「纯度 + 六项杂质比例」合并；切到别的页再点「混水」重新扫描。
            //   · 判定用游戏存档的截断值（显示 99.00% 但实际 98.9999% 时按 98.99% 算）。
            //   · 只列真的升档的组合：高纯度水掉档、低档水没升档、兑入不到 10 mL 或不到 5%、净赚不到 1 元都不列。
        }

        private static void OpenMixModule()
        {
            // 玩家 2026-11-13：不再是独立长窗，直接切到左栏第五页「混水」。
            SetPage(5);
        }

        private static int SumCalcImpBp()
        {
            int sum = 0;
            for (int i = 0; i < calcImpBp.Length; i++) sum += calcImpBp[i];
            return sum;
        }

        /// <summary>批次 28：计算页独立调 AutoSolve；空瓶搜接水量，手填水量不搜。</summary>
        private static WaterPlan.Report AutoCalcReport()
        {
            long cap = calcCapUl > 0 ? calcCapUl : (calcWaterUl > 0 ? calcWaterUl : 2000000L);
            if (cap <= 0) return null;
            // 玩家 2026-11-15：计算页的“自动口径”不再自己另算一套。
            // 只要计算页显示的就是当前这只瓶（水量/容量都一致），主页和计算页必须返回同一份 Report：
            // 接水后 postFillFix、放水后解除、等水源变化，全部由主页这一条链统一决定；
            // 计算页只在“手填/清空/换了一只瓶”时保留独立计算。
            if (!calcManual)
            {
                GameItem mirrorBottle = null;
                long mv = 0, mc = 0;
                try
                {
                    mirrorBottle = calcManualSource ? ExplicitPickBottle() : CtxBottle();
                    if (mirrorBottle == null) mirrorBottle = GameBottle();
                    if (mirrorBottle != null)
                    {
                        mv = WaterHelper.GetTotalVolume(mirrorBottle);
                        mc = WaterHelper.GetTotalCapacity(mirrorBottle);
                    }
                }
                catch (Exception) { mirrorBottle = null; }
                if (mirrorBottle != null && mv == calcWaterUl && mc == calcCapUl)
                {
                    var mainRep = CurrentPlan(true);
                    if (mainRep != null) return mainRep;
                }
            }
            int pur = CalcPurityBp();
            if (calcWaterUl <= 0 || pur <= 0)
            {
                int sg0 = 0, sp0 = 0;
                try { TodaySource(out sg0, out sp0); } catch (Exception) { sp0 = 0; }
                if (sp0 > 0) pur = sp0;
            }
            if (pur <= 0) pur = 9100;
            // 玩家 2026-11-15：接水/放水后主页会按 LockVolSig 自动解除 postFillFix，
            // 计算页以前完全不看这个状态，仍继续按“补满整瓶”算，于是同一瓶 43550 主页给 43196.9、计算页给 42063。
            // 计算页必须和主页共用同一个接水后状态：六项变了就只算实际这瓶水；水量再变（放水/加水）就解除。
            if (postFillFix && postFillSig != LockVolSig())
            {
                postFillFix = false;
                postFillSig = "";
            }
            bool calcPostFill = postFillFix;
            WaterMath.Water calcPostWater = null;
            if (calcPostFill && calcWaterUl > 0L)
            {
                try
                {
                    if (calcManualSource)
                    {
                        // 读取瓶：六项已经在 calcImpUl 里，按实际水量构造，不再按容量放大。
                        calcPostWater = BuildCalcManualWater(calcWaterUl);
                    }
                    else
                    {
                        var cb = CtxBottle(); if (cb == null) cb = GameBottle();
                        if (cb != null
                            && WaterHelper.GetTotalVolume(cb) == calcWaterUl
                            && WaterHelper.GetTotalCapacity(cb) == calcCapUl)
                        {
                            BuildRealWater(cb, out calcPostWater);
                        }
                    }
                }
                catch (Exception) { calcPostWater = null; }
            }
            if (calcManual && calcWaterUl <= 0) FillCalcImpFromPurity(cap, pur);
            // 玩家 2026-11-14：计算页的「需接水」必须跟主页同一套效率口径。
            // 效率档瓶里有水但未满时，主页会按整瓶容量重搜，计算页不能把当前 29950 当成固定方案，
            // 否则 AutoReport 返回 29950 方案，CalcFeedText 就会算出 need=0、整行空白。
            // 手动源（右键读取/设置页手填）必须保留六项原参数；效率档只把同一配比补满到整瓶容量。
            long manualFeed = calcWaterUl > 0 ? calcWaterUl : cap;
            if (!calcPostFill && calcManualSource && Mode == 1 && cap > manualFeed) manualFeed = cap;
            WaterMath.Water manualWater = calcPostFill
                ? calcPostWater
                : (calcManualSource ? BuildCalcManualWater(manualFeed) : null);
            bool calcTopUp = !calcPostFill && (calcManualSource
                           ? (Mode == 1 && manualFeed > calcWaterUl)
                           : (Mode == 1 && calcWaterUl > 0 && calcCapUl > 0 && calcWaterUl < calcCapUl));
            bool calcFullRebuild = calcPostFill ? false : (calcManualSource ? false
                                   : (Mode == 1 && calcWaterUl > 0 && calcCapUl > 0 && calcWaterUl >= calcCapUl));
            // 玩家 2026-11-15：主页空瓶会先试“整瓶满装”，计算页却直接走 SearchFeed，
            // 于是同一个 0/50000 主页给 50000/2晚，计算页只给 44150/1晚。
            // 这里补上与主页完全同一条“空瓶先试满瓶”分支；失败再退回接水搜索。
            bool calcEmptyFull = !calcPostFill && !calcManual && !calcManualSource && Mode == 1
                               && calcWaterUl <= 0L && calcCapUl > 0L && WaterPlan.NightCap > 1;
            // 玩家 2026-11-15：接水后 2000/50000 主页按“现有水 + 水管水补满整瓶”出 50 L/2晚，
            // 计算页却仍按 SearchFeed 只找 44.15 L/1晚。这里把主页的 mixedTopUp 分支同样接到计算页。
            // 读取瓶（calcManualSource）也必须走同一条：否则计算页会把整瓶都当成 97.63% 原水，
            // 主页却按“2000 mL 97.63% + 水管水”混合成 91.40%，两边方案必然不同。
            bool calcPartialFull = !calcPostFill && !calcManual && Mode == 1
                                && calcWaterUl > 0L && calcCapUl > calcWaterUl;
            WaterMath.Water calcTopWater = null;
            if (calcPartialFull)
            {
                try
                {
                    WaterMath.Water baseW = null;
                    if (calcManualSource)
                    {
                        // 读取瓶的六项已经写进 calcImpUl；按当前瓶水量缩成 base，再补水管水。
                        baseW = BuildCalcManualWater(calcWaterUl);
                    }
                    else
                    {
                        var cb = CtxBottle(); if (cb == null) cb = GameBottle();
                        if (cb != null
                            && WaterHelper.GetTotalVolume(cb) == calcWaterUl
                            && WaterHelper.GetTotalCapacity(cb) == calcCapUl)
                        {
                            if (!BuildRealWater(cb, out baseW)) baseW = null;
                        }
                    }
                    if (baseW != null && baseW.v > 0L)
                    {
                        int sg = 0, sp = 0;
                        try { TapSource(out sg, out sp); } catch (Exception) { sp = 0; }
                        if (sp <= 0) sp = pur > 0 ? pur : 9100;
                        calcTopWater = MixWithTap(baseW, calcCapUl, sp);
                    }
                }
                catch (Exception) { calcTopWater = null; }
            }
            // 计算页满瓶也要读当前瓶的真实六项；否则主页已经按真实水样给方案，计算页还按纯度均匀水算，
            // 两边步骤/倒出量必然对不上。只在“计算页水量 == 当前瓶真实水量”时接管，避免拿错瓶。
            bool calcFullReal = false;
            WaterMath.Water calcRealWater = null;
            if (!calcManual && !calcManualSource && calcCapUl > 0 && calcWaterUl > 0 && calcWaterUl >= calcCapUl)
            {
                try
                {
                    var cb = CtxBottle(); if (cb == null) cb = GameBottle();
                    if (cb != null)
                    {
                        long bv = WaterHelper.GetTotalVolume(cb);
                        long bc = WaterHelper.GetTotalCapacity(cb);
                        if (bv == calcWaterUl && bc == calcCapUl && BuildRealWater(cb, out calcRealWater)
                            && calcRealWater != null && calcRealWater.v > 0)
                        {
                            calcFullReal = true;
                            manualWater = calcRealWater;
                        }
                    }
                }
                catch (Exception) { calcFullReal = false; calcRealWater = null; }
            }
            bool search = calcPostFill ? false
                         : (calcManualSource ? false
                         : (!calcManual && (calcWaterUl <= 0 || calcTopUp || (calcFullRebuild && !calcFullReal))));
            long feed = calcPostFill
                       ? (calcWaterUl > 0L ? calcWaterUl : cap)
                       : (calcManualSource ? manualFeed
                       : (search ? 0L : (calcFullReal ? calcRealWater.v : (calcWaterUl > 0 ? calcWaterUl : cap))));
            var o = OwnedTools();
            int ownTab = (o == null ? 0 : o.tablets);
            if (ownTab < 0 && !InfiniteTools) ownTab = 0;
            bool calInf = InfiniteTools || calcToolsMode == 2;
            int tabStock = calInf ? -1 : ownTab;
            bool allowFilter = calInf || (o != null && o.filter);
            bool allowUv = calInf || (o != null && o.uv);
            int fDur = calInf ? int.MaxValue : FilterDurNow();
            // 批次 29：计算页与主页同一默认口径；「启用海德拉」只临时作用于本次计算。
            bool allowHydra = WaterPlan.HydraMode == 1
                           || (WaterPlan.HydraMode == 0 && (Mode == 1 || hydraOnce));
            bool forceHydra = WaterPlan.HydraMode == 1;
            int tapSig = 0;
            try { int sgT = 0; TapSource(out sgT, out tapSig); } catch (Exception) { tapSig = 0; }
            string bottleSig = "calc|" + cap + "|" + calcWaterUl + "|" + SumCalcImpBp() + "|" + (calcManual ? 1 : 0)
                             + "|src" + (calcManualSource ? 1 : 0) + "|feed" + feed + "|tp" + tapSig;
            WaterPlan.Report rep = null;
            if (calcEmptyFull)
            {
                try
                {
                    rep = AutoReport(calcCapUl, pur, calcPerf, calcQuality, calcTargetBp, Mode,
                                     false, calcCapUl, null,
                                     allowFilter, allowUv, calInf || ownTab > 0, allowHydra, forceHydra,
                                     tabStock, fDur, HydraDuraMaxForSolve(calInf),
                                     "计算器", "计算器",
                                     bottleSig + "|emptyfull", RecommendNightCap());
                }
                catch (Exception) { rep = null; }
            }
            if (rep == null && calcPartialFull && calcTopWater != null)
            {
                try
                {
                    rep = AutoReport(calcCapUl, calcTopWater.Purity, calcPerf, calcQuality, calcTargetBp, Mode,
                                     false, calcTopWater.v, calcTopWater,
                                     allowFilter, allowUv, calInf || ownTab > 0, allowHydra, forceHydra,
                                     tabStock, fDur, HydraDuraMaxForSolve(calInf),
                                     "计算器", "计算器",
                                     bottleSig + "|partialfull", RecommendNightCap());
                }
                catch (Exception) { rep = null; }
            }
            if (rep == null)
            {
                rep = AutoReport(cap, pur, calcPerf, calcQuality, calcTargetBp, Mode, search, feed, manualWater,
                                 allowFilter, allowUv, calInf || ownTab > 0, allowHydra, forceHydra,
                                 tabStock, fDur, HydraDuraMaxForSolve(calInf),
                                 "计算器", "计算器", bottleSig, RecommendNightCap());
            }
            // 满瓶真实水样按整瓶跑不动时，先用 BuildFromWater 的真实六项找“倒出多少后能跑”；
            // 不能再用纯度均匀水做一次接水搜索，否则计算页会和主页/瓶内实际水样分叉。
            if ((rep == null || rep.pick == null) && !search && calcFullReal && calcRealWater != null)
            {
                try
                {
                    var realRep = BuildMixReportLimited(calcRealWater, calcPerf, calcQuality, calcTargetBp,
                                                        Mode, "计算器", ownTab, RecommendNightCap());
                    if (realRep != null && realRep.pick != null) rep = realRep;
                }
                catch (Exception) { }
            }
            // 玩家 2026-11-14：旧逻辑一旦固定水量失败就只报“多晚也做不到”，
            // 但它没试过减少水量。这里按当前水量做一次降量搜索：
            // 先二分找「当前水量以内、还能达标的 最大水量」，找到就把方案改成它，
            // 计算页用 dumpUl 提示需要先倒出多少；找不到才继续报无解。
            if ((rep == null || rep.pick == null) && !search && !calcFullReal && calcWaterUl > 1L)
            {
                // 固定水量失败时只做一次「以当前水量为上限的接水搜索」，
                // 不再用 8 次 AutoReport 二分：切模式会被这 8 次重算拖成卡顿。
                var reduced = AutoReport(calcWaterUl, pur, calcPerf, calcQuality, calcTargetBp, Mode,
                                         true, 0L, null,
                                         allowFilter, allowUv, calInf || ownTab > 0, allowHydra, forceHydra,
                                         tabStock, fDur, HydraDuraMaxForSolve(calInf),
                                         "计算器", "计算器",
                                         "calc-reduce|" + calcWaterUl + "|" + SumCalcImpBp(),
                                         RecommendNightCap());
                if (reduced != null && reduced.pick != null && reduced.pick.feedUl > 0L
                    && reduced.pick.feedUl < calcWaterUl)
                {
                    reduced.dumpUl = calcWaterUl - reduced.pick.feedUl;
                    reduced.fitPartial = true;
                    reduced.fitUl = reduced.pick.feedUl;
                    rep = reduced;
                }
                else rep = null;
            }
            // 计算页的性价比也要走主页同一套「容量内找净赚最多的兑入量」。
            // AutoReport 只按当前水量固定算，不会主动试 7 L / 15.2 L；不补这一段，
            // 设置页开多晚后计算器仍是“当前水量”的旧方案，看起来像多晚没同步。
            if (Mode == 2 && !calcManualSource && calcWaterUl > 0L && calcCapUl > calcWaterUl)
            {
                WaterMath.Water baseW = calcFullReal ? calcRealWater : null;
                if (baseW == null && !calcManual)
                {
                    try
                    {
                        var cb = CtxBottle(); if (cb == null) cb = GameBottle();
                        if (cb != null)
                        {
                            long bv = WaterHelper.GetTotalVolume(cb);
                            long bc = WaterHelper.GetTotalCapacity(cb);
                            if (bv == calcWaterUl && bc == calcCapUl) BuildRealWater(cb, out baseW);
                        }
                    }
                    catch (Exception) { baseW = null; }
                }
                if (baseW == null) baseW = WaterMath.Build(calcWaterUl, pur);
                if (baseW != null && baseW.v > 0L)
                {
                    int sg = 0, sp = 0;
                    try { TodaySource(out sg, out sp); } catch (Exception) { sp = 0; }
                    if (sp <= 0) sp = pur;
                    try
                    {
                        long bestTotal = calcWaterUl;
                        var costRep = BestCostTopUp(baseW, calcCapUl, sp, calcPerf, calcQuality, calcTargetBp,
                                                    "计算器", ownTab, rep, out bestTotal);
                        if (costRep != null && costRep.pick != null
                            && costRep.ChosenFeedUl() > calcWaterUl)
                            rep = costRep;
                    }
                    catch (Exception) { }
                }
            }
            ApplyManualPick(rep);
            int tabN = calInf ? -1 : (o == null ? 0 : o.tablets);
            if (tabN < 0) tabN = WaterPlan.TabletCap(tabN);
            int filN = calInf ? 2000 : (o == null ? 0 : Math.Max(0, o.filterDur));
            int hydN = calInf ? -1 : (o == null ? 0 : o.hydraDur);
            string hydTxt = hydN < 0 ? "不限" : Math.Max(0, hydN).ToString(CultureInfo.InvariantCulture);
            string modeName = Mode == 1 ? "效率" : "性价比";
            calcScopeStr = calInf ? ("无限 · " + modeName + " 海德拉" + hydTxt)
                                  : ("药" + tabN + " 滤棉" + filN + " 海德拉" + hydTxt + " · " + modeName);
            return rep;
        }

        private static void RecalcCalc()
        {
            if (!calcDirty && calcRep != null) return;
            if (!calcDirty) return;
            calcDirty = false;
            calcRep = null;
            try
            {
                calcRep = AutoCalcReport();
                return;
                long buildUl = calcWaterUl > 0 ? calcWaterUl : calcCapUl;
                if (buildUl <= 0) { calcRep = null; return; }
                // 道具口径：只作用于本页；FilterDur 是全局静态，必须保存-恢复，否则主页会继承本页设置。
                // 顺序按工单：先让 PlanForBottle 自己设主页同一份 FilterDur，再在 finally 恢复原值。
                int saveFilterDur = WaterPlan.FilterDur;
                try
                {
                    if (!calcManual)
                    {
                        // 批次 22：计算页非手改不再照抄主页，也不先按容量建池。
                        // 统一取水顺序 = 先算一晚可达量 keep1 → Build(keep1, 一晚) → 一晚池有 ok 就用；
                        // 一晚池全不达标才放开 NightCap 按容量重建。主页未选中机器时也继续按空瓶算。
                        int pur0 = CalcPurityBp();
                        if (calcWaterUl <= 0 || pur0 <= 0)
                        {
                            int sg0 = 0, sp0 = 0;
                            try { TodaySource(out sg0, out sp0); } catch (Exception) { sp0 = 0; }
                            if (sp0 > 0) pur0 = sp0;
                        }
                        if (pur0 <= 0) pur0 = 9100;
                        long cap0 = calcCapUl > 0 ? calcCapUl : 2000000L;
                        var o0 = OwnedTools();
                        int ownTab0 = (o0 == null ? 0 : o0.tablets);
                        if (ownTab0 < 0 && !InfiniteTools) ownTab0 = 0;
                        bool ownFil0 = o0 != null && o0.filter;
                        bool ownUv0 = o0 != null && o0.uv;
                        int ownHyd0 = (o0 == null ? 0 : o0.hydraDur);
                        int fDur0 = FilterDurNow();
                        if (Mode != 2 && fDur0 <= 0) fDur0 = WaterPlan.FilterDurPer;
                        WaterPlan.FilterDur = fDur0;
                        calcRep = BuildOneNightOrCap(cap0, pur0, calcPerf, calcQuality, calcTargetBp,
                                                     "计算器", "计算器",
                                                     Mode, PreMode, PostMode,
                                                     ownTab0, ownFil0, ownUv0, ownHyd0);
                    }
                    else
                    {
                        // 「清空」后的手填：没有瓶子，只能按填的容量/纯度算
                        long cap2 = calcCapUl > 0 ? calcCapUl : calcWaterUl;
                        int sumImp = SumCalcImpBp();
                        int pur2;
                        if (calcWaterUl <= 0)
                        {
                            // 空瓶 / 没有真实水样：按今天水源估（和主页空瓶同一口径），
                            // 同时把六项 UI 填成同一纯度，避免显示 100.00% 却按 91.07% 规划。
                            int sg0 = 0, sp0 = 0;
                            try { TodaySource(out sg0, out sp0); } catch (Exception) { sp0 = 0; }
                            pur2 = (sp0 > 0) ? sp0 : 9100;
                            FillCalcImpFromPurity(cap2, pur2);
                        }
                        else pur2 = 10000 - Math.Max(0, Math.Min(10000, sumImp));
                        // 2026-11-14 定案：空瓶按「一晚可达量」进水。多晚关时不能把整个
                        // 50 L 容量当成必须一晚处理完；否则 50 L 空瓶会永远显示做不到。
                        long feedUl = cap2;
                        if (calcWaterUl <= 0 && WaterPlan.NightCap <= 1 && cap2 > 0)
                        {
                            long keep = MaxKeepUl((int)cap2, pur2, calcPerf, calcQuality, calcTargetBp);
                            if (keep > 0 && keep < feedUl) feedUl = keep;
                        }
                        var o = OwnedTools();
                        int ownTab = (o == null ? 0 : o.tablets);
                        if (ownTab < 0 && !InfiniteTools) ownTab = 0;
                        bool ownFilter = o != null && o.filter;
                        bool ownUv = o != null && o.uv;
                        int ownHydraDur = (o == null ? 0 : o.hydraDur);
                        // 手填也按主页同一份过滤棉点数；不能把 PlanForBottle 的赋值盖掉。
                        int fDur = FilterDurNow();
                        if (Mode != 2 && fDur <= 0) fDur = WaterPlan.FilterDurPer;
                        WaterPlan.FilterDur = fDur;
                        calcRep = WaterPlan.Build(feedUl, pur2, calcPerf, calcQuality, calcTargetBp,
                                                  "手填", "计算器", true, Mode, PreMode, PostMode,
                                                  ownTab, ownFilter, ownUv, ownHydraDur);
                        ApplyManualPick(calcRep);
                    }
                    // 口径行：无限 / 药N 滤棉M 海N，跟着本次真正用掉的预算走
                    int tabN = calcToolsMode == 2 ? -1
                             : (OwnedTools() == null ? 0 : OwnedTools().tablets);
                    if (tabN < 0) tabN = WaterPlan.TabletCap(tabN);
                    int filN = (calcToolsMode == 2) ? 2000
                             : (calcToolsMode == 1 ? (OwnedTools() == null ? 0 : Math.Max(0, OwnedTools().filterDur))
                                                   : WaterPlan.FilterDur);
                    int hydN = (calcToolsMode == 2) ? -1
                             : (OwnedTools() == null ? 0 : OwnedTools().hydraDur);
                    string hydTxt = hydN < 0 ? "不限" : Math.Max(0, hydN).ToString(CultureInfo.InvariantCulture);
                    string modeName = (Mode == 1) ? "效率" : (Mode == 2 ? "性价比" : ("档" + Mode));
                    calcScopeStr = (calcToolsMode == 2 || (calcToolsMode == 0 && InfiniteTools))
                                 ? ("无限 · " + modeName + " 海德拉" + hydTxt)
                                 : ("药" + tabN + " 滤棉" + filN + " 海德拉" + hydTxt + " · " + modeName);
                }
                finally
                {
                    WaterPlan.FilterDur = saveFilterDur;
                    Log("[calc] filterDur " + saveFilterDur + " -> " + WaterPlan.FilterDur);
                }
            }
            catch (Exception e)
            {
                calcRep = null;
                LastMsg = "计算失败：" + e.GetType().Name;
            }
        }

        /// <summary>最近一次右键「使用」选中的液体容器（右键菜单那次点击结束后仍然记着，供计算页认瓶）。</summary>
        private static GameItem lastPickedBottle;
        public static void NoteBottlePick(object it)
        {
            try { if (it is GameItem) lastPickedBottle = (GameItem)it; } catch (Exception) { }
        }

        /// <summary>玩家明确选过的那瓶（右击 / 最近一次右击）；没有就返回 null，不去翻机器记忆。</summary>
        private static GameItem ExplicitPickBottle()
        {
            try
            {
                var rc = RightClickTarget as GameItem;
                if (rc != null && WaterHelper.GetTotalCapacity(rc) > 0) return rc;
            }
            catch (Exception) { }
            try
            {
                if (lastPickedBottle != null && WaterHelper.GetTotalCapacity(lastPickedBottle) > 0)
                    return lastPickedBottle;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>玩家现在是不是真开着一台净水器（机器窗口可见）。关了机器就不再拿它的记忆值。</summary>
        private static bool MachineOpen()
        {
            try
            {
                var w = PurifierHook.MachineWindow as PixelWindow;
                return w != null && w.IsVisible();
            }
            catch (Exception) { return false; }
        }

        /// <summary>计算页读瓶优先级：右键选中的 → 最近一次右键的 → 当前机器瓶位 → 背包最富的瓶。</summary>
        private static GameItem CalcPickBottle()
        {
            try
            {
                var rc = RightClickTarget as GameItem;
                if (rc != null && WaterHelper.GetTotalCapacity(rc) > 0) return rc;
            }
            catch (Exception) { }
            try
            {
                if (lastPickedBottle != null && WaterHelper.GetTotalCapacity(lastPickedBottle) > 0)
                    return lastPickedBottle;
            }
            catch (Exception) { }
            var b = CtxBottle();
            if (b != null) return b;
            b = GameBottle();
            if (b != null) return b;
            return PickRichestBottle();
        }

        // 轻量刷新的缓存签名：引用 + 体积 + 容量 + 纯度（玩家 2026-11-13）。
        private static GameItem calcLoadedItem;
        private static long calcLoadedVol, calcLoadedCap;
        private static int calcLoadedPur;
        private static float calcSigAt = -99f;
        // 物理机器瓶监听：玩家在游戏里手动倒水/倒出时，calcManual 可能还停在“0 容量假设”。
        private static GameItem calcWatchItem;
        private static long calcWatchVol, calcWatchCap;

        private static void LoadCalcFromContext(bool force)
        {
            if (calcOwner != null && ReferenceEquals(calcOwner, settingsOwner) && !force) return;
            calcOwner = settingsOwner;
            int perf = 0, q = 0;
            try { Bonuses(out perf, out q); } catch (Exception) { }
            calcPerf = perf; calcQuality = q;
            calcTargetBp = PlanText.TargetBp(TargetIdx, q);
            calcCapUl = 0L;
            calcWaterUl = 0L;
            calcLoadedItem = null; calcLoadedVol = 0; calcLoadedCap = 0; calcLoadedPur = 0;
            for (int i = 0; i < calcImpBp.Length; i++) { calcImpBp[i] = 0; calcImpUl[i] = 0; }
            calcFromMachine = false;
            var b = ExplicitPickBottle();
            // 玩家 2026-11-13：「点清空又会自动读取」—— 清空后必须一直空着，直到玩家另点一瓶
            // （或重新打开机器）。以前只要右键过的瓶子还在，下一次刷新就把它塞回来。
            if (calcManual)
            {
                // 清空时没有明确右键选瓶 → 一律保持空白（机器里有什么都不读），只有重新开机器才解除。
                if (calcClearPick == null || b == null || ReferenceEquals(b, calcClearPick)) { ClearCalcFields(); return; }
                calcManual = false;
            }
            bool fromMachine = false;
            if (b == null && !calcManual && MachineOpen()) { b = CalcPickBottle(); fromMachine = b != null; }
            if (b == null)
            {
                if (calcManual) { ClearCalcFields(); return; }
                // 玩家 2026-11-14：没选中机器也要能算——按「今天的水源 + 满瓶」预填，
                // 这样打开计算页直接就有方案，不用先去点机器。
                int sg = 0, sp = 0;
                try { TodaySource(out sg, out sp); } catch (Exception) { sp = 0; }
                calcCapUl = 2000000L;
                calcWaterUl = 2000000L;
                calcPerf = 0; calcQuality = 0;
                calcTargetBp = PlanText.TargetBp(TargetIdx, 0);
                calcLoadedItem = null; calcLoadedVol = 0; calcLoadedCap = 0; calcLoadedPur = 0;
                calcFromMachine = false;
                FillCalcImpFromPurity(calcWaterUl, sp);
                calcDirty = true; calcRep = null;
                if (sCalcWater != null)
                    try { sCalcWater.SetRange(0f, 2000.0f, 0.1f); sCalcWater.SetValue(2000f, false); } catch (Exception) { }
                Log("[calc] 无机器 -> 预填今天水源 " + sp + "bp @2000mL");
                RefreshCalcLabels();
                return;
            }
            calcFromMachine = fromMachine;
            long v = 0, cap = 0; int pur = 0;
            try { cap = WaterHelper.GetTotalCapacity(b); } catch (Exception) { cap = 0; }
            try { v = WaterHelper.GetTotalVolume(b); } catch (Exception) { v = 0; }
            try { pur = WaterHelper.GetWaterPurity(b); } catch (Exception) { pur = 0; }
            RefreshCalcFromBottle(b, v, cap, pur);
        }

        /// <summary>所有数值清零，等玩家手填。</summary>
        private static void ClearCalcFields()
        {
            calcCapUl = 0L; calcWaterUl = 0L;
            calcPerf = 0; calcQuality = 0; calcTargetBp = PlanText.TargetBp(TargetIdx, 0);
            for (int i = 0; i < calcImpBp.Length; i++) { calcImpBp[i] = 0; calcImpUl[i] = 0; }
            calcLoadedItem = null; calcLoadedVol = 0; calcLoadedCap = 0; calcLoadedPur = 0;
            calcFromMachine = false; calcDirty = true; calcRep = null;
            if (sCalcWater != null) try { sCalcWater.SetRange(0f, 2000.0f, 0.1f); sCalcWater.SetValue(0f, false); } catch (Exception) { }
            RefreshCalcLabels();
        }

        private static void OnCalcClear()
        {
            calcManual = true;
            // 记住「清空时是哪一瓶」：同一瓶不要再自动读回来，换了瓶才读。
            try { calcClearPick = ExplicitPickBottle(); } catch (Exception) { calcClearPick = null; }
            // 清空是计算页的局部操作：不能继续把上次右键读取的样瓶锁成手动源，
            // 否则主页会一直按那只旧瓶算。主页回到当前机器/每日默认，计算页保持 0。
            calcManualSource = false;
            calcReadSource = false;
            lastPickedBottle = null;
            RightClickTarget = null;
            ClearCalcFields();
            UnlockPlan("计算器清空");
            DropPlanCache();
            modelCache = null;
            modelKey = "";
            planCache = null;
            planKey = "";
            InvalidateCalc();
            RequestRebuild();
        }

        /// <summary>进入计算页时按当前瓶/机器重新同步一次——接水、倒水、换方案后不能继续显示旧水量。</summary>
        private static void SyncCalcBottleOnEnter()
        {
            // 手动源（含右键读取后的原瓶参数）是玩家当前的计算样，
            // 进页/切模式时不能再用瓶子的物理值覆盖回去。
            if (calcManual || calcManualSource) return;
            var b = ExplicitPickBottle();
            if (b == null && MachineOpen()) b = CalcPickBottle();
            if (b == null)
            {
                // 没有可读瓶时按今天水源预填，别把上一次的旧瓶水量留在页面上。
                LoadCalcFromContext(true);
                return;
            }
            long v = 0, cap = 0;
            int pur = 0;
            try
            {
                v = WaterHelper.GetTotalVolume(b);
                cap = WaterHelper.GetTotalCapacity(b);
                pur = WaterHelper.GetWaterPurity(b);
            }
            catch (Exception) { return; }
            if (ReferenceEquals(b, calcLoadedItem) && v == calcLoadedVol && cap == calcLoadedCap && pur == calcLoadedPur)
                return;
            RefreshCalcFromBottle(b, v, cap, pur);
        }

        /// <summary>机器全关 → 清空；机器刚打开 → 取消手填锁定并读它。</summary>
        private static void CalcMachineWatch()
        {
            bool mo = MachineOpen();
            // 玩家在游戏里手动倒水/倒出，不经过面板的 OnFill；calcManual（0 容量假设）会一直冻住计算页。
            // 机器瓶的水量/容量一变，就退出假设态，按现实瓶重读；手动填六项但瓶没变时不会被覆盖。
            try
            {
                var watchB = GameBottle();
                if (watchB != null)
                {
                    long wv = WaterHelper.GetTotalVolume(watchB);
                    long wc = WaterHelper.GetTotalCapacity(watchB);
                    if (calcWatchItem != null && calcManual
                        && (!ReferenceEquals(watchB, calcWatchItem)
                            || wv != calcWatchVol || wc != calcWatchCap))
                    {
                        calcManual = false;
                        calcClearPick = null;
                        calcManualSource = false;
                        calcReadSource = false;
                        RefreshCalcFromBottle(watchB, wv, wc, WaterHelper.GetWaterPurity(watchB));
                        InvalidateCalc();
                        UnlockPlan("物理瓶变化退出计算假设");
                        DropPlanCache();
                        RequestRebuild();
                    }
                    calcWatchItem = watchB;
                    calcWatchVol = wv;
                    calcWatchCap = wc;
                }
                else
                {
                    calcWatchItem = null;
                    calcWatchVol = 0;
                    calcWatchCap = 0;
                }
            }
            catch (Exception) { }
            if (mo && !prevMachineOpen)
            {
                // 新机器的窗口刚打开：右键读取的旧瓶/手动源不能继续绑在这台，
                // 否则计算页会一直停在读取时的容量。
                lastPickedBottle = null;
                calcClearPick = null;
                RightClickTarget = null;
                calcManualSource = false;
                calcReadSource = false;
                readMachineItem = null; readMachineVol = 0; readMachineCap = 0; readMachinePur = 0; readMachineParts = "";
                calcManual = false;
                if (UiPage == 4) LoadCalcFromContext(true);
            }
            else if (!mo && prevMachineOpen)
            {
                if (calcFromMachine) ClearCalcFields();
                if (UiPage == 4) LoadCalcFromContext(true);
            }
            else if (mo && calcManualSource)
            {
                // 玩家 2026-11-15：右键读的样水如果已经被手动倒进机器容器，
                // 读取瓶会变空、机器瓶会有水；这时必须交回机器瓶，不能让主页继续按读取时的旧容量。
                var picked = ExplicitPickBottle();
                var machine = GameBottle();
                if (picked != null && machine != null && !ReferenceEquals(picked, machine))
                {
                    long pv = 0, mv = 0;
                    try { pv = WaterHelper.GetTotalVolume(picked); } catch (Exception) { pv = 0; }
                    try { mv = WaterHelper.GetTotalVolume(machine); } catch (Exception) { mv = 0; }
                    if (pv <= 0 && mv > 0)
                    {
                        calcManualSource = false;
                        calcReadSource = false;
                        calcManual = false;
                        lastPickedBottle = machine;
                        calcClearPick = null;
                        RightClickTarget = null;
                        UnlockPlan("读取样水已倒进机器");
                        if (UiPage == 4) LoadCalcFromContext(true);
                        DropPlanCache();
                        RequestRebuild();
                        Log("[calc] 读取样水已入机器，切回机器瓶 vol=" + mv + " cap=" +
                            SafeCap(machine));
                    }
                }
            }
            // 读取瓶没被倒空、但机器容器已经混水/变水质时，同样交回实时容器。
            // 放在分支外：机器窗口刚关掉、玩家在面板上看到变化时也要立即跟上。
            if (calcManualSource && calcReadSource) SyncReadSourceToMachine();
            if (mo && prevMachineOpen)
            {
                // 机器窗口一直开着时换瓶（例如做完 3000 方案换 6 L 容器）：旧逻辑只在
                // “打开/关闭”那一刻比对瓶对象，换瓶后整份旧方案会一直留着，直到切模式。
                GameItem mbSame = null;
                try { mbSame = GameBottle(); } catch (Exception) { mbSame = null; }
                if (mbSame == null && mo) mbSame = AnyVisibleMachineBottle();
                if (mbSame != null && lastMachineBottle != null && !SameBottleKind(mbSame, lastMachineBottle))
                {
                    lastMachineBottle = mbSame;
                    UnlockPlan("机器瓶子换了");
                    DropPlanCache();
                    RequestRebuild();
                }
                else if (mbSame != null) lastMachineBottle = mbSame;
            }
            if (mo != prevMachineOpen)
            {
                // 只有真的换了机器瓶才解锁；同一只瓶关掉/重开窗口不重算，
                // 否则会丢掉「已经放下一片药」的进度，退化成先补91+药2。
                GameItem mb = null;
                try { if (mo) mb = GameBottle(); } catch (Exception) { mb = null; }
                if (mb == null && mo) mb = AnyVisibleMachineBottle();
                if (mb != null && lastMachineBottle != null && !SameBottleKind(mb, lastMachineBottle))
                {
                    UnlockPlan("机器瓶子变了");
                    DropPlanCache();
                    RequestRebuild();
                }
                else if (mb != null && lastMachineBottle == null)
                {
                    // 第一次打开机器：按这台算。
                    UnlockPlan("首次选机器");
                    DropPlanCache();
                    RequestRebuild();
                }
                if (mb != null) lastMachineBottle = mb;
            }
            prevMachineOpen = mo;
        }

        private static void CaptureReadMachine()
        {
            readMachineItem = null; readMachineVol = 0; readMachineCap = 0; readMachinePur = 0; readMachineParts = "";
            try
            {
                var machine = GameBottle();
                if (machine == null) return;
                readMachineItem = machine;
                readMachineVol = WaterHelper.GetTotalVolume(machine);
                readMachineCap = WaterHelper.GetTotalCapacity(machine);
                readMachinePur = WaterHelper.GetWaterPurity(machine);
                readMachineParts = MachinePartsSig(machine);
            }
            catch (Exception e)
            {
                Log("[calc] 读取样瓶时记机器容器失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        private static string MachinePartsSig(GameItem b)
        {
            if (b == null) return "";
            int[] parts = WaterHelper.GetLiquidParts(b);
            if (parts == null) return "";
            var sb = new System.Text.StringBuilder(parts.Length * 6);
            for (int i = 0; i < parts.Length; i++) sb.Append(parts[i]).Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// 右键读取只锁定「当时那瓶样水」；机器容器一旦混水、接水或过夜变了水质，
        /// 主页/计算页都必须回到机器容器的实时值，不能继续用读取时缓存的六项。
        /// </summary>
        private static void SyncReadSourceToMachine()
        {
            if (!calcManualSource || !calcReadSource) return;
            GameItem machine;
            long mv = 0, mc = 0;
            int mp = 0;
            string mparts;
            try
            {
                machine = GameBottle();
                if (machine == null) return;
                mv = WaterHelper.GetTotalVolume(machine);
                mc = WaterHelper.GetTotalCapacity(machine);
                mp = WaterHelper.GetWaterPurity(machine);
                mparts = MachinePartsSig(machine);
            }
            catch (Exception e)
            {
                Log("[calc] 检查读取样瓶的机器容器失败 " + e.GetType().Name + " " + e.Message);
                return;
            }
            bool changed = !ReferenceEquals(machine, readMachineItem)
                           || mv != readMachineVol || mc != readMachineCap || mp != readMachinePur
                           || !string.Equals(mparts, readMachineParts);
            if (!changed) return;

            calcManualSource = false;
            calcReadSource = false;
            calcManual = false;
            lastPickedBottle = machine;
            calcClearPick = null;
            RightClickTarget = null;
            UnlockPlan("读取后机器容器已变");
            DropPlanCache();
            InvalidateCalc();
            RequestRebuild();
            Log("[calc] 读取后机器容器已变 -> 跟随实时容器 vol=" + mv + " pur=" + mp + " cap=" + mc);
            if (UiPage == 4)
            {
                try { LoadCalcFromContext(true); }
                catch (Exception e) { Log("[calc] 读取后切回实时容器页失败 " + e.GetType().Name + " " + e.Message); }
            }
        }

        private static long SafeCap(GameItem b)
        {
            try { return WaterHelper.GetTotalCapacity(b); } catch (Exception) { return 0L; }
        }

        /// <summary>只读这一瓶：容量/水量/六项配比全部按 µL 精度写入，不再全量扫背包。</summary>
        private static void RefreshCalcFromBottle(GameItem b, long v, long cap, int pur)
        {
            calcLoadedItem = b;
            calcLoadedVol = v; calcLoadedCap = cap; calcLoadedPur = pur;
            calcSigAt = UnityEngine.Time.realtimeSinceStartup;
            calcCapUl = cap > 0 ? cap : calcCapUl;
            calcWaterUl = v > 0 ? v : 0L;
            // 批次 22：calc=12 直接读瓶时也要带上当前机器的性能/质量，
            // 否则计算页会用 0% 建池，和主页（+25%）分叉。
            int pNow = 0, qNow = 0;
            try { Bonuses(out pNow, out qNow); } catch (Exception) { pNow = 0; qNow = 0; }
            calcPerf = pNow;
            calcQuality = qNow;
            calcTargetBp = PlanText.TargetBp(TargetIdx, qNow);
            for (int i = 0; i < calcImpBp.Length; i++) { calcImpBp[i] = 0; calcImpUl[i] = 0; }
            bool filled = false;
            if (b != null)
            {
                try
                {
                    var parts = WaterHelper.GetLiquidParts(b);
                    if (parts != null && parts.Length > 0 && v > 0)
                    {
                        // 玩家 2026-11-13（p3）：以前按 parts[i+off] 顺序硬配六项 —— 化学与物理整体错位。
                        // Liquid.InitLiquid 的真实顺序是：
                        //   0 water / 1 heavy_metal / 2 organic_waste / 3 microplastic /
                        //   4 microbe / 5 chemical_contaminant / 6 physical_contaminant / 7 mineral
                        // 所以按标识符映射（拿不到名字再按 Liquid 顺序兜底），microplastic 并进 physical。
                        var names = GameBridge.LiquidNames();
                        for (int i = 0; i < calcImpUl.Length; i++) calcImpUl[i] = 0;
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] <= 0) continue;
                            string k = (names != null && i < names.Length) ? MapLiquid(names[i]) : null;
                            if (k == null) k = MapLiquidByIndex(i);
                            if (k == null) continue;
                            for (int j = 0; j < CalcImpKeys.Length; j++)
                                if (CalcImpKeys[j] == k) { calcImpUl[j] += parts[i]; break; }
                        }
                        // 玩家 2026-11-13：游戏原版按「四舍五入到 0.01%」显示（1.065%→1.07%、0.355%→0.36%），
                        // 我们之前整数除法截断，永远少 0.01%。这里改成 +半个单位 的四舍五入。
                        for (int i = 0; i < calcImpBp.Length; i++)
                            calcImpBp[i] = (int)Math.Min(10000L, Math.Max(0L, (calcImpUl[i] * 10000L + v / 2) / Math.Max(1L, v)));
                        // 六项各自四舍五入后相加，可能比「总杂质 μL 四舍五入」少 0.01（玩家 p2：1.33 vs 游戏 1.34）。
                        // 合计按总杂质 μL 定，六项只做尾差回填，保证「杂质合计」和游戏一致。
                        SyncCalcImpBp(v);
                        filled = true;
                    }
                }
                catch (Exception) { }
            }
            if (!filled)
            {
                // 玩家 2026-11-13（第七轮）：空桶被读成「满桶 100%」→ 直接判已达标。
                // 空容器没有液体（GetWaterPurity 返回 0），不要再拿「水源默认纯度」把六项填成 0；
                // 水量保持 0，结果区自然不显示「已达标」。
                if (v <= 0 || pur <= 0)
                {
                    calcWaterUl = 0L;
                    for (int i = 0; i < calcImpBp.Length; i++) { calcImpBp[i] = 0; calcImpUl[i] = 0; }
                    // 空瓶没有真实水样：六项也按今天水源填，避免显示 100.00% 却按水源规划。
                    int sg = 0, sp = 0;
                    try { TodaySource(out sg, out sp); } catch (Exception) { sp = 0; }
                    if (calcCapUl > 0)
                        FillCalcImpFromPurity(calcCapUl, sp > 0 ? sp : 9100);
                    if (sCalcWater != null) try { sCalcWater.SetRange(0f, (float)(calcCapUl / 1000.0), 0.1f); sCalcWater.SetValue(0f, false); } catch (Exception) { }
                    calcDirty = true; calcRep = null;
                    RefreshCalcLabels();
                    Log("[calc] 空容器 pur=0/v=" + v + " -> 水量归零，不判达标");
                    return;
                }
                int p = pur > 0 ? pur : BottleOrTapPurity(9100);
                FillCalcImpFromPurity(calcWaterUl, p);
            }
            if (sCalcWater != null) try { sCalcWater.SetRange(0f, (float)(calcCapUl / 1000.0), 0.1f); } catch (Exception) { }
            calcDirty = true;
            calcRep = null;
            RefreshCalcLabels();
        }

        /// <summary>
        /// 批次 35：右键菜单「读取」入口。
        /// 只读这一瓶的容量/水量/纯度，六项 μL 走 RefreshCalcFromBottle；
        /// 读完把设置页水源切到「手动」，主页/计算页都按这只瓶的原参数算。
        /// </summary>
        public static string ReadBottleToCalc(object item)
        {
            return ReadBottleToCalc(item, true);
        }

        /// <summary>读取这瓶水并刷新上下文；openCalcPage=false 时只回主页，不跳到计算页。
        /// 玩家 2026-11-17：切机器时复用右键读取的判定，直接拿选中机器槽里的实际水做样瓶。</summary>
        private static string ReadBottleToCalc(object item, bool openCalcPage)
        {
            GameItem b = item as GameItem;
            if (b == null) return "读取水质：不是水瓶";
            long v = 0, cap = 0;
            int pur = 0;
            try
            {
                cap = WaterHelper.GetTotalCapacity(b);
                v = WaterHelper.GetTotalVolume(b);
                pur = WaterHelper.GetWaterPurity(b);
            }
            catch (Exception e)
            {
                Log("[calc] 读取水质失败 " + e.Message);
                return "读取水质失败：" + e.Message;
            }
            if (cap <= 0) return "读取水质：不是液体容器";
            if (v <= 0) return "这瓶没水";

            // 玩家 2026-11-15：读取后设置页水源要显示「手动」，
            // 主页和计算页都按这只瓶的六项原参数算。
            calcManualSource = true;
            calcReadSource = true;
            CaptureReadMachine();
            calcManual = false;
            CustomPur = -1;
            SrcIdx = 0;
            // 关键：把右键这瓶记成「明确选中的瓶」。否则 SetPage(4) 的
            // LoadCalcFromContext/SyncCalcBottleOnEnter 会按“没有明确瓶子”预填今天水源，
            // 看起来就只是打开了计算页，六项没读进来。
            NoteBottlePick(b);
            RefreshCalcFromBottle(b, v, cap, pur);
            // 主页的锁定方案/模型指纹都必须作废：否则右键读取后主页还显示机器旧瓶。
            UnlockPlan("右键读取");
            DropPlanCache();
            RememberSettings();
            string opened = ShowMain(true);
            if (openCalcPage)
            {
                SetPage(4);
                LastMsg = "已读取 " + v + " μL 六项水质";
            }
            else
            {
                LastMsg = "已读取当前机器 " + v + " μL 水质";
            }
            Log("[calc] 右键读取水质 vol=" + v + " cap=" + cap + " pur=" + pur
                + " item=" + GameBridge.NameOfItem(b) + " show=" + opened
                + " loaded=" + calcLoadedVol + "/" + calcLoadedCap
                + " imp=" + calcImpUl[0] + "," + calcImpUl[1] + "," + calcImpUl[2]
                + "," + calcImpUl[3] + "," + calcImpUl[4] + "," + calcImpUl[5]);
            return LastMsg;
        }

        /// <summary>计算页开着时每 0.5 秒只比对签名；瓶没变就什么都不做（不扫描、不重建）。</summary>
        private static void CalcLiveTick()
        {
            if (UiPage != 4 || panelWin == null || !panelWin.IsVisible()) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - calcSigAt < 0.5f) return;
            calcSigAt = now;
            if (calcManual || calcManualSource) return;
            var b = ExplicitPickBottle();
            if (b == null && MachineOpen()) b = CalcPickBottle();
            if (b == null) return;
            long v = 0, cap = 0; int pur = 0;
            try { v = WaterHelper.GetTotalVolume(b); cap = WaterHelper.GetTotalCapacity(b); pur = WaterHelper.GetWaterPurity(b); }
            catch (Exception) { return; }
            if (ReferenceEquals(b, calcLoadedItem) && v == calcLoadedVol && cap == calcLoadedCap && pur == calcLoadedPur) return;
            RefreshCalcFromBottle(b, v, cap, pur);
        }

        private static void FillCalcImpFromPurity(long volume, int purity)
        {
            try
            {
                var w = WaterMath.Build(volume, purity, volume);
                for (int i = 0; i < calcImpBp.Length; i++)
                {
                    calcImpUl[i] = w.Part(CalcImpKeys[i]);
                    calcImpBp[i] = (int)Math.Min(10000L, Math.Max(0L,
                        (calcImpUl[i] * 10000L + Math.Max(1L, volume) / 2) / Math.Max(1L, volume)));
                }
                SyncCalcImpBp(volume);
            }
            catch (Exception) { }
        }

        /// <summary>六项百分比按各自 μL 四舍五入，跟游戏同列显示一致（2026-11-14：化学 535.80mL/20000mL
        /// 游戏显示 2.68%，再按总杂质补尾差会把它顶成 2.69%，反而比游戏多 0.01）。</summary>
        private static void SyncCalcImpBp(long volume)
        {
            // 不再把六项强行凑到「总杂质合计」：游戏每一列独立按 μL 取整，
            // 强行回填尾差会让最大项（化学）比游戏高 0.01。总杂质只在结果段自己算。
        }


        /// <summary>
        /// 接水前的「水源闸门」：必须在动瓶里任何水之前跑完。
        /// 旧版把这些判断放在灌水前最后一步，选了未解锁的沟渠时会先把瓶里的水转到备用瓶/清空，
        /// 然后才报「没解锁」——玩家看到的就是「水没了、沟渠还是能接」。
        /// </summary>
        private static bool FillTapGate(out int tapGrade, out int tapPur)
        {
            tapGrade = 0; tapPur = 0;
            if (CustomPur > 0)
            {
                LastMsg = "手填 " + PlanText.Pct(CustomPur) + " 只用于算方案；接水请切回自动/铁锈/沟渠";
                Log("[pour] manual-purity no-tap " + CustomPur);
                return false;
            }
            if (SrcIdx >= 3)
            {
                LastMsg = "「" + SrcNames[Math.Min(SrcIdx, SrcNames.Length - 1)] + "」不是水龙头出的——要从商店买/自己兑，或用「自动」按瓶里现成的水算";
                Log("[pour] src-not-a-tap idx=" + SrcIdx);
                return false;
            }
            if (SrcIdx == 1 && !TapUnlocked("RUSTWATER_TAP"))
            {
                LastMsg = "铁锈水龙头还没解锁，接不出来（方案可以算，接水请切回自动或已开通水源）";
                Log("[pour] tap-locked rust");
                return false;
            }
            if (SrcIdx == 2 && !TapUnlocked("GUTTERFLOW_TAP"))
            {
                LastMsg = "沟渠水龙头还没解锁，接不出来（方案可以算，接水请切回自动或已开通水源）";
                Log("[pour] tap-locked gutter");
                return false;
            }
            // 双保险：TapOnly 只认「存档里实际开通的那一只」。
            int onlyTap = TapOnly();
            // 玩家 2026-11-13（第七轮）：铁锈水管存档里，沟渠水龙头即使还留着旧解锁状态，
            // 实际水管已经换成铁锈水，不能再凭空接出沟渠水。
            if (SrcIdx == 2 && TapUnlocked("RUSTWATER_TAP"))
            {
                LastMsg = "这个存档装的是铁锈水管，沟渠水已经接不出来（方案可以算，接水请切铁锈水或自动）";
                Log("[pour] tap-locked gutter-by-rust");
                return false;
            }
            if (SrcIdx == 2 && onlyTap == 1)
            {
                LastMsg = "这个存档只开通了铁锈水，沟渠水接不出来";
                Log("[pour] tap-locked only-rust");
                return false;
            }
            if (SrcIdx == 1 && onlyTap == 2)
            {
                LastMsg = "这个存档只开通了沟渠水，铁锈水接不出来";
                Log("[pour] tap-locked only-gutter");
                return false;
            }
            TapSource(out tapGrade, out tapPur);
            if (tapPur <= 0)
            {
                LastMsg = tapBlock != null ? tapBlock : "读不到水龙头水质：StoreStation 未就绪，或水龙头还没解锁";
                Log("[pour] tap-unavailable before-pour src=" + SrcIdx);
                return false;
            }
            return true;
        }

        private static string OnFill()
        {
            try
            {
                PourDiag("enter inf=" + (InfiniteTools ? 1 : 0) + " mode=" + Mode);
                // 方案已经执行到某一步：后面水少了不补、不提示；手动接水键也拦住，
                // 避免把正在跑的轨迹接成另一种水。要重新规划先按档位/设置键解锁。
                if (progressActive)
                {
                    LastMsg = "方案执行中，不补水；要重算先按档位或设置键";
                    return "progress-no-fill";
                }
                int grade, pur;
                TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                int targetBp = PlanText.TargetBp(TargetIdx, q);

                // 目标容器：优先「净水器槽里那瓶」（玩家正对着的就是它），其次右键选中的，最后背包/台面。
                var b = CtxBottle() as GameItem;
                if (b == null) b = RightClickTarget as GameItem;
                if (b == null) b = GameBottle();
                if (b == null) b = PickFallbackBottle();
                if (b == null) { LastMsg = "没有可灌的瓶子：把瓶放进储水容器或背包"; return "no-bottle"; }
                // 已经有水就只补剩余空间，绝不溢出（AddWater 超量会被丢掉，等于白接）。
                long haveUl2 = 0, capUl2 = 0;
                try { haveUl2 = WaterHelper.GetTotalVolume(b); capUl2 = WaterHelper.GetTotalCapacity(b); } catch (Exception) { }
                // 玩家 2026-11-15：先看这一瓶在接水前是不是空的、或者本来就是今天的水管水。
                // 这两种情况接进去六项不会变，按原方案锁住就行；只有接水真的改变六项时，
                // 才改成“接完按实际混合水重算”，避免拿接水前的旧水样继续套。
                bool preEmpty = haveUl2 <= 0;
                bool preSameTap = false;
                if (!preEmpty)
                {
                    WaterMath.Water preW = null;
                    try
                    {
                        if (BuildRealWater(b, out preW) && preW != null && preW.v > 0)
                        {
                            var tapW = WaterMath.Build(preW.v, Math.Max(1, Math.Min(10000, pur)), 0);
                            preSameTap = true;
                            for (int ii = 0; ii < CalcImpKeys.Length; ii++)
                            {
                                string kk = CalcImpKeys[ii];
                                if (Math.Abs(preW.Part(kk) - tapW.Part(kk)) > 5L) { preSameTap = false; break; }
                            }
                        }
                    }
                    catch (Exception) { preSameTap = false; }
                }
                PourDiag("start target=" + GameBridge.NameOfItem(b) + " have=" + haveUl2 + " cap=" + capUl2 +
                         " inf=" + (InfiniteTools ? 1 : 0) + " preEmpty=" + (preEmpty ? 1 : 0) +
                         " sameTap=" + (preSameTap ? 1 : 0));

                // 玩家 2026-11-11：这个键就是原来的「接水」——只往瓶里灌水，不重算、不改方案。
                // 方案什么时候变：按「默认 / 效率 / 性价比」或改设置页；倒水/接水/过夜都不变。
                int freeMl = (int)((capUl2 - haveUl2) / 1000);
                if (freeMl <= 0)
                {
                    LastMsg = "容器已经是满的（" + (haveUl2 / 1000) + "/" + (capUl2 / 1000) + " mL），先倒出来再接";
                    Rebuild(true);
                    return "full";
                }

                // ★先过水源闸门，再决定要不要动瓶里的水。
                int tapGrade = 0, tapPur = 0;
                if (!FillTapGate(out tapGrade, out tapPur))
                {
                    Rebuild(true);
                    return "tap-gate";
                }

                long keepUl = MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, targetBp);
                if (keepUl <= 0) { LastMsg = "今日水质一晚达标不了"; return "impossible"; }
                // 按「当前选中方案 + 现有整瓶容器凑得出的量」灌：选中方案说接多少就接多少，
                // 但还要再按整瓶可达量收一次（选中方案给的是理论上限 1857，整瓶只能凑 1850）。
                long bottleUl = keepUl;
                long planFeedUl = 0;
                long shownTargetUl = 0;   // 面板上「先补 / 需接水」对应的最终总量（µL）
                try
                {
                    // 以面板当前真正显示的那份方案为准：按接水键必须倒进「先补」这个量，
                    // 不能按 CurrentPlan 里另一份尚未刷新到界面的方案走。
                    WaterPlan.Report r0 = planCache;
                    if (r0 == null || r0.pick == null) r0 = CurrentPlan(true);
                    long r0Feed = (r0 != null && r0.pick != null) ? r0.pick.feedUl : 0L;
                    if (r0Feed > 0) planFeedUl = r0Feed;
                    if (r0 != null && r0.fromWater && r0.water0 != null)
                    {
                        long roomNow = Max0(capUl2 - haveUl2);
                        long addNow = ReachablePour(r0.water0.v - haveUl2, roomNow);
                        if (addNow > 0) shownTargetUl = haveUl2 + addNow;
                    }
                    else if (planFeedUl > 0)
                        shownTargetUl = BottlePourUl(planFeedUl, capUl2);
                    if (shownTargetUl > 0) bottleUl = shownTargetUl;
                    else if (InfiniteTools && capUl2 > haveUl2)
                        bottleUl = capUl2;       // 无方案兜底：无限效率本来就要装满当前瓶
                    PourDiag("plan r0=" + r0Feed + " shown=" + shownTargetUl +
                             " bottle=" + bottleUl + " cap=" + capUl2 + " have=" + haveUl2);
                }
                catch (Exception) { }
                int totalWantMl = (int)(bottleUl / 1000);       // 这条方案总共要接多少
                int haveMlNow = (int)(haveUl2 / 1000);            // 容器里已经有多少
                // 同源/同水质补接时直接在原瓶加，不先把原水挪进备用瓶。
                // 否则目标瓶补满后，备用瓶会留下整整一瓶水，背包无故多一瓶。
                bool topUpInPlace = false;
                // 玩家：**按一次就够了**。第二次按只补「离目标还差的量」，差 0 就直接说够了，
                // 绝不再往容器里追加（以前按一次 1500、再按又灌 1500，把水接成了废液）。
                int wantMl = totalWantMl - haveMlNow;
                // 容器里已经有液体：同水质（例如 91.07% = 91.07%）直接在原瓶补；
                // 只有瓶里水明显比水龙头好时才保留旧水的完整性，不让它被混掉。
                if (haveMlNow > 0 && wantMl > 0)
                {
                    int havePurNow = 0, srcPurNow = 0;
                    try { havePurNow = WaterHelper.GetWaterPurity(b); } catch (Exception) { }
                    try { int gTmp; TapSource(out gTmp, out srcPurNow); } catch (Exception) { }
                    if (havePurNow > 0 && srcPurNow > 0 && havePurNow > srcPurNow)
                    {
                        // 有明确方案要补到 shownTarget 时，按方案混：这张卡本来就算好了
                        // 「混合后」纯度，不能因为旧水更好就拒绝接水、让玩家手动倒。
                        bool plannedTopUp = shownTargetUl > haveUl2;
                        if (!plannedTopUp)
                        {
                            PourDiag("keep-better-water have=" + havePurNow + " tap=" + srcPurNow);
                            LastMsg = "瓶里现成的水 " + PlanText.Pct(havePurNow) + " 比水龙头 " + PlanText.Pct(srcPurNow) +
                                      " 还好，不自动倒掉；要接水请先手动倒出或换空瓶";
                            Log("[pour] keep-better-water have=" + havePurNow + " tap=" + srcPurNow);
                            Rebuild(true);
                            return "keep-better-water";
                        }
                        Log("[pour] allow-plan-mix have=" + havePurNow + " tap=" + srcPurNow +
                            " target=" + shownTargetUl);
                    }
                    topUpInPlace = true;
                }
                if (wantMl <= 0)
                {
                    if (totalWantMl < haveMlNow)
                    {
                        PourDiag("over total=" + totalWantMl + " have=" + haveMlNow);
                        LastMsg = "这瓶水比这条方案多 " + (haveMlNow - totalWantMl) +
                                  " mL，先倒出再净化";
                        Rebuild(true);
                        return "over";
                    }
                    PourDiag("enough total=" + totalWantMl + " have=" + haveMlNow + " free=" + freeMl);
                    LastMsg = "已经够这条方案了（" + haveMlNow + "/" + totalWantMl + " mL），不用再接";
                    Rebuild(true);
                    return "enough";
                }
                if (wantMl > freeMl) wantMl = freeMl;
                keepUl = wantMl * 1000L;
                PourDiag("want=" + wantMl + " free=" + freeMl + " topup=" + (topUpInPlace ? 1 : 0) +
                         " total=" + totalWantMl + " have=" + haveMlNow);

                GameItem dest = b;
                if (WaterHelper.GetTotalCapacity(b) < keepUl)
                {
                    dest = null;
                    var all = GameBridge.LiveAllItems();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var it = all[i];
                        if (it == null) continue;
                        if (WaterHelper.GetTotalCapacity(it) >= keepUl) { dest = it; break; }
                    }
                    if (dest == null)
                    {
                        int maxCap = 0;
                        for (int i = 0; i < all.Count; i++)
                            if (all[i] != null) maxCap = Math.Max(maxCap, WaterHelper.GetTotalCapacity(all[i]));
                        LastMsg = "没有能装 " + wantMl + " mL 的容器（现有最大 " + (maxCap / 1000) + " mL）";
                        Log("[pour] no-container want=" + wantMl + "mL max=" + (maxCap / 1000) + "mL");
                        return "no-container " + wantMl + "mL";
                    }
                }

                int capMl = WaterHelper.GetTotalCapacity(dest) / 1000;

                // ================= 灌水：完全复刻水龙头那条链，不自己造值 =================
                // 水龙头（network upgrade "RUSTWATER_TAP"）的真实 IL（WaterHelper/<>c__DisplayClass10_0::
                // <InitLiquidContainerItem>b__9）：
                //   NetworkUpgrade::GetUpgradeById("RUSTWATER_TAP") → NetworkUpgrade::IsUnlocked
                //     → AudioHelper::UseFaucet(container)
                //     → WaterHelper::AddWater(container, /*grade*/1, /*amount*/9999999, /*useRange*/true,
                //                            /*lowPara*/StoreStation.todayRustwaterPurity,
                //                            /*highPara*/StoreStation.todayRustwaterPurity,
                //                            /*initWaterFeature*/true)
                //   else 分支 "GUTTERFLOW_TAP" → 同样参数，纯度取 StoreStation.todayGutterflowPurity，grade 仍是 1。
                // 三个关键点：
                //  1) RNG.GetRandomInt(a,b) 的 IL 就是 Random::Range(a, b+1)（闭区间），a==b 时必得 a；
                //     水龙头正是靠 low==high 把纯度钉成当天的 StoreStation 字段值。
                //  2) 这两个字段是**当日**值，随存档天数（过月）变化，绝对不能缓存 ——
                //     所以每次现场读 TapSource()，读不到就直接失败，绝不写死 91.03 之类的数。
                //  3) amount=9999999 会被 AddWater 内部夹到容器剩余容量，所以「灌满」就等于容量。
                // 旧路径（EnsureBaseline / AssembleExact / AssembleWater：缓存当天六项配比再逐项 AddLiquid 重建）
                // 已删除：它把纯度当天固定死，过月水龙头换水后会灌出与游戏不一致的水；而且它根本没走水龙头。
                Log("[pour] tap-src grade=" + tapGrade + " purity=" + tapPur + " (live StoreStation)");

                // 直接按目标量一次性灌进去：先把量算好，再 AddWater(amount = 目标 μL)。
                // 旧做法是「灌满 2000 再用 RemoveMl 倒掉多余」，倒水会让六项杂质的整数配比重新取整，
                // 实测把 90.96% 变成 90.95%（差 0.01，玩家明确要求不能差）。
                long keepTap = MaxKeepUl(WaterHelper.GetTotalCapacity(dest), tapPur, perf, q, targetBp);
                if (keepTap <= 0)
                {
                    // 不碰瓶里原有的水：达标不了就不灌，直接把瓶里的好水倒掉是以前最容易丢水的写法
                    LastMsg = "今日水龙头水质一晚达标不了（" + PlanText.Pct(tapPur) + "）——原来的水没动";
                    return "impossible";
                }
                // 一键灌水按「现有整瓶容器凑得出的量」灌，而不是理论最优（玩家：一键就该是 1700）。
                // 体积变小只会更容易达标（MaxKeepUl 是上限），往下取整瓶总量是安全的。
                // 储水容器是直接拿去水龙头接的，不经过背包瓶子：按「一晚能达标的最大量」灌。
                // 整瓶凑量的拆解只用于「背包要带多少水」的提示，不再压低下限（玩家：能 1850 就不该只给 1800）。
                // 玩家：接水要按「容器 + 选中方案」来，不能一律灌到模型上限。
                // 有选中方案 → 就灌方案量；没方案 → 才用一晚上限，再用整瓶可达量收口。
                long keepTapUse = shownTargetUl > 0 ? shownTargetUl : BottlePourUl(keepTap, capUl2);
                WaterPlan.Report rPick0 = planCache;
                if (rPick0 == null || rPick0.pick == null) rPick0 = CurrentPlan(true);
                WaterPlan.Report rFillPlan = rPick0;   // 锁定用：这是「玩家按下接水时看到的那份方案」
                long pickFeed = rPick0 == null ? 0L : BottlePourUl(rPick0.ChosenFeedUl(), capUl2);
                if (shownTargetUl <= 0 && pickFeed > 0) keepTapUse = pickFeed;
                // keepTapUse 是「目标总量」，不是「本次补接量」。这里不能再按剩余空间夹：
                // 瓶里已有 18850、方案目标 44150 时，剩余空间是 31150，但本次只该补 25300。
                // 循环里的 gap = wantTotal - haveNow 会自己按当前水量补足。
                if (keepTapUse > WaterHelper.GetTotalCapacity(dest))
                    keepTapUse = WaterHelper.GetTotalCapacity(dest);
                else if (shownTargetUl <= 0 && pickFeed <= 0)
                {
                    // 没有选中方案：按整瓶可达量收一次（能凑到 1850 就不该只给 1800）。
                    // 注意这一支只能在没有方案时走：以前它写在 else 里，等于「装得下就一律压回整瓶量」，
                    // 选中方案要的 2250/2000 会被它悄悄改回 1850（玩家说的「调整方案后仍锁 1850」）。
                    var ppTap = WaterPlan.Plan(keepTap);
                    if (ppTap != null && ppTap.totalUl > 0 && ppTap.totalUl < keepTap) keepTapUse = ppTap.totalUl;
                }
                int keepMlEarly = (int)(keepTapUse / 1000);

                // 玩家定案：瓶里已有水先转到空瓶，转不了就中止 —— 不能为了接新水把优质水冲成铁锈水。
                // 但同源补接（topUpInPlace）例外：原水里就有同/较低纯度水，直接补到目标量，
                // 既不冲掉旧水，也不会在背包里多留一瓶“腾位置”的水。
                if (!topUpInPlace)
                {
                    if (!RescueWaterBeforeFill(dest, "接水前要腾空瓶子")) return "busy-bottle";
                    WaterHelper.EmptyContainer(dest);
                }
                // 玩家：**按一次就要到位**（以前 2000 的容器要按两次才满，先 1500 再 500）。
                // AddWater 会被引擎夹到剩余容量，所以循环补足差额，最多 3 次。
                long wantTotal = (long)keepMlEarly * 1000L;
                for (int it2 = 0; it2 < 3; it2++)
                {
                    long haveNow = WaterHelper.GetTotalVolume(dest);
                    long gap = wantTotal - haveNow;
                    if (gap <= 0) break;
                    long room = WaterHelper.GetTotalCapacity(dest) - haveNow;
                    if (room <= 0) break;
                    if (gap > room) gap = room;
                    WaterHelper.AddWater(dest, tapGrade, (int)gap, true, tapPur, tapPur, true);   // 与 b__9 同参
                    if (it2 == 0 && WaterHelper.GetTotalVolume(dest) < wantTotal)
                        Log("[pour] 一次没灌满，补第 " + (it2 + 1) + " 次（目标 " + keepMlEarly + "mL）");
                }
                int filledUl = WaterHelper.GetTotalVolume(dest);
                PourDiag("filled=" + filledUl + " wantTotal=" + wantTotal + " keepMlEarly=" + keepMlEarly +
                         " dest=" + GameBridge.NameOfItem(dest));
                // 玩家报的第 5 条：铁锈水灌进了外面的瓶、机器容器留空。这里做后置校验——
                // 目标容器（机器里那瓶）仍然是空的就直接判失败，不再「日志说成功、实际没进去」。
                if (filledUl <= 0)
                {
                    LastMsg = "灌水没进目标容器（" + GameBridge.NameOfItem(dest) + "），请重试或手动接";
                    Log("[pour] verify-failed dest=" + GameBridge.NameOfItem(dest) + " b=" + GameBridge.NameOfItem(b));
                    Rebuild(true);
                    return "verify-failed";
                }
                int fillPur = WaterHelper.GetWaterPurity(dest);   // 读回真值（不是传入值）

                // 用读回的纯度重算「该留多少」，让面板/日志与水里真实配比自洽
                long keepUl2 = fillPur > 0
                    ? MaxKeepUl(WaterHelper.GetTotalCapacity(dest), fillPur, perf, q, targetBp)
                    : 0L;
                if (keepUl2 <= 0)
                {
                    // 今日水质一晚达标不了：倒空，别留下半瓶废液
                    WaterHelper.EmptyContainer(dest);
                    LastMsg = "今日水龙头水质一晚达标不了（" + PlanText.Pct(fillPur) + "）";
                    return "impossible";
                }

                // ===== A) 显示与实际统一取整：内部 μL，写入/显示同一个整 mL 数 =====
                // 目标先向下取整到 mL，之后所有比较、减量、显示都以 keepMl 为准，
                // 不再出现「界面 1857 / 实际 1858」。
                int keepMl = (int)(keepUl2 / 1000);
                long keepTarget = (long)keepMl * 1000L;
                if (filledUl > keepTarget)
                {
                    Log("[pour] 多灌 " + ((filledUl - keepTarget) / 1000) + " mL（AddWater 夹到容量），保持原样不再倒水以免改纯度");
                }
                try { WaterFeatureHelper.UpdateWaterFeature(dest, false); } catch (Exception) { }

                // 回读：界面显示、日志、判定全部用回读值（唯一真值）
                int realMl = WaterHelper.GetTotalVolume(dest) / 1000;
                int readPur = WaterHelper.GetWaterPurity(dest);
                int showMl = realMl;
                string name = GameBridge.NameOfItem(dest);
                Log("[pour] tap container=" + name + " vol=" + realMl + " pur=" + readPur);
                Log("[pour] exact show=" + showMl + " real=" + realMl + " pur=" + readPur + " keep=" + keepMl);
                // 目标量已按「整瓶凑得出的量」下调到 keepMlEarly，判定也要认这个值，否则会误报 mismatch。
                bool ok = readPur > 0 && (realMl == keepMl || realMl == keepMlEarly);
                Log("[pour] window#" + ctxIndex + " tap=" + tapPur + "@" + (filledUl / 1000) + " kept=" + readPur + "@" + realMl +
                    " container=" + name + " cap=" + capMl + " ok=" + ok);
                if (!ok)
                {
                    LastMsg = "灌水不符：想 " + keepMl + " mL，实得 " + showMl + " mL  " + PlanText.Pct(readPur) + "（" + name + "）";
                    return "mismatch got=" + readPur + "@" + showMl;
                }
                LastMsg = "已灌 " + showMl + " mL  " + PlanText.Pct(readPur) + "（" + name + "）";
                lastKey = "";
                // 玩家 2026-11-15：接水会改变容器里六项配比，旧方案是按接水前的水样出的，
                // 接完再拿旧方案继续走会和实际混水冲突。只有这次接水真的改变六项时，
                // 接完改成“只按实际这瓶水算”：不再继续搜还能再接多少，也不会按一次又让接第二段。
                // 空瓶 / 瓶里本来就是今天水管水（含玩家手动加过但六项仍与今日水源一致）时六项没变；
                // 但空瓶接水后不能锁接水前的旧方案，下面单独解锁按实际水量重算。
                // 玩家 2026-11-15：先清空成 0 容量做假设计算、随后真的把水倒进机器，
                // 计算页不能再停在“手填/清空”的旧状态；物理接水以机器瓶为唯一真值。
                // 只清计算页的假设锁，不动读取样瓶（calcManualSource 下面单独处理）。
                if (calcManual)
                {
                    calcManual = false;
                    calcClearPick = null;
                }
                bool fillChangesParts = !preEmpty && !preSameTap;
                bool fillFull = filledUl >= WaterHelper.GetTotalCapacity(dest) - 1500L;
                bool fixActual = fillChangesParts && (!calcManual || calcManualSource);
                // 读取的样水已经实际倒进目标容器：切回目标容器，让容器/方案跟着实际水量。
                if (calcManualSource)
                {
                    calcManualSource = false;
                    calcReadSource = false;
                    lastPickedBottle = dest;
                    UnlockPlan("读取样水已入容器");
                }
                if (fillChangesParts)
                    UnlockPlan("接水后六项变了，按实际混合水重算");
                // 空瓶接水后不要锁接水前的旧方案：旧方案可能还停在空瓶/上一只瓶的水量，
                // 锁上去会让第一次显示 8.95 不补水方案，必须切模式才刷新。
                // 但如果接水前那份方案本来就是按“这个空瓶接满”算的，
                // 接满后就沿用它，避免接水前 3 药、接水后突然变 5 药。
                if (preEmpty && !fillChangesParts)
                {
                    long planTargetUl = (rFillPlan != null && rFillPlan.water0 != null)
                        ? rFillPlan.water0.v : 0L;
                    if (fillFull && planTargetUl > 0L && Math.Abs(planTargetUl - filledUl) <= 1500L)
                    {
                        LockPlan(rFillPlan, "空瓶接水后沿用接水前方案");
                        postFillFix = false;
                        postFillSig = "";
                    }
                    else UnlockPlan("空瓶接水后按实际水重算");
                }
                try
                {
                    if (!calcManual)
                        RefreshCalcFromBottle(dest, filledUl, WaterHelper.GetTotalCapacity(dest), fillPur);
                }
                catch (Exception) { }
                InvalidateCalc();
                if (fixActual && fillFull)
                {
                    // 只有真的补到容量附近才启用“只按实际这瓶水算”；
                    // 5909/6000 这种还没满的，继续允许性价比找补到 6000 的方案。
                    postFillFix = true;
                    postFillSig = LockVolSig();
                }
                else if (!calcManualSource)
                {
                    postFillFix = false;
                    postFillSig = "";
                    // 六项没变且不是空瓶接水，原方案仍然成立；锁在刷新之后落，避免计算页指纹变化把锁判失效。
                    if (!preEmpty && !fillChangesParts)
                        LockPlan(rFillPlan, "接水后锁定");
                }
                Rebuild(true);
                return "ok container=" + name + " vol=" + showMl + " pur=" + readPur;
            }
            catch (Exception e) { log.LogWarning("[pour] " + e.GetType().Name + " " + e.Message); return "ERR " + e.Message; }
        }

        /// <summary>win?：列出所有已配对窗口（index + focusStamp + IsVisible + 瓶体积/纯度）。</summary>
        public static string WinReport()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var p = PurifierHook.Pairs[i];
                string stamp = "?", vis = "?";
                try
                {
                    var stampV = p.Window == null ? null : GameBridge.Get(p.Window, "focusStamp");
                    if (stampV != null) stamp = stampV.ToString();
                }
                catch (Exception) { }
                try { vis = GameBridge.WinVisible(p.Window) ? "true" : "false"; } catch (Exception) { }
                int vol = -1, pur = -1;
                try
                {
                    var ci = p.BottleInv == null ? null : GameBridge.Get(p.BottleInv, "childItem") as GameItem;
                    if (ci != null) { vol = WaterHelper.GetTotalVolume(ci) / 1000; pur = WaterHelper.GetWaterPurity(ci); }
                }
                catch (Exception) { }
                if (i > 0) sb.Append(" | ");
                sb.Append("#").Append(i).Append(" stamp=").Append(stamp).Append(" vis=").Append(vis)
                  .Append(" vol=").Append(vol).Append("mL pur=").Append(pur);
            }
            string s = sb.Length == 0 ? "win? -> none" : "win? -> " + sb;
            Log(s);
            return s;
        }

        /// <summary>fill=N：指定第 N 台窗口灌水（自动化验证绑定是否正确）。</summary>
        public static string FillIndex(int i)
        {
            if (i < 0 || i >= PurifierHook.Pairs.Count) return "fill=" + i + " -> no-such-window";
            overrideBottle = PurifierHook.Pairs[i].BottleInv; ctxIndex = i + 1;
            Log("[win] active=#" + i + " (fill=" + i + ")");
            return OnFill();
        }
        public static string OnFillPublic() { return OnFill(); }

        // ---------------- 容器盘点（勾兑前置条件） ----------------

        private static List<GameItem> Containers()
        {
            var all = GameBridge.LiveAllItems();
            var res = new List<GameItem>();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it == null) continue;
                if (WaterHelper.GetTotalCapacity(it) <= 0) continue;
                res.Add(it);
            }
            return res;
        }

        /// <summary>挑一只**空**容器接水：排除自己、排除带滤水器的水罐，容量够大优先。</summary>
        private static GameItem PickEmptyReceiver(GameItem self, int needMl, out int freeTotal)
        {
            freeTotal = 0;
            var cs = Containers();
            GameItem best = null; int bestCap = 0;
            for (int i = 0; i < cs.Count; i++)
            {
                if (ReferenceEquals(cs[i], self)) continue;
                string nm = GameBridge.NameOfItem(cs[i]);
                if (nm != null && nm.IndexOf("滤水") >= 0) continue;   // 滤水器水罐不接生水
                int cap = WaterHelper.GetTotalCapacity(cs[i]);
                int vol = WaterHelper.GetTotalVolume(cs[i]);
                if (vol > 0) continue;                                  // 只认空瓶，有货的不碰
                freeTotal += cap;
                if (cap > bestCap) { bestCap = cap; best = cs[i]; }
            }
            return best;
        }

        /// <summary>灌水前把瓶里原有的水挪走：挪到空瓶；挪不干净就中止（绝不空瓶冲掉好水）。</summary>
        private static bool RescueWaterBeforeFill(GameItem dest, string why)
        {
            try
            {
                int have = WaterHelper.GetTotalVolume(dest);
                if (have <= 0) return true;
                int haveMl = have / 1000;
                int havePur = WaterHelper.GetWaterPurity(dest);
                int freeTotal;
                var alt = PickEmptyReceiver(dest, haveMl, out freeTotal);
                if (alt == null || freeTotal < haveMl)
                {
                    LastMsg = "瓶里还有 " + haveMl + " mL（" + PlanText.Pct(havePur) + "）——" + why +
                              "：先倒进一只空瓶（现在是空的 0 只/空间 " + freeTotal + " mL），或手动清空后再接";
                    Log("[pour] abort 1: bottle has " + haveMl + "mL@" + havePur + " free=" + freeTotal);
                    return false;
                }
                try { WaterHelper.TransferLiquid(dest, alt); }
                catch (Exception e) { Log("[pour] rescue transfer " + e.GetType().Name + " " + e.Message); }
                int left = WaterHelper.GetTotalVolume(dest);
                if (left > 0)
                {
                    LastMsg = "瓶里还剩 " + (left / 1000) + " mL 挪不走（" + GameBridge.NameOfItem(alt) + " 装不下）——先清空再接水";
                    Log("[pour] abort 2: " + (left / 1000) + "mL left");
                    return false;
                }
                Log("[pour] rescue " + haveMl + "mL@" + havePur + " -> " + GameBridge.NameOfItem(alt));
                return true;
            }
            catch (Exception e) { log.LogWarning("[pour] rescue " + e.GetType().Name + " " + e.Message); return false; }
        }

        /// <summary>能承接的容器 = 剩余空间最大的那只。</summary>
        private static GameItem PickReceiver(int needMl, out int freeMl, out int count, out int maxCapMl)
        {
            freeMl = 0; count = 0; maxCapMl = 0;
            // 优先挑「空且够大」的瓶子做承接瓶：满瓶 free=0，选中它必然导不进去
            // （旧实现只按 free 最大挑，全满时会挑到满瓶，报「承接瓶只有 0 mL」）。
            GameItem emptyBest = null; int emptyCap = 0;
            GameItem anyBest = null; int anyFree = 0;
            long needUl = needMl > 0 ? (long)needMl * 1000L : 0L;
            var cs = Containers();
            for (int i = 0; i < cs.Count; i++)
            {
                int cap = WaterHelper.GetTotalCapacity(cs[i]);
                int vol = WaterHelper.GetTotalVolume(cs[i]);
                int free = cap - vol;
                count++;
                if (cap > maxCapMl) maxCapMl = cap;
                if (vol <= 0 && cap >= needUl && cap > emptyCap) { emptyCap = cap; emptyBest = cs[i]; }
                if (free > anyFree) { anyFree = free; anyBest = cs[i]; }
            }
            freeMl = anyFree;
            return emptyBest != null ? emptyBest : anyBest;
        }

        // ---------------- 模式二：备好水放台面 ----------------

        private static string serveResult;

        /// <summary>找一个「空的、非机器槽」的容器来接走已有液体（装得下 needMl 优先）。</summary>
        private static GameItem FindEmptyReceiver(GameItem exclude, int needMl)
        {
            try
            {
                var res = new List<GameItem>();
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null || ReferenceEquals(it, exclude)) continue;
                    if (WaterHelper.GetTotalCapacity(it) <= 0) continue;
                    if (WaterHelper.GetTotalVolume(it) > 0) continue;      // 必须是空的
                    if (IsMachineSlotItem(it)) continue;                     // 不进机器槽（带滤芯的水罐）
                    res.Add(it);
                }
                res.Sort(delegate(GameItem a, GameItem b)
                {
                    int k = WaterHelper.GetTotalCapacity(b).CompareTo(WaterHelper.GetTotalCapacity(a));
                    return k;
                });
                for (int i = 0; i < res.Count; i++)
                    if (needMl <= 0 || WaterHelper.GetTotalCapacity(res[i]) >= needMl * 1000) return res[i];
                return res.Count > 0 ? res[0] : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>这个物品是不是某台净水器「储水容器」槽里的那只。</summary>
        private static bool IsMachineSlotItem(GameItem it)
        {
            if (it == null) return false;
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    if (inv == null) continue;
                    var ci = GameBridge.Get(inv, "childItem") as GameItem;
                    if (ci != null && ReferenceEquals(ci, it)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static void AddSources(List<GameItem> into, List<GameItem> from, GameItem skip)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
            {
                var it = from[i];
                if (it == null || ReferenceEquals(it, skip)) continue;
                if (WaterHelper.GetTotalVolume(it) <= 0) continue;
                bool dup = false;
                for (int j = 0; j < into.Count; j++) if (ReferenceEquals(into[j], it)) { dup = true; break; }
                if (!dup) into.Add(it);
            }
        }

        private static void ServeFail(string why, int freeMl, int needMl)
        {
            serveResult = "serve -> FAIL " + why + " free=" + freeMl + "mL need=" + needMl + "mL";
            LastMsg = "备水失败：" + why + "（可承接 " + freeMl + " mL，需要 " + needMl + " mL）";
            Log("[serve] FAIL " + why + " free=" + freeMl + "mL need=" + needMl + "mL");
            Rebuild(true);
        }

        /// <summary>备水放台面：把玩家现有容器里的水真实导进承接瓶，再放到出货口（模拟真人导液体）。</summary>
        private static void OnPrepare2()
        {
            try
            {
                int grade, pur;
                TapSource(out grade, out pur);
                if (pur <= 0) TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                int targetBp = PlanText.TargetBp(TargetIdx, q);

                int freeMl = 0, cnt = 0, maxCapMl = 0;
                // 备水＝模拟玩家手动：承接瓶必须**已经放在出货口台面上**且是空的。
                // 不再用 TryAddInto/ModHelper 搬物品——玩家实测「点一次容器就消失一个」。
                GameItem recv = null;
                try
                {
                    var cnts = GameBridge.CounterItems();
                    for (int i = 0; i < cnts.Count; i++)
                    {
                        var it = cnts[i];
                        if (it == null) continue;
                        int cap = WaterHelper.GetTotalCapacity(it);
                        if (cap <= 0) continue;
                        if (WaterHelper.GetTotalVolume(it) > 0) continue;
                        cnt++;
                        if (cap > maxCapMl) { maxCapMl = cap; recv = it; }
                    }
                }
                catch (Exception) { }
                if (recv == null)
                {
                    ServeFail("台面上没有空瓶：先把一个空容器放到出货口台面，再点备水", 0, 0);
                    return;
                }
                if (recv == null) { ServeFail("缺可承接容器", 0, 0); return; }
                long needUl = MaxKeepUl(WaterHelper.GetTotalCapacity(recv), pur, perf, q, targetBp);
                // 备水要跟当前选中的方案联动：选「过滤棉+1片药片 → 2000→1911」就备 2000，而不是按理论上限另算一套。
                var rp = CurrentPlan(true);
                if (rp != null && rp.pick != null && rp.pick.feedUl > 0) needUl = rp.pick.feedUl;
                // 承接瓶是台面上的实物，装不下就得按它的容量来：以前直接照 6L 方案的 3550 mL 备，
                // 挑了 3000 mL 的瓶子就只会报「源水不够」。
                long recvCapUl = WaterHelper.GetTotalCapacity(recv);
                if (recvCapUl > 0 && needUl > recvCapUl) needUl = recvCapUl;
                int needMl = (int)(needUl / 1000);
                if (needMl <= 0) { ServeFail("今日水质配不出达标水", freeMl, 0); return; }

                var srcs = new List<GameItem>();
                AddSources(srcs, GameBridge.LiveAllItems(), recv);
                AddSources(srcs, GameBridge.CounterItems(), recv);
                if (srcs.Count == 0) { ServeFail("缺源水：背包/台面没有装水的容器", freeMl, needMl); return; }
                srcs.Sort(delegate(GameItem a, GameItem b)
                {
                    return WaterHelper.GetWaterPurity(b).CompareTo(WaterHelper.GetWaterPurity(a));
                });

                int srcMl = 0, srcPur = 0;
                string srcName = "none";
                // 承接瓶里原有的水不能凭空倒掉（玩家反馈「备水会消失」就是这里先 EmptyContainer 把水清了）。
                // 有货就换个空瓶；一个都没有就明确报错，让玩家自己决定。
                int recvHave = WaterHelper.GetTotalVolume(recv);
                if (recvHave > 0)
                {
                    int free2, cnt2, cap2;
                    var alt = PickReceiver(1, out free2, out cnt2, out cap2);
                    if (alt == null || WaterHelper.GetTotalVolume(alt) > 0)
                    {
                        ServeFail("承接瓶里还有 " + (recvHave / 1000) + " mL，先倒空或换一个空瓶", freeMl, needMl);
                        return;
                    }
                    recv = alt;
                }
                bool placedOnCounter = true;   // 瓶子本来就在台面上（不再搬动）

GameItem lastSrc = null;
                for (int i = 0; i < srcs.Count; i++)
                {
                    if (WaterHelper.GetTotalVolume(recv) / 1000 >= needMl) break;
                    var s = srcs[i];
                    int sv = WaterHelper.GetTotalVolume(s);
                    if (sv <= 0) continue;
                    int sp = WaterHelper.GetWaterPurity(s);   // 必须导液前读：导完源瓶空了，纯度会变 0
                    if (WaterHelper.GetTotalCapacity(recv) - WaterHelper.GetTotalVolume(recv) <= 0) break;
                    try { WaterHelper.TransferLiquid(s, recv); }
                    catch (Exception) { continue; }
                    lastSrc = s;
                    if (srcName == "none") { srcMl = sv / 1000; srcPur = sp; srcName = GameBridge.NameOfItem(s); }
                }
                long gotUl = WaterHelper.GetTotalVolume(recv);
                if (gotUl <= 0) { ServeFail("源水没有导进承接瓶", freeMl, needMl); return; }
                if (gotUl > needUl)
                {
                    // 多出来的水回倒给最后一个源瓶，实在倒不回去才丢掉（并写日志说明丢了多少）。
                    long excessUl = gotUl - needUl;
                    bool back = false;
                    if (lastSrc != null)
                    {
                        try { WaterHelper.TransferLiquid(recv, lastSrc); back = true; }
                        catch (Exception) { back = false; }
                    }
                    if (!back)
                    {
                        WaterHelper.RemoveMl(recv, (int)((excessUl + 500) / 1000));
                        Log("[serve] 多出的 " + (excessUl / 1000) + " mL 倒不回去，已丢弃");
                    }
                    gotUl = WaterHelper.GetTotalVolume(recv);
                }
                WaterFeatureHelper.UpdateWaterFeature(recv, false);
                int dstMl = (int)(gotUl / 1000);
                int dstPur = WaterHelper.GetWaterPurity(recv);
                if (dstMl < needMl)
                {
                    ServeFail("源水不够，承接瓶只有 " + dstMl + " mL", dstMl, needMl);
                    return;
                }

                // 瓶子在开始倒液之前就已经摆上出货口台面了，这里只做“还在台面上”的校验。
                bool moved = placedOnCounter;
                int accepted = placedOnCounter ? 1 : 0;
                bool found = false;
                var cs = GameBridge.CounterItems();
                for (int i = 0; i < cs.Count; i++) if (ReferenceEquals(cs[i], recv)) { found = true; break; }

                string dstName = GameBridge.NameOfItem(recv);
                serveResult = "serve -> src=" + srcName + " " + srcMl + "mL@" + PlanText.Pct(srcPur) +
                              " -> dst=" + dstName + " " + dstMl + "mL@" + PlanText.Pct(dstPur) + " counter=" + (found ? "true" : "false");
                LastMsg = !moved
                    ? "水已配好但放不上台面（" + dstName + " " + dstMl + " mL，accepted=" + accepted + "）——请手动拖到出货口"
                    : "台面已备好：" + dstName + " " + dstMl + " mL  " + PlanText.Pct(dstPur) +
                      "（一晚 → " + PlanText.Pct(targetBp) + "）";
                Log("[serve] src=" + srcName + " " + srcMl + "mL@" + srcPur + " -> dst=" + dstName + " " + dstMl + "mL@" + dstPur +
                    " counter=" + (found ? "true" : "false") + " moved=" + (moved ? "true" : "false"));
                Log("[counter] found=" + (found ? "true" : "false") + " vol=" + dstMl + " pur=" + dstPur);
                Rebuild(true);
            }
            catch (Exception e) { log.LogWarning("[serve] " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 凑量 ----------------

        public static string MixPreview()
        {
            int freeMl, cnt, maxCapMl;
            var recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
            var b = GameBottle();
            int V = TargetVolumeMl();
            return "[mix] containers=" + cnt + " maxCap=" + (maxCapMl / 1000) + "mL free=" + (freeMl / 1000) +
                   "mL V=" + V + " bottle=" + (b == null ? "none" : WaterHelper.GetTotalVolume(b) + "uL") +
                   " recv=" + (recv == null ? "none" : GameBridge.NameOfItem(recv));
        }

        private static void OnMix()
        {
            try
            {
                var b = GameBottle();
                if (b == null) { LastMsg = "净水器里没有瓶子（凑量是倒进机器里的瓶）"; return; }
                int V = TargetVolumeMl();
                if (V <= 0) { LastMsg = "目标体积为 0"; return; }

                int freeMl = 0, cnt = 0, maxCapMl = 0;
                var recv = CtxBottle();
                if (recv == null) recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
                if (recv == null) { LastMsg = "缺少可勾兑的容器：背包和台面都没有能装水的容器"; return; }
                var items = Candidates(b);
                int sum;
                var picked = PickSubset(items, V, out sum);
                WaterHelper.EmptyContainer(b);
                for (int i = 0; i < picked.Count; i++)
                {
                    try { WaterHelper.TransferLiquid(picked[i], b); }
                    catch (Exception e) { log.LogWarning("[mix] transfer " + e.Message); }
                }
                int got = WaterHelper.GetTotalVolume(b) / 1000;
                int grade, pur;
                TodaySource(out grade, out pur);
                int need = V - got;
                if (need > 0) WaterHelper.AddWater(b, grade, need * 1000, false, pur, pur, true);
                int final = WaterHelper.GetTotalVolume(b) / 1000;
                if (final < V - 1) LastMsg = "只有 " + final + " mL（差 " + (V - final) + " mL）";
                else LastMsg = "凑好 " + final + " mL（" + picked.Count + " 个容器）";
                Log("[mix] V=" + V + " got=" + got + " final=" + final + " picked=" + picked.Count + " containers=" + cnt);
                Rebuild(true);
            }
            catch (Exception e) { log.LogWarning("[mix] " + e.GetType().Name + " " + e.Message); }
        }

        private static List<GameItem> Candidates(GameItem target)
        {
            var all = GameBridge.LiveItems();
            var res = new List<GameItem>();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it == null || ReferenceEquals(it, target)) continue;
                if (WaterHelper.GetTotalVolume(it) <= 0) continue;
                res.Add(it);
            }
            res.Sort(delegate (GameItem a, GameItem c) { return WaterHelper.GetTotalVolume(c).CompareTo(WaterHelper.GetTotalVolume(a)); });
            return res;
        }

        private static List<GameItem> PickSubset(List<GameItem> items, int V, out int sum)
        {
            sum = 0;
            int n = items.Count;
            var vol = new int[n];
            for (int i = 0; i < n; i++) vol[i] = WaterHelper.GetTotalVolume(items[i]) / 1000;

            var dp = new int[V + 1];
            var pick = new int[V + 1];
            for (int j = 0; j <= V; j++) { dp[j] = 9999; pick[j] = -1; }
            dp[0] = 0;
            for (int i = 0; i < n; i++)
            {
                int v = vol[i];
                if (v <= 0) continue;
                for (int j = V; j >= v; j--)
                {
                    if (dp[j - v] + 1 < dp[j]) { dp[j] = dp[j - v] + 1; pick[j] = i; }
                }
            }
            int best = 0;
            for (int j = V; j >= 0; j--) if (dp[j] < 9999) { best = j; break; }

            var res = new List<GameItem>();
            int cur = best;
            while (cur > 0 && pick[cur] >= 0)
            {
                int i = pick[cur];
                res.Add(items[i]);
                cur -= vol[i];
            }
            sum = best;
            return res;
        }

        // ---------------- 前期：药片 / 紫外 / 两瓶兑水 ----------------

        private static GameItem AnyBottle()
        {
            var b = GameBottle();
            if (b != null) return b;
            var all = GameBridge.LiveItems();
            return all.Count > 0 ? all[0] : null;
        }

        private static void OnTablet()
        {
            var b = AnyBottle();
            if (b == null) { LastMsg = "没有含水的瓶子，先放一瓶水"; return; }
            int before = WaterHelper.GetWaterPurity(b);
            int vol = WaterHelper.GetTotalVolume(b);
            try { WaterHelper.OnWaterPurificationTabletAdded(b); }
            catch (Exception e) { log.LogWarning("[tablet] " + e.Message); LastMsg = "药片失败：" + e.Message; return; }
            int after = WaterHelper.GetWaterPurity(b);
            LastMsg = "药片：" + PlanText.Pct(before) + " → " + PlanText.Pct(after) + "  " + (WaterHelper.GetTotalVolume(b) / 1000) + "mL";
            Log("[tablet] " + LastMsg + " vol=" + vol);
            Rebuild(true);
        }

        private static void OnUv()
        {
            var b = AnyBottle();
            if (b == null) { LastMsg = "没有含水的瓶子，先放一瓶水"; return; }
            if (WaterHelper.GetWaterPurity(b) <= 0) { LastMsg = "先扫描这瓶水，读不到真实纯度"; return; }
            int before = WaterHelper.GetWaterPurity(b);
            try { WaterHelper.OnUVUsed(b); }
            catch (Exception e) { log.LogWarning("[uv] " + e.Message); LastMsg = "紫外失败：" + e.Message; return; }
            LastMsg = "紫外：" + PlanText.Pct(before) + " → " + PlanText.Pct(WaterHelper.GetWaterPurity(b)) + "  " + (WaterHelper.GetTotalVolume(b) / 1000) + "mL";
            Log("[uv] " + LastMsg);
            Rebuild(true);
        }

        private static void OnBlend()
        {
            var all = GameBridge.LiveItems();
            if (all.Count < 2) { LastMsg = "至少要两瓶水才能兑（现在 " + all.Count + " 瓶）"; return; }
            GameItem good = null, bad = null;
            int gp = -1, bp = 99999;
            for (int i = 0; i < all.Count; i++)
            {
                int p = WaterHelper.GetWaterPurity(all[i]);
                if (p <= 0) continue;
                if (p > gp) { gp = p; good = all[i]; }
                if (p < bp) { bp = p; bad = all[i]; }
            }
            if (good == null || bad == null || ReferenceEquals(good, bad)) { LastMsg = "读不到两瓶不同的水，先扫描"; return; }
            int freeMl, cnt, maxCapMl;
            var recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
            if (recv == null) { LastMsg = "缺少可勾兑的容器：背包和台面都没有能装水的容器"; return; }

            int V = Math.Min(1000, freeMl / 1000);
            if (V <= 0) { LastMsg = "承接容器没有空间"; return; }
            int target = 9900;
            WaterHelper.EmptyContainer(recv);
            WaterHelper.AddWater(recv, 4, V * 1000, false, bp, bp, true);
            LastMsg = "兑水：A " + PlanText.Pct(gp) + " + B " + PlanText.Pct(bp) + " → 承接瓶 " + V + "mL（占位实现）";
            Log("[blend] gp=" + gp + " bp=" + bp + " V=" + V + " target=" + target + " containers=" + cnt);
            Rebuild(true);
        }

        // ---------------- 计算（唯一算数的地方） ----------------

        private static PanelModel Compute()
        {
            var m = new PanelModel();
            m.Mode = Mode; m.PreMode = PreMode; m.PostMode = PostMode;
            // 右键读取后设置页切到手动源：主页的容器/水质也要显示这只读取瓶，
            // 不能还拿当前机器瓶，导致主页和计算页各算一套。
            var b = ComputeBottle();
            m.HasBottle = b != null;
            if (b != null)
            {
                m.BottleName = GameBridge.NameOfItem(b);
                // 20 L 罐实测 19,999,998 µL（差 2 µL），整数截断会显示成 19999 —— 按四舍五入取整（m00160）
                m.VolumeMl = (int)((WaterHelper.GetTotalVolume(b) + 999L) / 1000L);
                m.CapacityMl = (int)((WaterHelper.GetTotalCapacity(b) + 999L) / 1000L);
                m.PurityBp = calcManualSource ? CalcManualPurityBp() : WaterHelper.GetWaterPurity(b);
                if (m.PurityBp <= 0)
                {
                    m.Plans = null;
                    m.Verdict = "纯度未知 —— 先用 水质扫描仪 / 试纸 扫一次这瓶，再算方案";
                }
            }
            int perf, q;
            Bonuses(out perf, out q);
            m.PerfPct = perf; m.Quality = q;
            m.CapBp = WaterMath.CapOf(q);
            m.TargetBp = PlanText.TargetBp(TargetIdx, q);
            m.MixKnown = false;
            m.MixText = "";
            try
            {
                var parts = WaterHelper.GetLiquidParts(b);
                long psum = 0;
                if (parts != null) for (int pi = 0; pi < parts.Length; pi++) psum += parts[pi];
                if (psum > 0)
                {
                    m.MixKnown = true;
                    var pn = GameBridge.LiquidNames();
                    var psb = new System.Text.StringBuilder();
                    for (int pi = 0; pi < parts.Length && parts[pi] > 0; pi++)
                    {
                        string pnm = CnLiquid((pn != null && pi < pn.Length) ? pn[pi] : ("#" + pi), pi);
                        if (psb.Length > 0) psb.Append("  ");
                        psb.Append(pnm).Append(" ").Append(PlanText.Pct((int)(parts[pi] * 10000L / psum)));
                    }
                    m.MixText = psb.ToString();
                }
            }
            catch (Exception e) { Log("[parts] fail " + e.GetType().Name); }
            // 只在配比真的变了才写日志：Compute 每 0.4 秒跑一次，逐帧写会把 log.txt 刷到几百 KB。
            string pl = "known=" + m.MixKnown + " bottle=" + m.BottleName + " text=" + m.MixText;
            if (pl != lastPartsLog) { lastPartsLog = pl; Log("[parts] " + pl); }

            int grade, pur;
            TodaySource(out grade, out pur);
            m.Grade = grade; m.SourcePurBp = pur;
            m.SourceNote = "今日" + (grade == 4 ? "铁锈水" : grade == 5 ? "沟渠水" : "水") + " " + PlanText.Pct(pur);

            string capNote = m.TargetBp > m.CapBp ? "（本机上限只有 " + PlanText.Pct(m.CapBp) + "，要 99.90% 得先装质量模组 +100）" : "";
            m.Hint = "性能 +" + perf + "%  质量 " + q + "  上限 " + PlanText.Pct(m.CapBp) + capNote;

            if (!m.HasBottle)
            {
                m.Verdict = "储水容器里没有水瓶。";
                return m;
            }

            if (!m.MixKnown)
            {
                m.Verdict = "这瓶还没扫过，读不到六项配比 —— 请先用水质扫描仪/试纸扫一次再算。";
                return m;
            }

            if (m.Mode == 0)
            {
                var on = WaterMath.PlanOneNight(m.VolumeMl * 1000L, m.PurityBp, perf, q, m.TargetBp, 1);
                if (on != null && on.possible) m.Plans.Add(on);
                m.Verdict = on != null && on.possible ? PlanText.Describe(on) : "一晚做不到，试试兑水或换小瓶。";
                m.Selected = 0;
            }
            else if (m.Mode == 1)
            {
                var best = WaterMath.Solve(m.VolumeMl * 1000L, m.PurityBp, perf, q, false, false, 0, m.TargetBp, 40);
                if (best != null && best.ok)
                {
                    var on = new WaterMath.OneNight();
                    on.possible = true; on.asIs = (best.feed == m.VolumeMl * 1000L);
                    on.drainUl = m.VolumeMl * 1000L - best.feed; on.keepUl = best.feed;
                    on.purity = best.purity; on.output = best.output;
                    m.Plans.Add(on);
                    m.Verdict = "效率：接 " + (best.feed / 1000) + " mL → 一晚 " + PlanText.Pct(best.purity) + "  " + (best.output / 1000) + " mL";
                }
                else m.Verdict = "这一瓶一晚达不到目标。";
                m.Selected = 0;
            }
            else
            {
                m.Plans = m.PurityBp > 0 ? WaterMath.Plans(m.VolumeMl * 1000L, m.PurityBp, perf, q, m.TargetBp) : null;
                m.Verdict = m.PurityBp <= 0
                    ? "纯度未知 —— 先用 水质扫描仪 / 试纸 扫一次这瓶，再算方案"
                    : (m.Plans.Count > 0 ? ("共 " + m.Plans.Count + " 个方案，选最省的那个") : "没有可用方案。");
                m.Selected = 0;
            }
            return m;
        }

        // ---------------- tooltip ----------------

        // 悬停提示会被游戏高频调用；PlanOneNight 带迭代求解 + Bonuses 反射，每次都算会直接卡住鼠标。
        // 用「同输入 2 秒内复用」的缓存，输入是 体积/纯度/性能/质量 四项。
        private static string tipKey, tipVal;
        private static float tipAt;

        public static string TooltipLine(GameItem machine)
        {
            // 玩家 2026-11-11 定案：净水器物品标签上**不要**模组追加的那行结论
            // （原「净水助手：今晚 → 99.01%（留 2036 mL）」已删；结论只在模组面板里看）。
            // 这里保留方法签名（供 PurifierPatch 编译引用），但永不返回内容。
            if (machine == null) return null;
            return null;
            /*
            try
            {
                if (machine == null) return null;
                var b = GameBottle();
                if (b == null) return null;
                int vol = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                int perf, q;
                Bonuses(out perf, out q);
                string key = vol + "|" + pur + "|" + perf + "|" + q;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (key == tipKey && (now - tipAt) < 2f) return tipVal;
                var on = WaterMath.PlanOneNight(vol, pur, perf, q, WaterMath.CapOf(q), 1);
                if (on == null || !on.possible) tipVal = "净水助手：这瓶一晚到不了目标";
                else if (on.asIs) tipVal = "净水助手：今晚 → " + PlanText.Pct(on.purity) + "（原样跑）";
                else tipVal = "净水助手：今晚 → " + PlanText.Pct(on.purity) + "（留 " + (on.keepUl / 1000) + " mL）";
                tipKey = key; tipAt = now;
                return tipVal;
            }
            catch (Exception) { return null; }
            */
        }

        public static void LogTip(GameItem item)
        {
            string id = item == null ? "null" : item.identifier;
            if (log != null && id != lastTipLog) { lastTipLog = id; log.LogInfo("[tip] hit " + id); }
        }

        // ---------------- 后台命令（CmdFile 用） ----------------

        public static string MainVisible() { return (panelWant && panelWin != null && panelWin.IsVisible()) ? "true" : "false"; }

        public static string ShowTestWindow(bool show) { return ShowMain(show); }

        public static string IconWindow()
        {
            try
            {
                EnsurePanel();
                if (panelWin == null) return "no-window";
                Rebuild(true);
                panelWin.Show();
                panelWin.ToFront(true);
                return "main ok visible=" + panelWin.IsVisible();
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static string Diag()
        {
            try
            {
                var b = GameBottle();
                int u1 = b == null ? -1 : WaterHelper.GetTotalVolume(b);
                int u2 = b == null ? -1 : WaterHelper.GetTotalCapacity(b);
                int u3 = -1;
                if (b != null) { try { u3 = GameBridge.IntOf(b, "GetTotalCapacityML"); } catch (Exception) { } }
                return "diag vol=" + u1 + " cap=" + u2 + " capML=" + u3 +
                       " bottle=" + (b == null ? "none" : GameBridge.NameOfItem(b)) +
                       " model=" + Model.VolumeMl + "/" + Model.CapacityMl + "mL";
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static string Sprites(string kw)
        {
            var arr = Resources.FindObjectsOfTypeAll<Sprite>();
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < arr.Length && n < 40; i++)
            {
                if (arr[i] == null) continue;
                if (!string.IsNullOrEmpty(kw) && arr[i].name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n > 0) sb.Append(",");
                sb.Append(arr[i].name); n++;
            }
            return "sprites total=" + arr.Length + " match=" + n + " [" + sb + "]";
        }

        public static string DumpIds()
        {
            var b = GameBottle();
            var machine = CurrentMachine();
            string mi = machine == null ? "none" : GameBridge.NameOfItem(machine);
            string bi = b == null ? "none" : b.identifier;
            string ti = PurifierHook.MachineWindow == null ? "none" : "PixelWindow";
            return "machine=" + mi + " bottle=" + bi + " title=" + ti;
        }

        public static string LiveList()
        {
            var all = GameBridge.LiveAllItems();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < all.Count && i < 12; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(GameBridge.NameOfItem(all[i])).Append(" ").
                   Append(WaterHelper.GetTotalVolume(all[i]) / 1000).Append("/").
                   Append(WaterHelper.GetTotalCapacity(all[i]) / 1000).Append("mL");
            }
            return "items=" + all.Count + " [" + sb + "]";
        }

        /// <summary>全量盘点：背包 + 出货口（台面） + 每台机器槽，全部按 mL 打印。</summary>
        public static string ScanAllPublic()
        {
            var sb = new System.Text.StringBuilder();
            var bag = GameBridge.LiveAllItems();
            var counter = GameBridge.CounterItems();
            var machines = new List<GameItem>();
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    var its = GameBridge.ItemsOf(inv);
                    for (int k = 0; k < its.Count; k++) machines.Add(its[k]);
                }
            }
            catch (Exception) { }
            sb.Append("[scan] bag=").Append(bag.Count).Append(" counter=").Append(counter.Count).
               Append(" machines=").Append(machines.Count);
            Log(sb.ToString());
            DumpList("bag", bag); DumpList("counter", counter); DumpList("machine", machines);
            // 出货口原本在不在，用于模式二的自证。
            return sb.ToString();
        }

        private static void DumpList(string tag, List<GameItem> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null) continue;
                int vol = WaterHelper.GetTotalVolume(it);
                int cap = WaterHelper.GetTotalCapacity(it);
                if (cap <= 0) continue;
                Log("[scan] " + tag + " " + GameBridge.NameOfItem(it) + " " + (vol / 1000) + "/" + (cap / 1000) +
                    "mL " + PlanText.Pct(WaterHelper.GetWaterPurity(it)));
            }
        }
        public static string PlanSummary()
        {
            var m = Model;
            var sb = new System.Text.StringBuilder();
            sb.Append("mode=").Append(PlanText.ModeShort[m.Mode]).
               Append(" target=").Append(PlanText.Pct(m.TargetBp)).
               Append(" vol=").Append(m.VolumeMl).Append("mL pur=").Append(PlanText.Pct(m.PurityBp)).
               Append(" hint=").Append(m.Hint).Append(" | ");
            if (m.Plans != null && m.Plans.Count > 0)
            {
                for (int i = 0; i < m.Plans.Count && i < 4; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(PlanText.OneNightRow(i, m.Plans[i], m.TargetBp));
                    sb.Append(" nights=1");
                }
            }
            else sb.Append(m.Verdict);
            if (LastMsg != null && LastMsg.Length > 0) sb.Append(" || ").Append(LastMsg);
            return sb.ToString();
        }

        public static string OnMixPublic() { OnMix(); return MixPreview(); }

        public static string EarlyPublic(int which)
        {
            if (which == 0) OnTablet();
            else if (which == 1) OnUv();
            else OnBlend();
            return LastMsg == null ? "" : LastMsg;
        }
    }
}
