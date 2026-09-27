using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Services.Facts;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using MassTransit;
using MockQueryable;
using NSubstitute;
using Shared.Messaging.Contracts.Events.Coaching;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services.Facts;

/// <summary>
/// Backfill: every piece of feedback a player can see goes out again as the snapshot a live share
/// sends, a batch at a time, each batch saved so the outbox sends it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FactRepublisherTests : UnitTestBase
{
    private List<Feedback> _feedback = null!;
    private IFeedbackRepository _feedbackRepository = null!;
    private IPublishEndpoint _bus = null!;
    private List<string> _calls = null!;
    private FactRepublisher _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedback = [];
        _calls = [];

        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _feedbackRepository.QueryNoTracking().Returns(_ => _feedback.BuildMock());
        _feedbackRepository.When(r => r.SaveChangesAsync()).Do(_ => _calls.Add("save"));

        _bus = Substitute.For<IPublishEndpoint>();
        _bus.When(b => b.Publish(Arg.Any<PraiseGivenEvent>(), Arg.Any<CancellationToken>()))
            .Do(_ => _calls.Add("praise"));

        _sut = new FactRepublisher(_feedbackRepository, _bus, TimeProvider);
    }

    [Test]
    public async Task RepublishAsync_PublishesEverySharedFeedbackGiven_AndCountsThem()
    {
        // Arrange
        var badged = AFeedback(shared: true, new Praise { Message = "Called it", BadgeType = BadgeType.LoudAndClear });
        var plain = AFeedback(shared: true);
        AFeedback(shared: false, new Praise { Message = "Private", BadgeType = BadgeType.Star });
        AFeedback(shared: true, deleted: true);

        // Act
        var result = await _sut.RepublishAsync();

        // Assert
        result.Praise.Should().Be(2);
        var sent = _bus.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<PraiseGivenEvent>().ToList();
        sent.Should().BeEquivalentTo(new[]
        {
            new { FeedbackId = badged.Id, State = PraiseState.Given, Badge = (string?)"LoudAndClear", SnapshotAt = Now },
            new { FeedbackId = plain.Id, State = PraiseState.Given, Badge = (string?)null, SnapshotAt = Now },
        });
    }

    [Test]
    public async Task RepublishAsync_SavesAfterEveryBatchOfFifty()
    {
        // Arrange
        for (var i = 0; i < 51; i++)
            AFeedback(shared: true);

        // Act
        await _sut.RepublishAsync();

        // Assert
        _calls.Should().Equal([.. Enumerable.Repeat("praise", 50), "save", "praise", "save"]);
    }

    [Test]
    public async Task RepublishAsync_WithNothingShared_SavesNothing()
    {
        // Arrange
        AFeedback(shared: false);

        // Act
        var result = await _sut.RepublishAsync();

        // Assert
        result.Praise.Should().Be(0);
        _calls.Should().BeEmpty();
    }

    private Feedback AFeedback(bool shared, Praise? praise = null, bool deleted = false)
    {
        var feedback = new Feedback
        {
            CoachUserId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            SharedWithPlayer = shared,
            IsDeleted = deleted,
            CreatedAt = PastDate(5),
            Praise = praise,
        };
        _feedback.Add(feedback);
        return feedback;
    }
}
