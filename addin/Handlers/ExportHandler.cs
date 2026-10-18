// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    public class ExportHandler
    {
        private readonly UIApplication _uiApp;
        private readonly Document      _doc;

        public ExportHandler(UIApplication uiApp, Document doc)
        {
            _uiApp = uiApp;
            _doc   = doc;
        }

        // ─── 匯出 IFC ─────────────────────────────────────────────────────────
        public string ExportIfc(string body)
        {
            var req = JsonConvert.DeserializeObject<ExportRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            var outDir  = string.IsNullOrEmpty(req.OutputPath)
                ? Path.GetTempPath()
                : Path.GetDirectoryName(req.OutputPath) ?? Path.GetTempPath();
            var outFile = string.IsNullOrEmpty(req.OutputPath)
                ? $"export_{DateTime.Now:yyyyMMddHHmmss}.ifc"
                : Path.GetFileName(req.OutputPath);

            Directory.CreateDirectory(outDir);

            var opts = new IFCExportOptions
            {
                FileVersion          = IFCVersion.IFC2x3CV2,
                SpaceBoundaryLevel   = 1,
                ExportBaseQuantities = true,
                WallAndColumnSplitting = true
            };

            using var tx = new Transaction(_doc, "MCP Export IFC");
            tx.Start();
            _doc.Export(outDir, outFile, opts);
            tx.Commit();

            var fullPath = Path.Combine(outDir, outFile);
            var size     = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

            var result = new ExportResult
            {
                FilePath  = fullPath,
                Format    = "IFC",
                SizeBytes = size
            };
            return JsonConvert.SerializeObject(ApiResponse<ExportResult>.Ok(result));
        }

        // ─── 匯出明細表 ───────────────────────────────────────────────────────
        public string ExportSchedule(string body)
        {
            var req = JsonConvert.DeserializeObject<ExportRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            var outDir = string.IsNullOrEmpty(req.OutputPath)
                ? Path.GetTempPath()
                : Path.GetDirectoryName(req.OutputPath) ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            // 找所有明細表視圖
            var schedules = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsInternalKeynoteSchedule)
                .ToList();

            if (req.ViewIds?.Count > 0)
                schedules = schedules
                    .Where(vs => req.ViewIds.Contains(vs.Id.IntegerValue))
                    .ToList();

            if (schedules.Count == 0)
                return JsonConvert.SerializeObject(ApiResponse<ExportResult>.Fail("No schedules found."));

            var opts = new ViewScheduleExportOptions
            {
                FieldDelimiter = ",",
                Title          = true,
                ColumnHeaders  = ExportColumnHeaders.OneRow
            };

            var exportedFiles = new List<string>();

            using var tx = new Transaction(_doc, "MCP Export Schedule");
            tx.Start();
            foreach (var sched in schedules)
            {
                var fileName = $"{SanitizeFileName(sched.Name)}_{DateTime.Now:yyyyMMddHHmmss}.csv";
                sched.Export(outDir, fileName, opts);
                exportedFiles.Add(Path.Combine(outDir, fileName));
            }
            tx.Commit();

            var totalSize = exportedFiles
                .Where(File.Exists)
                .Sum(f => new FileInfo(f).Length);

            var result = new ExportResult
            {
                FilePath  = exportedFiles.Count == 1
                    ? exportedFiles[0]
                    : outDir + $" ({exportedFiles.Count} files)",
                Format    = "CSV",
                SizeBytes = totalSize
            };
            return JsonConvert.SerializeObject(ApiResponse<ExportResult>.Ok(result));
        }

        private static string SanitizeFileName(string name) =>
            string.Concat(name.Split(Path.GetInvalidFileNameChars()));
    }
}
