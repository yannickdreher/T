using T.Models;
using T.Services;

namespace T.Tests;

public class OpenSshConfigImportTests
{
    [Fact]
    public void Parse_AppliesFirstValueAndNegatedPatterns()
    {
        using var temp = new TempDirectory();
        var key = temp.File("identity file #one");
        File.WriteAllText(key, "");
        var entries = Parse($"""
            User = global
            hOsT dev-* !dev-out
              Port=2200
              IdentityFile "~/identity file #one" # ignored
            Host dev-in dev-out
              HostName "{IPV6}"
              User ignored
              Port 2201
              IdentityFile ~/second
            Host *
              Port 2222
            """, temp.Path);

        var inside = entries.Single(entry => entry.Alias == "dev-in");
        Assert.Equal("global", inside.Profile.Session.Username);
        Assert.Equal(2200, inside.Profile.Session.Port);
        Assert.Equal(IPV6, inside.Profile.Session.Host);
        Assert.Equal(key, inside.Profile.Session.PrivateKeyPath);
        Assert.Contains(inside.Warnings, warning => warning.Contains("only the first"));
        Assert.Equal(2201, entries.Single(entry => entry.Alias == "dev-out").Profile.Session.Port);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Parse_UsesAliasAndLocalUserWhenDefaultsAreAbsent()
    {
        var entry = Assert.Single(Parse("Host example"));
        Assert.Equal("example", entry.Profile.Session.Host);
        Assert.Equal("local-user", entry.Profile.Session.Username);
        Assert.Equal(22, entry.Profile.Session.Port);
        Assert.True(entry.DefaultHost);
        Assert.True(entry.DefaultUser);
        Assert.True(entry.DefaultPort);
    }

    [Fact]
    public void Parse_ExplicitJumpListPreservesOrderAndOverridesHopSettings()
    {
        var entry = Find(Parse("""
            Host target
                ProxyJump alternate@bastion:2224,user@[2001:db8::1]:2200
            Host bastion
                HostName bastion.example
                User default-bastion
                Port 2201
            """), "target");

        Assert.Null(entry.RouteError);
        Assert.Collection(entry.Profile.JumpHosts,
            hop =>
            {
                Assert.Equal("bastion.example", hop.Host);
                Assert.Equal("alternate", hop.Username);
                Assert.Equal(2224, hop.Port);
            },
            hop =>
            {
                Assert.Equal(IPV6, hop.Host);
                Assert.Equal("user", hop.Username);
                Assert.Equal(2200, hop.Port);
            });
    }

    [Fact]
    public void Parse_PropagatesNestedHopsAndRejectsCyclesAndConflicts()
    {
        var entries = Parse("""
            Host target
                ProxyJump inner
            Host inner
                ProxyJump outer
            Host outer
                HostName outer.example
            Host conflicting
                ProxyJump other,inner
            Host cycle1
                ProxyJump cycle2
            Host cycle2
                ProxyJump cycle1
            """);

        Assert.Equal(["outer.example", "inner"], Find(entries, "target").Profile.JumpHosts.Select(hop => hop.Host));
        Assert.Contains("conflicts", Find(entries, "conflicting").RouteError);
        Assert.Contains("loop", Find(entries, "cycle1").RouteError);
    }

    [Fact]
    public void Parse_UsesFirstProxyDirectiveAndNeverExecutesProxyCommand()
    {
        var entries = Parse("""
            Host direct
                ProxyJump none
                ProxyCommand dangerous
            Host blocked
                ProxyCommand dangerous
                ProxyJump jump
            Host routed
                ProxyJump jump
                ProxyCommand dangerous
            Host no-proxy
                ProxyCommand none
                ProxyJump jump
            """);

        Assert.Empty(Find(entries, "direct").Profile.JumpHosts);
        Assert.Null(Find(entries, "direct").RouteError);
        Assert.Contains("ProxyCommand", Find(entries, "blocked").RouteError);
        Assert.Equal("jump", Assert.Single(Find(entries, "routed").Profile.JumpHosts).Host);
        Assert.Empty(Find(entries, "no-proxy").Profile.JumpHosts);
    }

    [Fact]
    public void Parse_RequiresReviewForUnsupportedRouteInputsAndTrustSettings()
    {
        var entries = Parse("""
            Include local.conf
            Match exec "unsafe"
                User not-imported
            Host example
                Port invalid
                IdentityFile relative/key
                UserKnownHostsFile /tmp/other
            Host tokens
                HostName %h.example
            """);
        var example = Find(entries, "example");
        Assert.True(example.RequiresReview);
        Assert.Equal("local-user", example.Profile.Session.Username);
        Assert.Equal(0, example.Profile.Session.Port);
        Assert.Contains(example.Warnings, warning => warning.Contains("Line 1: Include"));
        Assert.Contains(example.Warnings, warning => warning.Contains("UserKnownHostsFile"));
        Assert.Contains(example.Warnings, warning => warning.Contains("absolute local path"));
        Assert.True(Find(entries, "tokens").RequiresReview);
        Assert.Empty(Find(entries, "tokens").Profile.Session.Host);
    }

    [Fact]
    public void Parse_UsesSameMaximumJumpDepthAsSshConnection()
    {
        var file = string.Join('\n', Enumerable.Range(1, SshSession.MaxJumpHops + 1)
            .Select(index => $"Host host{index}\n    ProxyJump host{index + 1}"));
        var entries = Parse(file);
        Assert.Equal(SshSession.MaxJumpHops, Find(entries, "host2").Profile.JumpHosts.Count);
        Assert.Contains($"exceeds {SshSession.MaxJumpHops}", Find(entries, "host1").RouteError);
    }

    private const string IPV6 = "2001:db8::1";

    private static IReadOnlyList<OpenSshConfigImport.Entry> Parse(string text, string? home = null) =>
        OpenSshConfigImport.Parse(text, home ?? Path.GetTempPath(), "local-user");

    private static OpenSshConfigImport.Entry Find(IReadOnlyList<OpenSshConfigImport.Entry> entries, string alias) =>
        entries.Single(entry => entry.Alias == alias);
}
