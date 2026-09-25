using System.Linq.Expressions;
using AutoMapper;
using Coaching.Application.Extensions;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Models;
using Shared.Options;
using Shared.Services.FileStorage.Intefaces;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A <see cref="FeedbackService"/> over substitute repositories and the real mapping profile, and a
/// feedback that every read the service makes of it finds.
/// </summary>
public abstract class FeedbackServiceTestBase : UnitTestBase
{
    protected static readonly Guid CoachId = Guid.NewGuid();
    protected static readonly Guid PlayerId = Guid.NewGuid();

    protected IFeedbackRepository _feedbackRepository = null!;
    protected IRepository<Praise> _praiseRepository = null!;
    protected IPublishEndpoint _bus = null!;
    protected FeedbackService _sut = null!;

    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _praiseRepository = Substitute.For<IRepository<Praise>>();
        _bus = Substitute.For<IPublishEndpoint>();

        var userProfiles = Substitute.For<IRepository<UserProfile>>();
        userProfiles.Query().Returns(new List<UserProfile>().BuildMock());
        var attachments = Substitute.For<IRepository<FeedbackMedia>>();
        attachments.Query().Returns(new List<FeedbackMedia>().BuildMock());

        var services = new ServiceCollection();
        services.AddApplicationMappings();
        services.AddScoped(_ => Substitute.For<IFeedbackMediaUrlSigner>());
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        _sut = new FeedbackService(
            _feedbackRepository,
            Substitute.For<IRepository<ImprovementPoint>>(),
            Substitute.For<IRepository<ImprovementPointDrill>>(),
            Substitute.For<IRepository<ImprovementPointMedia>>(),
            attachments,
            _praiseRepository,
            Substitute.For<IRepository<Drill>>(),
            Substitute.For<IFeedbackAuthorizationService>(),
            Substitute.For<IEventsGrpcClient>(),
            userProfiles,
            _scope.ServiceProvider.GetRequiredService<IMapper>(),
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            TimeProvider,
            _bus);
    }

    [TearDown]
    public override void TearDown()
    {
        _scope.Dispose();
        _provider.Dispose();
        base.TearDown();
    }

    /// <summary>Feedback the coach gave the player three days ago, carrying <paramref name="praise"/>.</summary>
    protected Feedback GivenFeedback(bool shared, Praise? praise = null)
    {
        var feedback = new Feedback
        {
            CoachUserId = CoachId,
            RecipientUserId = PlayerId,
            SharedWithPlayer = shared,
            CreatedAt = PastDate(3),
        };
        if (praise != null)
        {
            praise.FeedbackId = feedback.Id;
            feedback.Praise = praise;
        }

        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);
        _feedbackRepository.GetByIdAsync(feedback.Id, Arg.Any<Expression<Func<Feedback, object>>[]>()).Returns(feedback);
        _feedbackRepository.GetByIdWithDetailsAsync(feedback.Id).Returns(feedback);
        return feedback;
    }
}
