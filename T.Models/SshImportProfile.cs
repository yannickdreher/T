namespace T.Models;

/// <summary>A session and the jump hosts needed to reach it, ordered from the client to the target.</summary>
public sealed record SshImportProfile(SshSession Session, IReadOnlyList<SshSession> JumpHosts);
