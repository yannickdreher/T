using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using T.ViewModels;

namespace T.Views.Components
{
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
}