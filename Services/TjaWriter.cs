using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ST_Fumen_Manager_WPF.Models;

namespace ST_Fumen_Manager_WPF.Services
{
    public static class TjaWriter
    {
        private const string CleanupComment = "// PeepoDrumKit 2025/09/13";

        public static string CleanRedundantNewlines(string content)
        {
            content = content.Replace(CleanupComment, "");
            content = content.Replace("\r\n", "\n");
            var lines = content.Split('\n').Select(l => l.TrimEnd()).Where(l => !string.IsNullOrWhiteSpace(l));
            return string.Join("\n", lines) + "\n";
        }

        public static string UpdateTjaWaveLines(string content)
        {
            var newLines = new List<string>();
            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string comment = "";
                string body = line;
                int commentIdx = line.IndexOf("//");
                if (commentIdx >= 0)
                {
                    body = line.Substring(0, commentIdx);
                    comment = line.Substring(commentIdx);
                }

                var match = Regex.Match(body, @"(?i)^(\s*WAVE\s*:\s*)(.*?)(\s*)$");
                if (match.Success)
                {
                    string prefix = match.Groups[1].Value;
                    string waveVal = match.Groups[2].Value.Trim();
                    string suffix = match.Groups[3].Value;

                    string lower = waveVal.ToLower();
                    if (lower.EndsWith(".wav.ogg")) waveVal = waveVal.Substring(0, waveVal.Length - ".wav.ogg".Length) + ".ogg";
                    else if (lower.EndsWith(".wav")) waveVal = waveVal.Substring(0, waveVal.Length - ".wav".Length) + ".ogg";

                    newLines.Add($"{prefix}{waveVal}{suffix}{comment}");
                }
                else
                {
                    newLines.Add(line);
                }
            }
            return string.Join("\n", newLines);
        }

        private static Dictionary<string, string> BuildCommonHeaderLines(CourseItem newData)
        {
            string title = (newData.Title ?? "").Trim();
            string subtitle = (newData.Subtitle ?? "").Trim();
            string genre = (newData.Genre ?? "").Trim();
            string maker = (newData.Maker ?? "").Trim();

            var lines = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(title)) lines["TITLE"] = $"TITLE:{title}";
            if (!string.IsNullOrEmpty(subtitle))
            {
                if (!subtitle.StartsWith("--") && !subtitle.StartsWith("++")) subtitle = $"--{subtitle}";
                lines["SUBTITLE"] = $"SUBTITLE:{subtitle}";
            }
            if (!string.IsNullOrEmpty(genre)) lines["GENRE"] = $"GENRE:{genre}";
            if (!string.IsNullOrEmpty(maker)) lines["MAKER"] = $"MAKER:{maker}";
            return lines;
        }

        public static void SaveSpecificCourseData(string filePath, string targetCourseRaw, CourseItem newData)
        {
            var encoding = TjaParser.GetTjaEncoding(filePath);
            var lines = File.ReadAllLines(filePath, encoding);

            var headerLines = BuildCommonHeaderLines(newData);
            var headerKeys = new HashSet<string>(headerLines.Keys);
            var seenHeaderKeys = new HashSet<string>();

            var newLines = new List<string>();
            CourseData? currentCourse = null;
            bool inCommonHeader = true;
            bool insertedMissingHeader = false;

            void InsertMissingCommonHeaders()
            {
                if (insertedMissingHeader) return;
                foreach (var key in new[] { "TITLE", "SUBTITLE", "GENRE", "MAKER" })
                {
                    if (headerKeys.Contains(key) && !seenHeaderKeys.Contains(key))
                    {
                        newLines.Add(headerLines[key]);
                        seenHeaderKeys.Add(key);
                    }
                }
                insertedMissingHeader = true;
            }

            foreach (var rawLine in lines)
            {
                string line = rawLine.Replace(CleanupComment, "");
                string trimmed = line.Trim();
                if (trimmed == "") continue;

                var courseMatch = Regex.Match(trimmed, @"(?i)^COURSE\s*:(.*)");
                if (courseMatch.Success)
                {
                    if (inCommonHeader)
                    {
                        InsertMissingCommonHeaders();
                        inCommonHeader = false;
                    }
                    string courseVal = courseMatch.Groups[1].Value.Trim();
                    currentCourse = newData.Courses.FirstOrDefault(c => string.Equals(c.RawCourseVal, courseVal, StringComparison.OrdinalIgnoreCase) ||
                                                                        string.Equals(c.CourseName, courseVal, StringComparison.OrdinalIgnoreCase));
                    if (currentCourse == null && newData.Courses.Count > 0)
                    {
                        // 1つしかない場合等のフォールバック
                        currentCourse = newData.Courses.FirstOrDefault();
                    }
                    newLines.Add(line);
                    continue;
                }

                var headerMatch = Regex.Match(trimmed, @"(?i)^(TITLE|SUBTITLE|GENRE|MAKER)\s*:");
                if (inCommonHeader && headerMatch.Success)
                {
                    string key = headerMatch.Groups[1].Value.ToUpper();
                    seenHeaderKeys.Add(key);
                    if (headerLines.TryGetValue(key, out var replacement))
                    {
                        newLines.Add(replacement);
                    }
                    continue;
                }

                if (!inCommonHeader && Regex.IsMatch(trimmed, @"(?i)^LEVEL\s*:"))
                {
                    string targetLv = currentCourse?.Level ?? newData.Level;
                    newLines.Add($"LEVEL:{targetLv}");
                }
                else if (!inCommonHeader && Regex.IsMatch(trimmed, @"(?i)^MAKER\s*:"))
                {
                    string maker = (newData.Maker ?? "").Trim();
                    if (!string.IsNullOrEmpty(maker))
                    {
                        newLines.Add($"MAKER:{maker}");
                    }
                }
                else
                {
                    newLines.Add(line);
                }
            }

            if (inCommonHeader)
            {
                InsertMissingCommonHeaders();
            }

            string content = CleanRedundantNewlines(string.Join("\n", newLines));
            SaveWithEncodingFallback(filePath, content);
        }

        public static void SaveWithEncodingFallback(string filePath, string content)
        {
            Encoding saveEncoding;
            try
            {
                var sjisStrict = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderReplacementFallback("?"));
                sjisStrict.GetBytes(content); // 例外チェック
                saveEncoding = Encoding.GetEncoding("shift_jis");
            }
            catch
            {
                saveEncoding = new UTF8Encoding(true); // Shift_JISで表せない文字があるためUTF-8 BOM
            }

            File.WriteAllText(filePath, content, saveEncoding);
        }
    }
}
