namespace ion.runtime.network;

using System.Text.RegularExpressions;

public static class IonTicketExtractor
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
    private static readonly Dictionary<char, int> CharMap;

    static IonTicketExtractor()
    {
        CharMap = new Dictionary<char, int>(Alphabet.Length);
        for (int i = 0; i < Alphabet.Length; i++)
            CharMap[Alphabet[i]] = i;
    }

    public static ReadOnlyMemory<byte>? ExtractTicketBytes(string input)
    {
        var match = Regex.Match(input, @"ticket#([0-9A-Za-z]+)");
        return !match.Success ? null : DecodeBase56(match.Groups[1].Value);
    }

    /// <summary>Decodes a bare base56 ticket, as a WebTransport client passes it in the query string.</summary>
    public static ReadOnlyMemory<byte>? DecodeTicket(string? base56)
        => string.IsNullOrEmpty(base56) ? null : DecodeBase56(base56);

    /// <summary>
    /// Decodes base56 the way every Ion client encodes it: each leading zero byte is one leading
    /// <c>'2'</c>, the rest is the big-endian number.
    /// </summary>
    /// <remarks>
    /// The leading <c>'2'</c>s used to be read as the number's own digits, and a zero digit adds
    /// nothing to a number — so <c>00 05</c> came back as <c>05</c>, and any ticket exchange whose
    /// tokens can start with a zero byte (a random binary token, say) saw a corrupted ticket.
    /// </remarks>
    internal static ReadOnlyMemory<byte>? DecodeBase56(string base56)
    {
        var zeros = 0;
        while (zeros < base56.Length && base56[zeros] == Alphabet[0])
            zeros++;

        // Little-endian while accumulating; reversed into place at the end.
        var value = new List<byte>(base56.Length);
        for (var i = zeros; i < base56.Length; i++)
        {
            if (!CharMap.TryGetValue(base56[i], out var digit))
                return null;

            var carry = digit;
            for (var j = 0; j < value.Count; j++)
            {
                var x = value[j] * 56 + carry;
                value[j] = (byte)x;
                carry = x >> 8;
            }

            while (carry > 0)
            {
                value.Add((byte)carry);
                carry >>= 8;
            }
        }

        var bytes = new byte[zeros + value.Count];
        for (var j = 0; j < value.Count; j++)
            bytes[^(j + 1)] = value[j];
        return bytes;
    }
}