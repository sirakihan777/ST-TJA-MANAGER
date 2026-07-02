using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// ジャンル別 official.stfdb と box.def を生成するサービス。
    /// 保存前に .bak を必ず作成する。
    /// 既存の box.def は上書きしない。
    /// </summary>
    public static class OfficialStfdbGenerator
    {
        // --- ジャンル別カラー定義 (box.def 用) ---

        private static readonly Dictionary<string, (string BgColor, string TextColor, string Explanation)> GenreColors =
            new(StringComparer.Ordinal)
            {
                ["ポップス"]           = ("#e9388c", "#ffffff", "ポップスの曲をあつめたよ!"),
                ["キッズ"]             = ("#f9a602", "#ffffff", "キッズの曲をあつめたよ!"),
                ["アニメ"]             = ("#4cb4e7", "#ffffff", "アニメの曲をあつめたよ!"),
                ["ボーカロイド™曲"]    = ("#43c267", "#ffffff", "ボーカロイド™曲をあつめたよ!"),
                ["ゲームミュージック"] = ("#e94e1b", "#ffffff", "ゲームミュージックをあつめたよ!"),
                ["バラエティ"]         = ("#f5dc00", "#222222", "バラエティの曲をあつめたよ!"),
                ["クラシック"]         = ("#8b6914", "#ffffff", "クラシックの曲をあつめたよ!"),
                ["ナムコオリジナル"]   = ("#e9191f", "#ffffff", "ナムコオリジナルの曲をあつめたよ!"),
            };

        // --- 公開API ---

        /// <summary>
        /// 照合結果から official.stfdb を生成する。
        /// </summary>
        /// <param name="results">照合結果リスト</param>
        /// <param name="outputSongsFolder">出力先Songsフォルダ</param>
        /// <param name="includeWarnings">Warningも出力するか</param>
        /// <param name="log">ログコールバック</param>
        /// <returns>生成したファイル数</returns>
        public static int GenerateAll(
            IList<OfficialMatchResult> results,
            string outputSongsFolder,
            bool includeWarnings,
            Action<string> log)
        {
            if (string.IsNullOrEmpty(outputSongsFolder))
            {
                log("[ERROR] 出力Songsフォルダが指定されていません。");
                return 0;
            }

            // ジャンルごとにグループ化
            var byGenre = new Dictionary<string, List<OfficialMatchResult>>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                if (!byGenre.TryGetValue(r.OfficialGenre, out var list))
                {
                    list = new List<OfficialMatchResult>();
                    byGenre[r.OfficialGenre] = list;
                }
                list.Add(r);
            }

            int generatedCount = 0;
            foreach (var (category, _, folderPrefix) in OfficialSongListService.Categories)
            {
                if (!byGenre.TryGetValue(category, out var genreResults))
                    continue;

                // 出力対象 (OK のみ、オプションで Warning も含む)
                var toWrite = genreResults
                    .Where(r => r.Status == MatchStatus.OK
                             || (includeWarnings && r.Status == MatchStatus.Warning))
                    .OrderBy(r => r.OfficialOrder)
                    .ToList();

                if (toWrite.Count == 0)
                {
                    log($"[SKIP] {category}: 出力対象なし");
                    continue;
                }

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

                    // パスリスト構築
                    var lines = new List<string>();
                    foreach (var r in toWrite)
                    {
                        // stfdbPath から TJAへの相対パスを計算
                        string relPath = StfdbService.MakeRelativeTjaPath(stfdbPath, r.TjaPath);
                        lines.Add(relPath);
                    }

                    string content = string.Join("\r\n", lines);
                    if (lines.Count > 0) content += "\r\n";

                    File.WriteAllText(stfdbPath, content, new UTF8Encoding(false));
                    log($"[OK] {category}: {lines.Count} 曲 → {stfdbPath}");
                    generatedCount++;

                    // box.def 生成 (存在しない場合のみ)
                    GenerateBoxDefIfNotExists(genreFolder, category, log);
                }
                catch (Exception ex)
                {
                    log($"[ERROR] {category} の生成に失敗しました: {ex.Message}");
                }
            }

            log($"[完了] {generatedCount} ジャンルの official.stfdb を生成しました");
            return generatedCount;
        }

        /// <summary>
        /// 1ジャンルの STFDB 内容をプレビュー用に文字列として返す (ファイル保存なし)
        /// </summary>
        public static string PreviewStfdb(IList<OfficialMatchResult> results, string stfdbPath)
        {
            var sb = new StringBuilder();
            foreach (var r in results
                .Where(r => r.Status == MatchStatus.OK)
                .OrderBy(r => r.OfficialOrder))
            {
                string rel = string.IsNullOrEmpty(stfdbPath)
                    ? r.TjaPath
                    : StfdbService.MakeRelativeTjaPath(stfdbPath, r.TjaPath);
                sb.AppendLine(rel);
            }
            return sb.ToString();
        }

        // --- 内部実装 ---

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
                GenreColors.TryGetValue(genre, out var colors);
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
