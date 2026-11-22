using System.Threading.RateLimiting;
using Kamala.Api;
using Kamala.Api.Endpoints;
using Kamala.Application;
using Kamala.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFilesApplication();
builder.Services.AddFilesInfrastructure(builder.Configuration);

var limits = builder.Configuration.GetSection(ProcessingLimits.Section).Get<ProcessingLimits>() ?? new ProcessingLimits();
var api = builder.Configuration.GetSection(ApiOptions.Section).Get<ApiOptions>() ?? new ApiOptions();

// Reject oversized uploads at the server, before they are buffered.
var maxRequest = limits.MaxTotalBytes + (1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxRequest);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = maxRequest);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequest;
    o.ValueCountLimit = 64;
    o.ValueLengthLimit = 64 * 1024;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();
builder.Services.AddHealthChecks();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(api.AllowedOrigins)
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type")
    .WithExposedHeaders("Content-Disposition")));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(FileEndpoints.ProcessingPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = api.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind IIS/ANCM or a reverse proxy the client IP arrives in X-Forwarded-For; needed for per-IP rate limits.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    await next();
});
app.UseCors();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapFileEndpoints();

app.Run();

public partial class Program;
