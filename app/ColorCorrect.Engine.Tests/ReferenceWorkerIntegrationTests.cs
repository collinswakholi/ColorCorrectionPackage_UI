using System.Text.Json;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.Engine.Tests;

/// <summary>Explicit native acceptance: requires the built worker and real chart fixtures.</summary>
public sealed class NativeWorkerFactAttribute : FactAttribute
{
    public NativeWorkerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CCP_NATIVE_WORKER_TESTS") != "1")
            Skip = "Native GPU acceptance requires CCP_NATIVE_WORKER_TESTS=1, CCP_ENGINE_EXE, CCP_NEW_UI_ROOT, and a validated inference GPU.";
    }
}

public sealed class ReferenceWorkerIntegrationTests
{
    [NativeWorkerFact(Timeout=120_000)]
    public async Task RealWorkerCustomReferencesRoundTripThroughDotNetFitAndApply()
    {
        var root = EnginePaths.FindRepoRoot();
        var exe = EnginePaths.FindExecutable();
        var image = Path.Combine(root,"test_images","ColorCard_Big.JPG");
        var sample = Path.Combine(root,"test_images","Sample_3572.JPG");
        Assert.True(File.Exists(image),$"Native fixture missing: {image}");
        Assert.True(File.Exists(sample),$"Native fixture missing: {sample}");
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var cancellation = timeout.Token;
        await using var worker = new SpeedyWorker(exe,root) {Policy="auto"};
        await worker.StartAsync(cancellation);
        await worker.RequireCapabilityAsync("custom_references_v1",cancellation);
        await worker.RequireCapabilityAsync("chart_identity_v1",cancellation);
        var detection = await worker.DetectAsync(image,device:"auto",cancellationToken:cancellation);
        Assert.True(detection.Ok,detection.Error+"\n"+worker.FormatRecentStderr());
        var handshake = new HandshakeDocument(detection.HandshakeJson);
        Assert.NotEmpty(handshake.SnapshotId);Assert.NotEmpty(handshake.ChartId(0));
        var client = new CorrectionClient(worker,root);
        var builtinOptions = new CorrectionOptions {Method="ccm",FitSamples=3,Phase2Teacher=false,WriteOutput=false,PreviewMode="none"};
        var builtin = await client.FitAsync(image,handshake,builtinOptions,0,false,0.75,cancellation);
        Assert.True(builtin.Ok,builtin.Error+"\n"+builtin.Stderr);
        var first = handshake.ActiveFace(0);
        var type = first.GetProperty("cli_chart").GetString()!;
        var template = ReferenceCatalog.Get(type);
        var profile = new ReferenceProfile {Id="dotnet-native-card",Name="Native .NET card",Chart=template.Chart,Values=template.Lab.Select(v=>v.ToArray()).ToList()};
        var customOptions = new CorrectionOptions {Method="ccm",FitSamples=3,Phase2Teacher=false,WriteOutput=false,PreviewMode="none",
            ReferenceProfiles=[profile],ReferenceAssignments=[new ChartReferenceAssignment {ChartId=handshake.ChartId(0),ProfileId=profile.Id,ProfileRevision=profile.Revision}]};
        var custom = await client.FitAsync(image,handshake,customOptions,0,false,0.75,cancellation);
        Assert.True(custom.Ok,custom.Error+"\n"+custom.Stderr);
        Assert.NotNull(builtin.Report);Assert.NotNull(custom.Report);
        Assert.InRange(Math.Abs(builtin.Report.MeanAfter-custom.Report.MeanAfter),0,1e-5);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(custom.ModelPath));
        var context = saved.RootElement.GetProperty("reference_context");
        Assert.Equal(handshake.SnapshotId,context.GetProperty("snapshot_id").GetString());
        Assert.Equal(profile.Id,context.GetProperty("reference_profiles")[0].GetProperty("id").GetString());
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(custom.ReportPath));
        Assert.Equal(handshake.ChartId(0),report.RootElement.GetProperty("patches")[0].GetProperty("provenance").GetProperty("chart_id").GetString());
        var apply = await client.ApplyModelAsync(sample,custom.ModelPath,new CorrectionOptions {WriteOutput=true,OutputFormat="png",PreviewMode="none"},cancellation);
        Assert.True(apply.Ok,apply.Error+"\n"+apply.Stderr);Assert.True(File.Exists(apply.OutputPath));
        var collisionDir = Path.Combine(root,"_implementation","native-dotnet","collision-inputs-"+Guid.NewGuid().ToString("N"));
        var collisionInputs = new List<string>();
        foreach (var (folder,name) in new[] {("one","a.jpg"),("two","a.jpg"),("literal","a_1.jpg")})
        {
            var destination = Path.Combine(collisionDir,folder,name);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sample,destination);collisionInputs.Add(destination);
        }
        collisionInputs.Add(collisionInputs[0]);
        var batch = await client.ApplyManyAsync(collisionInputs,custom.ModelPath,new CorrectionOptions {WriteOutput=true,OutputFormat="png",PreviewMode="none"},cancellation);
        Assert.Equal(4,batch.Count);Assert.All(batch,one=>Assert.True(one.Ok,one.Error));
        Assert.Equal(4,batch.Select(one=>one.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(batch,one=>Assert.True(File.Exists(one.OutputPath)));
        var result = new {status="passed",elapsed_seconds=started.Elapsed.TotalSeconds,worker=exe,detection_snapshot=handshake.SnapshotId,
            builtin_report=builtin.ReportPath,custom_report=custom.ReportPath,model=custom.ModelPath,applied=apply.OutputPath,
            batch_outputs=batch.Select(one=>one.OutputPath).ToArray(),
            builtin_mean_delta_e=builtin.Report.MeanAfter,custom_mean_delta_e=custom.Report.MeanAfter};
        var resultDir=Path.Combine(root,"_implementation","native-dotnet");Directory.CreateDirectory(resultDir);
        await File.WriteAllTextAsync(Path.Combine(resultDir,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
    }
}
