using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Delivery.BuildingBlocks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Delivery.Modules.Identity.Infrastructure;

public sealed record FederatedIdentity(string Issuer, string Subject, string Email);
public interface IFederatedProvider
{
    Task<string> AuthorizationUrlAsync(string state, string nonce, string challenge, CancellationToken ct);
    Task<FederatedIdentity> ExchangeAsync(string code, string verifier, string nonce, CancellationToken ct);
}
public sealed class OpenIdProvider : IFederatedProvider
{
    private readonly FederationOptions options;
    private readonly HttpClient client;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? configuration;
    public OpenIdProvider(IOptions<FederationOptions> settings, IHttpClientFactory clients)
    {
        options = settings.Value; client = clients.CreateClient("federation");
        if (options.Enabled)
        {
            if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https"
                || !Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "https"
                || redirect.AbsolutePath != "/api/v1/identity/federation/callback" || !string.IsNullOrEmpty(redirect.Query)
                || string.IsNullOrWhiteSpace(options.ClientId))
                throw new InvalidOperationException("Federation requires HTTPS authority/redirect and a client id.");
            configuration = new(options.Authority.TrimEnd('/') + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(client) { RequireHttps = true });
        }
    }
    private async Task<OpenIdConnectConfiguration> ConfigurationAsync(CancellationToken ct)
    {
        if (configuration is null) throw new ApiException(503, "federation_disabled", "El proveedor federado no está configurado.");
        try
        {
            var result = await configuration.GetConfigurationAsync(ct);
            if (result.Issuer.TrimEnd('/') != options.Authority.TrimEnd('/') || !Https(result.AuthorizationEndpoint) || !Https(result.TokenEndpoint))
                throw new ApiException(503, "invalid_provider_configuration", "Configuración del proveedor inválida.");
            return result;
        }
        catch (Exception ex) when (ex is not ApiException && !ct.IsCancellationRequested)
        { throw new ApiException(503, "provider_unavailable", "El proveedor no está disponible."); }
    }
    public async Task<string> AuthorizationUrlAsync(string state, string nonce, string challenge, CancellationToken ct)
    {
        var config = await ConfigurationAsync(ct);
        return QueryHelpers.AddQueryString(config.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        });
    }
    public async Task<FederatedIdentity> ExchangeAsync(string code, string verifier, string nonce, CancellationToken ct)
    {
        var config = await ConfigurationAsync(ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrEmpty(options.ClientSecret)) form["client_secret"] = options.ClientSecret;
        string token;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(config.TokenEndpoint, content, ct);
            if (!response.IsSuccessStatusCode) throw Invalid();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !json.RootElement.TryGetProperty("id_token", out var property)
                || property.ValueKind != System.Text.Json.JsonValueKind.String) throw Invalid();
            token = property.GetString() ?? throw Invalid();
            if (token.Length > 32768) throw Invalid();
        }
        catch (System.Text.Json.JsonException) { throw Invalid(); }
        catch (HttpRequestException) { throw new ApiException(503, "provider_unavailable", "El proveedor no está disponible."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ApiException(503, "provider_unavailable", "El proveedor no está disponible."); }
        try { return ValidateIdToken(token, config, options.ClientId, nonce); }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            configuration!.RequestRefresh();
            config = await ConfigurationAsync(ct);
            try { return ValidateIdToken(token, config, options.ClientId, nonce); }
            catch (Exception ex) when (ex is SecurityTokenException or ArgumentException) { throw Invalid(); }
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException) { throw Invalid(); }
    }
    public static FederatedIdentity ValidateIdToken(string token, OpenIdConnectConfiguration config, string audience, string nonce)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(5),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256]
        }, out var validated);
        var jwt = (JwtSecurityToken)validated;
        var subject = principal.FindFirstValue("sub");
        var issued = principal.FindFirstValue("iat");
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || principal.FindFirstValue("nonce") != nonce
            || !long.TryParse(issued, out var iat) || iat > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5
            || principal.FindFirstValue("email_verified") != "true") throw Invalid();
        var azp = principal.FindFirstValue("azp");
        if ((jwt.Audiences.Count() > 1 && azp != audience) || (azp is not null && azp != audience)) throw Invalid();
        return new(config.Issuer, subject, IdentityService.NormalizeEmail(principal.FindFirstValue("email")));
    }
    private static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    private static ApiException Invalid() => new(401, "invalid_federation", "La identidad federada no pudo verificarse.");
}
