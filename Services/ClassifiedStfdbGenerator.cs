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
    /// 公式Database / サヨナラ曲 / CS限定楽曲 の分類に基づいて STFDB を生成するサービス。
    /// </summary>
    public static class ClassifiedStfdbGenerator
    {
        // --- 追加ジャンルのカラー定義 (box.def 用) ---

        private const string ReferenceSongsFolder = @"S:\Songs";
        private const string OtherGenreFolderPrefix = "99 その他";
        private const string OtherGenreName = "その他";
        private const string ConsumerGenreName = "CS限定楽曲";
        private const string OtherBoxDefTemplatePath = @"S:\SongTest\10 サヨナラ曲ほか\99 その他\box.def";

        private static readonly Dictionary<string, string> ExtraBoxDefTemplates =
            new(StringComparer.Ordinal)
            {
                ["サヨナラ曲"] = @"S:\SongTest\08 サヨナラ曲\box.def",
                [ConsumerGenreName] = @"S:\SongTest\09 CS限定楽曲\box.def",
                ["サヨナラ曲ほか"] = @"S:\SongTest\10 サヨナラ曲ほか\box.def",
            };

        private static readonly Dictionary<string, (string BgColor, string TextColor, string Explanation)> ExtraGenreColors =
            new(StringComparer.Ordinal)
            {
                ["サヨナラ曲"] = ("#666666", "#ffffff", "サヨナラ曲をあつめたよ!"),
                [ConsumerGenreName] = ("#7b3fa0", "#ffffff", "CS限定楽曲をあつめたよ!"),
                ["サヨナラ曲ほか"] = ("#333333", "#ffffff", "サヨナラ曲ほかをあつめたよ!"),
                [OtherGenreName] = ("#333333", "#ffffff", "その他の曲をあつめたよ!"),
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

            // キャッシュ読み込み（空なら自動取得）
            var goodbyeSongs = GoodbyeSongListService.LoadFromCache();
            if (generateGoodbyeFolder && (goodbyeSongs.Count == 0 || GoodbyeSongListService.NeedsRefresh()))
            {
                log(goodbyeSongs.Count == 0
                    ? "[INFO] サヨナラ曲キャッシュがありません。自動取得します..."
                    : "[INFO] サヨナラ曲キャッシュが古い形式です。再取得します...");

                var fetchedGoodbyeSongs = await GoodbyeSongListService.FetchAndSaveAsync(log, ct);
                if (fetchedGoodbyeSongs.Count > 0)
                {
                    goodbyeSongs = fetchedGoodbyeSongs;
                }
            }

            var consumerSongs = ConsumerSongListService.LoadFromCache();
            if (generateConsumerCandidateFolder && consumerSongs.Count == 0)
            {
                log("[INFO] CS限定楽曲キャッシュがありません。自動取得します...");
                consumerSongs = await ConsumerSongListService.FetchAndSaveAsync(
                    ConsumerSongListService.DefaultWorks, log, ct);
            }

            // マッチ済みTJAパスの集合
            var matchedTjaPaths = new HashSet<string>(
                officialResults
                    .Where(r => r.OutputTarget == OfficialOutputTarget.Official
                             && (r.Status == MatchStatus.OK
                              || (includeWarnings && r.Status == MatchStatus.Warning)))
                    .Select(r => r.TjaPath)
                    .Where(p => !string.IsNullOrEmpty(p)),
                StringComparer.OrdinalIgnoreCase);

            // ユーザーが「10 未分類」を選んだ候補は、サヨナラ/CS分類より優先して未分類へ流す。
            var forcedUnclassifiedPaths = new HashSet<string>(
                officialResults
                    .Where(r => r.OutputTarget == OfficialOutputTarget.Unclassified
                             && !string.IsNullOrEmpty(r.TjaPath)
                             && !matchedTjaPaths.Contains(r.TjaPath))
                    .Select(r => r.TjaPath),
                StringComparer.OrdinalIgnoreCase);

            // 未マッチの公式曲のリストを作成して保存する
            var unmatchedOfficial = officialResults
                .Where(r => r.Status == MatchStatus.Unmatched)
                .OrderBy(r => r.OfficialGenre)
                .ThenBy(r => r.OfficialOrder)
                .ToList();

            if (unmatchedOfficial.Count > 0)
            {
                string reportPath = Path.Combine(outputSongsFolder, "unmatched_official_songs.txt");
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"=== 未マッチ公式曲リスト ({unmatchedOfficial.Count}件) ===");
                    sb.AppendLine($"生成日時: {DateTime.Now}");
                    sb.AppendLine();
                    foreach (var r in unmatchedOfficial)
                    {
                        sb.AppendLine($"ジャンル: {r.OfficialGenre} | 順番: {r.OfficialOrder} | 曲名: {r.OfficialTitle} | サブタイトル: {r.OfficialSubtitle}");
                    }
                    File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
                    log($"[OK] 未マッチ公式曲のレポートを生成しました: {reportPath}");
                }
                catch (Exception ex)
                {
                    log($"[ERROR] 未マッチ公式曲レポートの生成に失敗しました: {ex.Message}");
                }
            }

            // 未マッチのTJAを分類
            var unmatchedTjaInfos = tjaInfos
                .Where(t => !t.HasParseError
                         && !matchedTjaPaths.Contains(t.FilePath)
                         && !forcedUnclassifiedPaths.Contains(t.FilePath))
                .ToList();

            var officialSongs = OfficialSongListService.LoadFromCache();
            var classified = ClassifyAll(unmatchedTjaInfos, officialSongs, goodbyeSongs, consumerSongs);
            var tjaByPath = tjaInfos
                .Where(t => !string.IsNullOrEmpty(t.FilePath))
                .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var forcedUnclassifiedGenreByPath = officialResults
                .Where(r => r.OutputTarget == OfficialOutputTarget.Unclassified
                         && !string.IsNullOrEmpty(r.TjaPath)
                         && !string.IsNullOrEmpty(r.OfficialGenre))
                .GroupBy(r => r.TjaPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().OfficialGenre, StringComparer.OrdinalIgnoreCase);

            // すべての出力済みTJAパスを追跡
            var writtenTjaPaths = new HashSet<string>(matchedTjaPaths, StringComparer.OrdinalIgnoreCase);

            // 2. サヨナラ曲フォルダ
            if (generateGoodbyeFolder)
            {
                var goodbyeEntries = unmatchedTjaInfos
                    .Where(t => SongClassificationService.TryMatchGoodbye(t, goodbyeSongs, out _))
                    .Select(t =>
                    {
                        SongClassificationService.TryMatchGoodbye(t, goodbyeSongs, out var match);
                        return new ExtraFolderEntry(
                            t.FilePath,
                            ResolveEntryGenre(t.FilePath, match?.Genre, tjaByPath));
                    })
                    .ToList();

                if (goodbyeEntries.Count > 0)
                {
                    generated += GenerateExtraFolderByGenre(
                        outputSongsFolder, "08 サヨナラ曲", "サヨナラ曲", goodbyeEntries, log);
                    foreach (var p in goodbyeEntries.Select(e => e.TjaPath))
                    {
                        writtenTjaPaths.Add(p);
                    }
                }
                else
                {
                    log("[SKIP] サヨナラ曲: 該当なし");
                }
            }

            // 3. CS限定楽曲フォルダ
            if (generateConsumerCandidateFolder)
            {
                var consumerEntries = unmatchedTjaInfos
                    .Where(t => SongClassificationService.TryMatchConsumer(t, consumerSongs, out _))
                    .Select(t =>
                    {
                        SongClassificationService.TryMatchConsumer(t, consumerSongs, out var match);
                        return new ExtraFolderEntry(
                            t.FilePath,
                            ResolveEntryGenre(t.FilePath, match?.Genre, tjaByPath));
                    })
                    .ToList();

                if (consumerEntries.Count > 0)
                {
                    generated += GenerateExtraFolderByGenre(
                        outputSongsFolder, "09 CS限定楽曲", ConsumerGenreName, consumerEntries, log);
                    foreach (var p in consumerEntries.Select(e => e.TjaPath))
                    {
                        writtenTjaPaths.Add(p);
                    }
                }
                else
                {
                    log("[SKIP] CS限定楽曲: 該当なし");
                }
            }

            // 4. 未分類（その他）のTJAファイルを「10 サヨナラ曲ほか」フォルダに出力
            var remainingPaths = tjaInfos
                .Where(t => !t.HasParseError && !writtenTjaPaths.Contains(t.FilePath))
                .Select(t => t.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (remainingPaths.Count > 0)
            {
                var remainingEntries = remainingPaths
                    .Select(path => new ExtraFolderEntry(
                        path,
                        GetRemainingGenre(path, forcedUnclassifiedGenreByPath, classified, tjaByPath)))
                    .ToList();

                generated += GenerateExtraFolderByGenre(
                    outputSongsFolder, "10 サヨナラ曲ほか", "サヨナラ曲ほか", remainingEntries, log);
                log($"[INFO] サヨナラ曲ほかTJA: {remainingPaths.Count} 件を「10 サヨナラ曲ほか」に出力しました");
            }
            else
            {
                log("[SKIP] サヨナラ曲ほか: 該当なし");
            }

            return generated;
        }

        private static string GetRemainingGenre(
            string path,
            IReadOnlyDictionary<string, string> forcedUnclassifiedGenreByPath,
            IReadOnlyDictionary<string, Result> classified,
            IReadOnlyDictionary<string, TjaInfo> tjaByPath)
        {
            if (forcedUnclassifiedGenreByPath.TryGetValue(path, out var officialGenre)
                && !string.IsNullOrWhiteSpace(officialGenre))
            {
                return ResolveEntryGenre(path, officialGenre, tjaByPath);
            }

            if (classified.TryGetValue(path, out var classification)
                && !string.IsNullOrWhiteSpace(classification.Genre))
            {
                return ResolveEntryGenre(path, classification.Genre, tjaByPath);
            }

            return ResolveEntryGenre(path, null, tjaByPath);
        }

        private static string ResolveEntryGenre(
            string path,
            string? databaseGenre,
            IReadOnlyDictionary<string, TjaInfo> tjaByPath)
        {
            if (!string.IsNullOrWhiteSpace(databaseGenre))
            {
                return databaseGenre;
            }

            string pathGenre = GetGenreFromTjaPath(path);
            if (!string.IsNullOrWhiteSpace(pathGenre))
            {
                return pathGenre;
            }

            if (tjaByPath.TryGetValue(path, out var tja)
                && !string.IsNullOrWhiteSpace(tja.Genre))
            {
                return tja.Genre;
            }

            return OtherGenreName;
        }

        private static string GetGenreFromTjaPath(string tjaPath)
        {
            try
            {
                var songFolder = Directory.GetParent(tjaPath);
                var genreFolder = songFolder?.Parent;
                return genreFolder?.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private sealed class ExtraFolderEntry
        {
            public ExtraFolderEntry(string tjaPath, string genre)
            {
                TjaPath = tjaPath;
                Genre = genre;
            }

            public string TjaPath { get; }
            public string Genre { get; }
        }

        private static int GenerateExtraFolderByGenre(
            string outputSongsFolder,
            string parentFolderPrefix,
            string parentGenre,
            List<ExtraFolderEntry> entries,
            Action<string> log)
        {
            string parentFolder = Path.Combine(outputSongsFolder, parentFolderPrefix);
            try
            {
                Directory.CreateDirectory(parentFolder);
                GenerateExtraBoxDef(parentFolder, parentGenre, log);

                int generated = 0;
                foreach (var group in entries
                    .GroupBy(e => GetGenreFolderPrefix(e.Genre), StringComparer.Ordinal)
                    .OrderBy(g => GetGenreSortOrder(g.Key)))
                {
                    string folderPrefix = group.Key;
                    string genre = GetGenreDisplayName(folderPrefix);
                    string genreFolder = Path.Combine(parentFolder, folderPrefix);
                    string stfdbPath = Path.Combine(genreFolder, "official.stfdb");

                    Directory.CreateDirectory(genreFolder);
                    CopyReferenceGenreBoxDefIfPossible(genreFolder, folderPrefix, log);

                    if (File.Exists(stfdbPath))
                    {
                        string bakPath = stfdbPath + ".bak";
                        File.Copy(stfdbPath, bakPath, true);
                        log($"[BAK] バックアップ作成: {bakPath}");
                    }

                    var lines = group
                        .Select(e => e.TjaPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path => StfdbService.MakeRelativeTjaPath(stfdbPath, path))
                        .ToList();

                    string content = string.Join("\r\n", lines);
                    if (lines.Count > 0) content += "\r\n";

                    File.WriteAllText(stfdbPath, content, new UTF8Encoding(false));
                    log($"[OK] {parentGenre}/{genre}: {lines.Count} 曲 → {stfdbPath}");
                    generated++;
                }

                return generated;
            }
            catch (Exception ex)
            {
                log($"[ERROR] {parentGenre} の生成に失敗しました: {ex.Message}");
                return 0;
            }
        }

        private static string GetGenreFolderPrefix(string genre)
        {
            string normalized = NormalizeGenreName(genre);
            if (string.IsNullOrEmpty(normalized))
                return OtherGenreFolderPrefix;

            var category = OfficialSongListService.Categories
                .FirstOrDefault(c => NormalizeGenreName(c.DisplayName) == normalized);
            if (!string.IsNullOrEmpty(category.FolderPrefix))
                return category.FolderPrefix;

            return OtherGenreFolderPrefix;
        }

        private static string GetGenreDisplayName(string folderPrefix)
        {
            if (folderPrefix == OtherGenreFolderPrefix)
                return OtherGenreName;

            var category = OfficialSongListService.Categories
                .FirstOrDefault(c => string.Equals(c.FolderPrefix, folderPrefix, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(category.DisplayName))
                return NormalizeGenreName(category.DisplayName);

            int spaceIndex = folderPrefix.IndexOf(' ');
            return spaceIndex >= 0 && spaceIndex + 1 < folderPrefix.Length
                ? folderPrefix[(spaceIndex + 1)..]
                : folderPrefix;
        }

        private static int GetGenreSortOrder(string folderPrefix)
        {
            int spaceIndex = folderPrefix.IndexOf(' ');
            string prefixNumber = spaceIndex > 0 ? folderPrefix[..spaceIndex] : folderPrefix;
            return int.TryParse(prefixNumber, out int order) ? order : int.MaxValue;
        }

        private static string NormalizeGenreName(string genre)
        {
            string normalized = (genre ?? "").Trim();
            normalized = normalized.Replace("\r", "").Replace("\n", "");
            normalized = RemoveGenreFolderNumber(normalized);

            return normalized switch
            {
                "J-POP" or "POP" or "POPS" or "JPOP" or "Pop" => "ポップス",
                "Kids" or "Kids'" or "Children" or "Children & Folk" or "Children and Folk" or "どうよう" => "キッズ",
                "Anime" => "アニメ",
                "ボーカロイド™曲" or "ボーカロイド™" or "VOCALOID" or "Vocaloid" => "ボーカロイド",
                "Game Music" or "ゲームバラエティ" => "ゲームミュージック",
                "Variety" => "バラエティ",
                "Classical" or "Classic" => "クラシック",
                "NAMCO Original" or "Namco Original" or "Namco" or "Namco Originals" => "ナムコオリジナル",
                _ => normalized
            };
        }

        private static string RemoveGenreFolderNumber(string genre)
        {
            int spaceIndex = genre.IndexOf(' ');
            if (spaceIndex <= 0) return genre;

            string prefix = genre[..spaceIndex];
            if (!prefix.All(char.IsDigit)) return genre;

            return genre[(spaceIndex + 1)..].Trim();
        }

        private static void CopyReferenceGenreBoxDefIfPossible(string genreFolder, string folderPrefix, Action<string> log)
        {
            if (folderPrefix == OtherGenreFolderPrefix)
            {
                CopyBoxDefTemplateOrFallback(genreFolder, OtherGenreName, OtherBoxDefTemplatePath, log);
                return;
            }

            string referenceBoxDefPath = Path.Combine(ReferenceSongsFolder, folderPrefix, "box.def");
            string boxDefPath = Path.Combine(genreFolder, "box.def");

            try
            {
                if (!File.Exists(referenceBoxDefPath))
                {
                    GenerateExtraBoxDef(genreFolder, GetGenreDisplayName(folderPrefix), log);
                    log($"[WARN] 参照元 box.def が見つからないため簡易 box.def を使用しました: {referenceBoxDefPath}");
                    return;
                }

                if (File.Exists(boxDefPath))
                {
                    string bakPath = boxDefPath + ".bak";
                    File.Copy(boxDefPath, bakPath, true);
                    log($"[BAK] バックアップ作成: {bakPath}");
                }

                File.Copy(referenceBoxDefPath, boxDefPath, true);
                log($"[OK] ジャンル box.def を参照元からコピーしました: {referenceBoxDefPath} → {boxDefPath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] ジャンル box.def の生成に失敗しました: {ex.Message}");
            }
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

                GenerateExtraBoxDef(genreFolder, genre, log);
                return 1;
            }
            catch (Exception ex)
            {
                log($"[ERROR] {genre} の生成に失敗しました: {ex.Message}");
                return 0;
            }
        }

        private static void GenerateExtraBoxDef(string genreFolder, string genre, Action<string> log)
        {
            if (ExtraBoxDefTemplates.TryGetValue(genre, out var templatePath))
            {
                CopyBoxDefTemplateOrFallback(genreFolder, genre, templatePath, log);
                return;
            }

            GenerateBoxDefIfNotExists(genreFolder, genre, log);
        }

        private static void CopyBoxDefTemplateOrFallback(
            string genreFolder,
            string genre,
            string templatePath,
            Action<string> log)
        {
            string boxDefPath = Path.Combine(genreFolder, "box.def");

            try
            {
                if (!File.Exists(templatePath))
                {
                    GenerateBoxDefIfNotExists(genreFolder, genre, log);
                    log($"[WARN] 既定 box.def が見つからないため簡易 box.def を使用しました: {templatePath}");
                    return;
                }

                string sourceFullPath = Path.GetFullPath(templatePath);
                string targetFullPath = Path.GetFullPath(boxDefPath);
                if (string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    log($"[SKIP] 既定 box.def は出力先と同一です: {boxDefPath}");
                    return;
                }

                if (File.Exists(boxDefPath))
                {
                    string bakPath = boxDefPath + ".bak";
                    File.Copy(boxDefPath, bakPath, true);
                    log($"[BAK] バックアップ作成: {bakPath}");
                }

                File.Copy(templatePath, boxDefPath, true);
                log($"[OK] 既定 box.def をコピーしました: {templatePath} → {boxDefPath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] 既定 box.def の生成に失敗しました: {ex.Message}");
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
