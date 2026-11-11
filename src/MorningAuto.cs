using System;
using System.Reflection;
using UnityEngine;

#if DEV
namespace WaterCalcMod
{
    /// <summary>
    /// 早晨报告（StartOfDayUIManager，即那张「日历 / 夜间报告 / 状态」平板）自动收起。
    ///
    /// 玩家反馈：过夜/读档后它一直卡在屏幕上挡操作。这里在进入存档后监视它的可见性，
    /// 出现满 <see cref="GraceSeconds"/> 秒就调用游戏自己的 HideUI 关掉（不写档、不改数据），
    /// 关掉后进入冷却，避免和玩家主动打开的操作打架。
    /// </summary>
    public static class MorningAuto
    {
        const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        const float PollSeconds = 1f;      // 轮询间隔
        const float GraceSeconds = 5f;     // 出现多久后自动关
        const float CooldownSeconds = 30f; // 关过一次后的冷却

        static ManualLogSource log;
        static float nextPoll = 0f;
        static float shownAt = -1f;
        static float cooldownUntil = 0f;
        static int closedCount = 0;
        static float nextScan = 0f;
        static int paperClosed = 0;

        public static void Install(ManualLogSource l) { log = l; }

        static void Info(string s) { if (log != null) log.LogInfo("[morning] " + s); }

        public static void Watch()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll || now < cooldownUntil) return;
            nextPoll = now + PollSeconds;

            string step = "start";
            try
            {
                // 只在档内动手：主菜单是 EmporiumMenu，读档过程中也没有 StartOfDayUIManager。
                // 注意 PlayerStore.Instance 的 getter 在部分时机会抛 NRE，不能拿它做前置判断。
                step = "scene";
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (scene != "InventoryScene") { shownAt = -1f; return; }

                step = "instance";
                object inst = Instance("StartOfDayUIManager");
                if (inst == null) { shownAt = -1f; ScanPaperReport(); return; }

                step = "panel";
                GameObject panel = Panel(inst);
                if (panel == null || !panel.activeInHierarchy) { shownAt = -1f; ScanPaperReport(); return; }

                if (shownAt < 0f) { shownAt = now; return; }
                if (now - shownAt < GraceSeconds) return;

                step = "hide";
                GameBridge.Call(inst, "HideUI");
                shownAt = -1f;
                cooldownUntil = now + CooldownSeconds;
                closedCount++;
                Info("auto closed morning report #" + closedCount + " (after " + (int)GraceSeconds + "s)");
            }
            catch (Exception e)
            {
                shownAt = -1f;
                Exception inner = e is TargetInvocationException && ((TargetInvocationException)e).InnerException != null
                    ? ((TargetInvocationException)e).InnerException : e;
                if (log != null) log.LogWarning("[morning] watch fail at " + step + " " + e.GetType().Name + " inner=" + inner.GetType().Name + ": " + inner.Message);
            }
        }

        /// <summary>
        /// 第二道兜底：那张「日历 / 夜间报告 / 状态」平板不属于 StartOfDay/EndOfDay（pstate 实测
        /// StartOfDay=off EndOfDay=inst），只能按屏幕上的特征文字认它，认到就直接走
        /// PaperUIManager.CloseAllPaperUI 收掉（手里没开书时才动手，免得误关我们自己的手册）。
        /// </summary>
        static void ScanPaperReport()
        {
            if (log == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextScan) return;
            nextScan = now + 2f;

            try
            {
#if DEV
                // 早上自动关窗也曾经跟着 ctl.enable 开，玩家报「mod 会自动删开局日历界面」。
                // 改成只认独立的 auto.enable（默认不存在）⇒ 正常游玩时不再自动关任何窗口。
                if (!System.IO.File.Exists(System.IO.Path.Combine(CmdFile.Dir, "auto.enable"))) return;
#else
                return;   // 发布版：自动关夜报属开发功能
#endif
                object pum = Instance("PaperUIManager");
                if (pum == null) return;
                if (GameBridge.Get(pum, "currentBook") != null) return;   // 手册开着，别动

                bool found = false;
                var texts = Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>();
                for (int i = 0; i < texts.Length && i < 4000; i++)
                {
                    var t = texts[i];
                    if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                    string s = t.text;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf("夜间报告", StringComparison.Ordinal) >= 0 || s.IndexOf("店铺吸引力", StringComparison.Ordinal) >= 0)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return;

                string r = Core.ClosePaper();
                paperClosed++;
                Info("auto closed day report panel #" + paperClosed + " (" + r + ")");
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[morning] scan fail " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>ctl 用：morning / morning=now 立刻关一次，返回可见性。</summary>
        public static string Close(string arg)
        {
            try
            {
                object inst = Instance("StartOfDayUIManager");
                if (inst == null) return "morning -> no-instance";
                GameObject panel = Panel(inst);
                bool vis = panel != null && panel.activeInHierarchy;
                if (!vis && arg != "force") return "morning -> hidden already";
                GameBridge.Call(inst, "HideUI");
                bool vis2 = panel != null && panel.activeInHierarchy;
                if (arg == "off") { cooldownUntil = Time.realtimeSinceStartup + 99999f; return "morning -> auto=off visible=" + vis2; }
                if (arg == "on") { cooldownUntil = 0f; return "morning -> auto=on visible=" + vis2; }
                return "morning -> closed visible=" + vis2 + " auto=" + (Time.realtimeSinceStartup < cooldownUntil ? "off" : "on");
            }
            catch (Exception e) { return "morning -> ERR " + e.GetType().Name; }
        }

        static object PlayerStoreInstance() { return Instance("PlayerStore"); }

        static object Instance(string typeName)
        {
            return GameBridge.GetStatic(typeName, "Instance");
        }

        static GameObject Panel(object inst)
        {
            object o = GameBridge.Get(inst, "UIPanel");
            if (o is GameObject) return (GameObject)o;
            if (o is Component) return ((Component)o).gameObject;
            return null;
        }
    }
}
#endif
