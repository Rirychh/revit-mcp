// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 從 .rfa 檔載入族群到作用中專案。
    /// apply=false 預檢：檢查檔案存在、族群名稱是否已在專案裡，不開交易。
    /// overwrite=false（預設）：專案裡已有同名族群就跳過，不覆蓋既有參數值。
    /// </summary>
    public class FamilyLoadHandler
    {
        private readonly Document _doc;
        public FamilyLoadHandler(Document doc) => _doc = doc;

        // POST /families/load  { paths:["D:\\族群\\H型鋼梁.rfa"], folder?:"D:\\族群", overwrite:false, apply }
        public string Load(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false; bool overwrite = (bool?)r["overwrite"] ?? false;
            if (_doc.IsFamilyDocument) return Fail("作用中文件是族群檔，請切到專案再載入。");

            var paths = Strings(r["paths"]);
            string? folder = (string?)r["folder"];
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (!Directory.Exists(folder)) return Fail($"資料夾不存在：{folder}");
                paths.AddRange(Directory.GetFiles(folder!, "*.rfa", SearchOption.TopDirectoryOnly)
                    .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"\.\d{4}\.rfa$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return Fail("需要 paths 或 folder。");
            if (paths.Count > 200) return Fail($"一次最多 200 個檔（目前 {paths.Count}）。");

            var existing = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(Family)).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var plan = new List<object>(); var todo = new List<string>();
            foreach (var p in paths)
            {
                string fam = Path.GetFileNameWithoutExtension(p);
                if (!File.Exists(p)) { plan.Add(new { path = p, family = fam, action = "skip", reason = "檔案不存在" }); continue; }
                if (!p.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) { plan.Add(new { path = p, family = fam, action = "skip", reason = "不是 .rfa" }); continue; }
                bool exists = existing.Contains(fam);
                if (exists && !overwrite) { plan.Add(new { path = p, family = fam, action = "skip", reason = "專案已有同名族群（要覆蓋請帶 overwrite:true）" }); continue; }
                plan.Add(new { path = p, family = fam, action = exists ? "reload" : "load" });
                todo.Add(p);
            }
            if (!apply) return Ok(new { preflight = true, willLoad = todo.Count, overwrite, plan,
                note = "族群名稱以檔名判斷；實際名稱以檔案內為準。overwrite:true 會覆蓋專案裡既有族群的類型參數值。" });
            if (todo.Count == 0) return Ok(new { applied = true, loaded = 0, plan });

            var results = new List<object>(); int loadedCount = 0;
            var res = RunTx(_doc, "MCP Load Families", () =>
            {
                foreach (var p in todo)
                {
                    bool ok = _doc.LoadFamily(p, (overwrite ? FamilyLoad.Overwrite : FamilyLoad.SkipExisting), out Family fam);
                    if (ok) loadedCount++;
                    results.Add(new
                    {
                        path = p, loaded = ok, family = fam?.Name,
                        types = fam == null ? new List<string>() : fam.GetFamilySymbolIds().Select(id => _doc.GetElement(id)?.Name ?? "").ToList(),
                    });
                }
            });
            return res.committed
                ? Ok(new { applied = true, loaded = loadedCount, results, warnings = res.warnings })
                : Fail("載入失敗，已回滾、專案沒有變更：" + string.Join("；", res.errors));
        }
    }
}
