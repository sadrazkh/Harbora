using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Security;

/// <summary>Replaces secret values with "***" in any text bound for logs or the UI.</summary>
public sealed class SecretRedactor : ISecretRedactor
{
    public string Redact(string text, IEnumerable<string> secretValues)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in secretValues)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                text = text.Replace(secret, "***");
        }
        return text;
    }
}
