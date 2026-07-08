using System;
using System.Text.RegularExpressions;

namespace KaraokePlatform.Services.Audio;

public static class TextNormalizer
{
    // Регулярное выражение находит любые знаки препинания в начале или конце строки,
    // игнорируя дефисы и апострофы внутри слова.
    private static readonly Regex PunctuationCleanRegex = new Regex(@"(^[\p{P}&&[^\-']]+)|([\p{P}&&[^\-']]+$)", RegexOptions.Compiled);

    public static string NormalizeWord(string rawWord)
    {
        if (string.IsNullOrWhiteSpace(rawWord)) return string.Empty;

        // 1. Очищаем от знаков препинания по краям
        string cleaned = PunctuationCleanRegex.Replace(rawWord, string.Empty);

        // 2. Приводим к нижнему регистру
        return cleaned.ToLowerInvariant();
    }
}