namespace T.Abstractions;

public enum SshCommandOutcome
{
    Completed,
    Unavailable,
    TimedOut,
    OutputLimitExceeded
}

public sealed record SshCommandResult(
    SshCommandOutcome Outcome, int? ExitStatus = null, string Output = "", string Error = "");
