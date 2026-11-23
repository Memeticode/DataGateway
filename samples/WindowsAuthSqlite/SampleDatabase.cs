namespace WindowsAuthSample
{
    using Microsoft.Data.Sqlite;

    public static class SampleDatabase
    {
        public static async Task CreateAsync(string file, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(file),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            await using var db = new SqliteConnection(connection);
            await db.OpenAsync(cancellationToken);
            await using var transaction = db.BeginTransaction();
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE catalog_source (
                    name TEXT PRIMARY KEY COLLATE NOCASE,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    grain TEXT NOT NULL,
                    is_active INTEGER NOT NULL,
                    row_cap INTEGER NOT NULL,
                    object_name TEXT NOT NULL
                );
                INSERT INTO catalog_source VALUES (
                    'sample_items', 'Sample items', 'Three invented items',
                    'One item', 1, 2, 'items'
                );
                CREATE TABLE items (id INTEGER PRIMARY KEY);
                INSERT INTO items VALUES (1), (2), (3);
                """;
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
