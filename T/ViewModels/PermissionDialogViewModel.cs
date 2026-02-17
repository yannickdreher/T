using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace T.ViewModels;

public partial class PermissionDialogViewModel : ViewModelBase
{
    [ObservableProperty] private bool _ownerRead;
    [ObservableProperty] private bool _ownerWrite;
    [ObservableProperty] private bool _ownerExecute;
    [ObservableProperty] private bool _groupRead;
    [ObservableProperty] private bool _groupWrite;
    [ObservableProperty] private bool _groupExecute;
    [ObservableProperty] private bool _otherRead;
    [ObservableProperty] private bool _otherWrite;
    [ObservableProperty] private bool _otherExecute;

    public short ToOctal()
    {
        int owner = (OwnerRead ? 4 : 0) + (OwnerWrite ? 2 : 0) + (OwnerExecute ? 1 : 0);
        int group = (GroupRead ? 4 : 0) + (GroupWrite ? 2 : 0) + (GroupExecute ? 1 : 0);
        int other = (OtherRead ? 4 : 0) + (OtherWrite ? 2 : 0) + (OtherExecute ? 1 : 0);
        
        return (short)(owner * 100 + group * 10 + other);
    }

    public static PermissionDialogViewModel FromOctal(string permissions)
    {
        var perms = Convert.ToInt32(permissions, 8);
        return new PermissionDialogViewModel
        {
            OwnerRead = (perms & 0x100) != 0,
            OwnerWrite = (perms & 0x080) != 0,
            OwnerExecute = (perms & 0x040) != 0,
            GroupRead = (perms & 0x020) != 0,
            GroupWrite = (perms & 0x010) != 0,
            GroupExecute = (perms & 0x008) != 0,
            OtherRead = (perms & 0x004) != 0,
            OtherWrite = (perms & 0x002) != 0,
            OtherExecute = (perms & 0x001) != 0,
        };
    }
}