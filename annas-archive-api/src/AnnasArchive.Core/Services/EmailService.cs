using System;
using System.IO;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Serilog;

namespace AnnasArchive.Core.Services;

public interface IEmailService
{
    Task SendEmailWithAttachmentAsync(string recipientEmail, string subject, string body, string filePath, string fileName);
}

public class EmailService : IEmailService
{
    private readonly string _smtpServer;
    private readonly int _smtpPort;
    private readonly string _senderEmail;
    private readonly string _senderPassword;

    public EmailService(IConfiguration configuration)
    {
        _smtpServer = configuration["Email:SmtpServer"] ?? throw new InvalidOperationException("Email:SmtpServer not configured");
        _smtpPort = int.Parse(configuration["Email:SmtpPort"] ?? "587");
        _senderEmail = configuration["Email:SenderEmail"] ?? throw new InvalidOperationException("Email:SenderEmail not configured");
        _senderPassword = configuration["Email:SenderPassword"] ?? throw new InvalidOperationException("Email:SenderPassword not configured");

        Log.Information("[Email] EmailService initialized with SMTP server {SmtpServer}:{SmtpPort}", _smtpServer, _smtpPort);
    }

    public async Task SendEmailWithAttachmentAsync(string recipientEmail, string subject, string body, string filePath, string fileName)
    {
        var fileSize = new FileInfo(filePath).Length;
        Log.Information("[Email] Sending email to {RecipientEmail} with subject '{Subject}', attachment: {FileName} ({FileSize} bytes)",
            recipientEmail, subject, fileName, fileSize);

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Anna's Archive", _senderEmail));
            message.To.Add(new MailboxAddress("", recipientEmail));
            message.Subject = subject;

            var builder = new BodyBuilder
            {
                TextBody = body
            };

            // Add attachment
            await builder.Attachments.AddAsync(fileName, File.OpenRead(filePath));

            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            Log.Debug("[Email] Connecting to SMTP server {SmtpServer}:{SmtpPort}", _smtpServer, _smtpPort);
            await client.ConnectAsync(_smtpServer, _smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_senderEmail, _senderPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            Log.Information("[Email] Successfully sent email to {RecipientEmail}", recipientEmail);
        }
        catch (Exception ex)
        {
            Log.Warning("[Email] Failed to send email to {RecipientEmail}: {ErrorMessage}", recipientEmail, ex.Message);
            throw;
        }
    }
}
