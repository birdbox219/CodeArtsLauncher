using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Launcher.Core.Abstractions;
using Launcher.Core.Services;
using Launcher.Publisher.Publishing;
using Microsoft.AspNetCore.Http.Features;

// ===========================================================================================
// Birdbox Launcher — publishing panel.
//
// A local web UI over `butler push`, so releasing a build is pick game → choose folder → push
// instead of remembering a command line. It is a tool for the person who owns the games; it is
// never part of what players get, for two reasons that are not negotiable:
//
//   1. It runs butler with the local itch.io key. A key that can push builds must not leave this
//      machine, and must never be embedded in a build handed to players.
//   2. It browses the local filesystem on request, because a browser cannot hand a server a real
//      local path and butler needs one.
//
// Hence: bound to 127.0.0.1 only, loopback-checked per request, Host header pinned to localhost so
// a rebinding trick from another site cannot reach it, and a custom header required on anything
// that changes state so a cross-site form post cannot publish a build.
// ===========================================================================================

string? repoRoot = PublisherPaths.FindRepoRoot();
string? butlerPath = PublisherPaths.ResolveButler(repoRoot);

if (butlerPath is null)
{
    Console.Error.WriteLine(
        "butler.exe not found. Expected it at tools\\butler\\butler.exe next to GameLauncher.sln " +
        "(nothing is installed system-wide here), or pointed at by BUTLER_PATH.\n" +
        "Run the panel from inside the repo: dotnet run --project src\\Launcher.Publisher");
    return 1;
}

// Past the null check, so the lambdas below can capture it without a nullable warning.
string butlerExe = butlerPath;

int port = int.TryParse(Environment.GetEnvironmentVariable("PUBLISHER_PORT"), out int fromEnv) ? fromEnv : 5099;
string url = $"http://localhost:{port}";

// --no-browser is ours, not configuration; the command-line config provider rejects a lone flag.
bool openBrowser = !args.Contains("--no-browser");

// The panel is normally started from the repo root (`dotnet run --project src\Launcher.Publisher`),
// so the content root is pinned to the build output — otherwise ASP.NET looks for wwwroot next to
// the solution file and serves nothing.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(a => a != "--no-browser").ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot",
});

builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton<IConfigService>(_ => new LocalJsonConfigService());
builder.Services.AddSingleton<IItchCatalogService>(sp => new ItchProfileCatalogService(
    null,
    sp.GetService<ILogger<ItchProfileCatalogService>>()));
builder.Services.AddSingleton(sp => new ButlerCli(butlerExe, sp.GetService<ILogger<ButlerCli>>()));
builder.Services.AddSingleton<ButlerPublishService>();
builder.Services.AddSingleton<PublisherCatalog>();
builder.Services.AddSingleton<PushJobStore>();

var app = builder.Build();

// ---- local-only guard -------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var ip = context.Connection.RemoteIpAddress;
    if (ip is not null && !IPAddress.IsLoopback(ip))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("The publishing panel only serves localhost.");
        return;
    }

    // Pins the Host header: a page on the internet can point a hostname at 127.0.0.1, but it
    // cannot make the browser send Host: localhost.
    string host = context.Request.Host.Host;
    if (host is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync($"Unexpected Host header '{host}'. Open {url} instead.");
        return;
    }

    // Publishing is a state change, so it needs a header a cross-site form cannot set. This is
    // what stops another tab from pushing a build without you noticing.
    if (HttpMethods.IsPost(context.Request.Method) &&
        !context.Request.Headers.ContainsKey("X-Publisher-Panel"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Missing X-Publisher-Panel header.");
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- what the panel is looking at -------------------------------------------------------------
app.MapGet("/api/state", async (IConfigService config, PushJobStore jobs, CancellationToken ct) =>
{
    var cfg = await config.LoadConfigAsync(ct);
    bool hasKey = PublisherPaths.HasButlerCredentials();

    return Results.Ok(new PanelState(
        cfg.ItchProfileUsername,
        butlerExe,
        hasKey,
        hasKey
            ? $"Using the key from {PublisherPaths.ButlerCredentialsPath}"
            : "No itch.io key found. Run tools\\butler\\butler.exe login first — pushing needs it.",
        cfg.DefaultChannel,
        jobs.Current?.Id));
});

app.MapGet("/api/games", async (bool? refresh, PublisherCatalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.GetAsync(refresh ?? false, ct)));

// ---- picking the folder -----------------------------------------------------------------------
app.MapGet("/api/browse", (string? path) => Results.Ok(FolderBrowser.List(path)));

app.MapPost("/api/inspect", (InspectBody body) => Results.Ok(BuildFolderInspector.Inspect(body.Folder)));

// ---- pushing --------------------------------------------------------------------------------
app.MapPost("/api/push", async (
    PushBody body,
    IConfigService config,
    PushJobStore jobs,
    ButlerPublishService publisher,
    PublisherCatalog catalog,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var cfg = await config.LoadConfigAsync(ct);

    PublishTarget target;
    try
    {
        target = PublishTarget.Parse(
            body.Game ?? "",
            string.IsNullOrWhiteSpace(body.Owner) ? cfg.ItchProfileUsername : body.Owner!,
            string.IsNullOrWhiteSpace(body.Channel) ? cfg.DefaultChannel : body.Channel!);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var report = BuildFolderInspector.Inspect(body.Folder);
    if (!report.CanPush)
        return Results.BadRequest(new { error = string.Join(" ", report.Problems) });

    if (!PublisherPaths.HasButlerCredentials())
    {
        return Results.BadRequest(new
        {
            error = "No itch.io key found. Run tools\\butler\\butler.exe login first."
        });
    }

    var request = new PushRequest(
        target.Owner, target.Slug, target.Channel,
        report.Path,
        string.IsNullOrWhiteSpace(body.Version) ? null : body.Version!.Trim(),
        body.DryRun,
        body.OnlyIfChanged);

    if (!jobs.TryStart(request, out var job, out string? error) || job is null)
        return Results.Conflict(new { error });

    // Runs detached from the request: the browser watches it over /events, and a closed tab must
    // not abort an upload in progress.
    _ = Task.Run(async () =>
    {
        try
        {
            var outcome = await publisher.PushAsync(request, job.Append, job.Cts.Token);
            job.Complete(outcome);

            if (outcome is { Success: true, DryRun: false, Skipped: false })
                catalog.Invalidate();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Push job {Id} failed.", job.Id);
            job.Append(PushEvent.Error($"{ex.GetType().Name}: {ex.Message}"));
            job.Complete(new PushOutcome(false, request.DryRun, false, 0, null, ex.Message));
        }
    }, CancellationToken.None);

    return Results.Ok(new { jobId = job.Id });
});

app.MapGet("/api/push/{id}", (string id, PushJobStore jobs) =>
    jobs.Get(id) is { } job ? Results.Ok(JobSnapshot.From(job)) : Results.NotFound());

app.MapPost("/api/push/{id}/cancel", (string id, PushJobStore jobs) =>
{
    if (jobs.Get(id) is not { } job) return Results.NotFound();
    job.Cts.Cancel();
    return Results.Ok(new { cancelled = true });
});

// Server-sent events: one `data:` line per butler event, then a `done` event carrying the outcome.
app.MapGet("/api/push/{id}/events", async (string id, PushJobStore jobs, HttpContext http, CancellationToken ct) =>
{
    if (jobs.Get(id) is not { } job)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";
    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var (backlog, live) = job.Subscribe();

    async Task Send(string? eventName, object payload)
    {
        if (eventName is not null) await http.Response.WriteAsync($"event: {eventName}\n", ct);
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        foreach (var e in backlog) await Send(null, e);
        if (job.LastProgress is { } p) await Send(null, p);

        await foreach (var e in live.ReadAllAsync(ct)) await Send(null, e);

        await Send("done", JobSnapshot.From(job));
    }
    catch (OperationCanceledException)
    {
        // Tab closed or navigated away. The push keeps going; reconnecting replays the log.
    }
});

Console.WriteLine();
Console.WriteLine($"  Birdbox publishing panel   {url}");
Console.WriteLine($"  butler                    {butlerExe}");
Console.WriteLine($"  itch.io key               {(PublisherPaths.HasButlerCredentials() ? "found" : "MISSING — run butler login")}");
Console.WriteLine("  localhost only. Ctrl+C to stop.");
Console.WriteLine();

if (openBrowser) PublisherPaths.OpenBrowser(url);

app.Run();
return 0;
