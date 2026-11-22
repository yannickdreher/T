using System.Globalization;
using System.Net;
using System.Text;
using T.Models;

namespace T.Services;

/// <summary>Reads the supported subset of ssh_config without invoking OpenSSH or loading other files.</summary>
public static class OpenSshConfigImport
{
    private const int MaxLines = 10_000;
    private const int MaxAliases = 500;
    private const int MaxDisplayedWarnings = 40;

    public sealed record Entry(
        string Alias,
        SshImportProfile Profile,
        IReadOnlyList<string> Warnings,
        bool RequiresReview,
        string? RouteError,
        bool DefaultHost,
        bool DefaultUser,
        bool DefaultPort);

    public static IReadOnlyList<Entry> Parse(string snapshot, string homeDirectory, string defaultUser)
    {
        var blocks = new List<Block> { new([], []) };
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileWarnings = new List<string>();
        var inMatch = false;

        using var reader = new StringReader(snapshot);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++lineNumber > MaxLines)
                throw new FormatException($"The SSH configuration exceeds {MaxLines} lines.");

            if (ReadDirective(line, lineNumber) is not { } directive)
                continue;

            switch (directive.Name)
            {
                case "host":
                    if (directive.Values.Length == 0)
                        throw new FormatException($"Line {lineNumber}: Host requires at least one pattern.");

                    inMatch = false;
                    blocks.Add(new(directive.Values, []));
                    foreach (var pattern in directive.Values.Where(IsConcreteHost))
                        aliases.Add(pattern);
                    if (aliases.Count > MaxAliases)
                        throw new FormatException($"The SSH configuration contains more than {MaxAliases} host aliases.");
                    break;
                case "match":
                case "include":
                    fileWarnings.Add($"Line {lineNumber}: {directive.DisplayName} was not evaluated. Review the connection settings.");
                    inMatch |= directive.Name == "match";
                    break;
                default:
                    if (!inMatch)
                        blocks[^1].Directives.Add(directive);
                    break;
            }
        }

        var endpoints = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        Endpoint GetEndpoint(string alias)
        {
            if (!endpoints.TryGetValue(alias, out var endpoint))
            {
                endpoint = ResolveEndpoint(alias, blocks, homeDirectory, defaultUser);
                endpoints.Add(alias, endpoint);
            }
            return endpoint;
        }

        var entries = new List<Entry>(aliases.Count);
        foreach (var alias in aliases)
        {
            var endpoint = GetEndpoint(alias);
            var warnings = new List<string>(fileWarnings);
            warnings.AddRange(endpoint.Warnings);
            var requiresReview = fileWarnings.Count > 0 || endpoint.RequiresReview;
            string? routeError = null;
            IReadOnlyList<SshSession> hops = [];

            try
            {
                var route = ResolveRoute(alias, GetEndpoint, new(StringComparer.OrdinalIgnoreCase));
                foreach (var hop in route)
                {
                    warnings.AddRange(hop.Endpoint.Warnings.Select(warning => $"{hop.Alias}: {warning}"));
                    requiresReview |= hop.Endpoint.RequiresReview;
                    if (GetConnectionError(hop.Session) is { } error)
                        warnings.Add($"Jump host '{hop.Alias}': {error}");
                }
                hops = route.Select(hop => hop.Session).ToArray();
            }
            catch (FormatException ex)
            {
                routeError = ex.Message;
            }

            var displayedWarnings = warnings.Distinct().Take(MaxDisplayedWarnings + 1).ToArray();
            if (displayedWarnings.Length > MaxDisplayedWarnings)
                displayedWarnings[^1] = "Additional warnings omitted.";
            entries.Add(new(alias, new(endpoint.Session, hops), displayedWarnings, requiresReview,
                routeError, endpoint.DefaultHost, endpoint.DefaultUser, endpoint.DefaultPort));
        }

        return entries;
    }

    public static string? GetConnectionError(SshSession session)
    {
        var host = session.Host.Trim();
        if (!IsConcreteHost(host))
            return "Enter a host name or IP address without spaces, a user name or URL.";
        if (session.Port is < 1 or > 65535)
            return "The port must be between 1 and 65535.";

        var user = session.Username.Trim();
        if (user.Length == 0 || user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '@'))
            return "Enter a valid SSH user name.";

        var path = session.PrivateKeyPath.Trim();
        if (path.Length > 0 && !IsLocalKeyPath(path))
            return "Enter an absolute local identity file path or leave it empty.";

        return null;
    }

    private static Endpoint ResolveEndpoint(string alias, IReadOnlyList<Block> blocks, string homeDirectory, string defaultUser)
    {
        var selected = new Dictionary<string, Directive>(StringComparer.Ordinal);
        var warnings = new List<string>();
        bool requiresReview = false;
        Directive? proxy = null;
        foreach (var block in blocks)
        {
            if (!MatchesHost(block.Patterns, alias))
                continue;

            foreach (var directive in block.Directives)
            {
                switch (directive.Name)
                {
                    case "hostname":
                    case "user":
                    case "port":
                        selected.TryAdd(directive.Name, directive);
                        break;
                    case "identityfile":
                        if (!selected.TryAdd(directive.Name, directive))
                            warnings.Add(LineMessage(directive, "only the first IdentityFile is imported."));
                        break;
                    case "proxycommand":
                    case "proxyjump":
                        proxy ??= directive;
                        break;
                    default:
                        warnings.Add(LineMessage(directive, "not imported."));
                        requiresReview |= directive.Name is "canonicalizehostname" or "hostkeyalias" or
                            "userknownhostsfile" or "globalknownhostsfile" or "knownhostscommand" or
                            "stricthostkeychecking" or "checkhostip" or "revokedhostkeys" or
                            "identityagent" or "identitiesonly" or "certificatefile" or "pkcs11provider" or
                            "hostkeyalgorithms" or "verifyhostkeydns" or "updatehostkeys";
                        break;
                }
            }
        }

        string? Value(string directiveName)
        {
            if (!selected.TryGetValue(directiveName, out var directive))
                return null;
            if (directive.Values.Length == 1 && !HasUnsupportedToken(directive.Values[0]))
                return directive.Values[0];

            requiresReview = true;
            warnings.Add(LineMessage(directive, "value requires manual correction."));
            return "";
        }

        var hostName = Value("hostname");
        var user = Value("user");
        var portText = Value("port");
        var path = Value("identityfile") ?? "";
        if (path.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            path = "";
            warnings.Add("IdentityFile none does not disable T's configured default identities.");
            requiresReview = true;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(homeDirectory, path[2..]);
        }

        if (path.Length > 0 && !IsLocalKeyPath(path))
            warnings.Add("IdentityFile requires an absolute local path.");
        else if (path.Length > 0 && !File.Exists(path))
            warnings.Add("The identity file could not be found.");

        var session = new SshSession
        {
            Name = alias,
            Host = NormalizeHost(hostName ?? alias),
            Username = user ?? defaultUser,
            Port = portText is null ? 22 : int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
            PrivateKeyPath = path
        };

        return new(session, proxy, warnings, requiresReview, hostName is null, user is null, portText is null);
    }

    private static List<ResolvedHop> ResolveRoute(
        string alias, Func<string, Endpoint> getEndpoint, HashSet<string> ancestors)
    {
        if (!ancestors.Add(alias))
            throw new FormatException($"The ProxyJump route contains a loop at '{alias}'.");
        if (ancestors.Count > SshSession.MaxJumpHops + 1)
            throw new FormatException($"The ProxyJump route exceeds {SshSession.MaxJumpHops} jump hosts.");

        try
        {
            var proxy = getEndpoint(alias).Proxy;
            if (proxy is null || proxy.Values is [var value] && value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return [];
            if (proxy.Name == "proxycommand")
                throw new FormatException(LineMessage(proxy, "an active ProxyCommand cannot be imported."));
            if (proxy.Values.Length != 1 || HasUnsupportedToken(proxy.Values[0]))
                throw new FormatException(LineMessage(proxy, "the ProxyJump value cannot be resolved."));

            var hops = proxy.Values[0].Split(',');
            if (hops.Length > SshSession.MaxJumpHops)
                throw new FormatException($"The ProxyJump route exceeds {SshSession.MaxJumpHops} jump hosts.");

            var result = new List<ResolvedHop>();
            foreach (var hopText in hops)
            {
                var (host, user, port) = ParseJump(hopText.Trim(), proxy);
                var endpoint = getEndpoint(host);
                var nested = ResolveRoute(host, getEndpoint, ancestors);
                if (hops.Length == 1)
                    result.AddRange(nested);
                else if (nested.Count > 0 && !SameRoute(result, nested))
                    throw new FormatException(LineMessage(proxy, $"the route for jump host '{host}' conflicts with the ordered jump list."));

                var session = endpoint.Session.Clone();
                if (user != null) session.Username = user;
                if (port.HasValue) session.Port = port.Value;

                if (result.Any(hop => hop.Alias.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException(LineMessage(proxy, $"the jump host '{host}' occurs twice in the same route."));
                result.Add(new(host, session, endpoint));
            }

            if (result.Count > SshSession.MaxJumpHops)
                throw new FormatException($"The ProxyJump route exceeds {SshSession.MaxJumpHops} jump hosts.");
            return result;
        }
        finally
        {
            ancestors.Remove(alias);
        }
    }

    private static bool SameRoute(List<ResolvedHop> first, List<ResolvedHop> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(pair => pair.First.Session.Host.Equals(pair.Second.Session.Host, StringComparison.OrdinalIgnoreCase)
            && pair.First.Session.Username == pair.Second.Session.Username
            && pair.First.Session.Port == pair.Second.Session.Port
            && pair.First.Session.PrivateKeyPath == pair.Second.Session.PrivateKeyPath);

    private static (string Host, string? User, int? Port) ParseJump(string text, Directive directive)
    {
        if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            text = text[6..];

        // Like OpenSSH: the user part ends at the last '@' (user names may contain '@').
        int userEnd = text.LastIndexOf('@');
        var user = userEnd >= 0 ? text[..userEnd] : null;
        var address = text[(userEnd + 1)..];
        string host = address;
        string? port = null;
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']');
            if (close <= 1 || close + 1 < address.Length && address[close + 1] != ':')
                throw new FormatException(LineMessage(directive, "invalid bracketed jump host."));
            host = address[1..close];
            if (close + 1 < address.Length) port = address[(close + 2)..];
        }
        else if (address.Count(c => c == ':') == 1)
        {
            int split = address.IndexOf(':');
            host = address[..split];
            port = address[(split + 1)..];
        }

        if (!IsConcreteHost(host) || user is { Length: 0 } || user?.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) == true)
            throw new FormatException(LineMessage(directive, "invalid jump host or user name."));

        if (port is null)
            return (host, user, null);
        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 65535)
            throw new FormatException(LineMessage(directive, "invalid jump host port."));

        return (host, user, number);
    }

    private static Directive? ReadDirective(string line, int number)
    {
        var span = line.AsSpan().TrimStart();
        if (span.IsEmpty || span[0] == '#') return null;

        int split = 0;
        while (split < span.Length && span[split] != '=' && !char.IsWhiteSpace(span[split]))
            split++;

        if (split == 0) throw new FormatException($"Line {number}: missing SSH directive.");
        var name = span[..split].ToString();
        span = span[split..].TrimStart();
        if (!span.IsEmpty && span[0] == '=') span = span[1..].TrimStart();

        var words = new List<string>();
        var value = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < span.Length; index++)
        {
            char current = span[index];
            if (current == '\\' && index + 1 < span.Length &&
                (span[index + 1] is '\\' or '"' or '\'' or '#' || char.IsWhiteSpace(span[index + 1])))
            {
                value.Append(span[++index]);
                started = true;
            }
            else if (current == quote && quote != '\0')
            {
                quote = '\0';
            }
            else if (quote == '\0' && current is '"' or '\'')
            {
                quote = current;
                started = true;
            }
            else if (quote == '\0' && current == '#')
            {
                break;
            }
            else if (quote == '\0' && char.IsWhiteSpace(current))
            {
                if (!started) continue;
                words.Add(value.ToString());
                value.Clear();
                started = false;
            }
            else
            {
                value.Append(current);
                started = true;
            }
        }

        if (quote != '\0') throw new FormatException($"Line {number}: unterminated quoted value.");
        if (started) words.Add(value.ToString());
        return new(name.ToLowerInvariant(), name, [.. words], number);
    }

    private static bool MatchesHost(string[] patterns, string alias)
    {
        if (patterns.Length == 0) return true;
        bool matched = false;
        foreach (var pattern in patterns)
        {
            bool negated = pattern.StartsWith('!');
            if (!MatchPattern(negated ? pattern.AsSpan(1) : pattern.AsSpan(), alias.AsSpan()))
                continue;
            if (negated) return false;
            matched = true;
        }
        return matched;
    }

    private static bool MatchPattern(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        int p = 0, v = 0, star = -1, restart = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                restart = v;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++restart;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static string NormalizeHost(string host) =>
        host.StartsWith('[') && host.EndsWith(']') && IPAddress.TryParse(host[1..^1], out _) ? host[1..^1] : host;

    private static bool IsConcreteHost(string host)
    {
        if (host.Length == 0 || host.StartsWith('!') ||
            host.Any(c => IsUnsafeHostCharacter(c) || c is '*' or '?'))
            return false;

        var normalized = NormalizeHost(host);
        return !normalized.Contains('[') && !normalized.Contains(']') &&
            (!normalized.Contains(':') || IPAddress.TryParse(normalized, out _));
    }

    private static bool IsUnsafeHostCharacter(char c) => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '%' or ',';

    private static bool IsLocalKeyPath(string path) =>
        Path.IsPathFullyQualified(path) && !path.Any(char.IsControl) &&
        !path.StartsWith("//", StringComparison.Ordinal) && StepProfileValidation.IsLocalPath(path);

    private static bool HasUnsupportedToken(string value) => value.Contains('%') || value.Contains("${", StringComparison.Ordinal);

    private static string LineMessage(Directive directive, string message) =>
        $"Line {directive.Line}: {directive.DisplayName}: {message}";

    private sealed record Block(string[] Patterns, List<Directive> Directives);
    private sealed record Directive(string Name, string DisplayName, string[] Values, int Line);
    private sealed record Endpoint(SshSession Session, Directive? Proxy, List<string> Warnings, bool RequiresReview,
        bool DefaultHost, bool DefaultUser, bool DefaultPort);
    private sealed record ResolvedHop(string Alias, SshSession Session, Endpoint Endpoint);
}
