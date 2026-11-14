using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 批次 35：在游戏原生 ItemContextHandler 菜单末尾追加「读取」一行。
    /// 只做菜单行本身：UpdateConditions 负责按瓶子显示/隐藏，TriggerButton 负责点击入口。
    /// 行对象用条件弱表缓存，重复打开菜单只改文字/显隐，不再重复追加。
    /// </summary>
    internal static class ContextReadRow
    {
        private sealed class RowState
        {
            public string Caption;
            public RenderHandler.ColorPalette Color;
        }

        private const string RowMarker = "WaterCalcReadRow";

        private static readonly ConditionalWeakTable<RichTextElement, RowState> Rows =
            new ConditionalWeakTable<RichTextElement, RowState>();

        // 只按规格指定的 GridPixelElement 字段访问；不做动态类型扫描、不取 PropertyInfo。
        private static readonly FieldInfo Elements = AccessTools.Field(typeof(GridPixelElement), "childElements");
        private static readonly FieldInfo Inners = AccessTools.Field(typeof(GridPixelElement), "childInners");
        private static readonly FieldInfo Height = AccessTools.Field(typeof(GridPixelElement), "gridHeight");
        private static readonly FieldInfo Sizes = AccessTools.Field(typeof(GridPixelElement), "childMinimumSize");

        internal static bool IsOurs(RichTextElement row)
        {
            if (row == null) return false;
            RowState state;
            return Rows.TryGetValue(row, out state);
        }

        private static bool IsMarkedRow(RichTextElement row)
        {
            if (row == null || row.handler == null || row.handler.gameObject == null) return false;
            return row.handler.gameObject.name.StartsWith(RowMarker, StringComparison.Ordinal);
        }

        internal static bool IsOursOrMarked(object rowObject)
        {
            RichTextElement row = rowObject as RichTextElement;
            if (row == null) return false;
            if (IsOurs(row)) return true;
            if (!IsMarkedRow(row)) return false;
            RowState state;
            if (!Rows.TryGetValue(row, out state)) Rows.Add(row, new RowState());
            return true;
        }

        internal static void Add(PixelWindow window, RichTextElement sample,
                                 RenderHandler.ColorPalette color, string caption)
        {
            GridPixelElement grid = (window == null ? null : window.childElement as GridPixelElement);
            if (grid == null || grid.gridWidth != 1 || grid.gridHeight < 1 || grid.gridHeight > 30 ||
                sample == null || Elements == null || Inners == null || Sizes == null || Height == null)
            {
                return;
            }

            int gridHeight = grid.gridHeight;
            int width = sample.widthPixels;
            for (int i = 0; i < gridHeight; i++)
            {
                RichTextElement row = grid.GetElement(0, i) as RichTextElement;
                if (row == null) continue;
                if (IsOurs(row) || IsMarkedRow(row))
                {
                    RowState state;
                    if (!Rows.TryGetValue(row, out state)) Rows.Add(row, new RowState());
                    Paint(row, color, caption);
                    if (row.handler != null && row.handler.gameObject != null)
                        row.handler.gameObject.SetActive(true);
                    return;
                }
                width = Math.Max(width, row.widthPixels);
            }

            PixelElement[] elements = (PixelElement[])Elements.GetValue(grid);
            TreeNodeRender[] inners = (TreeNodeRender[])Inners.GetValue(grid);
            Array sizes = (Array)Sizes.GetValue(grid);
            if (elements == null || inners == null || sizes == null ||
                elements.Length != gridHeight || inners.Length != gridHeight || sizes.Length != gridHeight)
                return;

            RichTextElement newRow = new RichTextElement();
            newRow.handler.tag = "ContextMenuNode";
            newRow.handler.gameObject.name = RowMarker;
            Rows.Add(newRow, new RowState());
            Paint(newRow, color, caption);
            newRow.SetFixedSize(Math.Max(width, newRow.widthPixels), sample.heightPixels);

            TreeNodeRender node = TreeNodeRender.Instantiate().SetParent(grid.inner);
            PixelElement[] newElements = new PixelElement[gridHeight + 1];
            TreeNodeRender[] newInners = new TreeNodeRender[gridHeight + 1];
            Type sizeType = Sizes.FieldType.GetElementType();
            if (sizeType == null)
            {
                UnityEngine.Object.Destroy(newRow.handler.gameObject);
                UnityEngine.Object.Destroy(node.gameObject);
                return;
            }
            Array newSizes = Array.CreateInstance(sizeType, gridHeight + 1);
            Array.Copy(elements, newElements, gridHeight);
            Array.Copy(inners, newInners, gridHeight);
            Array.Copy(sizes, newSizes, gridHeight);
            newInners[gridHeight] = node;

            try
            {
                Elements.SetValue(grid, newElements);
                Inners.SetValue(grid, newInners);
                Sizes.SetValue(grid, newSizes);
                Height.SetValue(grid, gridHeight + 1);
                if (!grid.AttachPos(newRow, 0, gridHeight))
                    throw new InvalidOperationException("Cannot append read-water row.");
            }
            catch
            {
                Elements.SetValue(grid, elements);
                Inners.SetValue(grid, inners);
                Sizes.SetValue(grid, sizes);
                Height.SetValue(grid, gridHeight);
                UnityEngine.Object.Destroy(newRow.handler.gameObject);
                UnityEngine.Object.Destroy(node.gameObject);
                grid.Validate();
                throw;
            }
        }

        private static void Paint(RichTextElement row, RenderHandler.ColorPalette color, string caption)
        {
            RowState state;
            if (!Rows.TryGetValue(row, out state)) return;
            if (state.Caption == caption && state.Color == color) return;
            row.SetText(caption, 10000, color);
            state.Caption = caption;
            state.Color = color;
        }

        /// <summary>原生菜单悬停/选中行时，把「读取」行也刷成 textSelectColor；离开后回 textColor。</summary>
        internal static void Color(PixelWindow window, RichTextElement hovered,
                                   RenderHandler.ColorPalette idle, RenderHandler.ColorPalette active,
                                   string caption)
        {
            GridPixelElement grid = (window == null ? null : window.childElement as GridPixelElement);
            if (grid == null || grid.gridWidth != 1) return;
            for (int i = grid.gridHeight - 1; i >= 0; i--)
            {
                RichTextElement row = grid.GetElement(0, i) as RichTextElement;
                if (row == null || !(IsOurs(row) || IsMarkedRow(row))) continue;
                Paint(row, row == hovered ? active : idle, caption);
                return;
            }
        }

        internal static void Hide(PixelWindow window)
        {
            GridPixelElement grid = (window == null ? null : window.childElement as GridPixelElement);
            if (grid == null || grid.gridWidth != 1) return;
            for (int i = grid.gridHeight - 1; i >= 0; i--)
            {
                RichTextElement row = grid.GetElement(0, i) as RichTextElement;
                if (row == null || !(IsOurs(row) || IsMarkedRow(row))) continue;
                if (row.handler != null && row.handler.gameObject != null)
                    row.handler.gameObject.SetActive(false);
            }
        }
    }
}
