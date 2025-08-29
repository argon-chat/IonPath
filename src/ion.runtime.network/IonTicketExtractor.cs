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

    private static ReadOnlyMemory<byte>? DecodeBase56(string base56)
    {
        var digits = new List<byte>();
        foreach (var c in base56)
        {
            if (!CharMap.TryGetValue(c, out var val))
                return null;

            digits.Add((byte)val);
        }

        var value = new List<byte> { 0 }; 

        foreach (var digit in digits)
        {
            int carry = digit;
            for (var i = 0; i < value.Count; i++)
            {
                var x = value[i] * 56 + carry;
                value[i] = (byte)(x & 0xFF);
                carry = x >> 8;
            }
            while (carry > 0)
            {
                value.Add((byte)(carry & 0xFF));
                carry >>= 8;
            }
        }

        value.Reverse();
        return new ReadOnlyMemory<byte>(value.ToArray());
    }
}