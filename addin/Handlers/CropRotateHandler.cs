// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>旋轉平面視圖的裁剪範圍（把斜的量體轉成水平垂直）。</summary>
    public class CropRotateHandler
    {
        private readonly Document _doc;
        public CropRotateHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });

        // POST /views/rotate-crop
        // { viewId, angleDeg?, alignWallId?, alignRoomId?, apply }
        // angleDeg：逆時針正值。alignWallId：以該牆方向為水平。alignRoomId：以房間最長邊為水平。
        public string RotateCrop(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            long vid = (long?)r["viewId"] ?? 0;
            if (_doc.GetElement(ElementIdCompat.NewId(vid)) is not View v) return Fail($"找不到視圖 {vid}。");
            if (v.IsTemplate) return Fail("視圖樣板不能設裁剪。");
            if (!v.CropBoxActive) return Fail($"視圖「{v.Name}」沒有啟用裁剪範圍。");

            double? angleDeg = (double?)r["angleDeg"];
            long wallId = (long?)r["alignWallId"] ?? 0;
            long roomId = (long?)r["alignRoomId"] ?? 0;
            string basis = "angleDeg";

            if (angleDeg == null && (wallId > 0 || roomId > 0))
            {
                XYZ? dir = null;
                if (wallId > 0)
                {
                    if (_doc.GetElement(ElementIdCompat.NewId(wallId)) is not Wall w) return Fail($"找不到牆 {wallId}。");
                    if (w.Location is not LocationCurve lc || lc.Curve is not Line ln) return Fail("該牆不是直線牆。");
                    dir = ln.Direction; basis = "wall";
                }
                else
                {
                    var e = _doc.GetElement(ElementIdCompat.NewId(roomId));
                    var bb = e?.get_BoundingBox(null);
                    if (bb == null) return Fail($"找不到元素 {roomId} 或它沒有範圍。");
                    dir = (bb.Max.X - bb.Min.X) >= (bb.Max.Y - bb.Min.Y) ? XYZ.BasisX : XYZ.BasisY;
                    basis = "room-bbox";
                }
                var a = Math.Atan2(dir!.Y, dir.X) * 180.0 / Math.PI;
                // 轉到最接近的水平／垂直
                var snapped = Math.Round(a / 90.0) * 90.0;
                angleDeg = -(a - snapped);
            }
            if (angleDeg == null) return Fail("需要 angleDeg，或 alignWallId／alignRoomId。");

            var cb = v.CropBox;
            var mid = (cb.Min + cb.Max) / 2.0;
            var world = cb.Transform.OfPoint(mid);
            var rad = angleDeg.Value * Math.PI / 180.0;

            if (!apply)
                return Ok(new { applied = false, view = v.Name, basis, angleDeg = Math.Round(angleDeg.Value, 4) });

            using (var tx = new Transaction(_doc, "MCP 旋轉裁剪範圍"))
            {
                tx.Start();
                var fo = tx.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(new SwallowWarnings());
                tx.SetFailureHandlingOptions(fo);
                var rot = Transform.CreateRotationAtPoint(XYZ.BasisZ, rad, world);
                var nb = new BoundingBoxXYZ { Transform = rot * cb.Transform, Min = cb.Min, Max = cb.Max };
                v.CropBox = nb;
                tx.Commit();
            }
            return Ok(new { applied = true, view = v.Name, basis, angleDeg = Math.Round(angleDeg.Value, 4) });
        }
    }
}