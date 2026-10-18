// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    /// <summary>把元件吸附到天花模矩格線的請求。</summary>
    public class SnapRequest
    {
        [JsonProperty("elementIds")]  public List<long>?   ElementIds  { get; set; }
        [JsonProperty("levelName")]   public string?       LevelName   { get; set; }
        [JsonProperty("categories")]  public List<string>? Categories  { get; set; }
        [JsonProperty("familyNames")] public List<string>? FamilyNames { get; set; }
        /// <summary>只吸附掛在這些主體（天花）上的元件。</summary>
        [JsonProperty("hostIds")]     public List<long>?   HostIds     { get; set; }
        [JsonProperty("bbox")]        public BBoxDto?      BBox        { get; set; }

        /// <summary>格線模矩，mm。明架天花通常 600。</summary>
        [JsonProperty("moduleMm")]    public double        ModuleMm    { get; set; } = 600;
        /// <summary>格線原點，mm，專案座標。格線位於 origin + k*module。</summary>
        [JsonProperty("originX")]     public double        OriginX     { get; set; }
        [JsonProperty("originY")]     public double        OriginY     { get; set; }
        /// <summary>"center" 吸到格心（預設）；"line" 吸到格線交點。</summary>
        [JsonProperty("target")]      public string?       Target      { get; set; } = "center";

        [JsonProperty("sampleCount")] public int           SampleCount { get; set; } = 8;
        [JsonProperty("dryRun")]      public bool          DryRun      { get; set; } = true;
    }

    public class SnapSample
    {
        [JsonProperty("id")]   public long   Id   { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("dxMm")] public double DxMm { get; set; }
        [JsonProperty("dyMm")] public double DyMm { get; set; }
    }

    public class SnapResult
    {
        [JsonProperty("dryRun")]        public bool   DryRun        { get; set; }
        [JsonProperty("moduleMm")]      public double ModuleMm      { get; set; }
        [JsonProperty("target")]        public string Target        { get; set; } = "";
        [JsonProperty("originX")]       public double OriginX       { get; set; }
        [JsonProperty("originY")]       public double OriginY       { get; set; }
        [JsonProperty("matchCount")]    public int    MatchCount    { get; set; }
        [JsonProperty("moveCount")]     public int    MoveCount     { get; set; }
        [JsonProperty("alreadyOnGrid")] public int    AlreadyOnGrid { get; set; }
        [JsonProperty("moved")]         public int    Moved         { get; set; }
        [JsonProperty("skipped")]       public int    Skipped       { get; set; }
        [JsonProperty("maxDxMm")]       public double MaxDxMm       { get; set; }
        [JsonProperty("maxDyMm")]       public double MaxDyMm       { get; set; }
        [JsonProperty("samples")]       public List<SnapSample> Samples  { get; set; } = new();
        [JsonProperty("warnings")]      public List<string>     Warnings { get; set; } = new();
    }
}
