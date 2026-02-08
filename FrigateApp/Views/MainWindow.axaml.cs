using Avalonia.Controls;
using Avalonia.Input;

namespace FrigateApp.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            // Обработчик клавиш для полноэкранного режима
            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // F12 - переключение полноэкранного режима
            if (e.Key == Key.F12)
            {
                WindowState = WindowState == WindowState.FullScreen 
                    ? WindowState.Normal 
                    : WindowState.FullScreen;
                e.Handled = true;
            }
        }
    }
}