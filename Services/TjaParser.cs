using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    public static class TjaParser
    {
        private static readonly Dictionary<string, string> CourseMapFromVal = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = "Easy", ["easy"] = "Easy", ["かんたん"] = "Easy",
            ["1"] = "Normal", ["normal"] = "Normal", ["ふつう"] = "Normal",
            ["2"] = "Hard", ["hard"] = "Hard", ["むずかしい"] = "Hard",
            ["3"] = "Oni", ["oni"] = "Oni", ["おに"] = "Oni",
            ["4"] = "Edit", ["edit"] = "Edit", ["ura"] = "Edit", ["うら"] = "Edit"
        };

        public static Encoding GetTjaEncoding(string filePath)
        {
            try
            {
                byte[] rawData = File.ReadAllBytes(filePath);
                if (rawData.Length >= 3 && rawData[0] == 0xEF && rawData[1] == 0xBB && rawData[2] == 0xBF)
                {
                    return new UTF8Encoding(true);
                }

                // BOMなしUTF-8としての厳密デコード検証（例外を投げるデコーダ）
                try
                {
                    var utf8Strict = new UTF8Encoding(false, true);
                    utf8Strict.GetString(rawData);
                    return new UTF8Encoding(false);
                }
                catch
                {
                    // UTF-8として無効なシーケンスがあれば Shift_JIS (CP932)
                    return Encoding.GetEncoding("shift_jis");
                }
            }
            catch
            {
                return new UTF8Encoding(false);
            }
        }

        public static List<CourseItem> ParseTjaMultiCourse(string filePath, Encoding encoding)
        {
            var results = new List<CourseItem>();
            try
            {
                string content = File.ReadAllText(filePath, encoding);

                string globalTitle = Path.GetFileNameWithoutExtension(filePath);
                var matchTitle = Regex.Match(content, @"(?i)^TITLE\s*:(.*)", RegexOptions.Multiline);
                if (matchTitle.Success) globalTitle = matchTitle.Groups[1].Value.Trim();

                string globalSub = "";
                var matchSub = Regex.Match(content, @"(?i)^SUBTITLE\s*:(.*)", RegexOptions.Multiline);
                if (matchSub.Success)
                {
                    string s = matchSub.Groups[1].Value.Trim();
                    if (s.StartsWith("--") || s.StartsWith("++")) globalSub = s.Substring(2);
                    else globalSub = s;
                }

                string globalGenre = "";
                var matchGenre = Regex.Match(content, @"(?i)^GENRE\s*:(.*)", RegexOptions.Multiline);
                if (matchGenre.Success) globalGenre = matchGenre.Groups[1].Value.Trim();

                string globalMaker = "";
                var matchMaker = Regex.Match(content, @"(?i)^MAKER\s*:(.*)", RegexOptions.Multiline);
                if (matchMaker.Success) globalMaker = matchMaker.Groups[1].Value.Trim();

                var courseBlocks = Regex.Split(content, @"(?i)^COURSE\s*:", RegexOptions.Multiline);
                if (courseBlocks.Length <= 1) return results;

                var tjaItem = new CourseItem
                {
                    FilePath = filePath,
                    Title = globalTitle,
                    Subtitle = globalSub,
                    Genre = globalGenre,
                    Maker = globalMaker
                };

                for (int i = 1; i < courseBlocks.Length; i++)
                {
                    string block = courseBlocks[i];
                    var lines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) continue;

                    string rawCourse = lines[0].Trim().ToLower();
                    string courseName = CourseMapFromVal.TryGetValue(rawCourse, out var mapped) ? mapped : rawCourse;

                    var cData = new CourseData
                    {
                        CourseName = courseName,
                        RawCourseVal = rawCourse,
                        Level = "1",
                        Combo = 0,
                        HasGimmick = block.IndexOf("#HBSCROLL", StringComparison.OrdinalIgnoreCase) >= 0
                    };

                    bool inNotes = false;
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        var matchLv = Regex.Match(trimmed, @"(?i)^LEVEL\s*:(.*)");
                        if (matchLv.Success) cData.Level = matchLv.Groups[1].Value.Trim();

                        // ブロック内でもしTitle等があれば最初のコースのものまたは空でないものをグローバルに反映（補完）
                        var matchMk = Regex.Match(trimmed, @"(?i)^MAKER\s*:(.*)");
                        if (matchMk.Success && string.IsNullOrEmpty(tjaItem.Maker)) tjaItem.Maker = matchMk.Groups[1].Value.Trim();

                        var matchTt = Regex.Match(trimmed, @"(?i)^TITLE\s*:(.*)");
                        if (matchTt.Success && string.IsNullOrEmpty(tjaItem.Title)) tjaItem.Title = matchTt.Groups[1].Value.Trim();

                        int commentIdx = trimmed.IndexOf("//");
                        string cleanLine = commentIdx >= 0 ? trimmed.Substring(0, commentIdx).Trim() : trimmed;

                        if (cleanLine.StartsWith("#START", StringComparison.OrdinalIgnoreCase)) inNotes = true;
                        else if (cleanLine.StartsWith("#END", StringComparison.OrdinalIgnoreCase)) inNotes = false;
                        else if (inNotes && !cleanLine.StartsWith("#"))
                        {
                            string notesOnly = Regex.Replace(cleanLine, @"[^1234]", "");
                            cData.Combo += notesOnly.Length;
                        }
                    }

                    tjaItem.Courses.Add(cData);
                }

                if (tjaItem.Courses.Count > 0)
                {
                    tjaItem.SelectedCourse = tjaItem.Courses[0];
                    results.Add(tjaItem);
                }
            }
            catch
            {
                // 解析エラー時
            }
            return results;
        }
    }
}
