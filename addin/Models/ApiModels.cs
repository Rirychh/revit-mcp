// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    // ── 通用回應 ──────────────────────────────────────────
    public class ApiResponse<T>
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("data")]    public T?   Data    { get; set; }
        [JsonProperty("error")]   public string? Error { get; set; }

        public static ApiResponse<T> Ok(T data)    => new() { Success = true,  Data  = data };
        public static ApiResponse<T> Fail(string e)=> new() { Success = false, Error = e   };
    }

    // ── 元件 ─────────────────────────────────────────────
    public class ElementDto
    {
        [JsonProperty("id")]         public long   Id         { get; set; }
        [JsonProperty("name")]       public string Name       { get; set; } = "";
        [JsonProperty("category")]   public string Category   { get; set; } = "";
        [JsonProperty("familyName")] public string FamilyName { get; set; } = "";
        [JsonProperty("typeName")]   public string TypeName   { get; set; } = "";
        [JsonProperty("levelName")]  public string LevelName  { get; set; } = "";
        /// <summary>放置點（mm，專案座標）。無 LocationPoint 的元件回 null。</summary>
        [JsonProperty("location", NullValueHandling = NullValueHandling.Ignore)]
        public PointDto? Location { get; set; }
        /// <summary>主體元件 Id（面掛族群的天花／牆）。無主體回 null。</summary>
        [JsonProperty("hostId", NullValueHandling = NullValueHandling.Ignore)]
        public long? HostId { get; set; }
        /// <summary>繞垂直軸的旋轉角（度）。</summary>
        [JsonProperty("rotation", NullValueHandling = NullValueHandling.Ignore)]
        public double? Rotation { get; set; }
        [JsonProperty("bboxMinZ", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMinZ { get; set; }
        [JsonProperty("bboxMaxZ", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMaxZ { get; set; }
        [JsonProperty("bboxMinX", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMinX { get; set; }
        [JsonProperty("bboxMaxX", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMaxX { get; set; }
        [JsonProperty("bboxMinY", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMinY { get; set; }
        [JsonProperty("bboxMaxY", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMaxY { get; set; }
        [JsonProperty("levelElevMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? LevelElevMm { get; set; }
        [JsonProperty("groupId", NullValueHandling = NullValueHandling.Ignore)]
        public long? GroupId { get; set; }
        [JsonProperty("groupName", NullValueHandling = NullValueHandling.Ignore)]
        public string? GroupName { get; set; }
        [JsonProperty("parameters")] public Dictionary<string, string> Parameters { get; set; } = new();
    }

    public class PointDto
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("z")] public double Z { get; set; }
    }

    public class ElementListResult
    {
        [JsonProperty("total")]    public int           Total    { get; set; }
        [JsonProperty("count")]    public int           Count    { get; set; }
        [JsonProperty("offset")]   public int           Offset   { get; set; }
        [JsonProperty("hasMore")]  public bool          HasMore  { get; set; }
        [JsonProperty("elements")] public List<ElementDto> Elements { get; set; } = new();
        /// <summary>summary=true 時改回族群／類型統計，不回個別元件。</summary>
        [JsonProperty("groups", NullValueHandling = NullValueHandling.Ignore)]
        public List<CopyGroupDto>? Groups { get; set; }
    }

    public class CreateElementRequest
    {
        [JsonProperty("familyName")]  public string  FamilyName  { get; set; } = "";
        [JsonProperty("typeName")]    public string  TypeName    { get; set; } = "";
        [JsonProperty("levelName")]   public string? LevelName   { get; set; }
        [JsonProperty("x")]           public double  X           { get; set; }
        [JsonProperty("y")]           public double  Y           { get; set; }
        [JsonProperty("z")]           public double  Z           { get; set; }
        [JsonProperty("parameters")]  public Dictionary<string, string>? Parameters { get; set; }
    }

    public class ModifyElementRequest
    {
        [JsonProperty("bboxMinZ", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMinZ { get; set; }
        [JsonProperty("bboxMaxZ", NullValueHandling = NullValueHandling.Ignore)]
        public double? BboxMaxZ { get; set; }
        [JsonProperty("levelElevMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? LevelElevMm { get; set; }
        [JsonProperty("groupId", NullValueHandling = NullValueHandling.Ignore)]
        public long? GroupId { get; set; }
        [JsonProperty("groupName", NullValueHandling = NullValueHandling.Ignore)]
        public string? GroupName { get; set; }
        [JsonProperty("parameters")] public Dictionary<string, string> Parameters { get; set; } = new();
    }

    // ── 視圖 ─────────────────────────────────────────────
    public class ViewDto
    {
        [JsonProperty("id")]        public long   Id        { get; set; }
        [JsonProperty("name")]      public string Name      { get; set; } = "";
        [JsonProperty("viewType")]  public string ViewType  { get; set; } = "";
        [JsonProperty("levelName")] public string LevelName { get; set; } = "";
        [JsonProperty("scale")]     public int    Scale     { get; set; }
        /// <summary>
        /// 圖紙號碼。本案的「圖紙號碼」參數是空的，真正的圖號寫在工作集名稱裡
        /// （視圖 "圖紙: A9-17 - 廁所AB2詳圖"），所以兩邊都找。
        /// </summary>
        [JsonProperty("sheetNumber")] public string SheetNumber { get; set; } = "";
    }

    public class CreateViewRequest
    {
        [JsonProperty("viewType")]  public string  ViewType  { get; set; } = "FloorPlan";
        [JsonProperty("levelName")] public string? LevelName { get; set; }
        [JsonProperty("name")]      public string? Name      { get; set; }
    }

    public class ScreenshotResult
    {
        [JsonProperty("viewName")]   public string ViewName   { get; set; } = "";
        [JsonProperty("filePath")]   public string FilePath   { get; set; } = "";
        [JsonProperty("width")]      public int    Width      { get; set; }
        [JsonProperty("height")]     public int    Height     { get; set; }
    }

    // ── 樓層 / 房間 / 材質 ───────────────────────────────
    public class LevelDto
    {
        [JsonProperty("id")]        public long   Id        { get; set; }
        [JsonProperty("name")]      public string Name      { get; set; } = "";
        [JsonProperty("elevation")] public double Elevation { get; set; }
    }

    public class RoomDto
    {
        [JsonProperty("id")]        public long   Id        { get; set; }
        [JsonProperty("name")]      public string Name      { get; set; } = "";
        [JsonProperty("number")]    public string Number    { get; set; } = "";
        [JsonProperty("area")]      public double Area      { get; set; }
        [JsonProperty("levelName")] public string LevelName { get; set; } = "";
    }

    // ── 匯出 ─────────────────────────────────────────────
    public class ExportRequest
    {
        [JsonProperty("outputPath")] public string OutputPath { get; set; } = "";
        [JsonProperty("viewIds")]    public List<long>? ViewIds { get; set; }
    }

    public class ExportResult
    {
        [JsonProperty("filePath")] public string FilePath { get; set; } = "";
        [JsonProperty("format")]   public string Format   { get; set; } = "";
        [JsonProperty("sizeBytes")]public long   SizeBytes { get; set; }
    }

    // ── 封閉線整理 ───────────────────────────────────────
    public class CloseLoopRequest
    {
        [JsonProperty("elementIds")]  public List<long> ElementIds { get; set; } = new();
        [JsonProperty("toleranceMm")] public double     ToleranceMm { get; set; } = 3.0;
        [JsonProperty("apply")]       public bool       Apply       { get; set; } = false;
    }

    public class CloseLoopResult
    {
        [JsonProperty("applied")]        public bool        Applied        { get; set; }
        [JsonProperty("lineCount")]      public int         LineCount      { get; set; }
        [JsonProperty("skippedNonLine")] public List<long>  SkippedNonLine { get; set; } = new();
        [JsonProperty("toleranceMm")]    public double      ToleranceMm    { get; set; }
        [JsonProperty("movedLines")]     public int         MovedLines     { get; set; }
        [JsonProperty("changedLines")]   public int         ChangedLines   { get; set; }
        [JsonProperty("isClosed")]       public bool        IsClosed       { get; set; }
        [JsonProperty("components")]     public int         Components     { get; set; }
        [JsonProperty("openEnds")]       public int         OpenEnds       { get; set; }
        [JsonProperty("branchPoints")]   public int         BranchPoints   { get; set; }
        [JsonProperty("degenerate")]     public List<long>  Degenerate     { get; set; } = new();
        [JsonProperty("crossings")]      public List<long[]> Crossings     { get; set; } = new();
        [JsonProperty("failed")]         public List<string> Failed        { get; set; } = new();
    }

    // ── 標註：標籤 ────────────────────────────────────────
    public class TagTypeDto
    {
        [JsonProperty("id")]         public long   Id         { get; set; }
        [JsonProperty("familyName")] public string FamilyName { get; set; } = "";
        [JsonProperty("typeName")]   public string TypeName   { get; set; } = "";
        [JsonProperty("category")]   public string Category   { get; set; } = "";
    }

    public class TagElementsRequest
    {
        [JsonProperty("viewId")]          public long         ViewId          { get; set; }
        [JsonProperty("elementIds")]      public List<long>?  ElementIds      { get; set; }
        [JsonProperty("categories")]      public List<string>? Categories     { get; set; }
        [JsonProperty("tagFamilyName")]   public string?      TagFamilyName   { get; set; }
        [JsonProperty("tagTypeName")]     public string?      TagTypeName     { get; set; }
        [JsonProperty("offsetMm")]        public double       OffsetMm        { get; set; } = 0;
        [JsonProperty("offsetDirection")] public string       OffsetDirection { get; set; } = "none";
        [JsonProperty("hasLeader")]       public bool         HasLeader       { get; set; } = false;
        [JsonProperty("orientation")]     public string       Orientation     { get; set; } = "horizontal";
        [JsonProperty("skipTagged")]      public bool         SkipTagged      { get; set; } = true;
        /// <summary>連同視圖裡已載入的 RVT 連結一起找設備。本案設備都在主模型，預設關閉。</summary>
        [JsonProperty("includeLinks")]    public bool         IncludeLinks    { get; set; } = false;
        /// <summary>
        /// 限定範圍用的種子元件，通常就是這張立面的裝修背牆。
        /// 只保留水平方向落在種子範圍內、且深度在種子前方 maxDepthMm 之內的元件，
        /// 藉此把背景房間的設備排除掉。
        /// </summary>
        [JsonProperty("seedElementId")]   public long?        SeedElementId   { get; set; }
        [JsonProperty("maxDepthMm")]      public double       MaxDepthMm      { get; set; } = 2000;
        /// <summary>
        /// 沒給 seedElementId 時，直接找出這張圖在畫的房間，用房間邊界當範圍。
        /// 這個值是往畫面深處取樣的上限，超過就放棄。
        /// </summary>
        [JsonProperty("roomSearchDepthMm")] public double     RoomSearchDepthMm { get; set; } = 8000;
        /// <summary>類型標記是空的就不放標籤，改列進 skipped。</summary>
        [JsonProperty("skipBlankMark")]   public bool         SkipBlankMark   { get; set; } = true;
        /// <summary>
        /// 只標類型標記符合這個正規式的元件。菱形與矩形的分界不是類別而是編號格式：
        /// 材料是「字母+數字-數字」（W3-11、F3-13、C3-50），設備不是（D9、B2+C2、H3）。
        /// </summary>
        [JsonProperty("markInclude")]     public string?      MarkInclude     { get; set; }
        /// <summary>類型標記符合這個正規式的元件就不標，用來把材料編號排除在設備標籤之外。</summary>
        [JsonProperty("markExclude")]     public string?      MarkExclude     { get; set; }
        /// <summary>
        /// 同一個編號只放一個標籤，取最靠背牆的那個元件。
        /// 材料標籤要開（一面牆一個編號，不是每道牆各一個），設備標籤不要開。
        /// </summary>
        [JsonProperty("dedupeByMark")]    public bool         DedupeByMark    { get; set; } = false;
        /// <summary>
        /// onElement = 標籤壓在元件上（材料標籤用）；
        /// sideLeaders = 以房間中線為界分邊，標籤頭拉到房間兩側外的空白區，引線水平指回設備。
        /// sideLeaders 會強制開啟引線，並且需要裁切時把 annotationCropMm 放大，頭才不會被擠回框內。
        /// </summary>
        [JsonProperty("layout")]          public string       Layout          { get; set; } = "onElement";
        /// <summary>標籤頭離房間邊界多遠，mm。</summary>
        [JsonProperty("leaderMarginMm")]  public double       LeaderMarginMm  { get; set; } = 500;
        /// <summary>同一側兩個標籤頭的最小垂直間距，mm。</summary>
        [JsonProperty("leaderSpacingMm")] public double       LeaderSpacingMm { get; set; } = 400;
        [JsonProperty("apply")]           public bool         Apply           { get; set; } = false;
    }

    // ── 刪除視圖裡的標籤（跑錯時的退路） ──────────────────────────
    public class DeleteTagsRequest
    {
        [JsonProperty("viewId")]        public long        ViewId        { get; set; }
        /// <summary>指定要刪哪幾個標籤。給了就只看這份清單，其他條件不生效。</summary>
        [JsonProperty("tagIds")]        public List<long>? TagIds        { get; set; }
        [JsonProperty("tagFamilyName")] public string?     TagFamilyName { get; set; }
        [JsonProperty("tagTypeName")]   public string?     TagTypeName   { get; set; }
        /// <summary>只刪標籤文字是空的（類型標記沒填，畫面上是空菱形）。</summary>
        [JsonProperty("blankTextOnly")] public bool        BlankTextOnly { get; set; } = false;
        [JsonProperty("apply")]         public bool        Apply         { get; set; } = false;
    }

    public class DeleteTagsResult
    {
        [JsonProperty("applied")]  public bool         Applied  { get; set; }
        [JsonProperty("viewId")]   public long         ViewId   { get; set; }
        [JsonProperty("viewName")] public string       ViewName { get; set; } = "";
        [JsonProperty("matched")]  public List<string> Matched  { get; set; } = new();
        [JsonProperty("deleted")]  public int          Deleted  { get; set; }
        [JsonProperty("failed")]   public List<string> Failed   { get; set; } = new();
    }

    // ── 讀出視圖裡現有的標註（照抄既有圖面慣例用） ────────────────
    public class ViewTagDto
    {
        [JsonProperty("tagId")]       public long         TagId       { get; set; }
        [JsonProperty("category")]    public string       Category    { get; set; } = "";
        [JsonProperty("familyName")]  public string       FamilyName  { get; set; } = "";
        [JsonProperty("typeName")]    public string       TypeName    { get; set; } = "";
        [JsonProperty("text")]        public string       Text        { get; set; } = "";
        [JsonProperty("hasLeader")]   public bool         HasLeader   { get; set; }
        [JsonProperty("taggedIds")]   public List<long>   TaggedIds   { get; set; } = new();
        [JsonProperty("taggedNames")] public List<string> TaggedNames { get; set; } = new();
    }

    public class ViewDimDto
    {
        [JsonProperty("dimensionId")] public long         DimensionId { get; set; }
        [JsonProperty("category")]    public string       Category    { get; set; } = "";
        [JsonProperty("typeName")]    public string       TypeName    { get; set; } = "";
        [JsonProperty("segmentsMm")]  public List<double> SegmentsMm  { get; set; } = new();
    }

    public class ViewTextDto
    {
        [JsonProperty("textNoteId")] public long   TextNoteId { get; set; }
        [JsonProperty("typeName")]   public string TypeName   { get; set; } = "";
        [JsonProperty("text")]       public string Text       { get; set; } = "";
        /// <summary>文字位置，換算成圖面 mm（已除以視圖比例），排版核對用。</summary>
        [JsonProperty("x")]          public double X          { get; set; }
        [JsonProperty("y")]          public double Y          { get; set; }
    }

    public class ViewAnnotationsResult
    {
        [JsonProperty("viewId")]     public long             ViewId     { get; set; }
        [JsonProperty("viewName")]   public string           ViewName   { get; set; } = "";
        [JsonProperty("tags")]       public List<ViewTagDto> Tags       { get; set; } = new();
        [JsonProperty("dimensions")] public List<ViewDimDto> Dimensions { get; set; } = new();
        [JsonProperty("textNotes")]  public List<ViewTextDto> TextNotes { get; set; } = new();
    }

    public class TagPlacementDto
    {
        [JsonProperty("elementId")]   public long   ElementId   { get; set; }
        [JsonProperty("elementName")] public string ElementName { get; set; } = "";
        [JsonProperty("category")]    public string Category    { get; set; } = "";
        [JsonProperty("source")]      public string Source      { get; set; } = "";
        [JsonProperty("tagId")]       public long   TagId       { get; set; }
        [JsonProperty("tagText")]     public string TagText     { get; set; } = "";
        [JsonProperty("x")]           public double X           { get; set; }
        [JsonProperty("y")]           public double Y           { get; set; }
        [JsonProperty("z")]           public double Z           { get; set; }
    }

    public class SkippedDto
    {
        [JsonProperty("elementId")] public long   ElementId { get; set; }
        [JsonProperty("reason")]    public string Reason    { get; set; } = "";
    }

    public class TagElementsResult
    {
        [JsonProperty("applied")]      public bool                 Applied      { get; set; }
        [JsonProperty("viewId")]       public long                 ViewId       { get; set; }
        [JsonProperty("viewName")]     public string               ViewName     { get; set; } = "";
        [JsonProperty("tagTypeUsed")]  public string               TagTypeUsed  { get; set; } = "";
        /// <summary>這次用什麼界定「這一間」——自動找到的房間，或指定的種子元件。</summary>
        [JsonProperty("scope")]        public string               Scope        { get; set; } = "";
        [JsonProperty("candidates")]   public int                  Candidates   { get; set; }
        [JsonProperty("placed")]       public List<TagPlacementDto> Placed      { get; set; } = new();
        [JsonProperty("skipped")]      public List<SkippedDto>     Skipped      { get; set; } = new();
    }

    // ── 標註：尺寸鏈 ──────────────────────────────────────
    public class DimensionRequest
    {
        [JsonProperty("viewId")]            public long        ViewId            { get; set; }
        [JsonProperty("elementIds")]        public List<long>  ElementIds        { get; set; } = new();
        [JsonProperty("direction")]         public string      Direction         { get; set; } = "horizontal";
        [JsonProperty("offsetMm")]          public double      OffsetMm          { get; set; } = 500;
        [JsonProperty("side")]              public string      Side              { get; set; } = "auto";
        [JsonProperty("dimensionTypeName")] public string?     DimensionTypeName { get; set; }
        [JsonProperty("apply")]             public bool        Apply             { get; set; } = false;
    }

    public class DimensionRefDto
    {
        [JsonProperty("elementId")]  public long   ElementId  { get; set; }
        [JsonProperty("name")]       public string Name       { get; set; } = "";
        [JsonProperty("category")]   public string Category   { get; set; } = "";
        [JsonProperty("refCount")]   public int    RefCount   { get; set; }
        [JsonProperty("positionMm")] public double PositionMm { get; set; }
    }

    public class DimensionResult
    {
        [JsonProperty("applied")]       public bool                 Applied       { get; set; }
        [JsonProperty("viewId")]        public long                 ViewId        { get; set; }
        [JsonProperty("viewName")]      public string               ViewName      { get; set; } = "";
        [JsonProperty("direction")]     public string               Direction     { get; set; } = "";
        [JsonProperty("dimensionId")]   public long                 DimensionId   { get; set; }
        [JsonProperty("segmentCount")]  public int                  SegmentCount  { get; set; }
        [JsonProperty("segmentsMm")]    public List<double>         SegmentsMm    { get; set; } = new();
        [JsonProperty("references")]    public List<DimensionRefDto> References   { get; set; } = new();
        [JsonProperty("skipped")]       public List<SkippedDto>     Skipped       { get; set; } = new();
        [JsonProperty("dimensionType")] public string               DimensionType { get; set; } = "";
    }

    // ── 視圖裁切範圍 ──────────────────────────────────────
    public class CropViewRequest
    {
        [JsonProperty("viewId")]           public long          ViewId           { get; set; }
        /// <summary>
        /// room = 直接裁到這張圖在畫的那個房間的邊界（房間邊界就是牆內面），不做牆體搜尋；
        /// bounding = 找種子外側最近的一道結構；extent = 所有符合元件的總範圍；manual = 直接給數值。
        /// </summary>
        [JsonProperty("mode")]             public string        Mode             { get; set; } = "room";
        /// <summary>種子元件（通常是房間或裝修背牆）。未給時會自動找出這張圖在畫的房間來當種子。</summary>
        [JsonProperty("seedElementId")]    public long?         SeedElementId    { get; set; }
        /// <summary>自動找房間時，往畫面深處取樣的上限（mm）。</summary>
        [JsonProperty("roomSearchDepthMm")] public double       RoomSearchDepthMm { get; set; } = 8000;
        /// <summary>perpendicular = 只採計牆身方向朝畫面內延伸的牆（室內立面的側牆）；any = 不篩。</summary>
        [JsonProperty("wallRun")]          public string        WallRun          { get; set; } = "perpendicular";
        // mode="manual" 時使用，單位 mm，裁切框座標
        [JsonProperty("leftMm")]           public double?       LeftMm           { get; set; }
        [JsonProperty("rightMm")]          public double?       RightMm          { get; set; }
        [JsonProperty("bottomMm")]         public double?       BottomMm         { get; set; }
        [JsonProperty("topMm")]            public double?       TopMm            { get; set; }
        [JsonProperty("axis")]             public string        Axis             { get; set; } = "horizontal";
        /// <summary>厚度達此值即視為結構（RC12以上 → 120）。</summary>
        [JsonProperty("minThicknessMm")]   public double        MinThicknessMm   { get; set; } = 120;
        /// <summary>類型名稱含這些關鍵字就視為結構，不論厚度。預設 ["輕隔間"]。</summary>
        [JsonProperty("includeTypeNames")] public List<string>? IncludeTypeNames { get; set; }
        [JsonProperty("excludeTypeNames")] public List<string>? ExcludeTypeNames { get; set; }
        /// <summary>inner = 裁到牆內面，也就是房間邊界（預設）；outer = 裁到牆外面，牆身會被剖出來。</summary>
        [JsonProperty("wallEdge")]         public string        WallEdge         { get; set; } = "inner";
        [JsonProperty("paddingMm")]        public double        PaddingMm        { get; set; } = 0;
        /// <summary>搜尋範圍在現有裁切框外再外擴多少，讓略在框外的結構牆也找得到。</summary>
        [JsonProperty("searchPaddingMm")]  public double        SearchPaddingMm  { get; set; } = 2000;
        [JsonProperty("includeLinks")]     public bool          IncludeLinks     { get; set; } = true;
        /// <summary>直接指定本模型內的元件當裁切依據，會略過上面所有篩選規則。</summary>
        [JsonProperty("elementIds")]       public List<long>?   ElementIds       { get; set; }
        [JsonProperty("cropVisible")]      public bool?         CropVisible      { get; set; }
        /// <summary>
        /// 標註裁剪範圍要比模型裁剪範圍往外放多少（四邊相同，mm）。
        /// 模型裁剪收緊之後，標籤若貼近邊界會被 Revit 擠進框內並自動長出引線；
        /// 把標註裁剪放大就能讓標籤安心待在原位。0 = 不動標註裁剪。
        /// </summary>
        [JsonProperty("annotationCropMm")] public double        AnnotationCropMm { get; set; } = 0;
        [JsonProperty("apply")]            public bool          Apply            { get; set; } = false;
    }

    public class CropCandidateDto
    {
        [JsonProperty("elementId")]   public long   ElementId   { get; set; }
        [JsonProperty("source")]      public string Source      { get; set; } = "";
        [JsonProperty("typeName")]    public string TypeName    { get; set; } = "";
        [JsonProperty("thicknessMm")] public double ThicknessMm { get; set; }
        [JsonProperty("matchedBy")]   public string MatchedBy   { get; set; } = "";
        [JsonProperty("minMm")]       public double MinMm       { get; set; }
        [JsonProperty("maxMm")]       public double MaxMm       { get; set; }
        /// <summary>牆身方向與視線方向的夾角餘弦絕對值。接近 1 = 往畫面內延伸（側牆）。</summary>
        [JsonProperty("runAlignment")] public double RunAlignment { get; set; }
    }

    public class CropRectDto
    {
        [JsonProperty("leftMm")]   public double LeftMm   { get; set; }
        [JsonProperty("rightMm")]  public double RightMm  { get; set; }
        [JsonProperty("bottomMm")] public double BottomMm { get; set; }
        [JsonProperty("topMm")]    public double TopMm    { get; set; }
        [JsonProperty("widthMm")]  public double WidthMm  { get; set; }
        [JsonProperty("heightMm")] public double HeightMm { get; set; }
    }

    public class CropViewResult
    {
        [JsonProperty("applied")]       public bool                   Applied       { get; set; }
        [JsonProperty("viewId")]        public long                   ViewId        { get; set; }
        [JsonProperty("viewName")]      public string                 ViewName      { get; set; } = "";
        [JsonProperty("mode")]          public string                 Mode          { get; set; } = "";
        [JsonProperty("axis")]          public string                 Axis          { get; set; } = "";
        [JsonProperty("rule")]          public string                 Rule          { get; set; } = "";
        [JsonProperty("searchedLinks")] public List<string>           SearchedLinks { get; set; } = new();
        [JsonProperty("seed")]          public string                 Seed          { get; set; } = "";
        [JsonProperty("leftWall")]      public CropCandidateDto?      LeftWall      { get; set; }
        [JsonProperty("rightWall")]     public CropCandidateDto?      RightWall     { get; set; }
        [JsonProperty("bottomRef")]     public CropCandidateDto?      BottomRef     { get; set; }
        [JsonProperty("topRef")]        public CropCandidateDto?      TopRef        { get; set; }
        [JsonProperty("matched")]       public List<CropCandidateDto> Matched       { get; set; } = new();
        [JsonProperty("nearMiss")]      public List<CropCandidateDto> NearMiss      { get; set; } = new();
        [JsonProperty("before")]        public CropRectDto?           Before        { get; set; }
        [JsonProperty("after")]         public CropRectDto?           After         { get; set; }
        [JsonProperty("skipped")]       public List<SkippedDto>       Skipped       { get; set; } = new();
    }
}
