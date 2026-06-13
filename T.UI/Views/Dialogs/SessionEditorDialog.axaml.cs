using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using T.Models;

namespace T.UI.Views.Dialogs;

public partial class SessionEditorDialog : UserControl
{
    /// <summary>
    /// Represents a single entry in the jump host drop-down. <see cref="Session"/>
    /// is <see langword="null"/> for the "None (direct connection)" entry. The
    /// <see cref="ToString"/> override drives the text shown in the ComboBox.
    /// </summary>
    private sealed class ProxyOption(SshSession? session)
    {
        public SshSession? Session { get; } = session;
        public override string ToString() => Session?.Name is { Length: > 0 } name
            ? name
            : "None (direct connection)";
    }

    private readonly List<ProxyOption> _proxyOptions = [new ProxyOption(null)];

    /// <summary>
    /// Saved sessions that can be selected as the SSH jump host (ProxyJump) for
    /// the session currently being edited. Add the candidate sessions before
    /// showing the dialog; a "None" entry is always present automatically.
    /// </summary>
    public void AddProxySessions(IEnumerable<SshSession> sessions)
    {
        foreach (var s in sessions)
            _proxyOptions.Add(new ProxyOption(s));
    }

    /// <summary>
    /// The id of the session currently selected as the jump host, or
    /// <see langword="null"/> when the "None (direct connection)" entry is
    /// selected. Read this when the dialog is confirmed to persist the choice.
    /// </summary>
    public string? SelectedProxySessionId => (ProxyJumpCombo.SelectedItem as ProxyOption)?.Session?.Id;

    public SessionEditorDialog()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Populate the drop-down from code so it never depends on a XAML binding
        // to a mutated list. Pre-select the entry matching the persisted jump
        // host id, falling back to the leading "None" entry.
        ProxyJumpCombo.ItemsSource = _proxyOptions;

        var proxyJumpSessionId = (DataContext as SshSession)?.ProxyJumpSessionId;
        var index = _proxyOptions.FindIndex(o => o.Session?.Id == proxyJumpSessionId);

        ProxyJumpCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private async void OnBrowsePrivateKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshSession session) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

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

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (result.Count > 0)
        {
            session.PrivateKeyPath = result[0].Path.LocalPath;
        }
    }
}