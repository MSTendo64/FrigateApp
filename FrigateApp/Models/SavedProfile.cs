using Newtonsoft.Json;

namespace FrigateApp.Models;

public class SavedProfile
{
    [JsonProperty("serverUrl")]
    public string ServerUrl { get; set; } = "";

    [JsonProperty("username")]
    public string Username { get; set; } = "";

    [JsonProperty("password")]
    public string? Password { get; set; }

    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Отображаемое имя для списка (сервер + пользователь).</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? $"{ServerUrl} — {Username}"
        : DisplayName;
}
