using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coaching.Application.DTOs.Drills;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shared.DTOs;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

[TestFixture]
[Category("Integration")]
public class DrillsControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    public sealed record DrillRoute(string Method, string Template)
    {
        public override string ToString() => $"{Method} {Template}";
    }

    private static readonly DrillRoute[] RoutesNamingADrill =
    [
        new("GET", "drills/{drill}"),
        new("PUT", "drills/{drill}"),
        new("DELETE", "drills/{drill}"),
        new("POST", "drills/{drill}/dials"),
        new("PATCH", "drills/{drill}/dials/balls"),
        new("DELETE", "drills/{drill}/dials/balls"),
        new("POST", "drills/{drill}/fold"),
        new("POST", "drills/{drill}/like"),
        new("DELETE", "drills/{drill}/like"),
        new("GET", "drills/{drill}/like"),
        new("POST", "drills/{drill}/bookmark"),
        new("DELETE", "drills/{drill}/bookmark"),
        new("POST", "drills/{drill}/comments"),
        new("GET", "drills/{drill}/comments"),
        new("DELETE", "drills/{drill}/comments/{sub}"),
        new("POST", "drills/{drill}/attachments/upload-url"),
        new("POST", "drills/{drill}/attachments"),
        new("DELETE", "drills/{drill}/attachments/{sub}"),
        new("PUT", "drills/{drill}/animations"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
        _factory.ClubsGrpcClient.ClearReceivedCalls();
    }

    [Test]
    public async Task Create_WithCompleteRequest_ReturnsCreatedAndPersistsCompleteDrill()
    {
        // Arrange
        var targetOne = NewDrill("Target one", CreatorId);
        var targetTwo = NewDrill("Target two", CreatorId);
        await SeedAsync([CreatorProfile(), targetOne, targetTwo]);
        SetAuth(CreatorId);
        var request = CompleteCreateRequest(
            variations:
            [
                new CreateDrillVariationInput(targetOne.Id, "Simpler version"),
                new CreateDrillVariationInput(targetTwo.Id, "Harder version")
            ]);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);

        // Assert - HTTP representation
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        created.Should().NotBeNull();
        created!.Id.Should().NotBeEmpty();
        created.Name.Should().Be(request.Name);
        created.CreatedByUserId.Should().Be(CreatorId);
        created.LikeCount.Should().Be(0);
        created.Equipment.Select(e => (e.Name, e.IsOptional, e.Order)).Should().Equal(
            ("Volleyballs", false, 0),
            ("Targets", true, 1));
        created.Variations.Select(v => (v.DrillId, v.Note, v.Order)).Should().Equal(
            (targetOne.Id, "Simpler version", 0),
            (targetTwo.Id, "Harder version", 1));
        response.Headers.Location.Should().NotBeNull();

        // Assert - durable database state, independent of the response mapping
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var persisted = await db.Drills
            .AsNoTracking()
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Variations.OrderBy(v => v.Order))
            .SingleAsync(d => d.Id == created.Id);

        persisted.Should().BeEquivalentTo(new
        {
            request.Name,
            request.Description,
            request.Category,
            request.Intensity,
            request.Visibility,
            request.Duration,
            request.MinPlayers,
            request.MaxPlayers,
            request.VideoUrl,
            CreatedByUserId = CreatorId,
            LikeCount = 0
        });
        persisted.Skills.Should().Equal(request.Skills);
        persisted.Instructions.Should().Equal(request.Instructions);
        persisted.CoachingPoints.Should().Equal(request.CoachingPoints);
        persisted.Equipment.Select(e => (e.Name, e.IsOptional, e.Order)).Should().Equal(
            ("Volleyballs", false, 0),
            ("Targets", true, 1));
        persisted.Variations.Select(v => (v.TargetDrillId, v.Note, v.Order)).Should().Equal(
            (targetOne.Id, "Simpler version", 0),
            (targetTwo.Id, "Harder version", 1));
    }

    [Test]
    public async Task Create_WithSkillNumberEight_ReadsBackAsEyeworkReading()
    {
        // Arrange - the web sends skills by number, so 8 is the contract for Eyework/Reading
        await SeedAsync([CreatorProfile()]);
        SetAuth(CreatorId);
        var body = JsonSerializer.SerializeToNode(CompleteCreateRequest(), JsonOptions)!.AsObject();
        body["skills"] = new JsonArray(8);

        // Act
        var created = await _client.PostAsJsonAsync("/v1/drills", body);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var read = await _client.GetFromJsonAsync<JsonElement>($"/v1/drills/{id}");

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        read.GetProperty("skills").EnumerateArray().Select(s => s.ToString()).Should().Equal("Reading");
    }

    [Test]
    public async Task Create_WithoutAuthentication_ReturnsUnauthorizedAndDoesNotPersist()
    {
        // Arrange
        await SeedAsync([CreatorProfile()]);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/drills", CompleteCreateRequest(), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountDrillsAsync()).Should().Be(0);
    }

    [Test]
    public async Task Create_WithBlankName_ReturnsBadRequestAndDoesNotPersist()
    {
        // Arrange
        await SeedAsync([CreatorProfile()]);
        SetAuth(CreatorId);
        var request = CompleteCreateRequest() with { Name = "   " };

        // Act
        var response = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountDrillsAsync()).Should().Be(0);
    }

    [Test]
    public async Task Create_WithAJavascriptVideoUrl_ReturnsBadRequestNamingItAndDoesNotPersist()
    {
        // Arrange
        await SeedAsync([CreatorProfile()]);
        SetAuth(CreatorId);
        var request = CompleteCreateRequest() with { VideoUrl = "javascript:alert(document.cookie)" };

        // Act
        var response = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldsNamedBy(response)).Should().Equal("videoUrl");
        (await CountDrillsAsync()).Should().Be(0);
    }

    [Test]
    public async Task Create_ForClub_RequiresCoachRoleAndPersistsWhenAuthorized()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        await SeedAsync([CreatorProfile()]);
        SetAuth(CreatorId);
        var request = CompleteCreateRequest(clubId);
        _factory.ClubsGrpcClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(false);

        // Act / Assert - an ordinary member cannot create club-owned drills
        var forbidden = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountDrillsAsync()).Should().Be(0);

        // Act / Assert - HeadCoach/Admin/Owner authorization allows the same operation
        _factory.ClubsGrpcClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(true);
        var created = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var persisted = await FindDrillAsync((await created.Content.ReadFromJsonAsync<DrillDto>(JsonOptions))!.Id);
        persisted!.ClubId.Should().Be(clubId);
    }

    [Test]
    public async Task Update_AsCreator_ReplacesMutableFieldsEquipmentAndVariations()
    {
        // Arrange
        var oldTarget = NewDrill("Old target", CreatorId);
        var newTarget = NewDrill("New target", CreatorId);
        var source = NewDrill("Original drill", CreatorId);
        var oldEquipment = new DrillEquipment
        {
            DrillId = source.Id,
            Name = "Old equipment",
            Order = 0
        };
        var oldVariation = new DrillVariation
        {
            SourceDrillId = source.Id,
            TargetDrillId = oldTarget.Id,
            Note = "Old variation",
            Order = 0
        };
        source.Equipment.Add(oldEquipment);
        source.Variations.Add(oldVariation);
        source.LikeCount = 7;
        await SeedAsync([CreatorProfile(), oldTarget, newTarget, source]);
        SetAuth(CreatorId);
        var request = CompleteUpdateRequest(
            source.Id,
            variations: [new CreateDrillVariationInput(newTarget.Id, "Replacement variation")]);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/drills/{source.Id}", request, JsonOptions);

        // Assert - updated representation
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the API returned: {0}",
            await response.Content.ReadAsStringAsync());
        var updated = await response.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        updated.Should().NotBeNull();
        updated!.Id.Should().Be(source.Id);
        updated.Name.Should().Be(request.Name);
        updated.CreatedByUserId.Should().Be(CreatorId);
        updated.LikeCount.Should().Be(7);
        updated.Equipment.Select(e => (e.Name, e.IsOptional, e.Order)).Should().Equal(
            ("Updated net", false, 0),
            ("Updated cones", true, 1));
        updated.Variations.Should().ContainSingle()
            .Which.DrillId.Should().Be(newTarget.Id);

        // Assert - replacement was durable and stale children were removed
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var persisted = await db.Drills
            .AsNoTracking()
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Variations.OrderBy(v => v.Order))
            .SingleAsync(d => d.Id == source.Id);

        persisted.Should().BeEquivalentTo(new
        {
            request.Name,
            request.Description,
            request.Category,
            request.Intensity,
            request.Visibility,
            request.Duration,
            request.MinPlayers,
            request.MaxPlayers,
            request.VideoUrl,
            CreatedByUserId = CreatorId,
            LikeCount = 7
        });
        persisted.Skills.Should().Equal(request.Skills);
        persisted.Instructions.Should().Equal(request.Instructions);
        persisted.CoachingPoints.Should().Equal(request.CoachingPoints);
        persisted.UpdatedAt.Should().NotBeNull();
        persisted.Equipment.Select(e => (e.Name, e.IsOptional, e.Order)).Should().Equal(
            ("Updated net", false, 0),
            ("Updated cones", true, 1));
        persisted.Variations.Select(v => (v.TargetDrillId, v.Note, v.Order)).Should().Equal(
            (newTarget.Id, "Replacement variation", 0));
        (await db.DrillEquipment.IgnoreQueryFilters().AnyAsync(e => e.Id == oldEquipment.Id)).Should().BeFalse();
        (await db.DrillVariations.IgnoreQueryFilters().AnyAsync(v => v.Id == oldVariation.Id)).Should().BeFalse();
    }

    [Test]
    public async Task Update_WithDifferentRouteAndBodyIds_ReturnsBadRequestWithoutChangingDrill()
    {
        // Arrange
        var source = NewDrill("Original drill", CreatorId);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = CompleteUpdateRequest(Guid.NewGuid());

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/drills/{source.Id}", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FindDrillAsync(source.Id))!.Name.Should().Be("Original drill");
    }

    [Test]
    public async Task Update_AsDifferentUser_ReturnsForbiddenWithoutChangingDrill()
    {
        // Arrange — public, so the reader sees it and only the creator rule is left to refuse them.
        var source = NewDrill("Original drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/drills/{source.Id}", CompleteUpdateRequest(source.Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindDrillAsync(source.Id))!.Name.Should().Be("Original drill");
    }

    [Test]
    public async Task Create_WithDials_PersistsThemWithTheDrill()
    {
        // Arrange
        await SeedAsync([CreatorProfile()]);
        SetAuth(CreatorId);
        var request = CompleteCreateRequest() with
        {
            InstructionsHtml = "<ol><li><p>Serve {balls} balls</p></li></ol>",
            Instructions = [],
            Dials = [new DrillDialInputDto(null, "balls", DialKind.Number, "5")]
        };

        // Act
        var response = await _client.PostAsJsonAsync("/v1/drills", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        created!.Dials.Select(d => (d.Name, d.Kind, d.DefaultValue)).Should().Equal(("balls", DialKind.Number, "5"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var dials = await db.DrillDials.AsNoTracking().Where(d => d.DrillId == created.Id).ToListAsync();
        dials.Should().ContainSingle(d => d.Name == "balls" && d.DefaultValue == "5");
    }

    [Test]
    public async Task Update_ReplacingADialWithASameNamedNewOne_SavesInOneUnitOfWork()
    {
        // Arrange — the editor's delete-row-then-retype-the-token flow: the doomed dial and its
        // replacement share a name under a unique index, in one save.
        var drill = NewDrill("Dialed drill", CreatorId);
        await SeedAsync([CreatorProfile(), drill]);
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CoachingDbContext>();
            seedDb.DrillDials.Add(new DrillDial { DrillId = drill.Id, Name = "balls", Kind = DialKind.Number, DefaultValue = "3" });
            await seedDb.SaveChangesAsync();
        }
        SetAuth(CreatorId);
        var request = CompleteUpdateRequest(drill.Id) with
        {
            Dials = [new DrillDialInputDto(null, "balls", DialKind.Number, "7")]
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/drills/{drill.Id}", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var dials = await db.DrillDials.AsNoTracking().Where(d => d.DrillId == drill.Id).ToListAsync();
        dials.Should().ContainSingle(d => d.Name == "balls" && d.DefaultValue == "7");
    }

    [Test]
    public async Task Update_WithoutDials_LeavesTheDialSetAlone()
    {
        // Arrange — a client that has never heard of dials must not strip them by omission.
        var drill = NewDrill("Dialed drill", CreatorId);
        await SeedAsync([CreatorProfile(), drill]);
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CoachingDbContext>();
            seedDb.DrillDials.Add(new DrillDial { DrillId = drill.Id, Name = "balls", Kind = DialKind.Number, DefaultValue = "3" });
            await seedDb.SaveChangesAsync();
        }
        SetAuth(CreatorId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/drills/{drill.Id}", CompleteUpdateRequest(drill.Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var dials = await db.DrillDials.AsNoTracking().Where(d => d.DrillId == drill.Id).ToListAsync();
        dials.Should().ContainSingle(d => d.Name == "balls" && d.DefaultValue == "3");
    }

    [Test]
    public async Task Update_WithBlankName_ReturnsBadRequestWithoutChangingDrill()
    {
        // Arrange
        var source = NewDrill("Original drill", CreatorId);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = CompleteUpdateRequest(source.Id) with { Name = "" };

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/drills/{source.Id}", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FindDrillAsync(source.Id))!.Name.Should().Be("Original drill");
    }

    [Test]
    public async Task Update_ClubDrill_RequiresCoachRoleAndUpdatesWhenAuthorized()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var source = NewDrill("Original drill", CreatorId, clubId: clubId);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = CompleteUpdateRequest(source.Id, clubId);
        _factory.ClubsGrpcClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(false);

        // Act / Assert - being the creator is not enough after losing the coaching role
        var forbidden = await _client.PutAsJsonAsync($"/v1/drills/{source.Id}", request, JsonOptions);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FindDrillAsync(source.Id))!.Name.Should().Be("Original drill");

        // Act / Assert - an authorized creator can update the club drill
        _factory.ClubsGrpcClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(true);
        var updated = await _client.PutAsJsonAsync($"/v1/drills/{source.Id}", request, JsonOptions);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var persisted = await FindDrillAsync(source.Id);
        persisted!.Name.Should().Be(request.Name);
        persisted.ClubId.Should().Be(clubId);
    }

    [Test]
    public async Task Delete_AsCreator_RemovesDrillAndReturnsNoContent()
    {
        // Arrange
        var source = NewDrill("Delete me", CreatorId);
        source.Equipment.Add(new DrillEquipment { DrillId = source.Id, Name = "Ball", Order = 0 });
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);

        // Act
        var response = await _client.DeleteAsync($"/v1/drills/{source.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindDrillAsync(source.Id)).Should().BeNull();

        var getResponse = await _client.GetAsync($"/v1/drills/{source.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetById_PublicDrill_OpensForAnySignedInReader()
    {
        // Arrange
        var source = NewDrill("Public drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/drills/{source.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var drill = await response.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        drill!.Id.Should().Be(source.Id);
        drill.Name.Should().Be("Public drill");
    }

    [Test]
    public async Task GetById_PrivatePersonalDrill_OnlyAllowsCreator()
    {
        // Arrange
        var source = NewDrill("Private drill", CreatorId, DrillVisibility.Private);
        await SeedAsync([CreatorProfile(), source]);

        // Act / Assert - anonymous
        var anonymousResponse = await _client.GetAsync($"/v1/drills/{source.Id}");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Act / Assert - unrelated user
        SetAuth(OtherUserId);
        var unrelatedResponse = await _client.GetAsync($"/v1/drills/{source.Id}");
        unrelatedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act / Assert - creator
        SetAuth(CreatorId);
        var creatorResponse = await _client.GetAsync($"/v1/drills/{source.Id}");
        creatorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCaseSource(nameof(RoutesNamingADrill))]
    public async Task StrangerToAPrivateDrill_IsAnsweredAsIfItWereNeverThere(DrillRoute route)
    {
        // Arrange — whatever the route goes on to do, nothing about a drill one may not read is
        // an answer of its own (SPI-6437).
        var drill = NewDrill("Private drill", CreatorId, DrillVisibility.Private);
        await SeedAsync([CreatorProfile(), drill]);
        var missingId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        SetAuth(OtherUserId);

        // Act
        var refused = await SendAsync(route, drill.Id, subId);
        var neverThere = await SendAsync(route, missingId, subId);

        // Assert
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).Replace(drill.Id.ToString(), "{drill}")
            .Should().Be((await neverThere.Content.ReadAsStringAsync()).Replace(missingId.ToString(), "{drill}"));
    }

    [Test]
    public async Task GetComments_OnAPrivateClubDrill_OpenForItsClubsMembers()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var drill = NewDrill("Club drill", CreatorId, clubId: clubId);
        await SeedAsync([CreatorProfile(), drill]);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(OtherUserId, clubId).Returns(true);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/drills/{drill.Id}/comments");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetById_PrivateClubDrill_AllowsClubMembersOnly()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var source = NewDrill("Club drill", CreatorId, clubId: clubId);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(OtherUserId);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(OtherUserId, clubId).Returns(false);

        // Act / Assert - an authenticated non-member cannot read it, or tell it is there
        var refused = await _client.GetAsync($"/v1/drills/{source.Id}");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act / Assert - any active club member can read it, regardless of club role
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(OtherUserId, clubId).Returns(true);
        var allowed = await _client.GetAsync($"/v1/drills/{source.Id}");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        var drill = await allowed.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        drill!.Id.Should().Be(source.Id);
    }

    [Test]
    public async Task GetDrills_AStrangersListingReturnsPublicDrillsOnly()
    {
        // Arrange
        var publicDrill = NewDrill("Public drill", CreatorId, DrillVisibility.Public);
        var privateDrill = NewDrill("Private drill", CreatorId, DrillVisibility.Private);
        await SeedAsync([CreatorProfile(), publicDrill, privateDrill]);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync("/v1/drills");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResponse<DrillDto>>(JsonOptions);
        page!.Items.Should().ContainSingle().Which.Id.Should().Be(publicDrill.Id);
        page.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task GetDrills_ListingCarriesEachDrillsDials()
    {
        // Arrange — the library list feeds the plan builder's picker, whose preview and row
        // editor both read the drill's dials; a list row without them shows raw {tokens}.
        var source = NewDrill("Dialed drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<CoachingDbContext>();
            seedDb.DrillDials.Add(new DrillDial { DrillId = source.Id, Name = "balls", Kind = DialKind.Number, DefaultValue = "5" });
            await seedDb.SaveChangesAsync();
        }
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync("/v1/drills");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResponse<DrillDto>>(JsonOptions);
        page!.Items.Should().ContainSingle()
            .Which.Dials.Select(d => (d.Name, d.DefaultValue)).Should().Equal(("balls", "5"));
    }

    [Test]
    public async Task LikeAndUnlike_AreIdempotentAndKeepDenormalizedCountInSync()
    {
        // Arrange
        var source = NewDrill("Likeable drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);

        // Act
        var firstLike = await _client.PostAsync($"/v1/drills/{source.Id}/like", null);
        var secondLike = await _client.PostAsync($"/v1/drills/{source.Id}/like", null);
        var firstUnlike = await _client.DeleteAsync($"/v1/drills/{source.Id}/like");
        var secondUnlike = await _client.DeleteAsync($"/v1/drills/{source.Id}/like");

        // Assert
        (await firstLike.Content.ReadFromJsonAsync<DrillLikeStatusDto>(JsonOptions))!
            .Should().BeEquivalentTo(new { IsLiked = true, LikeCount = 1 });
        (await secondLike.Content.ReadFromJsonAsync<DrillLikeStatusDto>(JsonOptions))!
            .Should().BeEquivalentTo(new { IsLiked = true, LikeCount = 1 });
        (await firstUnlike.Content.ReadFromJsonAsync<DrillLikeStatusDto>(JsonOptions))!
            .Should().BeEquivalentTo(new { IsLiked = false, LikeCount = 0 });
        (await secondUnlike.Content.ReadFromJsonAsync<DrillLikeStatusDto>(JsonOptions))!
            .Should().BeEquivalentTo(new { IsLiked = false, LikeCount = 0 });

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        (await db.DrillLikes.CountAsync(l => l.DrillId == source.Id)).Should().Be(0);
        (await db.Drills.SingleAsync(d => d.Id == source.Id)).LikeCount.Should().Be(0);
    }

    [Test]
    public async Task BookmarkAndUnbookmark_AreIdempotentAndChangeSavedDrills()
    {
        // Arrange
        var source = NewDrill("Saveable drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);

        // Act / Assert - adding twice creates one bookmark
        var first = await _client.PostAsync($"/v1/drills/{source.Id}/bookmark", null);
        var second = await _client.PostAsync($"/v1/drills/{source.Id}/bookmark", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var savedResponse = await _client.GetAsync("/v1/me/drills/bookmarks");
        var saved = await savedResponse.Content.ReadFromJsonAsync<List<BookmarkedDrillDto>>(JsonOptions);
        saved.Should().ContainSingle().Which.Id.Should().Be(source.Id);

        // Act / Assert - removing twice is harmless
        var firstDelete = await _client.DeleteAsync($"/v1/drills/{source.Id}/bookmark");
        var secondDelete = await _client.DeleteAsync($"/v1/drills/{source.Id}/bookmark");
        (await firstDelete.Content.ReadFromJsonAsync<DrillBookmarkStatusDto>(JsonOptions))!
            .IsBookmarked.Should().BeFalse();
        (await secondDelete.Content.ReadFromJsonAsync<DrillBookmarkStatusDto>(JsonOptions))!
            .IsBookmarked.Should().BeFalse();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        (await db.DrillBookmarks.CountAsync(b => b.DrillId == source.Id)).Should().Be(0);
    }

    [Test]
    public async Task Comments_CreateReadAndDelete_RoundTrip()
    {
        // Arrange
        var source = NewDrill("Discussable drill", CreatorId, DrillVisibility.Public);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);

        // Act - create
        var createResponse = await _client.PostAsJsonAsync(
            $"/v1/drills/{source.Id}/comments",
            new CreateDrillCommentDto("Keep the platform quiet"),
            JsonOptions);

        // Assert - create and read
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<DrillCommentDto>(JsonOptions);
        created!.Content.Should().Be("Keep the platform quiet");
        created.UserId.Should().Be(CreatorId);

        var listResponse = await _client.GetAsync($"/v1/drills/{source.Id}/comments");
        var comments = await listResponse.Content.ReadFromJsonAsync<DrillCommentsResponseDto>(JsonOptions);
        comments!.Items.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        // Act - delete
        var deleteResponse = await _client.DeleteAsync($"/v1/drills/{source.Id}/comments/{created.Id}");

        // Assert - soft-deleted comments no longer appear
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterDeleteResponse = await _client.GetAsync($"/v1/drills/{source.Id}/comments");
        var afterDelete = await afterDeleteResponse.Content.ReadFromJsonAsync<DrillCommentsResponseDto>(JsonOptions);
        afterDelete!.Items.Should().BeEmpty();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.DrillComments.IgnoreQueryFilters().SingleAsync(c => c.Id == created.Id);
        stored.IsDeleted.Should().BeTrue();
    }

    [Test]
    public async Task AddAttachment_AsCreator_AppendsAttachmentAndPersistsIt()
    {
        // Arrange
        var source = NewDrill("Illustrated drill", CreatorId);
        source.Attachments.Add(new DrillAttachment
        {
            DrillId = source.Id,
            FileName = "first.jpg",
            FileUrl = "https://cdn.test/first.jpg",
            FileType = DrillAttachmentType.Image,
            FileSize = 100,
            Order = 0
        });
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = new CreateDrillAttachmentDto(
            "second.mp4",
            "https://cdn.test/second.mp4",
            DrillAttachmentType.Video,
            2048);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/drills/{source.Id}/attachments", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the API returned: {0}",
            await response.Content.ReadAsStringAsync());
        var attachment = await response.Content.ReadFromJsonAsync<DrillAttachmentDto>(JsonOptions);
        attachment.Should().BeEquivalentTo(new
        {
            DrillId = source.Id,
            request.FileName,
            request.FileUrl,
            request.FileType,
            request.FileSize,
            Order = 1
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.DrillAttachments.SingleAsync(a => a.Id == attachment!.Id);
        stored.Order.Should().Be(1);
    }

    [Test]
    public async Task AddAttachment_WithAJavascriptFileUrl_ReturnsBadRequestNamingItAndAddsNothing()
    {
        // Arrange
        var source = NewDrill("Linked drill", CreatorId);
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = new CreateDrillAttachmentDto("Tap me", "javascript:alert(1)", DrillAttachmentType.Document, 0);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/drills/{source.Id}/attachments", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldsNamedBy(response)).Should().Equal("fileUrl");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        (await db.DrillAttachments.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task UpdateAnimations_AsCreator_ReplacesStoredAnimationDocument()
    {
        // Arrange
        var source = NewDrill("Animated drill", CreatorId);
        source.Animations = "[{\"name\":\"Old\",\"keyframes\":[],\"speed\":100}]";
        await SeedAsync([CreatorProfile(), source]);
        SetAuth(CreatorId);
        var request = new UpdateDrillAnimationsDto(
        [
            new DrillAnimationDto
            {
                Name = "Serve path",
                Speed = 750,
                Keyframes =
                [
                    new AnimationKeyframeDto
                    {
                        Id = "frame-1",
                        Ball = new BallPositionDto { X = 0.25, Y = 0.75 },
                        Players =
                        [
                            new PlayerPositionDto
                            {
                                Id = "player-1",
                                X = 0.1,
                                Y = 0.2,
                                Color = "blue",
                                Label = "S"
                            }
                        ]
                    }
                ]
            }
        ]);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/drills/{source.Id}/animations", request, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<DrillDto>(JsonOptions);
        updated!.Animations.Should().ContainSingle();
        updated.Animations[0].Name.Should().Be("Serve path");
        updated.Animations[0].Speed.Should().Be(750);
        updated.Animations[0].Keyframes.Should().ContainSingle();
        updated.Animations[0].Keyframes[0].Players.Should().ContainSingle();

        var persisted = await FindDrillAsync(source.Id);
        persisted!.Animations.Should().NotBeNull();
        using var json = JsonDocument.Parse(persisted.Animations!);
        json.RootElement[0].GetProperty("name").GetString().Should().Be("Serve path");
        json.RootElement[0].GetProperty("speed").GetInt32().Should().Be(750);
    }

    private Task<HttpResponseMessage> SendAsync(DrillRoute route, Guid drillId, Guid subId)
    {
        var path = route.Template.Replace("{drill}", drillId.ToString()).Replace("{sub}", subId.ToString());
        var request = new HttpRequestMessage(new HttpMethod(route.Method), $"/v1/{path}");
        if (route.Method is "POST" or "PUT" or "PATCH")
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        return _client.SendAsync(request);
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountDrillsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CoachingDbContext>().Drills.CountAsync();
    }

    /// <summary>The fields a validation refusal names, read off the shared problem-details body.</summary>
    private static async Task<List<string>> FieldsNamedBy(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("field").GetString()!)
            .ToList();
    }

    private async Task<Drill?> FindDrillAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CoachingDbContext>()
            .Drills.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
    }

    private static UserProfile CreatorProfile() => new()
    {
        Id = CreatorId,
        Name = "Casey",
        Surname = "Coach",
        Email = "casey.coach@test.local",
        IsActive = true
    };

    private static Drill NewDrill(
        string name,
        Guid creatorId,
        DrillVisibility visibility = DrillVisibility.Private,
        Guid? clubId = null) => new()
    {
        Name = name,
        CreatedByUserId = creatorId,
        Visibility = visibility,
        ClubId = clubId,
        Skills = [],
        Instructions = [],
        CoachingPoints = []
    };

    private static CreateDrillDto CompleteCreateRequest(
        Guid? clubId = null,
        CreateDrillVariationInput[]? variations = null) => new(
        Name: "Serve receive under pressure",
        Description: "Rehearse three-player reception patterns.",
        Category: DrillCategory.Technical,
        Intensity: DrillIntensity.High,
        Visibility: DrillVisibility.Private,
        Skills: [DrillSkill.Serving, DrillSkill.Passing],
        Duration: 18,
        MinPlayers: 4,
        MaxPlayers: 12,
        Instructions: ["Form two groups", "Serve to zones"],
        CoachingPoints: ["Hold the platform", "Call early"],
        Variations: variations ?? [],
        Equipment:
        [
            new DrillEquipmentInput("Volleyballs"),
            new DrillEquipmentInput("Targets", IsOptional: true)
        ],
        VideoUrl: "https://video.test/serve-receive",
        ClubId: clubId);

    private static UpdateDrillDto CompleteUpdateRequest(
        Guid id,
        Guid? clubId = null,
        CreateDrillVariationInput[]? variations = null) => new(
        Id: id,
        Name: "Updated transition drill",
        Description: "Updated description",
        Category: DrillCategory.Tactical,
        Intensity: DrillIntensity.Medium,
        Visibility: DrillVisibility.Public,
        Skills: [DrillSkill.Defense, DrillSkill.Attacking],
        Duration: 25,
        MinPlayers: 6,
        MaxPlayers: 14,
        Instructions: ["Updated first step", "Updated second step"],
        CoachingPoints: ["Read the setter"],
        Variations: variations ?? [],
        Equipment:
        [
            new DrillEquipmentInput("Updated net"),
            new DrillEquipmentInput("Updated cones", IsOptional: true)
        ],
        VideoUrl: "https://video.test/updated",
        ClubId: clubId);

    private void SetAuth(Guid userId)
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwt(userId));
    }

    private static string GenerateJwt(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "test@example.com"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
