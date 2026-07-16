using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KaraokePlatform.Services.Audio
{
    public class NormalizedTextResult
    {
        public string NormalizedText { get; set; } = string.Empty;
        public List<string> OriginalWords { get; set; } = new();
        public List<string> CleanWords { get; set; } = new();
    }

    public static class MmsTextNormalizer
    {
        public static NormalizedTextResult Normalize(string rawText)
        {
            var result = new NormalizedTextResult();
            if (string.IsNullOrWhiteSpace(rawText)) return result;

            var words = rawText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                // Normalize word: lowercase, replace ё/ё
                string clean = word.ToLower().Replace("ё", "е");

                // Clean punctuation
                clean = Regex.Replace(clean, @"[^\w\s]", "");

                // Convert numbers if any digits exist
                if (Regex.IsMatch(clean, @"\d+"))
                {
                    clean = ConvertNumbersToWords(clean);
                }

                // If word became completely empty, we skip or use a placeholder to avoid breaking the count
                if (string.IsNullOrWhiteSpace(clean))
                {
                    continue;
                }

                result.OriginalWords.Add(word);
                result.CleanWords.Add(clean);
            }

            result.NormalizedText = string.Join(" ", result.CleanWords);
            return result;
        }

        private static string ConvertNumbersToWords(string input)
        {
            // Replace any sequences of digits with their Russian word representation (concatenated as a single word to keep 1:1 mapping)
            return Regex.Replace(input, @"\d+", m =>
            {
                if (int.TryParse(m.Value, out int num))
                {
                    return NumberToRussianWord(num);
                }
                return m.Value;
            });
        }

        private static string NumberToRussianWord(int num)
        {
            if (num == 0) return "ноль";
            if (num < 0) return "минус" + NumberToRussianWord(Math.Abs(num));

            var sb = new StringBuilder();

            int hundreds = num / 100;
            if (hundreds > 0)
            {
                string[] hundredsWords = { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };
                sb.Append(hundredsWords[hundreds]);
                num %= 100;
            }

            if (num >= 10 && num < 20)
            {
                string[] teensWords = { "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
                sb.Append(teensWords[num - 10]);
            }
            else
            {
                int tens = num / 10;
                if (tens > 0)
                {
                    string[] tensWords = { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
                    sb.Append(tensWords[tens]);
                }

                int ones = num % 10;
                if (ones > 0)
                {
                    string[] onesWords = { "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
                    sb.Append(onesWords[ones]);
                }
            }

            return sb.ToString(); // Contiguous single word (e.g. стодвадцатьпять)
        }
    }
}
