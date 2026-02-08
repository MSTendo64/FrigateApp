using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>Элемент сводки GET /api/events/summary.</summary>
public class EventsSummaryItemDto
{
    [JsonPropertyName("camera")]
    public string Camera { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("sub_label")]
    public string? SubLabel { get; set; }

    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
