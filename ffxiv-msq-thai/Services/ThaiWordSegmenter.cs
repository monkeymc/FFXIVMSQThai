using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin.Services;

namespace FfxivMsqThai.Services;

/// <summary>
/// Dictionary-based (Maximal / Longest-Match) Thai word segmenter.
///
/// At plugin startup call LoadDictionary() once to populate the runtime
/// HashSet from thai_dict.txt.  If the file is absent the class falls back
/// to the embedded bootstrap vocabulary so the plugin never crashes.
///
/// InjectZeroWidthSpaces() scans left-to-right, greedily consumes the
/// longest matching entry at each position, and joins tokens with U+200B.
///
/// ⚠  ImGui's TextWrapped ignores U+200B.  Feed the output through
///    ThaiTextProcessor.HardWrap() for pixel-accurate ImGui line breaking.
/// </summary>
public static class ThaiWordSegmenter
{
    // ── Embedded fallback vocabulary ──────────────────────────────────────────
    // Used when thai_dict.txt cannot be found or fails to parse.
    private static readonly string[] Fallback =
    {
        // function words / conjunctions
        "ความสามารถ", "ของ", "เจ้า", "อยู่", "ซึ่ง", "ก็",
        "นำมาสู่", "จุดประสงค์", "เรา", "ฉัน", "คิดว่า", "มีงาน",
        "ที่", "เหมาะสม", "กับ",
        // FFXIV proper nouns
        "ลัลลาเฟล", "อูลดาห์", "นักผจญภัย", "สถานที่",
        "บาดแผล", "มหาวิบัติ", "ทิ้งไว้",
    };

    // ── Runtime dictionary ────────────────────────────────────────────────────
    // Seeded with the fallback on first use; LoadDictionary() merges the file.
    private static readonly HashSet<string> Dict = new(Fallback);

    // Cached maximum word length to bound the inner probe loop.
    private static int _maxLen = ComputeMaxLen();

    private static int ComputeMaxLen()
    {
        var max = 1;
        foreach (var w in Dict)
            if (w.Length > max) max = w.Length;
        return max;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads <c>thai_dict.txt</c> from <paramref name="pluginDirectory"/> into
    /// the runtime dictionary.  Safe to call once from the plugin constructor;
    /// never call from the render thread.
    ///
    /// File format: UTF-8, one Thai word per line, blank lines and lines
    /// beginning with <c>#</c> are ignored.
    ///
    /// On any failure the embedded fallback vocabulary remains active.
    /// </summary>
    public static void LoadDictionary(string pluginDirectory, IPluginLog log)
    {
        var path = Path.Combine(pluginDirectory, "thai_dict.txt");

        if (!File.Exists(path))
        {
            log.Warning($"[ThaiWordSegmenter] thai_dict.txt not found at {path} — using bootstrap fallback.");
            return;
        }

        try
        {
            var added = 0;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                // Strip BOM, CR, LF, and any other whitespace/control characters
                // so keys stored in the HashSet are byte-identical to what the
                // tokeniser probes at runtime.
                var word = raw.TrimStart('\uFEFF').Trim(); // BOM first, then whitespace
                if (word.Length == 0 || word[0] == '#') continue;
                if (Dict.Add(word))
                {
                    added++;
                    if (word.Length > _maxLen) _maxLen = word.Length;
                }
            }
            log.Information($"[ThaiWordSegmenter] Loaded {added} new entries from thai_dict.txt (total {Dict.Count}).");
        }
        catch (Exception ex)
        {
            log.Warning($"[ThaiWordSegmenter] Failed to read thai_dict.txt: {ex.Message} — using bootstrap fallback.");
        }
    }

    // ── Tokenisation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Segments <paramref name="input"/> using left-to-right longest matching
    /// and returns each token as a separate string.
    /// Unmatched characters are accumulated into a single fallback token so
    /// that Thai combining characters (vowels, tone marks) stay together in
    /// one <c>ImGui.Text()</c> call and render correctly.
    /// </summary>
    public static string[] Segment(string input)
    {
        if (string.IsNullOrEmpty(input)) return Array.Empty<string>();

        var tokens   = new List<string>();
        var fallback = new StringBuilder();
        var i        = 0;

        while (i < input.Length)
        {
            var probeMax = Math.Min(input.Length - i, _maxLen);
            var matched  = false;

            for (var len = probeMax; len > 1; len--)
            {
                if (Dict.Contains(input.Substring(i, len)))
                {
                    if (fallback.Length > 0) { tokens.Add(fallback.ToString()); fallback.Clear(); }
                    tokens.Add(input.Substring(i, len));
                    i      += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                fallback.Append(input[i]);
                i++;
            }
        }

        if (fallback.Length > 0) tokens.Add(fallback.ToString());
        return tokens.ToArray();
    }

    /// <summary>
    /// Merges additional entries into the runtime dictionary.
    /// Useful for seeding words from the active translation data files.
    /// </summary>
    public static void AddWords(IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            if (string.IsNullOrEmpty(w)) continue;
            if (Dict.Add(w) && w.Length > _maxLen)
                _maxLen = w.Length;
        }
    }
}
