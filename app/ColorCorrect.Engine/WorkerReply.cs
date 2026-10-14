namespace ColorCorrect.Engine;

public sealed record WorkerReply(bool Ok, int ReturnCode, string Stdout, string Stderr, string Raw);
