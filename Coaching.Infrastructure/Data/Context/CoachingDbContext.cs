using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Evaluation;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Tactics;
using Coaching.Domain.Models.Templates;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess;
using MassTransit;

namespace Coaching.Infrastructure.Data.Context;

public class CoachingDbContext : BaseDbContext
{
    public CoachingDbContext(DbContextOptions<CoachingDbContext> options) : base(options)
    {
    }

    // Drills
    public DbSet<Drill> Drills => Set<Drill>();
    public DbSet<DrillAttachment> DrillAttachments => Set<DrillAttachment>();
    public DbSet<DrillEquipment> DrillEquipment => Set<DrillEquipment>();
    public DbSet<DrillDial> DrillDials => Set<DrillDial>();
    public DbSet<DrillVariation> DrillVariations => Set<DrillVariation>();
    public DbSet<DrillLike> DrillLikes => Set<DrillLike>();
    public DbSet<DrillBookmark> DrillBookmarks => Set<DrillBookmark>();
    public DbSet<DrillComment> DrillComments => Set<DrillComment>();

    // Training Plans
    public DbSet<TrainingPlan> TrainingPlans => Set<TrainingPlan>();
    public DbSet<PlanSection> PlanSections => Set<PlanSection>();
    public DbSet<PlanItem> PlanItems => Set<PlanItem>();
    public DbSet<PlanStation> PlanStations => Set<PlanStation>();
    public DbSet<PlanStationItem> PlanStationItems => Set<PlanStationItem>();
    public DbSet<PlanCoach> PlanCoaches => Set<PlanCoach>();
    public DbSet<PlanStationCoach> PlanStationCoaches => Set<PlanStationCoach>();
    public DbSet<PlanItemDialValue> PlanItemDialValues => Set<PlanItemDialValue>();
    public DbSet<PlanLike> PlanLikes => Set<PlanLike>();
    public DbSet<PlanBookmark> PlanBookmarks => Set<PlanBookmark>();
    public DbSet<PlanComment> PlanComments => Set<PlanComment>();
    public DbSet<TrainingPlanRun> TrainingPlanRuns => Set<TrainingPlanRun>();
    public DbSet<TrainingPlanRunItem> TrainingPlanRunItems => Set<TrainingPlanRunItem>();
    public DbSet<RunStation> RunStations => Set<RunStation>();
    public DbSet<RunStationItem> RunStationItems => Set<RunStationItem>();
    public DbSet<PlanCourtBooking> PlanCourtBookings => Set<PlanCourtBooking>();
    public DbSet<PlanItemPlacement> PlanItemPlacements => Set<PlanItemPlacement>();

    // Evaluation
    public DbSet<EvaluationExercise> EvaluationExercises => Set<EvaluationExercise>();
    public DbSet<EvaluationMetric> EvaluationMetrics => Set<EvaluationMetric>();
    public DbSet<MetricSkillWeight> MetricSkillWeights => Set<MetricSkillWeight>();
    public DbSet<EvaluationThreshold> EvaluationThresholds => Set<EvaluationThreshold>();
    public DbSet<EvaluationPlan> EvaluationPlans => Set<EvaluationPlan>();
    public DbSet<EvaluationPlanItem> EvaluationPlanItems => Set<EvaluationPlanItem>();
    public DbSet<EvaluationSession> EvaluationSessions => Set<EvaluationSession>();
    public DbSet<EvaluationParticipant> EvaluationParticipants => Set<EvaluationParticipant>();
    public DbSet<PlayerEvaluation> PlayerEvaluations => Set<PlayerEvaluation>();
    public DbSet<PlayerMetricScore> PlayerMetricScores => Set<PlayerMetricScore>();
    public DbSet<PlayerSkillScore> PlayerSkillScores => Set<PlayerSkillScore>();
    public DbSet<EvaluationGroup> EvaluationGroups => Set<EvaluationGroup>();
    public DbSet<EvaluationGroupPlayer> EvaluationGroupPlayers => Set<EvaluationGroupPlayer>();
    public DbSet<PlayerExerciseScore> PlayerExerciseScores => Set<PlayerExerciseScore>();

    // Feedback
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<ImprovementPoint> ImprovementPoints => Set<ImprovementPoint>();
    public DbSet<ImprovementPointDrill> ImprovementPointDrills => Set<ImprovementPointDrill>();
    public DbSet<ImprovementPointMedia> ImprovementPointMedia => Set<ImprovementPointMedia>();
    public DbSet<FeedbackMedia> FeedbackMedia => Set<FeedbackMedia>();
    public DbSet<Praise> Praises => Set<Praise>();
    public DbSet<PlayerBadge> PlayerBadges => Set<PlayerBadge>();

    // Tactics
    public DbSet<TacticsBoard> TacticsBoards => Set<TacticsBoard>();
    public DbSet<TacticsFolder> TacticsFolders => Set<TacticsFolder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoachingDbContext).Assembly);

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
