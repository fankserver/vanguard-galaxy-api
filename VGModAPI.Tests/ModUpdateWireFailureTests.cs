using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateWireFailureTests
{
    [Fact]
    public async Task LocalDriverRequiresRealCertificateRejectionAndStalledHandshakeCancellation()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.GetTempFileName();
        var facts = new List<string>();
        try
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, ""));
            await ModUpdateWireFailures.RunLocalAsync(path, facts.Add, CancellationToken.None);
            Assert.Equal(new[] { "wire-tls-untrusted-certificate-rejected", "wire-stalled-handshake-canceled" }, facts);
            using var stop = new CancellationTokenSource(); stop.Cancel(); facts.Clear();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModUpdateWireFailures.RunLocalAsync(path, facts.Add, stop.Token));
            Assert.Empty(facts);
        }
        finally { File.Delete(path); }
    }
}
