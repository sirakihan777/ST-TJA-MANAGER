using System;
using System.IO;

namespace ST_Fumen_Manager_WPF.Services
{
    public static class ConfigService
    {
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_path.txt");

        public static string LoadLastPath()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string path = File.ReadAllText(ConfigFilePath).Trim();
                    if (Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch
            {
                // エラー発生時は無視してフォールバック
            }

            return Environment.CurrentDirectory;
        }

        public static void SaveLastPath(string path)
        {
            try
            {
                File.WriteAllText(ConfigFilePath, path);
            }
            catch
            {
                // 保存失敗時は無視
            }
        }
    }
}
