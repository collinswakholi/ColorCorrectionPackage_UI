using System.Globalization;
using System.Text.Json;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class ReferenceProfileTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ccp-reference-tests-{Guid.NewGuid():N}");
    public ReferenceProfileTests() => Directory.CreateDirectory(_scratch);
    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private static ReferenceProfile Profile(string chart = "classic") => new()
    {
        Id = "measured", Name = "Measured chart", Chart = chart,
        Values = ReferenceCatalog.Get(chart).Lab.Select(v => v.ToArray()).ToList(),
    };

    [Theory]
    [InlineData("classic")]
    [InlineData("passport")]
    [InlineData("spyder")]
    [InlineData("digital-sg")]
    public void InputOrdersAndCoordinateImportsMapEveryCellCorrectly(string chart)
    {
        var template = ReferenceCatalog.Get(chart);
        var source = Profile(chart);
        source.Values = Enumerable.Range(0, template.Rows * template.Columns)
            .Select(i => new[] { 10.0 + i * 0.5, i * 0.1, -i * 0.1 }).ToList();
        var expected = source.RowMajorValues();
        var column = source.Snapshot(); column.Order = "column-major";
        column.Values = Enumerable.Range(0, template.Columns)
            .SelectMany(c => Enumerable.Range(0, template.Rows).Select(r => source.Values[r * template.Columns + c])).ToList();
        var imported = column.ImportText("L*,a*,b*\n" + string.Join("\n", column.Values.Select(Csv)));
        var coordinateRows = Enumerable.Range(0, expected.Count).Reverse()
            .Select(i => $"{template.Coordinate(i)},{Csv(expected[i])}");
        var labelled = source.ImportText("cell,L,a,b\n" + string.Join("\n", coordinateRows));
        var numbered = source.ImportText("row,column,L,a,b\n" + string.Join("\n", Enumerable.Range(0, expected.Count).Reverse().Select(i => $"{i / template.Columns + 1},{i % template.Columns + 1},{Csv(expected[i])}")));
        Assert.Equal("row-major", labelled.Order);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], imported.RowMajorValues()[i]);
            Assert.Equal(expected[i], labelled.RowMajorValues()[i]);
            Assert.Equal(expected[i], numbered.RowMajorValues()[i]);
        }
    }

    [Theory]
    [InlineData("R,G,B\n50,0,0", "channel headers")]
    [InlineData("L,b,a\n50,0,0", "channel headers")]
    [InlineData("L,a,b\nNaN,0,0", "Invalid number")]
    [InlineData("L,a,b\n50,0", "three values")]
    [InlineData("cell,L,a,b\nA1,50,0,0\nA1,45,0,0", "Duplicate")]
    [InlineData("cell,L,a,b\nZ1,50,0,0", "Invalid cell")]
    [InlineData("cell,L,a,b\nA1,50,0,0\n50,0,0", "Do not mix")]
    public void MalformedImportsFailAtomically(string text, string errorPart)
    {
        var profile = Profile();
        var original = profile.ToJson();
        var error = Assert.ThrowsAny<Exception>(() => profile.ImportText(text));
        Assert.Contains(errorPart, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, profile.ToJson());
    }

    [Fact]
    public void DeclaredEncodingControlsHeadersAndNumericValidation()
    {
        var rgb = Profile(); rgb.ColorSpace = "srgb"; rgb.WhitePoint = "d65"; rgb.Scale = 255;
        var imported = rgb.ImportText("R,G,B\n" + string.Join("\n", Enumerable.Repeat("64,128,192", 24)));
        Assert.Equal(new double[] {64, 128, 192}, imported.Values[0]);
        Assert.Throws<ArgumentException>(() => rgb.ImportText("X,Y,Z\n" + string.Join("\n", Enumerable.Repeat("0.2,0.3,0.4", 24))));
        imported.Values[0][0] = 256;
        Assert.Throws<ArgumentException>(imported.Validate);
        var xyz = Profile(); xyz.ColorSpace = "xyz"; xyz.Scale = 100;
        xyz.Values = Enumerable.Repeat(new double[] {20, 30, 40},24).Select(v => v.ToArray()).ToList();
        xyz.Validate(); xyz.Values[0][1] = -1;
        Assert.Throws<ArgumentException>(xyz.Validate);
    }

    [Fact]
    public void JsonSnapshotPreservesInterpretationAndRejectsUnsupportedVersions()
    {
        var profile = Profile("digital-sg"); profile.Revision = 7; profile.WhitePoint = "d55";
        var loaded = ReferenceProfile.FromJson(profile.ToJson());
        Assert.Equal(profile.ToJson(),loaded.ToJson());
        loaded.Values[0][0] = 12;
        Assert.NotEqual(profile.Values[0][0],loaded.Values[0][0]);
        profile.Version = 99;
        Assert.Throws<ArgumentException>(profile.Validate);
        Assert.Throws<ArgumentException>(() => ReferenceProfile.FromJson("{\"version\":99}"));
        Assert.True(ReferenceCatalog.Compatible("classic_4x6","passport"));
        Assert.False(ReferenceCatalog.Compatible("spyder_4x6","passport"));
    }

    [Fact]
    public void SessionDeserializationCannotSilentlyDropInterpretationOrInventMissingFields()
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(Profile().ToJson())!.AsObject();
        value["observer"] = 10;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReferenceProfile>(value.ToJsonString()));
        value.Remove("observer");
        value.Remove("white_point");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReferenceProfile>(value.ToJsonString()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChartReferenceAssignment>("{\"chart_id\":\"a\",\"profile_id\":\"b\"}"));
    }

    [Fact]
    public void DuplicateJsonInterpretationFieldsFailProfileAndSessionImports()
    {
        var valid = Profile().ToJson();
        var ambiguous = valid[..valid.LastIndexOf('}')] + ",\"white_point\":\"d65\"}";
        Assert.Throws<JsonException>(() => ReferenceProfile.FromJson(ambiguous));
        var path = Path.Combine(_scratch, "duplicate-profile-session.json");
        var content = "{\"Items\":[{\"ReferenceProfiles\":[" + ambiguous + "]}]}";
        File.WriteAllText(path, content);
        Assert.NotNull(SessionStore.TryLoadFrom(path).Error);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void DisplaySwatchesMatchIndependentColorVectorsAndDoNotMutateTruth()
    {
        var rgb = Profile(); rgb.ColorSpace = "srgb"; rgb.WhitePoint = "d65"; rgb.Scale = 1;
        var input = new double[] {0.22,0.47,0.68};
        AssertVector(input,ReferenceColorPreview.DisplaySrgb(rgb,input),1e-12);
        rgb.Scale = 255;
        AssertVector(input,ReferenceColorPreview.DisplaySrgb(rgb,input.Select(v => v*255).ToArray()),1e-12);
        rgb.ColorSpace = "linear-srgb"; rgb.Scale = 1;
        AssertVector(new[] {0.5,0.5,0.5},ReferenceColorPreview.DisplaySrgb(rgb,[0.21404114048223255,0.21404114048223255,0.21404114048223255]),1e-10);
        var xyz = Profile(); xyz.ColorSpace = "xyz"; xyz.WhitePoint = "d65";
        // IEC sRGB red primary, XYZ normalized to Y white = 1.
        AssertVector(new[] {1.0,0.0,0.0},ReferenceColorPreview.DisplaySrgb(xyz,[0.4123907993,0.2126390059,0.0193308187]),2e-6);
        var lab = Profile(); var wide = new double[] {50,180,-140}; var original = wide.ToArray();
        Assert.All(ReferenceColorPreview.DisplaySrgb(lab,wide),v => Assert.InRange(v,0,1));
        Assert.Equal(original,wide);
        AssertVector(new[] {1.0,1.0,1.0},ReferenceColorPreview.DisplaySrgb(lab,[100,0,0]),1e-10);
    }

    [Fact]
    public void SingleFaceExportPreservesSnapshotAndChosenChartIdentity()
    {
        var handshake = new HandshakeDocument(TwoCharts());
        var path = Path.Combine(_scratch,"selected.json");
        handshake.WriteForCorrection(path,1,false,0.75);
        using var selected = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("snapshot",selected.RootElement.GetProperty("snapshot_id").GetString());
        Assert.Equal("snapshot:b",selected.RootElement.GetProperty("chart_id").GetString());
        Assert.Equal(20, selected.RootElement.GetProperty("quad")[0][0].GetDouble());
        Assert.False(selected.RootElement.TryGetProperty("charts",out _));
        handshake.WriteForCorrection(path,0,true,0.75);
        using var all = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("snapshot:b",all.RootElement.GetProperty("charts")[1].GetProperty("chart_id").GetString());
    }

    [Fact]
    public void MalformedSessionReferenceCollectionsReturnAnErrorWithoutChangingTheFile()
    {
        var path = Path.Combine(_scratch, "invalid-session.json");
        var content = "{\"Items\":[{\"ReferenceProfiles\":null,\"ReferenceAssignments\":[]}]}";
        File.WriteAllText(path, content);
        var result = SessionStore.TryLoadFrom(path);
        Assert.NotNull(result.Error);
        Assert.Empty(result.State.Items);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void SessionRoundTripRetainsReferenceRevisionsAndDetectionSnapshot()
    {
        var image = Path.Combine(_scratch,"existing.png"); File.WriteAllText(image,"fixture path only");
        var profile = Profile(); profile.Revision = 3;
        var state = new SessionState { Items = [new SessionItemDto { Path=image,HandshakeJson=TwoCharts(),
            ReferenceProfiles=[profile],ReferenceAssignments=[new ChartReferenceAssignment {ChartId="snapshot:b",ProfileId=profile.Id,ProfileRevision=3}]}]};
        var path = Path.Combine(_scratch,"session.json"); Assert.True(SessionStore.Write(path,state));
        var (loaded,error) = SessionStore.TryLoadFrom(path);
        Assert.Null(error); var item = Assert.Single(loaded.Items);
        Assert.Equal(profile.ToJson(),Assert.Single(item.ReferenceProfiles).ToJson());
        Assert.Equal("snapshot:b",Assert.Single(item.ReferenceAssignments).ChartId);
        Assert.Equal("snapshot",new HandshakeDocument(item.HandshakeJson).SnapshotId);
    }

    private static string Csv(double[] values) => string.Join(",",values.Select(v => v.ToString("R",CultureInfo.InvariantCulture)));
    private static void AssertVector(double[] expected,double[] actual,double tolerance)
    { for(var i=0;i<3;i++) Assert.InRange(Math.Abs(expected[i]-actual[i]),0,tolerance); }
    private static string TwoCharts() => """
        {"ok":true,"snapshot_id":"snapshot","charts":[
            {"chart_id":"snapshot:a","chart_type":"classic_4x6","cli_chart":"classic","quad":[[0,0],[10,0],[10,10],[0,10]],"patch_obbs":[[[0,0],[10,0],[10,10],[0,10]]]},
            {"chart_id":"snapshot:b","chart_type":"classic_4x6","cli_chart":"classic","quad":[[20,0],[30,0],[30,10],[20,10]],"patch_obbs":[[[20,0],[30,0],[30,10],[20,10]]]}]}
        """;
}
