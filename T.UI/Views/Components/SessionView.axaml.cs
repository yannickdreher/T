using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class SessionView : UserControl
{
    private readonly IServiceProvider? _serviceProvider;
    private ContentControl? _terminalHost;
    private TerminalView? _terminalView;

    public SessionView()
    {
        InitializeComponent();
        _terminalHost = this.FindControl<ContentControl>("TerminalHost");
    }

    public SessionView(IServiceProvider serviceProvider) : this()
    {
        _serviceProvider = serviceProvider;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_terminalHost is null || _serviceProvider is null) return;

        if (DataContext is SessionViewModel vm)
        {
            _terminalView = ActivatorUtilities.CreateInstance<TerminalView>(_serviceProvider, vm);
            _terminalHost.Content = _terminalView;
        }
        else
        {
            _terminalHost.Content = null;
            _terminalView = null;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return base.ArrangeOverride(finalSize);
    }
}