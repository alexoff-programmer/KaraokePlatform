using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Data.Entities
{
    public class KaraokeTask
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(255)]
        public string OriginalFileName { get; set; } = string.Empty;

        // Путь к сохраненному .mp3 файлу на сервере
        [Required]
        public string AudioFilePath { get; set; } = string.Empty;

        [Required]
        [MaxLength(10)]
        public string Language { get; set; } = "auto"; // По умолчанию автоопределение

        // Путь к готовому .mp4 файлу (будет пустым, пока статус не Completed)
        public string? VideoFilePath { get; set; }

        [Required]
        public KaraokeTaskStatus Status { get; set; } = KaraokeTaskStatus.InQueue;

        public string? ErrorMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        // Ссылка на пользователя, загрузившего трек
        public AppUser? User { get; set; }
    }
}