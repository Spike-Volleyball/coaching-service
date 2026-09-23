using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Services;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Models;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// The session and dashboard DTOs carry an evaluator's name and each seated player's name and
/// picture, and nothing filled them; the setup page and the dashboard showed bare ids.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationPeopleTests : UnitTestBase
{
    private IRepository<UserProfile> _profiles = null!;
    private EvaluationPeople _sut = null!;

    private readonly UserProfile _evaluator = new() { Name = "Casey", Surname = "Coach" };
    private readonly UserProfile _player = new() { Name = "Pat", Surname = "Player", ImageUrl = "https://cdn.test/pat.jpg" };

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _profiles = Substitute.For<IRepository<UserProfile>>();
        _profiles.QueryNoTracking().Returns(_ => new[] { _evaluator, _player }.BuildMock());
        _sut = new EvaluationPeople(_profiles);
    }

    [Test]
    public async Task FillAsync_Groups_NamesTheEvaluatorAndEachPlayerWithTheirPicture()
    {
        // Arrange
        var groups = new List<EvaluationGroupDto>
        {
            new() { Name = "Court one", EvaluatorUserId = _evaluator.Id, Players = [new GroupPlayerDto { PlayerId = _player.Id }] },
        };

        // Act
        await _sut.FillAsync(groups);

        // Assert
        groups[0].EvaluatorName.Should().Be("Casey Coach");
        groups[0].Players[0].PlayerName.Should().Be("Pat Player");
        groups[0].Players[0].AvatarUrl.Should().Be("https://cdn.test/pat.jpg");
    }

    [Test]
    public async Task FillAsync_Groups_LeavesSomeoneWithNoProfileUnnamed()
    {
        // Arrange
        var groups = new List<EvaluationGroupDto>
        {
            new() { Name = "Court one", EvaluatorUserId = null, Players = [new GroupPlayerDto { PlayerId = Guid.NewGuid() }] },
        };

        // Act
        await _sut.FillAsync(groups);

        // Assert
        groups[0].EvaluatorName.Should().BeNull();
        groups[0].Players[0].PlayerName.Should().BeNull();
    }

    [Test]
    public async Task FillAsync_WithNobodyToName_DoesNotReadProfiles()
    {
        // Act
        await _sut.FillAsync(new List<EvaluationGroupDto> { new() { Name = "Empty court" } });

        // Assert
        _profiles.DidNotReceive().QueryNoTracking();
    }

    [Test]
    public async Task FillAsync_Progress_NamesEachGroupsEvaluator()
    {
        // Arrange
        var progress = new SessionProgressDto
        {
            Groups = [new GroupProgressDto { GroupName = "Court one", EvaluatorUserId = _evaluator.Id }],
        };

        // Act
        await _sut.FillAsync(progress);

        // Assert
        progress.Groups[0].EvaluatorName.Should().Be("Casey Coach");
    }
}
