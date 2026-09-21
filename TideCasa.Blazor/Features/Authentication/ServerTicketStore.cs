using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace TideCasa.Blazor.Features.Authentication;

// Only an encrypted reference is placed in the browser cookie. Tokens remain in this bounded server cache.
// A frontend restart intentionally ends these sessions; this is a single-instance first-stage store.
public sealed class ServerTicketStore : ITicketStore, IDisposable
{
    private readonly MemoryCache tickets = new(new MemoryCacheOptions { SizeLimit = 1024 });
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Store(key, ticket);
        return Task.FromResult(key);
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) { Store(key, ticket); return Task.CompletedTask; }
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!tickets.TryGetValue<byte[]>(key, out var bytes) || bytes is null) return Task.FromResult<AuthenticationTicket?>(null);
        var ticket = TicketSerializer.Default.Deserialize(bytes);
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= DateTimeOffset.UtcNow)
        { tickets.Remove(key); return Task.FromResult<AuthenticationTicket?>(null); }
        return Task.FromResult<AuthenticationTicket?>(ticket);
    }
    public Task RemoveAsync(string key) { tickets.Remove(key); return Task.CompletedTask; }
    private void Store(string key, AuthenticationTicket ticket)
    {
        var expiry = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow;
        if (expiry > DateTimeOffset.UtcNow.AddHours(1)) throw new InvalidOperationException("Invalid authentication lifetime.");
        tickets.Set(key, TicketSerializer.Default.Serialize(ticket), new MemoryCacheEntryOptions { AbsoluteExpiration = expiry, Size = 1 });
        if (!tickets.TryGetValue(key, out _)) throw new InvalidOperationException("Authentication session capacity is temporarily unavailable.");
    }
    public void Dispose() => tickets.Dispose();
}
