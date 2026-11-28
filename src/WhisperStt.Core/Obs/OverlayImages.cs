namespace WhisperStt.Core.Obs;

// Background pictures for the OBS canvases. The chosen picture is copied here, so moving or deleting the
// original doesn't break the overlay. One picture per canvas, named after the canvas.
public static class OverlayImages
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    // Settable so tests can work in a folder of their own.
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "overlay-images");

    // Copies the picture for a canvas and returns the name it is kept under.
    public static string Import(string canvasId, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new InvalidOperationException("Choose a PNG, JPG, GIF, WebP or BMP picture.");
        System.IO.Directory.CreateDirectory(Directory);
        Remove(canvasId);
        var name = canvasId + extension;
        File.Copy(sourcePath, Path.Combine(Directory, name), overwrite: true);
        return name;
    }

    public static void Remove(string canvasId)
    {
        foreach (var extension in Extensions)
            File.Delete(Path.Combine(Directory, canvasId + extension));
    }

    // -- sounds a canvas plays (typing, sentence, show, hide), copied here like the pictures

    public static readonly string[] SoundExtensions = [".mp3", ".wav", ".ogg", ".m4a", ".aac", ".flac", ".webm"];

    public static string ImportSound(string canvasId, string slot, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SoundExtensions.Contains(extension))
            throw new InvalidOperationException("Choose an MP3, WAV, OGG, M4A, AAC, FLAC or WebM sound.");
        System.IO.Directory.CreateDirectory(Directory);
        RemoveSound(canvasId, slot);
        var name = $"{canvasId}-{slot}{extension}";
        File.Copy(sourcePath, Path.Combine(Directory, name), overwrite: true);
        return name;
    }

    public static void RemoveSound(string canvasId, string slot)
    {
        foreach (var extension in SoundExtensions)
            File.Delete(Path.Combine(Directory, $"{canvasId}-{slot}{extension}"));
    }

    public static void RemoveSounds(string canvasId)
    {
        foreach (var slot in new[] { "typing", "sentence", "show", "hide" })
            RemoveSound(canvasId, slot);
    }

    public static string SoundContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".webm" => "audio/webm",
        _ => "audio/mpeg"
    };

    // -- the avatar's two pictures ("base" and "active"), copied here like the background

    public static readonly string[] AvatarSlots = ["base", "active"];

    public static string ImportAvatar(string canvasId, string slot, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            throw new InvalidOperationException("Choose a PNG, JPG, GIF, WebP or BMP picture.");
        System.IO.Directory.CreateDirectory(Directory);
        RemoveAvatar(canvasId, slot);
        var name = $"{canvasId}-avatar-{slot}{extension}";
        File.Copy(sourcePath, Path.Combine(Directory, name), overwrite: true);
        return name;
    }

    public static void RemoveAvatar(string canvasId, string slot)
    {
        foreach (var extension in Extensions)
            File.Delete(Path.Combine(Directory, $"{canvasId}-avatar-{slot}{extension}"));
    }

    public static void RemoveAvatars(string canvasId)
    {
        foreach (var slot in AvatarSlots)
            RemoveAvatar(canvasId, slot);
    }

    // -- files that come inside an imported template (no file on disk to copy): written straight here

    public static string SaveImage(string canvasId, string extension, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Remove(canvasId);
        return Save(canvasId + Checked(extension, Extensions), bytes);
    }

    public static string SaveSound(string canvasId, string slot, string extension, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        RemoveSound(canvasId, slot);
        return Save($"{canvasId}-{slot}{Checked(extension, SoundExtensions)}", bytes);
    }

    public static string SaveAvatar(string canvasId, string slot, string extension, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        RemoveAvatar(canvasId, slot);
        return Save($"{canvasId}-avatar-{slot}{Checked(extension, Extensions)}", bytes);
    }

    private static string Checked(string extension, string[] allowed) =>
        allowed.Contains(extension.ToLowerInvariant()) ? extension.ToLowerInvariant()
            : throw new InvalidOperationException($"WalkTalk can't use {extension} files here.");

    private static string Save(string name, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, name), bytes);
        return name;
    }

    // A picture of how a canvas looks, taken from its preview, for its tile in the canvas list.
    public static string ThumbnailPath(string canvasId) => Path.Combine(Directory, canvasId + ".thumb.png");

    public static void SaveThumbnail(string canvasId, byte[] png)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(ThumbnailPath(canvasId), png);
    }

    public static void RemoveThumbnail(string canvasId) => File.Delete(ThumbnailPath(canvasId));

    public static string? PathOf(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return null;
        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
