using System.Text;
using Renci.SshNet.Common;
using T.Abstractions;

namespace T.Services;

public sealed partial class SshService
{
    public Task<SshCommandResult> RunBoundedCommandAsync(
        string commandText, int maxOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var connection = _connection;

        // SSH.NET opens the exec channel synchronously before returning its execution task.
        return Task.Run(ExecuteAsync, cancellationToken);

        async Task<SshCommandResult> ExecuteAsync()
        {
            if (connection?.Ssh is not { IsConnected: true } ssh || !ReferenceEquals(connection, _connection))
                return new(SshCommandOutcome.Unavailable);

            using var deadline = new CancellationTokenSource(timeout);
            using var limit = new CancellationTokenSource();
            using var commandToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, connection.Token, deadline.Token, limit.Token);
            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            int remaining = maxOutputBytes;

            try
            {
                using var command = ssh.CreateCommand(commandText);
                var execution = command.ExecuteAsync(commandToken.Token);
                var readOutput = DrainAsync(command.OutputStream, stdout);
                var readError = DrainAsync(command.ExtendedOutputStream, stderr);
                await Task.WhenAll(execution, readOutput, readError).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (!ReferenceEquals(connection, _connection))
                    return new(SshCommandOutcome.Unavailable);
                if (limit.IsCancellationRequested)
                    return Result(SshCommandOutcome.OutputLimitExceeded);
                return Result(SshCommandOutcome.Completed, command.ExitStatus);
            }
            catch (Exception ex) when (ex is SshException or IOException or InvalidOperationException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (limit.IsCancellationRequested)
                    return Result(SshCommandOutcome.OutputLimitExceeded);
                if (deadline.IsCancellationRequested || ex is SshOperationTimeoutException)
                    return Result(SshCommandOutcome.TimedOut);
                return new(SshCommandOutcome.Unavailable);
            }

            async Task DrainAsync(Stream source, MemoryStream destination)
            {
                var buffer = new byte[8192];
                while (true)
                {
                    int count = await source.ReadAsync(buffer, commandToken.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    int before = Interlocked.Add(ref remaining, -count) + count;
                    int allowed = Math.Clamp(before, 0, count);
                    destination.Write(buffer, 0, allowed);
                    if (allowed == count) continue;

                    // Stop both readers and ask SSH.NET to close the bounded operation.
                    await limit.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }

            SshCommandResult Result(SshCommandOutcome outcome, int? exitStatus = null) =>
                new(outcome, exitStatus,
                    Encoding.UTF8.GetString(stdout.GetBuffer(), 0, (int)stdout.Length),
                    Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length));
        }
    }
}
