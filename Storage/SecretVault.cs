using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Balsoft.Hive.BDC.Storage;

/// <summary>Where the Azure run mode keeps secrets, so the settings only hold a reference ("kv:" + name).</summary>
public interface ISecretVault
{
    /// <summary>The current value, or null when the secret does not exist.</summary>
    string? Get(string name);

    void Set(string name, string value);
}

/// <summary>Azure Key Vault, signed in with the identity of the host (managed identity in Azure, Azure CLI locally).</summary>
public sealed class KeyVaultSecretVault : ISecretVault
{
    private readonly SecretClient _client;

    public KeyVaultSecretVault(SecretClient client) => _client = client;

    public static KeyVaultSecretVault For(string vaultUrl)
    {
        if (!Uri.TryCreate(vaultUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The Key Vault address must look like https://<vault>.vault.azure.net.");
        return new KeyVaultSecretVault(new SecretClient(url, new DefaultAzureCredential()));
    }

    public string? Get(string name)
    {
        try
        {
            return _client.GetSecret(name).Value.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public void Set(string name, string value) => _client.SetSecret(name, value);
}