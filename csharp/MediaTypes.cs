namespace DiskSpaceMap;

/// <summary>
/// The only file types the server will ever send to the browser for previewing. Anything not listed here is refused,
/// so scripts, executables, HTML and other active content can never be served through the preview endpoint.
/// The list is limited to formats browsers normally play or show without extra codecs.
/// </summary>
internal static class MediaTypes
{
    /// <summary>Convert is true for formats browsers can't show, which the server converts to JPEG first.</summary>
    public sealed record Entry(string Kind, string ContentType, bool Convert = false);

    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = new("image", "image/jpeg"),
        [".jpeg"] = new("image", "image/jpeg"),
        [".png"] = new("image", "image/png"),
        [".gif"] = new("image", "image/gif"),
        [".webp"] = new("image", "image/webp"),
        [".bmp"] = new("image", "image/bmp"),
        [".avif"] = new("image", "image/avif"),
        [".heic"] = new("image", "image/jpeg", Convert: true),
        [".heif"] = new("image", "image/jpeg", Convert: true),
        [".ico"] = new("image", "image/x-icon"),
        [".svg"] = new("image", "image/svg+xml"),

        [".mp4"] = new("video", "video/mp4"),
        [".m4v"] = new("video", "video/mp4"),
        // QuickTime files share the MP4 container. Chrome plays them (when the codec is supported) only if they are
        // served as video/mp4, not video/quicktime.
        [".mov"] = new("video", "video/mp4"),
        [".webm"] = new("video", "video/webm"),
        [".ogv"] = new("video", "video/ogg"),

        [".mp3"] = new("audio", "audio/mpeg"),
        [".wav"] = new("audio", "audio/wav"),
        [".ogg"] = new("audio", "audio/ogg"),
        [".oga"] = new("audio", "audio/ogg"),
        [".opus"] = new("audio", "audio/ogg"),
        [".m4a"] = new("audio", "audio/mp4"),
        [".aac"] = new("audio", "audio/aac"),
        [".flac"] = new("audio", "audio/flac"),

        [".pdf"] = new("pdf", "application/pdf"),
    };

    public static bool TryGet(string extension, out Entry entry) => Map.TryGetValue(extension, out entry!);

    /// <summary>Extension (lower case, with the dot) to kind, for the page to decide what is previewable.</summary>
    public static IReadOnlyDictionary<string, string> Kinds { get; } = Map.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Kind);
}
