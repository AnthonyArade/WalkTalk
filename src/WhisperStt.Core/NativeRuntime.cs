using System.Runtime.InteropServices;

namespace WhisperStt.Core;

// Whisper's native libraries need the Visual C++ runtime, which a freshly installed Windows may not have.
// WalkTalk carries a copy (the Native folder beside the app's files). Windows' own copy is used when
// there is one, since Windows keeps it up to date; otherwise the carried copy is loaded. Once a copy
// is in memory, Whisper's libraries find it by name.
public static class NativeRuntime
{
    // In the order they depend on each other.
    private static readonly string[] Files = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];
    private static readonly Lock Gate = new();
    private static bool _loaded;

    // Settable so tests can check that the carried copy is enough on its own.
    public static bool UseSystemCopy { get; set; } = true;

    // Call before the first use of Whisper. Safe to call any number of times.
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            foreach (var file in Files)
                if (!(UseSystemCopy && NativeLibrary.TryLoad(file, out _)))
                    NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "Native", file), out _);
        }
    }
}
