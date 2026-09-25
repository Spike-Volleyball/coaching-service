using Coaching.Application.DTOs.Feedback;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using NSubstitute;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Every piece of feedback a coach shares is a fact rewards-service counts, carrying the badge its
/// praise has, if any. The snapshot goes out when the feedback is shared, when its badge is put on,
/// changed or taken off while shared, and withdrawn when it is unshared or deleted — always before
/// the save, since the bus outbox drops a publish that follows the last one.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackPraiseFactTests : FeedbackServiceTestBase
{
    private static Praise WithBadge(BadgeType badge) => new() { Message = "Well played", BadgeType = badge };

    private List<PraiseGivenEvent> Published() =>
        [.. _bus.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<PraiseGivenEvent>()];

    private void AssertPublishedBeforeTheSave(Action save) =>
        Received.InOrder(() =>
        {
            _bus.Publish(Arg.Any<PraiseGivenEvent>(), Arg.Any<CancellationToken>());
            save();
        });

    [Test]
    public async Task ShareWithPlayerAsync_PrivateFeedbackWithABadge_PublishesItGivenBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false, WithBadge(BadgeType.Hustle));

        // Act
        await _sut.ShareWithPlayerAsync(feedback.Id, share: true, CoachId);

        // Assert
        Published().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FeedbackId = feedback.Id,
            CoachUserId = CoachId,
            PlayerUserId = PlayerId,
            State = PraiseState.Given,
            Badge = "Hustle",
            GivenAt = feedback.CreatedAt,
            SnapshotAt = Now,
        });
        AssertPublishedBeforeTheSave(() => _feedbackRepository.SaveChangesAsync());
    }

    [Test]
    public async Task ShareWithPlayerAsync_PrivateFeedbackWithoutPraise_PublishesItGivenWithNoBadge()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false);

        // Act
        await _sut.ShareWithPlayerAsync(feedback.Id, share: true, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().BeNull();
    }

    [Test]
    public async Task ShareWithPlayerAsync_SharedAgain_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Hustle));

        // Act
        await _sut.ShareWithPlayerAsync(feedback.Id, share: true, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task ShareWithPlayerAsync_Unsharing_PublishesItWithdrawnBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Hustle));

        // Act
        await _sut.ShareWithPlayerAsync(feedback.Id, share: false, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Withdrawn);
        fact.Badge.Should().BeNull();
        AssertPublishedBeforeTheSave(() => _feedbackRepository.SaveChangesAsync());
    }

    [Test]
    public async Task CreateAsync_SharedWithABadge_PublishesItGivenBeforeTheLastSave()
    {
        // Arrange
        Feedback? created = null;
        _feedbackRepository.When(r => r.Add(Arg.Any<Feedback>())).Do(c => created = c.Arg<Feedback>());
        _feedbackRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>()).Returns(_ => created);
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            SharedWithPlayer = true,
            Praise = new CreatePraiseDto { Message = "Never gave up", BadgeType = BadgeType.GoodEnergy },
        };

        // Act
        await _sut.CreateAsync(request, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.FeedbackId.Should().Be(created!.Id);
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().Be("GoodEnergy");
        Received.InOrder(() =>
        {
            _feedbackRepository.SaveChangesAsync();
            _bus.Publish(Arg.Any<PraiseGivenEvent>(), Arg.Any<CancellationToken>());
            _feedbackRepository.SaveChangesAsync();
        });
    }

    [Test]
    public async Task CreateAsync_Private_PublishesNothing()
    {
        // Arrange
        Feedback? created = null;
        _feedbackRepository.When(r => r.Add(Arg.Any<Feedback>())).Do(c => created = c.Arg<Feedback>());
        _feedbackRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>()).Returns(_ => created);
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            SharedWithPlayer = false,
            Praise = new CreatePraiseDto { Message = "Never gave up", BadgeType = BadgeType.GoodEnergy },
        };

        // Act
        await _sut.CreateAsync(request, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task UpdateAsync_SharingPrivateFeedback_PublishesItGivenWithItsBadge()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false, WithBadge(BadgeType.Coachable));

        // Act
        await _sut.UpdateAsync(feedback.Id, new UpdateFeedbackDto { SharedWithPlayer = true }, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().Be("Coachable");
        AssertPublishedBeforeTheSave(() => _feedbackRepository.SaveChangesAsync());
    }

    [Test]
    public async Task UpdateAsync_Unsharing_PublishesItWithdrawn()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Coachable));

        // Act
        await _sut.UpdateAsync(feedback.Id, new UpdateFeedbackDto { SharedWithPlayer = false }, CoachId);

        // Assert
        Published().Should().ContainSingle().Which.State.Should().Be(PraiseState.Withdrawn);
    }

    [Test]
    public async Task UpdateAsync_EditingTheNoteOfSharedFeedback_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Coachable));

        // Act
        await _sut.UpdateAsync(feedback.Id, new UpdateFeedbackDto { Content = "<p>Rewritten</p>" }, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task DeleteAsync_SharedFeedback_PublishesItWithdrawnBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.FairPlay));

        // Act
        await _sut.DeleteAsync(feedback.Id, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.FeedbackId.Should().Be(feedback.Id);
        fact.State.Should().Be(PraiseState.Withdrawn);
        fact.Badge.Should().BeNull();
        AssertPublishedBeforeTheSave(() => _feedbackRepository.SaveChangesAsync());
    }

    [Test]
    public async Task DeleteAsync_PrivateFeedback_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false, WithBadge(BadgeType.FairPlay));

        // Act
        await _sut.DeleteAsync(feedback.Id, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task AddPraiseAsync_WithABadgeOnSharedFeedback_PublishesTheBadgeBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true);

        // Act
        await _sut.AddPraiseAsync(feedback.Id, new CreatePraiseDto { Message = "Went for it", BadgeType = BadgeType.BraveCall }, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().Be("BraveCall");
        AssertPublishedBeforeTheSave(() => _praiseRepository.SaveChangesAsync());
    }

    [Test]
    public async Task AddPraiseAsync_WithoutABadge_PublishesNothing()
    {
        // Arrange — the feedback already counts as shared; praise without a badge changes nothing held.
        var feedback = GivenFeedback(shared: true);

        // Act
        await _sut.AddPraiseAsync(feedback.Id, new CreatePraiseDto { Message = "Went for it" }, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task AddPraiseAsync_OnPrivateFeedback_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false);

        // Act
        await _sut.AddPraiseAsync(feedback.Id, new CreatePraiseDto { Message = "Went for it", BadgeType = BadgeType.BraveCall }, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task AddPraiseAsync_AfterThePraiseWasRemoved_PublishesTheNewBadge()
    {
        // Arrange
        var removed = new Praise { Message = "Old", BadgeType = BadgeType.Star, IsDeleted = true };
        var feedback = GivenFeedback(shared: true, removed);

        // Act
        await _sut.AddPraiseAsync(feedback.Id, new CreatePraiseDto { Message = "New", BadgeType = BadgeType.Clutch }, CoachId);

        // Assert
        Published().Should().ContainSingle().Which.Badge.Should().Be("Clutch");
    }

    [Test]
    public async Task UpdatePraiseAsync_ChangingTheBadgeOnSharedFeedback_PublishesTheNewBadgeBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Star));

        // Act
        await _sut.UpdatePraiseAsync(feedback.Id, new UpdatePraiseDto { BadgeType = BadgeType.Leadership }, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().Be("Leadership");
        AssertPublishedBeforeTheSave(() => _praiseRepository.SaveChangesAsync());
    }

    [Test]
    public async Task UpdatePraiseAsync_ChangingOnlyTheMessage_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Star));

        // Act
        await _sut.UpdatePraiseAsync(feedback.Id, new UpdatePraiseDto { Message = "Even better" }, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task UpdatePraiseAsync_OnPrivateFeedback_PublishesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: false, WithBadge(BadgeType.Star));

        // Act
        await _sut.UpdatePraiseAsync(feedback.Id, new UpdatePraiseDto { BadgeType = BadgeType.Leadership }, CoachId);

        // Assert
        Published().Should().BeEmpty();
    }

    [Test]
    public async Task RemovePraiseAsync_WithABadgeOnSharedFeedback_PublishesItGivenWithNoBadgeBeforeTheSave()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, WithBadge(BadgeType.Skill));

        // Act
        await _sut.RemovePraiseAsync(feedback.Id, CoachId);

        // Assert
        var fact = Published().Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().BeNull();
        AssertPublishedBeforeTheSave(() => _praiseRepository.SaveChangesAsync());
    }
}
