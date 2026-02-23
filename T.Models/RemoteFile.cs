using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class RemoteFile : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _lastModified;
    [ObservableProperty] private string _permissions = "";
    [ObservableProperty] private string _owner = "";
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private bool _isSelected;

    public string SizeDisplay => IsDirectory ? "-" : FormatSize(Size);
    public string IconKey => IsDirectory ? "folder_regular" : GetDocumentIconKey(Name);
    public string IconColor => IsDirectory ? "#FFC107" : "#9E9E9E";

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnIsDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(IconColor));
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IconKey));

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

    private static string FormatSize(long bytes)
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