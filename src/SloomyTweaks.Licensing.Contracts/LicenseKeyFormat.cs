using System.Security.Cryptography;
using System.Text;

namespace SloomyTweaks.Licensing;

/// <summary>
/// License key format: <c>SLMY-XXXXX-XXXXX-XXXXX-XXXXX</c> using the Crockford base32 alphabet.
/// 19 random symbols (95 bits of entropy) followed by one checksum symbol.
/// The checksum only catches typing mistakes before contacting the server; it is NOT a security
/// mechanism. Validity is decided exclusively by the license server.
/// </summary>
public static class LicenseKeyFormat
{
    public const string Prefix = "SLMY";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int PayloadSymbols = 19;

    public static string Generate()
    {
        Span<byte> random = stackalloc byte[PayloadSymbols];
        RandomNumberGenerator.Fill(random);
        var symbols = new char[PayloadSymbols + 1];
        for (int i = 0; i < PayloadSymbols; i++)
        {
            symbols[i] = Alphabet[random[i] & 31];
        }
        symbols[PayloadSymbols] = Checksum(symbols.AsSpan(0, PayloadSymbols));
        return Format(symbols);
    }

    /// <summary>Normalizes user input (case, whitespace, ambiguous letters) to canonical form.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string compact = new string(input.Trim().ToUpperInvariant().Where(c => c is not ('-' or ' ')).ToArray());
        if (!compact.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        // Map visually ambiguous letters only in the key body (the prefix itself contains an L).
        string body = new string(compact[Prefix.Length..].Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());
        if (body.Length != PayloadSymbols + 1) return false;
        foreach (char c in body)
        {
            if (Alphabet.IndexOf(c) < 0) return false;
        }
        if (Checksum(body.AsSpan(0, PayloadSymbols)) != body[PayloadSymbols]) return false;

        normalized = Format(body.ToCharArray());
        return true;
    }

    private static char Checksum(ReadOnlySpan<char> symbols)
    {
        // Weighted sum mod 32 detects all single-symbol errors and most transpositions.
        int sum = 0;
        for (int i = 0; i < symbols.Length; i++)
        {
            sum += (i + 1) * Alphabet.IndexOf(symbols[i]);
        }
        return Alphabet[sum % 32];
    }

    private static string Format(char[] symbols)
    {
        var sb = new StringBuilder(Prefix);
        for (int i = 0; i < symbols.Length; i++)
        {
            if (i % 5 == 0) sb.Append('-');
            sb.Append(symbols[i]);
        }
        return sb.ToString();
    }
}
