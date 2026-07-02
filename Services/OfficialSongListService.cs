using System;
using System.Collections.Generic;
using System.IO;
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
    /// ナムコ公式 songlist から曲リストを取得してキャッシュするサービス。
    /// WikiWikiは使用しない。公式サイトのみを正とする。
    /// </summary>
    public static class OfficialSongListService
    {
        // --- カテゴリ定義 ---

        /// <summary>
        /// 公式ジャンルの定義。(表示名, phpファイル名, フォルダ接頭辞)
        /// </summary>
        public static readonly IReadOnlyList<(string DisplayName, string FileName, string FolderPrefix)> Categories =
            new List<(string, string, string)>
            {
                ("ポップス",           "pops.php",     "00 ポップス"),
                ("キッズ",             "kids.php",     "01 キッズ"),
                ("アニメ",             "anime.php",    "02 アニメ"),
                ("ボーカロイド™曲",    "vocaloid.php", "03 ボーカロイド™曲"),
                ("ゲームミュージック", "game.php",     "04 ゲームミュージック"),
                ("バラエティ",         "variety.php",  "05 バラエティ"),
                ("クラシック",         "classic.php",  "06 クラシック"),
                ("ナムコオリジナル",   "namco.php",    "07 ナムコオリジナル"),
            };

        private const string BaseUrl = "https://taiko.namco-ch.net/taiko/songlist/";

        // --- キャッシュパス ---

        /// <summary>Database保存先: %APPDATA%\ST-TJA-MANAGER\Database\official_songlist.json</summary>
        public static string DatabasePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ST-TJA-MANAGER", "Database", "official_songlist.json");

        // --- HTTP Client ---

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
            }
        };

        // --- JSON シリアライズ用モデル ---

        private class SongListCache
        {
            [JsonPropertyName("generated_at")]
            public string GeneratedAt { get; set; } = "";

            [JsonPropertyName("songs")]
            public List<OfficialSongRecordDto> Songs { get; set; } = new();
        }

        private class OfficialSongRecordDto
        {
            [JsonPropertyName("genre")] public string Genre { get; set; } = "";
            [JsonPropertyName("order")] public int Order { get; set; }
            [JsonPropertyName("title")] public string Title { get; set; } = "";
            [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = "";
            [JsonPropertyName("normalized_title")] public string NormalizedTitle { get; set; } = "";
            [JsonPropertyName("normalized_subtitle")] public string NormalizedSubtitle { get; set; } = "";
        }

        // --- 公開API ---

        /// <summary>
        /// ナムコ公式 songlist を全カテゴリ取得してキャッシュに保存する。
        /// </summary>
        /// <param name="log">ログコールバック</param>
        /// <param name="ct">キャンセルトークン</param>
        /// <returns>取得した全曲リスト</returns>
        public static async Task<List<OfficialSongRecord>> FetchAndSaveAsync(
            Action<string> log,
            CancellationToken ct = default)
        {
            var allSongs = new List<OfficialSongRecord>();

            foreach (var (displayName, fileName, _) in Categories)
            {
                ct.ThrowIfCancellationRequested();
                log($"[INFO] 取得中: {displayName} ({fileName})...");

                try
                {
                    var songs = await FetchCategoryAsync(displayName, fileName, log, ct);
                    allSongs.AddRange(songs);
                    log($"[OK] {displayName}: {songs.Count} 曲");
                }
                catch (OperationCanceledException)
                {
                    log("[INFO] キャンセルされました。");
                    throw;
                }
                catch (Exception ex)
                {
                    log($"[ERROR] {displayName} の取得に失敗しました: {ex.Message}");
                }

                // 連続アクセスを避けるため少し待つ
                await Task.Delay(300, ct);
            }

            // キャッシュに保存
            SaveToCache(allSongs, log);

            return allSongs;
        }

        /// <summary>
        /// ローカルキャッシュから読み込む。キャッシュがなければ空リストを返す。
        /// </summary>
        public static List<OfficialSongRecord> LoadFromCache()
        {
            try
            {
                if (!File.Exists(DatabasePath))
                    return new List<OfficialSongRecord>();

                string json = File.ReadAllText(DatabasePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<SongListCache>(json);
                if (cache == null) return new List<OfficialSongRecord>();

                var result = new List<OfficialSongRecord>();
                foreach (var dto in cache.Songs)
                {
                    result.Add(new OfficialSongRecord
                    {
                        Genre = dto.Genre,
                        Order = dto.Order,
                        Title = dto.Title,
                        Subtitle = dto.Subtitle,
                        NormalizedTitle = dto.NormalizedTitle,
                        NormalizedSubtitle = dto.NormalizedSubtitle
                    });
                }
                return result;
            }
            catch
            {
                return new List<OfficialSongRecord>();
            }
        }

        /// <summary>
        /// キャッシュの最終取得日時を返す。キャッシュがなければ null。
        /// </summary>
        public static string? GetCacheInfo()
        {
            try
            {
                if (!File.Exists(DatabasePath)) return null;
                string json = File.ReadAllText(DatabasePath, Encoding.UTF8);
                var cache = JsonSerializer.Deserialize<SongListCache>(json);
                return cache?.GeneratedAt;
            }
            catch
            {
                return null;
            }
        }

        // --- 内部実装 ---

        private static async Task<List<OfficialSongRecord>> FetchCategoryAsync(
            string displayName,
            string fileName,
            Action<string> log,
            CancellationToken ct)
        {
            string url = BaseUrl + fileName;
            byte[] bytes = await HttpClient.GetByteArrayAsync(url, ct);

            // 文字コード判定: Content-Typeの charset を見るか、自動判定
            // ナムコ公式はUTF-8なのでまずUTF-8で試みる
            string html;
            try
            {
                var utf8Strict = new UTF8Encoding(false, true);
                html = utf8Strict.GetString(bytes);
            }
            catch
            {
                // UTF-8失敗ならShift_JIS
                html = Encoding.GetEncoding("shift_jis").GetString(bytes);
            }

            return ExtractSongs(html, displayName);
        }

        /// <summary>
        /// HTMLから曲リストを抽出する。
        /// ナムコ公式 songlist の構造: th 要素に曲名、その中の p 要素にサブタイトル。
        /// </summary>
        private static List<OfficialSongRecord> ExtractSongs(string html, string genre)
        {
            var songs = new List<OfficialSongRecord>();
            var seenKeys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            // th タグを正規表現で抽出
            var thMatches = Regex.Matches(html, @"<th[^>]*>(.*?)</th>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            int order = 0;
            foreach (Match thMatch in thMatches)
            {
                string thContent = thMatch.Groups[1].Value;

                // サブタイトル (<p> 要素) を先に抽出して除去
                string subtitle = "";
                var pMatch = Regex.Match(thContent, @"<p[^>]*>(.*?)</p>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (pMatch.Success)
                {
                    subtitle = StripHtml(pMatch.Value);
                    thContent = thContent.Replace(pMatch.Value, "");
                }

                // span要素(new/ico等)を除去
                thContent = Regex.Replace(thContent,
                    @"<span[^>]*(class\s*=\s*""[^""]*(?:new|ico)[^""]*"")[^>]*>.*?</span>",
                    "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                string title = StripHtml(thContent).Trim();

                if (string.IsNullOrWhiteSpace(title)) continue;

                // ヘッダー行のスキップ
                if (title == "曲名" || title == "難易度") continue;
                if (title.Contains("ショップ") || title.Contains("AIバトル")) continue;
                if (title.Contains("アイコンの説明") || title.Contains("どんメダル")) continue;
                if (title.StartsWith("各アイコン")) continue;

                subtitle = subtitle.Trim();

                string normTitle = TitleNormalizer.NormalizeTitle(title);
                string normSubtitle = TitleNormalizer.NormalizeSubtitle(subtitle);

                if (string.IsNullOrWhiteSpace(normTitle)) continue;

                // 重複チェック (正規化済みキーで)
                string key = $"{normTitle}|{normSubtitle}";
                if (!seenKeys.Add(key)) continue;

                songs.Add(new OfficialSongRecord
                {
                    Genre = genre,
                    Order = order++,
                    Title = title,
                    Subtitle = subtitle,
                    NormalizedTitle = normTitle,
                    NormalizedSubtitle = normSubtitle
                });
            }

            return songs;
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            // HTMLタグを除去
            string stripped = Regex.Replace(html, @"<[^>]+>", " ");
            // HTMLエンティティをデコード
            stripped = System.Net.WebUtility.HtmlDecode(stripped);
            // &nbsp; を半角スペースに
            stripped = stripped.Replace('\u00A0', ' ');
            // 連続スペースを1つに
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            return stripped;
        }

        private static void SaveToCache(List<OfficialSongRecord> songs, Action<string> log)
        {
            try
            {
                string dir = Path.GetDirectoryName(DatabasePath)!;
                Directory.CreateDirectory(dir);

                var dtos = new List<OfficialSongRecordDto>();
                foreach (var s in songs)
                {
                    dtos.Add(new OfficialSongRecordDto
                    {
                        Genre = s.Genre,
                        Order = s.Order,
                        Title = s.Title,
                        Subtitle = s.Subtitle,
                        NormalizedTitle = s.NormalizedTitle,
                        NormalizedSubtitle = s.NormalizedSubtitle
                    });
                }

                var cache = new SongListCache
                {
                    GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Songs = dtos
                };

                string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DatabasePath, json, Encoding.UTF8);

                log($"[OK] Database を保存しました: {songs.Count} 曲 → {DatabasePath}");
            }
            catch (Exception ex)
            {
                log($"[ERROR] Database の保存に失敗しました: {ex.Message}");
            }
        }
    }
}
