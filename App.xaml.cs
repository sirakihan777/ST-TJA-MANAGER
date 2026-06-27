using System;
using System.Text;
using System.Windows;
using ST_Fumen_Manager_WPF.Services;

namespace ST_Fumen_Manager_WPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Shift_JISなどのエンコーディングを扱えるようにプロバイダーを登録
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (!AudioConverter.CheckFfmpegAvailable())
            {
                MessageBox.Show(
                    "ffmpegが見つかりません。環境変数PATHに設定されているか確認してください。\n一部の音声変換機能が正常に動作しない可能性があります。",
                    "エラー - ffmpeg未検出",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
