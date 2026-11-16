using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DiskSpaceMap;

internal sealed record ServerInfo(int Pid, string Address);
internal sealed record ScanRequest(string? Path, int? Workers, double? MinSizeMb);
internal sealed record SnapshotRequest(string? Name);
internal sealed record PathRequest(string? Path, string? Shell);

/// <summary>One scan (running, finished or loaded from a snapshot) that a browser tab can view.</summary>
internal sealed class ScanSession(int id)
{
    public int Id { get; } = id;
    public ScanStatus Status { get; } = new();
    public ScanData? Data { get; set; }
    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>The background web server. There is one per user; the command line talks to it over HTTP.</summary>
internal static class DiskSpaceServer
{
    public const string AppName = "DiskSpaceMap";
    public static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static readonly string InfoFile = Path.Combine(DataDirectory, "server.json");
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<int, ScanSession> Sessions = new();
    private static int _nextId;

    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        string snapshotDirectory = Path.Combine(DataDirectory, "scans");
        Directory.CreateDirectory(snapshotDirectory);
        HeicConverter.CleanCache();

        // Only one server per user: whoever holds this lock file is the server.
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return 0; }

        using (lockFile)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            WebApplication app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0"); // any free port; the real one goes in the info file

            // Only this machine's own pages may talk to the server. Checking Host blocks DNS-rebinding and
            // checking Origin blocks other websites from posting to the local port (this server can launch programs).
            app.Use(async (context, next) =>
            {
                string? origin = context.Request.Headers.Origin.FirstOrDefault();
                bool hostOk = context.Request.Host.Host is "127.0.0.1" or "localhost";
                bool originOk = origin is null || string.Equals(origin, $"http://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
                if (!hostOk || !originOk) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                await next();
            });

            ScanSession? Get(HttpRequest http) => int.TryParse(http.Query["scan"], out int id) && Sessions.TryGetValue(id, out ScanSession? s) ? s : null;

            app.MapGet("/", context => ServeEmbedded(context, "wwwroot.index.html", "text/html; charset=utf-8"));
            app.MapGet("/preview", context => ServeEmbedded(context, "wwwroot.preview.html", "text/html; charset=utf-8"));
            app.MapGet("/viewer.js", context => ServeEmbedded(context, "wwwroot.viewer.js", "text/javascript; charset=utf-8"));
            app.MapGet("/api/ping", () => new { app = AppName, pid = Environment.ProcessId, scans = Sessions.Count });
            app.MapGet("/api/status", (HttpRequest http) => Get(http)?.Status.Snapshot() ?? new ScanStatus().Snapshot());
            app.MapGet("/api/data", (HttpRequest http) => Get(http)?.Data is { } data ? Results.Json(data, JsonOptions) : Results.NoContent());
            app.MapPost("/api/scan", (ScanRequest request) =>
            {
                long? minBytes = request.MinSizeMb is { } mb && mb >= 0 ? (long)(mb * 1024 * 1024) : null;
                (ScanSession? session, string? error) = StartScan(request.Path, request.Workers ?? 4, minBytes);
                return session is null ? Results.BadRequest(new { error }) : Results.Ok(new { id = session.Id });
            });
            app.MapPost("/api/cancel", (HttpRequest http) =>
            {
                Get(http)?.Cts.Cancel();
                return Results.Accepted();
            });
            app.MapPost("/api/save", async (HttpRequest http, SnapshotRequest request) =>
            {
                ScanData? current = Get(http)?.Data;
                if (current is null) return Results.Conflict(new { error = "There is no completed scan to save." });
                string filename = string.IsNullOrWhiteSpace(request.Name) ? $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json" : Path.GetFileName(request.Name);
                if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) filename += ".json";
                await using FileStream file = File.Create(Path.Combine(snapshotDirectory, filename));
                await JsonSerializer.SerializeAsync(file, current, JsonOptions);
                return Results.Ok(new { saved = filename, directory = snapshotDirectory });
            });
            app.MapGet("/api/snapshots", () => Results.Json(Directory.EnumerateFiles(snapshotDirectory, "*.json").Select(Path.GetFileName).OrderByDescending(x => x).ToArray()));
            app.MapPost("/api/load", async (SnapshotRequest request) =>
            {
                string filename = Path.GetFileName(request.Name ?? "");
                string source = Path.Combine(snapshotDirectory, filename);
                if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) return Results.NotFound(new { error = "Snapshot not found." });
                await using FileStream file = File.OpenRead(source);
                ScanData? loaded = await JsonSerializer.DeserializeAsync<ScanData>(file, JsonOptions);
                if (loaded is null) return Results.BadRequest(new { error = "Snapshot is invalid." });
                var session = new ScanSession(Interlocked.Increment(ref _nextId)) { Data = loaded };
                session.Status.Complete();
                Sessions[session.Id] = session;
                return Results.Ok(new { id = session.Id, loaded = filename });
            });
            app.MapGet("/api/capabilities", () => new
            {
                os = Launcher.OsName,
                sep = Path.DirectorySeparatorChar.ToString(),
                fileManager = Launcher.FileManagerLabel,
                shells = Launcher.AvailableShells(),
                preview = MediaTypes.Kinds,
            });
            // Streams a previewable file (image, video, audio, PDF) to the browser, with range support so video can seek.
            app.MapGet("/api/file", async (HttpContext context) =>
            {
                ScanSession? session = Get(context.Request);
                string? path = ResolveWithinScan(session, context.Request.Query["path"]);
                if (path is null) return Results.BadRequest(new { error = "That path is not part of this scan." });
                if (!MediaTypes.TryGet(Path.GetExtension(path), out MediaTypes.Entry? type)) return Results.BadRequest(new { error = "That file type can't be previewed." });
                if (!File.Exists(path)) return Results.NotFound(new { error = "That file no longer exists." });
                if (PassesThroughLink(RootOf(session!.Data!), path)) return Results.BadRequest(new { error = "Links are not followed." });

                string servePath = path;
                if (type.Convert)
                {
                    (string? converted, string? convertError) = await HeicConverter.GetJpegAsync(path, context.RequestAborted);
                    if (converted is null) return Results.BadRequest(new { error = convertError });
                    servePath = converted;
                }

                IHeaderDictionary headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["X-Frame-Options"] = "SAMEORIGIN";
                headers["Cache-Control"] = "no-cache";
                // If the file is ever opened directly, nothing in it (e.g. an SVG with a script) may run. PDFs are excluded because the
                // browser's PDF viewer does not work inside a sandbox; it runs in its own isolated context.
                if (type.Kind != "pdf") headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
                try { return Results.Stream(new FileStream(servePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), type.ContentType, enableRangeProcessing: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.BadRequest(new { error = "The file could not be read." }); }
            });
            app.MapPost("/api/reveal", (HttpRequest http, PathRequest request) =>
            {
                string? path = ResolveWithinScan(Get(http), request.Path);
                if (path is null) return Results.BadRequest(new { error = "That path is not part of this scan." });
                string? error = Launcher.Reveal(path);
                return error is null ? Results.Ok(new { opened = true }) : Results.BadRequest(new { error });
            });
            app.MapPost("/api/open", (HttpRequest http, PathRequest request) =>
            {
                string? path = ResolveWithinScan(Get(http), request.Path);
                if (path is null) return Results.BadRequest(new { error = "That path is not part of this scan." });
                string? error = Launcher.OpenFile(path, out bool revealed);
                return error is null ? Results.Ok(new { opened = !revealed, revealed }) : Results.BadRequest(new { error });
            });
            app.MapPost("/api/shell", (HttpRequest http, PathRequest request) =>
            {
                string? path = ResolveWithinScan(Get(http), request.Path);
                if (path is null) return Results.BadRequest(new { error = "That path is not part of this scan." });
                string? error = Launcher.LaunchShell(request.Shell ?? "", path);
                return error is null ? Results.Ok(new { launched = true }) : Results.BadRequest(new { error });
            });
            app.MapPost("/api/shutdown", () =>
            {
                foreach (ScanSession s in Sessions.Values) s.Cts.Cancel();
                _ = Task.Run(async () => { await Task.Delay(200); app.Lifetime.StopApplication(); });
                return Results.Ok(new { stopping = true });
            });

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                File.WriteAllText(InfoFile, JsonSerializer.Serialize(new ServerInfo(Environment.ProcessId, address), JsonOptions));
            });
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                try { File.Delete(InfoFile); } catch (IOException) { }
            });

            await app.RunAsync();
            return 0;
        }
    }

    private static string DefaultRoot() => OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static async Task ServeEmbedded(HttpContext context, string nameSuffix, string contentType)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(nameSuffix, StringComparison.Ordinal));
        if (resource is null) { context.Response.StatusCode = 500; return; }
        await using Stream stream = assembly.GetManifestResourceStream(resource)!;
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(context.Response.Body);
    }

    private static string RootOf(ScanData data) => data.Root.Length > 0 ? data.Root : data.Drive + Path.DirectorySeparatorChar; // older snapshots have no root

    /// <summary>True if the file, or any folder between it and the scan root, is a symbolic link or junction.</summary>
    private static bool PassesThroughLink(string root, string fullPath)
    {
        try
        {
            char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            string rootTrimmed = Path.GetFullPath(root).TrimEnd(separators);
            if (new FileInfo(fullPath).LinkTarget is not null) return true;
            for (string? dir = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(dir) && dir.TrimEnd(separators).Length > rootTrimmed.Length; dir = Path.GetDirectoryName(dir))
                if (new DirectoryInfo(dir).LinkTarget is not null) return true;
            return false;
        }
        catch (Exception) { return true; } // can't tell, so don't serve it
    }

    /// <summary>Returns the full path if it lies inside the folder this scan covered, otherwise null.</summary>
    private static string? ResolveWithinScan(ScanSession? session, string? path)
    {
        if (session?.Data is not { } data || string.IsNullOrWhiteSpace(path)) return null;
        string root = RootOf(data);
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            string full = Path.GetFullPath(path);
            string rootPrefix = Path.GetFullPath(root).TrimEnd(separators);
            bool inside = full.TrimEnd(separators).Equals(rootPrefix, comparison) || full.StartsWith(rootPrefix + Path.DirectorySeparatorChar, comparison);
            return inside ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Validates the folder and starts scanning it in the background. Returns the new session, or an error message.</summary>
    private static (ScanSession? Session, string? Error) StartScan(string? requestedPath, int workerCount, long? minBytes)
    {
        string root = string.IsNullOrWhiteSpace(requestedPath) ? DefaultRoot() : requestedPath.Trim().Trim('"');
        try { root = Path.GetFullPath(root); }
        catch (Exception) { return (null, $"Not a valid path: {root}"); }
        if (!Directory.Exists(root)) return (null, $"Folder not found: {root}");

        int workers = Math.Clamp(workerCount, 1, 16);
        var session = new ScanSession(Interlocked.Increment(ref _nextId));
        Sessions[session.Id] = session;
        session.Status.Start(root);
        CancellationToken token = session.Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // No explicit minimum: start at 25 MB and step down if the map would be empty.
                long[] ladder = minBytes is { } explicitMin ? [explicitMin] : [DiskScanner.DefaultKeepBytes, 1024L * 1024, 64L * 1024];
                ScanData completed = null!;
                foreach (long keep in ladder)
                {
                    completed = await new DiskScanner(session.Status, workers, keep).ScanAsync(root, token);
                    if (completed.Tree.Children is { Count: > 0 }) break;
                }
                session.Data = completed;
                session.Status.Complete();
            }
            catch (OperationCanceledException) { session.Status.Cancel(); }
            catch (Exception ex) { session.Status.Fail(ex.Message); }
        });
        return (session, null);
    }
}
