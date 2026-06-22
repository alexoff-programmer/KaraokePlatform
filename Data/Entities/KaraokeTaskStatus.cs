namespace KaraokePlatform.Data.Entities
{
    public enum KaraokeTaskStatus
    {
        InQueue,      // В очереди
        Processing,   // В процессе обработки (работает Whisper/FFmpeg)
        AwaitingReview,
        Completed,    // Успешно завершено
        Failed
    }
}