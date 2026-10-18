// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";
import { ResponseFormat } from "../constants.js";
import type { TagTypeDto, TagElementsResult, DimensionResult, CropViewResult, CropCandidateDto,
              ViewAnnotationsResult } from "../types.js";

const CATEGORY_HINT =
  "PlumbingFixtures / SpecialityEquipment / MechanicalEquipment / ElectricalFixtures / " +
  "LightingFixtures / GenericModel / Casework / Furniture / Doors / Windows / Walls / Ceilings / Floors";

export function registerAnnotationTools(server: McpServer): void {

  // ── revit_list_tag_types ─────────────────────────────────────────────────
  server.registerTool(
    "revit_list_tag_types",
    {
      title: "List Revit Tag Types",
      description: `List annotation tag families/types available in the active model.

Use this before revit_tag_elements to find the exact tagFamilyName / tagTypeName
(e.g. the diamond equipment-number tag used on toilet detail sheets).

Args:
  - category (string, optional): case-insensitive substring matched against the tag's
    category name, family name, or type name (e.g. "衛浴", "Plumbing", "設備")
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        category:        z.string().optional().describe("Substring filter on category / family / type name"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ category, response_format }) => {
      try {
        const types = await revitRequest<TagTypeDto[]>("get", "/annotations/tagtypes", { category });
        const text = response_format === ResponseFormat.JSON
          ? JSON.stringify(types, null, 2)
          : [`# Tag Types (${types.length})`, "",
             ...types.map(t => `- **[${t.id}] ${t.familyName} : ${t.typeName}** — ${t.category}`)].join("\n");
        return { content: [{ type: "text", text }], structuredContent: { types } as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );


  // ── revit_read_view_annotations ──────────────────────────────────────────
  server.registerTool(
    "revit_read_view_annotations",
    {
      title: "Read the Annotations Already in a Revit View",
      description: `List the tags, dimensions and text notes that already exist in one view.

Use this to copy an existing sheet's annotation convention instead of guessing it: point it at a
finished view (e.g. one of the A9-13 toilet elevations) and it reports, for every tag, the family
and type actually used, the tag text, whether it has a leader, and which element it tags — plus
the dimension style names with their segment lengths, and the text-note styles.

Read the reference view first, then feed those exact family/type names to revit_tag_elements.

Args:
  - viewId (number): the view to read
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        viewId:          z.number().int().describe("Revit View Element ID"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ viewId, response_format }) => {
      try {
        const r = await revitRequest<ViewAnnotationsResult>("get", "/annotations/viewtags", { viewId });
        if (response_format === ResponseFormat.JSON) {
          return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }],
                   structuredContent: r as unknown as Record<string, unknown> };
        }
        const byType = new Map<string, { n: number; texts: string[] }>();
        for (const t of r.tags) {
          const k = `${t.category} | ${t.familyName} : ${t.typeName}${t.hasLeader ? " (引線)" : ""}`;
          const e = byType.get(k) ?? { n: 0, texts: [] };
          e.n++;
          if (t.text && e.texts.length < 8) e.texts.push(t.text);
          byType.set(k, e);
        }
        const text = [
          `# ${r.viewName} 現有標註`,
          "",
          `## 標籤 ${r.tags.length} 個`,
          ...[...byType.entries()].map(([k, v]) =>
            `- **${k}** ×${v.n}${v.texts.length ? ` → ${v.texts.map(x => `\`${x}\``).join(" ")}` : ""}`),
          "",
          `## 尺寸 ${r.dimensions.length} 條`,
          ...r.dimensions.slice(0, 25).map(d =>
            `- [${d.dimensionId}] ${d.typeName}：${d.segmentsMm.join(" / ")} mm`),
          r.dimensions.length > 25 ? `- …其餘 ${r.dimensions.length - 25} 條略` : "",
          "",
          `## 文字 ${r.textNotes.length} 則`,
          ...r.textNotes.slice(0, 25).map(n => `- ${n.typeName}：${n.text}`),
          r.textNotes.length > 25 ? `- …其餘 ${r.textNotes.length - 25} 則略` : "",
        ].filter(Boolean).join("\n");
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );


  // ── revit_tag_elements ───────────────────────────────────────────────────
  server.registerTool(
    "revit_tag_elements",
    {
      title: "Tag Elements in a Revit View",
      description: `Place tags (e.g. the diamond equipment-number tags on a toilet detail) on
elements inside one view — a plan, ceiling plan, elevation or section.

DRY RUN BY DEFAULT: apply=false only reports which elements would be tagged and where.
Review that list, then call again with apply=true to actually write to the model.

Pick targets EITHER by elementIds OR by categories (categories collects everything of that
category visible in the view). Supported categories: ${CATEGORY_HINT}.

Use revit_list_tag_types first to get tagFamilyName / tagTypeName. If both are omitted,
Revit's default tag type for each element's category is used. House convention on this project:
fixtures get the diamond multi-category tag; wall / floor / ceiling materials get the rectangular
category tags — so tag materials by calling this again with categories ["Walls"] / ["Floors"] /
["Ceilings"] and the matching rectangular tag type.

Scope is handled automatically. Interior elevations usually have a deep far clip, so "visible in
the view" also catches fixtures in the rooms behind. The tool samples along the line of sight from
the crop box, finds the room this view is actually drawing (host model or any loaded link), and
keeps only the fixtures inside that room's boundary. The room found is reported as "scope" — check
it matches the room you expect. Pass seedElementId only to override that, e.g. when the room is
missing from the model: the seed is then the finish back wall, and maxDepthMm sets the depth band.

Diamond vs rectangle is decided by the MARK FORMAT, not by category — a mirror wall carries D9 and
gets the diamond while the finish wall beside it carries W3-11 and gets the rectangle. So route with
markInclude / markExclude on the material pattern ^[A-Za-z]+\\d+-\\d+$ rather than by picking categories.

House convention on this project, read off the A9-13 sheet:
  - equipment: BAF_TG_Multi Category Tag_diamond : 編號-type mark, markExclude the material pattern,
    layout "sideLeaders"
  - materials: BAF_TG_Multi Category Tag : S50-100, markInclude the material pattern,
    dedupeByMark true (one W, one F, one C per view), layout "onElement"

layout "onElement" puts the tag at the element's centre. layout "sideLeaders" splits the targets at
the room's centre line, stacks the tag heads in a column just outside each side of the room, and lets
Revit draw the leader back to the element — crop the view with annotationCropMm large enough
(≥ leaderMarginMm + tag width) or Revit will shove the heads back inside the frame.

Args:
  - viewId (number): target view
  - elementIds (number[], optional): explicit elements to tag
  - categories (string[], optional): tag everything of these categories visible in the view
  - tagFamilyName / tagTypeName (string, optional): which tag type to place
  - offsetMm (number): shift the tag from the element centre, default 0
  - offsetDirection: "none" | "up" | "down" | "left" | "right" (view coordinates)
  - hasLeader (boolean): draw a leader line, default false
  - orientation: "horizontal" | "vertical"
  - skipTagged (boolean): skip elements already tagged in this view, default true
  - apply (boolean): false = dry run (default), true = write to model`,
      inputSchema: z.object({
        viewId:          z.number().int().describe("Revit View Element ID"),
        elementIds:      z.array(z.number().int()).optional().describe("Explicit element ids to tag"),
        categories:      z.array(z.string()).optional().describe(`Categories to collect: ${CATEGORY_HINT}`),
        tagFamilyName:   z.string().optional().describe("Tag family name"),
        tagTypeName:     z.string().optional().describe("Tag type name"),
        offsetMm:        z.number().default(0).describe("Offset from element centre, mm"),
        offsetDirection: z.enum(["none", "up", "down", "left", "right"]).default("none"),
        hasLeader:       z.boolean().default(false),
        orientation:     z.enum(["horizontal", "vertical"]).default("horizontal"),
        skipTagged:      z.boolean().default(true).describe("Skip elements that already have a tag in this view"),
        includeLinks:    z.boolean().default(false).describe("Also collect from loaded RVT links"),
        seedElementId:   z.number().int().optional().describe("Override the auto-detected room with this element (e.g. the finish back wall)"),
        maxDepthMm:      z.number().default(2000).describe("seedElementId only: how far in front of the seed still counts as this room, mm"),
        roomSearchDepthMm: z.number().default(8000).describe("How far into the view to sample when auto-detecting the room, mm"),
        skipBlankMark:   z.boolean().default(true).describe("Skip elements whose type mark is empty"),
        markInclude:     z.string().optional().describe("Only tag elements whose type mark matches this regex"),
        markExclude:     z.string().optional().describe("Skip elements whose type mark matches this regex"),
        dedupeByMark:    z.boolean().default(false).describe("One tag per distinct mark, on the element nearest the back wall"),
        layout:          z.enum(["onElement", "sideLeaders"]).default("onElement"),
        leaderMarginMm:  z.number().default(500).describe("sideLeaders: how far outside the room the tag heads sit, mm"),
        leaderSpacingMm: z.number().default(400).describe("sideLeaders: minimum vertical gap between heads on one side, mm"),
        apply:           z.boolean().default(false).describe("false = dry run, true = write to model"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<TagElementsResult>("post", "/annotations/tags", undefined, params);
        const head = r.applied
          ? `✅ 已在 **${r.viewName}** 放置 ${r.placed.length} 個標籤`
          : `🔍 乾跑：**${r.viewName}** 預計放置 ${r.placed.length} 個標籤（尚未寫入模型）`;
        const text = [
          head,
          `- 標籤類型：${r.tagTypeUsed}`,
          r.scope ? `- 範圍：${r.scope}` : null,
          `- 候選元件：${r.candidates}`,
          r.placed.length ? "" : null,
          ...r.placed.slice(0, 40).map(p =>
            `  - [${p.elementId}] ${p.elementName || p.category}（${p.source}）${p.tagText ? ` → \`${p.tagText}\`` : ""}`),
          r.placed.length > 40 ? `  - …其餘 ${r.placed.length - 40} 個略` : null,
          r.skipped.length ? `\n略過 ${r.skipped.length} 個：` : null,
          ...r.skipped.slice(0, 20).map(s => `  - [${s.elementId}] ${s.reason}`),
          r.skipped.length > 20 ? `  - …其餘 ${r.skipped.length - 20} 個略` : null,
        ].filter(x => x !== null).join("\n");
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_dimension ───────────────────────────────────────────────
  server.registerTool(
    "revit_create_dimension",
    {
      title: "Create a Dimension Chain in a Revit View",
      description: `Create one dimension chain across several elements in a view — e.g. the
horizontal wall-to-partition-to-wall chain, or the vertical FL → fixture height → ceiling
chain on a toilet elevation.

DRY RUN BY DEFAULT: apply=false reports which references were found, the resulting segment
lengths in mm, and where the dimension line would sit — without touching the model.
Check the segments look right, then call again with apply=true.

How references are found: the tool reads each element's geometry as seen in this view and
picks the planar faces whose normal is parallel to the dimension direction (the two outermost
faces per element). Family instances with no usable face fall back to their centre reference
plane. Elements with nothing usable are reported in "skipped" — try the other direction.

Args:
  - viewId (number): target view
  - elementIds (number[]): at least 2 elements to chain together (order does not matter —
    references are sorted along the dimension direction)
  - direction: "horizontal" | "vertical" (in view coordinates)
  - offsetMm (number): distance from the elements' outer edge to the dimension line, default 500
  - side: "auto" | "top" | "bottom" | "left" | "right" — which side to put the line on
  - dimensionTypeName (string, optional): dimension style name; omitted = view default
  - apply (boolean): false = dry run (default), true = write to model`,
      inputSchema: z.object({
        viewId:            z.number().int().describe("Revit View Element ID"),
        elementIds:        z.array(z.number().int()).min(2).describe("Elements to chain (≥2)"),
        direction:         z.enum(["horizontal", "vertical"]).default("horizontal"),
        offsetMm:          z.number().default(500).describe("Dimension line offset from elements, mm"),
        side:              z.enum(["auto", "top", "bottom", "left", "right"]).default("auto"),
        dimensionTypeName: z.string().optional().describe("Dimension style name"),
        apply:             z.boolean().default(false).describe("false = dry run, true = write to model"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<DimensionResult>("post", "/annotations/dimensions", undefined, params);
        const head = r.applied
          ? `✅ 已在 **${r.viewName}** 建立尺寸鏈 [${r.dimensionId}]`
          : `🔍 乾跑：**${r.viewName}** 預計建立尺寸鏈（尚未寫入模型）`;
        const text = [
          head,
          `- 方向：${r.direction}　型式：${r.dimensionType}`,
          `- 分段 ${r.segmentCount} 段：${r.segmentsMm.join(" / ")} mm`,
          r.references.length ? "\n參考：" : null,
          ...r.references.map(f =>
            `  - [${f.elementId}] ${f.name || f.category} — ${f.refCount} 個參考面 @ ${Math.round(f.positionMm)} mm`),
          r.skipped.length ? `\n略過 ${r.skipped.length} 個：` : null,
          ...r.skipped.slice(0, 20).map(s => `  - [${s.elementId}] ${s.reason}`),
        ].filter(x => x !== null).join("\n");
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_crop_view ──────────────────────────────────────────────────────
  server.registerTool(
    "revit_crop_view",
    {
      title: "Re-crop a Revit View to Bounding Walls",
      description: `Reset a view's crop region so it stops at the bounding structural walls —
typically an interior elevation trimmed to the walls on either side instead of running out
into empty space.

Walls usually live in a LINKED model (RVT Link). This tool searches the host document AND every
loaded RevitLinkInstance visible in the view, so linked walls are found automatically.

What counts as "structural" here is a project convention, not Revit's structural flag:
a wall qualifies if its TYPE NAME contains one of includeTypeNames (default ["輕隔間"]) OR its
thickness is at least minThicknessMm (default 120, i.e. RC12 and above).

Only walls that RUN INTO the view are considered by default (wallRun="perpendicular") — an
interior elevation's side walls run away from the viewer and show cut, whereas the back wall
and corridor walls run across the view and must not set the left/right edge. wallRun="any"
disables that filter.

mode="room" (default): sample along the line of sight from the crop box, find the room this view
is actually drawing (host model or any loaded link), and crop straight to that room's boundary.
A room boundary IS the inner wall face, so no wall search is involved and nothing has to be picked
by hand. The room found is reported as "seed" — check it matches the room you expect.
mode="bounding": take the NEAREST qualifying wall just outside the seed on each side. The seed is
the auto-detected room unless seedElementId overrides it.
mode="extent": use the full span of every qualifying wall found.
mode="manual": set leftMm/rightMm/bottomMm/topMm directly in crop-box coordinates — use this to
restore an exact previous crop (the "before" values from an earlier call).

DRY RUN BY DEFAULT: apply=false reports which wall was picked for each side (with its link,
type name and thickness), the crop size before and after in mm, and changes nothing. The
"nearMiss" list shows walls in range that failed the rule with their type/thickness, so the
threshold can be tuned. Review, then call again with apply=true.

Note: cropping a view placed on a sheet changes the viewport size — the view may need
repositioning on the sheet afterwards.

Args:
  - viewId (number): the view to re-crop
  - mode: "room" (crop to the auto-detected room's boundary, default) | "bounding" (nearest wall
    each side) | "extent" (span of all qualifying walls) | "manual"
  - roomSearchDepthMm (number): how far into the view to sample when detecting the room, default 8000
  - axis: "horizontal" (default) | "vertical" | "both" — the other axis is left untouched
  - minThicknessMm (number): thickness that qualifies as structure, default 120
  - includeTypeNames (string[]): type-name keywords that qualify regardless of thickness,
    default ["輕隔間"]
  - excludeTypeNames (string[], optional): type-name keywords to reject outright
  - wallEdge: "inner" (default — crop lands on the wall FACE, i.e. the room boundary, so no wall
    body appears in frame) | "outer" (crop to the wall's far face, wall shows cut)
  - paddingMm (number): expand outwards; negative trims inwards. Default 0
  - searchPaddingMm (number): how far outside the current crop to look for walls, default 2000
  - includeLinks (boolean): search linked models, default true
  - elementIds (number[], optional): host-model elements to crop to, bypassing all rules
  - cropVisible (boolean, optional): show/hide the crop boundary; omitted = leave as is
  - annotationCropMm (number): enlarge the ANNOTATION crop by this much on all four sides, mm.
    Tightening the model crop makes Revit shove edge-hugging tags back inside and sprout leader
    lines; pushing the annotation crop out lets the tags stay where they were placed. 0 = untouched
  - apply (boolean): false = dry run (default), true = write to model`,
      inputSchema: z.object({
        viewId:           z.number().int().describe("Revit View Element ID"),
        mode:             z.enum(["room", "bounding", "extent", "manual"]).default("room"),
        seedElementId:    z.number().int().optional().describe("Override the auto-detected room with this element (e.g. the finish back wall)"),
        roomSearchDepthMm: z.number().default(8000).describe("How far into the view to sample when auto-detecting the room, mm"),
        wallRun:          z.enum(["perpendicular", "any"]).default("perpendicular"),
        leftMm:           z.number().optional().describe("manual mode: crop-box left, mm"),
        rightMm:          z.number().optional().describe("manual mode: crop-box right, mm"),
        bottomMm:         z.number().optional().describe("manual mode: crop-box bottom, mm"),
        topMm:            z.number().optional().describe("manual mode: crop-box top, mm"),
        axis:             z.enum(["horizontal", "vertical", "both"]).default("horizontal"),
        minThicknessMm:   z.number().default(120).describe("Thickness qualifying as structure, mm"),
        includeTypeNames: z.array(z.string()).optional().describe('Type-name keywords, default ["輕隔間"]'),
        excludeTypeNames: z.array(z.string()).optional().describe("Type-name keywords to reject"),
        wallEdge:         z.enum(["inner", "outer"]).default("inner"),
        paddingMm:        z.number().default(0).describe("Expand outwards (negative trims inwards), mm"),
        searchPaddingMm:  z.number().default(2000).describe("Search margin outside current crop, mm"),
        includeLinks:     z.boolean().default(true).describe("Also search linked models"),
        elementIds:       z.array(z.number().int()).optional().describe("Host elements to crop to, bypassing rules"),
        cropVisible:      z.boolean().optional().describe("Show/hide crop boundary; omit to leave as is"),
        annotationCropMm: z.number().default(0).describe("Push the ANNOTATION crop this far outside the model crop, mm; 0 = leave alone"),
        apply:            z.boolean().default(false).describe("false = dry run, true = write to model"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<CropViewResult>("post", "/views/crop", undefined, params);
        const b = r.before, a = r.after;
        const head = r.applied
          ? `✅ 已重設 **${r.viewName}** 的裁切範圍`
          : `🔍 乾跑：**${r.viewName}** 的裁切範圍預計調整（尚未寫入模型）`;
        const line = (label: string, m?: CropCandidateDto) =>
          m ? `  - ${label}：[${m.elementId}] ${m.typeName || "(無類型名)"} ${m.thicknessMm}mm — ${m.matchedBy}（${m.source}）` : null;
        const text = [
          head,
          `- 模式：${r.mode}　軸向：${r.axis}`,
          r.seed ? `- 種子：${r.seed}` : null,
          `- 判定規則：${r.rule}`,
          r.searchedLinks.length ? `- 搜尋的連結：${r.searchedLinks.join("、")}` : "- 未搜尋連結",
          b && a ? `- 裁切前：${b.widthMm} × ${b.heightMm} mm　→　裁切後：${a.widthMm} × ${a.heightMm} mm` : null,
          (r.leftWall || r.rightWall || r.bottomRef || r.topRef) ? "\n選定邊界：" : null,
          line("左", r.leftWall), line("右", r.rightWall),
          line("下", r.bottomRef), line("上", r.topRef),
          r.matched.length ? `\n符合條件的牆共 ${r.matched.length} 道` : null,
          r.nearMiss.length ? `\n範圍內被篩掉的 ${r.nearMiss.length} 道（供調整門檻）：` : null,
          ...r.nearMiss.slice(0, 12).map(m =>
            `  - [${m.elementId}] ${m.typeName || "(無類型名)"} ${m.thicknessMm}mm（${m.source}）`),
          r.nearMiss.length > 12 ? `  - …其餘 ${r.nearMiss.length - 12} 道略` : null,
          ...r.skipped.map(s => `\n⚠ ${s.reason}`),
        ].filter(x => x !== null).join("\n");
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_delete_view_tags ───────────────────────────────────────────────
  // revit_tag_elements 跑錯時的退路。
  server.registerTool(
    "revit_delete_view_tags",
    {
      title: "Delete tags in a view",
      description: `刪掉某個視圖裡的標籤（revit_tag_elements 標錯時的退路）。
tagIds 若有給，tagFamilyName / tagTypeName / blankTextOnly 一律被忽略。
⚠ apply=true 會直接刪除模型元素、沒有二次確認，而且除了在 Revit 視窗按 Ctrl+Z 沒有其他還原方式。
一律先用 apply=false 預檢，把要刪的清單給使用者確認後再執行。沒有任何過濾條件時，apply=true 會刪掉該視圖裡「所有」標籤。
Args:
  - viewId: 視圖 ElementId
  - tagIds: 指定要刪的標籤 ElementId；給了就忽略其他過濾條件
  - tagFamilyName / tagTypeName: 只刪這個標籤族／類型的
  - blankTextOnly: 只刪文字為空的標籤（預設 false）
  - apply: false = 預檢（只列出將刪除的標籤，不改模型）；確認後再用 true 執行`,
      inputSchema: z.object({
        viewId:        z.number().int().describe("視圖 ElementId"),
        tagIds:        z.array(z.number().int()).optional().describe("指定要刪的標籤 ElementId；給了就忽略其他過濾條件"),
        tagFamilyName: z.string().optional(),
        tagTypeName:   z.string().optional(),
        blankTextOnly: z.boolean().default(false),
        apply:         z.boolean().default(false),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async (a) => {
      try {
        const r = await revitRequest<unknown>("post", "/annotations/deletetags", undefined, a);
        return { content: [{ type: "text", text: "```json\n" + JSON.stringify(r, null, 2) + "\n```" }], structuredContent: r as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );
}
