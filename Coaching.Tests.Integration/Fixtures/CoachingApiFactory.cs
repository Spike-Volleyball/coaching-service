using Coaching.Application.Interfaces.Services;
using Coaching.Infrastructure.Data.Context;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shared.Services.FileStorage.Intefaces;
using Shared.Services;
using Shared.Testing.Fixtures;

namespace Coaching.Tests.Integration.Fixtures;

public class CoachingApiFactory : WebApplicationFactory<Program>
{
    private readonly PostgresFixture _postgresFixture = new();
    private readonly bool _isolateMessageBroker;
    public DatabaseResetter DatabaseResetter { get; private set; } = null!;
    public string ConnectionString => _postgresFixture.ConnectionString;

    /// <param name="isolateMessageBroker">
    /// When true, the bus and its EF outbox stay real and RabbitMQ is pointed at a closed local
    /// port, so what a request publishes lands in the outbox table and stays there. For tests that
    /// read the outbox: with a broker reachable (docker-compose's, in local dev) the delivery service
    /// takes rows within a second and races the assertions. The bus retries in the background, so
    /// startup and requests are unaffected. By default the publish endpoint is a substitute and the
    /// bus never starts.
    /// </param>
    public CoachingApiFactory(bool isolateMessageBroker = false)
    {
        _isolateMessageBroker = isolateMessageBroker;
    }

    public const string JwtSecret = "314b7dfbe6cf5a56208d194297589c4bb12e07410c88c576044bddc4da82f884";
    public const string JwtIssuer = "AuthService";
    public const string JwtAudience = "AuthService-Users";

    public IEventsGrpcClient EventsGrpcClient { get; private set; } = null!;
    public IClubsGrpcClient ClubsGrpcClient { get; private set; } = null!;
    public IRunBroadcaster RunBroadcaster { get; private set; } = null!;
    public IFileService FileService { get; private set; } = null!;

    /// <summary>
    /// Only consulted on requests carrying an X-Acting-As header, by the shared authorizer behind
    /// [AcceptsSubject]. The relationship answer comes from the cache service; the permission and
    /// consent snapshot from the access source.
    /// </summary>
    public IGuardianCacheService GuardianCacheService { get; private set; } = null!;
    public IGuardianAccessSource GuardianAccessSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgresFixture.InitializeAsync();
        DatabaseResetter = new DatabaseResetter(_postgresFixture.ConnectionString);
        EventsGrpcClient = Substitute.For<IEventsGrpcClient>();
        ClubsGrpcClient = Substitute.For<IClubsGrpcClient>();
        RunBroadcaster = Substitute.For<IRunBroadcaster>();
        FileService = Substitute.For<IFileService>();
        GuardianCacheService = Substitute.For<IGuardianCacheService>();
        GuardianAccessSource = Substitute.For<IGuardianAccessSource>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Settings that come from the environment in a real deployment and so are absent from a
        // test host. The tokens the tests sign must be validated with the same key the host
        // reads, and the S3 options are validated the moment they are resolved — without
        // either, endpoints failed before their own code ran. Mirrors EventsApiFactory.
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["Jwt:ExpiryMinutes"] = "60",
                ["S3:Bucket"] = "test-bucket",
                ["S3:PublicBaseUrl"] = "https://cdn.test",
                ["S3:PresignedUrlExpiryMinutes"] = "15",
            }));

        if (_isolateMessageBroker)
        {
            // Port 1 is privileged and unbound, so the client is refused at once instead of stalling.
            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMQ:Host"] = "localhost",
                    ["RabbitMQ:Port"] = "1",
                }));
        }

        builder.ConfigureServices(services =>
        {
            var dbDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<CoachingDbContext>));
            if (dbDescriptor != null)
                services.Remove(dbDescriptor);

            services.AddDbContext<CoachingDbContext>(options =>
                options.UseNpgsql(_postgresFixture.ConnectionString));

            ReplaceWithSingleton(services, EventsGrpcClient);
            ReplaceWithSingleton(services, ClubsGrpcClient);
            ReplaceWithSingleton(services, RunBroadcaster);
            ReplaceWithSingleton(services, FileService);
            ReplaceWithSingleton(services, GuardianCacheService);
            ReplaceWithSingleton(services, GuardianAccessSource);

            if (!_isolateMessageBroker)
            {
                // Remove MassTransit hosted services to prevent RabbitMQ connection attempts.
                var massTransitHosted = services.Where(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    (d.ImplementationType?.FullName?.Contains("MassTransit") == true ||
                     d.ImplementationFactory?.Method.DeclaringType?.FullName?.Contains("MassTransit") == true))
                    .ToList();
                foreach (var d in massTransitHosted)
                    services.Remove(d);

                var publishEndpoints = services.Where(d => d.ServiceType == typeof(IPublishEndpoint)).ToList();
                foreach (var d in publishEndpoints)
                    services.Remove(d);
                services.AddSingleton(Substitute.For<IPublishEndpoint>());
            }

            var cacheDescriptors = services.Where(d => d.ServiceType == typeof(IDistributedCache)).ToList();
            foreach (var d in cacheDescriptors)
                services.Remove(d);
            services.AddDistributedMemoryCache();

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
            db.Database.Migrate();
        });

        // Development settings provide deterministic JWT configuration and detailed error
        // responses. External infrastructure remains isolated by the replacements above.
        builder.UseEnvironment("Development");
    }

    private static void ReplaceWithSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
        services.AddSingleton(instance);
    }

    public new async Task DisposeAsync()
    {
        await _postgresFixture.DisposeAsync();
        await base.DisposeAsync();
    }
}
