using Avalonia;
using Avalonia.Controls;

namespace T.Views.Components;

public partial class SessionView : UserControl
{
	public SessionView()
	{
		InitializeComponent();
	}

    protected override Size ArrangeOverride(Size finalSize)
    {
        return base.ArrangeOverride(finalSize);
    }
}