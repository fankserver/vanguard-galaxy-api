using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace EWTest;

/// <summary>
/// Streams newline-delimited JSON messages to the driver's loopback listener
/// (the "wire" protocol defined in docs/development/e2e-tests.md and implemented
/// by tools/e2e.py). The driver hosts the listener and assembles the report from
/// these messages, so results cross the game/driver boundary as a live connection
/// rather than a shared report file.
/// </summary>
public sealed class WireSender : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamWriter _writer;

    private WireSender(TcpClient client, StreamWriter writer)
    {
        _client = client;
        _writer = writer;
    }

    /// <summary>Connect to the driver listener at 127.0.0.1:{port}. Returns null if the
    /// driver is not reachable (the harness then records the failure locally and quits).</summary>
    public static WireSender? Connect(int port)
    {
        try
        {
            var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            return new WireSender(client, writer);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Send(object message)
    {
        if (_writer == null) return;
        _writer.WriteLine(Json.Serialize(message));
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }
}
