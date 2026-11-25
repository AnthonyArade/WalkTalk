using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WhisperStt.Core;

namespace WhisperStt.Desktop.Services;

// Keeps the Twitch sign-in between runs. The tokens let anyone holding them post chat messages as the
// user, so they are encrypted with Windows data protection: only this Windows account on this PC can
// read the file, and they are never written to the ordinary settings file.
public static class TwitchTokenStore
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("WalkTalk.Twitch");

    // Settable so tests can keep their sign-in apart from the user's.
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "twitch-sign-in.bin");

    public static TwitchSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Purpose, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchSession>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null; // unreadable (copied from another PC, damaged): the user simply connects again
        }
    }

    public static void Save(TwitchSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(session);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(json, Purpose, DataProtectionScope.CurrentUser));
    }

    public static void Clear() => File.Delete(FilePath);
}
