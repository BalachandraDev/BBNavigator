using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowMind.Infrastructure.Accounts;

/// <summary>Settings for the mail server FlowMind uses to send invitations and password-reset links. Empty = no e-mail is sent; admins copy the link instead.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>STARTTLS / TLS. Leave on for every real mail server.</summary>
    public bool EnableSsl { get; set; } = true;
    public string From { get; set; } = "";
    public string FromName { get; set; } = "BBNavigator";
}

public interface IEmailSender
{
    /// <summary>True when a mail server is configured.</summary>
    bool Enabled { get; }
    /// <summary>Sends a plain-text message. Returns false (and logs why, without the message) when it could not be sent.</summary>
    Task<bool> SendAsync(string to, string subject, string textBody, CancellationToken ct);
}

/// <summary>Sends through the operator's own SMTP server. No third-party service is involved unless the operator points it at one.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _o;
    private readonly ILogger<SmtpEmailSender> _log;
    public SmtpEmailSender(IOptions<EmailOptions> o, ILogger<SmtpEmailSender> log) { _o = o.Value; _log = log; }

    public bool Enabled => !string.IsNullOrWhiteSpace(_o.Host) && !string.IsNullOrWhiteSpace(_o.From);

    public async Task<bool> SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        if (!Enabled) return false;
        try
        {
            using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.EnableSsl, Timeout = 15000 };
            if (!string.IsNullOrEmpty(_o.User)) client.Credentials = new NetworkCredential(_o.User, _o.Password);
            using var msg = new MailMessage { From = new MailAddress(_o.From, _o.FromName), Subject = subject, Body = textBody, IsBodyHtml = false };
            msg.To.Add(new MailAddress(to));
            await client.SendMailAsync(msg, ct);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            _log.LogError(ex, "Could not send an e-mail through {Host}:{Port}", _o.Host, _o.Port);
            return false;
        }
    }
}
