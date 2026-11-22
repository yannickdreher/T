namespace T.VT;

/// <summary>State of the shell according to its OSC 133 prompt markers.</summary>
public enum ShellPromptState
{
    /// <summary>No marker seen yet, or the last command finished without a new prompt.</summary>
    Unknown,

    /// <summary>The prompt is being printed (OSC 133 A).</summary>
    Prompt,

    /// <summary>The user edits a command line (OSC 133 B).</summary>
    Input,

    /// <summary>A command runs (OSC 133 C, or the user pressed Enter).</summary>
    Executing
}

/// <summary>
/// The command line at the shell prompt as it is on the screen.
/// </summary>
/// <param name="Text">Text from the input start up to the cursor.</param>
/// <param name="StartRow">Screen row where the input starts.</param>
/// <param name="StartColumn">Screen column where the input starts.</param>
/// <param name="CursorAtEnd">Nothing follows the cursor (it is safe to show an inline suggestion).</param>
public readonly record struct ShellCommandLine(string Text, int StartRow, int StartColumn, bool CursorAtEnd);
