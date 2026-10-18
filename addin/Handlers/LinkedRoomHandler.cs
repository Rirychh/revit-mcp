// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 跨主模型與所有連結列出房間。本案的廁所房間都在 AR 連結裡，
    /// 主模型的 /rooms 查不到，所以「哪個型號用在哪些樓層」要靠這支。
    /// </summary>
    public class LinkedRoomHandler
    {
        private readonly Document _doc;
        public LinkedRoomHandler(Document doc) => _doc = doc;

        public string ListRooms(NameValueCollection qs)
        {
            string nameFilter  = qs["name"]  ?? "";
            string levelFilter = qs["level"] ?? "";

            var res = new LinkedRoomsResult();
            Collect(_doc, "本模型", nameFilter, levelFilter, Transform.Identity, res);

            foreach (var li in new FilteredElementCollector(_doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = li.GetLinkDocument();
                if (ldoc == null) continue;
                if (res.SearchedSources.Contains(ldoc.Title)) continue;
                res.SearchedSources.Add(ldoc.Title);
                Collect(ldoc, ldoc.Title, nameFilter, levelFilter, li.GetTotalTransform(), res);
            }

            res.Total = res.Rooms.Count;
            return JsonConvert.SerializeObject(ApiResponse<LinkedRoomsResult>.Ok(res));
        }

        private static void Collect(Document src, string label, string nameFilter,
                                    string levelFilter, Transform xf, LinkedRoomsResult res)
        {
            const double FT = 304.8;
            foreach (var r in new FilteredElementCollector(src)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .Cast<Element>())
            {
                if (r is not Room room) continue;
                if (room.Area <= 0) continue;               // 未放置或未封閉的房間

                string name  = room.Name ?? "";
                string level = room.Level?.Name ?? "";

                if (nameFilter.Length  > 0 && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (levelFilter.Length > 0 && level.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // 房間中心點，已轉成主模型座標，單位 mm —— 位置索引要靠它擺方框
                double cx = 0, cy = 0, cz = 0;
                var bb = room.get_BoundingBox(null);
                if (bb != null)
                {
                    var mid = (bb.Min + bb.Max) * 0.5;
                    var p   = xf.OfPoint(bb.Transform.OfPoint(mid));
                    cx = Math.Round(p.X * FT); cy = Math.Round(p.Y * FT); cz = Math.Round(p.Z * FT);
                }
                else if (room.Location is LocationPoint lp)
                {
                    var p = xf.OfPoint(lp.Point);
                    cx = Math.Round(p.X * FT); cy = Math.Round(p.Y * FT); cz = Math.Round(p.Z * FT);
                }

                res.Rooms.Add(new LinkedRoomDto
                {
                    Id     = room.Id.IntegerValue,
                    Source = label,
                    Number = room.Number ?? "",
                    Name   = name,
                    Level  = level,
                    AreaM2 = Math.Round(room.Area * 0.09290304, 2),
                    X = cx, Y = cy, Z = cz
                });
            }
        }
    }
}
