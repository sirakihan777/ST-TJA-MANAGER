using System;
using System.Globalization;
using System.Text;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// 曲タイトル・サブタイトルの正規化ユーティリティ。
    /// songlistconverter の NormalizationUtils から必要部分を移植したもの。
    /// </summary>
    public static class TitleNormalizer
    {
        /// <summary>
        /// 大文字・小文字が意味を持つ既知のTITLE。
        /// これらのTITLEは NormalizeTitle で大文字小文字を保持する。
        /// </summary>
        private static readonly HashSet<string> CaseSensitiveTitles = new(StringComparer.OrdinalIgnoreCase)
        {
            "ALiVE",
            "ALIVE",
        };

        /// <summary>
        /// TITLEを正規化する。
        /// 全角→半角、記号除去、大文字化、スペース除去を行う。
        /// 一部のTITLE（ALiVE/ALIVE等）は大文字小文字を保持する。
        /// </summary>
        public static string NormalizeTitle(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            bool preserveCase = CaseSensitiveTitles.Contains(s.Trim());

            var sb = new StringBuilder();
            foreach (var c in s.Normalize(NormalizationForm.FormKC))
            {
                var work = c;
                // 全角英数字 (FF01-FF5E) → 半角 (0021-007E)
                if (work >= 0xFF01 && work <= 0xFF5E)
                    work = (char)(work - 0xFEE0);

                // アポストロフィ系の表記ゆれを統一
                if ("'''′‵ʼ＇".Contains(work)) work = '\'';
                if (work == '\u201C' || work == '\u201D') work = '"';

                // ローマ数字の統一
                if (work == 'Ⅰ') { sb.Append("I"); continue; }
                if (work == 'Ⅱ') { sb.Append("II"); continue; }
                if (work == 'Ⅲ') { sb.Append("III"); continue; }
                if (work == 'Ⅳ') { sb.Append("IV"); continue; }
                if (work == 'Ⅴ') { sb.Append("V"); continue; }
                if (work == 'Ⅵ') { sb.Append("VI"); continue; }
                if (work == 'Ⅶ') { sb.Append("VII"); continue; }
                if (work == 'Ⅷ') { sb.Append("VIII"); continue; }
                if (work == 'Ⅸ') { sb.Append("IX"); continue; }
                if (work == 'Ⅹ') { sb.Append("X"); continue; }

                // 空白・制御文字を除去
                if (char.IsWhiteSpace(work) || char.IsControl(work)) continue;

                // 記号・句読点を除去
                if (IsIgnorableSymbol(work)) continue;

                sb.Append(work);
            }
            return preserveCase ? sb.ToString() : sb.ToString().ToUpperInvariant();
        }

        /// <summary>
        /// SUBTITLEを正規化する。
        /// TJAの "--" / "++" プレフィックスを除去してから NormalizeTitle を適用する。
        /// </summary>
        public static string NormalizeSubtitle(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var work = s.TrimStart().TrimStart('-').TrimStart('+').Trim();
            return NormalizeTitle(work);
        }

        private static bool IsIgnorableSymbol(char c)
        {
            if (c == '\uFE0E' || c == '\uFE0F' || c == '\u200D') return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat is UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol;
        }
    }
}
