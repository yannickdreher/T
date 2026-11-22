using Avalonia.Controls;

namespace T.UI.Views.Dialogs;

public partial class TunnelEditorDialog : UserControl
{
    public TunnelEditorDialog() => InitializeComponent();

    /// <summary>Shows the validation error above the fields (null hides it).</summary>
    public string? ValidationError
    {
        set
        {
            ValidationInfo.Message = value;
            ValidationInfo.IsOpen = value != null;
            ValidationInfo.IsVisible = value != null;
        }
    }
}
