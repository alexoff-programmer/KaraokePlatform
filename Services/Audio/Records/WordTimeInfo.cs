using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Records
{
    public record WordTimeInfo
    {
        public string Text { get; set; } = string.Empty;
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public TimeSpan Duration => End - Start;
    }
}