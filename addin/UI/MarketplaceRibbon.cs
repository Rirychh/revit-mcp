// Revit MCP © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT
#if MARKETPLACE
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitMcpAddin.UI
{
    /// <summary>Marketplace 版功能區：獨立「Revit MCP」頁籤（內部版改由 pyRevit 按鈕開啟面板）。</summary>
    internal static class MarketplaceRibbon
    {
        public const string HelpUrl = "https://github.com/Rirychh/revit-mcp#readme";
        private const string Tab = "Revit MCP";

        public static void Create(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(Tab); } catch { /* 已存在 */ }
            var panel = app.CreateRibbonPanel(Tab, "AI");
            string asm = Assembly.GetExecutingAssembly().Location;
            var help = new ContextualHelp(ContextualHelpType.Url, HelpUrl);

            Add(panel, asm, "RevitMcp_Chat", "AI\n對話", typeof(ShowPanelCommand),
                "開啟 AI 對話面板（Claude／OpenAI／Gemini，使用你自己的 API key）", "AI", Color.FromRgb(0xC9, 0x64, 0x42), help);
            Add(panel, asm, "RevitMcp_Connect", "連接\nClaude Desktop", typeof(ConnectClaudeCommand),
                "把 Revit MCP server 寫入 Claude Desktop 設定，重開 Claude Desktop 後即可用", "MCP", Color.FromRgb(0x2F, 0x5D, 0x8A), help);
            Add(panel, asm, "RevitMcp_Help", "說明", typeof(HelpCommand),
                "開啟使用說明（F1 亦可）", "?", Color.FromRgb(0x55, 0x55, 0x55), help);
        }

        private static void Add(RibbonPanel panel, string asm, string name, string text, System.Type cmd,
                                string tip, string glyph, Color color, ContextualHelp help)
        {
            var data = new PushButtonData(name, text, asm, cmd.FullName)
            {
                ToolTip = tip,
                LargeImage = Icon(glyph, color, 32),
                Image = Icon(glyph, color, 16),
            };
            var btn = (PushButton)panel.AddItem(data);
            btn.SetContextualHelp(help);
        }

        private static BitmapSource Icon(string glyph, Color color, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
                var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size * (glyph.Length > 2 ? 0.34 : 0.5), Brushes.White, 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
    }
}
#endif