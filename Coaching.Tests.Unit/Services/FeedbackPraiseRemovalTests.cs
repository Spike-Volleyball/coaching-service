using Coaching.Application.DTOs.Feedback;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using NSubstitute;
using Shared.Exceptions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Removing praise marks its row deleted, and a feedback holds one praise row, unique by feedback.
/// So a removed praise must read as absent everywhere: not shown, not editable, and given again
/// by reviving that row rather than refused as already there.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackPraiseRemovalTests : FeedbackServiceTestBase
{
    private static Praise RemovedPraise() =>
        new() { Message = "Big serve", BadgeType = BadgeType.Star, IsDeleted = true };

    [Test]
    public async Task AddPraiseAsync_AfterThePraiseWasRemoved_GivesTheSameRowAgain()
    {
        // Arrange
        var praise = RemovedPraise();
        var feedback = GivenFeedback(shared: true, praise);

        // Act
        var dto = await _sut.AddPraiseAsync(
            feedback.Id, new CreatePraiseDto { Message = "Chased every ball", BadgeType = BadgeType.Hustle }, CoachId);

        // Assert
        praise.IsDeleted.Should().BeFalse();
        praise.Message.Should().Be("Chased every ball");
        praise.BadgeType.Should().Be(BadgeType.Hustle);
        _praiseRepository.DidNotReceive().Add(Arg.Any<Praise>());
        _praiseRepository.Received(1).Update(praise);
        await _praiseRepository.Received(1).SaveChangesAsync();
        dto.Praise!.BadgeType.Should().Be(BadgeType.Hustle);
    }

    [Test]
    public async Task AddPraiseAsync_WhenPraiseIsThere_ThrowsConflict()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, new Praise { Message = "Big serve", BadgeType = BadgeType.Star });

        // Act
        var act = () => _sut.AddPraiseAsync(feedback.Id, new CreatePraiseDto { Message = "Again" }, CoachId);

        // Assert
        await act.Should().ThrowAsync<ConflictException>();
        await _praiseRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdatePraiseAsync_WhenThePraiseWasRemoved_ThrowsNotFound()
    {
        // Arrange
        var praise = RemovedPraise();
        var feedback = GivenFeedback(shared: true, praise);

        // Act
        var act = () => _sut.UpdatePraiseAsync(feedback.Id, new UpdatePraiseDto { BadgeType = BadgeType.Clutch }, CoachId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
        praise.BadgeType.Should().Be(BadgeType.Star);
        await _praiseRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task RemovePraiseAsync_WhenThePraiseWasAlreadyRemoved_ChangesNothing()
    {
        // Arrange
        var feedback = GivenFeedback(shared: true, RemovedPraise());

        // Act
        await _sut.RemovePraiseAsync(feedback.Id, CoachId);

        // Assert
        _praiseRepository.DidNotReceive().Update(Arg.Any<Praise>());
        await _praiseRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task RemovePraiseAsync_WithPraise_MarksItDeletedAndReadsWithoutIt()
    {
        // Arrange
        var praise = new Praise { Message = "Big serve", BadgeType = BadgeType.Star };
        var feedback = GivenFeedback(shared: true, praise);

        // Act
        var dto = await _sut.RemovePraiseAsync(feedback.Id, CoachId);

        // Assert
        praise.IsDeleted.Should().BeTrue();
        await _praiseRepository.Received(1).SaveChangesAsync();
        dto.Praise.Should().BeNull();
    }
}
