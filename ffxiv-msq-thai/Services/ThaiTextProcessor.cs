using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;

namespace FfxivMsqThai.Services;

/// <summary>
/// Pre-processes Thai strings before ImGui rendering.
///
/// Pass 1 — PSK glyph substitution
///   Detects tall Thai consonants (ป ผ ฝ พ ฟ ภ ฎ ฏ) followed by above-vowels
///   or tone marks and replaces the mark with its PSK-encoded lowered form.
///   The substitute codepoints (U+F700–U+F70F) are the Private-Use-Area slots
///   used by Sarabun PSK and other Thai PSK-encoded fonts.
///
/// Pass 2 — Hard word-wrap (ImGui)
///   HardWrap() must be called while the correct font is pushed on the ImGui
///   stack.  It uses ImGui.CalcTextSize + binary search to find the character
///   index where each line would exceed maxWidth, then walks backwards to the
///   nearest Thai syllable/word boundary and inserts '\n' there.
///   Standard ImGui TextWrapped does NOT break on U+200B, so InjectWordBreaks()
///   (ZWSP injection) is provided separately for renderers that do support it.
/// </summary>
public static class ThaiTextProcessor
{
    // ── PSK substitution ──────────────────────────────────────────────────────

    // Tall Thai consonants whose ascenders force above-marks to shift downward
    private static readonly HashSet<char> TallConsonants = new()
    {
        'ป', // ป
        'ผ', // ผ
        'ฝ', // ฝ
        'พ', // พ
        'ฟ', // ฟ
        'ภ', // ภ
        'ฎ', // ฎ
        'ฏ', // ฏ
    };

    // Normal above-mark → PSK Private-Use-Area lowered form.
    // Matches the encoding convention used by Sarabun PSK and similar
    // Thai government-standard fonts.
    private static readonly Dictionary<char, char> PskMap = new()
    {
        { 'ิ', '' }, // ิ  SARA I        → lowered
        { 'ี', '' }, // ี  SARA II       → lowered
        { 'ึ', '' }, // ึ  SARA UE       → lowered
        { 'ื', '' }, // ื  SARA UEE      → lowered
        { '็', '' }, // ็  MAI TAIKHU    → lowered
        { '่', '' }, // ่  MAI EK        → lowered
        { '้', '' }, // ้  MAI THO       → lowered
        { '๊', '' }, // ๊  MAI TRI       → lowered
        { '๋', '' }, // ๋  MAI CHATTAWA  → lowered
        { 'ํ', '' }, // ํ  NIKHAHIT      → lowered
    };

    /// <summary>
    /// Replaces above-marks that follow a tall consonant with their PSK lowered
    /// codepoints.  Works character-by-character; O(n).
    /// </summary>
    public static string ApplyPskSubstitution(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && TallConsonants.Contains(text[i - 1]) &&
                PskMap.TryGetValue(c, out var sub))
                sb.Append(sub);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Zero-width space injection (non-ImGui renderers) ─────────────────────

    private static readonly Regex ScriptEdge = new(
        @"(?<=[฀-๿])(?=[^฀-๿​])" +
        @"|(?<=[^฀-๿​])(?=[฀-๿])",
        RegexOptions.Compiled);

    /// <summary>
    /// Injects U+200B at Thai ↔ non-Thai script boundaries.
    /// Useful for non-ImGui renderers (e.g. web views) that break on ZWSP.
    /// Standard ImGui TextWrapped ignores U+200B — use HardWrap() instead.
    /// </summary>
    public static string InjectWordBreaks(string text)
        => string.IsNullOrEmpty(text) ? text : ScriptEdge.Replace(text, "​");

    // ── Hard wrap (ImGui CalcTextSize) ────────────────────────────────────────

    /// <summary>
    /// Inserts '\n' at Thai word/syllable boundaries so each line stays within
    /// maxWidth pixels when rendered with the currently-pushed ImGui font.
    /// Must be called inside a font Push() scope.
    /// </summary>
    public static string HardWrap(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Split on existing newlines so each logical line is wrapped independently
        var segments = text.Split('\n');
        var result   = new StringBuilder(text.Length + 16);

        for (var s = 0; s < segments.Length; s++)
        {
            if (s > 0) result.Append('\n');

            var seg = segments[s];
            if (string.IsNullOrEmpty(seg) ||
                ImGui.CalcTextSize(seg).X <= maxWidth)
            {
                result.Append(seg);
                continue;
            }

            WrapSegment(seg, maxWidth, result);
        }

        return result.ToString();
    }

    private static void WrapSegment(string seg, float maxWidth, StringBuilder sb)
    {
        var start = 0;
        var first = true;

        while (start < seg.Length)
        {
            if (!first) sb.Append('\n');
            first = false;

            var fitLen = FitLength(seg, start, maxWidth);

            // Remaining text fits — append and stop
            if (start + fitLen >= seg.Length)
            {
                sb.Append(seg, start, seg.Length - start);
                break;
            }

            // Walk backwards to the nearest break opportunity
            var breakOff = BreakBefore(seg, start, fitLen);
            sb.Append(seg, start, breakOff);

            // Advance past the break; skip a trailing space if present
            var next = start + breakOff;
            if (next < seg.Length && seg[next] == ' ') next++;
            start = next;
        }
    }

    /// <summary>
    /// Binary search: returns the length of the longest prefix of
    /// <c>text[start..]</c> whose pixel width ≤ maxWidth.
    /// Always returns at least 1 to guarantee forward progress.
    /// </summary>
    private static int FitLength(string text, int start, float maxWidth)
    {
        var lo = 1;
        var hi = text.Length - start;

        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text.Substring(start, mid)).X <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Scans backwards from <c>start+fitLen</c> to find the latest position
    /// that is a valid Thai word/syllable boundary.
    /// </summary>
    private static int BreakBefore(string text, int start, int fitLen)
    {
        for (var i = fitLen; i > 0; i--)
        {
            var abs  = start + i;
            if (abs >= text.Length) continue;

            var cur  = text[abs];
            var prev = text[abs - 1];

            // Break after a space
            if (prev == ' ') return i;

            // Break before a Thai lead vowel (เ แ โ ใ ไ) — unambiguous syllable start
            if (cur >= 'เ' && cur <= 'ไ') return i;

            // Break before a Thai base consonant that is not immediately after a
            // lead vowel (which would belong to the same syllable)
            if (IsThaiBase(cur) && !IsThaiLeadVowel(prev)) return i;
        }

        return fitLen; // no boundary found — hard-break at the fit boundary
    }

    // ── Character-class helpers ───────────────────────────────────────────────

    // Thai base consonants U+0E01–U+0E2E (ก–ฮ, 44 consonants)
    private static bool IsThaiBase(char c)      => c >= 'ก' && c <= 'ฮ';

    // Thai lead (preposed) vowels U+0E40–U+0E44 (เ แ โ ใ ไ)
    private static bool IsThaiLeadVowel(char c) => c >= 'เ' && c <= 'ไ';
}
