// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    // ── 新專案（含單位與中央檔）────────────────────────────
    public class NewProjectRequest
    {
        [JsonProperty("templatePath")]       public string  TemplatePath       { get; set; } = "";
        [JsonProperty("savePath")]           public string  SavePath           { get; set; } = "";
        [JsonProperty("lengthUnit")]         public string? LengthUnit         { get; set; }
        [JsonProperty("central")]            public bool    Central            { get; set; }
        [JsonProperty("levelsGridsWorkset")] public string? LevelsGridsWorkset { get; set; }
        [JsonProperty("defaultWorkset")]     public string? DefaultWorkset     { get; set; }
        [JsonProperty("overwrite")]          public bool    Overwrite          { get; set; } = true;
        [JsonProperty("openAfterSave")]      public bool    OpenAfterSave      { get; set; } = true;
    }

    public class NewProjectResult
    {
        [JsonProperty("savedPath")]  public string SavedPath  { get; set; } = "";
        [JsonProperty("central")]    public bool   Central    { get; set; }
        [JsonProperty("lengthUnit")] public string LengthUnit { get; set; } = "";
        [JsonProperty("opened")]     public bool   Opened     { get; set; }
        [JsonProperty("title")]      public string Title      { get; set; } = "";
        [JsonProperty("note")]       public string Note       { get; set; } = "";
    }

    // ── 標高 ─────────────────────────────────────────────
    public class LevelSpec
    {
        [JsonProperty("name")]        public string? Name        { get; set; }
        [JsonProperty("elevationMm")] public double  ElevationMm { get; set; }
    }

    public class CreateLevelsRequest
    {
        [JsonProperty("levels")]       public List<LevelSpec>? Levels       { get; set; }
        [JsonProperty("skipExisting")] public bool             SkipExisting { get; set; } = true;
    }

    public class CreateLevelsResult
    {
        [JsonProperty("created")] public int           Created { get; set; }
        [JsonProperty("skipped")] public List<string>  Skipped { get; set; } = new();
        [JsonProperty("levels")]  public List<LevelDto> Levels { get; set; } = new();
    }

    // ── 型別查詢（牆 / 樓板）──────────────────────────────
    public class BuildTypeDto
    {
        [JsonProperty("id")]      public long    Id      { get; set; }
        [JsonProperty("family")]  public string  Family  { get; set; } = "";
        [JsonProperty("name")]    public string  Name    { get; set; } = "";
        [JsonProperty("kind")]    public string  Kind    { get; set; } = "";
        [JsonProperty("widthMm")] public double? WidthMm { get; set; }
    }

    // ── 牆（含帷幕牆）─────────────────────────────────────
    public class WallLoopSpec
    {
        [JsonProperty("points")] public List<List<double>>? Points { get; set; }
        [JsonProperty("closed")] public bool                Closed { get; set; } = true;
    }

    public class CreateWallsRequest
    {
        [JsonProperty("wallTypeName")] public string? WallTypeName { get; set; }
        [JsonProperty("levelName")]    public string  LevelName    { get; set; } = "";
        [JsonProperty("heightMm")]     public double  HeightMm     { get; set; }
        [JsonProperty("baseOffsetMm")] public double  BaseOffsetMm { get; set; }
        [JsonProperty("loops")]        public List<WallLoopSpec>? Loops { get; set; }
        [JsonProperty("minSegmentMm")] public double  MinSegmentMm { get; set; } = 2.0;
        [JsonProperty("structural")]   public bool    Structural   { get; set; }
    }

    public class CreateWallsResult
    {
        [JsonProperty("created")]      public int          Created      { get; set; }
        [JsonProperty("wallTypeUsed")] public string       WallTypeUsed { get; set; } = "";
        [JsonProperty("levelName")]    public string       LevelName    { get; set; } = "";
        [JsonProperty("ids")]          public List<long>   Ids          { get; set; } = new();
        [JsonProperty("skipped")]      public List<string> Skipped      { get; set; } = new();
    }

    // ── 樓板 ─────────────────────────────────────────────
    public class FloorSpec
    {
        [JsonProperty("levelName")]      public string LevelName      { get; set; } = "";
        [JsonProperty("outer")]          public List<List<double>>? Outer { get; set; }
        [JsonProperty("openings")]       public List<List<List<double>>>? Openings { get; set; }
        [JsonProperty("heightOffsetMm")] public double HeightOffsetMm { get; set; }
    }

    public class CreateFloorsRequest
    {
        [JsonProperty("floorTypeName")] public string?  FloorTypeName { get; set; }
        [JsonProperty("newTypeName")]   public string?  NewTypeName   { get; set; }
        [JsonProperty("thicknessMm")]   public double?  ThicknessMm   { get; set; }
        [JsonProperty("minSegmentMm")]  public double   MinSegmentMm  { get; set; } = 2.0;
        [JsonProperty("floors")]        public List<FloorSpec>? Floors { get; set; }
    }

    public class CreateFloorsResult
    {
        [JsonProperty("created")]       public int          Created       { get; set; }
        [JsonProperty("openings")]      public int          Openings      { get; set; }
        [JsonProperty("floorTypeUsed")] public string       FloorTypeUsed { get; set; } = "";
        [JsonProperty("ids")]           public List<long>   Ids           { get; set; } = new();
        [JsonProperty("skipped")]       public List<string> Skipped       { get; set; } = new();
    }

    // ── 圓柱（DirectShape）────────────────────────────────
    public class ColumnSpec
    {
        [JsonProperty("x")]        public double  X        { get; set; }
        [JsonProperty("y")]        public double  Y        { get; set; }
        [JsonProperty("radiusMm")] public double? RadiusMm { get; set; }
    }

    public class CreateColumnsRequest
    {
        [JsonProperty("columns")]    public List<ColumnSpec>? Columns { get; set; }
        [JsonProperty("baseMm")]     public double  BaseMm     { get; set; }
        [JsonProperty("topMm")]      public double  TopMm      { get; set; }
        [JsonProperty("diameterMm")] public double? DiameterMm { get; set; }
    }

    public class CreateColumnsResult
    {
        [JsonProperty("created")] public int          Created { get; set; }
        [JsonProperty("ids")]     public List<long>   Ids     { get; set; } = new();
        [JsonProperty("skipped")] public List<string> Skipped { get; set; } = new();
    }
}
