using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>净水助手：F7 开关（F8/F9 归游戏的调试控制台与开发菜单）；算接水量/晚数/耗材，附兑水到 99.00 与配方速查。</summary>
    public class Plugin : MonoBehaviour
    {
        private ManualLogSource Logger;
        private bool show = false;
        public static Plugin Instance;
        private Rect win = new Rect(120, 90, 500, 560);
        private int tab = 0;
        private int fillIdx = -1;
        private static readonly string[] TabNames = { "做一次水", "我的瓶子", "兑水到 99", "常用配方" };

        // 净水输入
        private string sPurity = "91.04";
        private string sCap = "6000";
        private string sPerf = "0";
        private string sQuality = "0";
        private bool useFilter = true;
        private bool useUv = false;
        private int tablets = 0;
        private int targetIdx = 0;
        private static readonly string[] TargetNames = { "优质 99.00%", "纯水 99.90%" };
        private static readonly int[] Targets = { 9900, 9990 };

        // 兑水输入
        private string dGood = "99.29";
        private string dGoodVol = "1000";
        private string dBad = "98.00";
        private string dBadVol = "0";
        private string dTarget = "99.00";

        private List<SaveReader.Ctr> bottles = new List<SaveReader.Ctr>();
        private int pickIdx = 0;
        private float lastLive = -9f;
        private float lastMachine = -9f;
        private string machineLine = "";
        private string machineKey = "";
        private bool hasMachine;
        private int mVol, mCap, mPur, mNights;
        private int mOutPurity;
        private long mOut;
        private string oneLine = "";
        private string oneLine2 = "";
        private readonly System.Collections.Generic.List<WaterMath.OneNight> plans = new System.Collections.Generic.List<WaterMath.OneNight>();
        private int planSel = 0;
        private Vector2 plansScroll = Vector2.zero;
        private Vector2 recScroll = Vector2.zero;
        private bool optimistic = true;
        private string sTol = "0.05";
    // 方案排序偏好：true = 省耗材（宁愿多倒水），false = 少倒水（可用药片/过滤棉）
    private bool saveConsumable = true;
        private bool plansOpen = true;
        private static Font osFont;
        private int drawErrShown = -1;
        private string bottleMsg = "";
        private WaterMath.Plan plan;
        private string hint = "";
        private bool dirty = true;
        private bool scanned;

        private const string P = "dsh.watercalc.";

        private void Awake()
        {
            // 这条方法的铁律：绝不向外抛异常。
            // Unity 里 Awake 抛异常 = 该组件不再收到 Update()：F7、红框同步(MarkerTick)、
            // 点按钮后的延迟重建(pendingRebuild)、面板自动刷新全部一起失效——玩家看到的就是
            // 「红框没有 / 点圆点要重开面板才生效 / 按效率默认没反应」。
            Logger = ModEntry.Log ?? new ManualLogSource("WaterCalcMod");
            Instance = this;
            try
            {
                osFont = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, 16);
                Logger.LogInfo("[gui] 字体 " + (osFont != null ? osFont.name : "null"));
            }
            catch (Exception e) { try { Logger.LogWarning("[gui] 字体失败 " + e.Message); } catch (Exception) { } }
            try
            {
                sPurity = PlayerPrefs.GetString(P + "purity", sPurity);
                sCap = PlayerPrefs.GetString(P + "cap", sCap);
                sPerf = PlayerPrefs.GetString(P + "perf", sPerf);
                sQuality = PlayerPrefs.GetString(P + "quality", sQuality);
                useFilter = PlayerPrefs.GetInt(P + "filter", 1) == 1;
                useUv = PlayerPrefs.GetInt(P + "uv", 0) == 1;
                tablets = PlayerPrefs.GetInt(P + "tablets", 0);
                targetIdx = PlayerPrefs.GetInt(P + "target", 0);
                dGood = PlayerPrefs.GetString(P + "dgood", dGood);
                dBad = PlayerPrefs.GetString(P + "dbad", dBad);
                dTarget = PlayerPrefs.GetString(P + "dtarget", dTarget);
                // 独立面板默认关闭；老配置里存过 show=1 的一次性强制清掉。
                if (PlayerPrefs.GetInt(P + "migrated_hide_v1", 0) == 0)
                {
                    PlayerPrefs.SetInt(P + "show", 0);
                    PlayerPrefs.SetInt(P + "migrated_hide_v1", 1);
                    PlayerPrefs.Save();
                }
                show = false;
            }
            catch (Exception e) { try { Logger.LogWarning("[cfg] 读设置失败 " + e.GetType().Name); } catch (Exception) { } }
            try { Logger.LogInfo("净水助手 v0.3.1 已加载，F7 开关面板（F9 留给游戏开发菜单、F8 是游戏调试控制台）"); } catch (Exception) { }
            // GameBridge.Init 与各模块 Install 已由 ModEntry.Init 完成（原生 mod 的生命周期里先 Init 再 OnEnable）。
            // 这里再调一遍会给同一个游戏方法挂第二份 Harmony 补丁（日志里出现过两次「[ctx] 已挂」）。
            try
            {
                Logger.LogInfo("[save] bottles=" + bottles.Count);
                var lb = GameBridge.LiveBottles();
                if (lb != null && lb.Count > 0) bottles = lb;
                else
                {
                    var sv = SaveReader.Load();
                    if (sv != null) bottles = sv;
                }
                if (bottles != null && bottles.Count > 0) tab = 1;
            }
            catch (Exception e) { try { Logger.LogWarning("[save] 读背包/存档失败 " + e.GetType().Name); } catch (Exception) { } }
        }

        private void Save()
        {
            PlayerPrefs.SetString(P + "purity", sPurity);
            PlayerPrefs.SetString(P + "cap", sCap);
            PlayerPrefs.SetString(P + "perf", sPerf);
            PlayerPrefs.SetString(P + "quality", sQuality);
            PlayerPrefs.SetInt(P + "filter", useFilter ? 1 : 0);
            PlayerPrefs.SetInt(P + "uv", useUv ? 1 : 0);
            PlayerPrefs.SetInt(P + "tablets", tablets);
            PlayerPrefs.SetInt(P + "target", targetIdx);
            PlayerPrefs.SetString(P + "dgood", dGood);
            PlayerPrefs.SetString(P + "dbad", dBad);
            PlayerPrefs.SetString(P + "dtarget", dTarget);
            PlayerPrefs.SetInt(P + "show", 0);
            PlayerPrefs.Save();
        }

        // 一次性：把测试过夜扣掉的现金补回去（224 → -19，补到 224）
        // 只在 DEV 保留：它用 System.Type 上的 GetProperty/GetField 反射，
        // 属于 2026-12-10 起一律拒绝的 reflection-external。
#if DEV
        private static int cashTries;
        private void TryRestoreCash()
        {
            if (cashTries < 0) return;
            cashTries++;
            if (cashTries % 120 != 0) return;
            try
            {
                var t = GameBridge.T("PlayerStore");
                if (t == null) return;
                object inst = null;
                var prop = t.GetProperty("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null) { try { inst = prop.GetValue(null, null); } catch (Exception) { } }
                if (inst == null)
                {
                    var f2 = t.GetField("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (f2 != null) inst = f2.GetValue(null);
                }
                if (inst == null) return;
                var f = t.GetField("playerCash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return;
                int cash = (int)f.GetValue(inst);
                if (cash >= 0) { cashTries = -1; return; }
                f.SetValue(inst, 224);
                Logger.LogInfo("[fix] 现金 " + cash + " → " + f.GetValue(inst) + "（补回测试过夜扣的费用）");
                cashTries = -1;
            }
            catch (Exception e) { Logger.LogWarning("[fix] 补钱失败 " + e.GetType().Name + ": " + e.Message); }
        }
#endif

        private void Update()
        {
#if DEV
            updStage = 1;
            TryRestoreCash();
#endif
#if DEV
            updStage = 2;
            try { CmdFile.Tick(); } catch (Exception e) { Logger.LogWarning("[upd] cmd ERR " + e.GetType().Name + " " + e.Message); }
            updStage = 3;
            try { MorningAuto.Watch(); } catch (Exception e) { Logger.LogWarning("[upd] morning ERR " + e.GetType().Name); }
            try { AutoJoin.Watch(); } catch (Exception e) { Logger.LogWarning("[upd] join ERR " + e.GetType().Name); }
#else
            try { CmdFile.Tick(); } catch (Exception) { }
#endif
            try
            {
                if (Hotkey.Down())
                {
                    PurifierUi.ToggleMain();
                    Logger.LogInfo("[key] " + Hotkey.KeyName + " -> native panel");
                }
#if DEV
                updStage = 4;
#endif
                PollMachine();
#if DEV
                updStage = 5;
#endif
                try { PurifierUi.Tick(); } catch (Exception e) { Logger.LogWarning("[upd] ui ERR " + e.GetType().Name + " " + e.Message); }
#if DEV
                updStage = 6;
#endif
                if (UnityEngine.Time.realtimeSinceStartup - PurifierHook.OpenedAt < 0.5f) { PurifierHook.OpenedAt = -999f; }

            }
#if DEV
            catch (Exception e) { Logger.LogWarning("[upd] outer ERR " + e.GetType().Name + " " + e.Message); }
            updStage = 7;
            float nowU = UnityEngine.Time.realtimeSinceStartup;
            if (nowU - lastUpdAt >= 2f)
            {
                lastUpdAt = nowU;
                Logger.LogInfo("[upd] alive f=" + UnityEngine.Time.frameCount + " stage=" + updStage +
                               " pairs=" + PurifierHook.Pairs.Count +
                               " mouse=" + (Input.GetMouseButton(0) ? 1 : 0));
            }
#else
            catch (Exception) { }
#endif
        }

        /// <summary>每秒读一次净水器「滤水容器」里那瓶水；换了瓶就自动填进输入框。</summary>
        private void PollMachine()
        {
            PollMachineOnce();
        }

        private void PollMachineOnce()
        {
            if (UnityEngine.Time.realtimeSinceStartup - lastMachine < 1f) return;
            lastMachine = UnityEngine.Time.realtimeSinceStartup;
            try
            {
                var it = PurifierHook.CurrentBottle();
                if (it == null) { machineLine = ""; machineKey = ""; hasMachine = false; return; }
                int cap = GameBridge.IntOf(it, "GetTotalCapacity");
                int tot = GameBridge.IntOf(it, "GetTotalVolume");
                int pur = GameBridge.IntOf(it, "GetWaterPurity");
                if (cap <= 0)
                {
                    int ml = GameBridge.IntOf(it, "GetTotalCapacityML");
                    if (ml > 0) cap = ml * 1000;
                    if (cap <= 0) { cap = tot; Logger.LogWarning("[machine] 容量读不到，暂用体积 " + tot + " μL"); }
                }
                if (tot <= 0 || cap <= 0) { machineLine = ""; machineKey = ""; hasMachine = false; return; }
                hasMachine = true;
                mVol = tot; mCap = cap; mPur = pur;
                bool first = machineKey.Length == 0;
                string key = tot + "|" + cap + "|" + pur;
                machineLine = "机器里：" + GameBridge.NameOfItem(it) + "  " + (tot / 1000.0).ToString("0") + "/" +
                              (cap / 1000.0).ToString("0") + " mL · " + (pur / 100.0).ToString("0.00") + "%";
                if (key != machineKey)
                {
                    machineKey = key;
                    sPurity = (pur / 100.0).ToString("0.00");
                    sCap = (cap / 1000.0).ToString("0");
                    dirty = true;
                    if (first) { tab = 0; }
                    CalcNow();
                }
                ApplyBonuses(PurifierHook.ActiveMachine != null ? PurifierHook.ActiveMachine : PurifierHook.MachineItem);
            }
            catch (Exception e) { Logger.LogWarning("[machine] " + e.Message); }
        }

        /// <summary>F7：把场景里名字带「水/tap/sink/drain/faucet」的对象路径打到日志，用来找脏水源。</summary>
        private void DumpWaterScene()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            int n = 0;
            foreach (var tr in Resources.FindObjectsOfTypeAll<Transform>())
            {
                string path = tr.name;
                var cur = tr;
                for (int i = 0; i < 5 && cur.parent != null; i++) { cur = cur.parent; path = cur.name + "/" + path; }
                string low = path.ToLowerInvariant();
                if (path.IndexOf('水') >= 0 || low.IndexOf("water") >= 0 || low.IndexOf("tap") >= 0 ||
                    low.IndexOf("sink") >= 0 || low.IndexOf("drain") >= 0 || low.IndexOf("faucet") >= 0 ||
                    low.IndexOf("puddle") >= 0 || low.IndexOf("rust") >= 0)
                {
                    if (seen.Add(path)) { Logger.LogInfo("[scene] " + path); n++; }
                }
            }
            Logger.LogInfo("[scene] 命中 " + n + " 条");
        }

        private void CalcNow()
        {
            mNights = 0; mOut = 0; mOutPurity = 0; oneLine = ""; oneLine2 = "";
            if (!hasMachine || mVol <= 0) return;
            int capNow = WaterMath.CapOf(Parse(sQuality, 0.0));
            if (mPur >= capNow) { mOut = mVol; mOutPurity = mPur; }
            else
            {
                var mp = WaterMath.Run(mVol, mPur, Parse(sPerf, 0.0), Parse(sQuality, 0.0), false, false, 0,
                                       Targets[targetIdx], 40);
                mNights = mp.nights; mOut = mp.output; mOutPurity = mp.purity;
            }
            // 一晚直出：这瓶水怎样一晚上就达到面板选的目标
            int tgtRaw = Targets[targetIdx];
            int capQ = WaterMath.CapOf(Parse(sQuality, 0.0));
            // 容错：不知道真实水质时按最高质量估算，并多留一点余量，避免刚好卡在 99.00% 边缘
            int tolBp = optimistic ? (int)Math.Round(Parse(sTol, 0.05) * 100.0) : 0;
            if (tolBp < 0) tolBp = 0;
            int tgt = Math.Min(tgtRaw + tolBp, capQ);
            string tgtName = TargetNames[targetIdx];
            string capNote = capQ < tgtRaw
                ? "（本机上限只有 " + (capQ / 100.0).ToString("0.00") + "%，要 " + (tgtRaw / 100.0).ToString("0.00") + "% 得先装质量模组）"
                : "";
            WaterMath.PreferNoConsumable = saveConsumable;
            plans.Clear();
            plans.AddRange(WaterMath.Plans(mVol, mPur, Parse(sPerf, 0.0), Parse(sQuality, 0.0), tgt));
            if (planSel >= plans.Count) planSel = 0;
            if (plans.Count > 0)
            {
                WaterMath.OneNight best = plans[0];
                oneLine = best.asIs
                    ? ("一晚直出" + tgtName + " ✅ 直接跑 1 晚 → " + (best.purity / 100.0).ToString("0.00") + "%" + capNote)
                    : ("一晚直出" + tgtName + "：共 " + plans.Count + " 个方案" + capNote);
                oneLine2 = "";
            }
            else
            {
                // 连「满药片 + 过滤棉 + 紫外、原样不倒水」都到不了：报一晚的真实上限
                int maxP = 0;
                for (int tb = 0; tb <= 8; tb++)
                    for (int fb = 0; fb < 2; fb++)
                        for (int ub = 0; ub < 2; ub++)
                        {
                            var rr = WaterMath.Run(mVol, mPur, Parse(sPerf, 0.0), Parse(sQuality, 0.0),
                                                   fb == 1, ub == 1, tb, tgt, 1);
                            if (rr.purity > maxP) maxP = rr.purity;
                        }
                oneLine = "一晚直出" + tgtName + "：做不到——这瓶一晚最多 " + (maxP / 100.0).ToString("0.00") +
                          "%（用满 8 片药片 + 过滤棉 + 紫外也不行）" + capNote;
                oneLine2 = "要么先兑好水/换小瓶，要么分两晚跑。";
            }
        }

        /// <summary>方案列表里一行的高度概括（短）。</summary>
        private static string PlanRow(WaterMath.OneNight p)
        {
            var sb = new System.Text.StringBuilder();
            if (p.drainUl > 0) sb.Append("留 " + (p.keepUl / 1000.0).ToString("0") + " mL");
            if (p.useFilter) { if (sb.Length > 0) sb.Append("+"); sb.Append("过滤棉"); }
            if (p.useUv) { if (sb.Length > 0) sb.Append("+"); sb.Append("紫外"); }
            if (p.tablets > 0) { if (sb.Length > 0) sb.Append("+"); sb.Append(p.tablets + " 药片"); }
            if (sb.Length == 0) sb.Append("直接跑");
            sb.Append(" → " + (p.purity / 100.0).ToString("0.00") + "%");
            return sb.ToString();
        }

        /// <summary>选中某方案后，直接把它套进输入框并重算下方结论。</summary>
        private void ApplyPlan(WaterMath.OneNight p)
        {
            sCap = (p.keepUl / 1000.0).ToString("0");
            useFilter = p.useFilter;
            useUv = p.useUv;
            tablets = p.tablets;
            dirty = true;
            try { Calc(); } catch { }
            oneLine = "已选方案：" + (p.purity / 100.0).ToString("0.00") + "% ← " + PlanRow(p);
            oneLine2 = "倒掉 " + (p.drainUl / 1000.0).ToString("0") + " mL 后再跑一晚即可；下方输入已同步。";
            Save();
        }

        /// <summary>把一晚方案说成人话。</summary>
        private static string Describe(WaterMath.OneNight on)
        {
            var sb = new System.Text.StringBuilder();
            if (on.drainUl > 0)
                sb.Append("先倒掉 ").Append((on.drainUl / 1000.0).ToString("0")).Append(" mL（留 ")
                  .Append((on.keepUl / 1000.0).ToString("0")).Append(" mL）");
            if (on.tablets > 0) sb.Append(on.drainUl > 0 ? " + " : "").Append(on.tablets).Append(" 片药片");
            if (on.useFilter) sb.Append(" + 过滤棉");
            if (on.useUv) sb.Append(" + 紫外");
            if (on.drainUl > 0 || on.tablets > 0 || on.useFilter || on.useUv) sb.Append(" → ");
            sb.Append("跑 1 晚 → ").Append((on.output / 1000.0).ToString("0")).Append(" mL · ")
              .Append((on.purity / 100.0).ToString("0.00")).Append("%");
            return sb.ToString();
        }

        private void ApplyBonuses(object machine)
        {
            if (machine == null) return;
            int perf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", machine);
            int qual = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", machine);
            if (!ReferenceEquals(machine, lastBonusMachine))
            {
                lastBonusMachine = machine;
                Logger.LogInfo("[bonus] perf=" + perf + " qual=" + qual + " type=" + machine.GetType().Name);
            }
            if (perf >= 0) sPerf = perf.ToString();
            if (qual >= 0) sQuality = qual.ToString();
            if (perf > 0 || qual > 0) hint = "机器加成：性能 +" + perf + "% · 质量 " + qual;
        }

        private object lastBonusMachine;

        /// <summary>光标是否压在面板矩形里（GUI 坐标原点在左上，Input.mousePosition 在左下）。</summary>
        public static bool CursorInPanel()
        {
            if (!Plugin.Instance.show) return false;
            var m = Input.mousePosition;
            var p = new Vector2(m.x, Screen.height - m.y);
            return Plugin.Instance.win.Contains(p);
        }
        private static int guiCalls;

#if DEV
        private static string ScanSafe() { return GameBridge.Scan(); }
        // Update 走到第几步 + 2 秒心跳：用来分清「Update 没跑」「半路抛异常」「走到了但 Tick 内部提前 return」。
        private static int updStage;
        private static float lastUpdAt = -999f;
#else
        private static string ScanSafe() { return "扫描功能仅开发版可用"; }
#endif

        private void OnGUI()
        {
            // IMGUI 面板已停用：F9 与后台命令都走 PurifierUi 的原生窗口。
            guiCalls++;
        }

        private void GuiBody()
        {
            if (!scanned) { scanned = true; hint = ScanSafe(); }
            var st = new GUIStyle(GUI.skin.window) { fontSize = 14 };
            win = GUILayout.Window(889001, win, DrawWindow, "净水助手 v0.3.1（F9 关闭）", st);
            // PanelBlocker.Sync(win, show);
        }

        private void DrawWindow(int id)
        {
            var h1 = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            var t = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            var field = new GUIStyle(GUI.skin.textField) { fontSize = 14 };

            tab = GUILayout.Toolbar(tab, TabNames, GUILayout.Height(26));
            GUILayout.Space(6);

            if (targetIdx < 0 || targetIdx >= TargetNames.Length) targetIdx = 0;
            try
            {
                if (tab == 0) DrawPurify(h1, t, field);
                else if (tab == 1) DrawBottles(h1, t);
                else if (tab == 2) DrawDilute(h1, t, field);
                else { recScroll = GUILayout.BeginScrollView(recScroll, GUILayout.Height(300)); DrawRecipes(h1, t); GUILayout.EndScrollView(); }
            }
            catch (Exception e)
            {
                if (drawErrShown != tab) { drawErrShown = tab; Logger.LogWarning("[draw] tab=" + tab + " " + e); }
                GUILayout.Label("绘图出错(tab " + tab + ")：" + e.GetType().Name + " " + e.Message, t);
            }

            GUILayout.Space(4);
            if (GUILayout.Button("重置为默认", GUILayout.Height(22))) ResetAll();

            GUI.DragWindow();
        }

        private void DrawPurify(GUIStyle h1, GUIStyle t, GUIStyle field)
        {
            string sub;
            if (machineLine.Length == 0) sub = "机器里：空（把水瓶放进净水器的「滤水容器」）";
            else {
                int capNow = WaterMath.CapOf(Parse(sQuality, 0.0));
                if (mPur >= capNow) sub = "现状：这瓶 " + (mVol / 1000.0).ToString("0") + " mL 已到本机上限 " + (capNow / 100.0).ToString("0.00") + "%，再跑也不会更纯。";
                else if (mNights > 0) sub = "现状：这瓶直接跑 " + mNights + " 晚 → " + (mOut / 1000.0).ToString("0") + " mL · " + (mOutPurity / 100.0).ToString("0.00") + "%" + (mOutPurity < Targets[targetIdx] ? "（到不了 " + (Targets[targetIdx] / 100.0).ToString("0.00") + "%）" : "");
                else sub = "";
            }
            GUILayout.Label(machineLine, h1);
            GUILayout.Label(sub, t);
            GUILayout.Label(oneLine, h1);
            GUILayout.Label(oneLine2, t);
            string head = (plansOpen ? "▼ 方案 " : "▶ 方案 ") + plans.Count + " 个（点开选）";
            if (GUILayout.Button(head, t)) plansOpen = !plansOpen;
            if (plansOpen && plans.Count > 0)
            {
                plansScroll = GUILayout.BeginScrollView(plansScroll, GUILayout.Height(150));
                for (int i = 0; i < plans.Count; i++)
                {
                    var p = plans[i];
                    string row = (i + 1) + ". " + PlanRow(p);
                    if (GUILayout.Toggle(planSel == i, " " + row, t) != (planSel == i)) { planSel = i; ApplyPlan(p); }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("进水纯度 %", t, GUILayout.Width(110));
            sPurity = Num(field, sPurity, 80);
            GUILayout.Label("容器 mL", t, GUILayout.Width(70));
            sCap = Num(field, sCap, 80);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("性能 +%", t, GUILayout.Width(110));
            sPerf = Num(field, sPerf, 80);
            GUILayout.Label("质量模组", t, GUILayout.Width(70));
            sQuality = Num(field, sQuality, 80);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            useFilter = GUILayout.Toggle(useFilter, " 有过滤棉", t, GUILayout.Width(110));
            useUv = GUILayout.Toggle(useUv, " 有紫外线", t, GUILayout.Width(110));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("药片 " + tablets, t, GUILayout.Width(110));
            if (GUILayout.Button("-", GUILayout.Width(26))) { tablets = Math.Max(0, tablets - 1); dirty = true; }
            if (GUILayout.Button("+", GUILayout.Width(26))) { tablets = Math.Min(9, tablets + 1); dirty = true; }
            GUILayout.Label("目标", t, GUILayout.Width(40));
            int nt = GUILayout.Toolbar(targetIdx, TargetNames, GUILayout.Height(24));
            if (nt != targetIdx) { targetIdx = nt; dirty = true; }
            GUILayout.EndHorizontal();


            GUILayout.Space(4);
            if (GUILayout.Button("算这一步（改完会自动算）", GUILayout.Height(30))) dirty = true;
            if (GUILayout.Button("扫描当前机器 / 背包（结果写日志）", GUILayout.Height(24))) hint = ScanSafe();

            if (dirty)
            {
                dirty = false;
                Calc();
                Save();
            }

            GUILayout.Space(8);
            if (plan != null && plan.feed > 0)
            {
                if (hasMachine)
                    GUILayout.Label("新灌一瓶（容器 " + (Parse(sCap, 0.0)).ToString("0") + " mL · 进水 " + sPurity + "%）", t);
                GUILayout.Label("接 " + (plan.feed / 1000.0).ToString("0.0") + " mL", h1);
                string nightsText = plan.nights > 0 ? ("跑 " + plan.nights + " 晚") : "不用过夜";
                GUILayout.Label(nightsText + " · 出 " + (plan.output / 1000.0).ToString("0.0") + " mL · " +
                                (plan.purity / 100.0).ToString("0.00") + "%", t);
                string cost = "";
                if (plan.filterUsed > 0) cost += "过滤棉 -" + plan.filterUsed + " 点  ";
                if (plan.tablets > 0) cost += "最后放 " + plan.tablets + " 片药片";
                if (cost.Length > 0) GUILayout.Label(cost, t);
            }
            else if (plan != null)
            {
                GUILayout.Label("这个组合到不了目标：加性能/质量，或者少接一点水。", t);
            }
            var rec = Recommend((int)Math.Round(Parse(sPurity, 91.04) * 100), (long)Parse(sCap, 6000.0),
                                Parse(sPerf, 0.0), Parse(sQuality, 0.0), Targets[targetIdx]);
            if (!string.IsNullOrEmpty(rec)) GUILayout.Label(rec, t);
            if (!string.IsNullOrEmpty(hint)) GUILayout.Label(hint, t);
        }

        private void DrawBottles(GUIStyle h1, GUIStyle t)
        {
            GUILayout.BeginHorizontal();
            if (Time.realtimeSinceStartup - lastLive > 3f)
            {
                lastLive = Time.realtimeSinceStartup;
                try { var lb = GameBridge.LiveBottles(); if (lb.Count > 0) { bottles = lb; if (pickIdx >= bottles.Count) pickIdx = 0; } }
                catch (Exception e) { Logger.LogWarning("[live] " + e.Message); }
            }
            GUILayout.Label("身上 " + bottles.Count + " 瓶有水（实时读背包）", t);
            if (GUILayout.Button("重读背包", GUILayout.Width(96)))
            {
                bottles = GameBridge.LiveBottles();
                if (bottles.Count == 0) bottles = SaveReader.Load();
                pickIdx = 0;
            }
            GUILayout.EndHorizontal();

            if (bottles.Count == 0) { GUILayout.Label("没读到存档（先玩一次再进主菜单会写档）。", t); return; }

            for (int i = 0; i < bottles.Count; i++)
            {
                var b = bottles[i];
                bool on = i == pickIdx;
                string line = (on ? "▶ " : "   ") + b.name + "  " + (b.total / 1000.0).ToString("0") + " mL / " +
                              (b.cap / 1000.0).ToString("0") + " mL · " + (b.pur / 100.0).ToString("0.00") + "%";
                if (GUILayout.Button(line, t, GUILayout.Height(24))) { pickIdx = i; bottleMsg = ""; }
            }

            GUILayout.Space(4);
            if (GUILayout.Button("这瓶拿去净水器（一晚能到就一晚）", GUILayout.Height(26)))
            {
                if (bottles.Count == 0) bottleMsg = "身上没扫到有水的瓶子——先在「做一次水」页点「扫描当前机器/背包」。";
                else { UseBottle(true); bottleMsg = "已带入「做一次水」页：" + bottles[BankIdx()].name + " · " + (bottles[BankIdx()].pur / 100.0).ToString("0.00") + "%"; }
            }

            if (GUILayout.Button("用我最好的水，把这瓶兑到 99.00%", GUILayout.Height(26)))
            {
                if (bottles.Count == 0) bottleMsg = "身上没扫到有水的瓶子，先点「扫描当前机器/背包」。";
                else UseBestTo99();
            }
            if (!string.IsNullOrEmpty(bottleMsg)) GUILayout.Label(bottleMsg, t);
        }

        private void DrawDilute(GUIStyle h1, GUIStyle t, GUIStyle field)
        {
            GUILayout.Label("把好水兑普通水，刚好 99.00 不降档", h1);
            GUILayout.BeginHorizontal();
            GUILayout.Label("好水纯度 %", t, GUILayout.Width(110));
            dGood = Num(field, dGood, 80);
            GUILayout.Label("好水 mL", t, GUILayout.Width(70));
            dGoodVol = Num(field, dGoodVol, 80);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("普通水纯度 %", t, GUILayout.Width(110));
            dBad = Num(field, dBad, 80);
            GUILayout.Label("目标 %", t, GUILayout.Width(70));
            dTarget = Num(field, dTarget, 80);
            GUILayout.EndHorizontal();

            double p1 = Parse(dGood, 99.29), p2 = Parse(dBad, 98.00), ft = Parse(dTarget, 99.00);
            double vol = Parse(dGoodVol, 1000);
            GUILayout.Space(6);
            if (p1 > ft)
            {
                // 质量守恒：good·p1 + plain·p2 = (good+plain)·ft  →  plain/good = (p1-ft)/(ft-p2)
                double ratio = (p1 - ft) / (ft - p2);
                GUILayout.Label("每 1 mL 好水，最多兑 " + ratio.ToString("0.000") + " mL 普通水", h1);
                GUILayout.Label("你有 " + vol.ToString("0.0") + " mL 好水 → 最多兑 " + (vol * ratio).ToString("0.0") +
                                " mL 普通水，合计 " + (vol * (1 + ratio)).ToString("0.0") + " mL", t);
            }
            else
            {
                GUILayout.Label("好水纯度得高于目标，才兑得进普通水。", t);
            }
            GUILayout.Label("注意：兑完是 99.00% 的下限，越接近 99.01 越保险。", t);
        }

        private void DrawRecipes(GUIStyle h1, GUIStyle t)
        {
            GUILayout.Label("水质等级（低→高，按等级卖不按桶卖）", h1);
            GUILayout.Label("沟渠水 <90% · 铁锈水 90–95.99% · 幽灵水 96–97.99% · 基础水 98–98.99% · 优质水 99.00–99.89% · 纯水 99.90%+", t);
            GUILayout.Label("性能=一晚能清多少，质量=纯度上限（cap）。性能再高也顶不破质量定的上限。", t);

            GUILayout.Space(6);
            GUILayout.Label("一晚直出 99.01%（零模组，上限值）", h1);
            GUILayout.Label("沟渠水 88%：一晚最多 1830 mL → 99.01%", t);
            GUILayout.Label("沟渠水 88% + 1 片药片：一晚最多 1945 mL → 99.01%", t);
            GUILayout.Label("铁锈水 91%：一晚最多 1710 mL → 99.01%", t);
            GUILayout.Label("铁锈水 91% + 1 片药片：一晚最多 1860 mL → 99.01%", t);
            GUILayout.Label("铁锈水 95.8%：一晚最多 1830 mL → 99.01%", t);
            GUILayout.Label("幽灵水 97.5%：一晚最多 3240 mL → 99.01%", t);
            GUILayout.Label("基础水 / 优质水：12 L 以内一晚全到 99.01%", t);
            GUILayout.Label("纯水 99.90%：已高过裸机上限，不用过夜，直接卖", t);

            GUILayout.Space(6);
            GUILayout.Label("一晚直出 99.90%（质量 +100 才有的上限）", h1);
            GUILayout.Label("铁锈水 91%：一晚最多 1500 mL → 99.90%", t);
            GUILayout.Label("　　1650 mL 要 29% 性能；2000 mL 要 96% 性能", t);
            GUILayout.Label("沟渠水 88%：2000 mL 要 90% 性能 → 99.90%", t);
            GUILayout.Label("优质水 99.00%：6000 mL + 性能 1% → 99.90%", t);
            GUILayout.Label("基础水 98.5%：3000 mL 素水 → 99.90%", t);
            GUILayout.Label("质量 +75 封顶 99.80%：基础水 3.4 L、优质水 7.4 L", t);

            GUILayout.Space(6);
            GUILayout.Label("耗材怎么加", h1);
            GUILayout.Label("· ≥5 L 才上滤棉：小批量加棉反而少接 180–200 mL（削体积又拉低脏度，预算变小）", t);
            GUILayout.Label("· 大批量加棉收益大：幽灵水 3.2 L → 12 L、基础水 3 L → 8.8 L 都靠它", t);
            GUILayout.Label("· 药片只多接 100–370 mL，但它把水压更纯：2350 mL 铁锈水 + 1 片 → 99.28%（实测）", t);
            GUILayout.Label("· 1650 mL 铁锈水 → 一晚 99.01%（实测）；6000 mL 99.00% 优质水 + 质量 100 → 99.90%", t);
            GUILayout.Label("· 6 L 铁锈水 + 紫外灯 + 滤水器 + 2 片药片 → 质量 100 / 85 性能 → 5.6 L 纯水", t);
            GUILayout.Label("· 海德拉单台一次最多 7.5 L（耐久 75、每 100 mL 扣 1 点）；10 L 要分两晚或两台", t);

            GUILayout.Space(6);
            GUILayout.Label("上限档位（质量模组）", h1);
            GUILayout.Label("裸机 99.01% · +15–24 → 99.16% · +25–49 → 99.36% · +50–74 → 99.65%", t);
            GUILayout.Label("+75–99 → 99.80% · +100–199 → 99.90%（纯水门槛）· +200 以上 → 100.00%", t);

            GUILayout.Space(6);
            GUILayout.Label("顺序与坑", h1);
            GUILayout.Label("· 药片 / 紫外灯 / 滤水器都在净水器之前；净水器要最后用，且读的是本轮开始那一刻的属性", t);
            GUILayout.Label("· 药片每片溶 15 mL 氯、总处理 76.5 mL 污染物，化学清到占总量 0.7% 就停，多投有损耗", t);
            GUILayout.Label("· 紫外灯不耗电不耗耐久、只清微生物；净水器轮次没清掉东西不扣电（4 kJ/有效轮）", t);
            GUILayout.Label("· 集水器并行惩罚 ÷(1+0.25×(n−1))：2 台各 80%、3 台约 67%、4 台约 57%，宁做一台顶配", t);
            GUILayout.Label("· 净化前后体积不变；想减体积只能用蒸发器（质量 100 可全回收）", t);
            GUILayout.Label("· 卖之前把滤芯/滤水器取出，否则顾客拒收", t);

            GUILayout.Space(6);
            GUILayout.Label("水商利润（社区实测）", h1);
            GUILayout.Label("· 2000 mL 基础水买 80 → 卖 118（+47.5%），含加价小费 153（+91%）", t);
            GUILayout.Label("· 批发折扣 10–25% 是利润主源；屯水商 −25% 档实测卖 1500、约 +20 声望", t);
            GUILayout.Label("· 沟渠水直供 250（前置 300）无限取；铁锈水直供 350 再解锁，都是 0 成本水源", t);

            GUILayout.Label("数据来源：probablystolenguide / biligame 百科 / ludens 攻略 / B站 BV1fK4h6XE5C", t);
        }
        private void ResetAll()
        {
            sPurity = "91.04"; sCap = "6000"; sPerf = "0"; sQuality = "0";
            useFilter = true; useUv = false; tablets = 0; targetIdx = 0;
            dGood = "99.29"; dGoodVol = "1000"; dBad = "98.00"; dTarget = "99.00";
            dirty = true; Save();
        }

        private string Num(GUIStyle field, string cur, float w)
        {
            string nv = GUILayout.TextField(cur, field, GUILayout.Width(w));
            if (nv != cur) dirty = true;
            return nv;
        }

        private int BankIdx()
        {
            if (bottles.Count == 0) return 0;
            return Math.Max(0, Math.Min(pickIdx, bottles.Count - 1));
        }

        private void UseBottle(bool takeToPurifier)
        {
            if (bottles.Count == 0) return;
            var b = bottles[Math.Max(0, Math.Min(pickIdx, bottles.Count - 1))];
            if (takeToPurifier)
            {
                sPurity = (b.pur / 100.0).ToString("0.00");
                sCap = (b.cap / 1000.0).ToString("0");
                tab = 0; dirty = true; Save();
                hint = "已带入「" + b.name + "」：" + (b.total / 1000.0).ToString("0") + " mL / " + (b.pur / 100.0).ToString("0.00") + "%";
            }
            else
            {
                SaveReader.Ctr best = null;
                foreach (var c in bottles) if (c.total > 0 && (best == null || c.pur > best.pur)) best = c;
                if (best == null || best.pur <= 9900) hint = "身上没有比 99.00% 更高的水，兑不上去。";
                else
                {
                    long need = SaveReader.NeedGood(best, b);
                    if (need == 0) hint = "这瓶已经 ≥99.00%，不用兑。";
                    else if (need == -1) hint = "最好的那瓶不够纯。";
                    else if (need == -2) hint = "要兑 " + (need / 1000.0).ToString("0.0") + " mL 好水，这瓶装不下。";
                    else hint = "从「" + best.name + "」倒 " + (need / 1000.0).ToString("0.0") + " mL 进「" + b.name +
                                "」→ 约 " + ((b.total + need) / 1000.0).ToString("0.0") + " mL / 99.00%";
                }
                tab = 1;
            }
        }

        private void UseBestTo99()
        {
            UseBottle(false);
            bottleMsg = hint;
        }

        // ---- 后台命令通道用的静态入口（不经过鼠标键盘） ----
        public static bool ShowFlag { get { return Instance != null && Instance.show; } }
        public static void SetShow(bool v) { if (Instance != null) { Instance.show = v; Instance.Save(); } }
        public static void SetTab(int i) { if (Instance != null) { Instance.tab = Mathf.Clamp(i, 0, 3); Instance.Save(); } }
        /// <summary>后台通道：选中第 i 个方案并套用（等价于点击列表行）。</summary>
        public static string SelectPlan(int i)
        {
            if (Instance == null) return "no-instance";
            if (Instance.plans.Count == 0) return "no-plans";
            if (i < 0 || i >= Instance.plans.Count) i = 0;
            Instance.planSel = i; Instance.plansOpen = true;
            Instance.ApplyPlan(Instance.plans[i]);
            return "plan#" + (i + 1) + " " + PlanRow(Instance.plans[i]) + " | cap=" + Instance.sCap + " filter=" + Instance.useFilter + " uv=" + Instance.useUv + " tab=" + Instance.tablets;
        }

        public static void TogglePlans() { if (Instance != null) Instance.plansOpen = !Instance.plansOpen; }
        public static void SetTarget(int i) { if (Instance != null) { Instance.targetIdx = Mathf.Clamp(i, 0, Targets.Length - 1); Instance.dirty = true; Instance.Save(); } }
        public static string SetPurity(string v) { if (Instance == null) return "no-instance"; Instance.sPurity = v; Instance.dirty = true; Instance.Save(); return "purity=" + v; }
        public static string SetCap(string v) { if (Instance == null) return "no-instance"; Instance.sCap = v; Instance.dirty = true; Instance.Save(); return "cap=" + v; }
        public static string ScanNow()
        {
            if (Instance == null) return "no-instance";
            Instance.bottles = GameBridge.LiveBottles();
            Instance.dirty = true;
            return "bottles=" + (Instance.bottles == null ? 0 : Instance.bottles.Count);
        }
        public static string DumpScenePublic()
        {
            if (Instance == null) return "no-instance";
            Instance.DumpWaterScene();
            return "dumped";
        }

        private string Recommend(int purity, long capMl, double perf, double quality, int target)
        {
            int bestN = -1; long bestV = 0; int bestTab = 0;
            for (int n = 0; n <= 3; n++)
            {
                var pl = WaterMath.Solve(capMl * 1000L, purity, perf, quality, false, false, n, target, 1);
                if (pl.ok && pl.feed > bestV) { bestV = pl.feed; bestN = 1; bestTab = n; }
            }
            if (bestV <= 0) return "";
            return "";
        }

        private void Calc()
        {
            try
            {
                double purity = Parse(sPurity, 91.04);
                long capMl = (long)Parse(sCap, 6000.0);
                double perf = Parse(sPerf, 0.0);
                double quality = Parse(sQuality, 0.0);
                int target = Targets[targetIdx];
                plan = WaterMath.Solve(capMl * 1000L, (int)Math.Round(purity * 100), perf, quality,
                                       useFilter, useUv, tablets, target, 40);
                hint = plan.feed <= 0 ? "这个组合到不了目标，换个水源或加耗材。" : "";
                CalcNow();
            }
            catch (Exception e)
            {
                hint = "算出错：" + e.Message;
                Logger.LogWarning(e);
            }
        }

        private static double Parse(string s, double fallback)
        {
            double v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            if (double.TryParse((s ?? "").Trim(), out v)) return v;
            return fallback;
        }
    }
}















