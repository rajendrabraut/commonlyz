using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MimeKit;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Commonlyz.Tests.Integration;

/// <summary>A real SMTP server, in process, that requires a login and keeps every message it receives.</summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    public const string Username = "mailer";
    public const string Password = "s3cret";

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _running;

    public FakeSmtpServer()
    {
        Port = FreePort();
        var options = new SmtpServerOptionsBuilder()
            .ServerName("localhost")
            .Endpoint(e => e.Endpoint(new IPEndPoint(IPAddress.Loopback, Port)).AllowUnsecureAuthentication(true).AuthenticationRequired(true))
            .Build();
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new Store(Messages));
        services.Add(new Authenticator());
        _running = new SmtpServer.SmtpServer(options, services).StartAsync(_cts.Token);
    }

    public int Port { get; }

    public ConcurrentQueue<MimeMessage> Messages { get; } = new();

    public MimeMessage? Find(Func<MimeMessage, bool> predicate) => Messages.FirstOrDefault(predicate);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Store(ConcurrentQueue<MimeMessage> messages) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(buffer.ToArray());
            messages.Enqueue(await MimeMessage.LoadAsync(stream, cancellationToken));
            return SmtpResponse.Ok;
        }
    }

    private sealed class Authenticator : UserAuthenticator
    {
        public override Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken) =>
            Task.FromResult(user == Username && password == Password);
    }
}
