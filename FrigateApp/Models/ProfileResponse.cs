using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrigateApp.Models;

/// <summary>
/// Ответ API /api/profile — профиль пользователя и список разрешённых камер.
/// </summary>
public class ProfileResponse
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("allowed_cameras")]
    public List<string>? AllowedCameras { get; set; }
}
