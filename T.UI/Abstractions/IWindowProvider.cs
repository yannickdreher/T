using Avalonia.Controls;

namespace T.UI.Abstractions;

public interface IWindowProvider
{
    Window? MainWindow { get; set; }
}