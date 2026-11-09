using System.IO;
using System.Windows.Media.Imaging;

namespace YinggeVideoLibrary;

// Review thumbnails use a small independent, decoded-byte LRU. Requests for
// the same file version share one worker; no bitmap is decoded on the caller's
// dispatcher. The UI owns cancellation of its subscription, not the shared job.
internal static class ScrapeCoverImageCache
{
    internal const long LimitBytes = 32L * 1024 * 1024;
    readonly record struct Key(string Path, int Width, long Length, long Modified);
    sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<Key> Node);
    static readonly object Gate = new();
    static readonly Dictionary<Key, Entry> Images = [];
    static readonly Dictionary<Key, Task<BitmapSource?>> Pending = [];
    static readonly LinkedList<Key> Order = [];
    static readonly SemaphoreSlim Workers = new(3, 3);
    static long bytes, hits, decodes, failures;

    internal static (long Bytes, long Limit, int Entries, int Pending, long Hits, long Decodes, long Failures) Stats()
    { lock (Gate) return (bytes, LimitBytes, Images.Count, Pending.Count, hits, decodes, failures); }

    internal static long EstimateDecodedBytes(int width, int height, int bitsPerPixel)
    {
        if (width <= 0 || height <= 0 || bitsPerPixel <= 0) return long.MaxValue;
        try
        {
            // WIC may expose 24/48/64-bit pixels. Use a DWORD-aligned row
            // stride, rather than assuming four bytes per pixel.
            return checked((((long)width * bitsPerPixel + 31) / 32 * 4) * height);
        }
        catch (OverflowException) { return long.MaxValue; }
    }

    internal static Task<BitmapSource?> GetAsync(string path, int decodeWidth)
    {
        Key key;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var file = new FileInfo(full);
            if (!file.Exists) return Task.FromResult<BitmapSource?>(null);
            key = new Key(full.ToUpperInvariant(), Math.Clamp(decodeWidth, 1, 2048), file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Task.FromResult<BitmapSource?>(null); }

        lock (Gate)
        {
            if (Images.TryGetValue(key, out var cached))
            {
                hits++;
                Order.Remove(cached.Node); Order.AddLast(cached.Node);
                return Task.FromResult<BitmapSource?>(cached.Image);
            }
            if (Pending.TryGetValue(key, out var existing)) return existing;
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[key] = completion.Task;
            _ = LoadAsync(key, fullPath: path, completion);
            return completion.Task;
        }
    }

    static async Task LoadAsync(Key key, string fullPath, TaskCompletionSource<BitmapSource?> completion)
    {
        BitmapSource? image = null;
        await Workers.WaitAsync().ConfigureAwait(false);
        try
        {
            image = await Task.Run<BitmapSource?>(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = key.Width;
                bitmap.UriSource = new Uri(System.IO.Path.GetFullPath(fullPath), UriKind.Absolute);
                bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            }).ConfigureAwait(false);
            lock (Gate)
            {
                decodes++;
                var imageBytes = EstimateDecodedBytes(image!.PixelWidth, image.PixelHeight, image.Format.BitsPerPixel);
                if (imageBytes <= LimitBytes)
                {
                    // Keep only the latest version of this file at this width.
                    foreach (var stale in Images.Keys.Where(old => old.Path == key.Path && old.Width == key.Width).ToArray())
                        Remove(stale);
                    while (bytes + imageBytes > LimitBytes && Order.First is { } oldest) Remove(oldest.Value);
                    Images[key] = new Entry(image, imageBytes, Order.AddLast(key));
                    bytes += imageBytes;
                }
            }
        }
        catch (Exception error) when (error is IOException or System.IO.FileFormatException or UnauthorizedAccessException or NotSupportedException
                                            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { lock (Gate) failures++; }
        finally
        {
            Workers.Release();
            lock (Gate) Pending.Remove(key);
            completion.TrySetResult(image);
        }
    }

    static void Remove(Key key)
    {
        if (!Images.Remove(key, out var value)) return;
        Order.Remove(value.Node); bytes -= value.Bytes;
    }
}
