using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Records
{
    public record WordTimeInfo
    {
        public string Text { get; set; } = string.Empty;
        public int StartSample { get; set; }
        public int EndSample { get; set; }
        
        public TimeSpan Start
        {
            get => TimeSpan.FromSeconds(StartSample / 16000.0);
            set => StartSample = (int)Math.Round(value.TotalSeconds * 16000.0);
        }

        public TimeSpan End
        {
            get => TimeSpan.FromSeconds(EndSample / 16000.0);
            set => EndSample = (int)Math.Round(value.TotalSeconds * 16000.0);
        }

        public TimeSpan Duration => End - Start;
    }
}