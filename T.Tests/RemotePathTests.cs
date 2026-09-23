using T.Models;
using T.Services;

namespace T.Tests;

public class RemotePathTests
{
    [Theory]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    public void GetParent(string path, string expected) => Assert.Equal(expected, RemotePath.GetParent(path));

    [Theory]
    [InlineData("/a/b.txt", "b.txt")]
    [InlineData("/a/dir/", "dir")]
    [InlineData("/", "/")]
    public void GetName(string path, string expected) => Assert.Equal(expected, RemotePath.GetName(path));

    [Theory]
    [InlineData("/data", "/data", true)]
    [InlineData("/data/sub/x", "/data", true)]
    [InlineData("/database", "/data", false)]  // prefix, but not inside
    [InlineData("/other", "/data", false)]
    [InlineData("/anything", "/", true)]
    [InlineData("/data/", "/data", true)]
    public void IsSameOrInside(string path, string folder, bool expected) =>
        Assert.Equal(expected, RemotePath.IsSameOrInside(path, folder));

    [Fact]
    public void UniqueCopyName_File()
    {
        var existing = new HashSet<string> { "report.pdf" };
        Assert.Equal("report - Copy.pdf", RemotePath.UniqueCopyName("report.pdf", false, existing));

        existing.Add("report - Copy.pdf");
        Assert.Equal("report - Copy (2).pdf", RemotePath.UniqueCopyName("report.pdf", false, existing));

        existing.Add("report - Copy (2).pdf");
        Assert.Equal("report - Copy (3).pdf", RemotePath.UniqueCopyName("report.pdf", false, existing));
    }

    [Fact]
    public void UniqueCopyName_FolderAndDotFileKeepWholeName()
    {
        Assert.Equal("v1.2 - Copy", RemotePath.UniqueCopyName("v1.2", true, new HashSet<string>()));
        Assert.Equal(".bashrc - Copy", RemotePath.UniqueCopyName(".bashrc", false, new HashSet<string>()));
        Assert.Equal("archive.tar - Copy.gz", RemotePath.UniqueCopyName("archive.tar.gz", false, new HashSet<string>()));
    }

    [Theory]
    [InlineData("/home/user/file.txt", "'/home/user/file.txt'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf ~)", "'$(rm -rf ~)'")]
    [InlineData("a b;c|d`e`", "'a b;c|d`e`'")]
    [InlineData("-rf", "'-rf'")]
    public void ShellQuote_LeavesNothingForTheShellToInterpret(string value, string expected) =>
        Assert.Equal(expected, SshService.ShellQuote(value));
}
