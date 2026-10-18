// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

// 各版 Revit 的 ElementId API 差異補齊（C# 14 extension 屬性，不必改動既有呼叫端）：
//   Revit 2022–2023：沒有 ElementId.Value（long）→ 由 IntegerValue 補上
//   Revit 2026 起：IntegerValue 已移除 → 由 Value 補上（若該版仍有原生屬性，原生優先）
namespace Autodesk.Revit.DB
{
    public static class ElementIdCompat
    {
#if !REVIT2024_OR_GREATER
        extension(ElementId id)
        {
            public long Value => id.IntegerValue;
        }

        /// <summary>2022–2023 只有 ElementId(int)；統一用這個建立。</summary>
        public static ElementId NewId(long v) => new ElementId((int)v);
#else
        public static ElementId NewId(long v) => new ElementId(v);
#endif

#if REVIT2026_OR_GREATER
        extension(ElementId id)
        {
            public int IntegerValue => (int)id.Value;
        }
#endif
    }
}
