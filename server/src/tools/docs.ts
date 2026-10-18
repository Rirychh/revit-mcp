// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

const LONG = 600_000;
const txt = (o: unknown) => ({ content: [{ type: "text" as const, text: JSON.stringify(o, null, 2) }] });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const docArg = z.string().optional().describe("Target document: title or part of its path. Omit = active document");

export function registerDocTools(server: McpServer): void {
  server.registerTool("revit_document_info", {
    title: "Document Info",
    description: `Report one open document (by title/path fragment, or the active one): path, unsaved changes, workshared, Revit links (loaded or not), project base point shared coordinates (TWD97 m, angle to true north), and the list of open documents.`,
    inputSchema: z.object({ document: docArg }).strict(),
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ document }) => {
    try { return txt(await revitRequest("get", "/documents/info", { document })); } catch (e) { return err(e); }
  });

  server.registerTool("revit_save_document", {
    title: "Save Document",
    description: `Save an open document in place (Ctrl+S equivalent), by title/path fragment; does not need to be the active document.
Workshared local files are saved locally only — this never synchronizes with central. Refuses a central model itself and never-saved documents.`,
    inputSchema: z.object({ document: docArg }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ document }) => {
    try { return txt(await revitRequest("post", "/documents/save", undefined, { document }, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_activate_document", {
    title: "Activate Document",
    description: `Make an already-open document the active one, so the other revit_* tools act on it.`,
    inputSchema: z.object({ document: z.string().describe("Title or path fragment of an open document") }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ document }) => {
    try { return txt(await revitRequest("post", "/documents/activate", undefined, { document }, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_link_revit", {
    title: "Link Revit Model",
    description: `Link a .rvt into a document (default placement origin-to-origin; "shared" = by shared coordinates), optionally acquire its shared coordinates.
Revit refuses to link a file that is open in the same session: closeIfOpen=true closes it first, only when it has no unsaved changes.
Reuses an existing link of the same file instead of placing a second copy.`,
    inputSchema: z.object({
      document: docArg,
      path: z.string().describe("Full path of the .rvt to link"),
      placement: z.enum(["origin", "shared"]).default("origin"),
      acquireCoordinates: z.boolean().default(false),
      closeIfOpen: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/links/revit", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_clear_model", {
    title: "Clear Model Elements",
    description: `Delete model element instances (walls, floors, doors, furniture, rooms, model groups, site, mass…) from a document — e.g. the sample building that ships inside a project template. Views, sheets, levels, grids, annotations and types are kept.
apply=false (default) only counts per category; apply=true deletes in one transaction, auto-resolving Revit errors and dismissing warnings.
Optional categories: list of OST_ names to restrict the set.`,
    inputSchema: z.object({
      document: docArg,
      categories: z.array(z.string()).optional(),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/documents/clear-model", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_open_document", {
    title: "Open Document",
    description: `Open a .rvt (local path or UNC network path) in the running Revit and make it the active document. If it is already open it is simply activated.
Opening never closes or saves any other open document. Large models can take a few minutes.`,
    inputSchema: z.object({
      path: z.string().describe("Full path of the .rvt to open"),
      audit: z.boolean().default(false).describe("Open with Audit"),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/documents/open", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_list_design_options", {
    title: "List Design Options",
    description: `List design option sets and their options in a document: ids, names, which is primary, which is the ACTIVE option, and how many model elements each option (and the main model) holds.
Revit's API cannot create option sets/options, switch the active option, or move elements between options; those are UI-only. Elements created through revit_* tools land in the currently active option.`,
    inputSchema: z.object({ document: docArg }).strict(),
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ document }) => {
    try { return txt(await revitRequest("get", "/design-options", { document })); } catch (e) { return err(e); }
  });

  server.registerTool("revit_select_elements", {
    title: "Select Elements",
    description: `Select elements by id in the Revit window of the ACTIVE document (replaces the current selection), optionally zooming to them.
Use before a UI-only command such as Manage > Design Options > Add to Set.`,
    inputSchema: z.object({
      document: docArg,
      elementIds: z.array(z.number().int()).min(1).max(5000),
      zoom: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/selection", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_mass_boxes", {
    title: "Create Mass Boxes",
    description: `Create conceptual masses (one Metric Mass family per box: extruded rectangle or polygon loop) in a project, optionally creating/updating levels and adding mass floors so floor areas are reported.
Coordinates mm in project internal coordinates; zMin/zMax are absolute elevations. replace=true (default) deletes same-named mass families first.
Instances land in the currently active design option.`,
    inputSchema: z.object({
      document: docArg,
      boxes: z.array(z.object({
        name: z.string().describe("Family name, unique per box"),
        comment: z.string().optional(),
        xMin: z.number().optional(), yMin: z.number().optional(),
        xMax: z.number().optional(), yMax: z.number().optional(),
        loop: z.array(z.array(z.number()).length(2)).optional().describe("Polygon [[x,y],...] mm; overrides the rectangle"),
        zMin: z.number(), zMax: z.number(),
        massFloors: z.array(z.string()).optional().describe("Level names to cut mass floors at"),
      })).min(1),
      levels: z.array(z.object({ name: z.string(), elevationMm: z.number() })).optional(),
      familyFolder: z.string().optional(),
      replace: z.boolean().default(true),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/mass/boxes", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_list_documents", {
    title: "List Open Documents",
    description: `List every open, non-linked document: title, path, whether it is the active one, whether it has unsaved changes, whether it is a family file, whether it is workshared.
Use it to get the exact title for revit_close_document / revit_activate_document.`,
    inputSchema: z.object({}).strict(),
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async () => {
    try { return txt(await revitRequest("get", "/documents")); } catch (e) { return err(e); }
  });

  server.registerTool("revit_close_document", {
    title: "Close Document",
    description: `Close an open document.
⚠ 這是破壞性操作：force=true 會丟棄所有未存檔的變更且無法復原。dryRun=true（預設）只回報將關閉什麼；確認後才用 dryRun=false 真的關閉。
title must be EXACTLY Document.Title or Document.PathName (no substring matching) — get it from revit_list_documents.
Args:
  - title: exact document title or full path
  - save: save before closing (default false)
  - force: discard unsaved changes (default false)
  - dryRun: true = preview (default); false = actually close`,
    inputSchema: z.object({
      title:  z.string().min(1).describe("Exact Document.Title or full path"),
      save:   z.boolean().default(false),
      force:  z.boolean().default(false),
      dryRun: z.boolean().default(true),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/documents/close", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_sync_status", {
    title: "Worksharing Status",
    description: `Report worksharing status of the active document: whether it is workshared, the central file path, the current user, and whether there are unsaved changes.
Call this before revit_sync_with_central.`,
    inputSchema: z.object({}).strict(),
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async () => {
    try { return txt(await revitRequest("get", "/sync/status")); } catch (e) { return err(e); }
  });

  server.registerTool("revit_sync_with_central", {
    title: "Synchronize with Central",
    description: `Synchronize a workshared model with its central file, optionally saving the local file and relinquishing borrowed worksets.
⚠⚠ 這是最危險的一支：apply=true 會直接寫入中央模型，變更對團隊所有人立即可見，而且無法復原（沒有 undo）。
一定要先用 apply=false 看它會做什麼、並明確向使用者取得同意後才執行。先用 revit_sync_status 確認這個檔真的是工作共用的本機檔。
Args:
  - apply: false = 只回報將做什麼（預設）；true = 真的同步寫入中央檔
  - comment: sync comment
  - relinquish: relinquish all borrowed worksets after sync (default true)
  - compact: compact the central file (slower, default false)
  - saveLocalBefore / saveLocalAfter: save the local file before / after sync (default true)`,
    inputSchema: z.object({
      apply:           z.boolean().default(false),
      comment:         z.string().default(""),
      relinquish:      z.boolean().default(true),
      compact:         z.boolean().default(false),
      saveLocalBefore: z.boolean().default(true),
      saveLocalAfter:  z.boolean().default(true),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/sync", undefined, a, 30 * 60 * 1000)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_link_cad", {
    title: "Link CAD (DWG) files",
    description: `Link DWG files into the floor plan of a given level.
⚠ 沒有 apply／dryRun 預覽開關：每次呼叫都直接開 Transaction 寫入模型。請先確認路徑與樓層再呼叫。
Args:
  - document: target document keyword; omit = active document
  - paths: full paths of the DWGs to link
  - level: which level's floor plan to link into
  - thisViewOnly: visible in that view only (default false)
  - pin: pin the link after placing (default true)`,
    inputSchema: z.object({
      document:     docArg,
      paths:        z.array(z.string().min(1)).min(1).describe("Full paths of the DWG files"),
      level:        z.string().optional().describe("Level whose floor plan the CAD is linked into"),
      thisViewOnly: z.boolean().default(false),
      pin:          z.boolean().default(true),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/links/cad", undefined, a, LONG)); } catch (e) { return err(e); }
  });

  server.registerTool("revit_move_link", {
    title: "Move a Revit link instance",
    description: `Move one Revit link instance, and optionally acquire its shared coordinates / set the project base point elevation.
⚠ 沒有 apply／dryRun 預覽開關：每次呼叫都直接開 Transaction 寫入模型。
nameContains must match EXACTLY ONE link, otherwise the handler refuses (deliberate protection against moving the wrong link).
Args:
  - document: target document keyword; omit = active document
  - nameContains: link name keyword, must match exactly one link
  - dxMm / dyMm / dzMm: translation in mm (default 0)
  - acquireCoordinates: acquire shared coordinates from the link (default false)
  - setElevationM: set the project base point elevation (m)`,
    inputSchema: z.object({
      document:           docArg,
      nameContains:       z.string().optional().describe("Link name keyword; must match exactly one link"),
      dxMm:               z.number().default(0),
      dyMm:               z.number().default(0),
      dzMm:               z.number().default(0),
      acquireCoordinates: z.boolean().default(false),
      setElevationM:      z.number().optional(),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    try { return txt(await revitRequest("post", "/links/move", undefined, a, LONG)); } catch (e) { return err(e); }
  });
}