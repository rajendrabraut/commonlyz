using Commonlyz.Core;
using Commonlyz.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Commonlyz.Web.Notifications;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Outbox-backed notifications: modules call <see cref="INotificationSender"/>, which queues; the dispatcher
    /// delivers over SMTP when Email:Host is set, otherwise to the log.
    /// </summary>
    public static IServiceCollection AddEmailNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.Section)
            .ValidateDataAnnotations()
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.Timeout > TimeSpan.Zero, "Email:PollInterval and Email:Timeout must be positive.")
            .ValidateOnStart();

        services.AddModuleDbContext<NotificationsDb>(NotificationsDb.Schema);
        services.AddSingleton<INotificationSender, OutboxNotificationSender>();
        services.AddSingleton<IEmailTransport>(sp =>
            sp.GetRequiredService<IOptions<EmailOptions>>().Value.SmtpEnabled
                ? ActivatorUtilities.CreateInstance<SmtpEmailTransport>(sp)
                : ActivatorUtilities.CreateInstance<LogEmailTransport>(sp));
        services.AddSingleton<EmailOutbox>();
        services.AddSingleton<EmailDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<EmailDispatcher>());
        services.AddHealthChecks().AddDbContextCheck<NotificationsDb>("notifications-db");
        return services;
    }
}
