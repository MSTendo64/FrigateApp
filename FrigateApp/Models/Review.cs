using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>Сегмент события для обзора (ответ GET /api/review).</summary>
public class ReviewSegmentResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("camera")]
    public string Camera { get; set; } = "";

    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double EndTime { get; set; }

    [JsonPropertyName("has_been_reviewed")]
    public bool HasBeenReviewed { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("thumb_path")]
    public string? ThumbPath { get; set; }

    [JsonPropertyName("data")]
    public ReviewSegmentData? Data { get; set; }
}

public class ReviewSegmentData
{
    [JsonPropertyName("objects")]
    public List<string>? Objects { get; set; }

    [JsonPropertyName("audio")]
    public List<string>? Audio { get; set; }

    [JsonPropertyName("zones")]
    public List<string>? Zones { get; set; }
}

/// <summary>Сводка за последние 24 часа (GET /api/review/summary).</summary>
public class ReviewSummaryResponse
{
    [JsonPropertyName("last24Hours")]
    public Last24HoursReview? Last24Hours { get; set; }

    [JsonPropertyName("root")]
    public Dictionary<string, DayReview>? Root { get; set; }
}

public class Last24HoursReview
{
    [JsonPropertyName("reviewed_alert")]
    public int ReviewedAlert { get; set; }

    [JsonPropertyName("reviewed_detection")]
    public int ReviewedDetection { get; set; }

    [JsonPropertyName("total_alert")]
    public int TotalAlert { get; set; }

    [JsonPropertyName("total_detection")]
    public int TotalDetection { get; set; }
}

public class DayReview
{
    [JsonPropertyName("day")]
    public string? Day { get; set; }

    [JsonPropertyName("reviewed_alert")]
    public int ReviewedAlert { get; set; }

    [JsonPropertyName("reviewed_detection")]
    public int ReviewedDetection { get; set; }

    [JsonPropertyName("total_alert")]
    public int TotalAlert { get; set; }

    [JsonPropertyName("total_detection")]
    public int TotalDetection { get; set; }
}
