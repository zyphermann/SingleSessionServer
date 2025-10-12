
// One-time token store (hashes tokens) using IMemoryCache
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

sealed class TokenStore
{
    private readonly IMemoryCache _cache;
    public TokenStore(IMemoryCache cache) => _cache = cache;

    public string CreateToken(string playerId, TimeSpan ttl)
    {
        // Create URL-safe random token
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var hash = Utils.Hash(token);
        _cache.Set($"xfer:{hash}", playerId, ttl);
        return token;
    }

    public string? ConsumeToken(string token)
    {
        var hash = Utils.Hash(token);
        var key = $"xfer:{hash}";
        if (_cache.TryGetValue<string>(key, out var playerId))
        {
            _cache.Remove(key); // one-time
            return playerId;
        }
        return null;
    }


}