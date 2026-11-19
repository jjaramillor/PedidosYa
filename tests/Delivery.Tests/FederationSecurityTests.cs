using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Delivery.BuildingBlocks;
using Delivery.Modules.Identity;
using Delivery.Modules.Identity.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
namespace Delivery.Tests;

public sealed class FederationSecurityTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("unverified")]
    [InlineData("subject")]
    [InlineData("azp")]
    [InlineData("iat")]
    [InlineData("signature")]
    public void Id_token_requires_trusted_signature_issuer_audience_lifetime_nonce_and_verified_email(string scenario)
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test" };
        var config = new OpenIdConnectConfiguration { Issuer = "https://issuer.example" }; config.SigningKeys.Add(key);
        var claims = new List<Claim>
        {
            new("sub",scenario=="subject"?"":"external-id"),
            new("nonce",scenario=="nonce"?"wrong":"nonce"),
            new("email","USER@example.test"),
            new("email_verified",scenario=="unverified"?"false":"true",ClaimValueTypes.Boolean),
            new("iat",(scenario=="iat"?DateTimeOffset.UtcNow.AddMinutes(10):DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(),ClaimValueTypes.Integer64)
        };
        if (scenario == "azp") claims.Add(new("azp", "wrong-client"));
        var token = new JwtSecurityToken(scenario == "issuer" ? "https://wrong.example" : config.Issuer,
            scenario == "audience" ? "wrong-client" : "client", claims, DateTime.UtcNow.AddMinutes(-20),
            scenario == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(scenario == "signature" ? new RsaSecurityKey(otherRsa) { KeyId = "wrong" } : key, SecurityAlgorithms.RsaSha256));
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        if (scenario == "valid")
        {
            var result = OpenIdProvider.ValidateIdToken(encoded, config, "client", "nonce");
            Assert.Equal("user@example.test", result.Email); Assert.Equal("external-id", result.Subject);
        }
        else
        {
            var error = Record.Exception(() => OpenIdProvider.ValidateIdToken(encoded, config, "client", "nonce"));
            Assert.True(error is ApiException or SecurityTokenException, error?.ToString());
        }
    }

    [Fact]
    public async Task Disabled_provider_fails_closed_without_network_access()
    {
        var provider = new OpenIdProvider(Options.Create(new FederationOptions()), new FakeClientFactory(new HttpClient(new ProviderHandler("{}", "{}", "{}"))));
        var error = await Assert.ThrowsAsync<ApiException>(() => provider.AuthorizationUrlAsync("state", "nonce", "challenge", CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public void Provider_requires_https_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => new OpenIdProvider(Options.Create(new FederationOptions
        { Enabled = true, Authority = "http://issuer.example", ClientId = "client", RedirectUri = "https://api.example/callback" }),
            new FakeClientFactory(new HttpClient())));
    }

    [Fact]
    public async Task Discovery_code_exchange_and_pkce_use_the_configured_provider()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test" }; var p = rsa.ExportParameters(false);
        var jwt = new JwtSecurityToken("https://issuer.example", "client", [
            new("sub","external-id"),new("nonce","nonce"),new("email","user@example.test"),
            new("email_verified","true",ClaimValueTypes.Boolean),new("iat",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),ClaimValueTypes.Integer64)
        ], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        var metadata = JsonSerializer.Serialize(new
        {
            issuer = "https://issuer.example",
            authorization_endpoint = "https://issuer.example/authorize",
            token_endpoint = "https://issuer.example/token",
            jwks_uri = "https://issuer.example/jwks"
        });
        var keys = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(p.Modulus!), e = Base64UrlEncoder.Encode(p.Exponent!) } } });
        var handler = new ProviderHandler(metadata, keys, JsonSerializer.Serialize(new { id_token = new JwtSecurityTokenHandler().WriteToken(jwt) }));
        var provider = new OpenIdProvider(Options.Create(new FederationOptions
        { Enabled = true, Authority = "https://issuer.example", ClientId = "client", ClientSecret = "test-secret", RedirectUri = "https://api.example/api/v1/identity/federation/callback" }),
            new FakeClientFactory(new HttpClient(handler)));
        var url = await provider.AuthorizationUrlAsync("state", "nonce", "challenge", CancellationToken.None);
        Assert.Contains("response_type=code", url); Assert.Contains("code_challenge_method=S256", url); Assert.Contains("nonce=nonce", url);
        var identity = await provider.ExchangeAsync("code", "verifier", "nonce", CancellationToken.None);
        Assert.Equal("external-id", identity.Subject);
        Assert.Contains("code_verifier=verifier", handler.Posted); Assert.Contains("grant_type=authorization_code", handler.Posted);
        Assert.Contains("client_secret=test-secret", handler.Posted);
    }

    private sealed class FakeClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class ProviderHandler(string metadata, string keys, string tokens) : HttpMessageHandler
    {
        public string? Posted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) Posted = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("openid-configuration") ? metadata : path == "/jwks" ? keys : tokens) };
        }
    }
}
