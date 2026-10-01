using JobHunting.Server;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Personal overrides live in data/settings.json, next to the rest of the personal data.
// They beat appsettings.json; environment variables and the command line still beat them.
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration[$"{JobHuntingOptions.Section}:RepoRoot"] ?? "../.."));
builder.Configuration
    .AddJsonFile(Path.Combine(repoRoot, "data", "settings.json"), optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var section = builder.Configuration.GetSection(JobHuntingOptions.Section);
builder.Services.Configure<JobHuntingOptions>(section);
builder.Services.AddSingleton(new Paths(section.Get<JobHuntingOptions>() ?? new(), builder.Environment.ContentRootPath));
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<ResumeRenderer>();
builder.Services.AddSingleton<JobPipeline>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));

// Full-page captures (rendered HTML of every frame) can be several MB.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 100 * 1024 * 1024);

var app = builder.Build();

DataFolder.Initialize(app.Services.GetRequiredService<Paths>());
app.Services.GetRequiredService<JobStore>().Load();

// The dashboard is built into src/web/dist (see BuildSpa in the .csproj).
var spaDir = Path.Combine(repoRoot, "src", "web", "dist");
Directory.CreateDirectory(spaDir);
var spa = new StaticFileOptions { FileProvider = new PhysicalFileProvider(spaDir) };
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spa.FileProvider });
app.UseStaticFiles(spa);
app.MapApi();
app.MapFallbackToFile("index.html", spa);

app.Run();
