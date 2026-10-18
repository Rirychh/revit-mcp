// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import axios, { AxiosError } from "axios";
import { REQUEST_TIMEOUT_MS, PORT_RANGE_START, PORT_RANGE_COUNT } from "../constants.js";
import { resolvePort, baseUrlFor, clearSelectedPort } from "./instances.js";
import { authHeaders, invalidateToken, tokenFilePath } from "./token.js";
import type { ApiResponse } from "../types.js";

/**
 * 向目前選定的 Revit add-in HTTP server 發請求並解析 ApiResponse。
 * baseURL 在每次呼叫時才解析，所以中途切換實例會立刻生效。
 *
 * 認證：add-in 要求每個請求都帶 X-Revit-Mcp-Token（token 檔隨埠號，見 token.ts）。
 * add-in 重啟會換 token，所以收到 401 時重讀一次 token 檔並重試一次。
 */
export async function revitRequest<T>(
  method: "get" | "post" | "put",
  path:   string,
  params?: Record<string, unknown>,
  body?:   unknown,
  timeoutMs: number = REQUEST_TIMEOUT_MS
): Promise<T> {
  const port    = await resolvePort();
  const baseURL = baseUrlFor(port);

  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const resp = await axios.request<ApiResponse<T>>({
        baseURL,
        url:     path,
        method,
        timeout: timeoutMs,
        // charset 一定要帶。HttpListener 沒看到 charset 就用系統預設編碼讀 body，
        // 中文 Windows 是 Big5，中文參數會變亂碼。
        // X-Revit-Mcp-Token 是 add-in 的 shared secret；沒帶會被回 401。
        headers: {
          "Content-Type": "application/json; charset=utf-8",
          ...(await authHeaders(port)),
        },
        params:  method === "get" ? params : undefined,
        data:    method !== "get" ? body   : undefined,
      });

      if (!resp.data.success) {
        throw new Error(resp.data.error ?? "Revit returned an error.");
      }
      return resp.data.data as T;
    } catch (err) {
      // 401 = token 不符。最常見原因是 Revit 重啟後換了 token、我們還拿著舊的快取。
      // 重讀 token 檔再試一次；第二次還是 401 才算真的失敗。
      if (attempt === 0 && err instanceof AxiosError && err.response?.status === 401) {
        invalidateToken(port);
        continue;
      }
      // 連不上通常代表那個 Revit 已被關閉 —— 清掉選擇與 token，下次呼叫重新探索。
      if (err instanceof AxiosError && err.code === "ECONNREFUSED") {
        clearSelectedPort();
        invalidateToken(port);
      }
      throw new Error(handleError(err, baseURL, port));
    }
  }

  // for 迴圈兩次都 continue 不可能發生（第二次 401 會走 throw），純粹讓型別收斂。
  throw new Error("revitRequest: retry loop exhausted.");
}

function handleError(err: unknown, baseURL: string, port: number): string {
  if (err instanceof AxiosError) {
    if (err.code === "ECONNREFUSED")
      return (
        `連不上 ${baseURL}。該 Revit 可能已關閉；` +
        `請用 revit_list_instances 重新確認目前開著哪些 Revit ` +
        `（掃描範圍 ${PORT_RANGE_START}-${PORT_RANGE_START + PORT_RANGE_COUNT - 1}）。`
      );
    if (err.code === "ECONNABORTED")
      return "Request timed out. The Revit operation is taking too long.";
    if (err.response?.status === 401)
      return (
        `認證失敗（401）：${baseURL} 上的 add-in 不接受我們的 token。` +
        `token 檔是 ${tokenFilePath(port)}，由 add-in 啟動時寫入、關閉時刪除。` +
        `請重新啟動該 Revit 讓 add-in 重新產生 token 後重試。`
      );
    if (err.response?.status === 403)
      return (
        `被拒絕（403）：${baseURL} 上的 add-in 判定這個請求不是來自本機 MCP client。` +
        `（add-in 會擋掉帶 Origin header 或非 loopback 來源的請求。）`
      );
    if (err.response)
      return `Revit API error ${err.response.status}: ${JSON.stringify(err.response.data)}`;
  }
  return err instanceof Error ? err.message : String(err);
}
