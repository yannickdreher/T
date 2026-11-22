using Avalonia.Controls;

namespace T.UI.Views.Dialogs;

public partial class OpenSshImportDialog : UserControl
{
    public OpenSshImportDialog() => InitializeComponent();

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
