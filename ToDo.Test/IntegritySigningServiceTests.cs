using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ToDo.Domain;
using ToDo.Domain.Options;

namespace ToDo.Test;

public sealed class IntegritySigningServiceTests
{
    [Fact]
    public void SignAndVerify_RejectsTamperingAndUnknownKey()
    {
        var service = CreateService();
        var signature = service.SignHash("content-hash");

        Assert.True(service.IsConfigured);
        Assert.Equal("v2", service.CurrentKeyId);
        Assert.True(service.VerifyHash("content-hash", signature, "v2"));
        Assert.False(service.VerifyHash("tampered", signature, "v2"));
        Assert.False(service.VerifyHash("content-hash", signature, "unknown"));
    }

    [Fact]
    public void VerifyHash_AcceptsPreviousKeyDuringRotation()
    {
        var oldKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var hash = "historical-content-hash";
        var oldSignature = Convert.ToHexString(
            HMACSHA256.HashData(oldKey, Encoding.UTF8.GetBytes(hash))).ToLowerInvariant();
        var service = CreateService(oldKey);

        Assert.True(service.VerifyHash(hash, oldSignature, "v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("MTIzNDU2Nzg5MA==")]
    public void ProductionKeyValidation_RejectsWeakKeys(string key)
    {
        Assert.False(IntegritySigningService.IsProductionKeyValid(key));
    }

    private static IntegritySigningService CreateService(byte[]? oldKey = null)
    {
        var currentKey = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray();
        var options = new IntegritySigningOptions
        {
            CurrentKeyId = "v2",
            CurrentKey = Convert.ToBase64String(currentKey)
        };
        if (oldKey != null) options.PreviousKeys["v1"] = Convert.ToBase64String(oldKey);
        return new IntegritySigningService(Options.Create(options));
    }
}
