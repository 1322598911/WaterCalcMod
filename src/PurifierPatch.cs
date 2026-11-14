using System;
using HarmonyLib;

namespace WaterCalcMod
{
    /// <summary>
    /// 属性式补丁：Harmony 在 PatchAll 时按类型 + 方法名解析，参数是编译期强类型，
    /// 不会再出现 object[] __args 那种「装了但拿不到正确对象」的问题。
    /// 参考工坊 PurifierTomorrow 的 PurifierTooltipPatch。
    /// </summary>
    [HarmonyPatch(typeof(MachinePurifier), "CreateTooltip")]
    public static class PurifierTooltipPatch
    {
        private static void Postfix(RichTextBuilder __0, GameItem __1)
        {
            // 玩家 2026-11-11 定案：净水器物品标签上不加任何模组文案
            // （原「净水助手：今晚 → 99.01%（留 2036 mL）」那行已删）。补丁保留但什么都不做。
            return;
        }
    }
}
