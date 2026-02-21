using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using T.UI.ViewModels;

namespace T.UI;

[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        var vmType = data.GetType();
        var vmName = vmType.FullName!;
        var viewName = vmName.Replace("ViewModels", "Views", StringComparison.Ordinal)
                             .Replace("ViewModel", "View", StringComparison.Ordinal);

        var assembly = vmType.Assembly;

        var candidates = new[]
        {
            viewName,
            viewName.Replace(".Views.", ".Views.Components.", StringComparison.Ordinal),
            viewName.Replace(".Views.", ".Views.Dialogs.", StringComparison.Ordinal)
        };

        var viewType = candidates
            .Select(assembly.GetType)
            .FirstOrDefault(t => t != null);

        if (viewType is not null)
        {
            var services = ((App)Avalonia.Application.Current!).Services;
            var view = (Control?)services.GetService(viewType) ?? (Control)Activator.CreateInstance(viewType)!;
            view.DataContext = data;
            return view;
        }

        return new TextBlock { Text = "Not Found: " + viewName };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
