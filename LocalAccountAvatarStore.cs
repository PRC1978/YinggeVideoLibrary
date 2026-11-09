using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YinggeVideoLibrary;

// The local copy works offline. Only a cropped, re-encoded PNG is synchronized;
// the original path, EXIF and library data never leave this Windows profile.
internal static class LocalAccountAvatarStore
{
    const long MaxSourceBytes = 8 * 1024 * 1024;
    const long MaxSourcePixels = 40_000_000;
    const int MaxCloudBytes = 240 * 1024;

    internal static string PathFor(string endpoint, string username)
    {
        var key = endpoint.Trim().TrimEnd('/').ToLowerInvariant() + "\n" + username.Trim().ToLowerInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(AppPaths.DataDirectory, "account-avatars", digest + ".png");
    }

    internal static BitmapSource? Load(string endpoint, string username)
    {
        try
        {
            var path = PathFor(endpoint, username);
            if (!File.Exists(path)) return null;
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Decode(input, 512);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidOperationException or FormatException or
            System.Runtime.InteropServices.COMException) { return null; }
    }

    internal static void Save(string endpoint, string username, string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length == 0 || source.Length > MaxSourceBytes)
            throw new InvalidDataException("请选择不超过 8 MB 的有效图片。");
        using var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        SaveCore(endpoint, username, input);
    }

    internal static void SaveBytes(string endpoint, string username, byte[] png)
    {
        if (png.Length == 0 || png.Length > MaxCloudBytes)
            throw new InvalidDataException("云端头像大小不合法。");
        using var input = new MemoryStream(png, writable: false);
        SaveCore(endpoint, username, input);
    }

    internal static byte[]? ReadForCloud(string endpoint, string username)
    {
        var path = PathFor(endpoint, username);
        if (!File.Exists(path)) return null;
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoded = Decode(input, 192);
        var side = Math.Min(decoded.PixelWidth, decoded.PixelHeight);
        var square = new CroppedBitmap(decoded, new Int32Rect(
            (decoded.PixelWidth - side) / 2, (decoded.PixelHeight - side) / 2, side, side));
        var png = Encode(square);
        if (png.Length > MaxCloudBytes) throw new InvalidDataException("头像编码后超过云端大小限制。");
        return png;
    }

    static void SaveCore(string endpoint, string username, Stream input)
    {
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None).Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaxSourcePixels)
            throw new InvalidDataException("图片尺寸过大，请选一张不超过 4000 万像素的图片。");
        input.Position = 0;
        var decoded = Decode(input, 512);
        var side = Math.Min(decoded.PixelWidth, decoded.PixelHeight);
        var square = new CroppedBitmap(decoded, new Int32Rect(
            (decoded.PixelWidth - side) / 2, (decoded.PixelHeight - side) / 2, side, side));
        BitmapSource image = side > 256
            ? new TransformedBitmap(square, new ScaleTransform(256d / side, 256d / side))
            : square;

        var target = PathFor(endpoint, username);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Encode(image));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Delete(string endpoint, string username)
    {
        var path = PathFor(endpoint, username);
        if (File.Exists(path)) File.Delete(path);
    }

    static byte[] Encode(BitmapSource image)
    {
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
        encoder.Save(output);
        return output.ToArray();
    }

    static BitmapImage Decode(Stream stream, int width)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
