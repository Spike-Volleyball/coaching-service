using AutoMapper;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class BadgeServiceTests : UnitTestBase
{
    private static readonly Guid PlayerId = Guid.NewGuid();
    private static readonly Guid SomeoneElseId = Guid.NewGuid();

    private IRepository<PlayerBadge> _badges = null!;
    private BadgeService _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _badges = Substitute.For<IRepository<PlayerBadge>>();
        _sut = new BadgeService(_badges, Substitute.For<IMapper>());
    }

    [Test]
    public async Task GetRecentBadgesAsync_ReturnsOnlyThePlayersOwn_NewestFirst()
    {
        // Arrange — "me/badges/recent" handed out every user's latest badges (SPI-6437).
        var older = Badge(PlayerId, PastDate(3));
        var newer = Badge(PlayerId, PastDate(1));
        var someoneElses = Badge(SomeoneElseId, PastDate(0));
        _badges.Query().Returns(new List<PlayerBadge> { older, someoneElses, newer }.BuildMock());

        // Act
        var recent = await _sut.GetRecentBadgesAsync(PlayerId, limit: 10);

        // Assert
        recent.Select(b => b.Id).Should().Equal(newer.Id, older.Id);
    }

    [Test]
    public async Task GetRecentBadgesAsync_NeverReturnsMoreThanItsCap()
    {
        // Arrange
        var badges = Enumerable.Range(1, BadgeService.MaxPageSize + 10).Select(day => Badge(PlayerId, PastDate(day))).ToList();
        _badges.Query().Returns(badges.BuildMock());

        // Act
        var recent = await _sut.GetRecentBadgesAsync(PlayerId, limit: int.MaxValue);

        // Assert
        recent.Should().HaveCount(BadgeService.MaxPageSize);
    }

    [Test]
    public async Task GetPlayerBadgesAsync_NeverReturnsMoreThanItsCap()
    {
        // Arrange
        var badges = Enumerable.Range(1, BadgeService.MaxPageSize + 10).Select(day => Badge(PlayerId, PastDate(day))).ToList();
        _badges.Query().Returns(badges.BuildMock());

        // Act
        var page = await _sut.GetPlayerBadgesAsync(PlayerId, page: 1, pageSize: int.MaxValue);

        // Assert
        page.Should().HaveCount(BadgeService.MaxPageSize);
    }

    private static PlayerBadge Badge(Guid userId, DateTime awardedAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BadgeType = default(BadgeType),
        Message = "Great serve",
        AwardedByUserId = Guid.NewGuid(),
        CreatedAt = awardedAt
    };
}
