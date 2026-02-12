using System.Collections.Generic;
using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>Сегмент записи из GET /api/{camera}/recordings.</summary>
public class RecordingSegmentDto
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("start_time")]
    public double StartTime { get; set; }

    [JsonProperty("end_time")]
    public double EndTime { get; set; }

    [JsonProperty("duration")]
    public double Duration { get; set; }

    [JsonProperty("motion")]
    public double Motion { get; set; }

    [JsonProperty("objects")]
    public double Objects { get; set; }
}

/// <summary>Часовая сводка за день из GET /api/{camera}/recordings/summary.</summary>
public class RecordingHourDto
{
    [JsonProperty("hour")]
    public string Hour { get; set; } = "";

    [JsonProperty("events")]
    public int Events { get; set; }

    [JsonProperty("motion")]
    public double Motion { get; set; }

    [JsonProperty("objects")]
    public double Objects { get; set; }

    [JsonProperty("duration")]
    public double Duration { get; set; }
}

/// <summary>День с почасовой сводкой.</summary>
public class RecordingsDaySummaryDto
{
    [JsonProperty("day")]
    public string Day { get; set; } = "";

    [JsonProperty("events")]
    public int Events { get; set; }

    [JsonProperty("hours")]
    public List<RecordingHourDto>? Hours { get; set; }
}
