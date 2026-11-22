using CommunityToolkit.Mvvm.ComponentModel;
using T.Services;

namespace T.UI.ViewModels;

/// <summary>One row of the command suggestion popup.</summary>
public partial class CommandSuggestionItem : ObservableObject
{
    public CommandSuggestionItem(CommandSuggestion suggestion, string typed)
    {
        Suggestion = suggestion;
        int typedLength = suggestion.Text.StartsWith(typed, System.StringComparison.Ordinal) ? typed.Length : 0;
        Typed = suggestion.Text[..typedLength];
        Rest = suggestion.Text[typedLength..];
    }

    public CommandSuggestion Suggestion { get; }

    /// <summary>The part the user already typed (highlighted).</summary>
    public string Typed { get; }

    /// <summary>The part accepting the suggestion types.</summary>
    public string Rest { get; }

    public bool IsHistory => Suggestion.Kind == CommandSuggestionKind.History;
    public bool IsCommand => Suggestion.Kind == CommandSuggestionKind.Command;
    public string KindLabel => IsHistory ? "history" : "command";

    [ObservableProperty] private bool _isSelected;
}
