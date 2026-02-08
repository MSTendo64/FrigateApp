using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>
/// Ответ API /api/config — конфигурация Frigate.
/// </summary>
public class FrigateConfigResponse
{
    [JsonPropertyName("cameras")]
    public Dictionary<string, CameraConfig>? Cameras { get; set; }

    [JsonPropertyName("camera_groups")]
    public Dictionary<string, CameraGroupConfig>? CameraGroups { get; set; }
}

public class CameraConfig
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; } = true;

    [JsonPropertyName("ffmpeg")]
    public FfmpegConfig? Ffmpeg { get; set; }

    [JsonPropertyName("rotate")]
    public float Rotate { get; set; } = 0.0f;
}

public class FfmpegConfig
{
    [JsonPropertyName("inputs")]
    public List<FfmpegInput>? Inputs { get; set; }
}

public class FfmpegInput
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }
}

public class CameraGroupConfig
{
    [JsonPropertyName("cameras")]
    public List<string>? Cameras { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}
