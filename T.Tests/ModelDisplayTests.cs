using T.Models;

namespace T.Tests;

public class ModelDisplayTests
{
    [Theory]
    [InlineData("755", true, false, "drwxr-xr-x")]
    [InlineData("644", false, false, "-rw-r--r--")]
    [InlineData("777", false, true, "lrwxrwxrwx")]
    [InlineData("600", false, false, "-rw-------")]
    [InlineData("", false, false, "")]
    [InlineData("rwx", false, false, "rwx")]
    public void RemoteFile_PermissionsDisplay_IsLsStyle(string octal, bool isDirectory, bool isLink, string expected)
    {
        var file = new RemoteFile { Permissions = octal, IsDirectory = isDirectory, IsSymbolicLink = isLink };
        Assert.Equal(expected, file.PermissionsDisplay);
    }

    [Fact]
    public void RemoteFile_Directory_HasNoSizeOrExtension()
    {
        var folder = new RemoteFile { Name = "archive.d", IsDirectory = true, Size = 4096 };
        Assert.Equal("", folder.SizeDisplay);
        Assert.Equal("", folder.Extension);
    }

    [Fact]
    public void RemoteFile_Extension_IsLowerCase()
    {
        Assert.Equal(".json", new RemoteFile { Name = "Package.JSON" }.Extension);
        Assert.Equal("", new RemoteFile { Name = "Makefile" }.Extension);
    }

    [Fact]
    public void RemoteFile_ParentLink()
    {
        Assert.True(new RemoteFile { Name = "..", IsDirectory = true }.IsParentLink);
        Assert.False(new RemoteFile { Name = "..hidden" }.IsParentLink);
    }

    [Theory]
    [InlineData("root", "example.com", 22, "root@example.com")]
    [InlineData("deploy", "10.0.0.5", 2222, "deploy@10.0.0.5:2222")]
    [InlineData("", "host", 22, "host")]
    public void SshSession_Address(string user, string host, int port, string expected)
    {
        var session = new SshSession { Username = user, Host = host, Port = port };
        Assert.Equal(expected, session.Address);
    }
}
