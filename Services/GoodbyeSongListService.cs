using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    /// <summary>
    /// wikiwiki.jp/taiko-fumen のサヨナラ曲ページから曲リストを取得するサービス。
    /// </summary>
    public static class GoodbyeSongListService
    {
        private const string GoodbyePageUrl = "https://wikiwiki.jp/taiko-fumen/%E4%BD%9C%E5%93%81/%E6%96%B0AC/%E3%82%B5%E3%83%A8%E3%83%8A%E3%83%A9%E6%9B%B2";

        /// <summary>キャッシュ保存先: %APPDATA%\ST-TJA-MANAGER\Database\goodbye_songs.json</summary>
        public static string DatabasePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ST-TJA-MANAGER", "Database", "goodbye_songs.json");

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
            }
        };

        private class GoodbyeSongCache
        {
            [JsonPropertyName("generated_at")]
            public string GeneratedAt { get; set; } = "";

            [JsonPropertyName("songs")]
            public List<GoodbyeSongRecord> Songs { get; set; } = new();
        }

        /// <summary>
        /// サヨナラ曲ページを取得してキャッシュに保存する。
        /// </summary>
        public static async Task<List<GoodbyeSongRecord>> FetchAndSaveAsync(
            Action<string> log,
            CancellationToken ct = default)
        {
            log?.Invoke($"[INFO] 取得中: サヨナラ曲 ({GoodbyePageUrl})");

            byte[] bytes;
            try
            {
                bytes = await HttpClient.GetByteArrayAsync(GoodbyePageUrl, ct);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERROR] サヨナラ曲のダウンロードに失敗しました: {ex.Message}");
                return new List<GoodbyeSongRecord>();
            }

            string html;
            try
            {
                html = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                html = Encoding.GetEncoding("shift_jis").GetString(bytes);
            }

            var songs = ExtractSongs(html);
            SaveToCache(songs, log);
            log?.Invoke($"[OK] サヨナラ曲: {songs.Count} 曲");
            return songs;
        }

        /// <summary>
        /// ローカルキャッシュから読み込む。
        /// </summary>
        public static List<GoodbyeSongRecord> LoadFromCache()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return new List<GoodbyeSongRecord>();

                string json = File.ReadAllText(DatabasePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<GoodbyeSongCache>(json);
                return cache?.Songs ?? new List<GoodbyeSongRecord>();
            }
            catch
            {
                return new List<GoodbyeSongRecord>();
            }
        }

        /// <summary>
        /// キャッシュの最終取得日時を返す。
        /// </summary>
        public static string? GetCacheInfo()
        {
            try
            {
                if (!File.Exists(DatabasePath)) return null;
                string json = File.ReadAllText(DatabasePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<GoodbyeSongCache>(json);
                return cache?.GeneratedAt;
            }
            catch
            {
                return null;
            }
        }

        // --- 内部実装 ---

        private static List<GoodbyeSongRecord> ExtractSongs(string html)
        {
            var songs = new List<GoodbyeSongRecord>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            string songLinkPattern = @"/taiko-fumen/%E5%8F%8E%E9%8C%B2%E6%9B%B2/[^/""]+/([^""]+)";

            var tableMatches = Regex.Matches(html, @"<table[^>]*>(.*?)</table>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match tableMatch in tableMatches)
            {
                string tableHtml = tableMatch.Value;

                var rowMatches = Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)</tr>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                string currentGenre = "";

                foreach (Match rowMatch in rowMatches)
                {
                    string rowHtml = rowMatch.Value;

                    // ジャンル見出し行の検出
                    var genreHeaderMatch = Regex.Match(rowHtml,
                        @"<t[dh][^>]*colspan[^>]*>(.*?)</t[dh]>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (genreHeaderMatch.Success)
                    {
                        string genreText = StripHtml(genreHeaderMatch.Groups[1].Value).Trim();
                        if (TryDetectGenre(genreText, out var detectedGenre))
                        {
                            currentGenre = detectedGenre;
                            continue;
                        }
                    }

                    // 曲名リンクを含む行のみ処理
                    var linkMatches = Regex.Matches(rowHtml, songLinkPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (linkMatches.Count == 0) continue;

                    var cellMatches = Regex.Matches(rowHtml, @"<t[dh][^>]*>(.*?)</t[dh]>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (cellMatches.Count < 3) continue;

                    string titleFromLink = "";
                    foreach (Match linkMatch in linkMatches)
                    {
                        string encodedTitle = linkMatch.Groups[1].Value;
                        string decoded = Uri.UnescapeDataString(encodedTitle).Replace('_', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(decoded))
                        {
                            titleFromLink = decoded;
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(titleFromLink)) continue;

                    // 曲名セルを特定
                    int titleCellIndex = -1;
                    string titleCellHtml = "";
                    string title = "";
                    for (int i = 0; i < cellMatches.Count; i++)
                    {
                        string cellHtml = cellMatches[i].Groups[1].Value;
                        var strongMatch = Regex.Match(cellHtml, @"<strong[^>]*>(.*?)</strong>",
                            RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (!strongMatch.Success) continue;

                        string strongText = StripHtml(strongMatch.Groups[1].Value).Trim();
                        if (string.IsNullOrWhiteSpace(strongText)) continue;
                        if (strongText.Contains("曲名") || strongText.Contains("難易度")) continue;

                        string normStrong = TitleNormalizer.NormalizeTitle(strongText);
                        string normLink = TitleNormalizer.NormalizeTitle(titleFromLink);
                        if (normStrong == normLink || normLink.Contains(normStrong) || normStrong.Contains(normLink))
                        {
                            titleCellIndex = i;
                            titleCellHtml = cellHtml;
                            title = strongText;
                            break;
                        }
                    }

                    if (titleCellIndex < 0)
                    {
                        title = titleFromLink;
                        for (int i = 0; i < cellMatches.Count; i++)
                        {
                            if (Regex.IsMatch(cellMatches[i].Groups[1].Value, @"<strong[^>]*>(.*?)</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                            {
                                titleCellIndex = i;
                                titleCellHtml = cellMatches[i].Groups[1].Value;
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // サブタイトル
                    string subtitle = ExtractSubtitle(titleCellHtml, title);

                    // サヨナラ日: 曲名セルの直前のセル
                    string goodbyeDate = "";
                    if (titleCellIndex > 0)
                    {
                        goodbyeDate = StripHtml(cellMatches[titleCellIndex - 1].Groups[1].Value).Trim();
                    }

                    string normTitle = TitleNormalizer.NormalizeTitle(title);
                    string normSubtitle = TitleNormalizer.NormalizeSubtitle(subtitle);

                    if (string.IsNullOrWhiteSpace(normTitle)) continue;

                    string key = $"{normTitle}|{normSubtitle}";
                    if (!seenKeys.Add(key)) continue;

                    songs.Add(new GoodbyeSongRecord
                    {
                        Genre = currentGenre,
                        Title = title,
                        Subtitle = subtitle,
                        NormalizedTitle = normTitle,
                        NormalizedSubtitle = normSubtitle,
                        GoodbyeDate = goodbyeDate
                    });
                }
            }

            return songs;
        }

        private static string ExtractSubtitle(string titleCellHtml, string title)
        {
            var spanMatch = Regex.Match(titleCellHtml,
                @"<span[^>]*font-size:11px[^>]*>(.*?)</span>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (spanMatch.Success)
            {
                return StripHtml(spanMatch.Groups[1].Value).Trim();
            }

            var brSplit = Regex.Split(titleCellHtml, @"<br\s*/?>", RegexOptions.IgnoreCase);
            if (brSplit.Length > 1)
            {
                string afterBr = StripHtml(string.Join(" ", brSplit.Skip(1))).Trim();
                if (!string.IsNullOrWhiteSpace(afterBr) && !afterBr.Contains(title))
                    return afterBr;
            }

            return "";
        }

        private static bool TryDetectGenre(string rowText, out string genre)
        {
            genre = "";
            if (string.IsNullOrWhiteSpace(rowText)) return false;

            string[] genres = { "ポップス", "キッズ", "アニメ", "ボーカロイド™曲", "ボーカロイド™\n曲", "ゲームミュージック", "バラエティ", "クラシック", "ナムコオリジナル" };

            foreach (var g in genres)
            {
                if (rowText.Trim().Equals(g, StringComparison.Ordinal))
                {
                    genre = g.Replace("\n", "");
                    return true;
                }
            }

            return false;
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            string stripped = Regex.Replace(html, @"<[^>]+>", " ");
            stripped = System.Net.WebUtility.HtmlDecode(stripped);
            stripped = stripped.Replace('\u00A0', ' ');
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            return stripped;
        }

        private static void SaveToCache(List<GoodbyeSongRecord> songs, Action<string>? log)
        {
            try
            {
                string dir = Path.GetDirectoryName(DatabasePath)!;
                Directory.CreateDirectory(dir);

                var cache = new GoodbyeSongCache
                {
                    GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Songs = songs
                };

                string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DatabasePath, json, Encoding.UTF8);

                log?.Invoke($"[OK] サヨナラ曲 Database を保存しました: {songs.Count} 曲 → {DatabasePath}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERROR] サヨナラ曲 Database の保存に失敗しました: {ex.Message}");
            }
        }
    }
}
