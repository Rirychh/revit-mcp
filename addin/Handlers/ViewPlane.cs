// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 視圖平面座標（u＝視圖右方、v＝視圖上方，單位 mm，以視圖 Origin 為原點）。
    /// 標籤、尺寸、標號截圖等工具都用這套座標回報與接收位置，平面、立面、剖面一致。
    /// </summary>
    internal static class ViewPlane
    {
        public const double FT = 304.8;

        public struct Rect
        {
            public double U0, V0, U1, V1;
            public bool Valid => U1 >= U0 && V1 >= V0;
            public double CU => (U0 + U1) / 2; public double CV => (V0 + V1) / 2;
            public bool Overlaps(Rect o, double tolMm = 0) =>
                U0 < o.U1 - tolMm && o.U0 < U1 - tolMm && V0 < o.V1 - tolMm && o.V0 < V1 - tolMm;
            public bool Contains(double u, double v) => u >= U0 && u <= U1 && v >= V0 && v <= V1;
            public Rect Union(Rect o) => new Rect { U0 = Math.Min(U0, o.U0), V0 = Math.Min(V0, o.V0), U1 = Math.Max(U1, o.U1), V1 = Math.Max(V1, o.V1) };
            public object Dto() => new[] { Math.Round(U0), Math.Round(V0), Math.Round(U1), Math.Round(V1) };
        }

        public static (double u, double v) UV(View view, XYZ p)
        {
            var d = p - view.Origin;
            return (d.DotProduct(view.RightDirection) * FT, d.DotProduct(view.UpDirection) * FT);
        }

        /// <summary>uv（mm）→ 模型點；視深方向沿用 depthFrom 的位置。</summary>
        public static XYZ ToModel(View view, double u, double v, XYZ depthFrom)
        {
            double w = (depthFrom - view.Origin).DotProduct(view.ViewDirection);
            return view.Origin + view.RightDirection * (u / FT) + view.UpDirection * (v / FT) + view.ViewDirection * w;
        }

        public static Rect? FromBox(View view, BoundingBoxXYZ? bb)
        {
            if (bb == null) return null;
            var t = bb.Transform ?? Transform.Identity;
            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        var (u, v) = UV(view, t.OfPoint(new XYZ(x, y, z)));
                        u0 = Math.Min(u0, u); v0 = Math.Min(v0, v); u1 = Math.Max(u1, u); v1 = Math.Max(v1, v);
                    }
            return new Rect { U0 = u0, V0 = v0, U1 = u1, V1 = v1 };
        }

        public static Rect? ElementRect(View view, Element el) => FromBox(view, el.get_BoundingBox(view) ?? el.get_BoundingBox(null));

        public static Rect? CropRect(View view)
        {
            if (!view.CropBoxActive) return null;
            return FromBox(view, view.CropBox);
        }

        public static object R2(double u, double v) => new[] { Math.Round(u), Math.Round(v) };
    }
}
