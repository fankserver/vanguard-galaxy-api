using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace VGModAPI.E2E;

internal sealed class WireSender : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamWriter _writer;

    internal WireSender(int port)
    {
        _client = new TcpClient();
        try
        {
            if (!_client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("E2E controller did not accept the connection.");
            _client.SendTimeout = 5000;
            _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { _client.Dispose(); throw; }
    }

    internal void Send(object message) => _writer.WriteLine(JsonConvert.SerializeObject(message));
    public void Dispose() { try { _writer.Dispose(); } finally { _client.Dispose(); } }
}
