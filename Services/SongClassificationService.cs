using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// TJA曲を公式Database / サヨナラ曲 / CS限定楽曲 / 未分類 に分類するサービス。
    /// </summary>
    public static class SongClassificationService
    {
        /// <summary>
        /// 分類結果の種別。
        /// </summary>
        public enum Classification
        {
            Unknown,
            Official,
            Goodbye,
            ConsumerCandidate
        }

        /// <summary>
        /// 分類ソース。
        /// </summary>
        public static class Source
        {
            public const string NamcoOfficial = "NamcoOfficial";
            public const string WikiGoodbye = "WikiGoodbye";
            public const string WikiConsumer = "WikiConsumer";
            public const string None = "None";
        }

        /// <summary>
        /// 分類結果。
        /// </summary>
        public class Result
        {
            public Classification Kind { get; set; }
            public string Genre { get; set; } = "";
            public string? MatchedWorkTitle { get; set; }
            public string? GoodbyeDate { get; set; }
            public string Reason { get; set; } = "";
            public string Source { get; set; } = "";
            public string Message { get; set; } = "";
        }

        /// <summary>
        /// 単一のTJA情報を分類する。
        /// </summary>
        public static Result Classify(
            TjaInfo tja,
            IReadOnlyCollection<OfficialSongRecord> officialSongs,
            IReadOnlyCollection<GoodbyeSongRecord> goodbyeSongs,
            IReadOnlyCollection<ConsumerSongRecord> consumerSongs)
        {
            bool inOfficial = IsInOfficial(tja, officialSongs, out var officialMatch);
            bool inGoodbye = IsInGoodbye(tja, goodbyeSongs, out var goodbyeMatch);
            bool inConsumer = IsInConsumer(tja, consumerSongs, out var consumerMatch);

            // 1. 公式AC songlist照合
            if (inOfficial)
            {
                return new Result
                {
                    Kind = Classification.Official,
                    Genre = officialMatch!.Genre,
                    Reason = "ナムコ公式songlistに一致",
                    Source = Source.NamcoOfficial,
                    Message = $"公式Databaseにマッチ: {officialMatch.Title}"
                };
            }

            // 2. サヨナラ曲照合
            if (inGoodbye)
            {
                return new Result
                {
                    Kind = Classification.Goodbye,
                    Genre = goodbyeMatch!.Genre,
                    GoodbyeDate = goodbyeMatch.GoodbyeDate,
                    Reason = "サヨナラ曲リストに一致",
                    Source = Source.WikiGoodbye,
                    Message = $"サヨナラ曲にマッチ: {goodbyeMatch.Title}"
                };
            }

            // 3. CS限定楽曲照合
            // ナムコ公式AC songlistに存在しない曲のうち、CS版作品ページに存在する曲
            if (inConsumer)
            {
                return new Result
                {
                    Kind = Classification.ConsumerCandidate,
                    Genre = consumerMatch!.Genre,
                    MatchedWorkTitle = consumerMatch.WorkTitle,
                    Reason = "CS版作品ページに存在し、公式AC songlistには未一致",
                    Source = Source.WikiConsumer,
                    Message = $"CS限定楽曲: {consumerMatch.WorkTitle} / {consumerMatch.Title}"
                };
            }

            // 未分類
            return new Result
            {
                Kind = Classification.Unknown,
                Reason = "どのリストにも一致しない",
                Source = Source.None,
                Message = "どのDatabaseにもマッチしませんでした"
            };
        }

        /// <summary>
        /// TJAの属性（AC / CS / サヨナラ）を取得する。
        /// 複数の属性を持つ場合はカンマ区切りで返す。
        /// </summary>
        public static string GetAttributes(
            TjaInfo tja,
            IReadOnlyCollection<OfficialSongRecord> officialSongs,
            IReadOnlyCollection<GoodbyeSongRecord> goodbyeSongs,
            IReadOnlyCollection<ConsumerSongRecord> consumerSongs)
        {
            var attrs = new List<string>();

            if (IsInOfficial(tja, officialSongs, out _))
                attrs.Add("AC");

            if (IsInConsumer(tja, consumerSongs, out _))
                attrs.Add("CS");

            if (IsInGoodbye(tja, goodbyeSongs, out _))
                attrs.Add("サヨナラ");

            return string.Join(",", attrs);
        }

        public static bool TryMatchGoodbye(
            TjaInfo tja,
            IReadOnlyCollection<GoodbyeSongRecord> goodbyeSongs,
            out GoodbyeSongRecord? match)
            => IsInGoodbye(tja, goodbyeSongs, out match);

        public static bool TryMatchConsumer(
            TjaInfo tja,
            IReadOnlyCollection<ConsumerSongRecord> consumerSongs,
            out ConsumerSongRecord? match)
            => IsInConsumer(tja, consumerSongs, out match);

        /// <summary>
        /// 複数のTJA情報を一括分類する。
        /// </summary>
        public static Dictionary<string, Result> ClassifyAll(
            IEnumerable<TjaInfo> tjaInfos,
            IReadOnlyCollection<OfficialSongRecord> officialSongs,
            IReadOnlyCollection<GoodbyeSongRecord> goodbyeSongs,
            IReadOnlyCollection<ConsumerSongRecord> consumerSongs)
        {
            var results = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);
            foreach (var tja in tjaInfos)
            {
                if (tja.HasParseError) continue;
                results[tja.FilePath] = Classify(tja, officialSongs, goodbyeSongs, consumerSongs);
            }
            return results;
        }

        // --- 内部ヘルパー ---

        private static bool IsInOfficial(
            TjaInfo tja,
            IReadOnlyCollection<OfficialSongRecord> officialSongs,
            out OfficialSongRecord? match)
        {
            string nt = TitleNormalizer.NormalizeTitleForOfficialMatch(tja.Title);
            string ns = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);

            match = officialSongs.FirstOrDefault(r =>
                NormalizeOfficialTitleForLookup(r.Title) == nt
                && r.NormalizedSubtitle == ns);
            if (match == null && !string.IsNullOrEmpty(ns))
            {
                match = officialSongs.FirstOrDefault(r =>
                    NormalizeOfficialTitleForLookup(r.Title) == nt
                    && string.IsNullOrEmpty(r.NormalizedSubtitle));
            }
            if (match == null)
            {
                match = officialSongs.FirstOrDefault(r => NormalizeOfficialTitleForLookup(r.Title) == nt);
            }

            return match != null;
        }

        private static string NormalizeOfficialTitleForLookup(string title)
            => TitleNormalizer.NormalizeTitleForOfficialMatch(RemoveDoublePlayPrefix(title));

        private static string RemoveDoublePlayPrefix(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;

            string work = title.Trim();
            string stripped = Regex.Replace(
                work,
                @"^\s*(?:【\s*双打\s*】|［\s*双打\s*］|\[\s*双打\s*\]|\(\s*双打\s*\)|（\s*双打\s*）)\s*",
                "",
                RegexOptions.CultureInvariant);

            return string.Equals(stripped, work, StringComparison.Ordinal)
                ? title
                : stripped.Trim();
        }

        private static bool IsInGoodbye(
            TjaInfo tja,
            IReadOnlyCollection<GoodbyeSongRecord> goodbyeSongs,
            out GoodbyeSongRecord? match)
        {
            string nt = TitleNormalizer.NormalizeTitle(tja.Title);
            string ns = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);

            match = goodbyeSongs.FirstOrDefault(r =>
                r.NormalizedTitle == nt && r.NormalizedSubtitle == ns);
            if (match == null)
            {
                match = goodbyeSongs.FirstOrDefault(r => r.NormalizedTitle == nt);
            }

            return match != null;
        }

        private static bool IsInConsumer(
            TjaInfo tja,
            IReadOnlyCollection<ConsumerSongRecord> consumerSongs,
            out ConsumerSongRecord? match)
        {
            string nt = TitleNormalizer.NormalizeTitle(tja.Title);
            string ns = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);

            match = consumerSongs.FirstOrDefault(r =>
                r.NormalizedTitle == nt && r.NormalizedSubtitle == ns);
            if (match == null)
            {
                match = consumerSongs.FirstOrDefault(r => r.NormalizedTitle == nt);
            }

            return match != null;
        }
    }
}
