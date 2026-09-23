using Asp.Versioning;
using Coaching.Application.Extensions;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Infrastructure.Repositories;
using Coaching.Infrastructure.Services;
using Shared.Contracts.Grpc;
using Shared.DataAccess.Extensions;
using Shared.DataAccess.Repositories;
using Shared.DataAccess.Repositories.Interfaces;
using Coaching.Application.Consumers;
using Shared.Messaging.Consumers;
using Shared.Messaging.Definitions;
using Shared.Messaging.Extensions;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.Extensions;
using Shared.Middleware;
using Shared.Extensions;
using Shared.Microservices.Extensions;
using Coaching.Authorization;
using Shared.Security.Access;
using Shared.Security.Authentication;
using Shared.Security.Authorization;
using Shared.Security.Endpoints;
using Shared.Security.Output;
using OpenTelemetry.Trace;

namespace Coaching
{
    public class Startup
    {
        public IConfiguration Configuration { get; }
        public IWebHostEnvironment Environment { get; }

        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            Environment = environment;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers()
                .AddNewtonsoftJson(options =>
                {
                    options.SerializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
                    options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
                    options.SerializerSettings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
                    // Replace collections from the request body instead of appending to
                    // pre-populated defaults on the DTO (Newtonsoft's Auto mode appends).
                    options.SerializerSettings.ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace;
                    // No entity goes out, or comes in as a body: it carries whatever EF had loaded.
                    options.SerializerSettings.ContractResolver =
                        new EntityGuardContractResolver(options.SerializerSettings.ContractResolver);
                });

            services.ConfigureProblemDetailsValidation();

            // API Versioning
            services.AddApiVersioning(opt =>
            {
                opt.DefaultApiVersion = new ApiVersion(1, 0);
                opt.AssumeDefaultVersionWhenUnspecified = true;
                opt.ApiVersionReader = ApiVersionReader.Combine(
                    new UrlSegmentApiVersionReader(),
                    new QueryStringApiVersionReader("version"),
                    new HeaderApiVersionReader("X-Version")
                );
            });
            services.AddApiVersioning();
            services.AddGrpc();
            services.AddGrpcHealthChecks();

            services.AddSingleton(TimeProvider.System);

            // Database
            var connectionString = Configuration.GetConnectionString("DefaultConnection");
            services.AddDbContext<CoachingDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Bind DbContext for BaseRepository
            services.AddScoped<DbContext>(provider => provider.GetRequiredService<CoachingDbContext>());

            // Repositories
            services.AddScoped(typeof(IRepository<>), typeof(BaseRepository<>));
            services.AddScoped<IDrillRepository, DrillRepository>();
            services.AddScoped<IDrillLikeRepository, DrillLikeRepository>();
            services.AddScoped<IDrillBookmarkRepository, DrillBookmarkRepository>();
            services.AddScoped<IDrillCommentRepository, DrillCommentRepository>();
            services.AddScoped<IDrillAttachmentRepository, DrillAttachmentRepository>();

            // Plan repositories
            services.AddScoped<ITrainingPlanRepository, TrainingPlanRepository>();
            services.AddScoped<IPlanSectionRepository, PlanSectionRepository>();
            services.AddScoped<IPlanItemRepository, PlanItemRepository>();
            services.AddScoped<IPlanLikeRepository, PlanLikeRepository>();
            services.AddScoped<IPlanBookmarkRepository, PlanBookmarkRepository>();
            services.AddScoped<IPlanCommentRepository, PlanCommentRepository>();
            services.AddScoped<ITrainingPlanRunRepository, TrainingPlanRunRepository>();
            services.AddScoped<ITrainingPlanRunItemRepository, TrainingPlanRunItemRepository>();
            services.AddScoped<IRunStationRepository, RunStationRepository>();

            // Feedback repositories
            services.AddScoped<IFeedbackRepository, FeedbackRepository>();

            // Evaluation repositories
            services.AddScoped<IEvaluationExerciseRepository, EvaluationExerciseRepository>();
            services.AddScoped<IEvaluationPlanRepository, EvaluationPlanRepository>();
            services.AddScoped<IEvaluationSessionRepository, EvaluationSessionRepository>();
            services.AddScoped<IEvaluationParticipantRepository, EvaluationParticipantRepository>();
            services.AddScoped<IPlayerEvaluationRepository, PlayerEvaluationRepository>();
            services.AddScoped<IEvaluationGroupRepository, EvaluationGroupRepository>();
            services.AddScoped<IPlayerExerciseScoreRepository, PlayerExerciseScoreRepository>();

            // gRPC Clients
            var clubsGrpcAddress = Configuration["GrpcClients:ClubsService"] ?? "http://clubs-service:5021";
            services.AddGrpcClient<ClubsInternalService.ClubsInternalServiceClient>(o =>
            {
                o.Address = new Uri(clubsGrpcAddress);
            });
            services.AddScoped<IClubsGrpcClient, ClubsGrpcClient>();

            var eventsGrpcAddress = Configuration["GrpcClients:EventsService"] ?? "http://events-service:5011";
            services.AddGrpcClient<EventsInternalService.EventsInternalServiceClient>(o =>
            {
                o.Address = new Uri(eventsGrpcAddress);
            });
            services.AddScoped<IEventsGrpcClient, EventsGrpcClient>();

            var profilesGrpcAddress = Configuration["GrpcClients:ProfilesService"] ?? "http://profiles-service:5171";
            services.AddGrpcClient<UserProfileService.UserProfileServiceClient>(o =>
            {
                o.Address = new Uri(profilesGrpcAddress);
            });
            services.AddScoped<IProfilesGuardianGrpcClient, ProfilesGuardianGrpcClient>();
            services.AddScoped<Shared.Services.IGuardianAccessSource, ProfilesGuardianAccessSource>();

            // AutoMapper & Application services
            services.AddApplicationMappings();
            services.AddApplicationServices();
            services.AddSingleton(TimeProvider.System);
            services.AddScoped<Coaching.Application.Interfaces.Services.IRunBroadcaster, Coaching.Hubs.SignalRRunBroadcaster>();

            services.AddSharedDataAccess();

            // S3 Settings
            services.Configure<S3Settings>(Configuration.GetSection("S3"));
            services.AddDefaultAWSOptions(Configuration.GetAWSOptions());
            services.AddSharedServices();
            services.AddPostHogAnalytics(Configuration, "coaching-service");

            // Caching
            services.AddMemoryCache();
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = Configuration.GetValue<string>("Redis:ConnectionString");
            });

            services.AddMessaging<CoachingDbContext>(options =>
            {
                options.Host = Configuration["RabbitMQ:Host"] ?? "localhost";
                options.Port = ushort.Parse(Configuration["RabbitMQ:Port"] ?? "5672");
                options.VirtualHost = Configuration["RabbitMQ:VirtualHost"] ?? "/";
                options.Username = Configuration["RabbitMQ:Username"] ?? "guest";
                options.Password = Configuration["RabbitMQ:Password"] ?? "guest";
                options.ServicePrefix = "coaching";
            },
            bus =>
            {
                // Retried: each converges when it runs again. A replica write, a delete of what is
                // still there, and a deletion whose ack auth-service dedupes.
                bus.AddRetryingConsumer<UserProfileUpdatedConsumer>();
                bus.AddRetryingConsumer<EventDeletedConsumer>();
                bus.AddRetryingConsumer<Coaching.Application.Consumers.UserDeletionConfirmedConsumer>();
            });

            // Deny by default: anything that declares nothing needs a signed-in user (SPI-6446).
            services.AddSpikeAuthentication(Configuration);
            services.AddSpikeAuthorization();
            services.AddScoped<IResourceAuthority<DrillAccess>, DrillAuthority>();
            services.AddScoped<IResourceAuthority<EvaluationSessionAccess>, EvaluationSessionAuthority>();
            services.AddScoped<IResourceAuthority<RunAccess>, RunAuthority>();

            // SignalR
            var signalRBuilder = services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = Environment.IsDevelopment();
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.ReferenceHandler =
                    System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
                options.PayloadSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
                EntityGuard.Guard(options.PayloadSerializerOptions);
            });

            var signalRRedisConnection = Configuration.GetValue<string>("Redis:ConnectionString");
            if (!string.IsNullOrWhiteSpace(signalRRedisConnection))
            {
                signalRBuilder.AddStackExchangeRedis(signalRRedisConnection, options =>
                {
                    options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("coaching");
                });
            }

            // Swagger
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();

            // CORS
            var allowedOrigins = Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "http://localhost:3000" };

            services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    policy.WithOrigins(allowedOrigins)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            // Health checks
            services.AddHealthChecks();
            services.AddPrometheusMetrics(Configuration);
            services.AddTracing(Configuration, "coaching-service", tracing =>
            {
                tracing.AddFilteredEfCoreInstrumentation();
                tracing.AddGrpcClientInstrumentation();
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseCors("AllowFrontend");

            // Prometheus HTTP request metrics
            app.UsePrometheusMetrics();

            if (env.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseRouting();

            app.UseAuthentication();
            app.UseMiddleware<JwtBlacklistMiddleware>();
            app.UseAuthorization();

            app.UseMiddleware<ErrorHandlerMiddleware>();
            // After the error handler: the refusal it throws must be shaped into a 400, not escape as a 500.
            app.UseMiddleware<GuardianContextMiddleware>();

            var internalListenerPort = Configuration.GetInternalListenerPort();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<Coaching.Hubs.EvaluationHub>("/hubs/evaluation");
                endpoints.MapHub<Coaching.Hubs.TrainingRunHub>("/hubs/trainingrun");
                endpoints.MapInternalGrpcService<Grpc.CoachingInternalServiceImpl>(internalListenerPort);
                endpoints.MapHealthChecks("/health")
                    .AllowPublic("Liveness probe for Docker and the gateway; reports no data");
                endpoints.MapGrpcHealthChecksService().RequireInternalListener(internalListenerPort);
            });
        }
    }
}
