# SQLite replacement proof

This net10.0 assembly is a finite, tests-only implementation of the five public
`IDatabaseEngine` members. Product projects do not reference or ship this adapter
or its SQLite dependencies. SQL Server and PostgreSQL remain supported production engines.

`SqliteArchitectureTests`, `SqliteEngineTests` and `SqliteOperationsTests` run without
live prerequisites. From the repository root:

```text
dotnet restore src/DataGateway.Data.Sqlite/DataGateway.Data.Sqlite.csproj --locked-mode --ignore-failed-sources
dotnet build src/DataGateway.slnx -m:1 -nodeReuse:false
dotnet test src/tests/DataGateway.Web.Tests/DataGateway.Web.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~Sqlite
```

When working from the exported `src/` tree, omit `src/` in those paths. Run the full
Core and Web test projects separately to check existing expectations.

The fixture generates a unique temporary SQLite file containing invented catalog
and item rows. Parameterized inserts run in a transaction through a nonpooled
setup writer. Adapter connections use the original validated file connection text
with explicit read-only mode and pooling disabled. Fixture settings isolate user
secrets and inherited environment overrides; clients and hosts close before the
fixture directory is removed. No database is committed.

The proof covers list/describe through an app-owned test catalog composed with the public CatalogService API, normal HTTP/MCP
composition and synthetic sign-in, one bounded ordered detail query, nulls,
boolean equality/IN/null filters, nine literal pattern cases and nested patterns,
numeric COUNT with private Int64 match evidence, catalog shape refusals, a real
missing-object error and scoped source lifecycle. Each wire success has an
independent value oracle. Errors retain existing policy and incident correlation.

The adapter clones each validated query before SQLite COUNT/LIMIT/quoting/pattern
preparation. It binds the supplied parameter names. Only actual CLR boolean inputs
become Int64 0/1 parameters; only outputs declared `bool` in compiled metadata
convert integer 0/1 to JSON booleans. Null stays null, numeric 0/1 and COUNT stay
numeric. Rows are fresh, ordered, ordinal dictionaries that survive disposal.

The [library walkthrough](../README.md#add-it-to-a-new-app) uses a separate complete app-owned SQLite catalog; neither the library nor this engine requires the test fixture schema. See [catalog readers](../CATALOG-READERS.md) and [engines](../ENGINES.md).

The public constructor accepting `Func<string, SqliteConnection>` is an adapter-local
instrumentation seam. Each call transfers a fresh closed connection to the engine.
Tests wrap real SQLite connections, commands and readers to observe reads,
exceptions and completed cleanup, and to gate or fault cleanup after real disposal.
The engine awaits the consumer, expires saved sequence views, then attempts reader,
command and connection cleanup in order. A primary failure takes precedence over
cleanup failures. There is no provider framework or product registration change.

## Executable case names

The public Facts/Theories below provide the finite proof. Theory data expands
these methods into individual cases; no SQLite case has a live prerequisite or
skip attribute.

| Area | Public test methods |
|---|---|
| Architecture | `SqliteArchitectureTests.T1_project_seam_solution_and_incoming_reference_boundary`, `T1_every_product_restored_target_and_library_excludes_sqlite` |
| Validation and real read-only refusal | `SqliteEngineTests.T2_safe_validation`, `T2_required_mode`, `T2_guards_read_only_and_real_failure_facts`, `T2_arguments_refuse_before_factory_and_catalog_validations_are_local` |
| Compilation and patterns | `SqliteEngineTests.T3_compilation_goldens_and_clone_independence`, `T4_patterns_execute`, `T4_nested_clone_and_bound_injection` |
| Catalog and errors | `SqliteOperationsTests.T5_catalog_routes_normal_composition_freshness_and_fallback`, `T5_canonical_catalog_cells_and_sequential_cleanup`, `T6_catalog_shape_refusals_are_safe_and_clean` |
| Boolean output and filters | `SqliteEngineTests.T7_metadata_controls_detached_rows_and_boolean_refusal`, `SqliteOperationsTests.T7_detail_wire_types_and_keep_plus_one`, `T7_boolean_filters_execute_with_neutral_and_driver_types` |
| Count and validation | `SqliteOperationsTests.T8_count_types_private_evidence_and_zero_meaning`, `T8_boolean_aggregate_refusals_before_source_open` |
| Real provider error | `SqliteOperationsTests.T9_missing_object_provider_identity_wire_logs_and_leaks` |
| Ownership | `SqliteEngineTests.T10_real_lifecycle`, `T10_awaits_consumer_and_each_cleanup`, `T10_cleanup_faults_preserve_first_exception`, `T10_ordinal_names_and_enumeration_cancellation` |
| Native smoke | `SqliteArchitectureTests.T11_locked_graph_and_unsuppressed_native_provider_smoke` |

## Dependency and notice record

The only direct package is exactly `Microsoft.Data.Sqlite [10.0.13]`. The current generated
`packages.lock.json` records the complete adapter net10.0 package closure below,
including the dependencies inherited through Data and Core. The following licence/NOTICE observations summarize prior archive inspections; this documentation update does not include a new archive inspection. Updated Dapper.Data 3.1.2 and its Dapper 2.1.89 dependency are accounted for in the current lock; this documentation update does not claim a fresh upstream licence/NOTICE audit. Licence declarations reported below were inspected in actual cached NuGet archive nuspecs, not inferred from DLL names. Package-relative paths below refer to those archives.

| Package | Adapter resolved version | Licence provenance | Archive NOTICE finding |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.13 | `Microsoft.Data.Sqlite.nuspec`, MIT expression | No NOTICE-named file |
| Microsoft.Data.Sqlite.Core | 10.0.13 | `Microsoft.Data.Sqlite.Core.nuspec`, MIT expression | No NOTICE-named file |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | package nuspec, Apache-2.0 expression | No NOTICE-named file |
| SQLitePCLRaw.core | 2.1.13 | package nuspec, Apache-2.0 expression | No NOTICE-named file |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | package nuspec, Apache-2.0 expression | No NOTICE-named file |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | package nuspec, Apache-2.0 expression | No NOTICE-named file |
| Dapper | 2.1.89 | package nuspec, Apache-2.0 expression | Not re-inspected for this documentation update |
| Dapper.Data | 3.1.2 | Historical Apache-2.0 declaration; current archive not re-inspected | Not re-inspected for this documentation update |
| SqlKata | 4.0.1 | `SqlKata.nuspec`, MIT expression | No NOTICE-named file |
| SqlKata.JsonQuery | 0.2.0 | `SqlKata.JsonQuery.nuspec`, MIT expression | No NOTICE-named file |

The bundled SQLite engine is public domain ([SQLite copyright](https://sqlite.org/copyright.html));
the wrapper/native-build package has its own Apache-2.0 declaration. MIT requires
retaining the copyright and permission notice with redistributed copies or
substantial portions. Apache-2.0 requires its licence, retention of applicable
notices and identification of modifications; upstream NOTICE obligations must be
reviewed even when an archive has no NOTICE file. Licence expressions alone do not
complete that review. Public upstream sources are
[EF Core](https://github.com/dotnet/efcore/blob/main/LICENSE.txt),
[SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT),
[SqlKata](https://github.com/sqlkata/querybuilder/blob/master/LICENSE) and
[SqlKata.JsonQuery](https://github.com/mkonstan/SqlKata.JsonQuery).

The Web.Tests restored graph also includes shared product/test packages. Its graph
is distinct from the adapter lock. The current adapter closure also includes Dapper.Data and Dapper; shared versions agree;
System.Memory is absent from both net10.0 graphs. The inspected cached 4.5.3
archive is therefore not a resolved dependency; it contains LICENSE.TXT and
THIRD-PARTY-NOTICES.TXT and served as an inspection control. This README does not reproduce full restored graphs, shared-version
differences, archive hashes, exact applicable notice bytes or the executed native
asset/version. Existing product NOTICE is preserved. Offline restore's NU1900
means advisory data was unavailable; it is not a clean vulnerability audit. Online
advisory, bundled-native and final upstream licence/NOTICE review remain installation
gates.

## Proof boundary

This does not prove broad SQLite grammar or cap boundaries, collation/Unicode
equivalence, general date/numeric fidelity, audit/deployment, or long-running
timeout/cancellation behavior. Tokens, positive timeout guards and pre-cancellation
are tested, but do not establish interruption of long provider work. ASCII NOCASE
is used only for this fixture. Read-only enforcement is established only for the
tested open mode. This adapter provides no production SQLite support.

## Shared Web.Tests package inventory

This is the complete net10.0 Web.Tests package graph at restore, including existing
product and test dependencies. It adds no direct package to the adapter. The
licence column reports each inspected archive nuspec declaration (a file name
means a file-based declaration); the final upstream review remains an installation
gate. Exact archive licence/notice files and their hashes are not reproduced here; this documentation update does not reproduce an archive inspection.

| Package | Resolved | Archive licence declaration | NOTICE-named file |
|---|---|---|---|
| Dapper | 2.1.89 | Apache-2.0 | None |
| Dapper.Data | 3.1.2 | Historical Apache-2.0 declaration; current archive not re-inspected | Not re-inspected |
| Microsoft.AspNetCore.Authentication.Negotiate | 10.0.14 | MIT | THIRD-PARTY-NOTICES.TXT |
| Microsoft.AspNetCore.OpenApi | 10.0.14 | MIT | THIRD-PARTY-NOTICES.TXT |
| Microsoft.Bcl.Cryptography | 9.0.18 | MIT | THIRD-PARTY-NOTICES.TXT |
| Microsoft.CodeCoverage | 18.6.0 | MIT | ThirdPartyNotices.txt, build/netstandard2.0/ThirdPartyNotices.txt |
| Microsoft.Data.SqlClient | 7.1.1 | MIT | None |
| Microsoft.Data.SqlClient.Extensions.Abstractions | 7.1.1 | MIT | None |
| Microsoft.Data.SqlClient.Internal.Logging | 7.1.1 | MIT | None |
| Microsoft.Data.SqlClient.SNI.runtime | 7.1.0 | LICENSE.txt | None |
| Microsoft.Data.Sqlite | 10.0.13 | MIT | None |
| Microsoft.Data.Sqlite.Core | 10.0.13 | MIT | None |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT | None |
| Microsoft.Extensions.DependencyModel | 10.0.0 | MIT | THIRD-PARTY-NOTICES.TXT |
| Microsoft.IdentityModel.Abstractions | 8.16.0 | MIT | None |
| Microsoft.IdentityModel.JsonWebTokens | 8.16.0 | MIT | None |
| Microsoft.IdentityModel.Logging | 8.16.0 | MIT | None |
| Microsoft.IdentityModel.Protocols | 8.16.0 | MIT | None |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 8.16.0 | MIT | None |
| Microsoft.IdentityModel.Tokens | 8.16.0 | MIT | None |
| Microsoft.NET.Test.Sdk | 18.6.0 | MIT | None |
| Microsoft.OpenApi | 2.12.2 | MIT | None |
| Microsoft.SqlServer.Server | 1.0.0 | MIT | None |
| Microsoft.TestPlatform.ObjectModel | 18.6.0 | MIT | None |
| Microsoft.TestPlatform.TestHost | 18.6.0 | MIT | ThirdPartyNotices.txt |
| ModelContextProtocol | 2.1.0 | Apache-2.0 | None |
| ModelContextProtocol.AspNetCore | 2.1.0 | Apache-2.0 | None |
| ModelContextProtocol.Core | 2.1.0 | Apache-2.0 | None |
| Newtonsoft.Json | 13.0.3 | MIT | None |
| Scalar.AspNetCore | 2.18.0 | MIT | None |
| Serilog | 4.4.0 | Apache-2.0 | None |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | None |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | None |
| Serilog.Formatting.Compact | 3.0.0 | Apache-2.0 | None |
| Serilog.Settings.Configuration | 10.0.1 | Apache-2.0 | None |
| Serilog.Sinks.Console | 6.1.1 | Apache-2.0 | None |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | None |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | None |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | None |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | None |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | None |
| SqlKata | 4.0.1 | MIT | None |
| SqlKata.JsonQuery | 0.2.0 | MIT | None |
| System.Configuration.ConfigurationManager | 9.0.18 | MIT | THIRD-PARTY-NOTICES.TXT |
| System.DirectoryServices.Protocols | 10.0.14 | MIT | THIRD-PARTY-NOTICES.TXT |
| System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT | None |
| System.Security.Cryptography.Pkcs | 10.0.14 | MIT | THIRD-PARTY-NOTICES.TXT |
| System.Security.Cryptography.ProtectedData | 9.0.18 | MIT | THIRD-PARTY-NOTICES.TXT |
| xunit | 2.9.3 | Apache-2.0 | None |
| xunit.abstractions | 2.0.3 | Legacy licence URL: https://raw.githubusercontent.com/xunit/xunit/master/license.txt | None |
| xunit.analyzers | 1.18.0 | Apache-2.0 | None |
| xunit.assert | 2.9.3 | Apache-2.0 | None |
| xunit.core | 2.9.3 | Apache-2.0 | None |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 | None |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 | None |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | None |
