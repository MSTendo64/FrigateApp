namespace FrigateApp.Models;

/// <summary>Состояние зума мини-камеры (сетка): масштаб и сдвиг.</summary>
public class CameraZoomState
{
    public double ZoomLevel { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
}
