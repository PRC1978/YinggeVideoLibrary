using System.IO;

namespace YinggeVideoLibrary;

internal static class PostgresMigrationPreflightDiagnostics
{
    internal static IReadOnlyDictionary<string, bool> Verify()
    {
        const string path = @"C:\isolated-test\video.mp4";
        LibraryIndex Source() => new()
        {
            Folders = [@"C:\isolated-test"],
            Entries = [new IndexEntry { Path = path, Size = 42,
                ModifiedTicks = DateTime.UtcNow.Ticks }]
        };
        var metadata = new Dictionary<string, MovieMetadata>();
        var valid = true;
        try { PostgresLibraryMigration.ValidateSource(Source(), metadata); }
        catch { valid = false; }

        bool Rejects(Action change)
        {
            try { change(); return false; }
            catch (InvalidDataException) { return true; }
        }

        return new Dictionary<string, bool>
        {
            ["postgresPreflightAcceptsValidIndex"] = valid,
            ["postgresPreflightRejectsRelativePath"] = Rejects(() =>
            {
                var source = Source();
                source.Entries[0].Path = "relative.mp4";
                PostgresLibraryMigration.ValidateSource(source, metadata);
            }),
            ["postgresPreflightRejectsDuplicateFolder"] = Rejects(() =>
            {
                var source = Source();
                source.Folders = [@"C:\isolated-test", @"c:\ISOLATED-TEST"];
                PostgresLibraryMigration.ValidateSource(source, metadata);
            }),
            ["postgresPreflightRejectsInvalidTimestamp"] = Rejects(() =>
            {
                var source = Source();
                source.Entries[0].ModifiedTicks = -1;
                PostgresLibraryMigration.ValidateSource(source, metadata);
            }),
            ["postgresPreflightRejectsInvalidMetadata"] = Rejects(() =>
            {
                var invalid = new Dictionary<string, MovieMetadata>
                    { ["id"] = new MovieMetadata { SchemaVersion = 99 } };
                PostgresLibraryMigration.ValidateSource(Source(), invalid);
            })
        };
    }
}
