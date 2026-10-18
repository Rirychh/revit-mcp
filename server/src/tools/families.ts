// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

interface FamilyBuildResult {
  familyPath: string;
  familyName: string;
  template: string;
  elementsCreated: number;
  parameters: string[];
  loadedInto?: string | null;
  warnings: string[];
}

const pt = z.array(z.number()).length(2);

const paramSchema = z.object({
  name:     z.string().min(1),
  kind:     z.enum(["yesno", "length", "text", "integer", "number", "familyType"]),
  instance: z.boolean().default(true),
  value:    z.string().optional().describe("Default value. length in mm; yesno '1'/'0'; familyType = nested family name or 'Family:Type'"),
  formula:  z.string().optional().describe("Revit formula, e.g. 'not(四格)'"),
  category: z.string().optional().describe("familyType only: category, e.g. DetailComponents / GenericAnnotation"),
});

const elementSchema = z.object({
  kind:         z.enum(["filledRegion", "maskingRegion", "glyph", "line", "nested", "label"]),
  loops:        z.array(z.array(pt)).optional().describe("filledRegion/maskingRegion: point loops in mm; extra loops become holes"),
  rects:        z.array(z.array(z.number()).length(4)).optional().describe("filledRegion/maskingRegion: [cx, cy, w, h] rectangles in mm"),
  color:        z.array(z.number().int().min(0).max(255)).length(3).optional().describe("filledRegion/glyph solid colour RGB"),
  text:         z.string().optional().describe("glyph: text converted to filled-region outlines (scales with view)"),
  font:         z.string().optional().describe("glyph: font name, default Arial"),
  capHeight:    z.number().positive().optional().describe("glyph: capital letter height in mm (default 350)"),
  center:       pt.optional().describe("glyph: centre point in mm"),
  points:       z.array(pt).optional().describe("line: polyline points in mm"),
  closed:       z.boolean().optional(),
  family:       z.string().optional().describe("nested: family name (must be in nestedFamilies)"),
  type:         z.string().optional().describe("nested: type name"),
  at:           pt.optional().describe("nested: insertion point in mm"),
  typeParam:    z.string().optional().describe("nested: familyType parameter that drives this instance's type"),
  visibleParam: z.string().optional().describe("yes/no parameter that drives visibility"),
  showEdges:    z.boolean().optional().describe("regions: keep boundary lines (default false = invisible)"),
  paramMap:     z.record(z.string()).optional().describe("nested: {nestedInstanceParam: hostParam} associations, e.g. {\"代號\":\"代號1\"}"),
  source:              z.string().optional().describe("label: .rfa that already contains a label (API cannot create labels)"),
  sourceProjectFamily: z.string().optional().describe("label: name of a family loaded in the active project to copy the label from"),
  sourceLabelText:     z.string().optional().describe("label: pick the label whose sample text matches"),
  param:         z.string().optional().describe("label: rename the label's parameter to this"),
  paramInstance: z.boolean().optional().describe("label: make the parameter instance (default true)"),
  paramValue:    z.string().optional().describe("label: default text"),
  textSize:      z.number().positive().optional().describe("label: printed text height in mm"),
  textFont:      z.string().optional().describe("label: font name"),
});

export function registerFamilyTools(server: McpServer): void {
  server.registerTool(
    "revit_scan_family_labels",
    {
      title: "Find families that contain labels",
      description: `Open .rfa files (or folders), and/or annotation families loaded in the active project, and list the ones that contain labels together with their family parameters.
Use it to pick a label source for revit_build_family (kind "label"): the label should be bound to a non-built-in family parameter.

Args:
  - paths (string[]): .rfa files or folders (searched recursively)
  - fromProject (bool): also scan editable annotation families in the active project
  - nameContains (string): filter project families by name
  - max (int): max families to open (default 40)`,
      inputSchema: z.object({
        paths:        z.array(z.string()).default([]),
        fromProject:  z.boolean().default(false),
        nameContains: z.string().optional(),
        max:          z.number().int().positive().max(200).default(40),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        const r = await revitRequest<unknown>("post", "/families/labels", undefined, args, 300_000);
        return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  server.registerTool(
    "revit_build_family",
    {
      title: "Build a Revit Family (.rfa) from a JSON spec",
      description: `Create a new family file from a template, add parameters / geometry / nested families, save it as .rfa, and optionally load it into the active project.

Works without an open project (zero-doc) as long as Revit is running with the MCP add-in; loadIntoProject needs an active project document.
All coordinates are millimetres, origin = family insertion point. Default template = Metric Detail Item (公制詳圖項目).

Element kinds:
  - filledRegion  : solid-colour region from loops or rects (extra loops = holes)
  - maskingRegion : white masking region (hides what is behind the family in the project)
  - glyph         : text converted to filled-region outlines, scales with the view (good for codes like A/B/C)
  - line          : detail lines
  - nested        : place a nested family (listed in nestedFamilies); typeParam links its type to a familyType parameter; paramMap links its instance parameters to host parameters
  - label         : copy a label from an existing family (source / sourceProjectFamily), rename its parameter (param) — use revit_scan_family_labels first to find a usable label (bound to a family parameter, not a built-in one). Labels only live in annotation families (use template "Metric Generic Annotation.rft").

Visibility: set visibleParam to a yes/no parameter name. Formula parameters (e.g. 單格 = not(四格)) are applied last.
Typical workflow for a code box: build one small family per code with a glyph, then build the host family that nests them and exposes familyType parameters.

Args:
  - savePath (string): output .rfa path, e.g. "C:\\\\temp\\\\X.rfa"
  - template (string, optional): .rft path or file name
  - typeName (string, optional)
  - parameters, nestedFamilies, elements: see schema
  - loadIntoProject (bool): also load into the active project (overwrites same-name family)`,
      inputSchema: z.object({
        savePath:        z.string().min(5),
        template:        z.string().optional(),
        overwrite:       z.boolean().default(true),
        typeName:        z.string().optional(),
        parameters:      z.array(paramSchema).default([]),
        nestedFamilies:  z.array(z.string()).default([]).describe("Full paths of .rfa files to load before placing nested instances"),
        elements:        z.array(elementSchema).default([]),
        loadIntoProject: z.boolean().default(false),
        cleanTemplate:   z.boolean().default(true).describe("Delete the instruction text notes that some templates contain"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        const r = await revitRequest<FamilyBuildResult>("post", "/families/build", undefined, args, 180_000);
        const lines = [
          `✅ Family built: \`${r.familyPath}\``,
          `- Name: ${r.familyName}`,
          `- Template: ${r.template}`,
          `- Elements created: ${r.elementsCreated}`,
          `- Parameters: ${r.parameters.join(", ") || "(none)"}`,
          `- Loaded into: ${r.loadedInto ?? "(not loaded)"}`,
        ];
        if (r.warnings?.length) lines.push("", "Warnings:", ...r.warnings.map((w) => `- ${w}`));
        return { content: [{ type: "text", text: lines.join("\n") }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_inspect_family ─────────────────────────────────────────────────
  server.registerTool(
    "revit_inspect_family",
    {
      title: "Inspect a family (read-only)",
      description: `唯讀檢查作用中專案裡的某個族：參考平面、視圖方向、幾何草圖平面。
實作上會用 EditFamily 開一份暫存族文件、讀完就 Close(false) 不存檔，所以不會改到專案或 .rfa。
用來診斷「族放進去方向不對 / 轉不正」的問題（搭配 revit_align_elements_upright）。
Args:
  - name (string): 作用中專案裡的族名`,
      inputSchema: z.object({
        name: z.string().min(1).describe("作用中專案裡的族名"),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async (a) => {
      try {
        const r = await revitRequest<unknown>("post", "/families/inspect", undefined, a, 180_000);
        return { content: [{ type: "text", text: "```json\n" + JSON.stringify(r, null, 2) + "\n```" }], structuredContent: r as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );
}
