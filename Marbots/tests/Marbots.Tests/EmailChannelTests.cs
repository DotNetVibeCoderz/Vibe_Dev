using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Marbots.Abstractions;
using Marbots.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Tests;

/// <summary>The e-mail channel against in-process IMAP and SMTP servers speaking the real protocols (MailKit on the client side).</summary>
public sealed class EmailChannelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-email-" + Guid.NewGuid().ToString("N")[..8]);
    private ServiceProvider _sp = default!;
    private readonly FakeImap _imap = new();
    private readonly FakeSmtp _smtp = new();

    public async Task InitializeAsync()
    {
        _imap.Start();
        _smtp.Start();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMarbotsRuntime(new MarbotsOptions { DataDirectory = _dir });
        _sp = services.BuildServiceProvider();
        foreach (var hosted in _sp.GetServices<IHostedService>().Where(h => h is MarbotsBootstrapper or ChannelGateway))
            await hosted.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        _sp.GetRequiredService<MarbotsEngine>().Shutdown();
        await _sp.DisposeAsync();
        _imap.Dispose();
        _smtp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Mail(string from, string subject, string body, string messageId, string extraHeaders = "") =>
        $"From: {from}\r\nTo: bot@marbots.test\r\nSubject: {subject}\r\nMessage-ID: <{messageId}>\r\nDate: Wed, 07 Oct 2026 08:00:00 +0000\r\n{extraHeaders}MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n";

    [Fact]
    public async Task Mail_becomes_a_thread_and_the_reply_goes_out_threaded()
    {
        var gateway = _sp.GetRequiredService<ChannelGateway>();
        var channel = await gateway.SaveAsync(new ChannelConfig
        {
            Kind = ChannelKinds.Email, Name = "Support mailbox", BotId = "alice",
            Settings = new()
            {
                ["username"] = "bot@marbots.test", ["fromAddress"] = "bot@marbots.test", ["fromName"] = "Marbots Support",
                ["imapHost"] = "127.0.0.1", ["imapPort"] = _imap.Port.ToString(), ["imapSecurity"] = "none",
                ["smtpHost"] = "127.0.0.1", ["smtpPort"] = _smtp.Port.ToString(), ["smtpSecurity"] = "none",
            },
        }, new Dictionary<string, string> { ["password"] = "app-password" });

        _imap.Messages.Add(Mail("Ana <ana@example.com>", "Invoice question", "When is the invoice due?\r\n\r\nOn Tue, Bob wrote:\r\n> old quoted text", "q1@example.com"));
        _imap.Messages.Add(Mail("Ana <ana@example.com>", "Out of office", "I am away.", "ooo@example.com", "Auto-Submitted: auto-replied\r\n"));
        _imap.Messages.Add(Mail("no-reply@shop.example", "Your order", "Thanks for your order.", "n1@shop.example"));
        _sp.GetRequiredService<ModelRouter>().Mock.EnqueueText("The invoice is due on the 25th.");

        var poller = _sp.GetServices<IHostedService>().OfType<EmailPoller>().Single();
        Assert.Equal(1, await poller.PollOnceAsync(channel, default));
        Assert.Equal(3, _imap.SeenUids.Count);
        Assert.Equal("app-password", _imap.LoginPassword);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_smtp.Messages.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
        var sent = Assert.Single(_smtp.Messages);
        Assert.Contains("RCPT TO:<ana@example.com>", sent.Envelope);
        Assert.Contains("Subject: Re: Invoice question", sent.Data);
        Assert.Contains("In-Reply-To: <q1@example.com>", sent.Data);
        Assert.Contains("Auto-Submitted: auto-replied", sent.Data);
        Assert.Contains("The invoice is due on the 25th.", sent.Data);

        var threadId = await gateway.ThreadForAsync(channel.Id, "ana@example.com");
        var messages = await _sp.GetRequiredService<IMessageStore>().ListAsync(threadId!);
        var question = Assert.Single(messages, m => m.Role == "user");
        Assert.Contains("Subject: Invoice question", question.Content);
        Assert.Contains("When is the invoice due?", question.Content);
        Assert.DoesNotContain("old quoted text", question.Content);

        // Nothing new: nothing handled.
        Assert.Equal(0, await poller.PollOnceAsync(channel, default));
    }

    [Fact]
    public void Body_strips_quotes_signatures_and_html()
    {
        var m = MimeKit.MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            "From: a@b.c\r\nSubject: x\r\nMIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<p>Halo tim,</p><p>tolong cek&nbsp;laporan</p><div>Pada Senin, Budi menulis:</div><blockquote>lama</blockquote>")));
        Assert.Equal("Halo tim,\ntolong cek laporan", EmailPoller.Body(m).Replace(" ", " "));
        var sig = MimeKit.MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes("From: a@b.c\r\nSubject: x\r\n\r\nThanks!\r\n-- \r\nAna, ACME\r\n")));
        Assert.Equal("Thanks!", EmailPoller.Body(sig));
    }

    /// <summary>Just enough IMAP4rev1 for MailKit: LOGIN, LIST, SELECT, UID SEARCH, UID FETCH, UID STORE, LOGOUT.</summary>
    private sealed class FakeImap : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public List<string> Messages { get; } = [];
        public ConcurrentBag<int> SeenUids { get; } = [];
        public string? LoginPassword { get; private set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(() => ServeAsync(client));
                }
            });
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Write(string s) { var b = Encoding.UTF8.GetBytes(s); await stream.WriteAsync(b); }
            await Write("* OK [CAPABILITY IMAP4rev1] fake ready\r\n");
            while (await reader.ReadLineAsync() is { } line)
            {
                var space = line.IndexOf(' ');
                var tag = line[..space];
                var cmd = line[(space + 1)..];
                var upper = cmd.ToUpperInvariant();
                if (upper.StartsWith("CAPABILITY")) await Write($"* CAPABILITY IMAP4rev1\r\n{tag} OK done\r\n");
                else if (upper.StartsWith("LOGIN"))
                {
                    var parts = cmd.Split(' ', 3);
                    LoginPassword = parts[2].Trim('"');
                    await Write($"{tag} OK [CAPABILITY IMAP4rev1] logged in\r\n");
                }
                else if (upper.StartsWith("LIST")) await Write($"* LIST (\\HasNoChildren) \"/\" INBOX\r\n{tag} OK done\r\n");
                else if (upper.StartsWith("SELECT") || upper.StartsWith("EXAMINE"))
                    await Write($"* FLAGS (\\Seen)\r\n* {Messages.Count} EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY 7] ok\r\n* OK [UIDNEXT {Messages.Count + 1}] ok\r\n{tag} OK [READ-WRITE] selected\r\n");
                else if (upper.StartsWith("UID SEARCH"))
                {
                    var unseen = Enumerable.Range(1, Messages.Count).Where(u => !SeenUids.Contains(u));
                    await Write($"* SEARCH {string.Join(' ', unseen)}\r\n{tag} OK done\r\n".Replace("* SEARCH \r\n", "* SEARCH\r\n"));
                }
                else if (upper.StartsWith("UID FETCH"))
                {
                    var uid = int.Parse(cmd.Split(' ')[2]);
                    var body = Encoding.UTF8.GetBytes(Messages[uid - 1]);
                    await Write($"* {uid} FETCH (UID {uid} BODY[] {{{body.Length}}}\r\n");
                    await stream.WriteAsync(body);
                    await Write($")\r\n{tag} OK done\r\n");
                }
                else if (upper.StartsWith("UID STORE"))
                {
                    SeenUids.Add(int.Parse(cmd.Split(' ')[2]));
                    await Write($"{tag} OK done\r\n");
                }
                else if (upper.StartsWith("LOGOUT")) { await Write($"* BYE\r\n{tag} OK bye\r\n"); return; }
                else await Write($"{tag} OK done\r\n");
            }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    public sealed record SmtpMessage(string Envelope, string Data);

    /// <summary>Just enough ESMTP for MailKit: EHLO, AUTH PLAIN, MAIL, RCPT, DATA, QUIT.</summary>
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public ConcurrentQueue<SmtpMessage> Messages { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    _ = Task.Run(() => ServeAsync(client));
                }
            });
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            async Task Write(string s) { var b = Encoding.ASCII.GetBytes(s); await stream.WriteAsync(b); }
            await Write("220 fake.smtp ESMTP\r\n");
            var envelope = new StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO") || upper.StartsWith("HELO")) await Write("250-fake.smtp\r\n250-AUTH PLAIN\r\n250 8BITMIME\r\n");
                else if (upper.StartsWith("AUTH")) await Write("235 ok\r\n");
                else if (upper.StartsWith("MAIL") || upper.StartsWith("RCPT")) { envelope.AppendLine(line); await Write("250 ok\r\n"); }
                else if (upper == "DATA")
                {
                    await Write("354 go\r\n");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d.StartsWith("..") ? d[1..] : d);
                    Messages.Enqueue(new SmtpMessage(envelope.ToString(), data.ToString()));
                    envelope.Clear();
                    await Write("250 queued\r\n");
                }
                else if (upper == "QUIT") { await Write("221 bye\r\n"); return; }
                else await Write("250 ok\r\n");
            }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }
}
