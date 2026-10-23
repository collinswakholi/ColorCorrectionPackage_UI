using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class SessionAssetPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccp-session-assets-" + Guid.NewGuid().ToString("N"));
    public SessionAssetPersistenceTests() => Directory.CreateDirectory(_root);

    private string Asset(string name, string bytes)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, bytes);
        return path;
    }

    private SessionState Result(string prefix = "one")
    {
        var photo = Asset(prefix + "/photo.jpg", prefix + " original");
        var model = Asset(prefix + "/model.json", "{\"model\":\"" + prefix + "\"}");
        var white = Asset(prefix + "/white.jpg", prefix + " white");
        var output = Asset(prefix + "/corrected.png", prefix + " complete output");
        var report = Asset(prefix + "/report.json", "{\"result\":\"" + prefix + "\"}");
        var preview = Asset(prefix + "/previews/corrected.rgb", prefix + " complete required RGB");
        var context = CompletedResultContext.Capture(AssetIdentity.Capture(photo), model,
            new() { DoFfc = true, WhiteImagePath = white });
        return new SessionState { Items = [new() { Path = photo, ModelPath = model, CorrectedPath = output,
            ReportPath = report, PreviewDir = Path.GetDirectoryName(preview)!, CompletedContext = context }] };
    }

    private static Dictionary<string, string> BytesUnder(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => AssetIdentity.Capture(path).Sha256);

    [Fact]
    public void RetryRepairsPartialOwnedAssetsFromTheIntactCapturedResult()
    {
        var state = Result(); var path = Path.Combine(_root, "session.json");
        Assert.True(SessionStore.Write(path, state));
        var (saved, error) = SessionStore.TryLoadFrom(path); Assert.Null(error);
        var item = saved.Items.Single();
        foreach (var asset in new[] { item.ModelPath, item.CompletedContext!.White!.Path, item.CorrectedPath,
            item.ReportPath, Path.Combine(item.PreviewDir, "corrected.rgb") }) File.WriteAllText(asset, "partial");
        File.Delete(path); // Interrupted earlier save left assets but no committed JSON.
        Assert.True(SessionStore.Write(path, state));
        var (recovered, recoveryError) = SessionStore.TryLoadFrom(path); Assert.Null(recoveryError);
        var actual = recovered.Items.Single(); var original = state.Items.Single();
        Assert.Null(actual.CompletedContext!.ReplayError(actual.Path));
        Assert.Equal(File.ReadAllBytes(original.CorrectedPath), File.ReadAllBytes(actual.CorrectedPath));
        Assert.Equal(File.ReadAllBytes(original.ReportPath), File.ReadAllBytes(actual.ReportPath));
        Assert.Equal(File.ReadAllBytes(Path.Combine(original.PreviewDir, "corrected.rgb")), File.ReadAllBytes(Path.Combine(actual.PreviewDir, "corrected.rgb")));
        Assert.Empty(Directory.GetFiles(path + ".assets", "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(path + ".assets", "*.rollback", SearchOption.AllDirectories));
    }

    [Fact]
    public void MatchingOwnedAssetsAreVerifiedWithoutBeingRewritten()
    {
        var state = Result(); var path = Path.Combine(_root, "session.json");
        Assert.True(SessionStore.Write(path, state));
        var files = Directory.GetFiles(path + ".assets", "*", SearchOption.AllDirectories);
        var timestamp = new DateTime(2010, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        foreach (var file in files) File.SetLastWriteTimeUtc(file, timestamp);
        Assert.True(SessionStore.Write(path, state));
        Assert.All(files, file => Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file)));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("white")]
    [InlineData("output")]
    public void CorruptOwnedAssetsWithMissingOriginalsDoNotProduceASuccessfulSave(string kind)
    {
        var state = Result(); var path = Path.Combine(_root, "session.json");
        Assert.True(SessionStore.Write(path, state)); var previousJson = File.ReadAllBytes(path);
        var (saved, _) = SessionStore.TryLoadFrom(path); var owned = saved.Items.Single(); var original = state.Items.Single();
        var source = kind switch { "model" => original.ModelPath, "white" => original.CompletedContext!.White!.Path, _ => original.CorrectedPath };
        var destination = kind switch { "model" => owned.ModelPath, "white" => owned.CompletedContext!.White!.Path, _ => owned.CorrectedPath };
        File.Delete(source); File.WriteAllText(destination, "partial");
        var before = BytesUnder(path + ".assets");
        Assert.False(SessionStore.Write(path, state));
        Assert.Equal(previousJson, File.ReadAllBytes(path));
        Assert.Equal(before.OrderBy(pair => pair.Key), BytesUnder(path + ".assets").OrderBy(pair => pair.Key));
    }

    [Fact]
    public void UnresolvedLegacyAssetPathsAreRetainedWithoutBlessingUnknownCopies()
    {
        var state = Result(); var path = Path.Combine(_root, "session.json");
        Assert.True(SessionStore.Write(path, state));
        var (saved, _) = SessionStore.TryLoadFrom(path); var owned = saved.Items.Single(); var original = state.Items.Single();
        File.Delete(Path.Combine(Path.GetDirectoryName(owned.ModelPath)!, "asset-identities.v1.json"));
        File.Delete(original.CorrectedPath); File.WriteAllText(owned.CorrectedPath, "unknown partial output");
        Assert.True(SessionStore.Write(path, state));
        var (loaded, error) = SessionStore.TryLoadFrom(path); Assert.Null(error);
        Assert.Equal(original.CorrectedPath, loaded.Items.Single().CorrectedPath);
        Assert.False(File.Exists(loaded.Items.Single().CorrectedPath));
        File.Delete(original.ModelPath); File.Delete(owned.ModelPath);
        Assert.True(SessionStore.Write(path, state));
        Assert.Equal(Path.GetFullPath(original.ModelPath), SessionStore.TryLoadFrom(path).State.Items.Single().CompletedContext!.Model.Path);
    }

    [Fact]
    public void RequiredRgbPreviewDoesNotFreezeAnOptionalPngStillBeingEncoded()
    {
        var state = Result(); var original = state.Items.Single();
        File.WriteAllText(Path.Combine(original.PreviewDir, "corrected.png"), "unfinished PNG");
        var path = Path.Combine(_root, "session.json"); Assert.True(SessionStore.Write(path, state));
        var owned = SessionStore.TryLoadFrom(path).State.Items.Single();
        Assert.Equal(File.ReadAllBytes(Path.Combine(original.PreviewDir, "corrected.rgb")), File.ReadAllBytes(Path.Combine(owned.PreviewDir, "corrected.rgb")));
        Assert.False(File.Exists(Path.Combine(owned.PreviewDir, "corrected.png")));
    }

    [WindowsFileLockFact]
    public void FailedJsonCommitRollsBackNewAssetsAndPreservesPreviousSessionAndValidArtifacts()
    {
        var state = Result(); var path = Path.Combine(_root, "session.json"); Assert.True(SessionStore.Write(path, state));
        var previousJson = File.ReadAllBytes(path); var previousAssets = BytesUnder(path + ".assets");
        state.Items.Add(Result("two").Items.Single());
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(SessionStore.Write(path, state));
        Assert.Equal(previousJson, File.ReadAllBytes(path));
        Assert.Equal(previousAssets.OrderBy(pair => pair.Key), BytesUnder(path + ".assets").OrderBy(pair => pair.Key));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_root, "*.rollback", SearchOption.AllDirectories));
        Assert.True(SessionStore.Write(path, state));
        Assert.Equal(2, SessionStore.TryLoadFrom(path).State.Items.Count);
    }

    [Fact]
    public void LegacyPngPreviewRemainsVerifiableAfterItsOriginalFolderIsRemoved()
    {
        var state = Result(); var original = state.Items.Single();
        File.Delete(Path.Combine(original.PreviewDir, "corrected.rgb"));
        File.WriteAllText(Path.Combine(original.PreviewDir, "corrected.png"), "complete legacy PNG artifact");
        var path = Path.Combine(_root, "session.json"); Assert.True(SessionStore.Write(path, state));
        var preview = SessionStore.TryLoadFrom(path).State.Items.Single().PreviewDir;
        Directory.Delete(original.PreviewDir, recursive: true);
        Assert.True(SessionStore.Write(path, state));
        var before = File.ReadAllBytes(path);
        File.WriteAllText(Path.Combine(preview, "corrected.png"), "partial");
        Assert.False(SessionStore.Write(path, state));
        Assert.Equal(before, File.ReadAllBytes(path));
        File.Delete(Path.Combine(preview, "corrected.png"));
        Assert.True(SessionStore.Write(path, state));
        Assert.Equal(original.PreviewDir, SessionStore.TryLoadFrom(path).State.Items.Single().PreviewDir);
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public sealed class WindowsFileLockFactAttribute : FactAttribute
{
    public WindowsFileLockFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows sharing locks prohibit replacing an open session file; Unix rename behavior differs.";
    }
}
