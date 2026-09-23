using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class TerminalStatsOverlay : UserControl
{
    // ViewModel property: bind this from parent (or set DataContext manually)
    public static readonly StyledProperty<TerminalStatsViewModel?> ModelProperty =
        AvaloniaProperty.Register<TerminalStatsOverlay, TerminalStatsViewModel?>(nameof(Model));

    static TerminalStatsOverlay()
    {
        ModelProperty.Changed.AddClassHandler<TerminalStatsOverlay>((ctrl, e) =>
        {
            ctrl.DataContext = e.NewValue as TerminalStatsViewModel;
        });
    }

    public TerminalStatsOverlay()
    {
        InitializeComponent();

        // Until Model is set, do not inherit the session view model (the bindings expect TerminalStatsViewModel).
        DataContext = null;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public TerminalStatsViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }
}
