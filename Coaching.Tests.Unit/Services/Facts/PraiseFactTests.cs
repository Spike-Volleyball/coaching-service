using Coaching.Application.Services.Facts;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using Shared.Messaging.Contracts.Events.Coaching;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services.Facts;

/// <summary>
/// What a player holds from one piece of feedback: every shared piece counts, with or without a
/// badge, and nothing is held from feedback that is private or deleted.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PraiseFactTests : UnitTestBase
{
    private static Feedback AFeedback(bool shared, Praise? praise = null, bool deleted = false) => new()
    {
        CoachUserId = Guid.NewGuid(),
        RecipientUserId = Guid.NewGuid(),
        SharedWithPlayer = shared,
        IsDeleted = deleted,
        Praise = praise,
    };

    [Test]
    public void Of_SharedFeedbackWithABadge_IsGivenWithThatBadge()
    {
        // Arrange
        var feedback = AFeedback(shared: true, new Praise { Message = "Read the block", BadgeType = BadgeType.GameIq });

        // Act
        var fact = PraiseFact.Of(feedback);

        // Assert
        fact.Should().Be(new PraiseFact(PraiseState.Given, BadgeType.GameIq));
    }

    [Test]
    public void Of_SharedFeedbackWithoutPraise_IsGivenWithNoBadge()
    {
        // Act
        var fact = PraiseFact.Of(AFeedback(shared: true));

        // Assert
        fact.Should().Be(new PraiseFact(PraiseState.Given, null));
    }

    [Test]
    public void Of_SharedFeedbackWhosePraiseHasNoBadge_IsGivenWithNoBadge()
    {
        // Act
        var fact = PraiseFact.Of(AFeedback(shared: true, new Praise { Message = "Well played" }));

        // Assert
        fact.Should().Be(new PraiseFact(PraiseState.Given, null));
    }

    [Test]
    public void Of_SharedFeedbackWhosePraiseWasRemoved_IsGivenWithNoBadge()
    {
        // Arrange
        var removed = new Praise { Message = "Read the block", BadgeType = BadgeType.GameIq, IsDeleted = true };

        // Act
        var fact = PraiseFact.Of(AFeedback(shared: true, removed));

        // Assert
        fact.Should().Be(new PraiseFact(PraiseState.Given, null));
    }

    [Test]
    public void Of_PrivateFeedbackWithABadge_HoldsNothing()
    {
        // Act
        var fact = PraiseFact.Of(AFeedback(shared: false, new Praise { Message = "Clutch", BadgeType = BadgeType.Clutch }));

        // Assert
        fact.Should().Be(PraiseFact.None);
    }

    [Test]
    public void Of_DeletedSharedFeedback_HoldsNothing()
    {
        // Act
        var fact = PraiseFact.Of(AFeedback(shared: true, new Praise { Message = "Clutch", BadgeType = BadgeType.Clutch }, deleted: true));

        // Assert
        fact.Should().Be(PraiseFact.None);
    }

    [Test]
    public void Snapshot_OfGivenFeedback_NamesTheFeedbackItsPeopleAndTheBadge()
    {
        // Arrange
        var feedback = AFeedback(shared: true, new Praise { Message = "Called it", BadgeType = BadgeType.LoudAndClear });
        feedback.CreatedAt = PastDate(10);

        // Act
        var snapshot = PraiseFact.Of(feedback).Snapshot(feedback, Now);

        // Assert
        snapshot.Should().BeEquivalentTo(new
        {
            FeedbackId = feedback.Id,
            CoachUserId = feedback.CoachUserId,
            PlayerUserId = feedback.RecipientUserId,
            State = PraiseState.Given,
            Badge = "LoudAndClear",
            GivenAt = PastDate(10),
            SnapshotAt = Now,
        });
    }

    [Test]
    public void Snapshot_OfFeedbackTakenBack_IsWithdrawnWithNoBadge()
    {
        // Arrange
        var feedback = AFeedback(shared: false, new Praise { Message = "Called it", BadgeType = BadgeType.LoudAndClear });
        feedback.CreatedAt = PastDate(10);

        // Act
        var snapshot = PraiseFact.Of(feedback).Snapshot(feedback, Now);

        // Assert
        snapshot.State.Should().Be(PraiseState.Withdrawn);
        snapshot.Badge.Should().BeNull();
        snapshot.FeedbackId.Should().Be(feedback.Id);
        snapshot.GivenAt.Should().Be(PastDate(10));
        snapshot.SnapshotAt.Should().Be(Now);
    }
}
