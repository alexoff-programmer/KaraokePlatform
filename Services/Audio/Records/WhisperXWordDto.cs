using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Records
{
    public class WhisperXWordDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("word")]
        public string Word { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("end_ms")]
        public long EndMs { get; set; }
    }
}