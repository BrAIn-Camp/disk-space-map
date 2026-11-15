using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

using System.Text.Json.Serialization;

namespace DiskSpaceMap;

internal sealed class DiskScanner
{
    private const long KeepBytes = 25L * 1024 * 1024;
    private const int TopFileLimit = 300;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeOffline = 0x1000;
    private const uint FileAttributeRecallOnDataAccess = 0x400000;

    private readonly SemaphoreSlim _workers;
    private readonly ConcurrentDictionary<string, long[]> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Path, long Size)> _largest = new();
    private readonly ConcurrentQueue<string> _denied = new();
    private readonly ScanStatus _status;
    private long _fileCount, _directoryCount, _cloudCount, _cloudBytes, _linksSkipped, _bytes;

    public DiskScanner(ScanStatus status, int workerCount)
    {
        _status = status;
        _workers = new SemaphoreSlim(Math.Clamp(workerCount, 1, 16));
    }

    public async Task<ScanData> ScanAsync(string rootPath, CancellationToken cancellationToken)
    {
        string drive = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? throw new ArgumentException("Choose a valid drive or folder.");
        DateTimeOffset started = DateTimeOffset.Now;
        DiskNode root = await ScanDirectoryAsync(rootPath, Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath)) is { Length: > 0 } rootName ? rootName : Path.GetPathRoot(rootPath) ?? rootPath, cancellationToken);
        DriveInfo? driveInfo = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(Path.GetPathRoot(d.Name), drive, StringComparison.OrdinalIgnoreCase));
        long total = driveInfo?.TotalSize ?? root.Size;
        long free = driveInfo?.TotalFreeSpace ?? 0;
        long used = Math.Max(0, total - free);
        bool scanningWholeVolume = string.Equals(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), drive.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        long hidden = scanningWholeVolume ? used - root.Size : 0;
        if (hidden > KeepBytes)
        {
            root.Children ??= [];
            root.Children.Add(new DiskNode("(unreadable without admin: system / restore points)", hidden, 0) { Unreadable = true });
            root.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            root.Size += hidden;
        }

        return new ScanData
        {
            Drive = drive.TrimEnd(Path.DirectorySeparatorChar),
            ScannedAt = started.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Seconds = (int)Math.Round((DateTimeOffset.Now - started).TotalSeconds),
            Total = total,
            Used = used,
            Free = free,
            Stats = new ScanStats(_fileCount, _directoryCount, _cloudCount, _cloudBytes, _linksSkipped, _bytes),
            DeniedCount = _denied.Count,
            Denied = _denied.Take(300).ToList(),
            Extensions = _extensions.OrderByDescending(kv => kv.Value[0]).Take(40).Select(kv => new[] { JsonSerializer.SerializeToElement(kv.Key), JsonSerializer.SerializeToElement(kv.Value[0]), JsonSerializer.SerializeToElement(kv.Value[1]) }).ToList(),
            Biggest = _largest.OrderByDescending(x => x.Size).Take(TopFileLimit).Select(x => new[] { JsonSerializer.SerializeToElement(x.Path), JsonSerializer.SerializeToElement(x.Size) }).ToList(),
            Tree = root
        };
    }

    private async Task<DiskNode> ScanDirectoryAsync(string path, string name, CancellationToken token)
    {
        Interlocked.Increment(ref _directoryCount);
        _status.SetCurrent(path, Interlocked.Read(ref _directoryCount), Interlocked.Read(ref _fileCount), Interlocked.Read(ref _bytes));
        FileSystemInfo[] entries;
        await _workers.WaitAsync(token);
        try
        {
            try { entries = new DirectoryInfo(path).EnumerateFileSystemInfos().ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _denied.Enqueue(path);
                return new DiskNode(name, 0, 0) { Denied = true };
            }
        }
        finally { _workers.Release(); }

        var node = new DiskNode(name, 0, 0);
            var childDirectories = new List<(string Path, string Name)>();
            foreach (FileSystemInfo entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (IsLink(entry.FullName)) { Interlocked.Increment(ref _linksSkipped); continue; }
                    }
                    childDirectories.Add((entry.FullName, entry.Name));
                    continue;
                }

                long size;
                try { size = ((FileInfo)entry).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
                if ((attributes & (FileAttributes.Offline | (FileAttributes)FileAttributeRecallOnDataAccess)) != 0)
                {
                    Interlocked.Increment(ref _cloudCount);
                    Interlocked.Add(ref _cloudBytes, size);
                    continue;
                }

                Interlocked.Increment(ref _fileCount);
                Interlocked.Add(ref _bytes, size);
                node.Size += size;
                node.Files++;
                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (string.IsNullOrEmpty(extension)) extension = "(no extension)";
                long[] tally = _extensions.GetOrAdd(extension, _ => new long[2]);
                Interlocked.Add(ref tally[0], size);
                Interlocked.Increment(ref tally[1]);
                lock (_largest)
                {
                    _largest.Enqueue((entry.FullName, size));
                    if (_largest.Count > TopFileLimit * 2)
                    {
                        var keep = _largest.OrderByDescending(x => x.Size).Take(TopFileLimit).ToArray();
                        while (_largest.TryDequeue(out _)) { }
                        foreach (var item in keep) _largest.Enqueue(item);
                    }
                }
                if (size >= KeepBytes) (node.Children ??= []).Add(new DiskNode(entry.Name, size, 1) { IsFile = true });
                else { node.SmallBytes += size; node.SmallFiles++; }
            }

            var children = new List<DiskNode>(childDirectories.Count);
            for (int offset = 0; offset < childDirectories.Count; offset += 4)
            {
                var batch = childDirectories.Skip(offset).Take(4)
                    .Select(child => Task.Run(() => ScanDirectoryAsync(child.Path, child.Name, token), token));
                children.AddRange(await Task.WhenAll(batch));
            }
            foreach (DiskNode child in children)
            {
                node.Size += child.Size;
                node.Files += child.Files;
                if (child.Size >= KeepBytes) (node.Children ??= []).Add(child);
                else { node.SmallBytes += child.Size; node.SmallFiles += child.Files; }
            }
            if (node.Children is { Count: > 0 })
            {
                if (node.SmallBytes > 0) node.Children.Add(new DiskNode("(smaller items)", node.SmallBytes, node.SmallFiles) { Other = true });
                node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
        _status.SetCurrent(path, Interlocked.Read(ref _directoryCount), Interlocked.Read(ref _fileCount), Interlocked.Read(ref _bytes));
        return node;
    }

    private static bool IsLink(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            uint tag = GetReparseTag(path);
            return tag is 0xA0000003 or 0xA000000C;
        }
        catch { return true; }
    }

    private static uint GetReparseTag(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return 0;
        var buffer = new byte[8];
        return DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero) ? BitConverter.ToUInt32(buffer, 0) : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, IntPtr inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}

internal sealed class ScanStatus
{
    private readonly object _gate = new();
    public string State { get; private set; } = "idle";
    public string CurrentPath { get; private set; } = "";
    public long Directories { get; private set; }
    public long Files { get; private set; }
    public long Bytes { get; private set; }
    public string? Error { get; private set; }
    public void Start() { lock (_gate) { State = "scanning"; Error = null; CurrentPath = ""; Directories = Files = Bytes = 0; } }
    public void SetCurrent(string path, long dirs, long files, long bytes) { lock (_gate) { CurrentPath = path; Directories = dirs; Files = files; Bytes = bytes; } }
    public void Complete() { lock (_gate) State = "complete"; }
    public void Fail(string error) { lock (_gate) { State = "error"; Error = error; } }
    public void Cancel() { lock (_gate) State = "cancelled"; }
    public object Snapshot() { lock (_gate) return new { state = State, currentPath = CurrentPath, directories = Directories, files = Files, bytes = Bytes, error = Error }; }
}

internal sealed class ScanData
{
    [JsonPropertyName("drive")] public string Drive { get; set; } = "";
    [JsonPropertyName("scanned_at")] public string ScannedAt { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("used")] public long Used { get; set; }
    [JsonPropertyName("free")] public long Free { get; set; }
    [JsonPropertyName("stats")] public ScanStats Stats { get; set; } = new(0, 0, 0, 0, 0, 0);
    [JsonPropertyName("denied_count")] public int DeniedCount { get; set; }
    [JsonPropertyName("denied")] public List<string> Denied { get; set; } = [];
    [JsonPropertyName("exts")] public List<JsonElement[]> Extensions { get; set; } = [];
    [JsonPropertyName("biggest")] public List<JsonElement[]> Biggest { get; set; } = [];
    [JsonPropertyName("tree")] public DiskNode Tree { get; set; } = new("", 0, 0);
}
internal sealed record ScanStats([property: JsonPropertyName("files")] long Files, [property: JsonPropertyName("dirs")] long Directories, [property: JsonPropertyName("cloud_files")] long CloudFiles, [property: JsonPropertyName("cloud_bytes")] long CloudBytes, [property: JsonPropertyName("links_skipped")] long LinksSkipped, [property: JsonPropertyName("bytes")] long Bytes);
internal sealed record ExtensionStat([property: JsonPropertyName("ext")] string Extension, [property: JsonPropertyName("bytes")] long Bytes, [property: JsonPropertyName("count")] long Count);
internal sealed record BiggestFile([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("size")] long Size);
internal sealed class DiskNode(string name, long size, long files)
{
    [JsonPropertyName("n")] public string Name { get; set; } = name;
    [JsonPropertyName("s")] public long Size { get; set; } = size;
    [JsonPropertyName("f")] public long Files { get; set; } = files;
    [JsonPropertyName("c")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<DiskNode>? Children { get; set; }
    [JsonPropertyName("k")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsFile { get; set; }
    [JsonPropertyName("o")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Other { get; set; }
    [JsonPropertyName("u")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Unreadable { get; set; }
    [JsonPropertyName("d")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Denied { get; set; }
    [JsonIgnore] public long SmallBytes { get; set; }
    [JsonIgnore] public long SmallFiles { get; set; }
}
