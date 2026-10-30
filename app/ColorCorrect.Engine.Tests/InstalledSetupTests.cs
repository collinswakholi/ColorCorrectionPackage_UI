using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

public sealed class InstalledSetupTests
{
    [Fact]
    public void ComponentSelectionAlwaysRetainsCpuAndFiltersUnknownValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """{"components":["webgpu","unrecognized","webgpu",null]}""");
            Assert.Equal("cpu,webgpu", InstalledSetup.ComponentAllowlist(path));
            File.WriteAllText(path, """{"components":[]}""");
            Assert.Equal("cpu", InstalledSetup.ComponentAllowlist(path));
            File.WriteAllText(path, "incomplete");
            Assert.Null(InstalledSetup.ComponentAllowlist(path));
            File.WriteAllText(path, "[]");
            Assert.Null(InstalledSetup.ComponentAllowlist(path));
        }
        finally { File.Delete(path); }
        Assert.Null(InstalledSetup.ComponentAllowlist(path));
    }

    [Fact]
    public void HandshakeRetainsAdditiveCpuProvenanceAndOldHandshakesRemainReadable()
    {
        var current = new HandshakeDocument("""{"device":"cpu","provider":"CPUExecutionProvider","inference":{"detect":{"backend":"cpu","fallback":{"from":"webgpu","reason":"Unavailable"}}}}""");
        Assert.Equal("CPUExecutionProvider", current.Provider);
        Assert.Equal("cpu", current.Inference!.Value.GetProperty("detect").GetProperty("backend").GetString());
        var old = new HandshakeDocument("""{"device":"directml"}""");
        Assert.Null(old.Inference);
        Assert.Empty(old.Provider);
    }
}
