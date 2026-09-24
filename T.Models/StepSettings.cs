using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class StepSettings : ObservableObject
{
    /// <summary>Absolute path of the step CLI; empty searches the absolute directories in PATH.</summary>
    [ObservableProperty] private string _executablePath = "";

    [ObservableProperty] private List<StepCaProfile> _profiles = [];
}
