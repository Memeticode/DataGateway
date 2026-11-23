namespace WindowsAuthSample
{
    using Microsoft.Data.Sqlite;

    /// <summary>Creates the quickstart's database: the catalog table with one source, and that source's
    /// table.</summary>
    public static class SampleDatabase
    {
        /// <summary>Creates the file and its two tables in one transaction.</summary>
        /// <remarks>
        /// <para><c>catalog_source</c> gets one row: the source <c>sample_items</c>, active, with a row cap of 2,
        /// reading the table <c>items</c>. <c>items</c> gets three rows, ids 1 to 3, so a query without a limit
        /// stops at the cap and comes back with <c>more</c> set to true. All the names and values are
        /// invented.</para>
        /// <para>This is the one connection that writes, so it opens the file read-write. Pooling is off, so the
        /// file is closed when seeding ends. Seeding runs once: a second run fails because the tables already
        /// exist, and the transaction leaves nothing half made.</para>
        /// </remarks>
        /// <param name="file">The database file to create.</param>
        /// <param name="cancellationToken">Cancels the seeding.</param>
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
