using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ST_Fumen_Manager_WPF.Services
{
    public class BatchSummary
    {
        public List<(string Path, string Error)> ConvertErrors { get; } = new();
        public List<(string Path, string Error)> DeleteErrors { get; } = new();
        public List<(string Path, string Error)> LegacyErrors { get; } = new();
        public List<(string Path, string Error)> TjaErrors { get; } = new();
        public string? LogPath { get; set; }

        public int TotalErrorCount => ConvertErrors.Count + DeleteErrors.Count + LegacyErrors.Count + TjaErrors.Count;
    }

    public static class AudioConverter
    {
        public const bool DeleteOriginalWav = true;

        public static bool CheckFfmpegAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                return p != null && p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static (bool Success, string? Error) SafeDeleteFile(string filePath, int retries = 6, int delayMs = 200)
        {
            if (!File.Exists(filePath)) return (true, null);

            string? lastError = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    try
                    {
                        var attr = File.GetAttributes(filePath);
                        if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch { }

                    File.Delete(filePath);
                    return (true, null);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(delayMs * (i + 1));
                }
            }
            return (false, lastError);
        }

        public static void NormalizeLegacyWavOggFiles(string folderPath, List<(string Path, string Error)> errors)
        {
            var legacyFiles = Directory.GetFiles(folderPath, "*.wav.ogg", SearchOption.AllDirectories);
            foreach (var legacyPath in legacyFiles)
            {
                var canonicalPath = legacyPath.Substring(0, legacyPath.Length - ".wav.ogg".Length) + ".ogg";
                try
                {
                    if (File.Exists(canonicalPath))
                    {
                        var (ok, err) = SafeDeleteFile(legacyPath);
                        if (!ok)
                        {
                            errors.Add((legacyPath, $"legacy delete failed: {err}"));
                        }
                    }
                    else
                    {
                        File.Move(legacyPath, canonicalPath);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add((legacyPath, $"legacy normalize failed: {ex.Message}"));
                }
            }
        }

        public static string? CanonicalOggPathFromWav(string wavPath)
        {
            if (!wavPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return null;
            return wavPath.Substring(0, wavPath.Length - 4) + ".ogg";
        }

        public static BatchSummary BatchConvertAndCleanup(string folderPath, Action<string, string>? statusCallback = null)
        {
            var summary = new BatchSummary();

            NormalizeLegacyWavOggFiles(folderPath, summary.LegacyErrors);

            var wavFiles = Directory.GetFiles(folderPath, "*.wav", SearchOption.AllDirectories);
            foreach (var wavPath in wavFiles)
            {
                var oggPath = CanonicalOggPathFromWav(wavPath);
                if (oggPath == null) continue;

                try
                {
                    if (!File.Exists(oggPath))
                    {
                        statusCallback?.Invoke($"変換中: {Path.GetFileName(wavPath)}", "Cyan");
                        
                        var psi = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-y -i \"{wavPath}\" \"{oggPath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit();
                        if (p != null && p.ExitCode != 0)
                        {
                            string errOut = p.StandardError.ReadToEnd();
                            throw new Exception($"ffmpeg error (code {p.ExitCode}): {errOut}");
                        }
                    }

                    if (DeleteOriginalWav && File.Exists(oggPath) && File.Exists(wavPath))
                    {
                        var (ok, err) = SafeDeleteFile(wavPath);
                        if (!ok)
                        {
                            summary.DeleteErrors.Add((wavPath, err ?? "unknown error"));
                            statusCallback?.Invoke($"変換OK / WAV削除失敗: {Path.GetFileName(wavPath)} / {err}", "Orange");
                        }
                    }
                }
                catch (Exception ex)
                {
                    summary.ConvertErrors.Add((wavPath, ex.Message));
                    statusCallback?.Invoke($"変換失敗: {Path.GetFileName(wavPath)} / {ex.Message}", "Red");
                }
            }

            WriteErrorLog(folderPath, summary);
            return summary;
        }

        public static void WriteErrorLog(string folderPath, BatchSummary summary)
        {
            if (summary.TotalErrorCount == 0) return;

            string logPath = Path.Combine(folderPath, "STFM_error_log.txt");
            try
            {
                using var sw = new StreamWriter(logPath, false, new UTF8Encoding(false));
                sw.WriteLine("ST-Fumen Manager error log");
                sw.WriteLine("===========================\n");

                if (summary.ConvertErrors.Count > 0)
                {
                    sw.WriteLine("[変換失敗]");
                    foreach (var (path, err) in summary.ConvertErrors)
                        sw.WriteLine($"{path} / {err}");
                    sw.WriteLine();
                }

                if (summary.DeleteErrors.Count > 0)
                {
                    sw.WriteLine("[WAV削除失敗]");
                    foreach (var (path, err) in summary.DeleteErrors)
                        sw.WriteLine($"{path} / {err}");
                    sw.WriteLine();
                }

                if (summary.LegacyErrors.Count > 0)
                {
                    sw.WriteLine("[wav.ogg整理失敗]");
                    foreach (var (path, err) in summary.LegacyErrors)
                        sw.WriteLine($"{path} / {err}");
                    sw.WriteLine();
                }

                if (summary.TjaErrors.Count > 0)
                {
                    sw.WriteLine("[TJA更新失敗]");
                    foreach (var (path, err) in summary.TjaErrors)
                        sw.WriteLine($"{path} / {err}");
                    sw.WriteLine();
                }

                summary.LogPath = logPath;
            }
            catch
            {
                // ログ保存エラーは無視
            }
        }
    }
}
