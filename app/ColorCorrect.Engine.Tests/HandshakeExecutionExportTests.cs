using System.Text.Json;
using System.Text.Json.Nodes;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class HandshakeExecutionExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ccp-execution-export-" + Guid.NewGuid().ToString("N"));
    public HandshakeExecutionExportTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static HandshakeDocument Example() => new("""
        {"snapshot_id":"image-snapshot","runtime":"ort","backend":"onnx","device":"cpu",
         "provider":"CPUExecutionProvider","requested_policy":"auto","threads":8,"cached":false,
         "fallback":{"from":"webgpu","reason":"Device lost"},
         "inference":{"detect":{"backend":"cpu","provider":"CPUExecutionProvider","requested_policy":"auto","threads":8,
                                  "fallback":{"from":"webgpu","reason":"Device lost"},"attempts":[{"backend":"webgpu","reason":"Device lost"}]},
                      "recall":{"backend":"cpu","cached":true}},
         "charts":[
           {"chart_id":"image-snapshot:a","chart_type":"classic_4x6","cli_chart":"classic",
            "quad":[[0,0],[10,0],[10,10],[0,10]],"patch_obbs":[[[0,0],[10,0],[10,10],[0,10]]]},
           {"chart_id":"image-snapshot:b","chart_type":"classic_4x6","cli_chart":"classic",
            "quad":[[20,0],[30,0],[30,10],[20,10]],"patch_obbs":[[[20,0],[30,0],[30,10],[20,10]]]}]}
        """);

    [Fact]
    public void SelectedFaceRetainsWholeImageExecutionWithoutMutatingDetection()
    {
        var handshake = Example();
        var original = handshake.Json;
        var path = handshake.WriteForCorrection(Path.Combine(_folder, "selected.json"), 1, false, .75);
        using var exported = JsonDocument.Parse(File.ReadAllText(path));
        var root = exported.RootElement;
        Assert.Equal("image-snapshot", root.GetProperty("snapshot_id").GetString());
        Assert.Equal("image-snapshot:b", root.GetProperty("chart_id").GetString());
        Assert.Equal(20, root.GetProperty("quad")[0][0].GetDouble());
        Assert.Equal("cpu", root.GetProperty("device").GetString());
        Assert.Equal("CPUExecutionProvider", root.GetProperty("provider").GetString());
        Assert.Equal("ort", root.GetProperty("runtime").GetString());
        Assert.Equal("onnx", root.GetProperty("backend").GetString());
        Assert.Equal("auto", root.GetProperty("requested_policy").GetString());
        Assert.Equal(8, root.GetProperty("threads").GetInt32());
        Assert.False(root.GetProperty("cached").GetBoolean());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handshake.Root.GetProperty("inference").GetRawText()),
            JsonNode.Parse(root.GetProperty("inference").GetRawText())));
        Assert.Equal("Device lost", root.GetProperty("fallback").GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("charts", out _));
        Assert.Equal(original, handshake.Json);
    }

    [Fact]
    public void AllFacesRetainRootAndFaceSpecificExecutionWithoutInventingMissingAnnotations()
    {
        var json = JsonNode.Parse(Example().Json)!.AsObject();
        var first = json["charts"]![0]!.AsObject();
        first["provider"] = "DmlExecutionProvider";
        first["fallback"] = null;
        first["inference"] = JsonNode.Parse("""{"detect":{"backend":"directml","adapter":{"name":"Selected adapter","index":1}}}""");
        var handshake = new HandshakeDocument(json.ToJsonString());
        var path = handshake.WriteForCorrection(Path.Combine(_folder, "all.json"), 0, true, .75);
        using var all = JsonDocument.Parse(File.ReadAllText(path));
        var root = all.RootElement;
        Assert.Equal("CPUExecutionProvider", root.GetProperty("provider").GetString());
        Assert.Equal("cpu", root.GetProperty("inference").GetProperty("detect").GetProperty("backend").GetString());
        var charts = root.GetProperty("charts");
        Assert.Equal("DmlExecutionProvider", charts[0].GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, charts[0].GetProperty("fallback").ValueKind);
        Assert.Equal(1, charts[0].GetProperty("inference").GetProperty("detect").GetProperty("adapter").GetProperty("index").GetInt32());
        Assert.False(charts[1].TryGetProperty("provider", out _));
        Assert.Equal("image-snapshot:b", charts[1].GetProperty("chart_id").GetString());
        Assert.Equal(20, charts[1].GetProperty("quad")[0][0].GetDouble());

        path = handshake.WriteForCorrection(Path.Combine(_folder, "selected-annotated.json"), 0, false, .75);
        using var selected = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("DmlExecutionProvider", selected.RootElement.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, selected.RootElement.GetProperty("fallback").ValueKind);
    }

    [Fact]
    public void LegacyHandshakeExportsWithoutFabricatedExecutionMetadata()
    {
        var handshake = new HandshakeDocument("""{"snapshot_id":"old","chart_id":"old:a","chart_type":"classic_4x6","patch_obbs":[]}""");
        var path = handshake.WriteForCorrection(Path.Combine(_folder, "legacy.json"), 0, false, .75);
        using var exported = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var field in new[] { "inference", "device", "provider", "fallback", "runtime" })
            Assert.False(exported.RootElement.TryGetProperty(field, out _));
        Assert.Equal("old:a", exported.RootElement.GetProperty("chart_id").GetString());
    }
}
