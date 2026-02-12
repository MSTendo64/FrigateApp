using System.Collections.Generic;
using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>
/// Ответ API /api/config — конфигурация Frigate.
/// </summary>
public class FrigateConfigResponse
{
    [JsonProperty("cameras")]
    public Dictionary<string, CameraConfig>? Cameras { get; set; }

    [JsonProperty("camera_groups")]
    public Dictionary<string, CameraGroupConfig>? CameraGroups { get; set; }
}

public class CameraConfig
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("friendly_name")]
    public string? FriendlyName { get; set; }

    [JsonProperty("enabled")]
    public bool? Enabled { get; set; } = true;

    [JsonProperty("ffmpeg")]
    public FfmpegConfig? Ffmpeg { get; set; }

    [JsonProperty("rotate")]
    public float Rotate { get; set; } = 0.0f;

    /// <summary>Потоки для live (как в вебе: ключи "sub"/"main" или др., значения — имена потоков go2rtc).</summary>
    [JsonProperty("live")]
    public CameraLiveConfig? Live { get; set; }
}

public class CameraLiveConfig
{
    [JsonProperty("streams")]
    public Dictionary<string, string>? Streams { get; set; }
}

public class FfmpegConfig
{
    [JsonProperty("inputs")]
    public List<FfmpegInput>? Inputs { get; set; }
}

public class FfmpegInput
{
    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("roles")]
    public List<string>? Roles { get; set; }
}

public class CameraGroupConfig
{
    [JsonProperty("cameras")]
    public List<string>? Cameras { get; set; }

    [JsonProperty("icon")]
    public string? Icon { get; set; }

    [JsonProperty("order")]
    public int Order { get; set; }
}
