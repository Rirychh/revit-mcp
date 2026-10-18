// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

// ── 通用 API 回應 ─────────────────────────────────────────────────────────────
export interface ApiResponse<T> {
  success: boolean;
  data?:   T;
  error?:  string;
}

// ── 元件 ─────────────────────────────────────────────────────────────────────
export interface ElementDto {
  id:         number;
  name:       string;
  category:   string;
  familyName: string;
  typeName:   string;
  levelName:  string;
  /** 放置點，mm，專案座標 */
  location?:  { x: number; y: number; z: number };
  /** 主體元件 Id（面掛族群的天花／牆） */
  hostId?:    number;
  /** 繞垂直軸的旋轉角，度 */
  rotation?:  number;
  parameters: Record<string, string>;
}

export interface BBox { minX: number; minY: number; maxX: number; maxY: number }

export interface CopyGroupDto {
  familyName: string;
  typeName:   string;
  category:   string;
  count:      number;
  faceHosted: number;
}

export interface CopyElementsResult {
  dryRun:         boolean;
  sourceCount:    number;
  createdCount:   number;
  skippedCount:   number;
  sourceLevel:    string;
  targetLevel:    string;
  sourceBBox?:    BBox;
  targetCeilings: number;
  groups:         CopyGroupDto[];
  createdIds:     number[];
  warnings:       string[];
}

export interface ElementListResult {
  total:    number;
  count:    number;
  offset:   number;
  hasMore:  boolean;
  elements: ElementDto[];
  /** summary=true 時回族群／類型統計 */
  groups?:  CopyGroupDto[];
}

// ── 視圖 ─────────────────────────────────────────────────────────────────────
export interface ViewDto {
  id:        number;
  name:      string;
  viewType:  string;
  levelName: string;
  scale:     number;
}

export interface ScreenshotResult {
  viewName: string;
  filePath: string;
  width:    number;
  height:   number;
}

// ── 樓層 / 房間 ───────────────────────────────────────────────────────────────
export interface LevelDto {
  id:        number;
  name:      string;
  elevation: number;
}

export interface RoomDto {
  id:        number;
  name:      string;
  number:    string;
  area:      number;
  levelName: string;
}

// ── 匯出 ─────────────────────────────────────────────────────────────────────
export interface ExportResult {
  filePath:  string;
  format:    string;
  sizeBytes: number;
}

// ── 標註 ─────────────────────────────────────────────────────────────────────
export interface TagTypeDto {
  id:         number;
  familyName: string;
  typeName:   string;
  category:   string;
}

export interface TagPlacementDto {
  elementId:   number;
  elementName: string;
  category:    string;
  source:      string;
  tagId:       number;
  tagText:     string;
  x: number; y: number; z: number;
}

export interface SkippedDto {
  elementId: number;
  reason:    string;
}

export interface ViewTagDto {
  tagId:       number;
  category:    string;
  familyName:  string;
  typeName:    string;
  text:        string;
  hasLeader:   boolean;
  taggedIds:   number[];
  taggedNames: string[];
}

export interface ViewDimDto {
  dimensionId: number;
  category:    string;
  typeName:    string;
  segmentsMm:  number[];
}

export interface ViewTextDto {
  textNoteId: number;
  typeName:   string;
  text:       string;
}

export interface ViewAnnotationsResult {
  viewId:     number;
  viewName:   string;
  tags:       ViewTagDto[];
  dimensions: ViewDimDto[];
  textNotes:  ViewTextDto[];
}

export interface TagElementsResult {
  applied:     boolean;
  viewId:      number;
  viewName:    string;
  tagTypeUsed: string;
  scope?:      string;
  candidates:  number;
  placed:      TagPlacementDto[];
  skipped:     SkippedDto[];
}

export interface DimensionRefDto {
  elementId:  number;
  name:       string;
  category:   string;
  refCount:   number;
  positionMm: number;
}

export interface DimensionResult {
  applied:       boolean;
  viewId:        number;
  viewName:      string;
  direction:     string;
  dimensionId:   number;
  segmentCount:  number;
  segmentsMm:    number[];
  references:    DimensionRefDto[];
  skipped:       SkippedDto[];
  dimensionType: string;
}

export interface CropCandidateDto {
  elementId:   number;
  source:      string;
  typeName:    string;
  thicknessMm: number;
  matchedBy:   string;
  minMm:        number;
  maxMm:        number;
  runAlignment: number;
}

export interface CropRectDto {
  leftMm:   number;
  rightMm:  number;
  bottomMm: number;
  topMm:    number;
  widthMm:  number;
  heightMm: number;
}

export interface CropViewResult {
  applied:       boolean;
  viewId:        number;
  viewName:      string;
  mode:          string;
  axis:          string;
  rule:          string;
  searchedLinks: string[];
  seed:          string;
  leftWall?:     CropCandidateDto;
  rightWall?:    CropCandidateDto;
  bottomRef?:    CropCandidateDto;
  topRef?:       CropCandidateDto;
  matched:       CropCandidateDto[];
  nearMiss:      CropCandidateDto[];
  before?:       CropRectDto;
  after?:        CropRectDto;
  skipped:       SkippedDto[];
}
