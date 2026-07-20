using System.Security.Claims;
using System.Text.Json;

namespace GhcpCreditVisibility.Authorization
{
    /// <summary>
    /// App Service / Container Apps "Easy Auth" terminates Entra login at the platform
    /// and forwards the identity in the <c>X-MS-CLIENT-PRINCIPAL</c> header (base64 JSON).
    /// This middleware hydrates HttpContext.User from that header so <c>IsInRole</c>,
    /// role and group claims work in-app without any MSAL wiring. (Local dev without
    /// Easy Auth leaves User unauthenticated — guard with UseMock + a dev fallback.)
    /// </summary>
    public sealed class EasyAuthClaimsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<EasyAuthClaimsMiddleware> _logger;
        public EasyAuthClaimsMiddleware(RequestDelegate next, ILogger<EasyAuthClaimsMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext ctx)
        {
            var header = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
            if (!string.IsNullOrEmpty(header) && (ctx.User?.Identity?.IsAuthenticated != true))
            {
                try
                {
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header));
                    var principal = JsonSerializer.Deserialize<ClientPrincipal>(json);
                    if (principal?.Claims is { Count: > 0 })
                    {
                        // Do NOT trust principal.RoleClaimType (role_typ) here — on this app's Easy
                        // Auth config it comes back as the long ClaimTypes.Role URI, but the actual
                        // role claims in the "claims" array are always tagged with the short literal
                        // "roles" typ. Using role_typ as RoleClaimType makes them mismatch, so
                        // IsInRole("Admin") silently fails even though the "roles":"Admin" claim is
                        // present. Hardcode "roles" — confirmed via /debug/whoami against the real
                        // X-MS-CLIENT-PRINCIPAL payload.
                        var identity = new ClaimsIdentity(
                            authenticationType: principal.AuthenticationType ?? "aad",
                            nameType: principal.NameClaimType ?? ClaimTypes.Name,
                            roleType: "roles");
                        identity.AddClaims(principal.Claims.Select(c => new Claim(c.Type, c.Value)));
                        ctx.User = new ClaimsPrincipal(identity);
                    }
                    else
                    {
                        // Header was present but yielded zero claims — almost always a
                        // schema/parsing mismatch, not a genuinely anonymous caller. Log
                        // loudly so this doesn't silently masquerade as "anonymous" again.
                        _logger.LogWarning(
                            "X-MS-CLIENT-PRINCIPAL header present but deserialized with 0 claims — request will be treated as unauthenticated.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse X-MS-CLIENT-PRINCIPAL header — treating request as anonymous.");
                }
            }
            await _next(ctx);
        }

        private sealed class ClientPrincipal
        {
            // System.Text.Json is case-sensitive by default; the real X-MS-CLIENT-PRINCIPAL
            // payload uses these exact snake_case keys, NOT PascalCase — without the
            // [JsonPropertyName] attributes below, "claims" never binds and Claims stays
            // permanently empty, silently leaving every request unauthenticated.
            [System.Text.Json.Serialization.JsonPropertyName("auth_typ")] public string? AuthenticationType { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("name_typ")] public string? NameClaimType { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("role_typ")] public string? RoleClaimType { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("claims")] public List<ClientClaim> Claims { get; set; } = new();
        }

        private sealed class ClientClaim
        {
            [System.Text.Json.Serialization.JsonPropertyName("typ")] public string Type { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("val")] public string Value { get; set; } = "";
        }
    }
}
