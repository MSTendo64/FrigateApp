using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>Событие из GET /api/events.</summary>
public class EventDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("camera")]
    public string Camera { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("sub_label")]
    public string? SubLabel { get; set; }

    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double? EndTime { get; set; }

    [JsonPropertyName("has_clip")]
    public bool HasClip { get; set; }

    [JsonPropertyName("has_snapshot")]
    public bool HasSnapshot { get; set; }

    [JsonPropertyName("zones")]
    public List<string>? Zones { get; set; }

    [JsonPropertyName("retain_indefinitely")]
    public bool RetainIndefinitely { get; set; }

    [JsonPropertyName("data")]
    public EventDataDto? Data { get; set; }
}

public class EventDataDto
{
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("top_score")]
    public double? TopScore { get; set; }
}
