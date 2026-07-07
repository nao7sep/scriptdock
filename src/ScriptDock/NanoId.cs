using System;
using System.Security.Cryptography;

namespace ScriptDock;

/// <summary>
/// A dependency-free nanoid generator: a crypto-random, URL-safe id used anywhere the app
/// once reached for a <c>Guid</c> — a filename discriminator, a test's scratch directory
/// name, anything that only needs to be unique and unguessable, never parsed back into a
/// structured value. The alphabet is the standard 64-char URL-safe set (<c>A-Za-z0-9_-</c>);
/// 64 divides 256 evenly, so masking a random byte to its low 6 bits (<c>byte &amp; 0x3F</c>)
/// indexes the alphabet with perfect uniformity — no modulo bias, no rejection sampling.
/// </summary>
public static class NanoId
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
    private const int DefaultLength = 21;

    /// <summary>Generates a new id of <paramref name="length"/> characters (default 21, nanoid's
    /// standard default) drawn from the URL-safe alphabet using a cryptographic random source.</summary>
    public static string New(int length = DefaultLength)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be positive.");

        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);

        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[bytes[i] & 0x3F];

        return new string(chars);
    }
}
