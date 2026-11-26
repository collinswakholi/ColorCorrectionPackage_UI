using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class UserFoldersTests
{
    private static readonly string Absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ccp-user-folders"));

    [Fact]
    public void UsesTheStandardFolderWhenThePlatformReportsOne()
    {
        Assert.Equal(Absolute, UserFolders.LocalData(Absolute, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("relative")]
    public void NeverReturnsARelativePathWhenTheStandardFolderIsMissing(string? standard)
    {
        // A fresh Linux account has no ~/.local/share, and GetFolderPath then returned "",
        // so 0.5.0 created ./CCP-New in the working directory (and aborted at "/").
        var xdg = Path.Combine(Absolute, "xdg");
        Assert.Equal(xdg, UserFolders.LocalData(standard, xdg, Absolute));
        Assert.Equal(Path.Combine(Absolute, ".local", "share"), UserFolders.LocalData(standard, "relative-xdg", Absolute));
        Assert.Equal(Path.Combine(Absolute, ".local", "share"), UserFolders.LocalData(standard, null, Absolute));
    }

    [Fact]
    public void FailsClearlyWithoutAnyHomeInsteadOfUsingTheWorkingDirectory()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserFolders.LocalData("", null, null));
        Assert.Contains("COLORCORRECT_DATA_DIR", error.Message);
    }

    [Fact]
    public void TheDataFolderOnThisMachineIsAbsolute()
    {
        Assert.True(Path.IsPathFullyQualified(UserFolders.LocalData()));
        var previous = Environment.GetEnvironmentVariable("COLORCORRECT_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", null);
            Assert.True(Path.IsPathFullyQualified(SessionStore.AppDataDir()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COLORCORRECT_DATA_DIR", previous);
        }
    }
}
