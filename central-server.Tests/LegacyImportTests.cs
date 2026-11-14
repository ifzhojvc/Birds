using System.Security.Cryptography;
using System.Text.Json;
using BirdLab.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirdLab.Api.Tests;

public sealed class LegacyImportTests
{
    private static async Task<string> WriteSourceAsync(string directory, string path = "uploads/bird.jpg", bool wrongHash = false, bool orphan = false)
    {
        var media = Path.Combine(directory, "uploads");
        Directory.CreateDirectory(media);
        byte[] bytes = [0xff, 0xd8, 1, 2, 0xff, 0xd9];
        await File.WriteAllBytesAsync(Path.Combine(media, "bird.jpg"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, "bird_analysis.csv"), "preserved,legacy,csv\n");
        var source = Path.Combine(directory, "birds.db");
        await using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString());
        await sqlite.OpenAsync();
        await using var command = sqlite.CreateCommand();
        command.CommandText = """
            CREATE TABLE bird_info (payload TEXT);
            CREATE TABLE analyses (
                id INTEGER PRIMARY KEY, analysis_type TEXT, original_name TEXT, file_path TEXT, file_hash TEXT,
                predicted_species TEXT, scientific_name TEXT, confidence REAL, model_id TEXT, model_revision TEXT,
                analysis_date TEXT, description TEXT, elapsed_seconds REAL, metadata TEXT, bird_snapshot TEXT);
            CREATE TABLE predictions (analysis_id INTEGER, label TEXT, scientific_name TEXT, score REAL, rank INTEGER);
            CREATE TABLE evaluations (analysis_id INTEGER, expected_label TEXT, expected_scientific TEXT, label_source TEXT,
                file_hash TEXT, top1_correct INTEGER, top3_correct INTEGER, experiment TEXT);
            INSERT INTO bird_info VALUES ('{"scientific_name":"Cardinalis cardinalis","english_name":"Northern Cardinal","sources":["https://example.org/birds","file:///tmp/local"]}'), ('{"english_name":"unidentified"}');
            INSERT INTO analyses VALUES (91,'image','bird.jpg',$path,$hash,'Cardinal','Cardinalis cardinalis',0.8,
                'old-model','pinned-revision','2026-10-28T00:00:00Z','Legacy description',0.5,'{}','{}');
            INSERT INTO predictions VALUES (91,'Cardinal','Cardinalis cardinalis',0.8,1),(91,'Second',NULL,0.15,2),(91,'Third',NULL,0.05,3);
            INSERT INTO evaluations VALUES (91,'Cardinal','Cardinalis cardinalis','fixture',$hash,1,1,'original');
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", wrongHash ? "incorrect" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        await command.ExecuteNonQueryAsync();
        if (orphan)
        {
            command.CommandText = "INSERT INTO predictions VALUES (100,'orphan',NULL,0.2,1)";
            await command.ExecuteNonQueryAsync();
        }
        return source;
    }

    private static LegacyImporter Importer(SqlDatabase database, TestFiles output, string catalog)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CatalogPath"] = catalog }).Build();
        return new(database.Db, output.Store, new CatalogSeeder(database.Db, configuration, new TestEnvironment()), NullLogger<LegacyImporter>.Instance);
    }

    private static async Task<string> WriteCatalogAsync(string root)
    {
        var path = Path.Combine(root, "catalog.json");
        await File.WriteAllTextAsync(path, """
            {"bluejay":{"scientific_name":"Cyanocitta cristata","ukrainian_name":"Сойка блакитна"},
             "duplicate":{"scientific_name":"cyAnocitta cristata","ukrainian_name":"Duplicate"},
             "invalid":{"scientific_name":null}}
            """);
        return path;
    }

    [SqlFact]
    public async Task MigratesBackupMediaPredictionsAndEvaluationsWithoutChangingOriginalOrDuplicatingRows()
    {
        await using var database = new SqlDatabase();
        await database.Db.Database.MigrateAsync();
        using var input = new TestFiles();
        using var output = new TestFiles();
        var source = await WriteSourceAsync(input.Folder);
        var original = await File.ReadAllBytesAsync(source);
        var catalog = await WriteCatalogAsync(input.Folder);
        var importer = Importer(database, output, catalog);

        var first = JsonSerializer.SerializeToElement(await importer.ImportAsync(source));
        var second = JsonSerializer.SerializeToElement(await importer.ImportAsync(source));

        Assert.Equal(1, first.GetProperty("added").GetInt32());
        Assert.Equal(1, second.GetProperty("skipped").GetInt32());
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        var backup = first.GetProperty("backup").GetString()!;
        Assert.Equal("preserved,legacy,csv\n", await File.ReadAllTextAsync(Path.Combine(backup, "bird_analysis.csv")));
        Assert.True(File.Exists(Path.Combine(backup, "result.json")));
        var stored = await database.Db.Analyses.Include(a => a.Bird).Include(a => a.Predictions).Include(a => a.Evaluation).SingleAsync();
        Assert.Equal(91, stored.LegacyId);
        Assert.Equal("pinned-revision", stored.ModelRevision);
        Assert.Equal("Completed", stored.Status);
        Assert.Equal(3, stored.Predictions.Count);
        Assert.Null(stored.Predictions.Single(p => p.Rank == 2).ScientificName);
        Assert.True(stored.Evaluation!.Top1Correct);
        Assert.True(stored.Evaluation.Top3Correct);
        Assert.Equal("https://example.org/birds", stored.Bird!.SourceUrl);
        Assert.Equal(2, await database.Db.Birds.CountAsync());
        var path = Assert.Single(Directory.GetFiles(output.Folder));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(input.Folder, "uploads", "bird.jpg")), await File.ReadAllBytesAsync(path));
    }

    [SqlFact]
    public async Task RejectedImportCannotLeaveRowsOrCopiedMedia()
    {
        await using var database = new SqlDatabase();
        await database.Db.Database.MigrateAsync();
        using var input = new TestFiles();
        using var unsafeInput = new TestFiles();
        using var tamperedInput = new TestFiles();
        using var output = new TestFiles();
        var catalog = await WriteCatalogAsync(input.Folder);
        var importer = Importer(database, output, catalog);
        Assert.Contains("SQLite", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(Path.Combine(input.Folder, "absent.db")))).Message);
        var traversal = await WriteSourceAsync(unsafeInput.Folder, "../external.jpg");
        Assert.Contains("шлях", (await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(traversal))).Message);
        var tampered = await WriteSourceAsync(tamperedInput.Folder, wrongHash: true);
        Assert.Contains("SHA-256", (await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(tampered))).Message);
        var inconsistent = await WriteSourceAsync(input.Folder, orphan: true);
        Assert.Contains("Транзакцію скасовано", (await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(inconsistent))).Message);
        database.Db.ChangeTracker.Clear();
        Assert.Empty(await database.Db.Analyses.ToArrayAsync());
        Assert.Empty(await database.Db.Predictions.ToArrayAsync());
        Assert.Empty(Directory.GetFiles(output.Folder));
    }

    [SqlFact]
    public async Task EmptyDisposableDatabaseCanDowngradeAndReapplyAllMigrations()
    {
        await using var database = new SqlDatabase();
        var db = database.Db;
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await migrator.MigrateAsync();
        Assert.Equal(new[] { "FREE", "PRO", "PREMIUM" }, await db.SubscriptionPlans.OrderBy(p => p.Id).Select(p => p.Name).ToArrayAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
