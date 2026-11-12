using System.Text.Json.Serialization;
using Allowlist.Api.Auth;
using Allowlist.Api.Endpoints;
using Allowlist.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "appdata", "keys")));
builder.Services.AddAllowlistInfrastructure(builder.Configuration);
builder.Services.AddAllowlistAuthentication();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", "http_" + context.HttpContext.Response.StatusCode);
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.Configure<RouteOptions>(options => options.ConstraintMap["spa"] = typeof(SpaRouteConstraint));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "appdata"));
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AllowlistDbContext>().Database.EnsureCreatedAsync(app.Lifetime.ApplicationStopping);
    await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
}
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapEntries();
app.MapOperations();
app.MapAdministration();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapFallbackToFile("{*path:nonfile:spa}", "index.html");
app.Run();
public partial class Program;
