using System.Security.Cryptography;
using Revu.Sidecar;
using Velopack;

// Overwolf Electron owns the desktop lifecycle. The sidecar owns persistence,
// migrations, application workflows, and the authenticated loopback API.
var isolatedHostTest = Environment.GetEnvironmentVariable("REVU_ISOLATED_HOST_TEST") == "1";
// Electron applies updates only after the sidecar has drained accepted writes.
// A backend child must never independently apply a staged update on startup.
if (!isolatedHostTest) VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

// Acquire ownership before configuration migration, database access, or DI resolution.
using var hostSession = SidecarStartup.AcquireSession(isolatedHostTest);
// The desktop host discards console output. Ownership is held, so this process is the
// only writer of the rolling log beside the handshake (<data root>/Revu/logs).
using var fileLog = new SidecarFileLoggerProvider(SidecarFileLoggerProvider.DefaultDirectory);
AppDomain.CurrentDomain.UnhandledException += (_, e) => fileLog.WriteUnhandled(e.ExceptionObject);
fileLog.Write(LogLevel.Information, "Revu.Sidecar", $"Sidecar {typeof(Program).Assembly.GetName().Version} starting (process {Environment.ProcessId})", null);
// Storage preparation can take a while; let the host see that this launch is alive.
hostSession.PublishStarting();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(fileLog);
// Framework request chatter stays out of the file; its warnings and errors do not.
builder.Logging.AddFilter<SidecarFileLoggerProvider>("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter<SidecarFileLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Logging.AddFilter<SidecarFileLoggerProvider>("System.Net.Http", LogLevel.Warning);
// Binding failures reach the JSON error handler instead of an empty 400.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});
builder.Services.AddSidecarServices(isolatedHostTest);

var bearerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var app = builder.Build();
var backgroundWork = app.Services.GetRequiredService<SidecarBackgroundWork>();
await app.InitializeStorageAsync(isolatedHostTest);
app.UseSidecarApi(bearerToken, isolatedHostTest);
app.MapSidecarEndpoints(SidecarJson.CreateOptions(), hostSession);
app.PublishWhenStarted(hostSession, bearerToken, isolatedHostTest);
app.Run();
if (backgroundWork.ShutdownIncomplete) Environment.ExitCode = 1;

// Public for host integration tests without exposing a second application entry point.
public partial class Program { }
