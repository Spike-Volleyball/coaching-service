using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Services.Facts;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Tactics;
using FluentAssertions;
using MassTransit;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Messaging.Contracts.Events.Coaching;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services.Facts;

/// <summary>
/// Backfill: every piece of feedback a player can see, and every tactics board somebody drew, goes
/// out again as the snapshot a live change sends, a batch at a time, each batch saved so the outbox
/// sends it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FactRepublisherTests : UnitTestBase
{
    private List<Feedback> _feedback = null!;
    private List<TacticsBoard> _boards = null!;
    private IFeedbackRepository _feedbackRepository = null!;
    private IRepository<TacticsBoard> _boardRepository = null!;
    private IPublishEndpoint _bus = null!;
    private List<string> _calls = null!;
    private FactRepublisher _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedback = [];
        _boards = [];
        _calls = [];

        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _feedbackRepository.QueryNoTracking().Returns(_ => _feedback.BuildMock());
        _feedbackRepository.When(r => r.SaveChangesAsync()).Do(_ => _calls.Add("save"));

        _boardRepository = Substitute.For<IRepository<TacticsBoard>>();
        _boardRepository.QueryNoTracking().Returns(_ => _boards.BuildMock());
        _boardRepository.When(r => r.SaveChangesAsync()).Do(_ => _calls.Add("save"));

        _bus = Substitute.For<IPublishEndpoint>();
        _bus.When(b => b.Publish(Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(call => _calls.Add(call.Arg<object>() is PraiseGivenEvent ? "praise" : "board"));

        _sut = new FactRepublisher(_feedbackRepository, _boardRepository, _bus, TimeProvider);
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
    public async Task RepublishAsync_WithNothingSharedOrDrawn_SavesNothing()
    {
        // Arrange
        AFeedback(shared: false);
        ABoard(drawnAt: null);

        // Act
        var result = await _sut.RepublishAsync();

        // Assert
        result.Praise.Should().Be(0);
        result.Boards.Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Test]
    public async Task RepublishAsync_PublishesEveryBoardSomebodyDrew_FromWhenItWasFirstDrawn_AndCountsThem()
    {
        // Arrange — a blank board and a starter have no first-drawn time, and stay out.
        var drawn = ABoard(drawnAt: PastDate(3), "arrow", "heatmap");
        var plain = ABoard(drawnAt: PastDate(1));
        ABoard(drawnAt: null, "arrow");

        // Act
        var result = await _sut.RepublishAsync();

        // Assert
        result.Boards.Should().Be(2);
        var sent = _bus.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<TacticsBoardDrawnEvent>().ToList();
        sent.Should().BeEquivalentTo(new[]
        {
            new { BoardId = drawn.Id, MakerUserId = drawn.OwnerUserId, DrawnAt = PastDate(3), Tools = new[] { "arrow", "heatmap" }, SnapshotAt = Now },
            new { BoardId = plain.Id, MakerUserId = plain.OwnerUserId, DrawnAt = PastDate(1), Tools = Array.Empty<string>(), SnapshotAt = Now },
        });
    }

    [Test]
    public async Task RepublishAsync_SavesTheBoardsAfterThePraise_ABatchAtATime()
    {
        // Arrange
        AFeedback(shared: true);
        for (var i = 0; i < 51; i++)
            ABoard(drawnAt: PastDate(1));

        // Act
        await _sut.RepublishAsync();

        // Assert
        _calls.Should().Equal(["praise", "save", .. Enumerable.Repeat("board", 50), "save", "board", "save"]);
    }

    private TacticsBoard ABoard(DateTime? drawnAt, params string[] tools)
    {
        var board = new TacticsBoard
        {
            Title = "Serve receive",
            Category = "Match day",
            System = "5-1",
            Document = "{}",
            OwnerUserId = Guid.NewGuid(),
            Origin = drawnAt is null ? TacticsBoardOrigin.Starter : TacticsBoardOrigin.Made,
            DrawnAt = drawnAt,
            Tools = [.. tools],
        };
        _boards.Add(board);
        return board;
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
