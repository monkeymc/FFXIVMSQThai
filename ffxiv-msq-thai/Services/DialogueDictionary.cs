using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace FfxivMsqThai.Services;

public class DialogueDictionary
{
    private readonly Dictionary<string, QuestPaths> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QuestContent?> _fileCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _searchCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _searchCacheKeys = new();

    private const int MaxFileCached = 5;
    private const int MaxSearchCached = 100;

    private readonly IPluginLog _log;

    public int Count => _index.Count;
    public bool HasQuest(string questSlug) => _index.ContainsKey(questSlug);
    public string? GetQuestFilePath(string questSlug) => _index.TryGetValue(questSlug, out var p) ? p.RelPath : null;
    public int GetQuestKeyCount(string questSlug) => GetOrLoad(questSlug)?.Entries.Count ?? 0;

    public DialogueDictionary(string contentRoot, IPluginLog log)
    {
        _log = log;
        var root = Path.Combine(contentRoot, "content");
        if (!Directory.Exists(root))
        {
            _log.Warning($"[ffxiv-msq-thai] content/ not found at: {root}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var slug = ToPureAlphanumericKey(Path.GetFileNameWithoutExtension(file));
            if (slug.Length > 0) _index[slug] = new QuestPaths(file, rel);
        }
        _log.Information($"[ffxiv-msq-thai] Dictionary index ready — {_index.Count} quests.");
    }

    public string? GetTranslation(string questSlug, string gameTextEn)
    {
        var cacheKey = $"{questSlug}::{gameTextEn}";
        if (_searchCache.TryGetValue(cacheKey, out var cachedResult))
            return string.IsNullOrEmpty(cachedResult) ? null : cachedResult;

        var content = GetOrLoad(questSlug);
        if (content == null || content.Entries.Count == 0) return CacheAndReturn(cacheKey, null);

        var gameTokens = Tokenize(gameTextEn);
        if (gameTokens.Length == 0) return CacheAndReturn(cacheKey, null);

        QuestEntry? bestEntry = null;
        float bestScore = 0f;
        float bestStartRatio = 0f;
        float bestEndRatio = 1f;

        foreach (var entry in content.Entries)
        {
            int jsonLen = entry.EnTokens.Length;
            if (jsonLen == 0) continue;

            int matchCount = 0;
            int firstMatchIdx = -1;
            int lastMatchIdx = -1;

            var gamePool = new List<string>(gameTokens);

            for (int j = 0; j < jsonLen; j++)
            {
                var jToken = entry.EnTokens[j];
                for (int i = 0; i < gamePool.Count; i++)
                {
                    if (IsWordSimilar(jToken, gamePool[i]))
                    {
                        matchCount++;
                        if (firstMatchIdx == -1) firstMatchIdx = j;
                        lastMatchIdx = j;

                        gamePool.RemoveAt(i);
                        break;
                    }
                }
            }

            float score = (float)matchCount / gameTokens.Length;

            if (score > bestScore)
            {
                bestScore = score;
                bestEntry = entry;

                if (firstMatchIdx != -1)
                {
                    bestStartRatio = (float)firstMatchIdx / jsonLen;
                    bestEndRatio = (float)(lastMatchIdx + 1) / jsonLen;
                }
            }
        }

        // หากคะแนนผ่านเกณฑ์ 50%
        if (bestScore >= 0.50f && bestEntry != null)
        {
            var thText = bestEntry.TextTh;

            if (bestEntry.EnTokens.Length > gameTokens.Length * 1.3f)
            {
                int startChar = (int)(thText.Length * bestStartRatio);
                int endChar = (int)(thText.Length * bestEndRatio);

                string finalThai = SnapToThaiBoundary(thText, startChar, endChar);
                return CacheAndReturn(cacheKey, finalThai + " ...");
            }

            return CacheAndReturn(cacheKey, thText);
        }

        // หากหาไม่เจอ (MISS) ให้พ่น Log อธิบายสาเหตุ
        if (bestEntry != null && bestScore > 0)
        {
            var preview = bestEntry.TextEn.Length > 40 ? bestEntry.TextEn.Substring(0, 40) + "..." : bestEntry.TextEn;
            _log.Debug($"[MSQ-Thai-Diag] BoW Missed! Best score was {bestScore:P0} with JSON: '{preview}'");
        }
        else
        {
            _log.Debug($"[MSQ-Thai-Diag] BoW Missed! Score was 0% (No matching words at all or JSON is empty)");
        }

        return CacheAndReturn(cacheKey, null);
    }

    private string? CacheAndReturn(string key, string? result)
    {
        if (_searchCacheKeys.Count >= MaxSearchCached)
            _searchCache.Remove(_searchCacheKeys.Dequeue());

        _searchCacheKeys.Enqueue(key);
        _searchCache[key] = result ?? string.Empty;
        return result;
    }

    private static string[] Tokenize(string input)
    {
        var clean = Regex.Replace(input.ToLowerInvariant(), @"[^a-z0-9]", " ");
        return clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsWordSimilar(string w1, string w2)
    {
        if (w1 == w2) return true;
        if (Math.Abs(w1.Length - w2.Length) > 2) return false;
        if (w1.Length <= 3) return false;
        return LevenshteinDistance(w1, w2, 1) <= 1;
    }

    private static string SnapToThaiBoundary(string text, int start, int end)
    {
        while (start > 0 && start < text.Length && IsThaiDependentChar(text[start])) start--;
        while (end < text.Length && IsThaiDependentChar(text[end])) end++;

        int len = end - start;
        if (len <= 0) return text;
        return text.Substring(start, len).Trim();
    }

    private static bool IsThaiDependentChar(char c) =>
        (c >= '\u0E31' && c <= '\u0E3A') || (c >= '\u0E47' && c <= '\u0E4E');

    private QuestContent? GetOrLoad(string questSlug)
    {
        if (_fileCache.TryGetValue(questSlug, out var cached)) return cached;
        if (!_index.TryGetValue(questSlug, out var paths)) return null;

        var questFile = Read<QuestFile>(paths.FilePath);
        var entries = new List<QuestEntry>();

        if (questFile?.Dialogues != null)
        {
            foreach (var d in questFile.Dialogues)
            {
                if (string.IsNullOrWhiteSpace(d.TextEn) || string.IsNullOrWhiteSpace(d.TextTh)) continue;
                entries.Add(new QuestEntry(d.TextEn, d.TextTh, Tokenize(d.TextEn)));
            }
        }

        var content = new QuestContent(entries);
        if (_fileCache.Count >= MaxFileCached)
        {
            using var e = _fileCache.GetEnumerator();
            e.MoveNext();
            _fileCache.Remove(e.Current.Key);
        }
        _fileCache[questSlug] = content;
        return content;
    }

    public static string ToPureAlphanumericKey(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static readonly Regex MultiSpace = new(@" {2,}", RegexOptions.Compiled);

    public static string NormalizeEnglishKey(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var s = input.Trim().Replace("'", "'").Replace("“", "\"").Replace("”", "\"").Replace("\r", "").Replace("\n", " ");
        return MultiSpace.Replace(s, " ");
    }

    private static int LevenshteinDistance(string a, string b, int maxDist)
    {
        var m = a.Length; var n = b.Length;
        var row = new int[n + 1];
        for (var j = 0; j <= n; j++) row[j] = j;

        for (var i = 1; i <= m; i++)
        {
            var prev = i; var minInRow = i;
            for (var j = 1; j <= n; j++)
            {
                var curr = a[i - 1] == b[j - 1] ? row[j - 1] : 1 + Math.Min(row[j - 1], Math.Min(row[j], prev));
                row[j - 1] = prev; prev = curr;
                if (curr < minInRow) minInRow = curr;
            }
            row[n] = prev;
            if (minInRow > maxDist) return maxDist + 1;
        }
        return row[n];
    }

    private static T? Read<T>(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (Exception)
        {
            return default;
        }
    }

    private record QuestPaths(string FilePath, string RelPath);
    private class QuestEntry
    {
        public string TextEn { get; }
        public string TextTh { get; }
        public string[] EnTokens { get; }
        public QuestEntry(string en, string th, string[] tokens) { TextEn = en; TextTh = th; EnTokens = tokens; }
    }
    private class QuestContent
    {
        public List<QuestEntry> Entries { get; }
        public QuestContent(List<QuestEntry> e) => Entries = e;
    }

    // เปลี่นมาใช้คลาสปกติ (Bulletproof Json Binding)
    private class QuestFile
    {
        [JsonPropertyName("dialogues")] public List<Dialogue>? Dialogues { get; set; }
    }
    private class Dialogue
    {
        [JsonPropertyName("text_en")] public string? TextEn { get; set; }
        [JsonPropertyName("text")] public string? TextTh { get; set; }
    }
}