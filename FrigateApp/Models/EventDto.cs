using System.Collections.Generic;
using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>Событие из GET /api/events.</summary>
public class EventDto
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("camera")]
    public string Camera { get; set; } = "";

    [JsonProperty("label")]
    public string Label { get; set; } = "";

    [JsonProperty("sub_label")]
    public string? SubLabel { get; set; }

    [JsonProperty("start_time")]
    public double StartTime { get; set; }

    [JsonProperty("end_time")]
    public double? EndTime { get; set; }

    [JsonProperty("has_clip")]
    public bool HasClip { get; set; }

    [JsonProperty("has_snapshot")]
    public bool HasSnapshot { get; set; }

    [JsonProperty("zones")]
    public List<string>? Zones { get; set; }

    [JsonProperty("retain_indefinitely")]
    public bool RetainIndefinitely { get; set; }

    [JsonProperty("data")]
    public EventDataDto? Data { get; set; }
}

public class EventDataDto
{
    [JsonProperty("score")]
    public double? Score { get; set; }

    [JsonProperty("top_score")]
    public double? TopScore { get; set; }
}
