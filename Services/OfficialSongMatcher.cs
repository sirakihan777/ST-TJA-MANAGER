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

            // サブタイトルの "--" / "++" 接頭辞を除去
            string subtitle = rawSub.TrimStart().TrimStart('-').TrimStart('+').Trim();

            return new TjaInfo
            {
                FilePath = filePath,
                Title = title.Trim(),
                Subtitle = subtitle,
                RawSubtitle = rawSub.Trim()
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

            // TJA側の正規化インデックスを構築
            // key: (normTitle, normSubtitle) → List<TjaInfo>
            var tjaByKey = new Dictionary<(string, string), List<TjaInfo>>(
                new TupleStringComparer());
            var tjaByTitleOnly = new Dictionary<string, List<TjaInfo>>(StringComparer.Ordinal);

            foreach (var tja in tjaInfos)
            {
                if (tja.HasParseError) continue;

                string nt = TitleNormalizer.NormalizeTitle(tja.Title);
                string ns = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);
                var key = (nt, ns);

                if (!tjaByKey.TryGetValue(key, out var list))
                {
                    list = new List<TjaInfo>();
                    tjaByKey[key] = list;
                }
                list.Add(tja);

                if (!tjaByTitleOnly.TryGetValue(nt, out var listT))
                {
                    listT = new List<TjaInfo>();
                    tjaByTitleOnly[nt] = listT;
                }
                listT.Add(tja);
            }

            // マッチ済みTJAの追跡 (Duplicate検出用)
            var matchedTjaPaths = new Dictionary<string, OfficialSongRecord>(StringComparer.OrdinalIgnoreCase);

            // Database主導でループ
            foreach (var rec in database)
            {
                var result = MatchOne(rec, tjaByKey, tjaByTitleOnly, outputSongsFolder);

                // Duplicate チェック
                if (result.Status == MatchStatus.OK || result.Status == MatchStatus.Warning)
                {
                    if (!string.IsNullOrEmpty(result.TjaPath))
                    {
                        if (matchedTjaPaths.TryGetValue(result.TjaPath, out var prevRec))
                        {
                            result.Status = MatchStatus.Duplicate;
                            result.MatchMethodText = MatchMethod.Duplicate.ToString();
                            result.Message = $"このTJAは既に「{prevRec.Title}」にマッチしています";
                        }
                        else
                        {
                            matchedTjaPaths[result.TjaPath] = rec;
                        }
                    }
                }

                results.Add(result);
            }

            int ok = results.Count(r => r.Status == MatchStatus.OK);
            int warn = results.Count(r => r.Status == MatchStatus.Warning);
            int unmatched = results.Count(r => r.Status == MatchStatus.Unmatched);
            int dup = results.Count(r => r.Status == MatchStatus.Duplicate);

            log?.Invoke($"[INFO] 照合結果: OK={ok} / Warning={warn} / Unmatched={unmatched} / Duplicate={dup}");

            return results;
        }

        private static OfficialMatchResult MatchOne(
            OfficialSongRecord rec,
            Dictionary<(string, string), List<TjaInfo>> tjaByKey,
            Dictionary<string, List<TjaInfo>> tjaByTitleOnly,
            string outputSongsFolder)
        {
            string nt = rec.NormalizedTitle;
            string ns = rec.NormalizedSubtitle;

            var baseResult = new OfficialMatchResult
            {
                OfficialGenre = rec.Genre,
                OfficialOrder = rec.Order,
                OfficialTitle = rec.Title,
                OfficialSubtitle = rec.Subtitle,
            };

            // 出力先STFDBパスを計算
            var category = OfficialSongListService.Categories
                .FirstOrDefault(c => c.DisplayName == rec.Genre);
            string folderPrefix = category.FolderPrefix ?? rec.Genre;
            string destStfdb = !string.IsNullOrEmpty(outputSongsFolder)
                ? Path.Combine(outputSongsFolder, folderPrefix, "official.stfdb")
                : "";
            baseResult.DestinationStfdb = destStfdb;

            // 1. TITLE完全一致 + SUBTITLE完全一致
            {
                var key = (nt, ns);
                if (tjaByKey.TryGetValue(key, out var hits) && hits.Count > 0)
                {
                    var tja = hits[0];
                    baseResult.TjaTitle = tja.Title;
                    baseResult.TjaSubtitle = tja.Subtitle;
                    baseResult.TjaPath = tja.FilePath;
                    baseResult.MatchMethodText = MatchMethod.ExactTitleExactSubtitle.ToString();
                    baseResult.Status = MatchStatus.OK;
                    baseResult.Message = "";
                    return baseResult;
                }
            }

            // 2. TITLE完全一致 + SUBTITLE正規化一致
            {
                // TITLE側は同じ正規化TITLE、SUBTITLE は正規化のみ一致（元文字列不問）
                foreach (var kv in tjaByKey)
                {
                    if (kv.Key.Item1 != nt) continue;
                    if (kv.Key.Item2 == ns && kv.Value.Count > 0)
                    {
                        var tja = kv.Value[0];
                        baseResult.TjaTitle = tja.Title;
                        baseResult.TjaSubtitle = tja.Subtitle;
                        baseResult.TjaPath = tja.FilePath;
                        baseResult.MatchMethodText = MatchMethod.ExactTitleNormalizedSubtitle.ToString();
                        baseResult.Status = MatchStatus.OK;
                        baseResult.Message = "";
                        return baseResult;
                    }
                }
            }

            // 3. TITLE正規化一致 + SUBTITLE正規化一致
            {
                // 既に1/2でチェック済みだが、念のため全ペアをスキャン
                foreach (var kv in tjaByKey)
                {
                    if (kv.Key.Item1 != nt) continue;
                    if (kv.Key.Item2 == ns && kv.Value.Count > 0)
                    {
                        var tja = kv.Value[0];
                        baseResult.TjaTitle = tja.Title;
                        baseResult.TjaSubtitle = tja.Subtitle;
                        baseResult.TjaPath = tja.FilePath;
                        baseResult.MatchMethodText = MatchMethod.NormalizedTitleNormalizedSubtitle.ToString();
                        baseResult.Status = MatchStatus.OK;
                        baseResult.Message = "";
                        return baseResult;
                    }
                }
            }

            // 4. TITLE正規化一致 + SUBTITLE空同士
            if (string.IsNullOrEmpty(ns))
            {
                if (tjaByTitleOnly.TryGetValue(nt, out var titleHits))
                {
                    var emptySubHits = titleHits.Where(t =>
                        string.IsNullOrEmpty(TitleNormalizer.NormalizeSubtitle(t.Subtitle))).ToList();

                    if (emptySubHits.Count > 0)
                    {
                        var tja = emptySubHits[0];
                        baseResult.TjaTitle = tja.Title;
                        baseResult.TjaSubtitle = tja.Subtitle;
                        baseResult.TjaPath = tja.FilePath;
                        baseResult.MatchMethodText = MatchMethod.NormalizedTitleEmptySubtitle.ToString();
                        baseResult.Status = MatchStatus.OK;
                        baseResult.Message = "";
                        return baseResult;
                    }
                }
            }

            // 5. LooseTitle (TITLEのみ一致 / SUBTITLE不一致でも採用、ただしWarning)
            if (tjaByTitleOnly.TryGetValue(nt, out var looseTitleHits) && looseTitleHits.Count > 0)
            {
                // 同TITLEで複数のSUBTITLEが存在する場合はSubtitleMismatch (Warning)
                if (looseTitleHits.Count == 1)
                {
                    var tja = looseTitleHits[0];
                    string tjaSubNorm = TitleNormalizer.NormalizeSubtitle(tja.Subtitle);
                    baseResult.TjaTitle = tja.Title;
                    baseResult.TjaSubtitle = tja.Subtitle;
                    baseResult.TjaPath = tja.FilePath;
                    baseResult.Status = MatchStatus.Warning;

                    if (string.IsNullOrEmpty(ns) != string.IsNullOrEmpty(tjaSubNorm))
                    {
                        baseResult.MatchMethodText = MatchMethod.LooseTitle.ToString();
                        baseResult.Message = $"SUBTITLEが異なります (公式:'{rec.Subtitle}' TJA:'{tja.Subtitle}')";
                    }
                    else
                    {
                        baseResult.MatchMethodText = MatchMethod.SubtitleMismatch.ToString();
                        baseResult.Message = $"SUBTITLEが異なります (公式:'{rec.Subtitle}' TJA:'{tja.Subtitle}')";
                    }
                    return baseResult;
                }
                else
                {
                    // 複数候補 → 最初の候補をWarningで返す
                    var tja = looseTitleHits[0];
                    baseResult.TjaTitle = tja.Title;
                    baseResult.TjaSubtitle = tja.Subtitle;
                    baseResult.TjaPath = tja.FilePath;
                    baseResult.Status = MatchStatus.Warning;
                    baseResult.MatchMethodText = MatchMethod.SubtitleMismatch.ToString();
                    baseResult.Message = $"同TITLEに複数候補 ({looseTitleHits.Count}件)、SUBTITLEで絞り込めませんでした";
                    return baseResult;
                }
            }

            // 未マッチ
            baseResult.Status = MatchStatus.Unmatched;
            baseResult.MatchMethodText = MatchMethod.Unmatched.ToString();
            baseResult.Message = "対応するTJAが見つかりませんでした";
            return baseResult;
        }

        // --- 内部クラス ---

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
        public string ParseError { get; set; } = "";
        public bool HasParseError => !string.IsNullOrEmpty(ParseError);
    }
}
