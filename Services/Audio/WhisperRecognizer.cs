using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Settings;
using Microsoft.Extensions.Options;

namespace KaraokePlatform.Services.Audio;

public class WhisperRecognizer : ISpeechRecognizer
{
    private readonly HttpClient _httpClient;
    private readonly string _serviceUrl;

    public WhisperRecognizer(HttpClient httpClient, IOptions<WhisperSettings> settings)
    {
        _httpClient = httpClient;
        // Предполагается, что в настройках теперь лежит URL микросервиса (например, http://transcription-service:8000)
        _serviceUrl = settings.Value.ModelPath;
    }

    public async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        Action<int> onProgress)
    {
        var allWords = new List<WordTimeInfo>();

        if (!File.Exists(wavPath))
            throw new FileNotFoundException("Аудиофайл для транскрибации не найден", wavPath);

        onProgress?.Invoke(10); // Начало отправки

        HttpResponseMessage? response = null;
        int maxRetries = 12; // Ждем до 60 секунд (12 попыток по 5 сек)
        int delaySeconds = 5;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Создаем контент при КАЖДОЙ попытке заново, чтобы стримы не были закрыты
                using var attemptContent = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(wavPath);
                using var streamContent = new StreamContent(fileStream);

                attemptContent.Add(streamContent, "file", Path.GetFileName(wavPath));
                attemptContent.Add(new StringContent(language), "language");

                if (attempt == 1)
                {
                    onProgress?.Invoke(30); // Файл передан в поток запроса при первой попытке
                }

                // Пытаемся отправить запрос
                response = await _httpClient.PostAsync($"{_serviceUrl.TrimEnd('/')}/transcribe", attemptContent);
                break; // Успешное подключение, выходим из цикла ретраев
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // Сервис еще не запустился, подождем и попробуем снова
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        if (response == null)
        {
            throw new Exception("Не удалось подключиться к микросервису WhisperX: соединение отвергнуто. Возможно, сервис еще не запустился.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorMsg = await response.Content.ReadAsStringAsync();
            throw new Exception($"Ошибка микросервиса WhisperX: {response.StatusCode} - {errorMsg}");
        }

        onProgress?.Invoke(80); // Сервер обработал, парсим ответ

        var result = await response.Content.ReadFromJsonAsync<WhisperXResponse>();

        if (result?.Words != null)
        {
            foreach (var w in result.Words)
            {
                string wordText = w.Word ?? string.Empty;

                // Глубокая очистка: удаляем знаки препинания по краям, сохраняя внутренние одиночные дефисы (например, "рэп-батл")
                string cleanText = wordText.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '_', '*', '…', '-');

                // Нормализуем множественные дефисы и точки внутри слова
                cleanText = Regex.Replace(cleanText, @"-{2,}", "-");
                cleanText = Regex.Replace(cleanText, @"\.{2,}", "");

                // Заменяем "ё" на "е"
                cleanText = cleanText.Replace("ё", "е").Replace("Ё", "Е");

                // Если слово полностью состояло из знаков препинания, пропускаем его
                if (string.IsNullOrWhiteSpace(cleanText))
                    continue;

                allWords.Add(new WordTimeInfo
                {
                    Text = cleanText,
                    Start = TimeSpan.FromMilliseconds(w.StartMs),
                    End = TimeSpan.FromMilliseconds(w.EndMs)
                });
            }
        }

        onProgress?.Invoke(100);
        return allWords;
    }
}