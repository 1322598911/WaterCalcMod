using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace WaterCalcMod
{
    // 后台命令通道：外部脚本写 ctl.txt，插件每 0.25 秒读一次并执行，全程不碰鼠标键盘。
    public static class CmdFile
    {
#if DEV
        // 开发联调目录：环境变量 WATERCALC_DEV_DIR 指定；不设则用系统临时目录下的 watercalc-dev。
        // 发布版（Release）根本用不到它（整段被 #if DEV 剥掉）。
        public static readonly string Dir = DevDirOf();
        static string DevDirOf()
        {
            string e = Environment.GetEnvironmentVariable("WATERCALC_DEV_DIR");
            if (!string.IsNullOrEmpty(e)) return e;
            return Path.Combine(Path.GetTempPath(), "watercalc-dev");
        }
#else
        // 发布版：不带开发机绝对路径（上架清单不含 files 能力）
        public const string Dir = "";
#endif
#if DEV
        static string InPath { get { return Path.Combine(Dir, "ctl.txt"); } }
        static string OutPath { get { return Path.Combine(Dir, "ctl.out"); } }
#endif

        static ManualLogSource log;
        static float nextPoll;

        public static void Install(ManualLogSource l)
        {
            log = l;
#if DEV
            AutoLoad.Install(l);
#endif
            WaterBook.Install(l);
            MachineMarker.Install(l);
#if DEV
            // 测试用「早上自动关纸面窗口」与「自动进存档」：正式版不需要（玩家要求）。
            MorningAuto.Install(l);
            AutoJoin.Install(l);
#endif
#if DEV
            try
            {
                if (!System.IO.Directory.Exists(Dir)) System.IO.Directory.CreateDirectory(Dir);
                if (File.Exists(InPath)) File.Delete(InPath);
                File.WriteAllText(OutPath, "ready " + DateTime.Now.ToString("HH:mm:ss") + "\n", Encoding.UTF8);
            }
            catch (Exception e) { if (log != null) log.LogWarning("[cmd] 初始化失败 " + e.Message); }
#endif
        }

        public static void Tick()
        {
#if DEV
            if (log == null) return;
            // 测试开关：只有 _tools\ctl.enable 存在时才响应命令通道。
            // 正式玩的时候把这个文件删掉，插件就不会读任何外部指令；需要联调时再建回来。
            try
            {
                if (!File.Exists(Path.Combine(Dir, "ctl.enable"))) return;
            }
            catch (Exception) { return; }
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 0.25f;
            try
            {
                if (!File.Exists(InPath)) return;
                string text = File.ReadAllText(InPath, Encoding.UTF8).Trim();
                File.Delete(InPath);
                if (text.Length == 0) return;
                foreach (var raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string res;
                    try { res = Run(line); }
                    catch (Exception e) { res = "ERR " + e.GetType().Name + ": " + e.Message; }
                    log.LogInfo("[cmd] " + line + " -> " + res);
                    try { File.AppendAllText(OutPath, line + " -> " + res + "\n", Encoding.UTF8); } catch { }
                }
            }
            catch (Exception e) { log.LogWarning("[cmd] 轮询失败 " + e.Message); }
        #endif
}

#if DEV
        static string Run(string line)
        {
            if (line == "rust") return Core.TryFill(true);
            if (line == "gutter") return Core.TryFill(false);
            if (line == "empty") return Core.EmptyBottle();
            if (line == "endday") return Core.EndDay();
            if (line == "closeday") return Core.CloseDay();
            if (line == "paper") return Core.ClosePaper();
            if (line == "night") return Core.SkipNight();
            if (line == "pstate") return Core.PaperState();
#if DEV
            // 早晨报告（日历/夜间报告/状态 那张平板）：morning / morning=force / morning=off / morning=on
            if (line == "morning" || line.StartsWith("morning=")) return MorningAuto.Close(line.IndexOf('=') >= 0 ? line.Substring(line.IndexOf('=') + 1).Trim() : "");
            if (line == "autojoin" || line.StartsWith("autojoin=")) return AutoJoin.Cmd(line.IndexOf('=') >= 0 ? line.Substring(line.IndexOf('=') + 1).Trim() : "");
#endif
            if (line == "bal?") return PurifierUi.BalanceState();
            if (line == "bal") return PurifierUi.ToggleBalancePublic();
#if DEV
            if (line == "rctest") return ContextHook.SelfTest();
#endif
            if (line == "show") { return "show " + PurifierUi.ShowMain(true) + " native=1"; }
            if (line == "hide") { return "hide " + PurifierUi.ShowMain(false); }
            if (line == "plans") { PurifierUi.Rebuild(true); return PurifierUi.PlanSummary(); }
            if (line.StartsWith("early=")) return PurifierUi.EarlyPublic(int.Parse(line.Substring(6)));
            if (line == "perf?") return "perf -> " + PurifierUi.PerfOverride;
            if (line == "bonus") return PurifierUi.BonusReport();
            if (line == "machines") return PurifierUi.MachinesReport();
            // 注意：这里不要顺手 MachineMarker.Sync。描边每帧由 PurifierUi.Tick 重算，
            // 同一次 ctl 批次里立刻 Sync 会先给旧台挂一个、下一帧再给新台挂一个，mark? 会读到 count=2。
            // 想知道切换结果，把 `mark?` 单独发一批（隔一帧）即可。
            if (line.StartsWith("mach=")) { PurifierUi.MachineOverride = int.Parse(line.Substring(5)); PurifierUi.RebuildPublic(); return "mach -> " + PurifierUi.MachineOverride; }
            if (line.StartsWith("perf=")) { PurifierUi.PerfOverride = int.Parse(line.Substring(5)); PurifierUi.RebuildPublic(); return "perf -> " + PurifierUi.PerfOverride; }
            if (line.StartsWith("sim?")) return PurifierUi.SimPublic(line.Substring(4));
            if (line == "tap?") return PurifierUi.TapReport();
            if (line.StartsWith("uncap=")) { PurifierUi.UncapEff = line.Substring(6).Trim() == "1"; PurifierUi.RebuildPublic(); return "uncap -> " + (PurifierUi.UncapEff ? 1 : 0); }
            if (line.StartsWith("taplock=")) { PurifierUi.ForceTapLocked = int.Parse(line.Substring(8)); PurifierUi.RebuildPublic(); return "taplock -> " + PurifierUi.ForceTapLocked; }
            if (line.StartsWith("pre=")) return PurifierUi.SetUiPublic("pre", int.Parse(line.Substring(4)));
            if (line.StartsWith("post=")) return PurifierUi.SetUiPublic("post", int.Parse(line.Substring(5)));
            if (line.StartsWith("mode=")) return PurifierUi.SetUiPublic("mode", int.Parse(line.Substring(5)));
            if (line.StartsWith("tgt=")) return PurifierUi.SetUiPublic("target", int.Parse(line.Substring(4)));
            if (line == "tab0") { PurifierUi.ShowMain(true); return "tab=0 native"; }
            if (line == "tab1") { Plugin.SetTab(1); return "tab=1"; }
            if (line == "tab2") { Plugin.SetTab(2); return "tab=2"; }
            if (line == "tab3") { Plugin.SetTab(3); return "tab=3"; }
            if (line == "target0") { Plugin.SetTarget(0); return "target=99.00"; }
            if (line == "target1") { Plugin.SetTarget(1); return "target=99.90"; }
            if (line == "scan") return Plugin.ScanNow();
            if (line == "show?") return "show=" + (Plugin.ShowFlag ? "true" : "false") + " mainVisible=" + PurifierUi.MainVisible() + " native=1";
            if (line == "pwin") return PurifierUi.ShowTestWindow(true);
            if (line == "pwin0") return PurifierUi.ShowTestWindow(false);
            if (line == "iconwin") return PurifierUi.IconWindow();
            if (line == "diag") return PurifierUi.Diag();
            if (line.StartsWith("sprites=")) return PurifierUi.Sprites(line.Substring("sprites=".Length));
            if (line == "ids") return PurifierUi.DumpIds();
            if (line == "live") return PurifierUi.LiveList();
            if (line == "scanall") return PurifierUi.ScanAllPublic();
            if (line == "icons") return PurifierUi.IconsReport();
            if (line == "win=recipe") return PurifierUi.OpenRecipePublic();
            if (line.StartsWith("recipe="))
            {
                int pg;
                if (int.TryParse(line.Substring("recipe=".Length), out pg)) return PurifierUi.RecipePagePublic(pg);
            }
            // 原生说明书（游戏自己的书页 prefab）：book / book=3 / book0 / book?
            if (line == "book" || line == "book0" || line == "book?" || line == "bookm" || line.StartsWith("book=")) return WaterBook.Cmd(line);
            if (line == "slots" || line == "slots=0") return MachineMarker.Cmd(line);
            if (line.StartsWith("mark")) return MachineMarker.Cmd(line);
            // 验证描边用：pair=N 直接显示第 N 台净水器的窗口、pairh 全部收起（不注入鼠标键盘）
            if (line.StartsWith("pair=")) return PurifierHook.ShowPair(int.Parse(line.Substring(5)));
            if (line.StartsWith("pairh=")) return PurifierHook.HidePair(int.Parse(line.Substring(6)));
            if (line == "pairh") return PurifierHook.HidePairs();
            if (line == "win=detail") return PurifierUi.OpenDetailPublic();
            if (line == "win=slim") return PurifierUi.OpenSlimPublic();
            if (line.StartsWith("page=")) return PurifierUi.PagePublic(int.Parse(line.Substring(5)));
            if (line == "cfg") { PurifierUi.OpenSettings(); return "cfg -> ok"; }
            if (line == "srcwin") { PurifierUi.OpenSourcePicker(); return "srcwin -> ok"; }
            if (line == "steps") { PurifierUi.OpenSteps(); return "steps -> ok"; }
            if (line == "steps?") return PurifierUi.StepsTextPublic();
            if (line == "tab?") return PurifierUi.TabletProbe();
            if (line.StartsWith("stage?")) return PurifierUi.StagePublic(line.Substring(6));
            if (line == "gs?") return PurifierUi.GraphUtilsProbe();
            if (line == "pos?") return PurifierUi.PosPublic();
            if (line == "icons?") return PurifierUi.IconsPublic();
            if (line == "title?") return PurifierUi.TitlePublic();
            if (line == "uitree") return PurifierUi.UiTreePublic();
            if (line == "assert?") return PurifierUi.AssertPublic();
            if (line == "nights?") return PurifierUi.NightsPublic();
            if (line == "ceiling?") return PurifierUi.CeilingPublic();
            if (line.StartsWith("filt?")) return PurifierUi.FiltPublic(line.Substring(5));
            if (line == "mix?") return PurifierUi.MixPlanPublic();
            if (line == "tcase?") return PurifierUi.TabletCasePublic();
            if (line == "watch?") return PurifierUi.WatchNow();
            if (line.StartsWith("move=")) return PurifierUi.MovePublic(line.Substring(5));
            if (line == "items?") return PurifierUi.ItemsProbe();
            if (line == "mods?") return PurifierUi.ModsProbe();
            if (line == "pw?") return PurifierUi.PwProbe();
            // 诊断：把当前候选方案与筛选结果原样打出来（查「前置+后置筛空」用）
            if (line == "srcplan") return PurifierUi.SrcPlanPublic();
            if (line == "src?") return PurifierUi.SrcPublic();
            if (line.StartsWith("src=")) return PurifierUi.SetSrcPublic(line.Substring(4));
            // 网页的「自己填纯度」：pur=9096 直接按这个值算，pur=-1 关掉
            if (line.StartsWith("pur=")) return PurifierUi.SetCustomPurPublic(line.Substring(4));
            // pick 只有这一个入口：pick=N / pick=auto 选方案行；pick=<键>=<值> 仍按旧语法设下拉。
            // 以前这里有三份重名分支，第一份（Plugin.SelectPlan）把后两份全挡掉了，pick=auto 直接 FormatException。
            if (line.StartsWith("pick="))
            {
                string pickArg = line.Substring(5);
                if (pickArg.IndexOf('=') >= 0)
                {
                    string[] kv = pickArg.Split('=');
                    if (kv.Length == 2) return PurifierUi.SetUiPublic(kv[0], int.Parse(kv[1]));
                }
                return PurifierUi.PickPublic(pickArg);
            }
            if (line == "pur?") return PurifierUi.CustomPurPublic();
            if (line == "tap?") return PurifierUi.TapPublic();
            if (line == "combos?") return PurifierUi.CombosPublic();
            // 诊断：列出参与凑量的容器（含 1 mL / 10 mL 滴管），查「滴管有没有被认出来」
            if (line == "cups?" || line == "cups") return PurifierUi.CupsPublic();
            if (line == "plan?" || line == "plan") return PurifierUi.PlanPublic();
            if (line == "steps") return PurifierUi.StepsPublic();
            if (line == "fillpreview") return PurifierUi.MixPreview();
            if (line == "win?") return PurifierUi.WinReport();
            if (line.StartsWith("fill=")) return PurifierUi.FillIndex(int.Parse(line.Substring(5)));
            if (line == "fill") return PurifierUi.OnFillPublic();
            if (line == "prepare") return PurifierUi.OnPreparePublic();
            if (line == "tablet") return PurifierUi.OnTabletPublic();
            // 下拉项与质量覆盖：无鼠标时也能把 F9 状态设成任意值再核对数值
            if (line == "qual?") return "qual -> " + PurifierUi.QualityOverride;
            if (line.StartsWith("qual="))
            {
                PurifierUi.QualityOverride = int.Parse(line.Substring(5));
                PurifierUi.RebuildPublic();
                return "qual -> " + PurifierUi.QualityOverride;
            }
            if (line == "perf?") return "perf -> " + PurifierUi.PerfOverride;
            if (line.StartsWith("perf=")) { PurifierUi.PerfOverride = int.Parse(line.Substring(5)); PurifierUi.RebuildPublic(); return "perf -> " + PurifierUi.PerfOverride; }
            if (line == "items") return PurifierUi.ItemsDump();
            if (line == "deep?") return GameBridge.DeepReport();
            if (line == "hit") return PurifierUi.HitTest();
            if (line.StartsWith("hit=")) return PurifierUi.HitTestAt(line.Substring(4));
            if (line == "mix") return PurifierUi.OnMixPublic();
            if (line == "mixpreview") return PurifierUi.MixPreview();
            if (line.StartsWith("mode=")) { PurifierUi.Mode = int.Parse(line.Substring(5)); PurifierUi.Rebuild(true); return "mode=" + PurifierUi.Mode; }
            if (line.StartsWith("ntarget=")) { PurifierUi.TargetIdx = int.Parse(line.Substring(8)); PurifierUi.Rebuild(true); return "target=" + PurifierUi.TargetIdx; }
            if (line == "dump") return Plugin.DumpScenePublic();
            if (line.StartsWith("purity=")) return Plugin.SetPurity(line.Substring("purity=".Length));
            if (line.StartsWith("cap=")) return Plugin.SetCap(line.Substring("cap=".Length));
            // 后台读档（只读，不写档）：load / load=latest / load=2 / load=slot:2；saves 只列候选；load? 看状态
#if DEV
            if (line == "load?") return AutoLoad.Status();
            if (line == "saves") return AutoLoad.RunList();
            if (line == "load" || line.StartsWith("load=")) return AutoLoad.Run(AutoLoad.ArgOf(line));
#endif
            return "unknown";
        }
#endif
    }

    // 直接调游戏 API，不经过任何输入设备
    public static class Core
    {
        // 清空「当前机器储水容器里那瓶」，用来复验 fill 的整瓶可达量。
        public static string EmptyBottle()
        {
            object bottle = PurifierHook.CurrentBottle();
            if (bottle == null) return "empty -> no-bottle";
            int before = GameBridge.IntOf(bottle, "GetTotalVolume");
            GameBridge.StatVoid("WaterHelper", "EmptyContainer", bottle);
            int after = GameBridge.IntOf(bottle, "GetTotalVolume");
            return "empty -> " + before + " -> " + after;
        }

        public static string TryFill(bool rust)
        {
            object bottle = PurifierHook.CurrentBottle();
            if (bottle == null) return "no-bottle";
            GameBridge.StatVoid("WaterHelper", "EmptyContainer", bottle);
            GameBridge.StatVoid("WaterHelper", rust ? "FillWithRustWater" : "FillWithGutterflow", bottle);
            int pur = GameBridge.IntOf(bottle, "GetWaterPurity");
            int vol = GameBridge.IntOf(bottle, "GetTotalVolume");
            return (rust ? "rust " : "gutter ") + pur + "@" + vol;
        }

        // 诊断：屏幕上那块「日历/夜间报告/状态」到底归哪个 manager，谁还亮着。
        public static string PaperState()
        {
            var sb = new System.Text.StringBuilder();
            object pum = GameBridge.GetStatic("PaperUIManager", "Instance");
            sb.Append("pum=" + (pum == null ? "no" : "yes"));
            if (pum != null)
            {
                object cur = GameBridge.Get(pum, "currentBook");
                sb.Append(" book=" + (cur == null ? "none" : "yes"));
                try
                {
                    Component c = pum as Component;
                    if (c != null)
                    {
                        var tr = c.transform.parent != null ? c.transform.parent : c.transform;
                        sb.Append(" root=" + tr.name);
                        for (int i = 0; i < tr.childCount && i < 12; i++)
                        {
                            var ch = tr.GetChild(i);
                            sb.Append(" |" + ch.name + (ch.gameObject.activeSelf ? "+" : "-"));
                        }
                    }
                }
                catch (Exception) { }
            }
            foreach (string tn in new string[] { "StartOfDayUIManager", "EndOfDayUIManager" })
            {
                object inst = GameBridge.GetStatic(tn, "Instance");
                sb.Append(" " + tn.Replace("UIManager", "") + "=");
                if (inst == null) { sb.Append("no"); continue; }
                object uo = GameBridge.Get(inst, "UIPanel");
                GameObject go = uo as GameObject;
                if (go == null && uo is Component) go = ((Component)uo).gameObject;
                sb.Append(go == null ? "inst" : (go.activeInHierarchy ? "ON(" + go.name + ")" : "off"));
            }
            return "pstate -> " + sb;
        }

        // 屏幕上那张「日历 / 夜间报告 / 状态」平板属于纸面 UI：直接走 PaperUIManager.CloseAllPaperUI。
        public static string ClosePaper()
        {
            try
            {
                object inst = GameBridge.GetStatic("PaperUIManager", "Instance");
                if (inst == null) return "paper -> no-instance";
                GameBridge.Call(inst, "CloseAllPaperUI");
                return "paper -> closed";
            }
            catch (Exception e) { return "paper -> ERR " + e.GetType().Name + ": " + e.Message; }
        }

        // F9 调试面板里的「Skip Night (Machines Only)」：只跑机器结算、不推日期。
        // 用它测「一晚净化多少」只需一秒，不用真的过夜。
        public static string SkipNight()
        {
            try
            {
                DebugPanel.SkipNightMachine();
                return "night -> skipped";
            }
            catch (Exception e) { return "night -> ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static string CloseDay()
        {
            object inst = GameBridge.GetStatic("EndOfDayUIManager", "Instance");
            if (inst == null)
            {
                // 画面上那张「日历 / 夜间报告 / 状态」平板在过夜后是 StartOfDayUIManager（早晨报告），
                // 此时 EndOfDayUIManager.Instance 为 null，必须退回去关早晨报告。
                string mr = HideMorningReport();
                return mr != null ? ("closeday -> morning " + mr) : "no-instance";
            }
            // 玩家点 ✕ 走的是 CloseUI(true)；传 false 会在 DisableBackingBlock 后直接 return，
            // 不会 RestorePaperUIClosedForSleep，纸面平板关不掉（m03744 实测）。
            GameBridge.Call(inst, "CloseUI", true);
            try
            {
                object s = GameBridge.GetStatic("StoreUIManager", "Instance");
                if (s != null) GameBridge.Call(s, "DisableBackingBlock");
            }
            catch (Exception) { }
            bool ui = false, paper = false;
            try
            {
                object uo = GameBridge.Get(inst, "UI");
                if (uo is GameObject) ui = ((GameObject)uo).activeSelf;
            }
            catch (Exception) { }
            try
            {
                object po = GameBridge.GetStatic("PaperUIManager", "Instance");
                if (po != null)
                {
                    // 屏幕上那张带「日历 / 夜间报告 / 状态」的平板是纸面 UI，必须走它的关闭入口。
                    GameBridge.Call(po, "CloseAllPaperUI");
                    paper = ((Component)po).gameObject.activeSelf;
                }
            }
            catch (Exception) { }
            string msg = "closeday -> ui=" + (ui ? "True" : "False") + " paper=" + (paper ? "True" : "False");
            string m2 = HideMorningReport();
            if (m2 != null) msg += " morning=" + m2;
            return msg;
        }

        // 早晨报告（StartOfDayUIManager）：过夜后玩家看到的那张平板就是它。
        private static string HideMorningReport()
        {
            try
            {
                object inst = GameBridge.GetStatic("StartOfDayUIManager", "Instance");
                if (inst == null) return null;
                GameBridge.Call(inst, "HideUI");
                object uo = GameBridge.Get(inst, "UIPanel");
                bool vis = uo is GameObject ? ((GameObject)uo).activeSelf : false;
                return "hidden ui=" + (vis ? "True" : "False");
            }
            catch (Exception)
            {
                return null;
            }
        }
        public static string EndDay()
        {
            object inst = GameBridge.GetStatic("EndOfDayUIManager", "Instance");
            if (inst == null) return "no-instance";
            GameBridge.Call(inst, "OpenUI");
            GameBridge.Call(inst, "OnEndDayButtonClick");
            return "endday";
        }
    }
}


