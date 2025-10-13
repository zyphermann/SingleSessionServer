using System;
using System.Security.Cryptography;

public static class NanoId
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ123456789";

    public static string Generate(int length = 8)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");

        Span<byte> bytes = length <= 128 ? stackalloc byte[length] : new byte[length];
        Span<char> buffer = length <= 128 ? stackalloc char[length] : new char[length];

        RandomNumberGenerator.Fill(bytes);
        var alphabetLength = Alphabet.Length;
        for (var i = 0; i < length; i++)
            buffer[i] = Alphabet[bytes[i] % alphabetLength];

        return new string(buffer);
    }

    public static string nanoid(int length = 8) => Generate(length);
}
