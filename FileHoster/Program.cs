using FileHoster.Configuration;
using FileHoster.Data;
using FileHoster.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Configure Serilog before building the app
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "/app/logs/filehoster-.txt",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        fileSizeLimitBytes: 104857600, // 100 MB per file
        retainedFileCountLimit: 30     // Keep 30 days of logs
    )
    .CreateLogger();

try
{
    Log.Information("Application starting...");
    
    var builder = WebApplication.CreateBuilder(args);
    if (builder.Environment.IsProduction() &&
        string.IsNullOrWhiteSpace(builder.Configuration["TrustedApiNetworks"]))
        throw new InvalidOperationException("TrustedApiNetworks must be configured in production");
    if (builder.Environment.IsProduction())
        foreach (var cidr in builder.Configuration["TrustedApiNetworks"]!
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _ = System.Net.IPNetwork.Parse(cidr);
    if (builder.Environment.IsProduction() &&
        (builder.Configuration["InitialApplication:SecretKey"] is not { Length: >= 32 } initialKey ||
         initialKey.StartsWith("MUST_BE_SET", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("A unique 32+ character InitialApplication:SecretKey is required in production");

    // Use Serilog instead of default logging
    builder.Host.UseSerilog();

    // Add configuration
    builder.Configuration.AddEnvironmentVariables();
    builder.WebHost.ConfigureKestrel(options =>
    {
        Log.Information("Configuring Kestrel options...");
        options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB
        options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
        Log.Information("Kestrel configuration complete");
    });
    
    Log.Information("Configuring application URLs...");
    var urls = builder.Configuration["ASPNETCORE_URLS"] ?? "http://+:5000";
    Log.Information("URLs from config: {Urls}", urls);
    builder.WebHost.UseUrls(urls);
    Log.Information("URLs configured");

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddLogging();

// Configure form options for large uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 500 * 1024 * 1024; // 500 MB
});

// Register application services
builder.Services.AddScoped<IFileService, FileService>();
builder.Services.AddScoped(provider => 
    new StorageService(builder.Configuration["StoragePath"] ?? "/data/files"));
builder.Services.AddScoped<IStorageService>(provider =>
    provider.GetRequiredService<StorageService>());
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<ISignatureService, SignatureService>();

// Configure storage options
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("StorageOptions"));
builder.Services.Configure<SignatureOptions>(builder.Configuration.GetSection("SignatureOptions"));

// Add database
var connectionString = builder.Configuration.GetConnectionString("PostgreSQL");
builder.Services.AddDbContext<FileHosterContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        // Configure connection resilience
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(1),
            errorCodesToAdd: null);
    }),
    ServiceLifetime.Scoped
);

// Note: Connection pooling is configured via connection string or environment
// Default Npgsql pooling: Min 1, Max 100 connections per connection string

var app = builder.Build();

// Initialize database and seed data
Log.Information("Starting database initialization...");

using (var scope = app.Services.CreateScope())
{
    try
    {
        Log.Debug("Resolving database context...");
        var db = scope.ServiceProvider.GetRequiredService<FileHosterContext>();
        
        Log.Information("Running migrations...");
        db.Database.Migrate();
        Log.Information("✅ Migrations completed");

        var storageRoot = builder.Configuration["StoragePath"] ?? "/data/files";
        if (Directory.Exists(storageRoot))
        {
            foreach (var pending in Directory.EnumerateFiles(storageRoot, "*.pending", SearchOption.AllDirectories))
                File.Delete(pending);

            foreach (var quarantined in Directory.EnumerateFiles(storageRoot, "*.deleting", SearchOption.AllDirectories))
            {
                if (quarantined.Length < 42 ||
                    !Guid.TryParseExact(quarantined.Substring(quarantined.Length - 41, 32), "N", out _))
                    continue;
                var original = quarantined[..^(".deleting".Length + 33)];
                if (db.Files.Any(f => f.FilePath == original))
                {
                    if (File.Exists(original))
                        throw new IOException($"Both the stored file and its quarantined copy exist: {original}");
                    File.Move(quarantined, original);
                    Log.Warning("Restored interrupted deletion for {Path}", original);
                }
                else
                {
                    File.Delete(quarantined);
                    Log.Information("Cleaned up committed deletion for {Path}", original);
                }
            }

            foreach (var stored in Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(stored), "N", out _) ||
                    !db.Applications.Any(a => a.Id == Path.GetFileName(Path.GetDirectoryName(stored))))
                    continue;
                if (!db.Files.Any(f => f.FilePath == stored))
                {
                    File.Delete(stored);
                    Log.Warning("Cleaned up interrupted upload for {Path}", stored);
                }
            }
        }
        
        // Seed initial application if needed
        Log.Debug("Checking if SampleApp application exists...");
        var appExists = db.Applications.Any(a => a.Id == "SampleApp");
        Log.Information("Application exists: {AppExists}", appExists);
        
        if (!appExists)
        {
            Log.Debug("Reading InitialApplication:SecretKey...");
            var secretKey = builder.Configuration["InitialApplication:SecretKey"];
            Log.Debug("Secret key configured: {IsConfigured}", !string.IsNullOrEmpty(secretKey));
            
            if (string.IsNullOrEmpty(secretKey))
            {
                Log.Fatal("InitialApplication:SecretKey not configured!");
                throw new InvalidOperationException("InitialApplication:SecretKey must be configured");
            }
            
            Log.Information("Creating initial application...");
            var initialApp = new FileHoster.Models.Application
            {
                Id = "SampleApp",
                SecretKey = secretKey,
                CanUpload = true,
                CanDownload = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            db.Applications.Add(initialApp);
            db.SaveChanges();
            Console.WriteLine("✅ Initial application created");
        }
        else
        {
            if (builder.Environment.IsProduction() &&
                db.Applications.Single(a => a.Id == "SampleApp").SecretKey !=
                builder.Configuration["InitialApplication:SecretKey"])
                throw new InvalidOperationException("Stored application key differs from configured key; rotate the DB key before deployment");
            Log.Information("✅ Application already exists");
        }
        
        Log.Information("✅ Database initialization completed");
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Error during database initialization");
        throw;
    }
}

Log.Information("Configuring HTTP pipeline...");

// Add global exception handler middleware FIRST (before other middleware)
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;
        
        if (exception != null)
        {
            Log.Error(exception, "Unhandled exception in request to {Path}", exceptionHandlerPathFeature?.Path);
        }
        
        await context.Response.WriteAsJsonAsync(new 
        { 
            error = "Internal server error",
            timestamp = DateTime.UtcNow,
            traceId = context.TraceIdentifier
        });
    });
});

// Add security headers middleware
app.Use(async (context, next) =>
{
    // HSTS (HTTP Strict Transport Security)
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    
    // Prevent MIME sniffing
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    
    // Clickjacking protection
    context.Response.Headers["X-Frame-Options"] = "DENY";
    
    // XSS protection
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    
    // Cross-Origin Policy
    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    
    // Content Security Policy
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
    
    // Referrer Policy
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    
    // Permissions Policy (formerly Feature Policy)
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    
    Log.Debug("Security headers applied to response");
    await next();
});

// Configure the HTTP request pipeline
Log.Debug("Adding HMAC authentication middleware...");
app.UseMiddleware<HmacAuthenticationMiddleware>();

Log.Debug("Setting up routing...");
app.UseRouting();

Log.Debug("Mapping controllers...");
app.MapControllers();

Log.Debug("Mapping health endpoint...");
app.MapGet("/health", () => Results.Ok("OK"));

Log.Information("✅ Pipeline configured, starting application...");
Log.Information("Application running on {Urls}", string.Join(", ", builder.WebHost.GetSetting("urls") ?? "http://[::]:5000"));

// Set up graceful shutdown
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    Log.Information("Shutdown signal received (SIGTERM/SIGINT), initiating graceful shutdown...");
    cts.Cancel();
};

try
{
    app.Run();
    Log.Information("Application shutdown completed");
}
catch (OperationCanceledException)
{
    Log.Information("Application was cancelled");
}
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.Information("Flushing logs and shutting down...");
    await Log.CloseAndFlushAsync();
    Console.Error.Flush();
}
