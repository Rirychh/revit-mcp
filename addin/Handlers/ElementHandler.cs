// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 處理元件查詢、建立、修改；樓層、房間查詢
    /// </summary>
    public class ElementHandler
    {
        private readonly Document _doc;

        public ElementHandler(Document doc) => _doc = doc;

        // ─── 列出元件 ─────────────────────────────────────────────────────────
        public string ListElements(NameValueCollection qs)
        {
            var category  = qs["category"];         // 例如 "Walls" / "Doors" / "Floors"
            var levelName = qs["levelName"];
            var viewIdStr = qs["viewId"];           // 給定時只列該視圖看得到的元件
            int limit     = int.TryParse(qs["limit"],  out var l) ? l : 50;
            int offset    = int.TryParse(qs["offset"], out var o) ? o : 0;

            var collector = (!string.IsNullOrEmpty(viewIdStr) && long.TryParse(viewIdStr, out var vId))
                ? new FilteredElementCollector(_doc, new ElementId((int)vId)).WhereElementIsNotElementType()
                : new FilteredElementCollector(_doc).WhereElementIsNotElementType();

            // 依 BuiltInCategory 過濾。categories=A,B,C 可一次多類；category 維持單類相容。
            var catNames = (qs["categories"] ?? category ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var bics = catNames.Select(n => TryGetBuiltInCategory(n, out var b) ? (BuiltInCategory?)b : null)
                               .Where(b => b.HasValue).Select(b => b!.Value).ToList();
            if (bics.Count == 1)
                collector = collector.OfCategory(bics[0]);
            else if (bics.Count > 1)
                collector = collector.WherePasses(new ElementMulticategoryFilter(bics));

            // 區域過濾（mm，專案座標，只比對 XY）
            var box = ParseBBox(qs);
            var familyFilter = (qs["familyName"] ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var elements = collector
                .Cast<Element>()
                .Where(e => string.IsNullOrEmpty(levelName) || GetLevelName(e) == levelName)
                .Where(e => familyFilter.Count == 0 ||
                            (e is FamilyInstance f && familyFilter.Contains(f.Symbol.FamilyName)))
                .Where(e => box == null || InBBox(GetPoint(e), box))
                .ToList();

            var total = elements.Count;

            // summary=true：只回族群／類型統計，用來先看清楚一個區域裡到底有哪些設備族群
            if (string.Equals(qs["summary"], "true", StringComparison.OrdinalIgnoreCase))
            {
                var groups = elements
                    .Select(e => new
                    {
                        Fam = e is FamilyInstance f ? f.Symbol.FamilyName : "(非族群實體)",
                        Typ = e is FamilyInstance f2 ? f2.Symbol.Name : e.Name,
                        Cat = e.Category?.Name ?? "",
                        Face = e is FamilyInstance f3 && HasHostFace(f3)
                    })
                    .GroupBy(a => new { a.Fam, a.Typ, a.Cat })
                    .Select(g => new CopyGroupDto
                    {
                        FamilyName = g.Key.Fam, TypeName = g.Key.Typ, Category = g.Key.Cat,
                        Count = g.Count(), FaceHosted = g.Count(a => a.Face)
                    })
                    .OrderByDescending(g => g.Count).ToList();

                return JsonConvert.SerializeObject(ApiResponse<ElementListResult>.Ok(
                    new ElementListResult { Total = total, Count = groups.Count, Offset = 0,
                                            HasMore = false, Groups = groups }));
            }

            var page    = elements.Skip(offset).Take(limit).Select(e => MapElement(e)).ToList();
            var result  = new ElementListResult
            {
                Total    = total,
                Count    = page.Count,
                Offset   = offset,
                HasMore  = total > offset + limit,
                Elements = page
            };
            return JsonConvert.SerializeObject(ApiResponse<ElementListResult>.Ok(result));
        }

        // ─── 取得單一元件 ──────────────────────────────────────────────────────
        public string GetElement(long id)
        {
            var elem = _doc.GetElement(new ElementId((int)id));
            if (elem == null)
                return JsonConvert.SerializeObject(ApiResponse<ElementDto>.Fail($"Element {id} not found."));

            return JsonConvert.SerializeObject(ApiResponse<ElementDto>.Ok(MapElement(elem, fullParams: true)));
        }

        // ─── 建立元件（放置族群實體） ─────────────────────────────────────────
        public string CreateElement(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateElementRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            // 找 FamilySymbol
            var symbol = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s =>
                    s.FamilyName == req.FamilyName &&
                    s.Name       == req.TypeName)
                ?? throw new InvalidOperationException(
                    $"FamilySymbol '{req.FamilyName} : {req.TypeName}' not found.");

            // 找樓層
            Level? level = null;
            if (!string.IsNullOrEmpty(req.LevelName))
            {
                level = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(lv => lv.Name == req.LevelName)
                    ?? throw new InvalidOperationException($"Level '{req.LevelName}' not found.");
            }

            using var tx = new Transaction(_doc, "MCP Create Element");
            tx.Start();

            if (!symbol.IsActive) symbol.Activate();

            var location = new XYZ(
                req.X / 304.8,   // mm → feet
                req.Y / 304.8,
                req.Z / 304.8);

            var instance = level != null
                ? _doc.Create.NewFamilyInstance(location, symbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                : _doc.Create.NewFamilyInstance(location, symbol,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            // 設定參數
            if (req.Parameters != null)
                SetParameters(instance, req.Parameters);

            tx.Commit();

            return JsonConvert.SerializeObject(ApiResponse<ElementDto>.Ok(MapElement(instance)));
        }

        // ─── 修改元件參數 ──────────────────────────────────────────────────────
        public string ModifyElement(long id, string body)
        {
            var req = JsonConvert.DeserializeObject<ModifyElementRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            var elem = _doc.GetElement(new ElementId((int)id))
                       ?? throw new InvalidOperationException($"Element {id} not found.");

            using var tx = new Transaction(_doc, "MCP Modify Element");
            tx.Start();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);
            SetParameters(elem, req.Parameters);
            tx.Commit();

            return JsonConvert.SerializeObject(ApiResponse<ElementDto>.Ok(MapElement(elem, fullParams: true)));
        }

        // ─── 列出樓層 ─────────────────────────────────────────────────────────
        public string ListLevels()
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new LevelDto
                {
                    Id        = l.Id.IntegerValue,
                    Name      = l.Name,
                    Elevation = l.Elevation * 304.8   // feet → mm
                })
                .ToList();
            return JsonConvert.SerializeObject(ApiResponse<List<LevelDto>>.Ok(levels));
        }

        // ─── 列出房間 ─────────────────────────────────────────────────────────
        public string ListRooms(NameValueCollection qs)
        {
            var levelName = qs["levelName"];

            var rooms = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .Cast<Autodesk.Revit.DB.Architecture.Room>()
                .Where(r => string.IsNullOrEmpty(levelName) || GetLevelName(r) == levelName)
                .Select(r => new RoomDto
                {
                    Id        = r.Id.IntegerValue,
                    Name      = r.Name,
                    Number    = r.Number,
                    Area      = r.Area * 0.0929,        // sq-ft → sq-m
                    LevelName = GetLevelName(r)
                })
                .ToList();
            return JsonConvert.SerializeObject(ApiResponse<List<RoomDto>>.Ok(rooms));
        }

        // ─── 輔助方法 ─────────────────────────────────────────────────────────
        private ElementDto MapElement(Element e, bool fullParams = false)
        {
            var dto = new ElementDto
            {
                Id         = e.Id.IntegerValue,
                Name       = e.Name,
                Category   = e.Category?.Name ?? "",
                LevelName  = GetLevelName(e)
            };

            if (e is FamilyInstance fi)
            {
                dto.FamilyName = fi.Symbol.FamilyName;
                dto.TypeName   = fi.Symbol.Name;
                if (fi.Host != null) dto.HostId = fi.Host.Id.IntegerValue;
            }

            var pt = GetPoint(e);
            if (pt != null)
                dto.Location = new PointDto { X = pt.X * 304.8, Y = pt.Y * 304.8, Z = pt.Z * 304.8 };

            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    dto.BboxMinZ = Math.Round(bb.Min.Z * 304.8, 1);
                    dto.BboxMaxZ = Math.Round(bb.Max.Z * 304.8, 1);
                    dto.BboxMinX = Math.Round(bb.Min.X * 304.8, 1);
                    dto.BboxMaxX = Math.Round(bb.Max.X * 304.8, 1);
                    dto.BboxMinY = Math.Round(bb.Min.Y * 304.8, 1);
                    dto.BboxMaxY = Math.Round(bb.Max.Y * 304.8, 1);
                }
                var lvlId = e.LevelId;
                if (lvlId != null && lvlId != ElementId.InvalidElementId)
                {
                    if (_doc.GetElement(lvlId) is Level lv)
                        dto.LevelElevMm = Math.Round(lv.Elevation * 304.8, 1);
                }
            }
            catch { }

            if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId)
            {
                dto.GroupId = e.GroupId.IntegerValue;
                var grp = _doc.GetElement(e.GroupId);
                if (grp != null) dto.GroupName = grp.Name;
            }

            if (e.Location is LocationPoint lp)
                dto.Rotation = lp.Rotation * 180.0 / Math.PI;

            if (fullParams)
            {
                foreach (Parameter p in e.Parameters)
                {
                    if (!p.HasValue) continue;
                    dto.Parameters[p.Definition.Name] = p.AsValueString() ?? p.AsString() ?? "";
                }
            }
            return dto;
        }

        /// <summary>從查詢字串取 bbox（mm）。四個值都缺就回 null。</summary>
        private static BBoxDto? ParseBBox(NameValueCollection qs)
        {
            bool ok = double.TryParse(qs["minX"], out var x0) & double.TryParse(qs["minY"], out var y0)
                    & double.TryParse(qs["maxX"], out var x1) & double.TryParse(qs["maxY"], out var y1);
            if (!ok) return null;
            return new BBoxDto { MinX = Math.Min(x0, x1), MinY = Math.Min(y0, y1),
                                 MaxX = Math.Max(x0, x1), MaxY = Math.Max(y0, y1) };
        }

        /// <summary>點（feet）是否落在 bbox（mm）內，只比 XY。</summary>
        internal static bool InBBox(XYZ? p, BBoxDto box)
        {
            if (p == null) return false;
            var x = p.X * 304.8; var y = p.Y * 304.8;
            return x >= box.MinX && x <= box.MaxX && y >= box.MinY && y <= box.MaxY;
        }

        /// <summary>取元件代表點（feet，專案座標）。點式優先，其次曲線中點，最後 bbox 中心。</summary>
        internal static XYZ? GetPoint(Element e)
        {
            switch (e.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc when lc.Curve != null:
                    return lc.Curve.Evaluate(0.5, true);
            }
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null) return (bb.Min + bb.Max) / 2.0;
            }
            catch { }
            return null;
        }

        internal static bool TryCategory(string name, out BuiltInCategory bic) => TryGetBuiltInCategory(name, out bic);

        internal static bool HasHostFace(FamilyInstance fi)
        {
            try { return fi.HostFace != null; } catch { return false; }
        }

        private static string GetLevelName(Element e)
        {
            var levelId = e.LevelId;
            if (levelId == ElementId.InvalidElementId) return "";
            return e.Document.GetElement(levelId)?.Name ?? "";
        }

        private static void SetParameters(Element e, Dictionary<string, string> paramValues)
        {
            foreach (var kv in paramValues)
            {
                var key = kv.Key; var val = kv.Value;
                var param = e.LookupParameter(key);
                if (param == null || param.IsReadOnly) continue;

                switch (param.StorageType)
                {
                    case StorageType.String:  param.Set(val); break;
                    case StorageType.Double:
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { bool isLength = false; try { isLength = param.Definition.GetDataType() == SpecTypeId.Length; } catch { } /* 長度參數的數值視為 mm（舊版直接寫英呎內部單位） */ param.Set(isLength ? d / 304.8 : d); } break;
                    case StorageType.Integer:
                        if (int.TryParse(val, out var i)) param.Set(i); break;
                }
            }
        }

        private static bool TryGetBuiltInCategory(string name, out BuiltInCategory bic)
        {
            var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walls"]     = BuiltInCategory.OST_Walls,
                ["Doors"]     = BuiltInCategory.OST_Doors,
                ["Windows"]   = BuiltInCategory.OST_Windows,
                ["Floors"]    = BuiltInCategory.OST_Floors,
                ["Roofs"]     = BuiltInCategory.OST_Roofs,
                ["Columns"]   = BuiltInCategory.OST_Columns,
                ["Stairs"]    = BuiltInCategory.OST_Stairs,
                ["Rooms"]     = BuiltInCategory.OST_Rooms,
                ["Furniture"] = BuiltInCategory.OST_Furniture,
                ["Ceilings"]  = BuiltInCategory.OST_Ceilings,
                ["PlumbingFixtures"]    = BuiltInCategory.OST_PlumbingFixtures,
                ["SpecialityEquipment"] = BuiltInCategory.OST_SpecialityEquipment,
                ["MechanicalEquipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["ElectricalFixtures"]  = BuiltInCategory.OST_ElectricalFixtures,
                ["LightingFixtures"]    = BuiltInCategory.OST_LightingFixtures,
                ["GenericModel"]        = BuiltInCategory.OST_GenericModel,
                ["Casework"]            = BuiltInCategory.OST_Casework,
                ["CurtainWallPanels"]   = BuiltInCategory.OST_CurtainWallPanels,

                // ── 天花設備常用（原本缺，出回風口／排煙口都在 DuctTerminal）──
                ["DuctTerminal"]        = BuiltInCategory.OST_DuctTerminal,
                ["AirTerminal"]         = BuiltInCategory.OST_DuctTerminal,
                ["DuctAccessory"]       = BuiltInCategory.OST_DuctAccessory,
                ["DuctFitting"]         = BuiltInCategory.OST_DuctFitting,
                ["Ducts"]               = BuiltInCategory.OST_DuctCurves,
                ["Sprinklers"]          = BuiltInCategory.OST_Sprinklers,
                ["FireAlarmDevices"]    = BuiltInCategory.OST_FireAlarmDevices,
                ["ElectricalEquipment"] = BuiltInCategory.OST_ElectricalEquipment,
                ["CommunicationDevices"]= BuiltInCategory.OST_CommunicationDevices,
                ["DataDevices"]         = BuiltInCategory.OST_DataDevices,
                ["SecurityDevices"]     = BuiltInCategory.OST_SecurityDevices,
                ["LightingDevices"]     = BuiltInCategory.OST_LightingDevices,
                ["PipeAccessory"]       = BuiltInCategory.OST_PipeAccessory,
                ["PipeFitting"]         = BuiltInCategory.OST_PipeFitting,
                ["Pipes"]               = BuiltInCategory.OST_PipeCurves,
            };
            if (map.TryGetValue(name, out bic)) return true;

            // 對照表沒有的，允許直接給 Revit 內建列舉名（例如 "OST_DuctTerminal"）
            return Enum.TryParse(name, ignoreCase: true, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic);
        }
    }
}
