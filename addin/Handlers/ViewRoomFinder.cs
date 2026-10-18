// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMcpAddin.Handlers
{
    /// <summary>這張立面在畫哪一間？從裁切框的相機側往畫面裡逐點取樣，第一個打到的房間就是答案。</summary>
    internal class ViewRoomHit
    {
        public long       RoomId;
        public string     Label   = "";
        public string     Source  = "";
        public double     AreaM2;
        public double     DepthMm;                      // 取樣打中的深度，方便判讀是否合理
        public List<XYZ>  Corners = new List<XYZ>();    // 已轉成主模型座標的房間範圍角點
        /// <summary>同一個取樣點上其他也打到的房間，列出來讓人核對挑得對不對。</summary>
        public List<string> Alternatives = new List<string>();

        public override string ToString() =>
            $"{Label}（{Source}，id {RoomId}，{Math.Round(AreaM2, 1)}m²）";
    }

    /// <summary>
    /// 房間邊界本身就是牆的內面，拿它同時當裁切邊界與標註範圍，
    /// 兩件事就會用同一個依據，不必每張圖去挑背牆。
    ///
    /// 主模型與 AR 連結常常各有一套房間，同一個點會同時打到兩間
    /// （例如主模型的「廚房」和連結裡的「親子廁所」）。這種時候取面積最小的那一間 ——
    /// 廁所詳圖畫的一定是最小的那個空間 —— 其餘的列進 alternatives 供核對。
    /// </summary>
    internal static class ViewRoomFinder
    {
        private const double FT = 304.8;

        public static ViewRoomHit? Find(Document doc, View view, double maxDepthMm = 8000)
        {
            var cb = view.CropBox;
            if (cb == null) return null;
            var xf = cb.Transform;

            double w  = cb.Max.X - cb.Min.X;
            double cy = (cb.Min.Y + cb.Max.Y) * 0.5;
            var xs = new[]
            {
                (cb.Min.X + cb.Max.X) * 0.5,
                cb.Min.X + w * 0.3,
                cb.Min.X + w * 0.7
            };

            var links = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            double step  = 100.0 / FT;
            double depth = maxDepthMm / FT;

            for (double t = 0; t <= depth; t += step)
            {
                double z = cb.Max.Z - t;
                if (z < cb.Min.Z - 1e-9) break;

                var hits = new List<ViewRoomHit>();
                foreach (var x in xs)
                {
                    var p = xf.OfPoint(new XYZ(x, cy, z));

                    var r = doc.GetRoomAtPoint(p);
                    if (r != null) Add(hits, Make(r, Transform.Identity, "本模型"));

                    foreach (var li in links)
                    {
                        var ldoc = li.GetLinkDocument();
                        if (ldoc == null) continue;
                        var lxf = li.GetTotalTransform();
                        var lr  = ldoc.GetRoomAtPoint(lxf.Inverse.OfPoint(p));
                        if (lr != null) Add(hits, Make(lr, lxf, ldoc.Title));
                    }
                }

                if (hits.Count == 0) continue;

                var ordered = hits.OrderBy(h => h.AreaM2 <= 0 ? double.MaxValue : h.AreaM2).ToList();
                var best = ordered[0];
                best.DepthMm = Math.Round(t * FT);
                for (int i = 1; i < ordered.Count; i++) best.Alternatives.Add(ordered[i].ToString());
                return best;
            }
            return null;
        }

        private static void Add(List<ViewRoomHit> hits, ViewRoomHit? h)
        {
            if (h == null) return;
            if (hits.Any(x => x.RoomId == h.RoomId && x.Source == h.Source)) return;
            hits.Add(h);
        }

        private static ViewRoomHit? Make(Room room, Transform xf, string source)
        {
            var bb = room.get_BoundingBox(null);
            if (bb == null) return null;

            var hit = new ViewRoomHit
            {
                RoomId = room.Id.IntegerValue,
                Source = source,
                Label  = $"{room.Number} {room.Name}",
                AreaM2 = room.Area * 0.09290304   // ft² → m²
            };
            foreach (var p in Corners(bb)) hit.Corners.Add(xf.OfPoint(p));
            return hit;
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            var t = bb.Transform;
            var a = bb.Min;
            var b = bb.Max;
            for (int i = 0; i < 8; i++)
                yield return t.OfPoint(new XYZ(
                    (i & 1) == 0 ? a.X : b.X,
                    (i & 2) == 0 ? a.Y : b.Y,
                    (i & 4) == 0 ? a.Z : b.Z));
        }
    }
}
