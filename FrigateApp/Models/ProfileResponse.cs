using System.Collections.Generic;
using Newtonsoft.Json;

namespace FrigateApp.Models;

/// <summary>
/// Ответ API /api/profile — профиль пользователя и список разрешённых камер.
/// </summary>
public class ProfileResponse
{
    [JsonProperty("username")]
    public string? Username { get; set; }

    [JsonProperty("role")]
    public string? Role { get; set; }

    [JsonProperty("allowed_cameras")]
    public List<string>? AllowedCameras { get; set; }
}
