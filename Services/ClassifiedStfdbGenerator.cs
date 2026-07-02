using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ST_Fumen_Manager_WPF.Models;
using static ST_Fumen_Manager_WPF.Services.SongClassificationService;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// 公式Database / サヨナラ曲 / CS版限定候補 の分類に基づいて STFDB を生成するサービス。
    /// </summary>
    public static class ClassifiedStfdbGenerator
    {
        // --- 追加ジャンルのカラー定義 (box.def 用) ---

        private static readonly Dictionary<string, (string BgColor, string TextColor, string Explanation)> ExtraGenreColors =
            new(StringComparer.Ordinal)
            {
                ["サヨナラ曲"] = ("#666666", "#ffffff", "サヨナラ曲をあつめたよ!"),
                ["CS版限定候補"] = ("#7b3fa0", "#ffffff", "CS版限定候補の曲をあつめたよ!"),
            };

        /// <summary>
        /// 分類に基づいて STFDB を生成する。必要なキャッシュが空の場合は自動取得する。
        /// </summary>
        public static async Task<int> GenerateAllAsync(
            IList<OfficialMatchResult> officialResults,
            string outputSongsFolder,
            bool includeWarnings,
            IReadOnlyCollection<TjaInfo> tjaInfos,
            bool generateGoodbyeFolder,
            bool generateConsumerCandidateFolder,
            Action<string> log,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(outputSongsFolder))
            {
                log("[ERROR] 出力Songsフォルダが指定されていません。");
                return 0;
            }

            // 1. 公式Database主導の STFDB を生成
            int generated = OfficialStfdbGenerator.GenerateAll(
                officialResults, outputSongsFolder, includeWarnings, log);

            if (!generateGoodbyeFolder && !generateConsumerCandidateFolder)
                return generated;

            // キャッシュ読み込み（空なら自動取得）
            var goodbyeSongs = GoodbyeSongListService.LoadFromCache();
            if (generateGoodbyeFolder && goodbyeSongs.Count == 0)
            {
                log("[INFO] サヨナラ曲キャッシュがありません。自動取得します...");
                goodbyeSongs = await GoodbyeSongListService.FetchAndSaveAsync(log, ct);
            }

            var consumerSongs = ConsumerSongListService.LoadFromCache();
            if (generateConsumerCandidateFolder && consumerSongs.Count == 0)
            {
                log("[INFO] CS版曲キャッシュがありません。自動取得します...");
                consumerSongs = await ConsumerSongListService.FetchAndSaveAsync(
                    ConsumerSongListService.DefaultWorks, log, ct);
            }

            // マッチ済みTJAパスの集合
            var matchedTjaPaths = new HashSet<string>(
                officialResults
                    .Where(r => r.Status == MatchStatus.OK
                             || (includeWarnings && r.Status == MatchStatus.Warning))
                    .Select(r => r.TjaPath)
                    .Where(p => !string.IsNullOrEmpty(p)),
                StringComparer.OrdinalIgnoreCase);

            // 未マッチのTJAを分類
            var unmatchedTjaInfos = tjaInfos
                .Where(t => !t.HasParseError && !matchedTjaPaths.Contains(t.FilePath))
                .ToList();

            var officialSongs = OfficialSongListService.LoadFromCache();
            var classified = ClassifyAll(unmatchedTjaInfos, officialSongs, goodbyeSongs, consumerSongs);

            // 2. サヨナラ曲フォルダ
            if (generateGoodbyeFolder)
            {
                var goodbyePaths = classified
                    .Where(kv => kv.Value.Kind == Classification.Goodbye)
                    .Select(kv => kv.Key)
                    .ToList();

                if (goodbyePaths.Count > 0)
                {
                    generated += GenerateExtraFolder(
                        outputSongsFolder, "08 サヨナラ曲", "サヨナラ曲", goodbyePaths, log);
                }
                else
                {
                    log("[SKIP] サヨナラ曲: 該当なし");
                }
            }

            // 3. CS版限定候補フォルダ
            if (generateConsumerCandidateFolder)
            {
                var consumerPaths = classified
                    .Where(kv => kv.Value.Kind == Classification.ConsumerCandidate)
                    .Select(kv => kv.Key)
                    .ToList();

                if (consumerPaths.Count > 0)
                {
                    generated += GenerateExtraFolder(
                        outputSongsFolder, "09 CS版限定候補", "CS版限定候補", consumerPaths, log);
                }
                else
                {
                    log("[SKIP] CS版限定候補: 該当なし");
                }
            }

            // Unknown のログ出力
            int unknownCount = classified.Count(kv => kv.Value.Kind == Classification.Unknown);
            if (unknownCount > 0)
            {
                log($"[INFO] 未分類TJA: {unknownCount} 件");
            }

            return generated;
        }

        private static int GenerateExtraFolder(
            string outputSongsFolder,
            string folderPrefix,
            string genre,
            List<string> tjaPaths,
            Action<string> log)
        {
            string genreFolder = Path.Combine(outputSongsFolder, folderPrefix);
            string stfdbPath = Path.Combine(genreFolder, "official.stfdb");

            try
            {
                Directory.CreateDirectory(genreFolder);

                // .bak 作成
                if (File.Exists(stfdbPath))
                {
                    string bakPath = stfdbPath + ".bak";
                    File.Copy(stfdbPath, bakPath, true);
                    log($"[BAK] バックアップ作成: {bakPath}");
                }

                var lines = tjaPaths
                    .Select(path => StfdbService.MakeRelativeTjaPath(stfdbPath, path))
                    .ToList();

                string content = string.Join("\r\n", lines);
                if (lines.Count > 0) content += "\r\n";

                File.WriteAllText(stfdbPath, content, new UTF8Encoding(false));
                log($"[OK] {genre}: {lines.Count} 曲 → {stfdbPath}");

                GenerateBoxDefIfNotExists(genreFolder, genre, log);
                return 1;
            }
            catch (Exception ex)
            {
                log($"[ERROR] {genre} の生成に失敗しました: {ex.Message}");
                return 0;
            }
        }

        private static void GenerateBoxDefIfNotExists(string genreFolder, string genre, Action<string> log)
        {
            string boxDefPath = Path.Combine(genreFolder, "box.def");
            if (File.Exists(boxDefPath))
            {
                log($"[SKIP] box.def は既に存在します (上書きしません): {boxDefPath}");
                return;
            }

            try
            {
                ExtraGenreColors.TryGetValue(genre, out var colors);
                string bgColor = colors.BgColor ?? "#333333";
                string textColor = colors.TextColor ?? "#ffffff";
                string explanation = colors.Explanation ?? $"{genre}の曲をあつめたよ!";

                string content =
                    $"#TITLE:{genre}\r\n" +
                    $"#GENRE:{genre}\r\n" +
                    $"#EXPLANATION:{explanation}\r\n" +
                    $"#BGCOLOR:{bgColor}\r\n" +
                    $"#TEXTCOLOR:{textColor}\r\n";

                File.WriteAllText(boxDefPath, content, Encoding.GetEncoding("shift_jis"));
                log($"[OK] box.def を生成しました: {boxDefPath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] box.def の生成に失敗しました: {ex.Message}");
            }
        }
    }
}
