using JaeZoo.Server.Models;

namespace JaeZoo.Server.Services.Email;

public interface IEmailSender
{
    Task SendEmailConfirmationCodeAsync(User user, string code, CancellationToken ct);
}
