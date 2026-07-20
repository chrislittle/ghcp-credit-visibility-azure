using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GhcpCreditVisibility.Authorization
{
    /// <summary>
    /// Minimal ASP.NET Core authentication scheme that exists ONLY to give
    /// <c>AddAuthorization(o => o.FallbackPolicy = RequireAuthenticatedUser())</c> a
    /// registered scheme to challenge against. Real identity hydration already happens
    /// in <see cref="EasyAuthClaimsMiddleware"/> from the X-MS-CLIENT-PRINCIPAL header,
    /// so <see cref="HandleAuthenticateAsync"/> never needs to authenticate anything —
    /// it just defers to whatever HttpContext.User already is.
    ///
    /// Without this scheme registered, ANY request that reaches the container without a
    /// valid Easy Auth principal (platform warm-up pings, a race right after a restart,
    /// etc.) throws "No authenticationScheme was specified, and there was no
    /// DefaultChallengeScheme found" — an unhandled 500 — instead of a clean redirect
    /// back to Easy Auth's own login endpoint.
    /// </summary>
    public sealed class EasyAuthAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "EasyAuth";

        public EasyAuthAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // EasyAuthClaimsMiddleware runs earlier in the pipeline and already set
            // HttpContext.User when a valid principal header was present.
            if (Context.User?.Identity?.IsAuthenticated == true)
            {
                var ticket = new AuthenticationTicket(Context.User, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            // Easy Auth (platform) normally intercepts unauthenticated requests before they
            // ever reach the container. If one slips through anyway, send the browser to
            // Easy Auth's own login endpoint rather than crashing with an unhandled 500.
            var redirectUri = properties.RedirectUri;
            if (string.IsNullOrEmpty(redirectUri))
            {
                redirectUri = Request.Path + Request.QueryString;
            }

            var loginUrl = "/.auth/login/aad?post_login_redirect_uri=" + Uri.EscapeDataString(redirectUri);
            Response.Redirect(loginUrl);
            return Task.CompletedTask;
        }
    }
}
