using Kamala.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.Section));

var app = builder.Build();
var web = builder.Configuration.GetSection(WebOptions.Section).Get<WebOptions>() ?? new WebOptions();

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.Use(async (context, next) =>
{
    // Strict CSP: only our own scripts and styles; the browser may talk to nobody but us and the API.
    var api = web.ApiOrigin;
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data: {api}; " +
        $"connect-src 'self' {api}; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseStaticFiles();
app.MapGet("/error/{code:int}", (int code) => Results.Text(code == 404 ? "Page not found." : "Something went wrong.", statusCode: code));
app.MapControllers();

app.Run();

public partial class Program;
