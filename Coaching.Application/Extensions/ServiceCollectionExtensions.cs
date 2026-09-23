using Coaching.Application.Interfaces.Services;
using Coaching.Application.Mappings;
using Coaching.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Coaching.Application.Extensions;

public static class ServiceCollectionExtensions
{
    // Scanned by assembly rather than listing profiles: only the assembly overload also registers
    // IValueResolver implementations (SignedFeedbackMediaUrlResolver & co) in the container. Without
    // them AutoMapper falls back to Activator.CreateInstance and every map that uses one throws.
    public static IServiceCollection AddApplicationMappings(this IServiceCollection services)
    {
        services.AddAutoMapper(typeof(FeedbackMappingProfile).Assembly);
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<Shared.Services.IActionRiskClassifier, DefaultActionRiskClassifier>();
        services.AddScoped<Shared.Services.IGuardianCacheService, ProfilesGuardianCacheService>();
        services.AddScoped<Shared.Services.IGuardianAuthorizer, Shared.Services.GuardianAuthorizer>();
        services.AddScoped<IDrillService, DrillService>();
        services.AddScoped<IDrillDialService, DrillDialService>();
        services.AddScoped<IDrillDialReconciler, DrillDialReconciler>();
        services.AddScoped<ITrainingPlanService, TrainingPlanService>();
        services.AddScoped<IPlanCoachService, PlanCoachService>();
        services.AddScoped<IRunService, RunService>();
        services.AddScoped<IPlanFloorService, PlanFloorService>();
        services.AddScoped<ITacticsBoardService, TacticsBoardService>();

        // Evaluation services
        services.AddScoped<IEvaluationAccess, EvaluationAccess>();
        services.AddScoped<IEvaluationPeople, EvaluationPeople>();
        services.AddScoped<IEvaluationExerciseService, EvaluationExerciseService>();
        services.AddScoped<IEvaluationPlanService, EvaluationPlanService>();
        services.AddScoped<IEvaluationSessionService, EvaluationSessionService>();
        services.AddScoped<IEvaluationSessionLifecycleService, EvaluationSessionLifecycleService>();
        services.AddScoped<IEvaluationGroupService, EvaluationGroupService>();
        services.AddScoped<IEvaluationScoringService, EvaluationScoringService>();
        services.AddScoped<IPlayerEvaluationService, PlayerEvaluationService>();
        services.AddScoped<IScoreCalculationService, ScoreCalculationService>();
        services.AddScoped<IThresholdService, ThresholdService>();
        services.AddScoped<IExportService, ExportService>();

        // Feedback services
        services.AddScoped<IFeedbackService, FeedbackService>();
        services.AddScoped<IFeedbackAuthorizationService, FeedbackAuthorizationService>();
        services.AddScoped<IBadgeService, BadgeService>();
        services.AddScoped<IFeedbackMediaUrlSigner, FeedbackMediaUrlSigner>();
        return services;
    }
}
