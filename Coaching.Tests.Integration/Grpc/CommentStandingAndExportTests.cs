using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Templates;
using Coaching.Grpc;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shared.Contracts.Grpc;
using Shared.Models;

namespace Coaching.Tests.Integration.Grpc;

/// <summary>
/// What social asks coaching before it serves a drill's or a plan's comments, and the one-off
/// export of the comments coaching kept. The service is called as the internal listener would, on
/// the real database and the real access rules.
/// </summary>
[TestFixture]
[Category("Integration")]
public class CommentStandingAndExportTests
{
    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid ReaderId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();
    private static readonly Guid EventAdminId = Guid.NewGuid();

    private CoachingApiFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _ = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [TearDown]
    public async Task TearDown() => await _factory.DatabaseResetter.ResetAsync();

    [Test]
    public async Task Standing_OnAPublicDrill_IsReadableByAStranger()
    {
        // Arrange
        var drill = await SeedDrillAsync(DrillVisibility.Public);

        // Act
        var standing = await StandingAsync(CommentResourceKind.Drill, drill.Id, StrangerId);

        // Assert
        Describe(standing).Should().Be("exists read");
    }

    [Test]
    public async Task Standing_OnAPrivateClubDrill_IsReadableByMembersOnly()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var drill = await SeedDrillAsync(DrillVisibility.Private, clubId);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ReaderId, clubId).Returns(true);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(StrangerId, clubId).Returns(false);

        // Act
        var member = await StandingAsync(CommentResourceKind.Drill, drill.Id, ReaderId);
        var nonMember = await StandingAsync(CommentResourceKind.Drill, drill.Id, StrangerId);

        // Assert
        Describe(member).Should().Be("exists read");
        Describe(nonMember).Should().Be("exists");
    }

    [Test]
    public async Task Standing_OnAPrivateTemplate_IsReadableByItsCreatorOnly()
    {
        // Arrange
        var plan = await SeedPlanAsync(PlanType.Template, TemplateVisibility.Private);

        // Act
        var creator = await StandingAsync(CommentResourceKind.Plan, plan.Id, CreatorId);
        var stranger = await StandingAsync(CommentResourceKind.Plan, plan.Id, StrangerId);

        // Assert
        Describe(creator).Should().Be("exists read moderate");
        Describe(stranger).Should().Be("exists");
    }

    [Test]
    public async Task Standing_OnAPublicTemplate_IsReadableButNotModeratedByAReader()
    {
        // Arrange
        var plan = await SeedPlanAsync(PlanType.Template, TemplateVisibility.Public);

        // Act
        var standing = await StandingAsync(CommentResourceKind.Plan, plan.Id, ReaderId);

        // Assert
        Describe(standing).Should().Be("exists read");
    }

    [Test]
    public async Task Standing_OnAnEventsPlan_FollowsTheEventsStanding()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var plan = await SeedPlanAsync(PlanType.Instance, TemplateVisibility.Private, eventId);
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId).Returns((true, true));
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, StrangerId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, EventAdminId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, CreatorId).Returns((true, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, EventAdminId).Returns(true);

        // Act
        var participant = await StandingAsync(CommentResourceKind.Plan, plan.Id, ReaderId);
        var stranger = await StandingAsync(CommentResourceKind.Plan, plan.Id, StrangerId);
        var eventAdmin = await StandingAsync(CommentResourceKind.Plan, plan.Id, EventAdminId);
        var creator = await StandingAsync(CommentResourceKind.Plan, plan.Id, CreatorId);

        // Assert
        Describe(participant).Should().Be("exists read");
        Describe(stranger).Should().Be("exists");
        Describe(eventAdmin).Should().Be("exists read moderate");
        Describe(creator).Should().Be("exists read moderate");
    }

    [Test]
    public async Task Standing_OnAnEventsPlan_WhenTheEventsServiceFails_IsAnError()
    {
        // Arrange — a failure to find out is not a no
        var eventId = Guid.NewGuid();
        var plan = await SeedPlanAsync(PlanType.Instance, TemplateVisibility.Private, eventId);
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId)
            .Returns<(bool, bool)>(_ => throw new InvalidOperationException("events-service unavailable"));

        // Act
        var act = () => StandingAsync(CommentResourceKind.Plan, plan.Id, ReaderId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Standing_OfSomethingThatIsNotThere_IsNothing()
    {
        // Arrange
        var plan = await SeedPlanAsync(PlanType.Template, TemplateVisibility.Public);

        // Act
        var unknownDrill = await StandingAsync(CommentResourceKind.Drill, Guid.NewGuid(), CreatorId);
        var unknownPlan = await StandingAsync(CommentResourceKind.Plan, Guid.NewGuid(), CreatorId);
        var unspecified = await StandingAsync(CommentResourceKind.Unspecified, plan.Id, CreatorId);
        var drillAskedAsAPlan = await StandingAsync(CommentResourceKind.Drill, plan.Id, CreatorId);

        // Assert
        new[] { unknownDrill, unknownPlan, unspecified, drillAskedAsAPlan }
            .Select(Describe).Should().AllBe("nothing");
    }

    [Test]
    public async Task Export_PagesThroughEveryDrillCommentById_DeletedOnesFlagged()
    {
        // Arrange
        var drill = await SeedDrillAsync(DrillVisibility.Public);
        var comments = await SeedDrillCommentsAsync(drill.Id, count: 5);

        // Act
        var pages = await ExportAllAsync(CommentResourceKind.Drill, limit: 2);

        // Assert
        pages.Select(p => p.Comments.Count).Should().Equal(2, 2, 1);
        var exported = pages.SelectMany(p => p.Comments).ToList();
        exported.Select(c => c.Id).Should().Equal(comments.Select(c => c.Id.ToString()));
        exported.Select(c => c.IsDeleted).Should().Equal(false, true, false, false, false);
        exported[2].ParentCommentId.Should().Be(exported[0].Id);
        exported[3].ParentCommentId.Should().Be(exported[2].Id, "a reply to a reply keeps its parent");
        exported[0].Should().BeEquivalentTo(new
        {
            ResourceId = drill.Id.ToString(),
            AuthorId = CreatorId.ToString(),
            Content = "comment 0",
            ParentCommentId = ""
        });
        DateTime.Parse(exported[0].CreatedAt).Kind.Should().NotBe(DateTimeKind.Unspecified);
        pages[^1].NextAfterId.Should().BeEmpty();
        pages[0].NextAfterId.Should().Be(exported[1].Id);
    }

    [Test]
    public async Task Export_OfPlanComments_ReturnsOnlyPlanComments()
    {
        // Arrange
        var drill = await SeedDrillAsync(DrillVisibility.Public);
        await SeedDrillCommentsAsync(drill.Id, count: 1);
        var plan = await SeedPlanAsync(PlanType.Template, TemplateVisibility.Public);
        var planComment = new PlanComment { TemplateId = plan.Id, UserId = CreatorId, Content = "plan", IsDeleted = true };
        await SeedAsync(planComment);

        // Act
        var pages = await ExportAllAsync(CommentResourceKind.Plan, limit: 10);

        // Assert
        var exported = pages.Should().ContainSingle().Which.Comments;
        exported.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = planComment.Id.ToString(),
            ResourceId = plan.Id.ToString(),
            IsDeleted = true
        });
    }

    [Test]
    public async Task Export_WithNoKind_IsRefused()
    {
        // Act
        var act = () => ServiceCall(s => s.ExportComments(new ExportCommentsRequest(), null!));

        // Assert
        (await act.Should().ThrowAsync<global::Grpc.Core.RpcException>())
            .Which.StatusCode.Should().Be(global::Grpc.Core.StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Export_AsksForMoreThanTheCeiling_IsClamped()
    {
        // Arrange
        var drill = await SeedDrillAsync(DrillVisibility.Public);
        await SeedDrillCommentsAsync(drill.Id, count: 3);

        // Act
        var page = await ServiceCall(s => s.ExportComments(
            new ExportCommentsRequest { Kind = CommentResourceKind.Drill, Limit = int.MaxValue }, null!));

        // Assert
        page.Comments.Should().HaveCount(3);
        page.NextAfterId.Should().BeEmpty();
    }

    private static string Describe(GetCommentStandingResponse standing) =>
        string.Join(" ", new[]
        {
            standing.Exists ? "exists" : null,
            standing.CanRead ? "read" : null,
            standing.CanModerate ? "moderate" : null
        }.OfType<string>().DefaultIfEmpty("nothing"));

    private Task<GetCommentStandingResponse> StandingAsync(CommentResourceKind kind, Guid resourceId, Guid userId) =>
        ServiceCall(s => s.GetCommentStanding(
            new GetCommentStandingRequest { Kind = kind, ResourceId = resourceId.ToString(), UserId = userId.ToString() },
            null!));

    private async Task<List<ExportCommentsResponse>> ExportAllAsync(CommentResourceKind kind, int limit)
    {
        var pages = new List<ExportCommentsResponse>();
        var after = "";
        do
        {
            var page = await ServiceCall(s => s.ExportComments(
                new ExportCommentsRequest { Kind = kind, AfterId = after, Limit = limit }, null!));
            pages.Add(page);
            after = page.NextAfterId;
        } while (after.Length > 0);
        return pages;
    }

    private async Task<T> ServiceCall<T>(Func<CoachingInternalServiceImpl, Task<T>> call)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await call(ActivatorUtilities.CreateInstance<CoachingInternalServiceImpl>(scope.ServiceProvider));
    }

    private async Task<Drill> SeedDrillAsync(DrillVisibility visibility, Guid? clubId = null)
    {
        var drill = new Drill
        {
            Name = "Serve receive",
            CreatedByUserId = CreatorId,
            Visibility = visibility,
            ClubId = clubId,
            Skills = [],
            Instructions = [],
            CoachingPoints = []
        };
        await SeedAsync(Profile(CreatorId), drill);
        return drill;
    }

    private async Task<TrainingPlan> SeedPlanAsync(PlanType type, TemplateVisibility visibility, Guid? eventId = null)
    {
        var plan = new TrainingPlan
        {
            Name = "Thursday practice",
            CreatedByUserId = CreatorId,
            PlanType = type,
            EventId = eventId,
            Visibility = visibility
        };
        await SeedAsync(Profile(CreatorId), plan);
        return plan;
    }

    /// <summary>
    /// Comments 0, 1, 2 and 4 are kept and 1 was removed; 2 replies to 0, 3 replies to 2, and
    /// the ids are fixed so the order they are exported in is the order they are listed here.
    /// </summary>
    private async Task<List<DrillComment>> SeedDrillCommentsAsync(Guid drillId, int count)
    {
        var comments = Enumerable.Range(0, count)
            .Select(i => new DrillComment
            {
                Id = new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                DrillId = drillId,
                UserId = CreatorId,
                Content = $"comment {i}",
                IsDeleted = i == 1
            })
            .ToList();
        if (count > 2) comments[2].ParentCommentId = comments[0].Id;
        if (count > 3) comments[3].ParentCommentId = comments[2].Id;

        await SeedAsync(Profile(CreatorId));
        await SeedAsync(comments.Cast<object>().ToArray());
        return comments;
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        foreach (var entity in entities)
        {
            if (entity is UserProfile profile && await db.Set<UserProfile>().AnyAsync(p => p.Id == profile.Id))
                continue;
            db.Add(entity);
        }
        await db.SaveChangesAsync();
    }

    private static UserProfile Profile(Guid id) => new()
    {
        Id = id,
        Name = "Test",
        Surname = "Coach",
        Email = $"{id:N}@test.local",
        IsActive = true
    };
}
