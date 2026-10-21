using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Coaching.Tests.Integration.Migrations;

[TestFixture]
[Category("Integration")]
[NonParallelizable] // runs a migration's operations against the fixture's database
public class EraseDeletedAccountProfilesMigrationTests
{
    private CoachingApiFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [TearDown]
    public async Task TearDown() => await _factory.DatabaseResetter.ResetAsync();

    [Test]
    public async Task Up_ADeletedAccountsCopy_KeepsNothingThatIdentifiesThePerson()
    {
        // Arrange — the copy as the old deletion handler left it: renamed and its email blanked,
        // everything else kept
        var deletedId = await SeedProfileAsync("Deleted User", "", email: "", isActive: false);

        // Act
        await MigrateUpAsync();

        // Assert
        (await ReadAsync(deletedId)).Should().Be((null, null, null, null, null, false));
    }

    [Test]
    public async Task Up_AnyoneElse_IsLeftAsTheyAre()
    {
        // Arrange — inactive, as an account that has not verified its email is
        var liveId = await SeedProfileAsync("Lucia", "Moreno", email: "lucia@example.com", isActive: false);

        // Act
        await MigrateUpAsync();

        // Assert
        (await ReadAsync(liveId)).Should().Be(
            ("lucia@example.com", "+447700900123", new DateTime(2008, 3, 1), "https://cdn.example/lucia.png", "lucia-thumb", true));
    }

    private async Task MigrateUpAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var assembly = db.GetService<IMigrationsAssembly>();
        var id = assembly.Migrations.Keys.Single(k => k.EndsWith(nameof(Coaching.Infrastructure.Migrations.EraseDeletedAccountProfiles)));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.GetService<IDatabaseProvider>().Name);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model);

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        foreach (var command in commands)
        {
            await using var sql = new NpgsqlCommand(command.CommandText, connection);
            await sql.ExecuteNonQueryAsync();
        }
    }

    private async Task<Guid> SeedProfileAsync(string name, string surname, string email, bool isActive)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "UserProfile" ("Id", "Name", "Surname", "Email", "PhoneNumber", "DateOfBirth",
                "ImageUrl", "ImageThumbHash", "IsActive", "IsEmailVerified", "IsDeleted")
            VALUES (@id, @name, @surname, @email, '+447700900123', '2008-03-01',
                'https://cdn.example/lucia.png', 'lucia-thumb', @isActive, true, false);
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("surname", surname);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("isActive", isActive);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(string?, string?, DateTime?, string?, string?, bool)> ReadAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Email", "PhoneNumber", "DateOfBirth", "ImageUrl", "ImageThumbHash", "IsEmailVerified"
            FROM "UserProfile" WHERE "Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5));
    }
}
