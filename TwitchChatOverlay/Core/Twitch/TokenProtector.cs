using System.Security.Cryptography;
using System.Text;

namespace TwitchChatOverlay.Core.Twitch;

/// <summary>
/// DPAPI (CurrentUser scope) wrapper so OAuth tokens are never written to settings.json
/// in plain text. Ciphertext is Base64 so it round-trips through JSON.
/// </summary>
internal static class TokenProtector
{
    public static string Protect(string plainText)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>Returns null when the blob is missing or cannot be decrypted (e.g. copied
    /// to another Windows user account) — the caller then treats the user as logged out.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
