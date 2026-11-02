using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NotificationService.Data;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace NotificationService.Tests;

// Runs Notification Service in memory against real PostgreSQL, RabbitMQ and an SMTP server (Mailpit) in Docker.
public class NotificationServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int MailpitApiPort = 8025;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.33")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(MailpitApiPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(MailpitApiPort).ForPath("/readyz")))
        .Build();

    private readonly HttpClient _mailpitApi = new();

    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    private string MailpitUrl => $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(MailpitApiPort)}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:NotificationDb", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqConnectionString);
        builder.UseSetting("Smtp:Host", _mailpit.Hostname);
        builder.UseSetting("Smtp:Port", _mailpit.GetMappedPublicPort(SmtpPort).ToString());
    }

    public NotificationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Notifications.ExecuteDeleteAsync();
        await _mailpitApi.DeleteAsync($"{MailpitUrl}/api/v1/messages");
    }

    // Emails caught by Mailpit, newest first.
    public async Task<List<ReceivedEmail>> GetEmailsAsync()
    {
        var list = await _mailpitApi.GetFromJsonAsync<JsonElement>($"{MailpitUrl}/api/v1/messages");
        var emails = new List<ReceivedEmail>();
        foreach (var summary in list.GetProperty("messages").EnumerateArray())
        {
            var email = await _mailpitApi.GetFromJsonAsync<JsonElement>($"{MailpitUrl}/api/v1/message/{summary.GetProperty("ID").GetString()}");
            emails.Add(new ReceivedEmail(
                email.GetProperty("To")[0].GetProperty("Address").GetString()!,
                email.GetProperty("Subject").GetString()!,
                email.GetProperty("Text").GetString()!));
        }
        return emails;
    }

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync(), _mailpit.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _mailpitApi.Dispose();
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mailpit.DisposeAsync();
    }
}

public record ReceivedEmail(string To, string Subject, string Text);
