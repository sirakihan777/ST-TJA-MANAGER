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
    /// wikiwiki.jp/taiko-fumen の家庭用（CS）版作品ページから収録曲リストを取得するサービス。
    /// </summary>
    public static class ConsumerSongListService
    {
        private const string WikiBaseUrl = "https://wikiwiki.jp/taiko-fumen/";

        /// <summary>キャッシュ保存先: %APPDATA%\ST-TJA-MANAGER\Database\consumer_songs.json</summary>
        public static string DatabasePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ST-TJA-MANAGER", "Database", "consumer_songs.json");

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
            }
        };

        /// <summary>
        /// 主要なCS版作品。必要に応じて追加してください。
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultWorks = new List<string>
        {
            "PS4 1",
            "NS1",
            "NS2",
            "DF",
            "Wii U1",
            "Wii U2",
            "Wii U3",
            "3DS1",
            "3DS2",
            "3DS3",
            "PSPDX",
            "PSP2",
        };

        private class ConsumerSongCache
        {
            [JsonPropertyName("generated_at")]
            public string GeneratedAt { get; set; } = "";

            [JsonPropertyName("songs")]
            public List<ConsumerSongRecord> Songs { get; set; } = new();
        }

        /// <summary>
        /// 指定したCS版作品の曲リストを取得する。
        /// </summary>
        public static async Task<List<ConsumerSongRecord>> FetchWorkAsync(
            string workTitle,
            Action<string> log,
            CancellationToken ct = default)
        {
            string pageName = "作品/" + workTitle;
            string url = WikiBaseUrl + Uri.EscapeDataString(pageName).Replace("%2F", "/");
            log?.Invoke($"[INFO] 取得中: {workTitle} ({url})");

            byte[] bytes;
            try
            {
                bytes = await HttpClient.GetByteArrayAsync(url, ct);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERROR] {workTitle} のダウンロードに失敗しました: {ex.Message}");
                return new List<ConsumerSongRecord>();
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

            var songs = ExtractSongs(html, workTitle, url);
            log?.Invoke($"[OK] {workTitle}: {songs.Count} 曲");
            return songs;
        }

        /// <summary>
        /// 複数のCS版作品を一括取得してキャッシュに保存する。
        /// </summary>
        public static async Task<List<ConsumerSongRecord>> FetchAndSaveAsync(
            IEnumerable<string> works,
            Action<string> log,
            CancellationToken ct = default)
        {
            var allSongs = new List<ConsumerSongRecord>();

            foreach (var work in works)
            {
                ct.ThrowIfCancellationRequested();
                var songs = await FetchWorkAsync(work, log, ct);
                allSongs.AddRange(songs);

                // 連続アクセスを避ける
                await Task.Delay(500, ct);
            }

            SaveToCache(allSongs, log);
            return allSongs;
        }

        /// <summary>
        /// ローカルキャッシュから読み込む。
        /// </summary>
        public static List<ConsumerSongRecord> LoadFromCache()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return new List<ConsumerSongRecord>();

                string json = File.ReadAllText(DatabasePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<ConsumerSongCache>(json);
                return cache?.Songs ?? new List<ConsumerSongRecord>();
            }
            catch
            {
                return new List<ConsumerSongRecord>();
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
                var cache = JsonSerializer.Deserialize<ConsumerSongCache>(json);
                return cache?.GeneratedAt;
            }
            catch
            {
                return null;
            }
        }

        // --- 内部実装 ---

        private static List<ConsumerSongRecord> ExtractSongs(string html, string workTitle, string sourceUrl)
        {
            var songs = new List<ConsumerSongRecord>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            // 曲名リンクのパターン（難易度セル用）:
            // /taiko-fumen/収録曲/おに/曲名
            string songLinkPattern = @"/taiko-fumen/%E5%8F%8E%E9%8C%B2%E6%9B%B2/[^/""]+/([^""]+)";

            var tableMatches = Regex.Matches(html, @"<table[^>]*>(.*?)</table>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match tableMatch in tableMatches)
            {
                string tableHtml = tableMatch.Value;

                // 各行を処理
                var rowMatches = Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)</tr>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                string currentGenre = "";

                foreach (Match rowMatch in rowMatches)
                {
                    string rowHtml = rowMatch.Value;

                    // ジャンル見出し行の検出（colspan付きth/td 内のテキストから判定）
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

                    // 曲名リンクを含む行のみ処理（難易度セル）
                    var linkMatches = Regex.Matches(rowHtml, songLinkPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (linkMatches.Count == 0) continue;

                    // td/th を列に分割
                    var cellMatches = Regex.Matches(rowHtml, @"<t[dh][^>]*>(.*?)</t[dh]>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (cellMatches.Count < 4) continue;

                    // 難易度リンクから曲名を取得（複数難易度があるが同じ曲名）
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

                    // 曲名セルを特定: <strong> を含むセルのうち、曲名と一致するもの
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

                        // 曲名リンクのデコード結果と一致、または含む場合に採用
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

                    // 一致する strong がなければ、リンクから取得した曲名を使う
                    if (titleCellIndex < 0)
                    {
                        title = titleFromLink;
                        // 最もリンクに近い strong セルを探す（フォールバック）
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
                    if (title.Contains("ショップ") || title.Contains("AIバトル")) continue;

                    // サブタイトル: <span style="font-size:11px"> または <br> 後のテキスト
                    string subtitle = ExtractSubtitle(titleCellHtml, title);

                    // 曲名セルより前の全セルから DL / 隠し マークを取得
                    var markCellHtmlBuilder = new StringBuilder();
                    for (int i = 0; i < titleCellIndex; i++)
                    {
                        markCellHtmlBuilder.Append(cellMatches[i].Groups[1].Value);
                        markCellHtmlBuilder.Append(' ');
                    }
                    string markCellHtml = markCellHtmlBuilder.ToString();
                    bool isDlc = Regex.IsMatch(markCellHtml, @">\s*DL\s*<", RegexOptions.IgnoreCase);
                    bool isHidden = Regex.IsMatch(markCellHtml, @">\s*隠\s*<", RegexOptions.IgnoreCase);

                    string normTitle = TitleNormalizer.NormalizeTitle(title);
                    string normSubtitle = TitleNormalizer.NormalizeSubtitle(subtitle);

                    if (string.IsNullOrWhiteSpace(normTitle)) continue;

                    string key = $"{workTitle}|{normTitle}|{normSubtitle}";
                    if (!seenKeys.Add(key)) continue;

                    songs.Add(new ConsumerSongRecord
                    {
                        WorkTitle = workTitle,
                        Genre = currentGenre,
                        Title = title,
                        Subtitle = subtitle,
                        NormalizedTitle = normTitle,
                        NormalizedSubtitle = normSubtitle,
                        IsDlc = isDlc,
                        IsHidden = isHidden,
                        SourceUrl = sourceUrl
                    });
                }
            }

            return songs;
        }

        private static string ExtractSubtitle(string titleCellHtml, string title)
        {
            // <span style="font-size:11px">サブタイトル</span>
            var spanMatch = Regex.Match(titleCellHtml,
                @"<span[^>]*font-size:11px[^>]*>(.*?)</span>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (spanMatch.Success)
            {
                return StripHtml(spanMatch.Groups[1].Value).Trim();
            }

            // <br> 以降のテキスト
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

        private static void SaveToCache(List<ConsumerSongRecord> songs, Action<string>? log)
        {
            try
            {
                string dir = Path.GetDirectoryName(DatabasePath)!;
                Directory.CreateDirectory(dir);

                var cache = new ConsumerSongCache
                {
                    GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Songs = songs
                };

                string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DatabasePath, json, Encoding.UTF8);

                log?.Invoke($"[OK] Consumer Database を保存しました: {songs.Count} 曲 → {DatabasePath}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERROR] Consumer Database の保存に失敗しました: {ex.Message}");
            }
        }
    }
}
