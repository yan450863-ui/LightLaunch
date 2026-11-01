using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace LightLaunch;

internal sealed record PictureChoice(string? StoredName, byte[]? Bytes, string? Extension);

/// <summary>Stores owned copies separately from managed software and original images.</summary>
internal sealed class SoftwarePictures
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const long MaximumPixels = 40_000_000;
    private readonly string root;
    public SoftwarePictures(string dataDirectory) => root = Path.Combine(Path.GetFullPath(dataDirectory), "软件图片");

    public static PictureChoice ReadSelection(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumBytes) throw new IOException("图片为空或超过 32 MB，请选择较小的图片。");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        if ((long)image.Width * image.Height > MaximumPixels) throw new IOException("图片尺寸过大，请选择不超过 4000 万像素的图片。");
        string extension = image.RawFormat.Guid == ImageFormat.Png.Guid ? ".png"
            : image.RawFormat.Guid == ImageFormat.Jpeg.Guid ? ".jpg"
            : image.RawFormat.Guid == ImageFormat.Bmp.Guid ? ".bmp"
            : image.RawFormat.Guid == ImageFormat.Gif.Guid ? ".gif"
            : throw new IOException("请选择 PNG、JPG、BMP 或 GIF 图片。");
        return new(null, bytes, extension);
    }

    public static Bitmap Decode(byte[] bytes, int maxSide = 0)
    {
        using var stream = new MemoryStream(bytes, false);
        using var image = Image.FromStream(stream, false, true);
        if ((long)image.Width * image.Height > MaximumPixels) throw new IOException("图片尺寸过大。");
        int longest = Math.Max(image.Width, image.Height);
        if (maxSide <= 0 || longest <= maxSide) return new Bitmap(image);
        double scale = (double)maxSide / longest;
        var thumbnail = new Bitmap(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        try {
            using var graphics = Graphics.FromImage(thumbnail);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, 0, 0, thumbnail.Width, thumbnail.Height);
            return thumbnail;
        } catch { thumbnail.Dispose(); throw; }
    }

    public Bitmap? TryLoad(string? name, int maxSide = 0)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try {
            string path = StoredPath(name); ValidateDirectory(false);
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaximumBytes) return null;
            byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            return Decode(bytes, maxSide);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException) { return null; }
    }

    public string Import(PictureChoice selection)
    {
        if (selection.Bytes is null || selection.Extension is null) throw new ArgumentException("未选择新的图片。");
        ValidateDirectory(true);
        string name = Guid.NewGuid().ToString("N") + selection.Extension;
        string path = StoredPath(name);
        bool created = false;
        try {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            created = true;
            file.Write(selection.Bytes); file.Flush(true); return name;
        } catch { if (created) DeleteNewCopy(name); throw; }
    }

    public void DeleteNewCopy(string name)
    {
        try { ValidateDirectory(false); File.Delete(StoredPath(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private string StoredPath(string name)
    {
        if (!Regex.IsMatch(name, @"\A[0-9a-f]{32}\.(png|jpg|bmp|gif)\z", RegexOptions.CultureInvariant)) throw new ArgumentException("图片记录无效。");
        return Path.Combine(root, name);
    }

    private void ValidateDirectory(bool create)
    {
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("图片数据目录包含链接，未写入。");
        if (create) Directory.CreateDirectory(root);
    }
}
