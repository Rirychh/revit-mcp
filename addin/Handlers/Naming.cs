// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 命名設定：%LOCALAPPDATA%\revit-mcp\naming.json（沒有這個檔就全部不套用）。
    /// {
    ///   "familyPrefix": "XX_",            新建族（build_family）的前綴
    ///   "profilePrefix": null,            輪廓族前綴；null 沿用 familyPrefix
    ///   "massPrefix": null,               量體族前綴；null 沿用 familyPrefix
    ///   "textType": "3mm 內文",            圖紙修訂說明的預設文字類型
    ///   "revisionCloudTextType": null,    雲形註記的預設文字類型；null 沿用 textType
    ///   "titleBlocks": { "圖框類型名": { "x":119, "y":42.5, "dy":9.8, "chars":18, "maxLines":3 } }
    ///                                     圖紙修訂說明寫入位置（mm，相對圖框插入點）
    /// }
    /// 檔案修改後下次呼叫即生效（依修改時間重讀）。
    /// </summary>
    internal static class Naming
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "revit-mcp", "naming.json");

        private static JObject _cfg = new JObject();
        private static DateTime _stamp = DateTime.MinValue;
        private static readonly object _lock = new object();

        private static JObject Cfg
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (!File.Exists(FilePath)) { _cfg = new JObject(); _stamp = DateTime.MinValue; return _cfg; }
                        var t = File.GetLastWriteTimeUtc(FilePath);
                        if (t != _stamp) { _cfg = JObject.Parse(File.ReadAllText(FilePath)); _stamp = t; }
                    }
                    catch { _cfg = new JObject(); }
                    return _cfg;
                }
            }
        }

        private static string? Str(string key)
        {
            var v = (string?)Cfg[key];
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        /// <summary>kind：family / profile / mass。名稱已帶前綴就不重複加。</summary>
        public static string Prefixed(string name, string kind = "family")
        {
            string? p = kind switch
            {
                "profile" => Str("profilePrefix") ?? Str("familyPrefix"),
                "mass" => Str("massPrefix") ?? Str("familyPrefix"),
                _ => Str("familyPrefix"),
            };
            if (p == null || string.IsNullOrEmpty(name) || name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return name;
            return p + name;
        }

        /// <summary>圖紙修訂說明的圖框座標：{ "圖框類型名": { x, y, dy, chars, maxLines } }（mm，相對圖框插入點）。</summary>
        public static JObject? TitleBlocks => Cfg["titleBlocks"] as JObject;

        public static string? TextType => Str("textType");
        public static string? RevisionCloudTextType => Str("revisionCloudTextType") ?? Str("textType");
    }
}
