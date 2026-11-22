using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class RemoteFile : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private bool _isSymbolicLink;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _lastModified;
    [ObservableProperty] private string _permissions = "";

    /// <summary>Marked for "cut" (Ctrl+X): shown dimmed until it is pasted somewhere else.</summary>
    [ObservableProperty] private bool _isCut;

    public string SizeDisplay => IsDirectory ? "" : FormatSize(Size);
    public string IconKey => IsDirectory ? "folder_regular" : IsSymbolicLink ? "document_link_regular" : GetDocumentIconKey(Name);

    /// <summary>The ".." entry that leads to the parent directory.</summary>
    public bool IsParentLink => Name == "..";

    /// <summary>Lower-case extension (sort key of the type column); empty for folders.</summary>
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Permissions like ls -l: "drwxr-xr-x".</summary>
    public string PermissionsDisplay => FormatPermissions(Permissions, IsDirectory ? 'd' : IsSymbolicLink ? 'l' : '-');

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnIsDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(PermissionsDisplay));
    }
    partial void OnIsSymbolicLinkChanged(bool value)
    {
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(PermissionsDisplay));
    }
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(IsParentLink));
    }
    partial void OnPermissionsChanged(string value) => OnPropertyChanged(nameof(PermissionsDisplay));

    private static string FormatPermissions(string octal, char type)
    {
        if (octal.Length != 3 || !octal.All(c => c is >= '0' and <= '7'))
            return octal;

        Span<char> text = stackalloc char[10];
        text[0] = type;
        for (int i = 0; i < 3; i++)
        {
            int bits = octal[i] - '0';
            text[1 + (i * 3)] = (bits & 4) != 0 ? 'r' : '-';
            text[2 + (i * 3)] = (bits & 2) != 0 ? 'w' : '-';
            text[3 + (i * 3)] = (bits & 1) != 0 ? 'x' : '-';
        }
        return new string(text);
    }

    private static string GetDocumentIconKey(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs" => "document_cs_regular",
            ".fs" => "document_fs_regular",
            ".vb" => "document_vb_regular",
            ".py" => "document_py_regular",
            ".rb" => "document_rb_regular",
            ".java" => "document_java_regular",
            ".js" or ".cjs" or ".mjs" => "document_js_regular",
            ".ts" or ".tsx" => "document_ts_regular",
            ".css" => "document_css_regular",
            ".scss" or ".sass" => "document_sass_regular",
            ".csv" => "document_csv_regular",
            ".xlsx" or ".xls" => "document_excel_regular",
            ".docx" or ".doc" => "document_word_regular",
            ".pptx" or ".ppt" => "document_powerpoint_regular",
            ".pdf" => "document_pdf_regular",
            ".png" or ".jpg" or ".jpeg" or ".gif"
                or ".bmp" or ".webp" or ".ico" or ".svg" => "document_image_regular",
            ".yml" or ".yaml" => "document_yml_regular",
            ".json" or ".xml" or ".html" or ".htm"
                or ".sh" or ".bash" or ".zsh" => "document_code_regular",
            ".zip" or ".tar" or ".gz" or ".bz2"
                or ".7z" or ".rar" or ".tgz" => "folder_zip_regular",
            ".pem" or ".key" or ".crt" or ".cert"
                or ".p12" or ".pfx" => "document_key_regular",
            ".txt" or ".md" or ".log" or ".rst" => "document_text_regular",
            _ => "document_regular"
        };

    public static string FormatSize(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return bytes switch
        {
            < 0 => "0 B",
            < kb => $"{bytes} B",
            < mb => $"{bytes / (double)kb:0.0} KB",
            < gb => $"{bytes / (double)mb:0.0} MB",
            _ => $"{bytes / (double)gb:0.0} GB"
        };
    }
}
