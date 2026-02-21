using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
// bind to a known localhost port for the MAUI app to call (local development)
builder.WebHost.UseUrls("http://localhost:5000");

builder.Services.AddCors(options => options.AddPolicy("Default", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("Default");

// store the key in a per-user app data directory (outside the repo) so it won't be committed
var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RealWeatherApp");
Directory.CreateDirectory(dataDir);
var keyFile = Path.Combine(dataDir, "openweather.key");

// If the repo has a development appsettings file, import its key at startup for convenience
try
{
    // backend runs in KeyBackend/, project root is parent directory of KeyBackend
    var repoDevSettings = Path.Combine(AppContext.BaseDirectory, "..", "..", "Properties", "appsettings.development.json");
    repoDevSettings = Path.GetFullPath(repoDevSettings);
    if (File.Exists(repoDevSettings) && !File.Exists(keyFile))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(repoDevSettings));
            if (doc.RootElement.TryGetProperty("OpenWeather", out var ow) && ow.TryGetProperty("ApiKey", out var k))
            {
                var key = k.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key)) File.WriteAllText(keyFile, key);
            }
        }
        catch
        {
            // ignore JSON parse issues
        }
    }
}
catch
{
    // ignore seeding failures
}

// Get key
app.MapGet("/api/key", () =>
{
    if (!File.Exists(keyFile)) return Results.NotFound(new { message = "API key not set" });
    var key = File.ReadAllText(keyFile);
    return Results.Ok(new { apiKey = key });
});

// Set key (body JSON: { "apiKey": "..." })
app.MapPost("/api/key", async (HttpContext ctx) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        if (!body.TryGetProperty("apiKey", out var apiKeyEl)) return Results.BadRequest(new { message = "Missing apiKey" });
        var apiKey = apiKeyEl.GetString() ?? string.Empty;
        File.WriteAllText(keyFile, apiKey);
        return Results.Ok(new { message = "Key saved" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Delete key
app.MapDelete("/api/key", () =>
{
    if (File.Exists(keyFile)) File.Delete(keyFile);
    return Results.Ok(new { message = "Key deleted" });
});

app.Run();
