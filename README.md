# Revit MCP

© 2026 Rita (Rirychh)

讓 Claude 透過 [Model Context Protocol](https://modelcontextprotocol.io) 讀取與操作 Autodesk Revit 模型。支援 Revit 2022–2027。

## 架構

```
Claude Desktop ──stdio──▶ server/ (Node.js MCP server) ──HTTP 127.0.0.1 + token──▶ addin/ (Revit 增益集)
```

| 資料夾 | 內容 |
|---|---|
| `addin/` | C# Revit 增益集：在 Revit 內啟動本機 HTTP 服務、執行 Revit API；另含 Claude 對話面板 |
| `addin/installer/` | HTA 安裝精靈與安裝腳本 |
| `addin/license-tool/` | 授權碼發行工具（ECDSA P-256 簽章，綁 Windows 帳號／電腦／到期日） |
| `server/` | TypeScript MCP server，提供 165 個 Revit 工具，見下方「功能」 |

## 功能

共 165 個 Revit 工具，另有 2 個同時開多個 Revit 時用來切換的工具（`revit_list_instances`、`revit_use_instance`）。會改模型的工具預設只預檢（`apply=false`），回報會做什麼，確認後再用 `apply=true` 在單一交易裡執行，任何錯誤整批回滾。長度單位一律 mm。

### 文件與專案

| 工具 | 說明 |
|---|---|
| `revit_instance_info` | 回報這個 Revit 實例的身分（埠、pid、版本、作用中文件與視圖、已開啟文件清單）。 |
| `revit_list_documents` | 列出所有已開啟的非連結文件（title、path、是否作用中、是否已修改、是否族檔、是否工作共用）。 |
| `revit_open_document` | 開啟 .rvt（本機或 UNC 網路路徑）並設為作用中文件；已開啟則直接啟用。不需先開啟專案。 |
| `revit_new_project` | 由樣板建立新專案，設定長度單位，可建中央檔並存檔開啟。不需先開啟專案。 |
| `revit_activate_document` | 把一份已開啟的文件設為作用中，讓其他 revit_* 工具作用在它上面。 |
| `revit_save_document` | 就地存檔一份已開啟的文件（等同 Ctrl+S），不必是作用中文件。 |
| `revit_close_document` | 關閉指定的已開啟文件（可選擇存檔或丟棄變更）。 |
| `revit_document_info` | 回報一份已開啟文件的資訊：路徑、是否有未存變更、是否工作共用、Revit 連結（含是否載入）、專案基準點共用座標（TWD97 m、對正北角度）、以及所有已開啟文件清單。 |
| `revit_sync_with_central` | 與中央模型同步（工作共用模型），可一併存本機檔與釋放工作集。 |
| `revit_sync_status` | 回報工作共用狀態：是否 workshared、中央檔路徑、使用者、是否有未存變更。 |
| `revit_clear_model` | 刪除文件裡的模型元件實例（牆、樓板、門、家具、房間、模型群組、基地、量體…），例如專案樣板內附的範例建築。視圖、圖紙、標高、軸網、註解與類型會保留。 |
| `revit_project_info` | 讀取或設定專案資訊（專案名稱、編號、業主、地址、狀態、發行日期等）。不給 set＝只讀。 |
| `revit_project_units` | 讀取或設定專案單位：長度（mm／cm／m）、面積、體積與小數位數、角度小數位數。 |
| `revit_add_project_parameter` | 新增專案參數並綁定到品類（實例或類型；文字、數字、整數、長度、面積、是／否）。apply=false 預檢。 |
| `revit_set_phases` | 列出階段與階段篩選；設定元件的建立／拆除階段、視圖的階段與階段篩選。apply=false 預檢。 |
| `revit_worksets` | 工作集：列出（擁有者、是否開啟／可編輯）、新建、把元件移到指定工作集（略過被別人借用的）。apply=false 預檢。 |

### 查詢與選取

| 工具 | 說明 |
|---|---|
| `revit_list_elements` | 列出／統計模型元件，可依品類、樓層、視圖、族名、XY 範圍過濾並分頁。 |
| `revit_get_element` | 取得單一元件的完整資料與所有參數。 |
| `revit_list_levels` | 列出所有樓層（標高，mm）。 |
| `revit_list_build_types` | 列出可用的牆類型或樓板類型（含厚度，mm）。 |
| `revit_list_rooms` | 列出房間（id、名稱、編號、面積 m²、樓層）。 |
| `revit_list_linked_rooms` | 跨主模型與所有 Revit 連結列出房間（含中心點座標，mm）。 |
| `revit_list_linked_elements` | 列出「被連結的」Revit 模型（結構 ST、機電 MEP…）裡的元件。主模型的元件請用 revit_list_elements。 |
| `revit_get_selection` | 讀取使用者目前在 Revit 中選取的元件（唯讀）。 |
| `revit_select_elements` | 在 Revit 視窗中依 id 選取元件（取代目前選取），可同時縮放到選取範圍。 |
| `revit_list_design_options` | 列出設計選項集與其選項：id、名稱、哪個是主要、哪個是作用中，以及各選項（與主模型）各有多少模型元素。 |
| `revit_select_category` | 把某品類的元件全部選起來（整個專案或某視圖內，可限定樓層），給「設計選項 → 新增至集合」等 UI 指令接手。 |

### 建模

| 工具 | 說明 |
|---|---|
| `revit_create_element` | 在指定座標放置一個族實例。 |
| `revit_create_levels` | 批次建立標高（樓層）。 |
| `revit_create_walls` | 沿一或多條折線在單一樓層建牆（含帷幕牆）。 |
| `revit_create_floors` | 批次在一或多個樓層建樓板（可含開口）。 |
| `revit_create_columns` | 以 DirectShape 建圓柱（歸在結構柱品類）。 |
| `revit_create_grids` | 建立網格線。apply=false 預檢。 |
| `revit_create_structural_framing` | 建立結構梁。apply=false 預檢。 |
| `revit_create_rooms` | 在點位建立房間。apply=false 預檢。 |
| `revit_create_room_separation` | 建立房間分隔線。apply=false 預檢。 |
| `revit_mass_boxes` | 在專案裡建概念量體（每個 box 一個 Metric Mass 族：擠出矩形或多邊形），可同時建樓層並加量體樓板以回報面積。 |
| `revit_place_doors_windows` | 把門窗放進牆：給點位會自動找最近的直牆並投影到牆中心線，可設窗台高、翻轉。apply=false 預檢。 |
| `revit_create_ceilings` | 建立天花板：依房間（以房間完成面邊界）或指定迴圈，可設離地高度。apply=false 預檢。 |
| `revit_create_roof` | 依跡線建立屋頂，可設坡度（每條邊都定義坡度，形成四坡）與基準偏移。apply=false 預檢。 |
| `revit_create_opening` | 建立開口：牆上矩形開口、跨樓層豎井、樓板／屋頂／天花上的開口。apply=false 預檢。 |
| `revit_create_stairs` | 依兩個樓層建立組合樓梯：直跑、L 型或 U 型（含平台），欄杆可保留、換類型或刪除。apply=false 預檢並算出級數、級高、級深。 |
| `revit_create_railings` | 建立欄杆：沿折線路徑（指定樓層），或掛在既有樓梯上。apply=false 預檢並列出欄杆類型。 |
| `revit_create_sweeps` | 建立掃掠：牆飾條／分割縫（可指定輪廓與高度）、樓板邊緣（沿樓板頂面外圍）、封簷板與排水溝（沿屋頂簷口）。apply=false 預檢。 |
| `revit_curtain_wall` | 帷幕牆：查格線／嵌板／豎框、加垂直或水平格線、在格線上加豎框、換嵌板類型。apply=false 預檢。 |
| `revit_attach_walls` | 牆頂或牆底附著到屋頂、樓板、天花（或取消附著）；Revit 2025 以上。apply=false 預檢。 |
| `revit_area_plans` | 面積平面：列出面積配置與面積、建立面積平面、畫面積邊界（迴圈或沿房間邊界）、放置面積。apply=false 預檢。 |

### 修改

| 工具 | 說明 |
|---|---|
| `revit_modify_element` | 修改既有元件的一個或多個參數。 |
| `revit_batch_set_parameters` | 批次設定多個元件的參數，全有或全無：任何一筆無法設定就整批不執行；類型參數預設保護。apply=false 預檢。 |
| `revit_rotate_elements` | 繞任意軸旋轉指定元件（可先預檢不套用）。 |
| `revit_mirror_elements` | 鏡射元件（預設保留原件）。apply=false 預檢。 |
| `revit_copy_elements` | 把既有天花設備佈局（燈具、撒水頭、風口、偵測器）複製到另一個樓層，可重新附著到天花。 |
| `revit_snap_elements_to_grid` | 把既有元件的平面位置吸附到天花模矩格線（只動 XY，不動高程）。 |
| `revit_align_elements_upright` | 把工作平面基礎的牆面族實例統一轉正（指定軸對齊世界上方）。 |
| `revit_ungroup_groups` | 解除指定的群組實例（只動被指名的 groupIds）。 |
| `revit_find_element_constraints` | 唯讀：找出哪些尺寸標註／對齊約束參考到指定元件（搬不動元件時用來查兇手）。 |
| `revit_delete_elements` | 依 ElementId 刪除作用中模型裡的元素（任何品類：模型構件、註解、製圖線等）。 |
| `revit_move_elements` | 依向量移動元件（mm），可一次多個。釘選或在群組內的會跳過並說明。apply=false 預檢。 |
| `revit_array_elements` | 照向量複製元件，count>1 為線性陣列（第 i 份位移 delta×i）。apply=false 預檢。 |
| `revit_change_type` | 把元件換成同族群或同品類的其他類型（例如牆換牆類型、門換門類型）。apply=false 預檢並列出可換的類型。 |
| `revit_duplicate_type` | 複製出新類型（例如新的牆類型），可設定總厚度（牆、樓板、屋頂、天花）與類型參數。apply=false 預檢。 |
| `revit_join_geometry` | 接合／取消接合／切換接合順序（例如牆與樓板、柱與梁）；也可給一批元件，自動接合彼此相交的組合。apply=false 預檢。 |
| `revit_set_type_layers` | 讀取或設定牆／樓板／屋頂／天花類型的構造層（功能、材料、厚度、核心層）。apply=false 預檢。 |
| `revit_groups` | 群組：列出群組類型與數量、把元件建成群組（可命名）、在指定位置放置群組。apply=false 預檢。 |

### 族群

| 工具 | 說明 |
|---|---|
| `revit_build_family` | 依 JSON 規格從樣板建立族檔（.rfa）：加參數、幾何、嵌套族，存檔並可載入專案。不需先開啟專案。 |
| `revit_load_families` | 從 .rfa 載入族群。apply=false 預檢。 |
| `revit_inspect_family` | 唯讀檢查族群：參考平面、視圖方向、幾何草圖平面。 |
| `revit_scan_family_labels` | 掃描 .rfa 檔／資料夾，或作用中專案裡的註解族，列出含標籤（label）的族及其族參數。不需先開啟專案。 |
| `revit_create_profile_family` | 依封閉點位建立輪廓族（.rfa，公制輪廓樣板）並載入專案，供牆飾條、樓板邊緣、欄杆等使用。apply=false 預檢。 |
| `revit_family_type_params` | 可載入族的類型參數：列出各類型參數值、修改類型參數值、設定公式（開族檔改公式後重新載入）。apply=false 預檢。 |
| `revit_purge_all` | 全面清除未使用項目（族群、類型、材質、樣式…，等同「清除未使用的項目」），可多輪；Revit 2024 以上。dryRun 預設 true。 |

### 視圖

| 工具 | 說明 |
|---|---|
| `revit_list_views` | 列出非樣板視圖（id、名稱、類型、樓層、比例、所在圖號）。 |
| `revit_create_view` | 建立新視圖（平面／剖面／3D）。 |
| `revit_duplicate_views` | 複製視圖（含詳圖／不含詳圖／從屬）。 |
| `revit_crop_view` | 重設視圖裁剪範圍，讓它停在界定房間的結構牆上（廁所詳圖之類的放大圖用）。 |
| `revit_rotate_view_crop` | 旋轉平面視圖的裁剪範圍（把斜的量體轉成水平垂直）。 |
| `revit_create_room_views` | 為房間批次建立平面放大圖與立面圖（含裁剪範圍與視圖樣板）。 |
| `revit_list_view_templates` | 列出所有視圖樣板（id、名稱、視圖類型、目前套用的視圖數）。 |
| `revit_apply_view_template` | 把視圖樣板套用到多個視圖。 |
| `revit_set_category_visibility` | 在視圖中隱藏或顯示品類（可見性／圖形取代）。連結模型設為「依主體視圖」時對連結內同品類也有效。 |
| `revit_color_by_parameter` | 依參數值在視圖中上色（元件覆寫）。apply=false 預檢。 |
| `revit_clear_view_overrides` | 清除視圖中元件的圖形覆寫。apply=false 預檢。 |
| `revit_update_view_properties` | 修改視圖的比例與名稱。 |
| `revit_screenshot_view` | 把指定視圖輸出成 PNG 到暫存檔，回傳影像資訊。 |
| `revit_labeled_snapshot` | 暫時在元件上標數字（可隔離）後輸出視圖圖片，再整批回滾；回傳圖檔與「數字→元件 id」對照，讓 AI 看圖定位。 |
| `revit_set_view_range` | 讀取或設定平面視圖的視圖範圍（頂部、剖切面、底部、視圖深度，相對所屬樓層的偏移 mm）。apply=false 先回報目前值。 |
| `revit_set_view_display` | 設定視圖的詳細等級、視覺樣式、專業、範圍框（可一次多張）。apply=false 先回報目前值。 |
| `revit_create_view_filter` | 建立視圖篩選器（品類＋參數條件）並套到視圖，可設可見性、顏色、半色調、線寬；同名篩選器會直接沿用。apply=false 預檢。 |
| `revit_create_view_template` | 從現有視圖建立視圖樣板（等同「從目前視圖建立樣板」）。apply=false 預檢。 |
| `revit_set_visibility` | 暫時隔離／隱藏元件或品類、重設暫時隱藏；或永久隱藏／取消隱藏元件。apply=false 預檢。 |
| `revit_set_section_box` | 設定 3D 視圖剖面框：框住指定元件（外擴 marginMm）或指定範圍，也可關閉。apply=false 預檢。 |
| `revit_create_elevations` | 在平面放一個立面標記，建立指定方向的立面視圖（north＝北向立面，看北側外牆）。apply=false 預檢。 |
| `revit_create_callout` | 在平面、剖面或立面上框一塊建立詳圖索引視圖（可命名、設比例）。apply=false 預檢。 |
| `revit_show_grids_3d` | 設定 3D 視圖的「顯示柱線」（Show Grids）：柱線畫在哪些樓層平面上。只有與樓層平面相交的柱線會顯示；此設定不屬於視圖樣板。 |

### 標籤與尺寸

| 工具 | 說明 |
|---|---|
| `revit_list_tag_types` | 列出專案裡所有註解類（Annotation）的標籤族類型。 |
| `revit_read_view_annotations` | 讀出某張視圖現有的標籤、尺寸標註與文字，用來照抄既有圖面的標註慣例。 |
| `revit_tag_elements` | 在視圖中放置標籤（例如廁所詳圖上的菱形設備編號標籤）。 |
| `revit_inspect_tags` | 列出視圖中所有標籤的位置、被標元件、引線與轉折點，並找出互相重疊、壓在元件上、跑出裁剪框的標籤。 |
| `revit_adjust_tags` | 逐一移動標籤頭（絕對位置或位移）、讓引線水平對齊被標元件、設定直角或直線引線轉折。apply=false 預檢。 |
| `revit_arrange_tags` | 把標籤頭整齊排到邊界外側（裁剪框、指定元件或矩形），依位置排序、保持間距，引線改成直角或直線。apply=false 預檢。 |
| `revit_delete_view_tags` | 刪掉視圖裡的標籤（跑錯標註時的退路）。 |
| `revit_create_dimension` | 在視圖中建立一條跨多個元件的尺寸鏈。 |
| `revit_create_wall_dimension_chains` | 在平面視圖自動建立牆的連續尺寸線（牆兩側面或單側面，可含軸線），水平、垂直或兩向。apply=false 預檢。 |
| `revit_inspect_dimensions` | 列出視圖中的線性尺寸：各段長度、過短段落、尺寸線位置，並找出平行且靠太近（< 300 mm）的尺寸線。 |
| `revit_adjust_dimensions` | 移動尺寸線（垂直於尺寸線平移，或 uv 位移）、刪除重複的尺寸。apply=false 預檢。 |
| `revit_tag_rooms` | 在平面視圖幫房間放房間標籤（預設：視圖所在樓層、還沒標的房間）。apply=false 預檢。 |
| `revit_add_text_notes` | 在任何 2D 視圖或圖紙上放文字註記（可指定文字類型與換行寬度）。apply=false 預檢。 |
| `revit_add_spot_elevations` | 在樓板、屋頂、天花等主體的頂面放高程點（可指定點位，否則取中心）。apply=false 預檢。 |
| `revit_add_filled_regions` | 在 2D 視圖畫填滿區域（例如材料圖例、範圍示意），可指定填滿區域類型。apply=false 預檢。 |
| `revit_place_detail_components` | 在 2D 視圖放詳圖元件（可旋轉）；族群需先載入。apply=false 預檢。 |

### 圖紙與修訂

| 工具 | 說明 |
|---|---|
| `revit_list_sheets` | 列出所有圖紙（id、圖號、圖名）、可用圖框類型，以及目前的開啟畫面 viewId。 |
| `revit_create_sheet` | 建立新圖紙，可同時放入視圖並設為開啟畫面。 |
| `revit_batch_create_sheets` | 批次建立圖紙（自動沿用標題欄類型，已存在的圖號跳過）。 |
| `revit_sheet_layout` | 列出圖紙的圖框尺寸與已放的視埠（視圖名、中心、寬高，皆為圖紙 mm）。 |
| `revit_place_views` | 把視圖放進「既有」圖紙（新增視埠）。一次一張圖紙，可放多個視圖。 |
| `revit_set_starting_view` | 設定模型開啟時的起始畫面。給 sheetNumber 或 viewId 其一。 |
| `revit_export_sheets_pdf` | 依圖號批次輸出 PDF，每張一檔，檔名「圖號{separator}圖名.pdf」。 |
| `revit_list_revisions` | 列出專案所有修訂（id、順序、描述、日期、雲形線數量）。 |
| `revit_write_revision_notes` | 把修正說明寫進各圖紙圖框的「修訂內容」欄（新增 TextNote，自動在、，處換行）。 |
| `revit_annotate_revision_clouds` | 在指定修訂的每個雲形線右上角加文字＋直線引線，文字依雲形線所在圖紙的圖號從 notes 取。 |
| `revit_add_revision_clouds` | 在圖紙或 2D 視圖畫矩形修訂雲形線，掛到既有修訂或新建一筆修訂。apply=false 預檢。 |

### 製圖視圖與線

| 工具 | 說明 |
|---|---|
| `revit_list_drafting_types` | 列出可用的製圖視圖族類型、文字類型、線型式。 |
| `revit_create_drafting_view` | 建立製圖視圖（索引表、位置配置圖用）；同名已存在則回傳既有的。 |
| `revit_add_drafting_content` | 在製圖視圖裡畫框線、線段與文字（含引線）。座標單位是「圖面 mm」。 |
| `revit_clear_drafting_view` | 清掉視圖裡所有細部線與文字，方便整張重畫。 |
| `revit_create_detail_lines` | 在視圖畫詳圖線。apply=false 預檢。 |
| `revit_draw_cad_lines` | 把外部轉出的 CAD 折線座標畫成 Revit 模型線，每條線加註解 CAD:{tag}／{style}。 |
| `revit_close_lines` | 調整一組直線的端點，吸附成封閉且不自相交的迴圈（出圖用）。 |

### 明細表

| 工具 | 說明 |
|---|---|
| `revit_create_schedule` | 建立明細表並加入欄位。apply=false 預檢並列出可用欄位。 |
| `revit_list_schedules` | 列出模型中的明細表（id、名稱、品類、欄位數）。 |
| `revit_get_schedule_data` | 讀取明細表內容（表頭與本體，可分頁），照明細表畫面原樣呈現。 |
| `revit_export_schedule` | 把一個或多個明細表視圖匯出為 CSV。 |
| `revit_schedule_settings` | 明細表排序／群組、篩選、逐項列出、總計、隱藏欄位；不給設定＝讀目前設定。apply=false 預檢。 |

### CAD 與連結

| 工具 | 說明 |
|---|---|
| `revit_link_cad` | 連結 CAD（DWG）檔到指定樓層的平面視圖。 |
| `revit_cad_extract` | 萃取視圖中 DWG 的線段、封閉迴圈（含首尾相接的線段串成的迴圈）、圓，存成工作階段快取，回傳各圖層統計與 cacheId。 |
| `revit_cad_generate_bim` | 依 revit_cad_extract 的快取產生柱（矩形／圓形迴圈，含旋轉角）或樓板（封閉迴圈）。apply=false 預檢列出尺寸分組與可用類型。 |
| `revit_link_revit` | 把 .rvt 連結進文件（預設原點對原點，shared＝依共用座標），可同時取得其共用座標。 |
| `revit_move_link` | 移動單一 Revit 連結實例，並可取得共用座標／設定專案基準高程。 |
| `revit_delete_in_link` | 刪除「被連結的 Revit 檔」裡的元件，並把結果存回那個檔案。 |
| `revit_cad_wall_diff` | 視圖中 DWG 牆線與 Revit 可見牆比對：列出 CAD 有 Revit 沒有（newWalls）、Revit 有 CAD 沒有（obsoleteWalls／partialWalls）；apply=true 依 newWalls 建牆。 |

### 基地與材質

| 工具 | 說明 |
|---|---|
| `revit_create_property_line` | 以「地界線」線型式的封閉模型線迴圈畫出基地地界線，並組成模型群組（地界線的替代做法）。 |
| `revit_simplify_site` | 基地模精簡：DirectShape 共平面三角面合併＋Toposolid 格網減點。 |
| `revit_site_directshapes` | 查詢（或刪除）符合條件的 DirectShape 基地模元素。 |
| `revit_set_site_materials` | 依註解關鍵字規則批次指定基地模 DirectShape／地形的材質與顏色。 |
| `revit_set_material_appearance` | 為材質建立／更新「一般」彩現外觀（漫射色、透明度、光澤），Enscape 可見。 |
| `revit_materials` | 材料：列出、建立（可複製既有材料、設顏色與透明度）、指定到元件或類型的材料參數。apply=false 預檢。 |

### MEP

| 工具 | 說明 |
|---|---|
| `revit_create_ducts` | 建立風管（兩點一段，可多段）。apply=false 預檢。 |
| `revit_create_pipes` | 建立水管（兩點一段，可多段）。apply=false 預檢。 |
| `revit_create_mep_system` | 把設備／末端的空接頭組成風管或管路系統。apply=false 預檢。 |
| `revit_inspect_mep_connectors` | 列出 MEP 元件（設備、末端、管線）的接頭：位置、方向、尺寸、領域、是否已接、接到哪些元件、所屬系統。 |
| `revit_route_mep_connectors` | 在兩個空接頭之間自動接水管或風管：直線或直角繞行（先沿接頭方向伸出 clearanceMm），轉角自動放彎頭。apply=false 預檢。 |
| `revit_audit_mep` | 全模型 MEP 稽核：沒接上的管端（依品類統計）、設備接頭沒有系統。 |

### 分析與模型 QC

| 工具 | 說明 |
|---|---|
| `revit_model_health` | 作用中模型的健檢總覽（唯讀）：檔案大小、元素數、警告總數與前 15 類、可載入／內建族、匯入與連結 CAD、Revit 連結載入狀態、群組與未使用群組類型、未上圖紙的視圖與明細表、未使用視圖樣板、未放置／未封閉房間、設計選項、工作集、材料與填充樣式數、可清除元素數。 |
| `revit_performance_report` | 找出拖慢模型的主因（唯讀）：各品類實例數、各族的實例數×幾何量排序、單一類型最複雜的族、每個 CAD 的曲線／圖塊量、2D 雜物（詳圖線、模型線、填充區域、文字、尺寸、DirectShape、修訂雲形）、Fine 細緻度視圖數。 |
| `revit_model_statistics` | 模型統計（唯讀）：各品類實例數、族群、類型、圖紙、樓層、房間、警告、視圖數。 |
| `revit_list_warnings` | 列出作用中模型的警告（唯讀）：依描述分組的統計（含 autoFix 標記），以及逐筆明細（相關元件 id、品類、族、類型、樓層、是否在群組內）。 |
| `revit_resolve_warnings` | 自動修正可安全處理的警告類型（依 Revit 內建警告 id 判斷，不受介面語言影響）。 |
| `revit_purge_scan` | 列出沒有被放置的族（預設只看註解類品類）。 |
| `revit_purge_delete` | 依名單刪除未被使用的族（仍在使用中的會自動跳過）。dryRun 預設 true 只回傳預覽清單。 |
| `revit_check_clashes` | 碰撞檢查（唯讀）：兩組品類之間的實體幾何硬碰撞，回報兩側元件與位置（mm）。 |
| `revit_material_quantities` | 材料數量（唯讀）：依材料彙總材料體積（m³）與面積（m²）。 |
| `revit_export_room_data` | 房間資料（唯讀）：編號、名稱、樓層、面積、周長、體積、可選邊界座標。 |

### 匯出

| 工具 | 說明 |
|---|---|
| `revit_export_ifc` | 把作用中模型匯出為 IFC 2x3 CV2 檔。 |
| `revit_export_dwg` | 把視圖或圖紙匯出成 DWG，可用專案裡的匯出設定。apply=false 預檢並列出可用設定。 |
| `revit_export_nwc` | 匯出 Navisworks NWC（整個模型或指定 3D 視圖）；需要電腦已安裝 NWC 匯出器。apply=false 預檢。 |

## 支援的 AI

**Revit 內的對話面板**：上方「AI」選單切換 Claude／GPT／Gemini，各家 API 金鑰分開存在 Windows 認證管理員。「模型」欄可改模型 ID（預設 `claude-sonnet-5`、`gpt-5`、`gemini-2.5-pro`）。三家共用同一份 `tools-manifest.json`；Gemini 不支援的 JSON Schema 關鍵字會自動轉換。

**外部 MCP 客戶端**（透過 `server/`）：

| 客戶端 | 設定檔 |
|---|---|
| Claude Desktop | 安裝精靈自動寫入 `claude_desktop_config.json` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` 的 `mcpServers` |
| Codex CLI（GPT） | `%USERPROFILE%\.codex\config.toml` 的 `[mcp_servers.revit]` |

Gemini CLI：

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Rirychh\\RevitMCP\\node\\node.exe",
      "args": ["C:\\Users\\<you>\\AppData\\Local\\Rirychh\\RevitMCP\\server\\dist\\index.js"],
      "timeout": 600000
    }
  }
}
```

Codex CLI：

```toml
[mcp_servers.revit]
command = 'C:\Users\<you>\AppData\Local\Rirychh\RevitMCP\node\node.exe'
args = ['C:\Users\<you>\AppData\Local\Rirychh\RevitMCP\server\dist\index.js']
tool_timeout_sec = 600
```

ChatGPT 網頁版／桌面版只能連遠端 MCP server，不能連這個本機 stdio server。

## 編譯

增益集（需 .NET 8 SDK；Revit 2027 需 .NET 10 SDK）：

```powershell
cd addin
.\build-all.ps1                 # 全部版本
dotnet build -c Release -p:RevitVersion=2024   # 單一版本
```

MCP server（需 Node.js 18+）：

```powershell
cd server
npm install
npm run build
```

## 授權機制

發佈版不含發行者公鑰。要使用授權機制：

1. 執行 `addin/license-tool/授權管理.bat`，選 init 建立金鑰（私鑰以 DPAPI 存在 `%APPDATA%\Rirychh\RevitMCP-Issuer`，不會進 repo）。
2. 把產生的 `public-key.txt` 內 X／Y 值填入 `addin/Licensing/LicenseManager.cs` 的 `PubX`、`PubY`，重新編譯。
3. 使用者安裝後取得申請碼，以同一工具發出授權碼。

## 安全

- HTTP 服務只綁 `127.0.0.1`，每次啟動產生 shared-secret token，MCP server 帶 token 才能呼叫。
- Anthropic API 金鑰（對話面板用）以 DPAPI 加密存在使用者本機。

## 授權條款

[MIT](LICENSE) © 2026 Rita (Rirychh)