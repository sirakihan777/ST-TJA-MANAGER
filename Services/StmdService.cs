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
    public static class StmdService
    {
        private const string MetadataFormat = "sirakintaikometadata";

        public static List<StmdEntryItem> LoadStmdForStfdb(string stfdbPath)
        {
            var result = new List<StmdEntryItem>();
            if (string.IsNullOrWhiteSpace(stfdbPath) || !File.Exists(stfdbPath))
                return result;

            var stfdbEntries = StfdbService.LoadStfdbFile(stfdbPath, out _, out _);
            var metadataEntries = LoadMetadataEntries(stfdbPath);
            var metadataByResolvedPath = BuildMetadataQueues(stfdbPath, metadataEntries);

            if (stfdbEntries.Count > 0)
            {
                for (int i = 0; i < stfdbEntries.Count; i++)
                {
                    var stfdbEntry = stfdbEntries[i];
                    var metadata = TakeMatchingMetadata(stfdbEntry.ResolvedTjaPath, metadataByResolvedPath);
                    var fallback = ReadFirstTjaCourse(stfdbEntry.ResolvedTjaPath);

                    result.Add(new StmdEntryItem
                    {
                        RowNumber = i + 1,
                        TjaPath = stfdbEntry.EditedPath,
                        ResolvedTjaPath = stfdbEntry.ResolvedTjaPath,
                        Exists = stfdbEntry.Exists,
                        Title = metadata?.Title ?? fallback?.Title ?? stfdbEntry.Title,
                        Subtitle = metadata?.Subtitle ?? fallback?.Subtitle ?? "",
                        Genre = metadata?.Genre ?? fallback?.Genre ?? stfdbEntry.Genre,
                        Stage = metadata?.Stage ?? fallback?.Stage ?? "",
                        Order = metadata?.Order ?? i + 1,
                        Message = stfdbEntry.Exists ? "" : "TJAファイルが存在しません"
                    });
                }

                return result;
            }

            string? stfdbDir = Path.GetDirectoryName(stfdbPath);
            for (int i = 0; i < metadataEntries.Count; i++)
            {
                var metadata = metadataEntries[i];
                string resolved = "";
                bool exists = false;
                if (!string.IsNullOrWhiteSpace(metadata.Path) && stfdbDir != null)
                {
                    StfdbService.TryResolveStfdbEntryPath(stfdbPath, metadata.Path, out resolved);
                    exists = !string.IsNullOrEmpty(resolved) && File.Exists(resolved);
                }

                result.Add(new StmdEntryItem
                {
                    RowNumber = i + 1,
                    TjaPath = metadata.Path ?? "",
                    ResolvedTjaPath = resolved,
                    Exists = exists,
                    Title = metadata.Title ?? "",
                    Subtitle = metadata.Subtitle ?? "",
                    Genre = metadata.Genre ?? "",
                    Stage = metadata.Stage ?? "",
                    Order = metadata.Order,
                    Message = exists ? "" : "STFDB側のTJA参照が見つかりません"
                });
            }

            return result;
        }

        public static void SaveStmdForStfdb(string stfdbPath, IList<StmdEntryItem> entries)
        {
            if (string.IsNullOrWhiteSpace(stfdbPath))
                return;

            string metadataPath = stfdbPath + ".stmd";
            if (File.Exists(metadataPath))
            {
                File.Copy(metadataPath, metadataPath + ".bak", true);
            }

            var file = new StmdFile
            {
                Format = MetadataFormat,
                Version = 1,
                Entries = entries.Select(e => new StmdEntry
                {
                    Path = e.TjaPath.Trim(),
                    Title = e.Title ?? "",
                    Subtitle = e.Subtitle ?? "",
                    Genre = e.Genre ?? "",
                    Stage = string.IsNullOrWhiteSpace(e.Stage) ? null : e.Stage.Trim(),
                    Order = e.Order
                }).ToList()
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            string json = JsonSerializer.Serialize(file, options);
            File.WriteAllText(metadataPath, json, new UTF8Encoding(false));
        }

        private static List<StmdEntry> LoadMetadataEntries(string stfdbPath)
        {
            string metadataPath = stfdbPath + ".stmd";
            if (!File.Exists(metadataPath))
                return new List<StmdEntry>();

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var file = JsonSerializer.Deserialize<StmdFile>(
                    File.ReadAllText(metadataPath, Encoding.UTF8), options);

                if (file == null
                    || !string.Equals(file.Format, MetadataFormat, StringComparison.OrdinalIgnoreCase)
                    || file.Entries == null)
                {
                    return new List<StmdEntry>();
                }

                return file.Entries;
            }
            catch
            {
                return new List<StmdEntry>();
            }
        }

        private static Dictionary<string, Queue<StmdEntry>> BuildMetadataQueues(
            string stfdbPath,
            IEnumerable<StmdEntry> entries)
        {
            var result = new Dictionary<string, Queue<StmdEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                    continue;

                if (!StfdbService.TryResolveStfdbEntryPath(stfdbPath, entry.Path, out string resolved)
                    || !StfdbService.TryGetFullPathKey(resolved, out string key))
                {
                    continue;
                }

                if (!result.TryGetValue(key, out var queue))
                {
                    queue = new Queue<StmdEntry>();
                    result[key] = queue;
                }
                queue.Enqueue(entry);
            }

            return result;
        }

        private static StmdEntry? TakeMatchingMetadata(
            string resolvedTjaPath,
            Dictionary<string, Queue<StmdEntry>> metadataByResolvedPath)
        {
            if (!StfdbService.TryGetFullPathKey(resolvedTjaPath, out string key))
                return null;

            if (!metadataByResolvedPath.TryGetValue(key, out var queue) || queue.Count == 0)
                return null;

            return queue.Dequeue();
        }

        private static CourseItem? ReadFirstTjaCourse(string resolvedTjaPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(resolvedTjaPath) || !File.Exists(resolvedTjaPath))
                    return null;

                var encoding = TjaParser.GetTjaEncoding(resolvedTjaPath);
                return TjaParser.ParseTjaMultiCourse(resolvedTjaPath, encoding).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private sealed class StmdFile
        {
            [JsonPropertyName("format")]
            public string Format { get; set; } = MetadataFormat;

            [JsonPropertyName("version")]
            public int Version { get; set; } = 1;

            [JsonPropertyName("entries")]
            public List<StmdEntry> Entries { get; set; } = new();
        }

        private sealed class StmdEntry
        {
            [JsonPropertyName("path")]
            public string? Path { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("subtitle")]
            public string? Subtitle { get; set; }

            [JsonPropertyName("genre")]
            public string? Genre { get; set; }

            [JsonPropertyName("stage")]
            public string? Stage { get; set; }

            [JsonPropertyName("order")]
            public int Order { get; set; }
        }
    }
}
