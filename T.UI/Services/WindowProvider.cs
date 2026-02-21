using Avalonia.Controls;
using T.UI.Abstractions;

namespace T.UI.Services;

public sealed class WindowProvider : IWindowProvider
{
    public Window? MainWindow { get; set; }
}