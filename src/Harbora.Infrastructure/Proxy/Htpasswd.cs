namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// Builds htpasswd lines for Traefik basic-auth middleware. Uses bcrypt, which Traefik accepts
/// and which is safe to store (the plaintext password is never persisted).
/// </summary>
public static class Htpasswd
{
    public static string Line(string user, string password) =>
        $"{user}:{BCrypt.Net.BCrypt.HashPassword(password)}";
}
