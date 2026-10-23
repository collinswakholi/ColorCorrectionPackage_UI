using ColorCorrect.App.Services;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class DesktopDialogPolicyTests
{
    [Theory]
    [InlineData(true, "CCP-Validation", "6.6.87.1-microsoft-standard-WSL2", true)]
    [InlineData(true, "CCP-Validation", null, true)]
    [InlineData(true, null, "6.6.87.1-microsoft-standard-WSL2\n", true)]
    [InlineData(true, "", "4.4.0-19041-Microsoft", true)]
    [InlineData(true, " ", "6.6.87.1-MICROSOFT-standard-WSL2", true)]
    [InlineData(true, null, "6.8.0-87-generic", false)]
    [InlineData(true, "", "6.11.0-1018-azure", false)]
    [InlineData(true, null, null, false)]
    [InlineData(true, " ", "", false)]
    [InlineData(false, "CCP-Validation", "6.6.87.1-microsoft-standard-WSL2", false)]
    [InlineData(false, null, "Microsoft Windows NT 10.0", false)]
    [InlineData(false, null, "23.6.0", false)]
    public void OnlyWslSelectsManagedPickers(bool linux, string? distribution, string? kernel, bool expected)
        => Assert.Equal(expected, DesktopDialogPolicy.IsWsl(linux, distribution, kernel));
}
