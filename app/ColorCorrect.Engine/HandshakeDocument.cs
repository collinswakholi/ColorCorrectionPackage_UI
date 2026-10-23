using System.Text.Json;
using System.Text.Json.Nodes;

namespace ColorCorrect.Engine;

public sealed class HandshakeDocument
{
    public HandshakeDocument(string json)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject
            ?? throw new ArgumentException("Chart geometry must be a JSON object.");
        var snapshot = node["snapshot_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        node["snapshot_id"] = snapshot;
        if (node["charts"] is JsonArray charts)
        {
            for (var i = 0; i < charts.Count; i++)
                if (charts[i] is JsonObject face) face["chart_id"] ??= $"{snapshot}:{i}";
        }
        node["chart_id"] ??= $"{snapshot}:0";
        Json = node.ToJsonString();
        using var doc = JsonDocument.Parse(Json);
        Root = doc.RootElement.Clone();
    }

    public string Json { get; }
    public JsonElement Root { get; }

    public bool Ok => !Root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
    public string ChartType => Str("chart_type", Str("cli_chart"));
    public int ChartCount => Faces().Count;
    public string CliChart => Str("cli_chart");
    public string Runtime => Str("runtime");
    public string Backend => Str("backend");
    public string Device => Str("device");
    public string SnapshotId => Str("snapshot_id");
    public string ChartId(int choice) => GetStr(ActiveFace(choice), "chart_id") ?? "";
    public int DetectionCount => GetInt("n_dets");
    private int? _patchCount;
    private IReadOnlyList<JsonElement>? _faces;

    public int PatchCount => _patchCount ??= Math.Max(AllLatticeQuads().Count, AllSampleQuads(1).Count);

    public string GetString(string name, string fallback = "") => Str(name, fallback);

    public int GetInt(string name, int fallback = 0)
    {
        if (Root.TryGetProperty(name, out var el))
        {
            if (el.TryGetInt32(out var n))
            {
                return n;
            }

            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            {
                return n;
            }
        }

        return fallback;
    }

    public IReadOnlyList<JsonElement> Faces()
    {
        if (_faces is not null)
        {
            return _faces;
        }

        if (Root.TryGetProperty("charts", out var charts) && charts.ValueKind == JsonValueKind.Array && charts.GetArrayLength() > 0)
        {
            _faces = charts.EnumerateArray().ToList();
            return _faces;
        }

        _faces = Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("chart_type", out _)
            ? [Root]
            : [];
        return _faces;
    }

    public IReadOnlyList<string> FaceLabels()
    {
        return Faces()
            .Select((face, i) =>
            {
                var type = GetStr(face, "chart_type") ?? GetStr(face, "cli_chart") ?? "chart";
                return $"{i + 1}. {type}";
            })
            .ToList();
    }

    public JsonElement ActiveFace(int choice)
    {
        var faces = Faces();
        if (faces.Count == 0)
        {
            return Root;
        }

        var index = Math.Clamp(choice, 0, faces.Count - 1);
        return faces[index];
    }

    public IReadOnlyList<Quad> LatticeQuads(int choice)
    {
        return ReadQuads(ActiveFace(choice), "patch_cells");
    }

    public IReadOnlyList<Quad> AllLatticeQuads()
    {
        var list = new List<Quad>();
        var count = Math.Max(1, Faces().Count);
        for (var i = 0; i < count; i++)
        {
            list.AddRange(LatticeQuads(i));
        }

        return list;
    }

    public IReadOnlyList<Quad> AllSampleQuads(double crop)
    {
        var list = new List<Quad>();
        var count = Math.Max(1, Faces().Count);
        for (var i = 0; i < count; i++)
        {
            list.AddRange(SampleQuads(i, crop));
        }

        return list;
    }

    public IReadOnlyList<Quad> AllFaceQuads()
    {
        var list = new List<Quad>();
        var count = Math.Max(1, Faces().Count);
        for (var i = 0; i < count; i++)
        {
            var quad = FaceQuad(i);
            if (quad is not null)
            {
                list.Add(quad);
            }
        }

        return list;
    }

    public IReadOnlyList<Quad> SampleQuads(int choice, double crop)
    {
        var cells = LatticeQuads(choice);
        if (cells.Count > 0)
        {
            return cells.Select(q => q.ScaleAboutCenter(crop)).ToList();
        }

        return ReadQuads(ActiveFace(choice), "patch_obbs");
    }

    public Quad? FaceQuad(int choice)
    {
        var face = ActiveFace(choice);
        if (face.TryGetProperty("quad", out var quad))
        {
            return Quad.TryParse(quad);
        }

        if (face.TryGetProperty("corners", out var corners) && corners.ValueKind == JsonValueKind.Object)
        {
            if (TryCorner(corners, "tl", out var tl) &&
                TryCorner(corners, "tr", out var tr) &&
                TryCorner(corners, "br", out var br) &&
                TryCorner(corners, "bl", out var bl))
            {
                return new Quad(tl, tr, br, bl);
            }
        }

        return null;
    }

    public string WriteForCorrection(string path, int choice, bool useAll, double crop)
    {
        using var doc = JsonDocument.Parse(Json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            if (useAll && Faces().Count > 1)
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteString("snapshot_id", SnapshotId);
                writer.WriteBoolean("use_all", true);
                writer.WriteNumber("crop", crop);
                writer.WriteNumber("n_charts", Faces().Count);
                writer.WritePropertyName("charts");
                writer.WriteStartArray();
                foreach (var face in Faces())
                {
                    WriteFace(writer, face, crop, SnapshotId);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else
            {
                WriteFace(writer, ActiveFace(choice), crop, SnapshotId);
            }
        }

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static void WriteFace(Utf8JsonWriter writer, JsonElement face, double crop, string snapshotId)
    {
        writer.WriteStartObject();
        writer.WriteString("snapshot_id", snapshotId);
        CopyString(writer, face, "chart_id");
        CopyString(writer, face, "chart_type");
        CopyString(writer, face, "cli_chart");
        writer.WriteBoolean("ok", true);
        writer.WriteBoolean("accepted", true);
        writer.WriteNumber("crop", crop);
        if (face.TryGetProperty("rows", out var rows)) writer.WriteNumber("rows", rows.GetInt32());
        if (face.TryGetProperty("cols", out var cols)) writer.WriteNumber("cols", cols.GetInt32());
        if (face.TryGetProperty("quad", out var quad))
        {
            writer.WritePropertyName("quad");
            quad.WriteTo(writer);
        }
        if (face.TryGetProperty("corners", out var corners))
        {
            writer.WritePropertyName("corners");
            corners.WriteTo(writer);
        }

        var cells = ReadQuads(face, "patch_cells");
        if (cells.Count > 0)
        {
            WriteQuads(writer, "patch_cells", cells);
            WriteQuads(writer, "patch_obbs", cells.Select(q => q.ScaleAboutCenter(crop)).ToList());
        }
        else
        {
            WriteQuads(writer, "patch_obbs", ReadQuads(face, "patch_obbs"));
        }

        writer.WriteEndObject();
    }

    private static void WriteQuads(Utf8JsonWriter writer, string name, IReadOnlyList<Quad> quads)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var quad in quads)
        {
            writer.WriteStartArray();
            foreach (var p in quad.Points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(p.X);
                writer.WriteNumberValue(p.Y);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }

    private static List<Quad> ReadQuads(JsonElement face, string name)
    {
        var list = new List<Quad>();
        if (!face.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var quad = Quad.TryParse(item);
            if (quad is not null)
            {
                list.Add(quad);
            }
        }

        return list;
    }

    private static void CopyString(Utf8JsonWriter writer, JsonElement face, string name)
    {
        if (face.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            writer.WriteString(name, value.GetString());
        }
    }

    private static bool TryCorner(JsonElement corners, string key, out Pt pt)
    {
        pt = default;
        return corners.TryGetProperty(key, out var el) && TryCornerPoint(el, out pt);
    }

    private static bool TryCornerPoint(JsonElement el, out Pt pt)
    {
        pt = default;
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
        {
            pt = new Pt(el[0].GetDouble(), el[1].GetDouble());
            return true;
        }

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y))
        {
            pt = new Pt(x.GetDouble(), y.GetDouble());
            return true;
        }

        return false;
    }

    private string Str(string a, string fallback = "")
    {
        if (Root.TryGetProperty(a, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? fallback;
        }

        return fallback;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
