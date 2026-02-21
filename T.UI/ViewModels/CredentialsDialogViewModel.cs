using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using T.Models;

namespace T.UI.ViewModels;

public partial class CredentialsDialogViewModel : ViewModelBase
{
    private Window? _owner;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _privateKeyPath = "";
    [ObservableProperty] private string _privateKeyPassword = "";
    [ObservableProperty] private bool _saveCredentials;
    [ObservableProperty] private string _hostname = "";
    [ObservableProperty] private string? _errorMessage;

    public SshCredentials? Result { get; private set; }

    public bool HasPrivateKey => !string.IsNullOrEmpty(PrivateKeyPath);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public CredentialsDialogViewModel(
        string hostname, 
        string? existingUsername = null, 
        string? existingPrivateKeyPath = null,
        string? existingPrivateKeyPassword = null,
        string? errorMessage = null)
    {
        _hostname = hostname;
        _username = existingUsername ?? "";
        _privateKeyPath = existingPrivateKeyPath ?? "";
        _privateKeyPassword = existingPrivateKeyPassword ?? "";
        _errorMessage = errorMessage;
        
        UpdateResult();
    }

    partial void OnPrivateKeyPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPrivateKey));
        UpdateResult();
    }

    partial void OnUsernameChanged(string value) => UpdateResult();
    partial void OnPasswordChanged(string value) => UpdateResult();
    partial void OnPrivateKeyPasswordChanged(string value) => UpdateResult();
    partial void OnSaveCredentialsChanged(bool value) => UpdateResult();

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private void UpdateResult()
    {
        Result = new SshCredentials
        {
            Username = Username,
            Password = Password,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassword = PrivateKeyPassword,
            SaveCredentials = SaveCredentials
        };
    }

    public void SetOwner(Window owner) => _owner = owner;

    [RelayCommand]
    private async Task BrowsePrivateKeyAsync()
    {
        if (_owner?.StorageProvider == null)
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Select SSH Private Key",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("SSH Private Keys")
                {
                    Patterns = ["id_rsa", "id_ecdsa", "id_ed25519", "*.pem", "*.key"]
                },
                new("All Files")
                {
                    Patterns = ["*"]
                }
            ]
        };

        var result = await _owner.StorageProvider.OpenFilePickerAsync(options);
        if (result.Count > 0)
        {
            PrivateKeyPath = result[0].Path.LocalPath;
        }
    }
}