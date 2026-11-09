using System;
using System.IO;

namespace DevicePower;

/// <summary>
/// 远程排障只有这一个入口：%APPDATA%\DevicePower\dock.log。只在内容变化时追加，
/// 免得每秒一条把文件刷满。
/// </summary>
internal static class DiagLog
{
    private static string _path;
    private static string _last = "";

    public static void WriteIfChanged(string line)
    {
        if (line == _last) return;
        _last = line;

        try
        {
            if (_path == null)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevicePower");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "dock.log");
            }

            if (File.Exists(_path) && new FileInfo(_path).Length > 200000) File.Delete(_path);
            File.AppendAllText(_path,
                DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
