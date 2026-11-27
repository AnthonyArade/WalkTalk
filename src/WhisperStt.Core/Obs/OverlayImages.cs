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
