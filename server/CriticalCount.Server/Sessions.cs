using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CriticalCount.Server;

/// Short-lived session tokens, held in memory only.
///
/// The phone proves who it is with its account secret once (POST /v1/sessions) and gets a token
/// for everything after. Tokens are never written to disk; a server restart simply makes every
/// phone sign in again with its secret, which it does on its own. Only a hash of each token is
/// kept, so even a memory dump would not hand out working tokens.
public sealed class Sessions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (string accountId, DateTime expires)> _byHash = new();
    private readonly Func<DateTime> _clock;

    public Sessions(Func<DateTime> clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public string Issue(string accountId)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _byHash[Hash(token)] = (accountId, _clock() + Lifetime);
        return token;
    }

    /// The account a token belongs to, or null if it is unknown or expired.
    public string Resolve(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        string key = Hash(token);
        if (!_byHash.TryGetValue(key, out var entry)) return null;
        if (entry.expires < _clock())
        {
            _byHash.TryRemove(key, out _);
            return null;
        }
        return entry.accountId;
    }

    /// Every token for this account stops working (account deleted).
    public void RevokeAll(string accountId)
    {
        foreach (var pair in _byHash)
            if (pair.Value.accountId == accountId) _byHash.TryRemove(pair.Key, out _);
    }

    public void Sweep()
    {
        DateTime now = _clock();
        foreach (var pair in _byHash)
            if (pair.Value.expires < now) _byHash.TryRemove(pair.Key, out _);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
