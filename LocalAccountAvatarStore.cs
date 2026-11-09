using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YinggeVideoLibrary;

// Account portraits belong to this Windows profile. No image or original path
// is sent to the report service or written to the library configuration.
internal static class LocalAccountAvatarStore
{
    const long MaxSourceBytes = 8 * 1024 * 1024;
    const long MaxSourcePixels = 40_000_000;

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
            var bitmap = Decode(input);
            bitmap.Freeze();
            return bitmap;
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

        BitmapSource image;
        using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 ||
                (long)frame.PixelWidth * frame.PixelHeight > MaxSourcePixels)
                throw new InvalidDataException("图片尺寸过大，请选一张不超过 4000 万像素的图片。");
            input.Position = 0;
            var decoded = Decode(input);
            var side = Math.Min(decoded.PixelWidth, decoded.PixelHeight);
            var square = new CroppedBitmap(decoded, new Int32Rect(
                (decoded.PixelWidth - side) / 2, (decoded.PixelHeight - side) / 2, side, side));
            image = side > 256
                ? new TransformedBitmap(square, new ScaleTransform(256d / side, 256d / side))
                : square;
            image.Freeze();
        }

        var target = PathFor(endpoint, username);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(output);
                output.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Delete(string endpoint, string username)
    {
        var path = PathFor(endpoint, username);
        if (File.Exists(path)) File.Delete(path);
    }

    static BitmapImage Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = 512;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
