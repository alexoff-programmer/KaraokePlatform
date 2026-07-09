using System;

namespace KaraokePlatform.Services.Audio.Records;

public record WordTiming
{
    public string Text { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public TimeSpan Duration => End - Start;
}
