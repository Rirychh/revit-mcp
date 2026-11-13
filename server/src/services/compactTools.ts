// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

/**
 * 讓 AI 選工具更準、tools/list 更短。
 *
 * 1. 描述裡的「Args:」段落和 inputSchema 重複：把每一行搬進對應參數的說明
 *    （參數原本沒有說明，或原本的說明明顯較短時才換），再從描述裡拿掉。資訊不少，只是放對地方。
 * 2. 功能相近、容易選錯的工具，在描述最後加一行「相近：…」指向另一支。
 *
 * 只改送給 AI 的 tools/list；Revit 端的路由與 tools-manifest.json 不受影響。
 * 必須在 installTargetGuard 之後安裝（這層在外面，先整理描述，再交給 targetGuard 加 document）。
 */

/** 容易混淆的工具：一行說明何時改用另一支。 */
export const RELATED: Record<string, string> = {
  revit_list_elements: "要統計或健檢用 revit_model_statistics／revit_model_health；連結模型裡的元件用 revit_list_linked_elements；單一元件全部參數用 revit_get_element",
  revit_list_linked_elements: "主模型的元件用 revit_list_elements",
  revit_get_element: "列很多元件用 revit_list_elements",
  revit_modify_element: "多個元件一起改用 revit_batch_set_parameters（全有或全無）；族的類型參數與公式用 revit_family_type_params",
  revit_batch_set_parameters: "族的類型參數與公式用 revit_family_type_params",
  revit_create_element: "門窗要放進牆用 revit_place_doors_windows；樓梯用 revit_create_stairs",
  revit_create_columns: "這支建的是 DirectShape 圓柱；要放柱族群用 revit_create_element",
  revit_copy_elements: "一般複製或線性陣列用 revit_array_elements；移動用 revit_move_elements",
  revit_move_elements: "已釘選的元件先用 revit_pin_elements 解除釘選；移動 Revit 連結用 revit_move_link；吸附到天花格線用 revit_snap_elements_to_grid；放進設計選項用 revit_move_to_design_option",
  revit_pin_elements: "解除釘選後要移動用 revit_move_elements、刪除用 revit_delete_elements；做完可再 pinned=true 釘回",
  revit_move_to_design_option: "先用 revit_list_design_options 確認作用中選項",
  revit_list_design_options: "把元件移進作用中選項用 revit_move_to_design_option",
  revit_array_elements: "把天花設備整層複製到另一樓層用 revit_copy_elements",
  revit_delete_elements: "刪標籤用 revit_delete_view_tags；清除未使用的族與類型用 revit_purge_all",
  revit_create_view: "立面用 revit_create_elevations；詳圖索引用 revit_create_callout；複製既有視圖用 revit_duplicate_views；房間放大圖用 revit_create_room_views",
  revit_set_category_visibility: "暫時隔離／隱藏或隱藏個別元件用 revit_set_visibility；依條件上色或隱藏用 revit_create_view_filter",
  revit_set_visibility: "永久隱藏整個品類用 revit_set_category_visibility",
  revit_create_view_filter: "依參數值直接上色（不建篩選器）用 revit_color_by_parameter",
  revit_color_by_parameter: "要能沿用到其他視圖的規則用 revit_create_view_filter",
  revit_apply_view_template: "從現有視圖建立新樣板用 revit_create_view_template",
  revit_create_view_template: "套到其他視圖用 revit_apply_view_template",
  revit_screenshot_view: "要在圖上對照元件 id 用 revit_labeled_snapshot",
  revit_labeled_snapshot: "單純輸出視圖圖片用 revit_screenshot_view",
  revit_tag_elements: "房間標籤用 revit_tag_rooms；標籤排版用 revit_arrange_tags／revit_adjust_tags",
  revit_tag_rooms: "設備、材料等其他標籤用 revit_tag_elements",
  revit_create_dimension: "整片牆的連續尺寸線用 revit_create_wall_dimension_chains",
  revit_create_wall_dimension_chains: "指定元件之間的單一尺寸鏈用 revit_create_dimension",
  revit_add_text_notes: "製圖視圖裡畫框線＋文字用 revit_add_drafting_content；圖框修訂欄用 revit_write_revision_notes",
  revit_add_drafting_content: "一般視圖或圖紙上的文字用 revit_add_text_notes",
  revit_create_detail_lines: "製圖視圖的框線＋文字用 revit_add_drafting_content",
  revit_create_sheet: "一次建很多張用 revit_batch_create_sheets",
  revit_batch_create_sheets: "建一張並放入視圖用 revit_create_sheet",
  revit_add_revision_clouds: "替既有雲形線加註記用 revit_annotate_revision_clouds",
  revit_annotate_revision_clouds: "新畫雲形線用 revit_add_revision_clouds",
  revit_purge_scan: "全面清除（族群、類型、材質等）用 revit_purge_all",
  revit_purge_delete: "全面清除（族群、類型、材質等）用 revit_purge_all",
  revit_purge_all: "只看未放置的註解族用 revit_purge_scan",
  revit_list_rooms: "面積、周長、邊界座標用 revit_export_room_data；連結模型的房間用 revit_list_linked_rooms",
  revit_create_rooms: "法規面積用面積平面 revit_area_plans",
  revit_area_plans: "房間用 revit_create_rooms",
  revit_materials: "彩現外觀用 revit_set_material_appearance；牆、樓板等的材料在構造層 revit_set_type_layers；基地模用 revit_set_site_materials",
  revit_set_material_appearance: "建立材料或指定給元件用 revit_materials",
  revit_set_site_materials: "一般材料用 revit_materials",
  revit_set_type_layers: "只改總厚度用 revit_duplicate_type",
  revit_duplicate_type: "逐層設定材料與厚度用 revit_set_type_layers",
  revit_change_type: "建立新類型用 revit_duplicate_type",
  revit_ungroup_groups: "建立或放置群組用 revit_groups",
  revit_groups: "解除群組用 revit_ungroup_groups",
  revit_link_cad: "把 DWG 線條轉成 Revit 元件用 revit_cad_extract → revit_cad_generate_bim；牆用 revit_cad_wall_diff",
  revit_cad_extract: "牆用 revit_cad_wall_diff",
  revit_draw_cad_lines: "DWG 已連結在視圖裡時用 revit_cad_extract",
  revit_create_walls: "帷幕牆格線與豎框用 revit_curtain_wall；牆頂附著屋頂用 revit_attach_walls",
  revit_create_railings: "建樓梯時順便處理欄杆用 revit_create_stairs",
  revit_create_sweeps: "先建輪廓族用 revit_create_profile_family",
  revit_create_profile_family: "其他族用 revit_build_family",
  revit_build_family: "輪廓族用 revit_create_profile_family",
  revit_model_health: "只要數量統計（較快）用 revit_model_statistics；找拖慢原因用 revit_performance_report",
  revit_model_statistics: "完整健檢用 revit_model_health",
  revit_list_schedules: "讀內容用 revit_get_schedule_data；排序篩選用 revit_schedule_settings",
  revit_create_schedule: "排序、篩選、總計用 revit_schedule_settings",
  revit_schedule_settings: "建立明細表用 revit_create_schedule",
  revit_export_schedule: "直接讀內容（不寫檔）用 revit_get_schedule_data",
};

const ARGS_HEAD = /^\s*Args:\s*$/;
const ARG_LINE  = /^\s*-\s*([A-Za-z0-9_ /]+?)\s*(\([^)]*\))?\s*:\s*(.*)$/;

/** 拆出描述裡的 Args 段落：回傳剩下的描述與 {參數: 說明}。 */
export function splitArgs(desc: string): { rest: string; args: Record<string, string> } {
  const lines = desc.split("\n");
  const out: string[] = [];
  const args: Record<string, string> = {};
  for (let i = 0; i < lines.length; i++) {
    if (!ARGS_HEAD.test(lines[i])) { out.push(lines[i]); continue; }
    i++;
    let cur: string[] | null = null;
    for (; i < lines.length; i++) {
      const ln = lines[i];
      const indented = /^[ \t]/.test(ln);
      const blankBeforeIndented = ln.trim() === "" && i + 1 < lines.length && /^[ \t]/.test(lines[i + 1]);
      if (!indented && !blankBeforeIndented) { i--; break; }
      const m = ARG_LINE.exec(ln);
      if (m) {
        cur = m[1].split("/").map((s) => s.trim()).filter(Boolean);
        for (const k of cur) args[k] = m[3].trim();
      } else if (cur && ln.trim()) {
        for (const k of cur) args[k] += " " + ln.trim();
      }
    }
  }
  return { rest: out.join("\n").replace(/\n{3,}/g, "\n\n").trim(), args };
}

export function installCompactTools(server: McpServer): void {
  const inner = server.registerTool.bind(server) as (...a: any[]) => any;
  (server as any).registerTool = (name: string, config: any, cb: any) => {
    let description: string = config?.description ?? "";
    let inputSchema = config?.inputSchema;

    const { rest, args } = splitArgs(description);
    if (Object.keys(args).length > 0) {
      description = rest;
      const shape = inputSchema?.shape ?? (inputSchema && typeof inputSchema === "object" && !inputSchema._def ? inputSchema : null);
      if (shape) {
        const patch: Record<string, any> = {};
        for (const [k, text] of Object.entries(args)) {
          const field = shape[k];
          if (!field || typeof field.describe !== "function") continue;
          const now: string = field.description ?? "";
          if (text.length > now.length + 20) patch[k] = field.describe(text);
        }
        if (Object.keys(patch).length > 0)
          inputSchema = typeof inputSchema.extend === "function" ? inputSchema.extend(patch) : { ...inputSchema, ...patch };
      }
    }

    const rel = RELATED[name];
    if (rel) description = `${description}\n相近：${rel}。`;

    return inner(name, { ...config, description, inputSchema }, cb);
  };
}
