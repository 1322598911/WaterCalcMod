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
            try
            {
                if (__1 == null) return;
                PurifierUi.LogTip(__1);                      // 无条件：证明 postfix 真的进来了
                if (__0 == null) return;
                if (__1.identifier != "water_purifier") return;
                string line = PurifierUi.TooltipLine(__1);
                if (string.IsNullOrEmpty(line)) return;
                __0.AddLine(line, true, (RenderHandler.ColorPalette)12092762,
                    false, false, false, false,
                    (RenderHandler.ColorPalette)(-1),
                    (RenderHandler.ColorPalette)(-1),
                    (RenderHandler.ColorPalette)(-1));
            }
            catch (Exception) { }
        }
    }
}
