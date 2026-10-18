// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    // ── 跨連結的房間查詢 ──────────────────────────────────
    public class LinkedRoomDto
    {
        [JsonProperty("id")]     public long   Id     { get; set; }
        [JsonProperty("source")] public string Source { get; set; } = "";
        [JsonProperty("number")] public string Number { get; set; } = "";
        [JsonProperty("name")]   public string Name   { get; set; } = "";
        [JsonProperty("level")]  public string Level  { get; set; } = "";
        [JsonProperty("areaM2")] public double AreaM2 { get; set; }
        /// <summary>房間中心點，主模型座標，mm。</summary>
        [JsonProperty("x")]      public double X      { get; set; }
        [JsonProperty("y")]      public double Y      { get; set; }
        [JsonProperty("z")]      public double Z      { get; set; }
    }

    public class UpdateDraftingViewRequest
    {
        [JsonProperty("viewId")] public long    ViewId { get; set; }
        [JsonProperty("scale")]  public int?    Scale  { get; set; }
        [JsonProperty("name")]   public string? Name   { get; set; }
    }

    public class DeleteElementsRequest
    {
        [JsonProperty("elementIds")] public List<long> ElementIds { get; set; } = new();
        [JsonProperty("apply")]      public bool       Apply      { get; set; } = false;
    }

    public class DeleteElementsResult
    {
        [JsonProperty("applied")] public bool         Applied { get; set; }
        [JsonProperty("matched")] public List<string> Matched { get; set; } = new();
        [JsonProperty("deleted")] public int          Deleted { get; set; }
        [JsonProperty("failed")]  public List<string> Failed  { get; set; } = new();
    }

    public class LinkedRoomsResult
    {
        [JsonProperty("total")]           public int                 Total           { get; set; }
        [JsonProperty("searchedSources")] public List<string>        SearchedSources { get; set; } = new();
        [JsonProperty("rooms")]           public List<LinkedRoomDto> Rooms           { get; set; } = new();
    }

    // ── 製圖視圖 ─────────────────────────────────────────
    public class NamedIdDto
    {
        [JsonProperty("id")]   public long   Id   { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
    }

    public class DraftingTypesResult
    {
        [JsonProperty("viewFamilyTypes")] public List<NamedIdDto> ViewFamilyTypes { get; set; } = new();
        [JsonProperty("textNoteTypes")]   public List<NamedIdDto> TextNoteTypes   { get; set; } = new();
        [JsonProperty("lineStyles")]      public List<NamedIdDto> LineStyles      { get; set; } = new();
    }

    public class CreateDraftingViewRequest
    {
        [JsonProperty("name")]              public string  Name              { get; set; } = "";
        [JsonProperty("scale")]             public int     Scale             { get; set; } = 100;
        [JsonProperty("viewFamilyTypeName")] public string? ViewFamilyTypeName { get; set; }
        [JsonProperty("apply")]             public bool    Apply             { get; set; } = false;
    }

    public class CreateDraftingViewResult
    {
        [JsonProperty("viewId")]         public long   ViewId         { get; set; }
        [JsonProperty("viewName")]       public string ViewName       { get; set; } = "";
        [JsonProperty("scale")]          public int    Scale          { get; set; }
        [JsonProperty("alreadyExisted")] public bool   AlreadyExisted { get; set; }
    }

    /// <summary>座標單位一律是「圖面上的 mm」，處理時乘視圖比例換成模型單位。</summary>
    public class BoxDto
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("w")] public double W { get; set; }
        [JsonProperty("h")] public double H { get; set; }
    }

    public class LineDto
    {
        [JsonProperty("x1")] public double X1 { get; set; }
        [JsonProperty("y1")] public double Y1 { get; set; }
        [JsonProperty("x2")] public double X2 { get; set; }
        [JsonProperty("y2")] public double Y2 { get; set; }
    }

    public class TextDto
    {
        [JsonProperty("rotationDeg")] public double RotationDeg { get; set; } = 0;
        [JsonProperty("x")]     public double X     { get; set; }
        [JsonProperty("y")]     public double Y     { get; set; }
        [JsonProperty("text")]  public string Text  { get; set; } = "";
        /// <summary>水平對齊：left / center / right</summary>
        [JsonProperty("align")] public string Align { get; set; } = "left";
        /// <summary>垂直對齊：top / middle / bottom</summary>
        [JsonProperty("vAlign")] public string VAlign { get; set; } = "top";
        /// <summary>
        /// 這一段文字要用的文字類型。字級在 Revit 是類型屬性，不能單獨改一段文字的大小，
        /// 所以要不同字級就指定不同類型（例如標題用 5mm、內文用 3mm）。
        /// 省略時用請求層級的 textTypeName。
        /// </summary>
        [JsonProperty("textTypeName")] public string? TextTypeName { get; set; }
        /// <summary>換行寬度（圖面 mm）。給了就固定寬度自動換行，沒給就單行不折。</summary>
        [JsonProperty("widthMm")] public double? WidthMm { get; set; }
        /// <summary>
        /// 引線終點（圖面 mm）。給了就替這段文字加一條引線指過去；
        /// 沒給就是純文字。轉折點沒指定時取文字與終點的中間高度。
        /// </summary>
        [JsonProperty("leaderX")]     public double? LeaderX     { get; set; }
        [JsonProperty("leaderY")]     public double? LeaderY     { get; set; }
        [JsonProperty("leaderElbowX")] public double? LeaderElbowX { get; set; }
        [JsonProperty("leaderElbowY")] public double? LeaderElbowY { get; set; }
        /// <summary>auto / left / right —— 引線從文字的哪一側出去。auto 依終點方向判斷。</summary>
        [JsonProperty("leaderSide")]  public string  LeaderSide  { get; set; } = "auto";
        /// <summary>直線或弧線引線。</summary>
        [JsonProperty("leaderArc")]   public bool    LeaderArc   { get; set; } = false;
    }

    public class DraftingContentRequest
    {
        [JsonProperty("viewId")]        public long            ViewId        { get; set; }
        [JsonProperty("boxes")]         public List<BoxDto>?   Boxes         { get; set; }
        [JsonProperty("lines")]         public List<LineDto>?  Lines         { get; set; }
        [JsonProperty("texts")]         public List<TextDto>?  Texts         { get; set; }
        [JsonProperty("textTypeName")]  public string?         TextTypeName  { get; set; }
        [JsonProperty("lineStyleName")] public string?         LineStyleName { get; set; }
        [JsonProperty("apply")]         public bool            Apply         { get; set; } = false;
    }

    public class DraftingContentResult
    {
        [JsonProperty("applied")]       public bool         Applied       { get; set; }
        [JsonProperty("viewId")]        public long         ViewId        { get; set; }
        [JsonProperty("viewName")]      public string       ViewName      { get; set; } = "";
        [JsonProperty("textTypeUsed")]  public string       TextTypeUsed  { get; set; } = "";
        [JsonProperty("plannedCurves")] public int          PlannedCurves { get; set; }
        [JsonProperty("plannedTexts")]  public int          PlannedTexts  { get; set; }
        [JsonProperty("curvesCreated")] public int          CurvesCreated { get; set; }
        [JsonProperty("textsCreated")]  public int          TextsCreated  { get; set; }
        [JsonProperty("leadersCreated")] public int         LeadersCreated { get; set; }
        [JsonProperty("skipped")]       public List<string> Skipped       { get; set; } = new();
    }
}
