namespace T.Models;

/// <summary>
/// A keyboard-interactive prompt from the server that cannot be answered
/// automatically (e.g. a one-time password / 2FA code).
/// </summary>
public sealed class AuthPromptRequest
{
    public required string Host { get; init; }
    public string Instruction { get; init; } = "";
    public required string Prompt { get; init; }

    /// <summary>When false the answer is secret and must be masked.</summary>
    public bool IsEchoed { get; init; }

    /// <summary>Cancelled when the connection attempt ends; the prompt must then close.</summary>
    public CancellationToken CancellationToken { get; init; }
}
