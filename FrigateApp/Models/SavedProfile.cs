using System.Text.Json.Serialization;

namespace FrigateApp.Models;

public class SavedProfile
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Отображаемое имя для списка (сервер + пользователь).</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? $"{ServerUrl} — {Username}"
        : DisplayName;
}
