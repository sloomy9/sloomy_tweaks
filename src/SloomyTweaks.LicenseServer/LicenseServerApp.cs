using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SloomyTweaks.Licensing;

namespace SloomyTweaks.LicenseServer;

// Administrative contracts: server-only, never shipped in the client.
public sealed record CreateLicensesRequest(string Plan, int? MaxDevices, int? Quantity, string? CustomerId, string? CustomerEmail, string? CustomerName, DateTimeOffset? ExpiresAt);
public sealed record UpdateLicenseRequest(DateTimeOffset? ExpiresAt, bool? ClearExpiry, int? MaxDevices);
public sealed record RevokeRequest(string? Reason);
public sealed record CreateCustomerRequest(string Email, string? Name, string? Notes);

public sealed record LicenseView(
    string Id, string KeyHint, string? CustomerId, string? CustomerEmail, string Plan, int MaxDevices, int ActiveDevices,
    DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string? RevokeReason, string Status);

public static class LicenseServerApp
{
    public const string AdminKeyHeader = "X-Admin-Key";

    public static WebApplication Build(string[] args, Action<LicenseServerOptions>? configure = null, TimeProvider? time = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var options = new LicenseServerOptions();
        builder.Configuration.GetSection("Sloomy").Bind(options);
        configure?.Invoke(options);
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(_ => new LicenseRepository(options.DatabasePath));
        builder.Services.AddSingleton(sp =>
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(options.SigningKeyPath));
            if (key.KeySize != 256) throw new InvalidOperationException("The signing key must be ECDSA P-256.");
            return new LicenseManager(sp.GetRequiredService<LicenseRepository>(), options, key, sp.GetRequiredService<TimeProvider>());
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddProblemDetails();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("client", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ActivationRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        WebApplication app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            // TLS is terminated by Kestrel or a reverse proxy; never serve the API over plain HTTP in production.
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            if (ctx.Request.Path.StartsWithSegments("/admin"))
                ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";
            await next();
        });
        app.UseRateLimiter();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapClientApi(app);
        MapAdminApi(app);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    private static void MapClientApi(WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1/licenses").RequireRateLimiting("client");

        api.MapPost("/activate", (ActivationRequest req, LicenseManager m) => ToHttp(m.Activate(req)));
        api.MapPost("/refresh", (TokenRequest req, LicenseManager m) => ToHttp(m.Refresh(req)));
        api.MapPost("/deactivate", (TokenRequest req, LicenseManager m) =>
        {
            var r = m.Deactivate(req);
            return r.Error is null ? Results.NoContent() : Results.Json(r.Error, statusCode: r.StatusCode);
        });
        // Public key distribution for client builds and verification tooling.
        app.MapGet("/api/v1/keys", (LicenseManager m, LicenseServerOptions o) => Results.Ok(new { kid = o.SigningKeyId, alg = "ES256", pem = m.PublicKeyPem }));
    }

    private static void MapAdminApi(WebApplication app)
    {
        RouteGroupBuilder admin = app.MapGroup("/admin/api").RequireRateLimiting("admin").AddEndpointFilter(async (ctx, next) =>
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<LicenseServerOptions>();
            string? provided = ctx.HttpContext.Request.Headers[AdminKeyHeader];
            if (!AdminKeyMatches(provided, options.AdminKeySha256))
                return Results.Json(new ApiError("unauthorized", "Invalid admin key."), statusCode: 401);
            return await next(ctx);
        });

        admin.MapGet("/licenses", (LicenseRepository repo, TimeProvider time, string? customerId) =>
            Results.Ok(repo.ListLicenses(customerId).Select(l => View(l, repo, time.GetUtcNow()))));

        admin.MapPost("/licenses", (CreateLicensesRequest req, LicenseManager m, LicenseRepository repo, LicenseServerOptions o, TimeProvider time) =>
        {
            if (!LicensePlanExtensions.TryParse(req.Plan, out LicensePlan plan))
                return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, "Plan must be one of 7, 30, 90, 365, lifetime."), statusCode: 400);
            string? customerId = req.CustomerId;
            if (customerId is null && !string.IsNullOrWhiteSpace(req.CustomerEmail))
            {
                if (!req.CustomerEmail.Contains('@')) return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, "Invalid customer email."), statusCode: 400);
                customerId = (repo.FindCustomerByEmail(req.CustomerEmail) ?? repo.CreateCustomer(req.CustomerEmail, req.CustomerName, null, time.GetUtcNow())).Id;
            }
            try
            {
                var created = m.CreateLicenses("admin", plan, req.MaxDevices ?? o.DefaultMaxDevices, req.Quantity ?? 1, customerId, req.ExpiresAt);
                return Results.Ok(created);
            }
            catch (ArgumentException e)
            {
                return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, e.Message), statusCode: 400);
            }
        });

        admin.MapPatch("/licenses/{id}", (string id, UpdateLicenseRequest req, LicenseManager m) =>
        {
            try { return m.Update("admin", id, req.ExpiresAt, req.ClearExpiry == true, req.MaxDevices) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException e) { return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, e.Message), statusCode: 400); }
        });
        admin.MapPost("/licenses/{id}/revoke", (string id, RevokeRequest req, LicenseManager m) => m.Revoke("admin", id, req.Reason) ? Results.NoContent() : Results.NotFound());
        admin.MapPost("/licenses/{id}/reinstate", (string id, LicenseManager m) => m.Reinstate("admin", id) ? Results.NoContent() : Results.NotFound());
        admin.MapGet("/licenses/{id}/activations", (string id, LicenseRepository repo) => Results.Ok(repo.ListActivations(id, activeOnly: false)));
        admin.MapPost("/activations/{id:long}/deactivate", (long id, LicenseManager m) => m.AdminDeactivate("admin", id) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/customers", (LicenseRepository repo) => Results.Ok(repo.ListCustomers()));
        admin.MapPost("/customers", (CreateCustomerRequest req, LicenseRepository repo, TimeProvider time) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, "A valid email is required."), statusCode: 400);
            if (repo.FindCustomerByEmail(req.Email) is not null)
                return Results.Json(new ApiError(ApiErrorCodes.InvalidRequest, "A customer with this email already exists."), statusCode: 409);
            Customer c = repo.CreateCustomer(req.Email, req.Name, req.Notes, time.GetUtcNow());
            repo.Audit("admin", "customer.created", $"customer={c.Id}", time.GetUtcNow());
            return Results.Ok(c);
        });
        admin.MapGet("/audit", (LicenseRepository repo, int? limit) => Results.Ok(repo.ListAudit(Math.Clamp(limit ?? 200, 1, 1000))));
    }

    private static LicenseView View(LicenseRecord l, LicenseRepository repo, DateTimeOffset now)
    {
        string status = l.RevokedAt is not null ? "Revoked"
            : l.ExpiresAt is { } e && now >= e ? "Expired"
            : l.ActivatedAt is null ? "Not activated"
            : "Active";
        string? email = l.CustomerId is null ? null : repo.GetCustomer(l.CustomerId)?.Email;
        return new LicenseView(l.Id, "…" + l.KeyHint, l.CustomerId, email, l.Plan.DisplayName(), l.MaxDevices,
            repo.ListActivations(l.Id, activeOnly: true).Count, l.CreatedAt, l.ActivatedAt, l.ExpiresAt, l.RevokedAt, l.RevokeReason, status);
    }

    private static IResult ToHttp(ServiceResult<LicenseResponse> r) =>
        r.Error is null ? Results.Ok(r.Value) : Results.Json(r.Error, statusCode: r.StatusCode);

    internal static bool AdminKeyMatches(string? provided, string expectedSha256Hex)
    {
        if (string.IsNullOrEmpty(provided) || expectedSha256Hex.Length != 64) return false;
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256Hex); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
