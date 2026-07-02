using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// ジャンル別 official.stfdb と box.def を生成するサービス。
    /// 保存前に .bak を必ず作成する。
    /// </summary>
    public static class OfficialStfdbGenerator
    {
        private const string ReferenceSongsFolder = @"S:\Songs";

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
                    .Where(r => r.OutputTarget == OfficialOutputTarget.Official
                             && (r.Status == MatchStatus.OK
                              || (includeWarnings && r.Status == MatchStatus.Warning)))
                    .OrderBy(r => r.OfficialSortOrder)
                    .ThenBy(r => r.OfficialSortSubOrder)
                    .ThenBy(r => r.CandidateRank)
                    .ThenBy(r => r.OfficialOrder)
                    .ToList();

                if (toWrite.Count == 0)
                {
                    log($"[SKIP] {category}: 出力対象なし");
                    continue;
                }

                string genreFolder = Path.Combine(outputSongsFolder, folderPrefix);
                string stfdbPath = Path.Combine(genreFolder, "official.stfdb");
                string metadataPath = stfdbPath + ".stmd";

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
                    if (File.Exists(metadataPath))
                    {
                        string metadataBakPath = metadataPath + ".bak";
                        File.Copy(metadataPath, metadataBakPath, true);
                        log($"[BAK] バックアップ作成: {metadataBakPath}");
                    }

                    // パスリスト構築
                    var lines = new List<string>();
                    var metadataEntries = new List<SirakinTaikoMetadataEntry>();
                    foreach (var r in toWrite)
                    {
                        // stfdbPath から TJAへの相対パスを計算
                        string relPath = StfdbService.MakeRelativeTjaPath(stfdbPath, r.TjaPath);
                        lines.Add(relPath);
                        metadataEntries.Add(new SirakinTaikoMetadataEntry
                        {
                            Path = relPath,
                            Title = r.OfficialTitle,
                            Subtitle = r.OfficialSubtitle,
                            Genre = r.OfficialGenre,
                            Stage = "",
                            Order = r.OfficialOrder
                        });
                    }

                    string content = string.Join("\r\n", lines);
                    if (lines.Count > 0) content += "\r\n";

                    File.WriteAllText(stfdbPath, content, new UTF8Encoding(false));
                    WriteMetadataFile(metadataPath, metadataEntries);
                    log($"[OK] {category}: {lines.Count} 曲 → {stfdbPath}");
                    log($"[OK] {category}: SirakinTaiko metadata → {metadataPath}");
                    generatedCount++;

                    GenerateOfficialBoxDef(genreFolder, folderPrefix, category, log);
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
                .Where(r => r.OutputTarget == OfficialOutputTarget.Official
                         && r.Status == MatchStatus.OK)
                .OrderBy(r => r.OfficialSortOrder)
                .ThenBy(r => r.OfficialSortSubOrder)
                .ThenBy(r => r.CandidateRank)
                .ThenBy(r => r.OfficialOrder))
            {
                string rel = string.IsNullOrEmpty(stfdbPath)
                    ? r.TjaPath
                    : StfdbService.MakeRelativeTjaPath(stfdbPath, r.TjaPath);
                sb.AppendLine(rel);
            }
            return sb.ToString();
        }

        // --- 内部実装 ---

        private static void WriteMetadataFile(string metadataPath, List<SirakinTaikoMetadataEntry> entries)
        {
            var metadata = new SirakinTaikoMetadataFile
            {
                Format = "sirakintaikometadata",
                Version = 1,
                Entries = entries
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(metadata, options);
            File.WriteAllText(metadataPath, json, new UTF8Encoding(false));
        }

        private sealed class SirakinTaikoMetadataFile
        {
            [JsonPropertyName("format")]
            public string Format { get; set; } = "sirakintaikometadata";

            [JsonPropertyName("version")]
            public int Version { get; set; } = 1;

            [JsonPropertyName("entries")]
            public List<SirakinTaikoMetadataEntry> Entries { get; set; } = new();
        }

        private sealed class SirakinTaikoMetadataEntry
        {
            [JsonPropertyName("path")]
            public string Path { get; set; } = "";

            [JsonPropertyName("title")]
            public string Title { get; set; } = "";

            [JsonPropertyName("subtitle")]
            public string Subtitle { get; set; } = "";

            [JsonPropertyName("genre")]
            public string Genre { get; set; } = "";

            [JsonPropertyName("stage")]
            public string Stage { get; set; } = "";

            [JsonPropertyName("order")]
            public int Order { get; set; }
        }

        private static void GenerateOfficialBoxDef(string genreFolder, string folderPrefix, string genre, Action<string> log)
        {
            string boxDefPath = Path.Combine(genreFolder, "box.def");
            try
            {
                string referenceBoxDefPath = Path.Combine(ReferenceSongsFolder, folderPrefix, "box.def");
                if (!File.Exists(referenceBoxDefPath))
                {
                    log($"[ERROR] 参照元 box.def が見つかりません: {referenceBoxDefPath}");
                    return;
                }

                if (Path.GetFullPath(referenceBoxDefPath).Equals(Path.GetFullPath(boxDefPath), StringComparison.OrdinalIgnoreCase))
                {
                    log($"[SKIP] box.def は参照元と同一です: {boxDefPath}");
                    return;
                }

                if (File.Exists(boxDefPath))
                {
                    string bakPath = boxDefPath + ".bak";
                    File.Copy(boxDefPath, bakPath, true);
                    log($"[BAK] バックアップ作成: {bakPath}");
                }

                File.Copy(referenceBoxDefPath, boxDefPath, true);
                log($"[OK] box.def を参照元からコピーしました: {referenceBoxDefPath} → {boxDefPath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] box.def の生成に失敗しました: {ex.Message}");
            }
        }
    }
}
