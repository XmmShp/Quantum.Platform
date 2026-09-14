namespace Quantum.Platform.Authentication;

public sealed class QuantumPlatformOidcOptions
{
    public const string SectionName = "QuantumPlatform:OidcServer";

    public string Issuer { get; set; } = "http://localhost:8080/oauth2";
    public string PathBase { get; set; } = "/oauth2";
    public string Audience { get; set; } = "quantum-platform";
    public string SigningKeyEncryptionKey { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
}
