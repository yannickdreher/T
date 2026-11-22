namespace T.UI.Models;

/// <summary>One part of the explorer's address bar (breadcrumb): the server root or a directory.</summary>
public sealed record PathSegment(string Name, string Path, bool IsRoot = false);
