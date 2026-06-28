using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    public static class StfdbService
    {
        public static List<string> FindStfdbFiles(string folderPath)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return list;

            try
            {
                list.AddRange(Directory.GetFiles(folderPath, "*.stfdb", SearchOption.AllDirectories));
            }
            catch
            {
                // アクセス権限等でエラーが生じた場合は無視
            }
            return list;
        }

        public static List<StfdbEntryItem> LoadStfdbFile(string stfdbPath, out Encoding encoding, out string newline)
        {
            var entries = new List<StfdbEntryItem>();
            encoding = Encoding.GetEncoding("shift_jis");
            newline = "\r\n";

            if (string.IsNullOrEmpty(stfdbPath) || !File.Exists(stfdbPath))
                return entries;

            try
            {
                // エンコーディング厳密判定 (BOM付きUTF-8 -> BOMなしUTF-8厳密検証 -> Shift_JIS)
                byte[] rawBytes = File.ReadAllBytes(stfdbPath);
                if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
                {
                    encoding = new UTF8Encoding(true);
                }
                else
                {
                    try
                    {
                        var utf8Strict = new UTF8Encoding(false, true);
                        utf8Strict.GetString(rawBytes);
                        encoding = new UTF8Encoding(false);
                    }
                    catch
                    {
                        encoding = Encoding.GetEncoding("shift_jis");
                    }
                }

                string content = File.ReadAllText(stfdbPath, encoding);

                if (content.Contains("\r\n")) newline = "\r\n";
                else if (content.Contains("\n")) newline = "\n";
                else if (content.Contains("\r")) newline = "\r";

                string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                for (int i = 0; i < lines.Length; i++)
                {
                    string rawLine = lines[i];
                    string trimmed = rawLine.Trim().Trim('\"');
                    
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                        continue;

                    TryResolveStfdbEntryPath(stfdbPath, rawLine, out string resolved);
                    bool exists = !string.IsNullOrEmpty(resolved) && File.Exists(resolved);

                    var entry = new StfdbEntryItem
                    {
                        StfdbFilePath = stfdbPath,
                        LineIndex = i,
                        RawPath = rawLine,
                        EditedPath = rawLine,
                        ResolvedTjaPath = resolved,
                        Exists = exists
                    };

                    if (exists && resolved.EndsWith(".tja", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadTjaSummaryForStfdb(entry);
                    }

                    entries.Add(entry);
                }

                ValidateStfdbEntries(entries);
                RefreshRowNumbers(entries);
            }
            catch
            {
                // 読み込みエラー
            }

            return entries;
        }

        public static bool TryResolveStfdbEntryPath(string stfdbPath, string entryPath, out string resolved)
        {
            resolved = "";
            try
            {
                string trimmed = entryPath.Trim().Trim('\"');
                if (string.IsNullOrEmpty(trimmed))
                    return false;

                trimmed = trimmed.Replace('/', Path.DirectorySeparatorChar);
                trimmed = trimmed.Replace('\\', Path.DirectorySeparatorChar);

                string targetPath;
                if (Path.IsPathRooted(trimmed))
                {
                    targetPath = trimmed;
                }
                else
                {
                    string? stfdbDir = Path.GetDirectoryName(stfdbPath);
                    if (stfdbDir == null) return false;
                    targetPath = Path.Combine(stfdbDir, trimmed);
                }

                resolved = Path.GetFullPath(targetPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveStfdbEntryPath(string stfdbPath, string entryPath)
        {
            TryResolveStfdbEntryPath(stfdbPath, entryPath, out string res);
            return res;
        }

        public static bool TryGetFullPathKey(string path, out string key)
        {
            key = "";
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                key = Path.GetFullPath(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string MakeRelativeTjaPath(string stfdbPath, string tjaPath)
        {
            try
            {
                string? stfdbDir = Path.GetDirectoryName(stfdbPath);
                if (stfdbDir == null) return tjaPath;

                string rel = Path.GetRelativePath(stfdbDir, tjaPath);
                return rel.Replace('/', '\\');
            }
            catch
            {
                return tjaPath;
            }
        }

        public static void ValidateStfdbEntries(IList<StfdbEntryItem> entries)
        {
            foreach (var e in entries)
            {
                TryResolveStfdbEntryPath(e.StfdbFilePath, e.EditedPath, out string resolved);
                e.ResolvedTjaPath = resolved;
                e.Exists = !string.IsNullOrEmpty(e.ResolvedTjaPath) && File.Exists(e.ResolvedTjaPath);
            }

            var resolvedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (TryGetFullPathKey(e.ResolvedTjaPath, out string norm))
                {
                    resolvedCounts[norm] = resolvedCounts.TryGetValue(norm, out int count) ? count + 1 : 1;
                }
            }

            foreach (var e in entries)
            {
                string path = e.EditedPath;
                string trimmed = path.Trim().Trim('\"');

                bool isDuplicate = false;
                if (TryGetFullPathKey(e.ResolvedTjaPath, out string normResolved))
                {
                    isDuplicate = resolvedCounts.TryGetValue(normResolved, out int count) && count > 1;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    e.Severity = "Error";
                    e.Message = "空行です";
                }
                else if (string.IsNullOrEmpty(e.ResolvedTjaPath))
                {
                    e.Severity = "Error";
                    e.Message = "不正なパス文字列です";
                }
                else if (trimmed.Equals(".tja", StringComparison.OrdinalIgnoreCase))
                {
                    e.Severity = "Error";
                    e.Message = "ファイル名がなく .tja のみです";
                }
                else if (!trimmed.EndsWith(".tja", StringComparison.OrdinalIgnoreCase))
                {
                    e.Severity = "Error";
                    e.Message = ".tja で終わっていません";
                }
                else if (!e.Exists)
                {
                    e.Severity = "Error";
                    e.Message = "TJAファイルが存在しません";
                }
                else if (isDuplicate)
                {
                    e.Severity = "Warning";
                    e.Message = "同じパスが重複しています";
                }
                else if (Path.IsPathRooted(trimmed))
                {
                    e.Severity = "Warning";
                    e.Message = "絶対パスになっています";
                }
                else if (path != path.Trim())
                {
                    e.Severity = "Warning";
                    e.Message = "前後に余計な空白があります";
                }
                else if (trimmed.Length >= 5 && char.IsWhiteSpace(trimmed[trimmed.Length - 5]))
                {
                    e.Severity = "Warning";
                    e.Message = "拡張子直前に空白があります";
                }
                else
                {
                    e.Severity = "OK";
                    e.Message = "";
                }
            }
        }

        public static void ReadTjaSummaryForStfdb(StfdbEntryItem entry)
        {
            try
            {
                if (!File.Exists(entry.ResolvedTjaPath)) return;
                var enc = TjaParser.GetTjaEncoding(entry.ResolvedTjaPath);
                var courses = TjaParser.ParseTjaMultiCourse(entry.ResolvedTjaPath, enc);
                if (courses.Count > 0)
                {
                    var c = courses[0];
                    entry.Title = c.Title;
                    entry.Genre = c.Genre;
                    entry.Course = c.CourseName;
                    entry.Level = c.Level;
                }
            }
            catch
            {
                // ヘッダー読み込み失敗時は空欄
            }
        }

        public static void RefreshRowNumbers(IList<StfdbEntryItem> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].RowNumber = i + 1;
            }
        }

        public static void SaveStfdbFile(string stfdbPath, IList<StfdbEntryItem> entries, Encoding originalEncoding, string originalNewline)
        {
            if (string.IsNullOrEmpty(stfdbPath)) return;

            var lines = new List<string>();
            foreach (var e in entries)
            {
                string p = e.EditedPath.Trim();
                if (Path.IsPathRooted(p))
                {
                    p = MakeRelativeTjaPath(stfdbPath, p);
                }

                // 変換後も絶対パスのままである場合（別ドライブ等）はエラーにする
                if (Path.IsPathRooted(p))
                {
                    throw new InvalidOperationException($"絶対パス '{p}' をSTFDB基準の相対パスに変換できませんでした（別ドライブの可能性があります）。保存を中断しました。");
                }

                lines.Add(p);
            }

            // 全件の検証をクリアしてからバックアップを作成する
            if (File.Exists(stfdbPath))
            {
                string bakPath = stfdbPath + ".bak";
                File.Copy(stfdbPath, bakPath, true);
            }

            string content = string.Join(originalNewline, lines);
            if (lines.Count > 0) content += originalNewline;

            try
            {
                File.WriteAllText(stfdbPath, content, originalEncoding);
            }
            catch (EncoderFallbackException)
            {
                File.WriteAllText(stfdbPath, content, new UTF8Encoding(true));
            }
            catch
            {
                File.WriteAllText(stfdbPath, content, new UTF8Encoding(true));
            }
        }

        public static bool RunSanityCheck(out string report)
        {
            var sb = new StringBuilder();
            bool allPassed = true;

            try
            {
                string dummyStfdb = @"C:\Songs\GenreA\test.stfdb";
                
                string res1 = ResolveStfdbEntryPath(dummyStfdb, @"../GenreB/song.tja");
                string expected1 = @"C:\Songs\GenreB\song.tja";
                if (!string.Equals(res1, expected1, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"[FAIL] ResolveStfdbEntryPath: expected {expected1}, got {res1}");
                    allPassed = false;
                }
                else sb.AppendLine("[PASS] ResolveStfdbEntryPath");

                string rel1 = MakeRelativeTjaPath(dummyStfdb, @"C:\Songs\GenreB\song.tja");
                if (rel1 != @"..\GenreB\song.tja")
                {
                    sb.AppendLine($"[FAIL] MakeRelativeTjaPath: expected ..\\GenreB\\song.tja, got {rel1}");
                    allPassed = false;
                }
                else sb.AppendLine("[PASS] MakeRelativeTjaPath");

                var items = new List<StfdbEntryItem>
                {
                    new StfdbEntryItem { StfdbFilePath = dummyStfdb, EditedPath = @"..\song.txt" },
                    new StfdbEntryItem { StfdbFilePath = dummyStfdb, EditedPath = @"..\song .tja" },
                    new StfdbEntryItem { StfdbFilePath = dummyStfdb, EditedPath = @".tja" },
                    new StfdbEntryItem { StfdbFilePath = dummyStfdb, EditedPath = @"||invalid||" }
                };
                ValidateStfdbEntries(items);

                if (items[0].Severity != "Error" || !items[0].Message.Contains(".tja")) allPassed = false;
                if (items[1].Severity != "Warning" || !items[1].Message.Contains("拡張子直前に空白")) allPassed = false;
                if (items[2].Severity != "Error" || !items[2].Message.Contains(".tja のみ")) allPassed = false;
                if (items[3].Severity != "Error" || !items[3].Message.Contains("不正なパス")) allPassed = false;

                if (allPassed) sb.AppendLine("[PASS] All edge case validations passed.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[EXCEPTION] {ex.Message}");
                allPassed = false;
            }

            report = sb.ToString();
            return allPassed;
        }
    }
}
