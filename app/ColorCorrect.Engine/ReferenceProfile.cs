using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace ColorCorrect.Engine;

/// <summary>A portable source reference. The worker derives canonical Lab; display RGB is never reference truth.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReferenceProfile
{
    [JsonRequired, JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonRequired, JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "Custom reference";
    [JsonRequired, JsonPropertyName("revision")] public int Revision { get; set; } = 1;
    [JsonRequired, JsonPropertyName("chart")] public string Chart { get; set; } = "classic";
    [JsonRequired, JsonPropertyName("color_space")] public string ColorSpace { get; set; } = "lab";
    [JsonRequired, JsonPropertyName("white_point")] public string WhitePoint { get; set; } = "d50";
    [JsonRequired, JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonRequired, JsonPropertyName("order")] public string Order { get; set; } = "row-major";
    [JsonRequired, JsonPropertyName("values")] public List<double[]> Values { get; set; } = [];

    public ReferenceProfile Snapshot() => JsonSerializer.Deserialize<ReferenceProfile>(JsonSerializer.Serialize(this))!;
    public string ToJson() { Validate(); return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }); }
    public static ReferenceProfile FromJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        string[] required = ["version", "id", "name", "revision", "chart", "color_space", "white_point", "scale", "order", "values"];
        if (document.RootElement.ValueKind != JsonValueKind.Object || required.Any(key => !document.RootElement.TryGetProperty(key, out _)) || document.RootElement.EnumerateObject().Any(p => !required.Contains(p.Name)))
            throw new ArgumentException("A version 1 reference JSON must contain exactly version, id, name, revision, chart, color_space, white_point, scale, order and values (2° observer). Export a reference template first.");
        var profile = JsonSerializer.Deserialize<ReferenceProfile>(json) ?? throw new ArgumentException("The reference file is empty.");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException($"Unsupported reference version {Version}.");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name) || Revision < 1)
            throw new ArgumentException("A reference needs an ID, name, and positive revision.");
        if (Chart is null || ColorSpace is null || WhitePoint is null) throw new ArgumentException("Reference interpretation fields cannot be null.");
        var template = ReferenceCatalog.Get(Chart);
        if (Chart != template.Chart) throw new ArgumentException($"Use canonical chart ID '{template.Chart}'.");
        if (Order is not ("row-major" or "column-major")) throw new ArgumentException("Choose row-major or column-major order.");
        if (!ColorSpaces.WhitePoints.Any(w => w.Id == WhitePoint)) throw new ArgumentException("Choose a supported reference white point.");
        var rgb = ColorSpaces.RgbSpaces.Any(s => s.Id == ColorSpace);
        if (!rgb && ColorSpace is not ("lab" or "xyz")) throw new ArgumentException("Choose Lab, XYZ, or a supported RGB encoding.");
        if (!double.IsFinite(Scale) || (ColorSpace == "lab" ? Scale != 1 : ColorSpace == "xyz" ? Scale is not (1 or 100) : Scale is not (1 or 255 or 65535)))
            throw new ArgumentException("Use Lab scale 1, XYZ scale 1 or 100, or RGB scale 1, 255 or 65535.");
        if (Values is null || Values.Count != template.Rows * template.Columns) throw new ArgumentException($"{Chart} requires exactly {template.Rows * template.Columns} cells.");
        for (var i = 0; i < Values.Count; i++)
        {
            var v = Values[i];
            if (v is null || v.Length != 3 || v.Any(c => !double.IsFinite(c))) throw new ArgumentException($"Cell {i + 1} needs three finite numbers.");
            if (ColorSpace == "lab" && (v[0] < 0 || v[0] > 100)) throw new ArgumentException($"Cell {i + 1}: L* must be between 0 and 100.");
            if (rgb && v.Any(c => c < 0 || c > Scale)) throw new ArgumentException($"Cell {i + 1}: RGB must be between 0 and {Scale}.");
            if (ColorSpace == "xyz" && v.Any(c => c < 0)) throw new ArgumentException($"Cell {i + 1}: XYZ cannot be negative.");
        }
    }

    public List<double[]> RowMajorValues()
    {
        Validate();
        var t = ReferenceCatalog.Get(Chart);
        return Enumerable.Range(0, Values.Count).Select(i => Values[Order == "row-major" ? i : (i % t.Columns) * t.Rows + i / t.Columns].ToArray()).ToList();
    }

    /// <summary>Import into a copy. A failed parse leaves the current profile untouched.</summary>
    public ReferenceProfile ImportText(string text)
    {
        var result = Snapshot();
        var template = ReferenceCatalog.Get(Chart);
        var values = new List<double[]>();
        var named = new Dictionary<int, double[]>();
        using var parser = new TextFieldParser(new StringReader(text));
        parser.SetDelimiters(text.Contains('\t') ? "\t" : text.Contains(';') ? ";" : ",");
        parser.HasFieldsEnclosedInQuotes = true;
        var first = true;
        bool? labelled = null;
        int? rowWidth = null;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            if (first && fields.Length is 3 or 4 or 5 && fields.Skip(fields.Length - 3).Any(f => !double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var headers = fields.Select(f => f.Trim().ToLowerInvariant()).ToArray();
                var channels = headers.TakeLast(3).ToArray();
                var expected = ColorSpace == "lab" ? new[] { "l", "a", "b" } : ColorSpace == "xyz" ? new[] { "x", "y", "z" } : new[] { "r", "g", "b" };
                var cleaned = channels.Select(h => h.TrimEnd('*')).ToArray();
                if ((!cleaned.SequenceEqual(expected) && !cleaned.SequenceEqual(new[] { "v1", "v2", "v3" })) || (headers.Length == 4 && headers[0] is not ("cell" or "coordinate" or "patch")) || (headers.Length == 5 && (headers[0] != "row" || headers[1] is not ("column" or "col"))))
                    throw new ArgumentException("CSV channel headers must match the selected color encoding, optionally preceded by cell or row,column. Generic v1,v2,v3 is also supported.");
                first = false; continue;
            }
            first = false;
            if (fields.Length is not (3 or 4 or 5)) throw new ArgumentException("Each input row must contain three values, optionally preceded by a cell coordinate or 1-based row,column.");
            var hasCell = fields.Length > 3;
            if (labelled is not null && labelled != hasCell) throw new ArgumentException("Do not mix coordinate-labelled and unlabelled rows.");
            if (rowWidth is not null && rowWidth != fields.Length) throw new ArgumentException("Do not mix coordinate formats in one table.");
            rowWidth = fields.Length;
            labelled = hasCell;
            var triplet = fields.TakeLast(3).Select(f => double.TryParse(f.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new ArgumentException($"Invalid number '{f}'. Use a decimal point.")).ToArray();
            if (!hasCell) values.Add(triplet);
            else if (fields.Length == 5)
            {
                if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) || row < 1 || row > template.Rows || column < 1 || column > template.Columns)
                    throw new ArgumentException($"Invalid cell row,column '{fields[0]},{fields[1]}'.");
                var index = (row - 1) * template.Columns + column - 1;
                if (!named.TryAdd(index, triplet)) throw new ArgumentException($"Duplicate cell {template.Coordinate(index)}.");
            }
            else
            {
                var coordinate = fields[0].Trim().ToUpperInvariant();
                if (coordinate.Length < 2 || coordinate[0] < 'A' || coordinate[0] >= 'A' + template.Columns || !int.TryParse(coordinate[1..], out var row) || row < 1 || row > template.Rows)
                    throw new ArgumentException($"Invalid cell coordinate '{coordinate}'.");
                var index = (row - 1) * template.Columns + coordinate[0] - 'A';
                if (!named.TryAdd(index, triplet)) throw new ArgumentException($"Duplicate cell {coordinate}.");
            }
        }
        result.Values = labelled == true ? Enumerable.Range(0, template.Rows * template.Columns).Select(i => named.TryGetValue(i, out var v) ? v : throw new ArgumentException($"Missing cell {template.Coordinate(i)}.")).ToList() : values;
        if (labelled == true) result.Order = "row-major";
        result.Validate();
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ChartReferenceAssignment
{
    [JsonRequired, JsonPropertyName("chart_id")] public string ChartId { get; set; } = "";
    [JsonRequired, JsonPropertyName("profile_id")] public string ProfileId { get; set; } = "";
    [JsonRequired, JsonPropertyName("profile_revision")] public int ProfileRevision { get; set; } = 1;
}

public sealed class ReferenceTemplate
{
    public string Chart { get; set; } = "";
    public int Rows { get; set; }
    public int Columns { get; set; }
    public List<string> Names { get; set; } = [];
    public List<double[]> Lab { get; set; } = [];
    public string Coordinate(int index) => $"{(char)('A' + index % Columns)}{index / Columns + 1}";
}

public static class ReferenceCatalog
{
    private static readonly Lazy<List<ReferenceTemplate>> Templates = new(() =>
    {
        using var stream = typeof(ReferenceCatalog).Assembly.GetManifestResourceStream("ColorCorrect.Engine.ReferenceTemplates.json")!;
        return JsonSerializer.Deserialize<List<ReferenceTemplate>>(stream)!;
    });
    public static string CanonicalChart(string value) => value switch
    {
        "classic_4x6" or "classic-4x6" => "classic",
        "spyder_4x6" or "spyder-4x6" => "spyder",
        "digital_sg_10x14" or "digital-sg-10x14" => "digital-sg",
        _ => value,
    };
    public static ReferenceTemplate Get(string chart) => Templates.Value.FirstOrDefault(t => t.Chart == CanonicalChart(chart)) ?? throw new ArgumentException($"Unsupported chart '{chart}'.");
    public static bool Compatible(string detected, string profile) => CanonicalChart(detected) == profile || CanonicalChart(detected) == "classic" && profile == "passport";
}
