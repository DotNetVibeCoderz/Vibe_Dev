using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Marbots.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Marbots.Runtime;

/// <summary>
/// E-mail channel: replies over SMTP. Each sender address is one conversation (one thread); replies keep the subject
/// ("Re: …") and set In-Reply-To/References so mail clients thread them.
/// Settings: imapHost, imapPort (993), imapSecurity (ssl|starttls|none), smtpHost, smtpPort (587), smtpSecurity
/// (starttls|ssl|none), username, fromAddress, fromName, folder (INBOX), pollSeconds (30). Secret: password.
/// </summary>
public sealed class EmailAdapter(ChannelContext ctx, IDocumentStore<ChannelConversation> conversations) : IChannelAdapter
{
    public string Kind => ChannelKinds.Email;

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var conv = await conversations.GetAsync($"{channel.Id}|{conversationId}", ct);
        var meta = conv?.Meta ?? [];
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(ChannelContext.Setting(channel, "fromName", channel.Name), From(channel)));
        message.To.Add(MailboxAddress.Parse(conversationId));
        var subject = meta.GetValueOrDefault("subject") is { Length: > 0 } s ? s : ChannelContext.Setting(channel, "subject", $"Message from {channel.Name}");
        message.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject;
        if (meta.GetValueOrDefault("messageId") is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
            foreach (var r in (meta.GetValueOrDefault("references") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) message.References.Add(r);
            message.References.Add(inReplyTo);
        }
        // Mark as automated so other auto-responders do not answer it (RFC 3834).
        message.Headers.Add("Auto-Submitted", "auto-replied");
        message.Headers.Add("X-Marbots-Channel", channel.Id);
        message.Body = new TextPart("plain") { Text = text };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(ChannelContext.Setting(channel, "smtpHost", ""), int.Parse(ChannelContext.Setting(channel, "smtpPort", "587"), System.Globalization.CultureInfo.InvariantCulture),
            Security(ChannelContext.Setting(channel, "smtpSecurity", "starttls")), ct);
        if (ctx.Secret(channel, "password") is { Length: > 0 } password)
            await smtp.AuthenticateAsync(ChannelContext.Setting(channel, "username", From(channel)), password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    public static string From(ChannelConfig c) => ChannelContext.Setting(c, "fromAddress", ChannelContext.Setting(c, "username", ""));

    public static SecureSocketOptions Security(string s) => s.ToLowerInvariant() switch
    {
        "ssl" or "tls" or "implicit" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}

/// <summary>Polls each enabled e-mail channel's IMAP folder for unseen mail and hands it to the channel's bot.</summary>
public sealed class EmailPoller(IDocumentStore<ChannelConfig> channels, IDocumentStore<ChannelConversation> conversations, ChannelGateway gateway,
    ChannelContext ctx, ILogger<EmailPoller> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextPoll = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var due = (await channels.ListAsync(stoppingToken))
                .Where(c => c.Enabled && c.Kind == ChannelKinds.Email && _nextPoll.GetValueOrDefault(c.Id) <= DateTimeOffset.UtcNow).ToList();
            foreach (var c in due)
            {
                var seconds = Math.Clamp(int.TryParse(ChannelContext.Setting(c, "pollSeconds", "30"), out var p) ? p : 30, 5, 3600);
                _nextPoll[c.Id] = DateTimeOffset.UtcNow.AddSeconds(seconds);
                await PollOnceAsync(c, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    /// <summary>Fetches unseen messages once; returns how many were handed to the bot.</summary>
    public async Task<int> PollOnceAsync(ChannelConfig c, CancellationToken ct)
    {
        var handled = 0;
        try
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(ChannelContext.Setting(c, "imapHost", ""), int.Parse(ChannelContext.Setting(c, "imapPort", "993"), System.Globalization.CultureInfo.InvariantCulture),
                EmailAdapter.Security(ChannelContext.Setting(c, "imapSecurity", "ssl")), ct);
            await imap.AuthenticateAsync(ChannelContext.Setting(c, "username", EmailAdapter.From(c)), ctx.RequireSecret(c, "password"), ct);
            var folder = await imap.GetFolderAsync(ChannelContext.Setting(c, "folder", "INBOX"), ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            foreach (var uid in uids.Take(25))
            {
                var mail = await folder.GetMessageAsync(uid, ct);
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                if (await HandleAsync(c, mail, ct)) handled++;
            }
            await imap.DisconnectAsync(true, ct);
            if (c.LastError is not null) { c.LastError = null; await channels.UpsertAsync(c, ct); }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or MailKit.ServiceNotAuthenticatedException
                                       or AuthenticationException or ImapCommandException or ImapProtocolException or InvalidOperationException or FormatException or FolderNotFoundException)
        {
            log.LogWarning("E-mail polling for {Channel} failed: {Error}", c.Name, ex.Message);
            c.LastError = ex.Message;
            await channels.UpsertAsync(c, CancellationToken.None);
        }
        return handled;
    }

    private async Task<bool> HandleAsync(ChannelConfig c, MimeMessage mail, CancellationToken ct)
    {
        var from = mail.From.Mailboxes.FirstOrDefault();
        if (from is null) return false;
        var address = from.Address.ToLowerInvariant();
        // Loop protection: our own mail, auto-replies, bounces and bulk mail are never answered.
        if (address.Equals(EmailAdapter.From(c), StringComparison.OrdinalIgnoreCase) || IsAutomated(mail)) return false;
        var text = Body(mail);
        if (text.Length == 0) return false;
        var subject = mail.Subject ?? "";
        var prompt = subject.Length > 0 && !IsReplySubject(subject) ? $"Subject: {subject}\n\n{text}" : text;

        // Remember threading headers for the reply before the bot starts (it may answer quickly).
        var key = $"{c.Id}|{address}";
        var conv = await conversations.GetAsync(key, ct);
        if (conv is not null)
        {
            conv.Meta["subject"] = subject;
            conv.Meta["messageId"] = mail.MessageId ?? "";
            conv.Meta["references"] = string.Join(' ', mail.References);
            await conversations.UpsertAsync(conv, ct);
        }
        try
        {
            await gateway.ReceiveAsync(c, new InboundMessage(address, address, from.Name is { Length: > 0 } n ? n : null, prompt), ct);
        }
        catch (ChannelAuthException ex)
        {
            log.LogInformation("E-mail from {Sender} ignored: {Reason}", address, ex.Message);
            return false;
        }
        if (conv is null && await conversations.GetAsync(key, ct) is { } created)
        {
            created.Meta["subject"] = subject;
            created.Meta["messageId"] = mail.MessageId ?? "";
            created.Meta["references"] = string.Join(' ', mail.References);
            await conversations.UpsertAsync(created, ct);
        }
        return true;
    }

    public static bool IsAutomated(MimeMessage m)
    {
        var auto = m.Headers[HeaderId.AutoSubmitted];
        if (!string.IsNullOrEmpty(auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)) return true;
        var precedence = m.Headers[HeaderId.Precedence]?.Trim().ToLowerInvariant();
        if (precedence is "bulk" or "list" or "junk" or "auto_reply") return true;
        if (m.Headers.Contains("X-Marbots-Channel") || m.Headers.Contains("X-Autoreply") || m.Headers.Contains("X-Autorespond")) return true;
        var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        return from.StartsWith("mailer-daemon@", StringComparison.OrdinalIgnoreCase) || from.StartsWith("postmaster@", StringComparison.OrdinalIgnoreCase)
            || from.StartsWith("no-reply@", StringComparison.OrdinalIgnoreCase) || from.StartsWith("noreply@", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplySubject(string s) => s.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Bls:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The new text of a message: plain text (or HTML without tags), quoted history and signatures removed.</summary>
    public static string Body(MimeMessage m)
    {
        var text = m.TextBody;
        if (string.IsNullOrWhiteSpace(text) && m.HtmlBody is { } html)
            text = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"<(br|/p|/div)[^>]*>", "\n", RegexOptions.IgnoreCase), "<[^>]+>", ""));
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            // Stop at the quoted original: "On … wrote:", "Pada … menulis:", Outlook's "-----Original Message-----" or "From:" header block.
            if (Regex.IsMatch(line, @"^(On .+wrote:|Pada .+menulis:|-{2,}\s*Original Message\s*-{2,}|_{5,}|From: .+)$", RegexOptions.IgnoreCase)) break;
            if (raw == "-- ") break;
            if (line.StartsWith('>')) continue;
            lines.Add(line);
        }
        return string.Join('\n', lines).Trim();
    }
}
