using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace NotificationService;

public class EmailSender(IConfiguration config)
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(config["Smtp:From"]!));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        // A short timeout, because a consumer holds up its queue while it waits.
        using var client = new SmtpClient { Timeout = 10_000 };
        // Auto uses TLS when the server offers it. Mailpit in development does not.
        await client.ConnectAsync(config["Smtp:Host"]!, config.GetValue<int>("Smtp:Port"), SecureSocketOptions.Auto, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
