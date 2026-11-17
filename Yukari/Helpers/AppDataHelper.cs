using System;
using System.IO;
using Yukari.Models.DTO;

namespace Yukari.Helpers;

public static class AppDataHelper
{
    // Keep the original Yukari storage path so existing libraries and settings survive the Mana rebrand.
    private static readonly string _appDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Yukari"
    );

    public static string GetAppDataPath() => EnsureDirectory(_appDataPath);

    public static string GetDataPath() => EnsureDirectory(Path.Combine(GetAppDataPath(), "Data"));

    // Keep diagnostic logs easy to find in the current user's home directory:
    // C:\\Users\\<user>\\logs on Windows, /home/<user>/logs on Linux.
    public static string GetLogsPath()
    {
        var homeLogsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "logs"
        );
        try
        {
            return EnsureDirectory(homeLogsPath);
        }
        catch (UnauthorizedAccessException)
        {
            // Packaged Windows apps may not be allowed to write directly under the profile.
            return EnsureDirectory(Path.Combine(GetAppDataPath(), "Logs"));
        }
        catch (IOException)
        {
            return EnsureDirectory(Path.Combine(GetAppDataPath(), "Logs"));
        }
    }

    public static string GetDatabasePath() => Path.Combine(GetDataPath(), "yukari.db");

    public static string GetPluginsPath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "Plugins"));

    public static string GetPluginImagesPath() =>
        EnsureDirectory(Path.Combine(GetPluginsPath(), "Images"));

    public static string GetComicDataPath(ContentKey comicKey) =>
        EnsureDirectory(GetComicPath(comicKey));

    public static string GetComicChapterDataPath(ContentKey comicKey, string chapterId) =>
        EnsureDirectory(GetChapterPath(comicKey, chapterId));

    public static void DeleteComicDataPath(ContentKey comicKey) =>
        DeleteDirectory(GetComicPath(comicKey));

    public static void DeleteComicChapterDataPath(ContentKey comicKey, string chapterId) =>
        DeleteDirectory(GetChapterPath(comicKey, chapterId));

    public static string CopyDllToPluginsDirectory(string sourceDllPath)
    {
        string fileName = Path.GetFileName(sourceDllPath);
        string destPath = Path.Combine(GetPluginsPath(), fileName);
        File.Copy(sourceDllPath, destPath, true);
        return destPath;
    }

    private static string GetComicPath(ContentKey comicKey) =>
        Path.Combine(GetDataPath(), comicKey.Source, comicKey.Id);

    private static string GetChapterPath(ContentKey comicKey, string chapterId) =>
        Path.Combine(GetComicDataPath(comicKey), chapterId);

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
