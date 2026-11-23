using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class AssetIdentityCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccp-identity-" + Guid.NewGuid().ToString("N"));

    public AssetIdentityCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CachedCaptureMatchesFullHash()
    {
        var file = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var full = AssetIdentity.Capture(file);
        Assert.Equal(full, AssetIdentity.CaptureCached(file));
        Assert.True(full.MatchesCached());
    }

    [Fact]
    public void ContentChangeWithNewSizeIsDetected()
    {
        var file = Path.Combine(_dir, "b.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var identity = AssetIdentity.Capture(file);
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        Assert.False(identity.MatchesCached());
    }

    [Fact]
    public void SameSizeEditIsDetectedThroughWriteTime()
    {
        var file = Path.Combine(_dir, "c.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var identity = AssetIdentity.Capture(file);
        var stamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllBytes(file, [9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(file, stamp.AddSeconds(2));
        Assert.False(identity.MatchesCached());
        Assert.False(identity.Matches());
    }

    [Fact]
    public void UncachedFileIsHashedAndMissingFileDoesNotMatch()
    {
        var file = Path.Combine(_dir, "d.bin");
        File.WriteAllBytes(file, [7]);
        var expected = AssetIdentity.Capture(file).Sha256;
        var other = Path.Combine(_dir, "e.bin");
        File.Copy(file, other);
        Assert.Equal(expected, AssetIdentity.CaptureCached(other).Sha256);
        File.Delete(other);
        Assert.False(new AssetIdentity(other, expected).MatchesCached());
    }
}
