// AppSettings.cs
//
// mde (MarkDown インラインエディタ) の一部。
// アプリ終了時のウィンドウ状態（サイズ・位置・最大化有無・フォルダ/アウトラインペインの
// 表示有無・表示倍率）を記憶し、次回起動時に復元するための設定クラス。
// %AppData%\mde\settings.json にJSON形式で保存する。

using System;
using System.IO;
using System.Text.Json;

namespace mde
{
    /// <summary>次回起動時に復元する、ウィンドウ・ペインの状態一式。</summary>
    public class AppSettings
    {
        public double WindowWidth { get; set; } = 1240;
        public double WindowHeight { get; set; } = 860;
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public bool IsMaximized { get; set; } = false;
        public bool FolderPaneVisible { get; set; } = true;
        public bool OutlinePaneVisible { get; set; } = true;
        public double ZoomLevel { get; set; } = 1.0;

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mde", "settings.json");

        /// <summary>保存済みの設定を読み込む。ファイルが存在しない、または壊れている場合は
        /// 既定値を返す。</summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch
            {
                // 読み込みに失敗しても、既定値で起動を続ける
            }
            return new AppSettings();
        }

        /// <summary>現在の設定をディスクへ保存する。</summary>
        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // 保存に失敗しても致命的ではない（ベストエフォート）
            }
        }
    }
}
