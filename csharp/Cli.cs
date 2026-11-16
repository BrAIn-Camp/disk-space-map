using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace DiskSpaceMap;

/// <summary>Command line front end: starts, talks to, and stops the background server.</summary>
internal static class Cli
{
    private const string HelpText = """
        Usage: DiskSpaceMap [start | stop | status] [folder] [options]

          (no arguments)   same as 'start'
          start            start the background server if needed and open the home page
          stop             stop the background server
          status           show whether the server is running
          <folder>         scan the folder (starting the server first if needed); opens a new tab for it

        Options:
          --min-size SIZE  smallest item shown on the map, e.g. 1MB or 500KB
                           (default: automatic, 25MB stepping down if nothing is found)
          --workers N      parallel directory readers, 1-16 (default 4)
          --no-open        don't open a browser tab, just print the address
          -h, --help       show this help
        """;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<int> RunAsync(string[] args)
    {
        string? verb = null, path = null;
        bool serve = false, noOpen = false;
        int workers = 4;
        double? minMb = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--serve": serve = true; break; // internal: run as the background server
                case "--no-open": noOpen = true; break;
                case "-h" or "--help" or "/?": Console.WriteLine(HelpText); return 0;
                case "--workers":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out workers)) return Fail("--workers needs a number.");
                    break;
                case "--min-size":
                    long? bytes = i + 1 < args.Length ? ParseSize(args[++i]) : null;
                    if (bytes is null) return Fail("--min-size needs a size like 1MB or 500KB.");
                    minMb = bytes.Value / (1024.0 * 1024.0);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal)) return Fail($"Unknown option {arg}. Try --help.");
                    if (verb is null && path is null && arg.ToLowerInvariant() is "start" or "stop" or "status") verb = arg.ToLowerInvariant();
                    else if (path is null) path = arg;
                    else return Fail($"Unexpected argument {arg}. Try --help.");
                    break;
            }
        }

        if (serve) return await DiskSpaceServer.RunAsync();
        if (verb is "stop" or "status" && path is not null) return Fail($"'{verb}' doesn't take a folder.");
        return verb switch
        {
            "stop" => await StopAsync(),
            "status" => await StatusAsync(),
            _ => await StartAsync(path, workers, minMb, noOpen),
        };
    }

    private static async Task<int> StartAsync(string? path, int workers, double? minMb, bool noOpen)
    {
        string? folder = null;
        if (path is not null)
        {
            try { folder = Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception) { return Fail($"Not a valid path: {path}"); }
            if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");
        }

        string? baseUrl = await FindServerAsync();
        if (baseUrl is null)
        {
            baseUrl = await SpawnServerAsync();
            if (baseUrl is null) return Fail("The server did not start.");
            Console.WriteLine($"Started DiskSpaceMap at {baseUrl}");
        }
        else Console.WriteLine($"DiskSpaceMap is already running at {baseUrl}");

        string url = baseUrl + "/";
        if (folder is not null)
        {
            try
            {
                using HttpResponseMessage response = await Http.PostAsJsonAsync(baseUrl + "/api/scan", new ScanRequest(folder, workers, minMb), DiskSpaceServer.JsonOptions);
                using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!response.IsSuccessStatusCode) return Fail(body.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "Scan failed." : "Scan failed.");
                url = $"{baseUrl}/?scan={body.RootElement.GetProperty("id").GetInt32()}";
                Console.WriteLine($"Scanning {folder}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Fail($"Could not reach the server: {ex.Message}"); }
        }

        Console.WriteLine(url);
        if (!noOpen)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { Console.WriteLine("(Could not open a browser; open the address above.)"); }
        }
        return 0;
    }

    private static async Task<int> StopAsync()
    {
        string? baseUrl = await FindServerAsync();
        if (baseUrl is null) { Console.WriteLine("DiskSpaceMap is not running."); return 0; }
        try { using HttpResponseMessage _ = await Http.PostAsync(baseUrl + "/api/shutdown", null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        for (int i = 0; i < 50; i++)
        {
            if (await FindServerAsync() is null) { Console.WriteLine("Stopped DiskSpaceMap."); return 0; }
            await Task.Delay(200);
        }
        return Fail("The server did not stop in time.");
    }

    private static async Task<int> StatusAsync()
    {
        string? baseUrl = await FindServerAsync();
        if (baseUrl is null) { Console.WriteLine("DiskSpaceMap is not running."); return 0; }
        try
        {
            using JsonDocument ping = JsonDocument.Parse(await Http.GetStringAsync(baseUrl + "/api/ping"));
            Console.WriteLine($"DiskSpaceMap is running at {baseUrl} (pid {ping.RootElement.GetProperty("pid").GetInt32()}, {ping.RootElement.GetProperty("scans").GetInt32()} scan(s) in memory)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Console.WriteLine($"DiskSpaceMap is running at {baseUrl}"); }
        return 0;
    }

    /// <summary>Returns the address of the running server, or null if there isn't a responsive one.</summary>
    private static async Task<string?> FindServerAsync()
    {
        try
        {
            if (!File.Exists(DiskSpaceServer.InfoFile)) return null;
            ServerInfo? info = JsonSerializer.Deserialize<ServerInfo>(await File.ReadAllTextAsync(DiskSpaceServer.InfoFile), DiskSpaceServer.JsonOptions);
            if (info is null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using JsonDocument ping = JsonDocument.Parse(await Http.GetStringAsync(info.Address + "/api/ping", timeout.Token));
            return ping.RootElement.TryGetProperty("app", out JsonElement app) && app.GetString() == DiskSpaceServer.AppName ? info.Address : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or OperationCanceledException) { return null; }
    }

    /// <summary>Launches this program again as a detached background server and waits for it to come up.</summary>
    private static async Task<string?> SpawnServerAsync()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find this program's path.");
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirecting the standard streams stops the server holding on to this console's pipes.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Under 'dotnet run' / 'dotnet DiskSpaceMap.dll' the process is dotnet itself and the dll is an argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        startInfo.ArgumentList.Add("--serve");

        using Process? server = Process.Start(startInfo);
        server?.StandardInput.Close();

        for (int i = 0; i < 75; i++)
        {
            await Task.Delay(200);
            if (await FindServerAsync() is { } address) return address;
        }
        return null;
    }

    /// <summary>Parses "25", "25MB", "500KB" or "1GB" into bytes (a bare number means MB).</summary>
    private static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim().ToUpperInvariant();
        long unit = 1024 * 1024;
        if (t.EndsWith("GB", StringComparison.Ordinal)) { unit = 1024L * 1024 * 1024; t = t[..^2]; }
        else if (t.EndsWith("MB", StringComparison.Ordinal)) t = t[..^2];
        else if (t.EndsWith("KB", StringComparison.Ordinal)) { unit = 1024; t = t[..^2]; }
        return double.TryParse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 ? (long)(v * unit) : null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
