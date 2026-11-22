using System;
using System.Globalization;
using Avalonia.Data.Converters;
using T.Models;
using T.UI.Models;
using T.UI.Services;

namespace T.UI.Converters;

/// <summary>
/// Colored icon of a remote file or folder (<see cref="RemoteFile"/>, <see cref="DirectoryNode"/>,
/// <see cref="FolderTreeNode"/>). The parameter is the displayed size in DIPs (default 16).
/// </summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double size = parameter switch
        {
            double d => d,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 16
        };

        return value switch
        {
            RemoteFile file => FileIcons.Get(file.Name, file.IsDirectory || file.Name == "..", file.IsSymbolicLink, size, file.IconKey),
            DirectoryNode or FolderTreeNode => FileIcons.GetFolder(size),
            _ => null
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Type column text of a <see cref="RemoteFile"/> ("File folder", "Text Document", ...).</summary>
public sealed class FileTypeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RemoteFile file && file.Name != ".."
            ? file.IsSymbolicLink && !file.IsDirectory ? "Symbolic link" : FileIcons.GetTypeName(file.Name, file.IsDirectory)
            : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
