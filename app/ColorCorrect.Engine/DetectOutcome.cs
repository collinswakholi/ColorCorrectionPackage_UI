namespace ColorCorrect.Engine;

public sealed record DetectOutcome(
    bool Ok,
    string HandshakeJson,
    string HandshakePath,
    string ChartType,
    int ChartCount,
    string Error);
