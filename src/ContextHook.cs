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
                    prefix: new HarmonyMethod(typeof(ContextHook), "Prefix"),
                    postfix: new HarmonyMethod(typeof(ContextHook), "Postfix"));

                var uc = GameBridge.Target("ItemContextHandler", "UpdateConditions", Type.EmptyTypes);
                if (uc == null)
                {
                    log.LogWarning("[ctx] 找不到 ItemContextHandler.UpdateConditions，右键读取行不会出现");
                }
                else
                {
                    hm.Patch(uc, postfix: new HarmonyMethod(typeof(ContextHook), "UpdatePostfix"));
                    log.LogInfo("[ctx] 已挂 ItemContextHandler.UpdateConditions（追加/隐藏读取行）");
                }

                // 原生菜单的悬停/选中着色只走 UpdateSortSelectButtonColor；
                // 只追加行不挂这个 Postfix，鼠标指到「读取」时就不会有原生按钮的选中效果。
                var sc = GameBridge.Target("ItemContextHandler", "UpdateSortSelectButtonColor",
                                           new Type[] { typeof(RichTextElement) });
                if (sc == null)
                {
                    log.LogWarning("[ctx] 找不到 ItemContextHandler.UpdateSortSelectButtonColor，读取行无悬停色");
                }
                else
                {
                    hm.Patch(sc, postfix: new HarmonyMethod(typeof(ContextHook), "ColorPostfix"));
                    log.LogInfo("[ctx] 已挂 ItemContextHandler.UpdateSortSelectButtonColor（读取行悬停色）");
                }

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
                // 批次 35：先认原生菜单里追加的「读取水质」行；命中后不进入原使用/排序逻辑。
                if (ContextReadRow.IsOursOrMarked(button))
                {
                    object readItem = GameBridge.Get(__instance, "currentItem");
                    string readMsg = PurifierUi.ReadBottleToCalc(readItem);
                    GameBridge.Call(__instance, "Stop");
                    log.LogInfo("[ctx] 右键读取水质 " + readMsg);
                    return false;
                }

                object use = GameBridge.Get(__instance, "buttonUse");
                if (use == null) return true;
                if (!ReferenceEquals(use, button)) return true;                      // 不是「使用」
                if (GameBridge.Get(__instance, "currentInventory") != null) return true; // 排序菜单，放行

                object item = GameBridge.Get(__instance, "currentItem");
                if (item == null) return true;
                if (GameBridge.IntOf(item, "GetTotalCapacity") <= 0) return true;    // 不是液体容器
                if (!HasVisiblePurifier()) return true;                              // 没开净水器，保持原行为

                PurifierUi.RightClickTarget = item;
                PurifierUi.NoteBottlePick(item);
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

        /// <summary>原生菜单鼠标移动到某一行时，同步刷新「读取」行的文字颜色，保留原生选中效果。</summary>
        public static void ColorPostfix(RichTextElement __0,
                                        PixelWindow ___contextWindow,
                                        RenderHandler.ColorPalette ___textColor,
                                        RenderHandler.ColorPalette ___textSelectColor)
        {
            if (!installed) return;
            try
            {
                ContextReadRow.Color(___contextWindow, __0, ___textColor, ___textSelectColor, "读取");
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[ctx] 读取行悬停色异常 " + e.Message);
            }
        }

        /// <summary>
        /// 批次 35：原生右键菜单每次刷新时，在菜单 GridPixelElement 末尾追加/隐藏「读取」。
        /// 空瓶、非液体容器、没有菜单窗口时只隐藏旧行，不新增。
        /// </summary>
        public static void UpdatePostfix(GameItem ___currentItem,
                                          RichTextElement ___buttonOpen,
                                          RichTextElement ___buttonUnload,
                                          RichTextElement ___buttonPin,
                                          PixelWindow ___contextWindow,
                                          RenderHandler.ColorPalette ___textColor)
        {
            if (!installed) return;
            try
            {
                if (___currentItem == null)
                {
                    ContextReadRow.Hide(___contextWindow);
                    return;
                }

                long cap = 0, vol = 0;
                try
                {
                    cap = WaterHelper.GetTotalCapacity(___currentItem);
                    vol = WaterHelper.GetTotalVolume(___currentItem);
                }
                catch (Exception e)
                {
                    if (log != null) log.LogWarning("[ctx] 读取右键瓶子失败 " + e.Message);
                    ContextReadRow.Hide(___contextWindow);
                    return;
                }

                if (cap <= 0 || vol <= 0)
                {
                    ContextReadRow.Hide(___contextWindow);
                    return;
                }

                RichTextElement sample = ___buttonOpen ?? ___buttonUnload ?? ___buttonPin;
                ContextReadRow.Add(___contextWindow, sample, ___textColor, "读取");
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[ctx] 读取水质行异常 " + e.Message);
            }
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
