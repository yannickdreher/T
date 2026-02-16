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

    public short ToPermissions()
    {
        short perms = 0;
        if (OwnerRead) perms |= 0x100;
        if (OwnerWrite) perms |= 0x080;
        if (OwnerExecute) perms |= 0x040;
        if (GroupRead) perms |= 0x020;
        if (GroupWrite) perms |= 0x010;
        if (GroupExecute) perms |= 0x008;
        if (OtherRead) perms |= 0x004;
        if (OtherWrite) perms |= 0x002;
        if (OtherExecute) perms |= 0x001;
        return perms;
    }

    public static PermissionDialogViewModel FromOctal(string octalPermissions)
    {
        var perms = Convert.ToInt32(octalPermissions, 8);
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