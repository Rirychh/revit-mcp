// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { PINNED_PORT } from "../constants.js";
import {
  discoverInstances,
  describeInstance,
  getExplicitTarget,
  setSelectedPort,
  type RevitInstance,
} from "./instances.js";

/**
 * 多個 Revit 同時開著時，避免「會改模型的工具」改到錯的檔。
 *
 *  - 每個非唯讀工具自動多一個選填參數 document（檔名關鍵字）。
 *    有給 → 找作用中檔名符合的那個 Revit，切過去並鎖定該檔。
 *  - 沒給 → 只開一個 Revit 就直接用；開了兩個以上，必須先用 document
 *    或 revit_use_instance 明確指定，否則擋下不執行。
 *  - 明確指定後，那個 Revit 的作用中檔案若換掉了，也擋下。
 *  - 每次執行結果第一行標出實際作用的 port 與檔名。
 *
 * 工具自己就有 document 參數時（revit_activate_document、revit_save_document、
 * revit_link_cad…：那個 document 是「要處理哪一個已開啟的檔」，不只是選實例）：
 *  - 不覆寫工具原本的 schema（必填就維持必填）。
 *  - document 會「同時」用來選實例、並原樣轉交給 handler。
 *    舊版在呼叫 callback 前就把 document 剝掉，導致 revit_activate_document 之類
 *    永遠收不到檔名（送出 document=undefined）。
 *  - 選實例時放寬為「該檔在這個 Revit 裡有開著」即可，不必是作用中檔案 ——
 *    否則 activate_document 要指定的正好是「還不是作用中」的那個檔，必然被擋死。
 *    仍然保留所有防護：對不到、對到多個、忙碌中、鎖定檔被換掉，一律擋下。
 */

const SKIP = new Set(["revit_use_instance", "revit_list_instances"]);

/** 工具的 inputSchema 自己是否已經宣告 document（zod object 或 raw shape 物件）。 */
function declaresDocument(schema: any): boolean {
  if (!schema || typeof schema !== "object") return false;
  const shape = typeof schema._def?.shape === "function" ? schema._def.shape()
              : schema.shape ?? schema;
  return Boolean(shape && typeof shape === "object" && "document" in shape);
}

const list = (xs: RevitInstance[]) => xs.map((i) => `  - ${describeInstance(i)}`).join("\n");
const lc = (s: string | undefined) => (s ?? "").toLowerCase();

/**
 * @param document  使用者給的檔名關鍵字
 * @param ownDoc    true = 這個 document 是工具自己的參數（指名要處理的已開啟檔），
 *                  比對時放寬到「有開著」即可，不限作用中檔案。
 */
async function chooseTarget(document?: string, ownDoc = false): Promise<RevitInstance> {
  const found = await discoverInstances();

  if (PINNED_PORT !== null) {
    const t = found.find((i) => i.port === PINNED_PORT);
    if (!t) throw new Error(`REVIT_PORT=${PINNED_PORT} 上沒有 Revit。`);
    if (t.busy) throw new Error(`${describeInstance(t)} 沒有回應，請先切回該 Revit 處理對話框或等它忙完。`);
    if (document) {
      const n = lc(document);
      const isActive = lc(t.activeDocument).includes(n) || lc(t.activeDocumentPath).includes(n);
      const isOpen   = ownDoc && t.documents.some((d) => lc(d).includes(n));
      if (!isActive && !isOpen)
        throw new Error(
          `已釘死在 port ${PINNED_PORT}，但${ownDoc ? "它沒有開著符合" : `它的作用中檔案是「${t.activeDocument}」，不符合`} "${document}"。`
        );
    }
    return t;
  }

  if (found.length === 0)
    throw new Error("找不到執行中的 Revit（或 add-in 未載入）。");

  // ── 有指定 document ──
  if (document) {
    const n = lc(document);
    const hit = found.filter((i) => !i.busy && (lc(i.activeDocument).includes(n) || lc(i.activeDocumentPath).includes(n)));
    if (hit.length === 1) {
      setSelectedPort(hit[0].port, true, hit[0].activeDocument);
      return hit[0];
    }
    if (hit.length > 1)
      throw new Error(`"${document}" 同時符合多個 Revit 的作用中檔案，請用更完整的檔名：\n${list(hit)}`);
    const openedNotActive = found.filter((i) => i.documents.some((d) => lc(d).includes(n)));
    if (ownDoc) {
      // 工具自己吃 document：指名的檔只要在某個 Revit 裡開著就算對到。
      const live = openedNotActive.filter((i) => !i.busy);
      if (live.length === 1) {
        setSelectedPort(live[0].port, true, live[0].activeDocument);
        return live[0];
      }
      if (live.length > 1)
        throw new Error(`"${document}" 同時在多個 Revit 裡開著，請用更完整的檔名：\n${list(live)}`);
    } else if (openedNotActive.length) {
      throw new Error(
        `"${document}" 有開著，但不是該 Revit 的作用中檔案（工具只作用在作用中檔案）。` +
        `請在 Revit 切到該檔的視窗後重試：\n${list(openedNotActive)}`
      );
    }
    const busy = found.filter((i) => i.busy);
    throw new Error(
      `沒有 Revit 的作用中檔案符合 "${document}"。目前：\n${list(found)}` +
      (busy.length ? `\n（有 ${busy.length} 個 Revit 沒回應，讀不到它開的檔案）` : "")
    );
  }

  // ── 沒指定 document ──
  if (found.length === 1) {
    const only = found[0];
    if (only.busy) throw new Error(`${describeInstance(only)} 沒有回應，請先切回 Revit 處理對話框或等它忙完。`);
    const ex = getExplicitTarget();
    if (ex && ex.port === only.port && ex.lockedDoc && ex.lockedDoc !== only.activeDocument)
      throw new Error(
        `先前指定的檔案是「${ex.lockedDoc}」，但這個 Revit 的作用中檔案已換成「${only.activeDocument}」。` +
        `請確認後加 document 參數重新指定。`
      );
    setSelectedPort(only.port);
    return only;
  }

  const ex = getExplicitTarget();
  if (ex) {
    const t = found.find((i) => i.port === ex.port);
    if (!t) throw new Error(`先前指定的 port ${ex.port} 已關閉。目前：\n${list(found)}\n請加 document 參數重新指定。`);
    if (t.busy) throw new Error(`${describeInstance(t)} 沒有回應，請先切回該 Revit 處理對話框或等它忙完。`);
    if (ex.lockedDoc && t.activeDocument !== ex.lockedDoc)
      throw new Error(
        `先前指定的是 port ${t.port} 的「${ex.lockedDoc}」，但它的作用中檔案已換成「${t.activeDocument}」。` +
        `請加 document 參數重新指定。`
      );
    setSelectedPort(t.port);
    return t;
  }

  throw new Error(
    `偵測到 ${found.length} 個 Revit 同時開著。為避免改到錯的檔，這個工具會改模型，需要先指定目標：` +
    `加 document 參數（作用中檔名的關鍵字），或先用 revit_use_instance。\n${list(found)}`
  );
}

const DOC_DESC = "目標檔名關鍵字（作用中檔案）。多個 Revit 同時開著時必填，或先用 revit_use_instance。";

/** 必須在註冊任何工具之前呼叫。 */
export function installTargetGuard(server: McpServer): void {
  const orig = server.registerTool.bind(server) as (...a: any[]) => any;

  (server as any).registerTool = (name: string, config: any, cb: any) => {
    if (SKIP.has(name) || config?.annotations?.readOnlyHint === true) return orig(name, config, cb);

    const had = config?.inputSchema !== undefined;
    const ownDoc = had && declaresDocument(config.inputSchema);
    const docField = z.string().optional().describe(DOC_DESC);

    // 工具自己已經有 document → 保留它原本的 schema（必填／說明都不動），
    // 只有在它沒有的時候才注入一個選填的 document。
    const inputSchema = !had
      ? z.object({ document: docField })
      : ownDoc
        ? config.inputSchema
        : typeof config.inputSchema.extend === "function"
          ? config.inputSchema.extend({ document: docField })
          : { ...config.inputSchema, document: docField };

    const note = ownDoc
      ? `\n\n多個 Revit 同時開著時：這個工具的 document 參數同時用來選實例（該檔在哪個 Revit 裡開著就送去那邊），並原樣交給 Revit 處理；沒指定且開著兩個以上會擋下不執行。`
      : `\n\n多個 Revit 同時開著時：加 document（作用中檔名關鍵字）指定目標，或先用 revit_use_instance；沒指定會擋下不執行。`;
    const description = `${config?.description ?? ""}${note}`;

    const wrapped = async (args: any, extra: any) => {
      const { document, ...rest } = args ?? {};
      let t: RevitInstance;
      try { t = await chooseTarget(document, ownDoc); }
      catch (e) { return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }], isError: true }; }
      // ownDoc 的工具必須收到 document，否則 handler 會以為「沒指定 → 作用中檔案」。
      const callArgs = ownDoc && document !== undefined ? { ...rest, document } : rest;
      const res = had ? await cb(callArgs, extra) : await cb(extra);
      const tag = { type: "text", text: `[目標] port ${t.port}｜${t.activeDocument}` };
      return { ...res, content: [tag, ...(res?.content ?? [])] };
    };

    return orig(name, { ...config, description, inputSchema }, wrapped);
  };
}
