using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// TJA情報と公式Databaseを照合して OfficialMatchResult を生成するサービス。
    /// </summary>
    public static class OfficialSongMatcher
    {
        // --- TJA読み取り ---

        /// <summary>
        /// 指定フォルダから .tja を再帰検索し、各TJAのヘッダー情報を読み取る。
        /// TITLEJA/SUBTITLEJA を優先する。
        /// 既存 TjaParser を使用するが、TITLEJA/SUBTITLEJA は直接正規表現で読む。
        /// </summary>
        public static List<TjaInfo> ReadTjaInfos(string rootFolder, Action<string>? log = null)
        {
            var results = new List<TjaInfo>();
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
                return results;

            string[] files;
            try
            {
                files = Directory.GetFiles(rootFolder, "*.tja", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERROR] TJA検索に失敗しました: {ex.Message}");
                return results;
            }

            foreach (string file in files)
            {
                try
                {
                    var enc = TjaParser.GetTjaEncoding(file);
                    string content = File.ReadAllText(file, enc);
                    var info = ParseTjaHeader(file, content);
                    results.Add(info);
                }
                catch (Exception ex)
                {
                    results.Add(new TjaInfo
                    {
                        FilePath = file,
                        Title = Path.GetFileNameWithoutExtension(file),
                        Subtitle = "",
                        ParseError = ex.Message
                    });
                }
            }

            log?.Invoke($"[INFO] TJA: {results.Count} 件読み取り完了");
            return results;
        }

        private static TjaInfo ParseTjaHeader(string filePath, string content)
        {
            // TITLEJA 優先 → TITLE
            string title = GetHeaderValue(content, "TITLEJA")
                        ?? GetHeaderValue(content, "TITLE")
                        ?? Path.GetFileNameWithoutExtension(filePath);

            // SUBTITLEJA 優先 → SUBTITLE
            string rawSub = GetHeaderValue(content, "SUBTITLEJA")
                         ?? GetHeaderValue(content, "SUBTITLE")
                         ?? "";
            string genre = GetHeaderValue(content, "GENRE") ?? "";

            // サブタイトルの "--" / "++" 接頭辞を除去
            string subtitle = rawSub.TrimStart().TrimStart('-').TrimStart('+').Trim();

            return new TjaInfo
            {
                FilePath = filePath,
                Title = title.Trim(),
                Subtitle = subtitle,
                RawSubtitle = rawSub.Trim(),
                Genre = genre.Trim()
            };
        }

        private static string? GetHeaderValue(string content, string key)
        {
            var match = Regex.Match(content,
                $@"(?im)^{Regex.Escape(key)}\s*:(.*)",
                RegexOptions.Multiline);
            if (!match.Success) return null;
            string val = match.Groups[1].Value.Trim();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        // --- 照合 ---

        /// <summary>
        /// TJAリストとDatabaseを照合して OfficialMatchResult リストを返す。
        /// 各 OfficialSongRecord に対してTJAを1つ見つける（Database主導）。
        /// </summary>
        public static List<OfficialMatchResult> Match(
            List<OfficialSongRecord> database,
            List<TjaInfo> tjaInfos,
            string outputSongsFolder,
            Action<string>? log = null)
        {
            var results = new List<OfficialMatchResult>();

            var validTjas = tjaInfos
                .Where(t => !t.HasParseError)
                .Select((tja, index) => new IndexedTja(tja, index))
                .ToList();

            // 公式側は、同一曲が複数ジャンルに載ることがあるため曲キー単位で候補を共有する。
            // 【双打】枠も通常曲TJAに同梱される前提で、通常曲と同じ曲キーへ寄せる。
            foreach (var group in database.GroupBy(GetOfficialSongKey, StringComparer.Ordinal))
            {
                string officialSongKey = group.Key;
                var records = group.ToList();
                var representative = records.FirstOrDefault(r => !IsDoublePlayTitle(r.Title))
                                  ?? records[0];
                var candidates = FindCandidates(representative, validTjas);

                if (candidates.Count == 0)
                {
                    foreach (var rec in records)
                        results.Add(CreateUnmatchedResult(rec, records, officialSongKey, outputSongsFolder));
                    continue;
                }

                for (int rank = 0; rank < candidates.Count; rank++)
                {
                    var candidate = candidates[rank];
                    foreach (var rec in records)
                    {
                        results.Add(CreateCandidateResult(
                            rec, records, officialSongKey, candidate, rank, outputSongsFolder));
                    }
                }
            }

            MarkDuplicateConflicts(results);

            int ok = results.Count(r => r.Status == MatchStatus.OK);
            int warn = results.Count(r => r.Status == MatchStatus.Warning);
            int unmatched = results.Count(r => r.Status == MatchStatus.Unmatched);
            int dup = results.Count(r => r.Status == MatchStatus.Duplicate);

            log?.Invoke($"[INFO] 照合結果: OK={ok} / Warning={warn} / Unmatched={unmatched} / Duplicate={dup}");

            return results;
        }

        private static List<MatchCandidate> FindCandidates(
            OfficialSongRecord rec,
            IReadOnlyList<IndexedTja> tjas)
        {
            var bestByPath = new Dictionary<string, MatchCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (var indexed in tjas)
            {
                var candidate = EvaluateCandidate(rec, indexed);
                if (candidate == null) continue;

                if (!bestByPath.TryGetValue(candidate.Tja.FilePath, out var current)
                    || candidate.SortPriority < current.SortPriority
                    || (candidate.SortPriority == current.SortPriority
                        && candidate.VersionPriority < current.VersionPriority))
                {
                    bestByPath[candidate.Tja.FilePath] = candidate;
                }
            }

            return bestByPath.Values
                .OrderBy(c => c.SortPriority)
                .ThenBy(c => c.VersionPriority)
                .ThenBy(c => c.SourceIndex)
                .ThenBy(c => c.Tja.Title, StringComparer.Ordinal)
                .ThenBy(c => c.Tja.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static MatchCandidate? EvaluateCandidate(OfficialSongRecord rec, IndexedTja indexed)
        {
            var tja = indexed.Tja;
            string officialTitle = NormalizeOfficialTitle(rec.Title);
            string officialTitleForSuffix = NormalizeOfficialTitleForSuffixMatch(rec.Title);
            string officialSubtitle = TitleNormalizer.NormalizeSubtitle(rec.Subtitle);
            string officialCombined = NormalizeCombinedOfficialTitle(rec.Title, rec.Subtitle);

            string tjaTitle = TitleNormalizer.NormalizeTitle(tja.Title);
            string tjaTitleForSuffix = TitleNormalizer.NormalizeTitleForOfficialMatch(tja.Title);
            string tjaSubtitle = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);
            string tjaSubtitleAsTitle = NormalizeSubtitleAsTitleReference(tja.Subtitle);

            if (tjaTitle == officialTitle && tjaSubtitle == officialSubtitle)
            {
                return CreateCandidate(
                    tja, indexed.Index, MatchMethod.NormalizedTitleNormalizedSubtitle,
                    MatchStatus.OK, 0, "");
            }

            if (tjaTitleForSuffix == officialTitleForSuffix && tjaSubtitle == officialSubtitle)
            {
                return CreateCandidate(
                    tja, indexed.Index, MatchMethod.OfficialVersionSuffix,
                    MatchStatus.OK, 10, GetVersionPriority(tja.Title),
                    "音源・バージョン接尾辞を除去して一致");
            }

            if (!string.IsNullOrEmpty(officialSubtitle)
                && tjaTitleForSuffix == officialSubtitle
                && (tjaSubtitleAsTitle == officialTitleForSuffix
                    || (!string.IsNullOrEmpty(tjaSubtitle)
                        && tjaSubtitle.Contains(officialTitleForSuffix, StringComparison.Ordinal))))
            {
                return CreateCandidate(
                    tja, indexed.Index, MatchMethod.SwappedTitleSubtitle,
                    MatchStatus.OK, 20, GetVersionPriority(tja.Title),
                    "TITLE/SUBTITLEの割り当て違いを救済");
            }

            if (!string.IsNullOrEmpty(officialSubtitle)
                && tjaTitleForSuffix == officialCombined)
            {
                return CreateCandidate(
                    tja, indexed.Index, MatchMethod.CombinedTitleSubtitle,
                    MatchStatus.OK, 30, GetVersionPriority(tja.Title),
                    "公式TITLEとSUBTITLEを結合したタイトルとして一致");
            }

            if (tjaTitle == officialTitle || tjaTitleForSuffix == officialTitleForSuffix)
            {
                return CreateCandidate(
                    tja, indexed.Index, MatchMethod.LooseTitle,
                    MatchStatus.Warning, 90, GetVersionPriority(tja.Title),
                    $"SUBTITLEが異なります (公式:'{rec.Subtitle}' TJA:'{tja.Subtitle}')");
            }

            return null;
        }

        private static MatchCandidate CreateCandidate(
            TjaInfo tja,
            int sourceIndex,
            MatchMethod method,
            MatchStatus status,
            int sortPriority,
            string message)
            => CreateCandidate(tja, sourceIndex, method, status, sortPriority, 0, message);

        private static MatchCandidate CreateCandidate(
            TjaInfo tja,
            int sourceIndex,
            MatchMethod method,
            MatchStatus status,
            int sortPriority,
            int versionPriority,
            string message)
        {
            return new MatchCandidate
            {
                Tja = tja,
                SourceIndex = sourceIndex,
                Method = method,
                Status = status,
                SortPriority = sortPriority,
                VersionPriority = versionPriority,
                Message = message
            };
        }

        private static OfficialMatchResult CreateCandidateResult(
            OfficialSongRecord rec,
            IReadOnlyList<OfficialSongRecord> groupRecords,
            string officialSongKey,
            MatchCandidate candidate,
            int candidateRank,
            string outputSongsFolder)
        {
            var result = CreateBaseResult(rec, groupRecords, officialSongKey, outputSongsFolder);
            bool isDoublePlay = IsDoublePlayTitle(rec.Title);

            result.TjaTitle = candidate.Tja.Title;
            result.TjaSubtitle = candidate.Tja.Subtitle;
            result.TjaPath = candidate.Tja.FilePath;
            result.CandidateKey = candidate.Tja.FilePath;
            result.CandidateRank = candidateRank;
            result.Status = candidate.Status;
            result.MatchMethodText = isDoublePlay
                ? MatchMethod.DoublePlaySet.ToString()
                : candidate.Method.ToString();
            result.Message = isDoublePlay
                ? AppendMessage(candidate.Message, "通常曲TJAに同梱される双打枠として紐づけ")
                : candidate.Message;
            result.OutputTarget = candidateRank == 0
                ? OfficialOutputTarget.Official
                : OfficialOutputTarget.Unclassified;
            result.IsOutputTargetSelectable = true;

            return result;
        }

        private static OfficialMatchResult CreateUnmatchedResult(
            OfficialSongRecord rec,
            IReadOnlyList<OfficialSongRecord> groupRecords,
            string officialSongKey,
            string outputSongsFolder)
        {
            var result = CreateBaseResult(rec, groupRecords, officialSongKey, outputSongsFolder);
            result.Status = MatchStatus.Unmatched;
            result.MatchMethodText = MatchMethod.Unmatched.ToString();
            result.Message = IsDoublePlayTitle(rec.Title)
                ? "対応する通常曲TJAが見つかりませんでした"
                : "対応するTJAが見つかりませんでした";
            result.OutputTarget = OfficialOutputTarget.Unclassified;
            result.IsOutputTargetSelectable = false;
            return result;
        }

        private static OfficialMatchResult CreateBaseResult(
            OfficialSongRecord rec,
            IReadOnlyList<OfficialSongRecord> groupRecords,
            string officialSongKey,
            string outputSongsFolder)
        {
            int sortOrder = rec.Order;
            int sortSubOrder = 0;

            if (TryGetDoublePlayBaseTitle(rec.Title, out _))
            {
                sortSubOrder = 1;
                var baseRecord = groupRecords
                    .Where(r => string.Equals(r.Genre, rec.Genre, StringComparison.Ordinal)
                             && !IsDoublePlayTitle(r.Title))
                    .OrderBy(r => r.Order)
                    .FirstOrDefault();
                if (baseRecord != null)
                    sortOrder = baseRecord.Order;
            }

            var category = OfficialSongListService.Categories
                .FirstOrDefault(c => c.DisplayName == rec.Genre);
            string folderPrefix = category.FolderPrefix ?? rec.Genre;
            string destStfdb = !string.IsNullOrEmpty(outputSongsFolder)
                ? Path.Combine(outputSongsFolder, folderPrefix, "official.stfdb")
                : "";

            return new OfficialMatchResult
            {
                OfficialGenre = rec.Genre,
                OfficialOrder = rec.Order,
                OfficialSortOrder = sortOrder,
                OfficialSortSubOrder = sortSubOrder,
                OfficialTitle = rec.Title,
                OfficialSubtitle = rec.Subtitle,
                DestinationStfdb = destStfdb,
                OfficialSongKey = officialSongKey
            };
        }

        private static void MarkDuplicateConflicts(IReadOnlyList<OfficialMatchResult> results)
        {
            var selected = results
                .Where(r => r.OutputTarget == OfficialOutputTarget.Official
                         && !string.IsNullOrEmpty(r.TjaPath)
                         && (r.Status == MatchStatus.OK || r.Status == MatchStatus.Warning))
                .GroupBy(r => r.TjaPath, StringComparer.OrdinalIgnoreCase);

            foreach (var pathGroup in selected)
            {
                var distinctSongKeys = pathGroup
                    .Select(r => r.OfficialSongKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (distinctSongKeys.Count <= 1)
                    continue;

                foreach (var result in pathGroup)
                {
                    result.Status = MatchStatus.Duplicate;
                    result.MatchMethodText = MatchMethod.Duplicate.ToString();
                    result.Message = "同じTJAが別の公式曲グループにも公式候補として選択されています";
                }
            }
        }

        private static string GetOfficialSongKey(OfficialSongRecord rec)
        {
            string title = NormalizeOfficialTitleForSuffixMatch(rec.Title);
            string subtitle = TitleNormalizer.NormalizeSubtitle(rec.Subtitle);
            return $"{title}|{subtitle}";
        }

        private static string NormalizeOfficialTitle(string title)
            => TitleNormalizer.NormalizeTitle(RemoveDoublePlayPrefix(title));

        private static string NormalizeOfficialTitleForSuffixMatch(string title)
            => TitleNormalizer.NormalizeTitleForOfficialMatch(RemoveDoublePlayPrefix(title));

        private static string NormalizeCombinedOfficialTitle(string title, string subtitle)
        {
            if (string.IsNullOrWhiteSpace(subtitle)) return string.Empty;
            return TitleNormalizer.NormalizeTitleForOfficialMatch(
                $"{RemoveDoublePlayPrefix(title)} {subtitle}");
        }

        private static string NormalizeSubtitleAsTitleReference(string subtitle)
        {
            if (string.IsNullOrWhiteSpace(subtitle)) return string.Empty;

            string work = subtitle.TrimStart().TrimStart('-').TrimStart('+').Trim();
            var quoted = Regex.Match(work, @"[「『](.*?)[」』]\s*より");
            if (quoted.Success)
                work = quoted.Groups[1].Value;
            else
            {
                work = Regex.Replace(work, @"\s*より\s*$", "");
                work = Regex.Replace(work, @"\s*から\s*$", "");
            }

            return TitleNormalizer.NormalizeTitleForOfficialMatch(work);
        }

        private static bool IsDoublePlayTitle(string title)
            => TryGetDoublePlayBaseTitle(title, out _);

        private static string RemoveDoublePlayPrefix(string title)
            => TryGetDoublePlayBaseTitle(title, out var baseTitle) ? baseTitle : title;

        private static bool TryGetDoublePlayBaseTitle(string title, out string baseTitle)
        {
            baseTitle = title;
            if (string.IsNullOrWhiteSpace(title)) return false;

            string work = title.Trim();
            string stripped = Regex.Replace(
                work,
                @"^\s*(?:【\s*双打\s*】|［\s*双打\s*］|\[\s*双打\s*\]|\(\s*双打\s*\)|（\s*双打\s*）)\s*",
                "",
                RegexOptions.CultureInvariant);

            if (string.Equals(stripped, work, StringComparison.Ordinal))
                return false;

            baseTitle = stripped.Trim();
            return !string.IsNullOrWhiteSpace(baseTitle);
        }

        private static int GetVersionPriority(string title)
        {
            string normalized = TitleNormalizer.NormalizeTitle(title);

            if (normalized.Contains("本家版", StringComparison.Ordinal)
                || normalized.Contains("ORIGINALVERSION", StringComparison.Ordinal)
                || normalized.Contains("NEWAUDIO", StringComparison.Ordinal)
                || normalized.Contains("新曲", StringComparison.Ordinal))
                return 0;

            if (normalized.Contains("カバー版", StringComparison.Ordinal)
                || normalized.Contains("COVERVERSION", StringComparison.Ordinal))
                return 20;

            if (normalized.Contains("OLDAUDIO", StringComparison.Ordinal)
                || normalized.Contains("BEENAVERSION", StringComparison.Ordinal)
                || normalized.Contains("旧曲", StringComparison.Ordinal))
                return 30;

            return 10;
        }

        private static string AppendMessage(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return $"{first} / {second}";
        }

        // --- 内部クラス ---

        private sealed record IndexedTja(TjaInfo Tja, int Index);

        private sealed class MatchCandidate
        {
            public TjaInfo Tja { get; set; } = null!;
            public int SourceIndex { get; set; }
            public MatchMethod Method { get; set; }
            public MatchStatus Status { get; set; }
            public int SortPriority { get; set; }
            public int VersionPriority { get; set; }
            public string Message { get; set; } = "";
        }

        private class TupleStringComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y)
                => string.Equals(x.Item1, y.Item1, StringComparison.Ordinal)
                && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

            public int GetHashCode((string, string) obj)
                => HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(obj.Item1),
                    StringComparer.Ordinal.GetHashCode(obj.Item2));
        }
    }

    /// <summary>
    /// TJAファイルから読み取ったヘッダー情報
    /// </summary>
    public class TjaInfo
    {
        public string FilePath { get; set; } = "";
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string RawSubtitle { get; set; } = "";
        public string Genre { get; set; } = "";
        public string ParseError { get; set; } = "";
        public bool HasParseError => !string.IsNullOrEmpty(ParseError);
    }
}
