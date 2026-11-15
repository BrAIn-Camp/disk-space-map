using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DiskSpaceMap;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();
string localAddress = "http://127.0.0.1:5088";
app.Urls.Clear();
app.Urls.Add(localAddress);

var status = new ScanStatus();
ScanData? scanData = null;
CancellationTokenSource? activeScan = null;
object scanGate = new();
JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
string snapshotDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceMap", "scans");
Directory.CreateDirectory(snapshotDirectory);

app.MapGet("/", async context =>
{
    Assembly assembly = Assembly.GetExecutingAssembly();
    string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("wwwroot.index.html", StringComparison.Ordinal));
    if (resource is null) { context.Response.StatusCode = 500; return; }
    await using Stream stream = assembly.GetManifestResourceStream(resource)!;
    context.Response.ContentType = "text/html; charset=utf-8";
    await stream.CopyToAsync(context.Response.Body);
});
app.MapGet("/api/status", () => status.Snapshot());
app.MapGet("/api/data", () => scanData is null ? Results.NoContent() : Results.Json(scanData, jsonOptions));
app.MapPost("/api/scan", (ScanRequest request) =>
{
    lock (scanGate)
    {
        if (status.State == "scanning") return Results.Conflict(new { error = "A scan is already running." });
        activeScan?.Dispose();
        activeScan = new CancellationTokenSource();
        scanData = null;
        status.Start();
    }
    string root = string.IsNullOrWhiteSpace(request.Path) ? (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar : request.Path;
    int workers = Math.Clamp(request.Workers ?? 4, 1, 16);
    CancellationToken token = activeScan.Token;
    _ = Task.Run(async () =>
    {
        try
        {
            var scanner = new DiskScanner(status, workers);
            ScanData completed = await scanner.ScanAsync(root, token);
            scanData = completed;
            status.Complete();
        }
        catch (OperationCanceledException) { status.Cancel(); }
        catch (Exception ex) { status.Fail(ex.Message); }
    });
    return Results.Accepted("/api/status", new { state = "scanning", path = root, workers });
});
app.MapPost("/api/cancel", () =>
{
    lock (scanGate) { activeScan?.Cancel(); }
    return Results.Accepted("/api/status", new { state = "cancelling" });
});
app.MapPost("/api/save", async (SnapshotRequest request) =>
{
    ScanData? current = scanData;
    if (current is null) return Results.Conflict(new { error = "There is no completed scan to save." });
    string filename = string.IsNullOrWhiteSpace(request.Name) ? $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json" : Path.GetFileName(request.Name);
    if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) filename += ".json";
    string destination = Path.Combine(snapshotDirectory, filename);
    await using FileStream file = File.Create(destination);
    await JsonSerializer.SerializeAsync(file, current, jsonOptions);
    return Results.Ok(new { saved = filename, directory = snapshotDirectory });
});
app.MapGet("/api/snapshots", () => Results.Json(Directory.EnumerateFiles(snapshotDirectory, "*.json").Select(Path.GetFileName).OrderByDescending(x => x).ToArray()));
app.MapPost("/api/load", async (SnapshotRequest request) =>
{
    string filename = Path.GetFileName(request.Name ?? "");
    string source = Path.Combine(snapshotDirectory, filename);
    if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) return Results.NotFound(new { error = "Snapshot not found." });
    await using FileStream file = File.OpenRead(source);
    ScanData? loaded = await JsonSerializer.DeserializeAsync<ScanData>(file, jsonOptions);
    if (loaded is null) return Results.BadRequest(new { error = "Snapshot is invalid." });
    scanData = loaded;
    status.Complete();
    return Results.Ok(new { loaded = filename });
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    try { Process.Start(new ProcessStartInfo(localAddress) { UseShellExecute = true }); }
    catch { }
});
await app.RunAsync();

internal sealed record ScanRequest(string? Path, int? Workers);
internal sealed record SnapshotRequest(string? Name);
