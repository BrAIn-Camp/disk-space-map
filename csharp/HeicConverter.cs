using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace DiskSpaceMap;

/// <summary>
/// Converts HEIC/HEIF photos (which browsers can't show) to JPEG for previewing. Converted files are cached on disk,
/// keyed by the source path, size and modified time, so each photo is converted once and a changed photo is converted again.
/// </summary>
internal static class HeicConverter
{
    private const int MaxDimension = 4096;
    private const int JpegQuality = 85;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly SemaphoreSlim Gate = new(2); // conversions are CPU heavy; don't let a fast clicker start dozens

    private static readonly string CacheDirectory = Path.Combine(DiskSpaceServer.DataDirectory, "cache");

    static HeicConverter()
    {
        // Photos are untrusted input: cap what a hostile file can make the decoder allocate.
        ResourceLimits.Width = 30000;
        ResourceLimits.Height = 30000;
        ResourceLimits.Memory = 1024UL * 1024 * 1024;
        ResourceLimits.Time = 60;
    }

    /// <summary>Deletes cached previews that haven't been written in a week. Called once at server start.</summary>
    public static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(CacheDirectory))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > CacheLifetime) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Returns the path of a JPEG version of the photo, or an error message.</summary>
    public static async Task<(string? Path, string? Error)> GetJpegAsync(string sourcePath, CancellationToken cancel)
    {
        FileInfo source;
        try { source = new FileInfo(sourcePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "The file could not be read."); }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
        string target = Path.Combine(CacheDirectory, key + ".jpg");
        if (File.Exists(target)) return (target, null);

        await Gate.WaitAsync(cancel);
        try
        {
            if (File.Exists(target)) return (target, null); // another request finished it while we waited
            Directory.CreateDirectory(CacheDirectory);
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await Task.Run(() =>
                {
                    // Format is fixed so the file's own contents can't make ImageMagick pick some other, riskier decoder.
                    using var image = new MagickImage(source.FullName, new MagickReadSettings { Format = MagickFormat.Heic });
                    image.AutoOrient();
                    image.Resize(new MagickGeometry((uint)MaxDimension, (uint)MaxDimension) { Greater = true });
                    image.Format = MagickFormat.Jpeg;
                    image.Quality = JpegQuality;
                    image.Strip(); // no location or camera metadata in the cached copy
                    image.Write(temp);
                }, cancel);
                File.Move(temp, target, overwrite: true);
                return (target, null);
            }
            catch (MagickException) { return (null, "This HEIC photo couldn't be decoded (it may be damaged or use an unsupported variant)."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "The photo couldn't be converted (disk error)."); }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }
        finally { Gate.Release(); }
    }
}
