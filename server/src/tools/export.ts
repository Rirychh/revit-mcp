// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";
import type { ExportResult } from "../types.js";

export function registerExportTools(server: McpServer): void {

  // ── revit_export_ifc ─────────────────────────────────────────────────────
  server.registerTool(
    "revit_export_ifc",
    {
      title: "Export Revit Model to IFC",
      description: `Export the active Revit model to an IFC 2x3 CV2 file.

IFC (Industry Foundation Classes) is an open BIM standard used for interoperability.
If outputPath is not specified, the file is saved to the system temp directory.

Args:
  - outputPath (string, optional): Full path for the output .ifc file, e.g. "C:\\BIM\\building.ifc"`,
      inputSchema: z.object({
        outputPath: z.string().optional().describe("Full output file path (optional)"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ outputPath }) => {
      try {
        const result = await revitRequest<ExportResult>("post", "/export/ifc", undefined, { outputPath });
        const sizeMb = (result.sizeBytes / 1024 / 1024).toFixed(2);
        const text   = [
          `✅ IFC Export complete`,
          `- File: \`${result.filePath}\``,
          `- Size: ${sizeMb} MB`,
        ].join("\n");
        return { content: [{ type: "text", text }], structuredContent: result as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_export_schedule ────────────────────────────────────────────────
  server.registerTool(
    "revit_export_schedule",
    {
      title: "Export Revit Schedules to CSV",
      description: `Export one or more Revit schedule views to CSV files.

If no viewIds are provided, all schedules in the model are exported.
Each schedule becomes a separate .csv file in the output directory.

Args:
  - outputPath (string, optional): Directory path for output CSV files
  - viewIds (number[], optional): Specific schedule view IDs to export (use revit_list_views with viewType="Schedule")`,
      inputSchema: z.object({
        outputPath: z.string().optional().describe("Output directory path"),
        viewIds:    z.array(z.number().int()).optional().describe("Specific schedule view IDs to export"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ outputPath, viewIds }) => {
      try {
        const result = await revitRequest<ExportResult>("post", "/export/schedule", undefined, { outputPath, viewIds });
        const sizeKb = (result.sizeBytes / 1024).toFixed(1);
        const text   = [
          `✅ Schedule export complete (${result.format})`,
          `- Output: \`${result.filePath}\``,
          `- Total size: ${sizeKb} KB`,
        ].join("\n");
        return { content: [{ type: "text", text }], structuredContent: result as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );
}
