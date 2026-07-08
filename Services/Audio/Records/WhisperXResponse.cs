using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Records
{
    public class WhisperXResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("words")]
        public List<WhisperXWordDto> Words { get; set; } = new();
    }
}