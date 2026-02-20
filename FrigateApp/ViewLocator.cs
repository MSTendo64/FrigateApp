using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using FrigateApp.ViewModels;
using FrigateApp.Views;

namespace FrigateApp
{
    /// <summary>
    /// Given a view model, returns the corresponding view if possible.
    /// </summary>
    [RequiresUnreferencedCode(
        "Default implementation of ViewLocator involves reflection which may be trimmed away.",
        Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
    public class ViewLocator : IDataTemplate
    {
        public Control? Build(object? param)
        {
            if (param is null)
                return null;

            // Специальная обработка для OptimizedCamerasViewModel
            if (param is OptimizedCamerasViewModel)
            {
                return new OptimizedCamerasView();
            }
            
            // Специальная обработка для ProfessionalCameraPlayerViewModel
            if (param is ProfessionalCameraPlayerViewModel)
            {
                return new ProfessionalCameraPlayerView();
            }
            
            // Специальная обработка для LowLatencyCameraPlayerViewModel
            if (param is LowLatencyCameraPlayerViewModel)
            {
                return new LowLatencyCameraPlayerView();
            }

            var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            var type = Type.GetType(name);

            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }

            return new TextBlock { Text = "Not Found: " + name };
        }

        public bool Match(object? data)
        {
            return data is ViewModelBase;
        }
    }
}
