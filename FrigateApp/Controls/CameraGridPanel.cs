using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace FrigateApp.Controls;

/// <summary>
/// Панель для размещения камер с поддержкой вертикальных камер (2 клетки по высоте).
/// Упаковывает камеры плотно, заполняя пространство вокруг вертикальных камер.
/// </summary>
    public class CameraGridPanel : Panel
    {
        private const double TileMargin = 2;

    /// <summary>Масштаб размера плиток (0.5 = 50%, 1.0 = 100%, 2.0 = 200%).</summary>
    public static readonly StyledProperty<double> TileScaleProperty =
        AvaloniaProperty.Register<CameraGridPanel, double>(nameof(TileScale), 1.0);

    public double TileScale
    {
        get => GetValue(TileScaleProperty);
        set => SetValue(TileScaleProperty, value);
    }

    static CameraGridPanel()
    {
        AffectsMeasure<CameraGridPanel>(TileScaleProperty);
    }

    private double GetTileWidth() => 240 * TileScale;
    private double GetTileHeight() => 160 * TileScale;

    protected override Size MeasureOverride(Size availableSize)
    {
        var tileWidth = GetTileWidth();
        var tileHeight = GetTileHeight();
        
        var width = double.IsInfinity(availableSize.Width) ? 1920 : availableSize.Width;
        var columnsCount = Math.Max(1, (int)(width / (tileWidth + TileMargin * 2)));
        
        // Сетка для отслеживания занятых ячеек
        var grid = new List<List<bool>>();
        var currentRow = 0;
        
        foreach (var child in Children)
        {
            child.Measure(new Size(tileWidth, tileHeight * 2));
            var childHeight = child.DesiredSize.Height;
            var isVertical = childHeight > (tileHeight + TileMargin); // Вертикальная камера занимает 2 ячейки
            
            // Находим свободное место для элемента
            var placed = false;
            for (var row = 0; row < currentRow + 10 && !placed; row++)
            {
                // Убедимся что строка существует
                while (grid.Count <= row + (isVertical ? 1 : 0))
                {
                    grid.Add(new List<bool>());
                    for (var i = 0; i < columnsCount; i++)
                        grid[grid.Count - 1].Add(false);
                }
                
                for (var col = 0; col < columnsCount; col++)
                {
                    if (isVertical)
                    {
                        // Проверяем 2 ячейки по вертикали
                        if (!grid[row][col] && !grid[row + 1][col])
                        {
                            grid[row][col] = true;
                            grid[row + 1][col] = true;
                            placed = true;
                            break;
                        }
                    }
                    else
                    {
                        // Обычная камера - 1 ячейка
                        if (!grid[row][col])
                        {
                            grid[row][col] = true;
                            placed = true;
                            break;
                        }
                    }
                }
                
                if (placed && row >= currentRow)
                    currentRow = row;
            }
        }
        
        var totalHeight = (currentRow + 2) * (tileHeight + TileMargin * 2);
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tileWidth = GetTileWidth();
        var tileHeight = GetTileHeight();
        
        var columnsCount = Math.Max(1, (int)(finalSize.Width / (tileWidth + TileMargin * 2)));
        
        // Сетка для отслеживания занятых ячеек
        var grid = new List<List<bool>>();
        
        foreach (var child in Children)
        {
            var childHeight = child.DesiredSize.Height;
            var isVertical = childHeight > (tileHeight + TileMargin); // Вертикальная камера
            
            // Находим свободное место
            var placed = false;
            for (var row = 0; row < 100 && !placed; row++)
            {
                // Убедимся что строки существуют
                while (grid.Count <= row + (isVertical ? 1 : 0))
                {
                    grid.Add(new List<bool>());
                    for (var i = 0; i < columnsCount; i++)
                        grid[grid.Count - 1].Add(false);
                }
                
                for (var col = 0; col < columnsCount; col++)
                {
                    if (isVertical)
                    {
                        // Вертикальная камера - нужно 2 ячейки
                        if (!grid[row][col] && !grid[row + 1][col])
                        {
                            var x = col * (tileWidth + TileMargin * 2) + TileMargin;
                            var y = row * (tileHeight + TileMargin * 2) + TileMargin;
                            var h = tileHeight * 2 + TileMargin * 2;
                            
                            child.Arrange(new Rect(x, y, tileWidth, h));
                            
                            grid[row][col] = true;
                            grid[row + 1][col] = true;
                            placed = true;
                            break;
                        }
                    }
                    else
                    {
                        // Горизонтальная камера - 1 ячейка
                        if (!grid[row][col])
                        {
                            var x = col * (tileWidth + TileMargin * 2) + TileMargin;
                            var y = row * (tileHeight + TileMargin * 2) + TileMargin;
                            
                            child.Arrange(new Rect(x, y, tileWidth, tileHeight));
                            
                            grid[row][col] = true;
                            placed = true;
                            break;
                        }
                    }
                }
            }
        }
        
        return finalSize;
    }
}
