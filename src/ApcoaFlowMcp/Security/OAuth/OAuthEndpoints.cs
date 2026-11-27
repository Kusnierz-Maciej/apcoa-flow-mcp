using System.Net;
using System.Text;
using ApcoaFlowApiClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Security.OAuth;

/// <summary>
/// Minimal built-in OAuth 2.1 authorization server: discovery, dynamic client registration, an HTML login
/// (API key + APCOA email + password), and the token endpoint (authorization_code + refresh_token). Stateless
/// — the authorization code and tokens are self-contained encrypted blobs (<see cref="CredentialTokenService"/>);
/// there is no client store or session store. Access is gated by the <see cref="ApiKeyAllowlist"/>; APCOA
/// credentials are validated at login via <see cref="IApcoaFlowLoginValidator"/>.
/// </summary>
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadataDoc);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);
        app.MapPost("/register", Register);
        app.MapGet("/authorize", AuthorizeForm);
        app.MapPost("/authorize", AuthorizeSubmit);
        app.MapPost("/token", Token);
    }

    // The protected-resource metadata (RFC 9728) clients fetch after a 401. Since this app is its own
    // authorization server, it points back at this same issuer.
    private static IResult ProtectedResourceMetadataDoc(HttpContext ctx)
    {
        var issuer = Issuer(ctx);
        return Results.Json(new Dictionary<string, object>
        {
            ["resource"] = issuer,
            ["authorization_servers"] = new[] { issuer },
            ["bearer_methods_supported"] = new[] { "header" },
        });
    }

    private static IResult AuthorizationServerMetadata(HttpContext ctx)
    {
        var issuer = Issuer(ctx);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{issuer}/authorize",
            ["token_endpoint"] = $"{issuer}/token",
            ["registration_endpoint"] = $"{issuer}/register",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
        });
    }

    // Dynamic client registration (RFC 7591). Stateless: we don't persist clients. We echo a generated
    // client_id and the submitted redirect_uris; PKCE + exact redirect_uri matching secure the flow.
    private static IResult Register([FromBody] ClientRegistrationRequest? body, ILogger<Marker> logger)
    {
        var redirectUris = body?.RedirectUris ?? [];
        logger.LogInformation("OAuth client registration for {Count} redirect URI(s).", redirectUris.Length);
        var clientId = "mcp-" + Guid.NewGuid().ToString("N");
        return Results.Json(new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult AuthorizeForm(
        HttpContext ctx,
        ApiKeyAllowlist allowlist,
        [FromQuery(Name = "client_id")] string? clientId,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery] string? state,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
        [FromQuery(Name = "response_type")] string? responseType)
    {
        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
            return Results.BadRequest("Unsupported response_type; only 'code' is supported.");
        if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
            return Results.BadRequest("PKCE with code_challenge_method=S256 is required.");
        if (string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(codeChallenge))
            return Results.BadRequest("redirect_uri and code_challenge are required.");

        return Html(RenderForm(clientId, redirectUri, state, codeChallenge, allowlist.RequireApiKey, error: null));
    }

    private static async Task<IResult> AuthorizeSubmit(
        HttpContext ctx,
        ApiKeyAllowlist allowlist,
        IApcoaFlowLoginValidator loginValidator,
        CredentialTokenService tokens,
        IOptions<OAuthOptions> oauth,
        ILogger<Marker> logger,
        CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        string? F(string k) => form[k].FirstOrDefault();

        var redirectUri = F("redirect_uri");
        var state = F("state");
        var codeChallenge = F("code_challenge");
        var clientId = F("client_id");
        var apiKey = F("api_key");
        var email = F("apcoa_email");
        var password = F("apcoa_password");

        // The page's script submits in the background (Accept: application/json) and gets JSON back: an error
        // message it shows above the untouched form, or the redirect URL it navigates to. Nothing typed is ever
        // sent back. A plain form POST (JavaScript off) gets the HTML form again, with only the email kept.
        var wantsJson = ctx.Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

        IResult Fail(string message, int status) => wantsJson
            ? Results.Json(new Dictionary<string, string> { ["error"] = message }, statusCode: status)
            : Html(RenderForm(clientId, redirectUri!, state, codeChallenge!, allowlist.RequireApiKey, message, email), status);

        if (string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(codeChallenge))
            return wantsJson
                ? Results.Json(new Dictionary<string, string> { ["error"] = "Missing authorization parameters." }, statusCode: StatusCodes.Status400BadRequest)
                : Results.BadRequest("Missing authorization parameters.");

        // 1. API-key allowlist gate (auto-passes when RequireMcpApiKey is off).
        if (!allowlist.IsAllowed(apiKey))
        {
            logger.LogWarning("OAuth authorize denied: API key not in allowlist.");
            return Fail("Invalid API key.", StatusCodes.Status401Unauthorized);
        }

        // 2. Validate the APCOA credentials by a real login — classify bad-creds vs API error.
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Fail("APCOA email and password are required.", StatusCodes.Status400BadRequest);

        var outcome = await loginValidator.ValidateAsync(email, password, ct);
        switch (outcome.Kind)
        {
            case LoginOutcomeKind.BadCredentials:
                logger.LogInformation("OAuth authorize: APCOA credentials rejected.");
                return Fail("APCOA rejected these credentials. Check your email and password.", StatusCodes.Status401Unauthorized);
            case LoginOutcomeKind.ApiError:
                logger.LogWarning("OAuth authorize: APCOA API error during validation ({Detail}).", outcome.Detail);
                return Fail("APCOA service is temporarily unavailable. Please try again.", StatusCodes.Status503ServiceUnavailable);
        }

        // 3. Mint a single-use, short-lived authorization code binding the creds + PKCE + redirect.
        // When no API key is required, there is no key to hash — embed an empty hash (IsHashAllowed
        // passes it through in that mode).
        var apiKeyHash = allowlist.RequireApiKey ? CredentialTokenService.HashApiKey(apiKey!) : string.Empty;
        var code = tokens.Protect(new TokenPayload(
            TokenKind.Code, email!, password!, apiKeyHash,
            codeChallenge, redirectUri), oauth.Value.AuthorizationCodeLifetime);

        logger.LogInformation("OAuth authorize: credentials validated, issuing authorization code.");

        var sep = redirectUri.Contains('?') ? '&' : '?';
        var location = $"{redirectUri}{sep}code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrEmpty(state))
            location += $"&state={Uri.EscapeDataString(state)}";
        // fetch() would follow a 302 itself, so the script gets the URL and navigates the page to it.
        return wantsJson
            ? Results.Json(new Dictionary<string, string> { ["redirect"] = location })
            : Results.Redirect(location);
    }

    private static async Task<IResult> Token(
        HttpContext ctx,
        ApiKeyAllowlist allowlist,
        CredentialTokenService tokens,
        IOptions<OAuthOptions> oauth,
        ILogger<Marker> logger,
        CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        string? F(string k) => form[k].FirstOrDefault();
        var grantType = F("grant_type");

        return grantType switch
        {
            "authorization_code" => ExchangeCode(F, allowlist, tokens, oauth.Value, logger),
            "refresh_token" => Refresh(F, allowlist, tokens, oauth.Value, logger),
            _ => TokenError("unsupported_grant_type"),
        };
    }

    private static IResult ExchangeCode(
        Func<string, string?> f, ApiKeyAllowlist allowlist, CredentialTokenService tokens, OAuthOptions oauth, ILogger<Marker> logger)
    {
        var code = f("code");
        var verifier = f("code_verifier");
        var redirectUri = f("redirect_uri");

        var payload = tokens.TryUnprotect(code, TokenKind.Code);
        if (payload is null)
        {
            logger.LogWarning("OAuth token: authorization code invalid or expired.");
            return TokenError("invalid_grant");
        }

        if (!Pkce.VerifyS256(verifier, payload.CodeChallenge))
        {
            logger.LogWarning("OAuth token: PKCE verification failed.");
            return TokenError("invalid_grant");
        }

        if (!string.Equals(redirectUri, payload.RedirectUri, StringComparison.Ordinal))
        {
            logger.LogWarning("OAuth token: redirect_uri mismatch.");
            return TokenError("invalid_grant");
        }

        // Re-check the key is still allowlisted at issue time too.
        if (!allowlist.IsHashAllowed(payload.ApiKeyHash))
        {
            logger.LogWarning("OAuth token: API key no longer allowlisted; refusing to issue tokens.");
            return TokenError("invalid_grant");
        }

        logger.LogInformation("OAuth token: issuing access + refresh tokens.");
        return IssueTokens(payload, tokens, oauth);
    }

    private static IResult Refresh(
        Func<string, string?> f, ApiKeyAllowlist allowlist, CredentialTokenService tokens, OAuthOptions oauth, ILogger<Marker> logger)
    {
        var refreshToken = f("refresh_token");
        var payload = tokens.TryUnprotect(refreshToken, TokenKind.Refresh);
        if (payload is null)
        {
            logger.LogWarning("OAuth refresh: refresh token invalid or expired.");
            return TokenError("invalid_grant");
        }

        // The kill-switch: a pulled API key invalidates refresh too.
        if (!allowlist.IsHashAllowed(payload.ApiKeyHash))
        {
            logger.LogWarning("OAuth refresh: API key no longer allowlisted; session ended.");
            return TokenError("invalid_grant");
        }

        logger.LogInformation("OAuth refresh: re-minting access token (rotating refresh).");
        return IssueTokens(payload, tokens, oauth);
    }

    private static IResult IssueTokens(TokenPayload payload, CredentialTokenService tokens, OAuthOptions oauth)
    {
        var access = tokens.Protect(
            payload with { Kind = TokenKind.Access, CodeChallenge = null, RedirectUri = null }, oauth.AccessTokenLifetime);
        var refresh = tokens.Protect(
            payload with { Kind = TokenKind.Refresh, CodeChallenge = null, RedirectUri = null }, oauth.RefreshTokenLifetime);

        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)oauth.AccessTokenLifetime.TotalSeconds,
            ["refresh_token"] = refresh,
        });
    }

    private static IResult TokenError(string error) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error },
            statusCode: StatusCodes.Status400BadRequest);

    private static string Issuer(HttpContext ctx)
    {
        var req = ctx.Request;
        return $"{req.Scheme}://{req.Host}{req.PathBase}";
    }

    /// <summary>
    /// HTML response marked <c>Cache-Control: no-store</c>: after a failed sign-in the form carries what the user
    /// typed (password and API key included), so the page must never be cached or restored from disk.
    /// </summary>
    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) => new NoStoreHtml(html, statusCode);

    private sealed class NoStoreHtml(string html, int statusCode) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, statusCode).ExecuteAsync(context);
        }
    }

    /// <param name="email">
    /// Kept in the no-JavaScript fallback after a failed sign-in. Secrets (password, API key) are never echoed:
    /// with JavaScript the form isn't re-rendered at all, so what the user typed simply stays in the fields.
    /// </param>
    private static string RenderForm(
        string? clientId, string redirectUri, string? state, string codeChallenge, bool requireApiKey, string? error,
        string? email = null)
    {
        string H(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        // Always present so the script can show errors in place; hidden while there is none.
        var errorBlock = $"<p class=\"error\" id=\"error\" role=\"alert\" tabindex=\"-1\"{(error is null ? " hidden" : "")}>{H(error)}</p>";

        var apiKeyBlock = requireApiKey
            ? """
                <label for="mcp-api-key">MCP server API key</label>
                <div class="secret">
                  <input id="mcp-api-key" name="api_key" type="password"
                         autocapitalize="none" spellcheck="false" required>
                  <button type="button" class="reveal" aria-controls="mcp-api-key" aria-pressed="false"
                          data-what="API key" aria-label="Show API key">Show</button>
                </div>
                <p class="field-hint">Provided by whoever runs this MCP server (not your APCOA password).</p>
            """
            : "";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <title>Unofficial APCOA Flow MCP — Sign in</title>
            <link rel="icon" href="/favicon.ico" sizes="16x16 32x32 48x48">
            <link rel="icon" href="/icon-192.png" type="image/png" sizes="192x192">
            <link rel="apple-touch-icon" href="/apple-touch-icon.png">
            <style>
              /* Service brand colours — the only per-service part of this page (framework guideline §5.2).
                 APCOA Flow green from the official app icon (#79b52a). White text on it is only 2.5:1, so
                 filled buttons use a dark ink (6.4:1) and brand-coloured text on light surfaces uses a
                 darker shade (--brand-text, 5.5:1). */
              :root{--brand:#79b52a;--brand-hover:#6aa023;--brand-ink:#14260a;--brand-text:#4a7516;
                    --brand-tint:rgba(121,181,42,.12)}
              body{font-family:system-ui,sans-serif;background:#f4f5f7;margin:0;padding:2rem;color:#1a1a2e}
              .card{max-width:24rem;margin:3rem auto;background:#fff;border-radius:12px;padding:1.75rem;
                    box-shadow:0 2px 12px rgba(0,0,0,.08)}
              h1{font-size:1.2rem;margin:0 0 1rem} label{display:block;margin:.75rem 0 .25rem;font-size:.85rem;font-weight:600}
              input{width:100%;box-sizing:border-box;padding:.6rem;border:1px solid #cbd0d8;border-radius:8px;font-size:1rem;
                    background:#fff;color:#1a1a2e}
              button{margin-top:1.25rem;width:100%;padding:.7rem;border:0;border-radius:8px;background:var(--brand);
                     color:var(--brand-ink);font-size:1rem;font-weight:600;cursor:pointer}
              button:hover{background:var(--brand-hover)}
              button:disabled{opacity:.65;cursor:progress} input:read-only{opacity:.65}
              .secret{position:relative} .secret input{padding-right:4.5rem}
              button.reveal{position:absolute;right:.35rem;top:50%;transform:translateY(-50%);margin:0;width:auto;
                            padding:.3rem .65rem;font-size:.78rem;font-weight:600;background:transparent;
                            color:var(--brand-text);border-radius:6px}
              button.reveal:hover{background:var(--brand-tint)}
              button.reveal:focus-visible{outline:2px solid var(--brand);outline-offset:1px}
              .error[hidden]{display:none}
              .error{background:#fde8e8;color:#9b1c1c;padding:.6rem .75rem;border-radius:8px;font-size:.85rem}
              .field-hint{color:#667085;font-size:.72rem;margin:.3rem 0 0}
              .hint{color:#667085;font-size:.78rem;margin-top:1rem}
              @media (prefers-color-scheme: dark){
                body{background:#0f1117;color:#e6e8ee}
                .card{background:#1a1d27;box-shadow:0 2px 12px rgba(0,0,0,.4)}
                input{background:#0f1117;color:#e6e8ee;border-color:#3a3f4b}
                input::placeholder{color:#8b90a0}
                :root{--brand:#79b52a;--brand-hover:#8cc63f;--brand-text:#9bd157;--brand-tint:rgba(121,181,42,.18)}
                .error{background:#3a1b1d;color:#f3b4b4}
                .field-hint,.hint{color:#9aa0b0}
              }
            </style></head>
            <body><div class="card">
              <h1>Connect APCOA Flow (unofficial)</h1>
              {{errorBlock}}
              <form id="login" method="post" action="/authorize">
                <input type="hidden" name="client_id" value="{{H(clientId)}}">
                <input type="hidden" name="redirect_uri" value="{{H(redirectUri)}}">
                <input type="hidden" name="state" value="{{H(state)}}">
                <input type="hidden" name="code_challenge" value="{{H(codeChallenge)}}">

                <label for="apcoa_email">APCOA account email</label>
                <input id="apcoa_email" name="apcoa_email" type="email" value="{{H(email)}}"
                       autocomplete="username" autocapitalize="none" spellcheck="false" required>

                <label for="apcoa_password">APCOA account password</label>
                <div class="secret">
                  <input id="apcoa_password" name="apcoa_password" type="password"
                         autocomplete="current-password" required>
                  <button type="button" class="reveal" aria-controls="apcoa_password" aria-pressed="false"
                          data-what="password" aria-label="Show password">Show</button>
                </div>
            {{apiKeyBlock}}
                <button id="submit" type="submit">Sign in</button>
              </form>
              <p class="hint">Your APCOA credentials are used to sign in on your behalf and are stored only
              inside your encrypted session token — never on the server.</p>
            </div>
            <script>
              // Sign-in runs in the background: the form is posted with fetch and the page is never replaced on
              // failure, so everything typed (password included) stays in the fields and is never sent back by
              // the server. Without JavaScript the plain form POST still works.
              // Loading state: lock the form while the APCOA login runs (it can take a few seconds).
              // Inputs go read-only, not disabled — disabled fields would be left out of the request.
              const form = document.getElementById('login');
              const button = document.getElementById('submit');
              const errorBox = document.getElementById('error');
              const showError = message => {
                errorBox.textContent = message;
                errorBox.hidden = false;
                errorBox.focus();
              };
              const fields = form.querySelectorAll('input:not([type=hidden])');
              const reveals = form.querySelectorAll('button.reveal');

              // Show/Hide toggle on secret fields.
              const setRevealed = (toggle, shown) => {
                const input = document.getElementById(toggle.getAttribute('aria-controls'));
                input.type = shown ? 'text' : 'password';
                toggle.textContent = shown ? 'Hide' : 'Show';
                toggle.setAttribute('aria-pressed', String(shown));
                toggle.setAttribute('aria-label', (shown ? 'Hide ' : 'Show ') + toggle.dataset.what);
              };
              reveals.forEach(t => t.addEventListener('click', () => {
                const input = document.getElementById(t.getAttribute('aria-controls'));
                setRevealed(t, input.type === 'password');
                input.focus();
              }));
              const setBusy = busy => {
                fields.forEach(f => f.readOnly = busy);
                reveals.forEach(t => t.disabled = busy);
                button.disabled = busy;
                button.textContent = busy ? 'Signing in…' : 'Sign in';
                form.setAttribute('aria-busy', String(busy));
              };
              form.addEventListener('submit', async e => {
                e.preventDefault();
                if (button.disabled) return; // swallow double submits
                reveals.forEach(t => setRevealed(t, false)); // never leave secrets shown as plain text
                const body = new URLSearchParams(new FormData(form));
                errorBox.hidden = true;
                setBusy(true);
                try {
                  const response = await fetch(form.action, {
                    method: 'POST', body, cache: 'no-store', credentials: 'same-origin',
                    headers: { 'Accept': 'application/json' },
                  });
                  let data = null;
                  try { data = await response.json(); } catch { /* not JSON */ }
                  if (response.ok && data && data.redirect) {
                    button.textContent = 'Redirecting…';
                    window.location.assign(data.redirect); // hand the code back to the MCP client
                    return; // stay locked while the browser navigates
                  }
                  showError((data && data.error) || `Sign-in failed (HTTP ${response.status}). Please try again.`);
                } catch {
                  showError('Could not reach the server. Check your connection and try again.');
                }
                setBusy(false);
              });
              // Back/forward cache restores the locked page — unlock it.
              window.addEventListener('pageshow', e => { if (e.persisted) setBusy(false); });
            </script>
            </body></html>
            """;
    }

    /// <summary>Marker type so endpoint handlers can resolve a categorised <see cref="ILogger{T}"/>.</summary>
    public sealed class Marker;

    public sealed class ClientRegistrationRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
        public string[] RedirectUris { get; set; } = [];
    }
}
