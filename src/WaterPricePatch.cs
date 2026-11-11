using System;
using System.Reflection;
using HarmonyLib;

namespace WaterCalcMod
{
    /// <summary>
    /// 水价探针（Harmony Postfix），替代「硬编码 40 元/L」的猜测。
    ///
    /// 基础水价在游戏里写死在 WaterFeatureHelper.GetWaterPrice(int totalLiquidParts)：
    /// IL 是 40 * parts / 1000000 再 Math.Round。游戏更新或别的 mod 改了这个常数时，
    /// 这里用「游戏自己算出来的钱」反推每升价，界面就不会报错数。
    ///
    /// 对**游戏代码**打补丁不需要任何能力（官方 APIChanges.zh-CN：补丁目标限定在
    /// 游戏程序集 / 自己程序集 / 前置 mod / UI 三命名空间时免费），所以：
    ///   · 用 ModReflect.Target 取 MethodBase（不触发字符串反射扫描）；
    ///   · 不声明 reflection-external / native / dynamic-code / threads。
    /// 本游戏不自动 PatchAll，所以 Install 里自己挂。
    /// </summary>
    internal static class WaterPricePatch
    {
        internal static bool Ready;
        internal static long LastParts, LastPrice;

        internal static void Install(ManualLogSource log)
        {
            try
            {
                var mb = ModReflect.Target("WaterFeatureHelper", "GetWaterPrice",
                                           new Type[] { typeof(int) });
                if (mb == null)
                {
                    if (log != null) log.LogWarning("水价探针：找不到 WaterFeatureHelper.GetWaterPrice(int)，沿用 40 元/L");
                    return;
                }
                var post = typeof(WaterPricePatch).GetMethod("Post",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var h = new Harmony("dsh.watercalc.price");
                h.Patch(mb, null, new HarmonyMethod(post));
                Ready = true;
                if (log != null) log.LogInfo("水价探针已挂上（GetWaterPrice(int) Postfix）");
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("水价探针安装失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>游戏算完一笔水价后，用 parts/钱数反推「基础水价 元/升」。</summary>
        static void Post(int totalLiquidParts, ref int __result)
        {
            try
            {
                if (totalLiquidParts <= 0 || __result <= 0) return;
                LastParts = totalLiquidParts;
                LastPrice = __result;
                double perL = __result * 1000000.0 / totalLiquidParts;
                if (perL >= 5 && perL <= 500) WaterEconomy.LiveBasePerLitre = perL;
            }
            catch (Exception) { }
        }

        internal static string Status()
        {
            string s = Ready ? "探针 ON" : "探针 OFF";
            s += " · 基础 " + WaterEconomy.Money(WaterEconomy.LiveBasePerLitre) + " 元/L";
            if (LastParts > 0) s += " · 最近 " + (LastParts / 1000) + " mL 卖 " + LastPrice + " 元";
            return s;
        }
    }
}
