using MassTransit;
using Microsoft.Extensions.Logging;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Messaging.Contracts.Events.Auth;
using Shared.Models;

namespace Coaching.Application.Consumers;

public class UserDeletionConfirmedConsumer : IConsumer<UserDeletionConfirmedEvent>
{
    private readonly IRepository<UserProfile> _userProfileRepository;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<UserDeletionConfirmedConsumer> _logger;

    public UserDeletionConfirmedConsumer(
        IRepository<UserProfile> userProfileRepository,
        IPublishEndpoint publishEndpoint,
        ILogger<UserDeletionConfirmedConsumer> logger)
    {
        _userProfileRepository = userProfileRepository;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserDeletionConfirmedEvent> context)
    {
        var userId = context.Message.UserId;
        _logger.LogInformation("Processing user deletion for coaching-service, user {UserId}", userId);

        var profile = await _userProfileRepository.GetByIdAsync(userId);
        if (profile != null)
        {
            profile.Anonymize();
            _userProfileRepository.Update(profile);
            await _userProfileRepository.SaveChangesAsync();
        }

        await _publishEndpoint.Publish(new UserDataDeletedEvent
        {
            UserId = userId,
            ServiceName = "coaching"
        });

        _logger.LogInformation("User deletion completed for coaching-service, user {UserId}", userId);
    }
}
