using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>Сегмент записи из GET /api/{camera}/recordings.</summary>
public class RecordingSegmentDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double EndTime { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("motion")]
    public double Motion { get; set; }

    [JsonPropertyName("objects")]
    public double Objects { get; set; }
}

/// <summary>Часовая сводка за день из GET /api/{camera}/recordings/summary.</summary>
public class RecordingHourDto
{
    [JsonPropertyName("hour")]
    public string Hour { get; set; } = "";

    [JsonPropertyName("events")]
    public int Events { get; set; }

    [JsonPropertyName("motion")]
    public double Motion { get; set; }

    [JsonPropertyName("objects")]
    public double Objects { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }
}

/// <summary>День с почасовой сводкой.</summary>
public class RecordingsDaySummaryDto
{
    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("events")]
    public int Events { get; set; }

    [JsonPropertyName("hours")]
    public List<RecordingHourDto>? Hours { get; set; }
}
