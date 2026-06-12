using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace KaraokePlatform.Services
{
    public static class ModelDownloader
    {
        public static async Task EnsureModelExistsAsync(string contentRootPath)
        {
            var modelsPath = Path.Combine(contentRootPath, "Models");
            var modelFilePath = Path.Combine(modelsPath, "ggml-medium.bin");

            if (File.Exists(modelFilePath))
            {
                Console.WriteLine("[INFO] Модель ggml-medium.bin уже существует. Скачивание пропущено.");
                return;
            }

            Console.WriteLine("[INFO] Модель ggml-medium.bin не найдена в папке Models. Начинается скачивание...");

            if (!Directory.Exists(modelsPath))
            {
                Directory.CreateDirectory(modelsPath);
            }

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(20); // Файл большой, таймаут 20 минут

            var modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin";

            try
            {
                using var response = await httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                using var downloadStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(modelFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920]; // Буфер 80 КБ
                long totalReadBytes = 0;
                int readBytes;
                int lastReportedPercent = -1;

                if (totalBytes != -1)
                {
                    Console.WriteLine($"[INFO] Размер файла: {(totalBytes / 1024.0 / 1024.0):F2} MB. Начинаем стриминг...");
                }
                else
                {
                    Console.WriteLine("[INFO] Размер файла неизвестен. Начинаем стриминг...");
                }

                while ((readBytes = await downloadStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, readBytes);
                    totalReadBytes += readBytes;

                    if (totalBytes != -1)
                    {
                        int percentage = (int)((totalReadBytes * 100) / totalBytes);

                        if (percentage % 5 == 0 && percentage != lastReportedPercent)
                        {
                            var downloadedMb = totalReadBytes / 1024.0 / 1024.0;
                            Console.WriteLine($"[DOWNLOAD] Прогресс: {percentage}% ({downloadedMb:F1} MB скачано)");
                            lastReportedPercent = percentage;
                        }
                    }
                }

                Console.WriteLine("[SUCCESS] Модель успешно скачана и сохранена в Models/ggml-medium.bin!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRITICAL ERROR] Не удалось скачать модель Whisper: {ex.Message}");
                throw new InvalidOperationException("Запуск приложения невозможен без модели Whisper.", ex);
            }
        }
    }
}