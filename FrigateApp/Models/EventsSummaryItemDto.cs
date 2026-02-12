using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>Элемент сводки GET /api/events/summary.</summary>
public class EventsSummaryItemDto
{
    [JsonProperty("camera")]
    public string Camera { get; set; } = "";

    [JsonProperty("label")]
    public string Label { get; set; } = "";

    [JsonProperty("sub_label")]
    public string? SubLabel { get; set; }

    [JsonProperty("day")]
    public string Day { get; set; } = "";

    [JsonProperty("count")]
    public int Count { get; set; }
}
