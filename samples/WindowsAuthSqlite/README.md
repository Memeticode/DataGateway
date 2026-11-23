# Windows sign-in sample over SQLite

A complete DataGateway app that admits only callers signed in with Windows (Kerberos, or NTLM as fallback) and serves a one-file SQLite database it creates itself. No database server, catalog setup or password is involved, so it isolates the sign-in part: use it to prove Windows sign-in on a machine, on IIS, and through Claude Desktop before connecting real data.

- [SampleProgram.cs](SampleProgram.cs) is the library README's quickstart app with Windows sign-in added: the Negotiate handler, and a fallback policy that makes every route, `/mcp` included, refuse anonymous callers.
- [SampleCatalog.cs](SampleCatalog.cs) and [SampleDatabase.cs](SampleDatabase.cs) are the README quickstart's files unchanged (a test keeps them identical): one source, `sample_items`, with three invented rows.
- [web.config](web.config) turns IIS Windows Authentication on and Anonymous Authentication off.

It uses the tests-only SQLite engine, `DataGateway.Data.Sqlite`, which is not a supported production engine. For real data, use [DataGateway.Web](../../DataGateway.Web/README.md) with SQL Server or PostgreSQL; its [IIS guide](../../DataGateway.Web/IIS.md) covers the same hosting with a SQL Server catalog.

## Run it on this PC

On Windows with the .NET 10 SDK, from the public source root:

```powershell
dotnet build samples/WindowsAuthSqlite
if ($LASTEXITCODE -ne 0) { throw 'Building the sample failed.' }
dotnet run --project samples/WindowsAuthSqlite --no-build --no-launch-profile -- --seed
dotnet run --project samples/WindowsAuthSqlite --no-build --no-launch-profile -- --urls http://127.0.0.1:5078
```

`--seed` writes `sample.db` beside the program, once: a second seed fails because the tables exist. The last command keeps running. From another window:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5078/sources/list -UseDefaultCredentials -AllowUnencryptedAuthentication -ContentType 'application/json' -Body '{}'
```

It returns `sample_items`. Without `-UseDefaultCredentials` the answer is HTTP 401 with `WWW-Authenticate: Negotiate`. `-AllowUnencryptedAuthentication` is needed only because this check uses plain `http`. Claude Desktop needs `https`: run with `--urls https://localhost:5443` and the .NET development certificate (`dotnet dev-certs https --trust`), or host it on IIS.

## Host it on IIS

Follow [DataGateway.Web/IIS.md](../../DataGateway.Web/IIS.md) with these differences:

- Skip steps 2 and 6: there is no SQL Server and no catalog connection. The app pool identity needs only read access to the site folder, which holds `sample.db`.
- Step 4 publishes this project instead, then seeds once in the published folder:

  ```powershell
  dotnet publish samples/WindowsAuthSqlite -c Release -o publish/WindowsAuthSqlite
  publish/WindowsAuthSqlite/WindowsAuthSqlite.exe --seed
  ```

  Copy `publish/WindowsAuthSqlite`, with `sample.db`, to the server.
- Steps 1, 3, 5, 7 and 8 are the same. Step 8's check returns `sample_items`.

## Connect Claude Desktop

Install the [DataGateway.Proxy extension](../../DataGateway.Proxy/mcpb/README.md) and enter the site's address with `/mcp`, for example `https://data.example.com/mcp`. Asking Claude to list the data sources returns `sample_items`, read as the Windows user running Claude Desktop.
