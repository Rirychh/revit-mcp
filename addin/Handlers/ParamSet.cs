// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;

namespace RevitMcpAddin.Handlers
{
    /// <summary>參數寫入共用邏輯：數值單位 長度 mm、面積 m²、體積 m³、角度 度；字串交給 SetValueString。</summary>
    internal static class ParamSet
    {
        public static string Spec(Parameter p)
        {
            try
            {
                var t = p.Definition.GetDataType();
                if (t == SpecTypeId.Length) return "length";
                if (t == SpecTypeId.Area) return "area";
                if (t == SpecTypeId.Volume) return "volume";
                if (t == SpecTypeId.Angle) return "angle";
            }
            catch { }
            return "";
        }

        /// <summary>回傳 null 表示成功，否則是錯誤原因。</summary>
        public static string? Set(Parameter p, JToken v)
        {
            if (p.IsReadOnly) return "唯讀參數";
            switch (p.StorageType)
            {
                case StorageType.String: return p.Set(v.ToString()) ? null : "設定失敗";
                case StorageType.Integer:
                    if (v.Type == JTokenType.Boolean) return p.Set((bool)v ? 1 : 0) ? null : "設定失敗";
                    if (v.Type == JTokenType.Integer) return p.Set((int)v) ? null : "設定失敗";
                    return "要填整數或 true/false";
                case StorageType.ElementId:
                    return v.Type == JTokenType.Integer && p.Set(ElementIdCompat.NewId((long)v)) ? null : "要填元件 id（整數）";
                case StorageType.Double:
                    if (v.Type == JTokenType.String) return p.SetValueString((string)v!) ? null : "Revit 無法解析這個值";
                    if (v.Type != JTokenType.Integer && v.Type != JTokenType.Float) return "要填數字";
                    double d = (double)v; string s = Spec(p);
                    d = s == "length" ? d / 304.8 : s == "area" ? d / 0.09290304 : s == "volume" ? d / 0.028316846592 : s == "angle" ? d * Math.PI / 180 : d;
                    return p.Set(d) ? null : "設定失敗";
            }
            return "不支援的參數型別";
        }
    }
}
