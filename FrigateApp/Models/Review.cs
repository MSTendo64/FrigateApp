using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>Сегмент события для обзора (ответ GET /api/review).</summary>
public class ReviewSegmentResponse
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("camera")]
    public string Camera { get; set; } = "";

    [JsonProperty("start_time")]
    public double StartTime { get; set; }

    [JsonProperty("end_time")]
    public double EndTime { get; set; }

    [JsonProperty("has_been_reviewed")]
    public bool HasBeenReviewed { get; set; }

    [JsonProperty("severity")]
    public string Severity { get; set; } = "";

    [JsonProperty("thumb_path")]
    public string? ThumbPath { get; set; }

    [JsonProperty("data")]
    public ReviewSegmentData? Data { get; set; }
}

public class ReviewSegmentData
{
    [JsonProperty("objects")]
    public List<string>? Objects { get; set; }

    [JsonProperty("audio")]
    public List<string>? Audio { get; set; }

    [JsonProperty("zones")]
    public List<string>? Zones { get; set; }
}

/// <summary>Сводка за последние 24 часа (GET /api/review/summary).</summary>
public class ReviewSummaryResponse
{
    [JsonProperty("last24Hours")]
    public Last24HoursReview? Last24Hours { get; set; }

    [JsonProperty("root")]
    public Dictionary<string, DayReview>? Root { get; set; }
}

public class Last24HoursReview
{
    [JsonProperty("reviewed_alert")]
    public int ReviewedAlert { get; set; }

    [JsonProperty("reviewed_detection")]
    public int ReviewedDetection { get; set; }

    [JsonProperty("total_alert")]
    public int TotalAlert { get; set; }

    [JsonProperty("total_detection")]
    public int TotalDetection { get; set; }
}

public class DayReview
{
    [JsonProperty("day")]
    public string? Day { get; set; }

    [JsonProperty("reviewed_alert")]
    public int ReviewedAlert { get; set; }

    [JsonProperty("reviewed_detection")]
    public int ReviewedDetection { get; set; }

    [JsonProperty("total_alert")]
    public int TotalAlert { get; set; }

    [JsonProperty("total_detection")]
    public int TotalDetection { get; set; }
}
