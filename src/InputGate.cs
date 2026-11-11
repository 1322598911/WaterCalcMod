using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WaterCalcMod
{
#if DEV
    /// <summary>输入隔离：鼠标压在 mod 面板上时，吞掉游戏的鼠标键事件。</summary>
    public static class InputGate
    {
        // 默认关掉：原生 UI 已用 watercalc_blocker_* 吞点击；再用 Input 钩子按旧 IMGUI 矩形拦鼠标键
        // 会误杀游戏窗口（玩家实测：打开详情后，之前开着的净水器窗口点不动，只能先关详情）。
        public static bool Enabled = false;
        private static ManualLogSource log;
        private static int blocked;

        public static void Install(ManualLogSource l)
        {
            log = l;
            try
            {
                var t = typeof(Input);
                string[] names = { "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseButton" };
                var pre = new HarmonyMethod(typeof(InputGate).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static));
                var h = new Harmony("dsh.watercalc.input");
                foreach (var n in names)
                {
                    var m = t.GetMethod(n, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                    if (m == null) { log.LogWarning("[gate] 没有 Input." + n); continue; }
                    h.Patch(m, prefix: pre);
                    log.LogInfo("[gate] 已挂 Input." + n);
                }
            }
            catch (Exception e) { log.LogWarning("[gate] 安装失败 " + e); }
        }

        public static bool Prefix(ref bool __result)
        {
            if (!Enabled) return true;
            try
            {
                if (Plugin.CursorInPanel())
                {
                    __result = false;
                    blocked++;
                    if (blocked == 1) log.LogInfo("[gate] 开始拦截面板内的鼠标键");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }
#endif
}
