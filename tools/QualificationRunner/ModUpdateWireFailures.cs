using System;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;

namespace VGModAPI.Qualification;

// Qualification-only transport primitives: production ModFeedClient continues to forbid these hosts.
internal static class ModUpdateWireFailures
{
    private static bool Contains(Exception error, Func<Exception, bool> predicate)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (predicate(current)) return true;
        return false;
    }
    internal static bool IsCertificateTrustFailure(Exception error)
    {
        if (Contains(error, item => item.Message.IndexOf("NameMismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
            item.Message.IndexOf("NotTimeValid", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        return Contains(error, item => item is WebException web && web.Status == WebExceptionStatus.TrustFailure) ||
            Contains(error, item => item is AuthenticationException) && Contains(error, item =>
                item.Message.IndexOf("UntrustedRoot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Message.IndexOf("CERTIFICATE_VERIFY_FAILED", StringComparison.OrdinalIgnoreCase) >= 0);
    }
    internal static void ValidateCertificate(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.Cast<X509Extension>().SingleOrDefault(extension => extension.Oid?.Value == "2.5.29.17");
        if (!certificate.HasPrivateKey || certificate.Subject != certificate.Issuer ||
            san == null || !san.RawData.SequenceEqual(new byte[] { 0x30, 6, 0x87, 4, 127, 0, 0, 1 }) ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("TLS fixture requires a currently valid self-signed certificate with exactly the loopback IP SAN.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain.Build(certificate) || chain.ChainStatus.Length == 0 ||
            chain.ChainStatus.Any(status => status.Status != X509ChainStatusFlags.UntrustedRoot))
            throw new InvalidOperationException("TLS fixture must fail chain validation only because its root is untrusted.");
    }
    internal static async Task RunAsync(string certificatePath, Action<string> record, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using (var transport = new HttpModFeedTransport())
        {
            var rejected = false;
            try { using var response = await transport.GetAsync(new Uri("https://vgmodapi-qualification.invalid/"), cancellation); }
            catch (Exception error)
            {
                cancellation.ThrowIfCancellationRequested();
                rejected = Contains(error, item => item is SocketException socket &&
                    (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.NoData) ||
                    item is WebException web && web.Status == WebExceptionStatus.NameResolutionFailure);
                if (!rejected) throw new InvalidOperationException("DNS failure was not identified by its native exception classification.", error);
            }
            if (!rejected) throw new InvalidOperationException("Reserved invalid domain unexpectedly resolved.");
            cancellation.ThrowIfCancellationRequested();
            record("wire-dns-name-resolution-failure");
        }
        await RunLocalAsync(certificatePath, record, cancellation);
    }
    internal static async Task RunLocalAsync(string certificatePath, Action<string> record, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
#pragma warning disable SYSLIB0057 // Unity targets netstandard2.1, not the newer X509CertificateLoader API.
        using var certificate = new X509Certificate2(certificatePath, "", X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        ValidateCertificate(certificate);
        await LocalFailure(certificate, false, cancellation);
        cancellation.ThrowIfCancellationRequested(); record("wire-tls-untrusted-certificate-rejected");
        await LocalFailure(certificate, true, cancellation);
        cancellation.ThrowIfCancellationRequested(); record("wire-stalled-handshake-canceled");
    }
    private static async Task LocalFailure(X509Certificate2 certificate, bool stall, CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        using var stopAccept = stop.Token.Register(() => listener.Stop());
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var abort = stop.Token.Register(() => peer.Dispose());
                accepted.TrySetResult(true);
                if (stall) await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                else
                {
                    using var ssl = new SslStream(peer.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                    // Server handshake completion alone does not prove client certificate acceptance.
                    var count = await ssl.ReadAsync(new byte[1], 0, 1, stop.Token).ConfigureAwait(false);
                    if (count != 0) throw new InvalidOperationException("Client sent application data through untrusted TLS.");
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
            catch (AuthenticationException) { /* Client rejection is independently required below. */ }
            catch (System.IO.IOException) { /* TLS alert/connection close: client classification remains mandatory. */ }
        });
        try
        {
            using var transport = new HttpModFeedTransport();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(stall ? 2 : 6));
            var rejected = false;
            try { using var response = await transport.GetAsync(new Uri("https://127.0.0.1:" + port + "/"), deadline.Token); }
            catch (Exception error)
            {
                cancellation.ThrowIfCancellationRequested();
                rejected = stall ? error is OperationCanceledException && deadline.IsCancellationRequested
                    : IsCertificateTrustFailure(error);
                if (!rejected) throw new InvalidOperationException("Native transport failure classification was not proven.", error);
            }
            cancellation.ThrowIfCancellationRequested();
            if (!rejected || !accepted.Task.IsCompleted) throw new InvalidOperationException("Loopback transport failure lacked a real accepted connection.");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            await server;
        }
    }
}
