using System;
using HarmonyLib;

namespace WaterCalcMod
{
    /// <summary>
    /// 右键菜单探针 + 右击「使用」拦截：
    /// 挂钩 ItemContextHandler.TriggerButton，记录玩家点了哪个菜单项、点的是哪件道具、手上拿着什么。
    /// 入口依据 _tools\rightclick_notes.md。
    ///
    /// 官方接口迁移（2026-11-11）：成员读取全部走 GameBridge.Get/Set（内部 ModReflect），
    /// 补丁目标用 ModReflect.Target 取 MethodBase；不再持有 FieldInfo、不再 GetMethods 扫描，
    /// 这样工坊扫描器不会判 reflection-external。
    /// </summary>
    public static class ContextHook
    {
        private static ManualLogSource log;
        private static bool installed;
        private static int hits;

        public static void Install(ManualLogSource logger)
        {
            log = logger;
            try
            {
                var m = GameBridge.Target("ItemContextHandler", "TriggerButton", new Type[] { typeof(RichTextElement) });
                if (m == null) m = GameBridge.Target("ItemContextHandler", "TriggerButton", new Type[] { typeof(object) });
                if (m == null)
                {
                    log.LogWarning("[ctx] 找不到 ItemContextHandler.TriggerButton");
                    return;
                }

                var hm = new Harmony("dsh.watercalc.context");
                hm.Patch(m,
                    prefix: new HarmonyMethod(typeof(ContextHook).GetMethod("Prefix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)),
                    postfix: new HarmonyMethod(typeof(ContextHook).GetMethod("Postfix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                installed = true;
                log.LogInfo("[ctx] 已挂 ItemContextHandler.TriggerButton（prefix 接管 + postfix 记录）");
            }
            catch (Exception e) { log.LogWarning("[ctx] 安装失败 " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>返回 false = 吞掉这次点击（已按今日水源灌到达标体积）。</summary>
        public static bool Prefix(object __instance, object button)
        {
            if (!installed || __instance == null) return true;
            try
            {
                object use = GameBridge.Get(__instance, "buttonUse");
                if (use == null) return true;
                if (!ReferenceEquals(use, button)) return true;                      // 不是「使用」
                if (GameBridge.Get(__instance, "currentInventory") != null) return true; // 排序菜单，放行

                object item = GameBridge.Get(__instance, "currentItem");
                if (item == null) return true;
                if (GameBridge.IntOf(item, "GetTotalCapacity") <= 0) return true;    // 不是液体容器
                if (!HasVisiblePurifier()) return true;                              // 没开净水器，保持原行为

                PurifierUi.RightClickTarget = item;
                string msg;
                try { msg = PurifierUi.OnFillPublic(); }
                finally { PurifierUi.RightClickTarget = null; }

                GameBridge.Call(__instance, "OnEventReset");
                log.LogInfo("[ctx] 右键使用→灌水 " + msg + " item=" + GameBridge.NameOfItem(item));
                return false;
            }
            catch (Exception e)
            {
                PurifierUi.RightClickTarget = null;
                log.LogWarning("[ctx] prefix 异常，放行原逻辑 " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        private static bool HasVisiblePurifier()
        {
            try
            {
                var pairs = PurifierHook.Pairs;
                if (pairs == null) return false;
                for (int i = 0; i < pairs.Count; i++)
                {
                    object w = pairs[i].Window;
                    if (w == null) continue;
                    object r = GameBridge.WinVisible(w) ? (object)true : null;
                    if (r is bool && (bool)r) return true;
                }
            }
            catch { }
            return false;
        }

        public static void Postfix(object __instance, object button)
        {
            if (!installed || __instance == null) return;
            try
            {
                object item = GameBridge.Get(__instance, "currentItem");
                object inv = GameBridge.Get(__instance, "currentInventory");
                string which = "other";
                if (ReferenceEquals(GameBridge.Get(__instance, "buttonUse"), button)) which = "use";
                else if (ReferenceEquals(GameBridge.Get(__instance, "buttonUnload"), button)) which = "unload";

                string id = GameBridge.NameOfItem(item);
                if (hits++ < 40)
                    log.LogInfo("[ctx] btn=" + which + " item=" + id + " inv=" + (inv != null));
            }
            catch (Exception e) { log.LogWarning("[ctx] postfix " + e.Message); }
        }

#if DEV
        /// <summary>无需鼠标的自检：把 currentItem 设为指定液体容器，再直接调 TriggerButton(buttonUse)。</summary>
        public static string SelfTest()
        {
            try
            {
                if (!installed) return "rctest -> not-installed";
                object inst = GameBridge.GetStatic("ItemContextHandler", "current");
                if (inst == null) return "rctest -> no-instance";

                object bottle = null;
                foreach (object it in GameBridge.LiveAllItems())
                {
                    if (GameBridge.IntOf(it, "GetTotalCapacity") > 0) { bottle = it; break; }
                }
                if (bottle == null) return "rctest -> no-bottle";
                GameBridge.Set(inst, "currentItem", bottle);

                // 自检需要一台「可见的净水器」，否则 prefix 会放行
                if (!HasVisiblePurifier())
                {
                    try
                    {
                        var pairs = PurifierHook.Pairs;
                        if (pairs != null && pairs.Count > 0 && pairs[0].Window != null)
                            GameBridge.Call(pairs[0].Window, "Show");
                    }
                    catch { }
                }

                object btn = GameBridge.Get(inst, "buttonUse");
                if (btn == null) return "rctest -> no-use-button";

                GameBridge.Call(inst, "TriggerButton", btn);
                return "rctest -> invoked item=" + GameBridge.NameOfItem(bottle) + " hits=" + hits;
            }
            catch (Exception e) { return "rctest -> FAIL " + e.GetType().Name + " " + e.Message; }
        }
#endif
    }
}
