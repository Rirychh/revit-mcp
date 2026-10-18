// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { ResponseFormat, PINNED_PORT } from "../constants.js";
import { revitRequest } from "../services/revitClient.js";
import {
  discoverInstances,
  getSelectedPort,
  setSelectedPort,
  describeInstance,
  type RevitInstance,
} from "../services/instances.js";

export function registerInstanceTools(server: McpServer): void {

  // ── revit_list_instances ─────────────────────────────────────────────────
  server.registerTool(
    "revit_list_instances",
    {
      title: "List running Revit instances",
      description: `List every running Revit instance that has the MCP add-in loaded.

Use this when more than one Revit is open, to see which instance is which
(port, process id, Revit version, and the document each one currently has active).
The instance marked "ACTIVE" is the one all other revit_* tools will act on.

Args:
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ response_format }) => {
      try {
        const instances = await discoverInstances();
        const current   = getSelectedPort();

        if (response_format === ResponseFormat.JSON) {
          return {
            content: [{ type: "text", text: JSON.stringify({ current, pinned: PINNED_PORT, instances }, null, 2) }],
            structuredContent: { instances } as unknown as Record<string, unknown>,
          };
        }

        const text = instances.length === 0
          ? "找不到執行中的 Revit（或 add-in 未載入）。"
          : [
              `# 執行中的 Revit 實例 (${instances.length})`,
              PINNED_PORT !== null ? `\n> 已由 REVIT_PORT 釘死在 ${PINNED_PORT}\n` : "",
              ...instances.map((i) => {
                const mark = i.port === current ? " **← ACTIVE**" : "";
                const docs = i.documents.length ? i.documents.join("\n    - ") : "(無)";
                return [
                  `- **port ${i.port}**${mark}`,
                  `    Revit ${i.revitVersion} (build ${i.revitBuild})  |  pid ${i.pid}`,
                  `    目前文件：${i.activeDocument || "(尚未開啟模型)"}`,
                  `    目前視圖：${i.activeView || "-"}`,
                  `    已開啟：\n    - ${docs}`,
                ].join("\n");
              }),
            ].join("\n");

        return {
          content: [{ type: "text", text }],
          structuredContent: { instances } as unknown as Record<string, unknown>,
        };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_use_instance ───────────────────────────────────────────────────
  server.registerTool(
    "revit_use_instance",
    {
      title: "Select which Revit instance to control",
      description: `Choose which running Revit instance all subsequent revit_* tools should act on.

Identify the target either by its port (from revit_list_instances) or by "match" —
a case-insensitive substring of the document name, file path, Revit version, or pid.
The selection stays in effect for the rest of this session, until changed.

Args:
  - port  (number, optional): Exact port, e.g. 48885
  - match (string, optional): Substring of document name / path / version / pid`,
      inputSchema: z.object({
        port:  z.number().int().optional().describe("Exact port of the target instance"),
        match: z.string().optional().describe("Substring of document name, path, Revit version, or pid"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ port, match }) => {
      try {
        if (port === undefined && !match)
          throw new Error("請至少提供 port 或 match 其中一個。");

        if (PINNED_PORT !== null)
          throw new Error(
            `這個 MCP server 已由環境變數 REVIT_PORT=${PINNED_PORT} 釘死，無法在對話中切換。` +
            `若要動態切換，請移除 Claude 設定檔中的 REVIT_PORT。`
          );

        const instances = await discoverInstances();
        if (instances.length === 0)
          throw new Error("找不到執行中的 Revit。請先開啟 Revit 並確認 add-in 已載入。");

        let target: RevitInstance | undefined;

        if (port !== undefined) {
          target = instances.find((i) => i.port === port);
          if (!target)
            throw new Error(
              `port ${port} 上沒有 Revit。目前可用：\n` +
              instances.map((i) => `  - ${describeInstance(i)}`).join("\n")
            );
        } else {
          const needle  = match!.toLowerCase();
          const matched = instances.filter((i) =>
            [i.activeDocument, i.activeDocumentPath, i.revitVersion, String(i.pid), ...i.documents]
              .some((f) => (f ?? "").toLowerCase().includes(needle))
          );

          if (matched.length === 0)
            throw new Error(
              `沒有實例符合 "${match}"。目前可用：\n` +
              instances.map((i) => `  - ${describeInstance(i)}`).join("\n")
            );
          if (matched.length > 1)
            throw new Error(
              `"${match}" 同時符合多個實例，請改用 port 指定：\n` +
              matched.map((i) => `  - ${describeInstance(i)}`).join("\n")
            );
          target = matched[0];
        }

        if (target.busy)
          throw new Error(`${describeInstance(target)} 目前沒有回應，請先切回該 Revit 處理對話框或等它忙完。`);
        setSelectedPort(target.port, true, target.activeDocument);
        return {
          content: [{ type: "text", text: `已切換到 ${describeInstance(target)}` }],
          structuredContent: { selected: target } as unknown as Record<string, unknown>,
        };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_instance_info ──────────────────────────────────────────────────
  // GET /info 的直通工具。revit_list_instances 掃整個埠範圍（可能慢、也可能有忙碌的
  // 實例讀不到）；這支只問「目前選定的那一個」，回原始 /info 內容。
  server.registerTool(
    "revit_instance_info",
    {
      title: "Info about the currently targeted Revit instance",
      description: `回報目前選定的那個 Revit 實例的身分（埠、pid、版本、使用者、作用中文件與視圖、已開啟文件清單），直接回傳 add-in GET /info 的原始內容。
要「一次看完所有開著的 Revit」請用 revit_list_instances；要看作用中模型的細節（路徑、連結、基準點座標）請用 revit_document_info。`,
      inputSchema: z.object({}).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async () => {
      try {
        const r = await revitRequest<unknown>("get", "/info");
        return { content: [{ type: "text", text: "```json\n" + JSON.stringify(r, null, 2) + "\n```" }], structuredContent: r as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );
}
