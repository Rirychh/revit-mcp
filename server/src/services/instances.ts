// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import axios from "axios";
import {
  PORT_RANGE_START,
  PORT_RANGE_COUNT,
  PINNED_PORT,
  PROBE_TIMEOUT_MS,
} from "../constants.js";
import { authHeaders, invalidateToken, tokenFilePath } from "./token.js";

/**
 * 一律用 127.0.0.1，不要用 "localhost"。
 * add-in 的 HttpListener 只綁 http://127.0.0.1:{port}/，而 Node 18+ 解析
 * "localhost" 時可能先拿到 ::1，連過去會 ECONNREFUSED。
 */
export const baseUrlFor = (port: number): string => `http://127.0.0.1:${port}`;

/** 一個正在執行、且載入了 MCP add-in 的 Revit 實例。 */
export interface RevitInstance {
  port:               number;
  pid:                number;
  revitVersion:       string;
  revitBuild:         string;
  username:           string;
  activeDocument:     string;
  activeDocumentPath: string;
  activeView:         string;
  documents:          string[];
  /** true = 埠有人聽，但讀不到內容（主執行緒忙碌、或認證失敗）。 */
  busy?:              boolean;
  /** true = add-in 在那裡，但我們沒有正確的 token（檔案被刪或 Revit 需重啟）。 */
  authFailed?:        boolean;
}

/** 這個 MCP session 目前選定的埠（PINNED_PORT 優先）。 */
let selectedPort: number | null = null;

/**
 * 使用者明確指定的目標（revit_use_instance 或工具的 document 參數）。
 * lockedDoc 記下指定當下的作用中檔名；之後那個 Revit 換了作用中檔案，
 * 會改模型的工具就會擋下，避免改到別的檔。
 */
let explicitPort: number | null = null;
let lockedDoc:    string | null = null;

export function getSelectedPort(): number | null {
  return PINNED_PORT ?? selectedPort;
}

export function setSelectedPort(port: number, explicit = false, activeDocument?: string): void {
  selectedPort = port;
  if (explicit) {
    explicitPort = port;
    lockedDoc    = activeDocument ?? null;
  }
}

export function getExplicitTarget(): { port: number; lockedDoc: string | null } | null {
  return explicitPort === null ? null : { port: explicitPort, lockedDoc };
}

/** Revit 被關掉時清掉選擇，下次呼叫會重新探索。 */
export function clearSelectedPort(): void {
  selectedPort = null;
  explicitPort = null;
  lockedDoc    = null;
}

const portRange = (): number[] =>
  Array.from({ length: PORT_RANGE_COUNT }, (_, i) => PORT_RANGE_START + i);

/**
 * 帶 token 的 GET。回 null = 連不上／逾時（那個埠沒有東西在聽，或 Revit 卡住）。
 * 收到 401 時重讀一次 token 檔再試 —— add-in 重啟會換 token，快取裡的會過期。
 * 沒有 token 檔時仍會打一次不帶 token 的請求，純粹為了分辨
 * 「Revit 沒開」(連不上) 與「Revit 開著但我們缺 token」(401)，不會因此取得資料。
 */
async function authedGet(
  port: number,
  path: string,
  timeout: number
): Promise<{ status: number; data: any } | null> {
  for (let attempt = 0; attempt < 2; attempt++) {
    let headers: Record<string, string> | undefined;
    try {
      headers = await authHeaders(port);
    } catch {
      headers = undefined;
    }

    try {
      const r = await axios.get(`${baseUrlFor(port)}${path}`, {
        timeout,
        headers,
        validateStatus: () => true,
      });
      if (r.status === 401 && headers !== undefined && attempt === 0) {
        invalidateToken(port);
        continue;
      }
      return { status: r.status, data: r.data };
    } catch {
      return null;
    }
  }
  return null;
}

const unauthorizedInstance = (port: number): RevitInstance => ({
  port,
  busy:               true,
  authFailed:         true,
  pid:                0,
  revitVersion:       "?",
  revitBuild:         "?",
  username:           "",
  activeDocument:     "(認證失敗，讀不到)",
  activeDocumentPath: "",
  activeView:         "",
  documents:          [],
});

/** 對單一埠打 /info；不是我們的 add-in 就回 null。 */
async function probe(port: number): Promise<RevitInstance | null> {
  const info = await authedGet(port, "/info", PROBE_TIMEOUT_MS);

  if (info !== null) {
    if (info.status === 401 || info.status === 403) return unauthorizedInstance(port);
    const body = info.data;
    if (info.status < 400)
      return body?.success && body?.data?.server === "revit-mcp-addin"
        ? ({ ...body.data, port, busy: false } as RevitInstance)
        : null; // 那個埠上是別的服務
    // 4xx/5xx：可能是 Revit 主執行緒出問題，往下用 /ping 再判斷一次
  }

  // /info 要排隊進 Revit 主執行緒，Revit 忙碌或有對話框擋著就會逾時。
  // 改打不經主執行緒的 /ping，分辨「Revit 沒開」與「Revit 開著但沒空回應」。
  const p = await authedGet(port, "/ping", PROBE_TIMEOUT_MS);
  if (p === null) return null;
  if (p.status === 401 || p.status === 403) return unauthorizedInstance(port);
  if (p.data?.data?.server !== "revit-mcp-addin") return null;
  return {
    port,
    busy:               true,
    pid:                p.data.data.pid ?? 0,
    revitVersion:       "?",
    revitBuild:         "?",
    username:           "",
    activeDocument:     "(忙碌中，讀不到)",
    activeDocumentPath: "",
    activeView:         "",
    documents:          [],
  };
}

/** 掃描整個埠範圍，回傳目前開著的所有 Revit。 */
export async function discoverInstances(): Promise<RevitInstance[]> {
  const results = await Promise.all(portRange().map(probe));
  return results
    .filter((r): r is RevitInstance => r !== null)
    .sort((a, b) => a.port - b.port);
}

export function describeInstance(i: RevitInstance): string {
  const doc = i.activeDocument || "(尚未開啟模型)";
  const flag = i.authFailed
    ? " | ⚠ 認證失敗（缺少或過期的 token，請重新啟動該 Revit）"
    : i.busy
      ? " | ⚠ 忙碌中／未回應"
      : "";
  return `port ${i.port} | Revit ${i.revitVersion} | pid ${i.pid} | ${doc}${flag}`;
}

/**
 * 決定這次請求要送到哪一個 Revit。
 *  - 有 REVIT_PORT 或已選定 → 直接用
 *  - 只找到一個 → 自動選它
 *  - 找到多個 → 丟出錯誤並列出選項，請先用 revit_use_instance 指定
 */
export async function resolvePort(): Promise<number> {
  const current = getSelectedPort();
  if (current !== null) return current;

  const found = await discoverInstances();

  if (found.length === 0) {
    throw new Error(
      `找不到執行中的 Revit（已掃描 ${PORT_RANGE_START}-${PORT_RANGE_START + PORT_RANGE_COUNT - 1}）。` +
        `請確認 Revit 已開啟且 RevitMcpAddin 已載入。`
    );
  }

  if (found.every((i) => i.authFailed)) {
    throw new Error(
      `Revit 有開著（port ${found.map((i) => i.port).join(", ")}），但認證失敗（401/403）。` +
        `add-in 每次啟動會把 token 寫到 ${tokenFilePath(found[0].port)}（檔名隨埠號）；` +
        `若該檔被刪除或與執行中的 add-in 不符，請重新啟動 Revit 讓它重新產生。`
    );
  }

  if (found.every((i) => i.busy)) {
    throw new Error(
      `Revit 有開著（port ${found.map((i) => i.port).join(", ")}），但沒有回應 —— ` +
        `通常是有對話框擋著、正在開啟／同步模型，或前一個操作還在跑。` +
        `請切回 Revit 看一下畫面，處理完再重試。`
    );
  }

  const live = found.filter((i) => !i.busy);
  if (live.length === 1) {
    selectedPort = live[0].port;
    return live[0].port;
  }

  throw new Error(
    `偵測到 ${found.length} 個 Revit 實例，請先用 revit_use_instance 指定目標再重試：\n` +
      found.map((i) => `  - ${describeInstance(i)}`).join("\n")
  );
}

export async function resolveBaseUrl(): Promise<string> {
  return baseUrlFor(await resolvePort());
}
