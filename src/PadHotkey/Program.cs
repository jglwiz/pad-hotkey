using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PadHotkey;
using PadHotkey.Core;

var options = Options.Parse(args);
if (options.Command == "help") { Console.WriteLine(Options.Help); return; }
if (options.Command == "start")
{
    var launch = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
    if (options.Backend == "hid") launch.Verb = "runas";
    foreach (var value in new[] { "serve", "--backend", options.Backend, "--data", options.Data,
        "--port", options.Port.ToString(), "--index", options.Index.ToString(), "--seconds", options.Seconds.ToString(),
        "--paused", options.Paused.ToString() }) launch.ArgumentList.Add(value);
    using var child = System.Diagnostics.Process.Start(launch);
    Console.WriteLine("PadHotkey launch requested; use status to verify readiness.");
    return;
}
if (options.Command != "serve") { await Cli.Run(options); return; }

// Keep a handle open on a named semaphore for process-wide exclusivity without
// thread-affine Mutex ownership across async Main continuations.
using var singleton = new Semaphore(1, 1, @"Local\PadHotkey-v1");
if (!singleton.WaitOne(0)) throw new InvalidOperationException("PadHotkey is already running in this session.");
try
{
    var storage = new Storage(options.Data);
    using var runtime = new Runtime(storage, options.Backend, options.Index, options.Paused);
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, options.Port); k.Limits.MaxRequestBodySize = 65536; });
    var app = builder.Build();
    app.Use(async (ctx, next) =>
    {
        try
        {
            if (app.Lifetime.ApplicationStopping.IsCancellationRequested) { ctx.Response.StatusCode = 503; return; }
            if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost") || ctx.Request.Headers.ContainsKey("Origin"))
            { ctx.Response.StatusCode = 403; return; }
            var expected = Encoding.UTF8.GetBytes("Bearer " + storage.Token);
            var actual = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) { ctx.Response.StatusCode = 401; return; }
            if (ctx.Request.Method is "PUT" or "POST" && ctx.Request.ContentLength > 0 && !ctx.Request.HasJsonContentType())
            { ctx.Response.StatusCode = 415; return; }
            await next(ctx);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or RevisionException or BadHttpRequestException)
        {
            ctx.Response.StatusCode = ex switch { RevisionException => 412, InvalidOperationException => 409, _ => 400 };
            await ctx.Response.WriteAsJsonAsync(new { error = ex is RevisionException ? "Revision changed; re-read the profile." : ex.Message });
        }
    });
    app.MapGet("/v1/status", () => Results.Json(runtime.Status()));
    app.MapGet("/v1/profile", (HttpContext ctx) =>
    {
        var p = runtime.GetProfile(); ctx.Response.Headers.ETag = $"\"{p.Version}\""; return Results.Json(p, Storage.Json);
    });
    static int Revision(HttpContext ctx)
    {
        if (!int.TryParse(ctx.Request.Headers.IfMatch.ToString().Trim('"'), out var rev) || rev < 1)
            throw new ArgumentException("If-Match header with the current revision is required.");
        return rev;
    }
    app.MapPut("/v1/profile", async (HttpContext ctx) =>
    {
        var p = await JsonSerializer.DeserializeAsync<Profile>(ctx.Request.Body, Storage.Json, ctx.RequestAborted) ?? throw new ArgumentException("Profile required.");
        p = runtime.Update(p, Revision(ctx)); ctx.Response.Headers.ETag = $"\"{p.Version}\""; return Results.Json(p, Storage.Json);
    });
    app.MapPut("/v1/profile/bindings/{id}", async (string id, HttpContext ctx) =>
    {
        var b = await JsonSerializer.DeserializeAsync<Binding>(ctx.Request.Body, Storage.Json, ctx.RequestAborted) ?? throw new ArgumentException("Binding required.");
        var p = runtime.UpdateBinding(id, b, Revision(ctx)); ctx.Response.Headers.ETag = $"\"{p.Version}\""; return Results.Json(p, Storage.Json);
    });
    app.MapPut("/v1/runtime", async (HttpContext ctx) =>
    {
        var change = await JsonSerializer.DeserializeAsync<RuntimeUpdate>(ctx.Request.Body, Storage.Json, ctx.RequestAborted) ?? throw new ArgumentException("Runtime state required.");
        runtime.Pause(change.Paused); return Results.Json(runtime.Status());
    });
    app.MapPost("/v1/release-all", () => { runtime.Pause(true); return Results.Json(runtime.Status()); });
    app.MapPost("/v1/shutdown", (HttpContext ctx) =>
    {
        runtime.Pause(true);
        ctx.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
        return Results.Json(new { stopping = true });
    });
    app.MapPost("/v1/diagnostics", async (HttpContext ctx) =>
    {
        var request = await JsonSerializer.DeserializeAsync<DiagnosticRequest>(ctx.Request.Body, Storage.Json, ctx.RequestAborted) ?? throw new ArgumentException("Diagnostic required.");
        return Results.Json(new { id = runtime.Diagnose(request) }, statusCode: 202);
    });
    app.MapGet("/v1/events", (long? after) => Results.Json(runtime.Events(after ?? 0)));
    app.MapGet("/v1/events/stream", async (HttpContext ctx) =>
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
        var streamToken = streamCancellation.Token;
        long seq = 0;
        try { while (!streamToken.IsCancellationRequested)
        {
            foreach (var entry in runtime.Events(seq))
            {
                await ctx.Response.WriteAsync($"id: {entry.Sequence}\ndata: {JsonSerializer.Serialize(entry)}\n\n", streamToken);
                seq = entry.Sequence;
            }
            await ctx.Response.WriteAsync(": heartbeat\n\n", streamToken);
            await ctx.Response.Body.FlushAsync(streamToken);
            await Task.Delay(500, streamToken);
        } } catch (OperationCanceledException) { }
    });
    app.MapGet("/v1/schema", () => Results.Json(new
    {
        version = 1, keys = KeyCatalog.Keys, actions = new[] { "tap", "hold" },
        note = "PUT uses the current ETag as If-Match. Keys use HID physical US positions; non-US layouts may produce different characters.",
        inputs = ProfileValidator.Inputs,
        example = new Profile()
    }));
    using var cancellation = new CancellationTokenSource();
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try { runtime.Pause(true); }
        catch (Exception ex) { Console.Error.WriteLine("Shutdown release failed: " + ex.Message); }
        finally { cancellation.Cancel(); }
    });
    var polling = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
        try { while (await timer.WaitForNextTickAsync(cancellation.Token)) runtime.Tick(); }
        catch (OperationCanceledException) { }
    });
    if (options.Seconds > 0) _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(options.Seconds)); app.Lifetime.StopApplication(); });
    Console.WriteLine($"PadHotkey {options.Backend} ready (paused={options.Paused}) at http://127.0.0.1:{options.Port}; token file: {Path.Combine(options.Data, "api-token.txt")}");
    try { await app.RunAsync(); }
    finally { cancellation.Cancel(); await polling; }
}
finally { singleton.Release(); }

namespace PadHotkey
{
    public sealed record Options(string Command, string Data, string Backend, int Port, int Index, int Seconds, string? File, string? Button, string? Hold, string? Tap, bool Paused)
    {
        public const string Help = "PadHotkey start|serve|status|profile|apply|bind|pause|resume|release|stop|monitor|diagnose [--data DIR] [--port 17843]\nserve: --backend hid|dry-run --index 0 --seconds N (0 = continuous); starts mapping immediately; --paused true for diagnostics\napply: --file profile.json; bind: --button B --hold RightAlt OR --tap LeftCtrl+F10\nmonitor: --seconds 10; diagnose: --hold RightAlt OR --tap LeftCtrl+F10 --seconds 5 (paused runtime required)\nToken read from data/api-token.txt. No secrets on command line.";
        public static Options Parse(string[] args)
        {
            var command = args.Length == 0 ? "start" : args[0];
            if (!new[] { "help", "start", "serve", "status", "profile", "apply", "bind", "pause", "resume", "release", "stop", "monitor", "diagnose" }.Contains(command)) throw new ArgumentException(Help);
            var map = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !new[] { "--data", "--backend", "--port", "--index", "--seconds", "--file", "--button", "--hold", "--tap", "--paused" }.Contains(args[i])) throw new ArgumentException(Help);
                map.Add(args[i], args[i + 1]);
            }
            string? Get(string key) => map.GetValueOrDefault("--" + key);
            int Number(string key, int fallback) => Get(key) is string v ? int.Parse(v) : fallback;
            var data = Path.GetFullPath(Get("data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PadHotkey"));
            var o = new Options(command, data, Get("backend") ?? "hid", Number("port", 17843), Number("index", 0), Number("seconds", command is "serve" or "start" ? 0 : 10), Get("file"), Get("button"), Get("hold"), Get("tap"), bool.Parse(Get("paused") ?? "false"));
            if (o.Backend is not ("hid" or "dry-run") || o.Port is < 1024 or > 65535 || o.Index is < 0 or > 3 || o.Seconds is < 0 or > 86400) throw new ArgumentException("Invalid backend, port, index or seconds.");
            return o;
        }
    }

    public static class Cli
    {
        public static async Task Run(Options o)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{o.Port}"), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", File.ReadAllText(Path.Combine(o.Data, "api-token.txt")).Trim());
            async Task<string> Send(HttpMethod method, string path, object? body = null, string? etag = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, Storage.Json), Encoding.UTF8, "application/json");
                if (etag != null) request.Headers.TryAddWithoutValidation("If-Match", etag);
                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");
                return text;
            }
            switch (o.Command)
            {
                case "status": Console.WriteLine(await Send(HttpMethod.Get, "/v1/status")); break;
                case "profile": Console.WriteLine(await Send(HttpMethod.Get, "/v1/profile")); break;
                case "pause": case "resume": Console.WriteLine(await Send(HttpMethod.Put, "/v1/runtime", new RuntimeUpdate { Paused = o.Command == "pause" })); break;
                case "release": Console.WriteLine(await Send(HttpMethod.Post, "/v1/release-all")); break;
                case "stop": Console.WriteLine(await Send(HttpMethod.Post, "/v1/shutdown")); break;
                case "apply": case "bind":
                    var profile = JsonSerializer.Deserialize<Profile>(await Send(HttpMethod.Get, "/v1/profile"), Storage.Json)!;
                    if (o.Command == "apply")
                    {
                        var replacement = JsonSerializer.Deserialize<Profile>(File.ReadAllText(o.File ?? throw new ArgumentException("--file required")), Storage.Json)!;
                        Console.WriteLine(await Send(HttpMethod.Put, "/v1/profile", replacement, $"\"{profile.Version}\""));
                    }
                    else
                    {
                        if ((o.Hold == null) == (o.Tap == null) || o.Button == null) throw new ArgumentException("--button and exactly one of --hold / --tap required.");
                        var binding = profile.Bindings.FirstOrDefault(b => b.Input == o.Button) ?? new Binding { Id = o.Button.ToLowerInvariant(), Input = o.Button };
                        binding.Mode = o.Hold != null ? "hold" : "tap"; binding.Keys = (o.Hold ?? o.Tap)!.Split('+');
                        Console.WriteLine(await Send(HttpMethod.Put, "/v1/profile/bindings/" + Uri.EscapeDataString(binding.Id), binding, $"\"{profile.Version}\""));
                    }
                    break;
                case "monitor":
                    if (o.Seconds is < 1 or > 300) throw new ArgumentException("monitor --seconds must be 1..300");
                    for (int i = 0; i < o.Seconds * 5; i++) { Console.WriteLine(await Send(HttpMethod.Get, "/v1/status")); await Task.Delay(200); }
                    Console.WriteLine(await Send(HttpMethod.Get, "/v1/events")); break;
                case "diagnose":
                    if ((o.Hold == null) == (o.Tap == null) || o.Seconds is < 1 or > 15) throw new ArgumentException("Exactly one --hold / --tap, --seconds 1..15 required.");
                    Console.WriteLine(await Send(HttpMethod.Post, "/v1/diagnostics", new DiagnosticRequest((o.Hold ?? o.Tap)!.Split('+'), o.Hold != null ? "hold" : "tap", o.Seconds * 1000, 3000)));
                    await Task.Delay(o.Seconds * 1000 + 3500);
                    Console.WriteLine(await Send(HttpMethod.Get, "/v1/status"));
                    Console.WriteLine(await Send(HttpMethod.Get, "/v1/events")); break;
            }
        }
    }
}
