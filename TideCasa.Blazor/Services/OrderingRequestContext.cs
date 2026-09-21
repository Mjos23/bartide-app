using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace TideCasa.Blazor.Services;

/// <summary>Trusted initial connection context retained for the life of one circuit.</summary>
public sealed class OrderingRequestContext(IDataProtectionProvider protection)
{
    private readonly ITimeLimitedDataProtector protector = protection
        .CreateProtector("TideCasa.Ordering.InitialConnection.v1").ToTimeLimitedDataProtector();
    private bool initialized;
    public string? ClientAddress { get; private set; }

    // Called only by server middleware, after trusted forwarded-header processing.
    internal void CaptureConnection(IPAddress? address)
    {
        if (initialized) return;
        initialized = true;
        ClientAddress = address?.ToString();
    }

    internal string? ProtectForCircuit() => ClientAddress is { } address
        ? protector.Protect(address, TimeSpan.FromHours(1)) : null;

    // The root's parameter is authenticated ciphertext, never a caller-selected IP.
    // Its lifetime limits circuit startup, not requests from an established circuit.
    internal void RestoreForCircuit(string? protectedContext)
    {
        if (initialized) return;
        initialized = true;
        if (string.IsNullOrWhiteSpace(protectedContext) || protectedContext.Length > 2048) return;
        try
        {
            var value = protector.Unprotect(protectedContext, out _);
            if (IPAddress.TryParse(value, out var address)) ClientAddress = address.ToString();
        }
        catch (CryptographicException) { }
    }
}
