using Coaching.Application.Consumers;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Messaging.Contracts.Events.Auth;
using Shared.Models;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Consumers;

[TestFixture]
[Category("Unit")]
public class UserDeletionConfirmedConsumerTests : UnitTestBase
{
    private IRepository<UserProfile> _userProfiles = null!;
    private IPublishEndpoint _publishEndpoint = null!;
    private UserDeletionConfirmedConsumer _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();

        _userProfiles = Substitute.For<IRepository<UserProfile>>();
        _publishEndpoint = Substitute.For<IPublishEndpoint>();

        _sut = new UserDeletionConfirmedConsumer(
            _userProfiles, _publishEndpoint, NullLogger<UserDeletionConfirmedConsumer>.Instance);
    }

    [Test]
    public async Task Consume_ErasesThisServicesCopyOfTheProfile()
    {
        // Arrange — the copy is erased here, not left to profiles' anonymised update: that update
        // races this handler, and what it would have cleared survived on prod
        var userId = Guid.NewGuid();
        var profile = new UserProfile
        {
            Id = userId,
            Name = "Lucia",
            Surname = "Moreno",
            Email = "lucia@example.com",
            ImageUrl = "https://cdn.example/lucia.png",
            DateOfBirth = new DateTime(2008, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
            IsEmailVerified = true
        };
        _userProfiles.GetByIdAsync(userId).Returns(profile);

        // Act
        await _sut.Consume(ContextFor(userId));

        // Assert
        profile.Name.Should().Be(UserProfile.DeletedUserName);
        profile.Surname.Should().BeEmpty();
        profile.DateOfBirth.Should().BeNull();
        profile.Email.Should().BeNull();
        profile.ImageUrl.Should().BeNull();
        profile.IsActive.Should().BeFalse();
        profile.IsEmailVerified.Should().BeFalse();
        await _userProfiles.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task Consume_NoLocalCopy_StillAcknowledges()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userProfiles.GetByIdAsync(userId).Returns((UserProfile?)null);

        // Act
        await _sut.Consume(ContextFor(userId));

        // Assert
        await _publishEndpoint.Received(1).Publish(
            Arg.Is<UserDataDeletedEvent>(e => e.UserId == userId && e.ServiceName == "coaching"),
            Arg.Any<CancellationToken>());
    }

    private static ConsumeContext<UserDeletionConfirmedEvent> ContextFor(Guid userId)
    {
        var context = Substitute.For<ConsumeContext<UserDeletionConfirmedEvent>>();
        context.Message.Returns(new UserDeletionConfirmedEvent { UserId = userId });
        return context;
    }
}
