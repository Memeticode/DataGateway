# Store types: filter values typed like their columns (SQL Server)

A SQL Server source can record each catalog column's database type in `store_type`. DataGateway then sends every
filter value compared with that column as the column's own type, the way Entity Framework Core's SQL Server provider
does. SQL Server can then use the column's index without converting the column, and one query plan serves every
value. A column whose `store_type` is empty binds its values exactly as before. One change applies to every SQL Server
source, with or without store types: IN and NOT IN lists are padded (see below), which returns the same rows but
changes the SQL text and the parameter count. PostgreSQL and SQLite sources ignore `store_type`.

## The catalog cells

| cell | type | meaning |
|---|---|---|
| `store_type` | text, nullable | the view column's type as SQL Server spells it: `varchar(20)`, `nvarchar(max)`, `char(10)`, `decimal(19,4)`, `datetime`, `int` |
| `store_type_override` | bool, default false | true: `store_type` is a deliberate choice and is used even when the view's type differs |

Existing catalogs add both cells with [db/catalog-store-type.sql](db/catalog-store-type.sql) (SQL Server) or
[db/postgresql/catalog-store-type.sql](db/postgresql/catalog-store-type.sql) (PostgreSQL). Both are rerunnable and keep
audit history.

## How a value binds

| store_type | value | sent as |
|---|---|---|
| `varchar(n)`, `char(n)` | ASCII text up to 4000 characters | `varchar`: size n if it fits, else 8000; on a `char(n)` column a value exactly n long goes as `char(n)` |
| `varchar(n)`, `char(n)` | ASCII text over 4000 characters | `varchar(max)`, whatever n is |
| `varchar(n)`, `char(n)` | other text | `nvarchar`, as without a store type: a non-Unicode parameter would turn characters the column cannot hold into `?` |
| `varchar` with a `_UTF8` collation | text | `nvarchar` |
| `nvarchar(n)`, `nchar(n)` | text | `nvarchar`: size n if it fits, else 4000, else max; on an `nchar(n)` column a value exactly n long goes as `nchar(n)` |
| `int`, `bigint`, `smallint`, `tinyint` | whole number in range | that integer type |
| `datetime2` | date | `datetime2` |
| `datetime`, `smalldatetime`, `date` | date the type holds exactly: for `datetime` whole hundredths of a second, for `smalldatetime` whole minutes, for `date` no time part | that date type |
| `datetime`, `smalldatetime`, `date` | any other date | `datetime2`, as without a store type: converting would round or drop part of it |
| `bit` | bool | `bit` |
| `decimal`, `numeric` | number | the value's own precision and scale: a filter value is never rounded or cut to the column's scale |
| anything else, or a value that does not fit | | as without a store type |

A value is never cut short: a value longer than the column goes at the larger size or max. Non-Unicode text over 4000
characters always goes as `varchar(max)`, so it is not cut even when the column is really `nvarchar` (a
`store_type_override`, or a view changed since the check): SQL Server would convert a `varchar(8000)` value to
`nvarchar(4000)`. A shorter fixed-length value is sent as the variable-length type because SQL Server would pad it with
blanks, which a `like` pattern would then have to match.

IN and NOT IN lists are padded on every SQL Server source, with or without store types, by repeating their last value
(6 to 10 values become 10, and so on), so lists of similar length share a plan. The rows are unchanged; the SQL text
and the parameter count differ. SQL Server takes at most 2100 parameters in one query, so a list is not padded when
padding would bring the query's parameters, every one counted, above 2098; a query that runs unpadded is never refused
because of padding.

## The visible differences

Two things can change which rows a filter returns once a column has its `store_type`. Both are what Entity Framework's
binding does too, and both apply only to `varchar` and `char` columns, where an ASCII value is now sent as `varchar`.

Trailing blanks: with a non-Unicode type, `like` and `contains` follow SQL Server's non-Unicode rule and ignore trailing
blanks. On a `char(10)` column holding `abc`, `like '%abc'` now matches; without a store type it does not. Equality is
unchanged (SQL Server ignores trailing blanks for `=` either way).

SQL_ collations: under a SQL collation (a name starting `SQL_`, such as `SQL_Latin1_General_CP1_CI_AS`), SQL Server
compares a non-Unicode value with the column by the collation's non-Unicode rules, and a Unicode value by its Unicode
rules, which treat some characters as equal that the non-Unicode rules do not. So on such a column `eq`, `in`, `like`
and `contains` can lose rows, and `neq` and `not_in` gain them. For example, on a column holding a word spelled with
U+00DF (the German sharp s), `eq` with the same word spelled `ss` returns that row without a store type and does not
with one. Windows collations (such as `Latin1_General_CI_AS`) compare the same either way. To keep the old binding on
a SQL_ collation column, leave its `store_type` empty. The fill tool never fills such a column: it names it in a
comment, and its owner may set `store_type` by hand once these differences are acceptable.

## When the catalog and the view disagree

The first time a SQL Server source with store types is used, DataGateway reads its view's column types (one extra
connection, bounded by the lesser of 15 seconds and the source's query timeout) and keeps them until restart.

| case | log (category `DataGateway.StoreType`) | the column binds |
|---|---|---|
| types match | none | as `store_type` |
| mismatch, `store_type_override` false | Error, once per process | as without a store type |
| mismatch, `store_type_override` true | Warning, once | as `store_type` |
| column missing from the view | Error, once | as without a store type |
| two view columns differ from the catalog's name only in letter case and neither matches it exactly (a case-sensitive database), `store_type_override` true or false | Error, once | as without a store type |
| view types could not be read | Error on the first failure, then not again for that source until a read succeeds, or at most once an hour; retried on a query at least 30 seconds after the failed read began | every column of the source as without a store type |
| `object_name` with a database or server part, or a synonym for an object on another server, in another database or no longer there | Error, once per process; not retried | every column of the source as without a store type |
| `store_type` not readable | Warning, once | as without a store type |

Column names match ignoring case; when a view has two columns that differ only in case, the one spelled exactly like
the catalog's column is the one compared. Types compare with SQL Server's defaults filled in (a bare `decimal` is `decimal(18,0)`, `datetime2` is
`datetime2(7)`), and names SQL Server treats as one type are equal: `decimal` and `numeric` at the same precision and
scale, `rowversion` and `timestamp`.

Every message names the source and, when it is known, the column and both types, and says how to fix it. A query is
never refused or held up beyond that one read, and host startup never reads source metadata.

## Filling and checking store types

`SqlServerStoreTypeScript.CreateAsync(sources, CatalogScriptDialect.SqlServer or PostgreSql, timeoutSeconds, token)`
reads each SQL Server source's view and returns UPDATE statements for the catalog, written for the catalog's own
engine. It runs nothing. It writes a statement only for a column whose `store_type` is empty or differs from the view.
Everything else it could not vouch for gets a comment and no statement: columns with `store_type_override` set,
columns the view lacks, columns that match two view columns in different letter case, `varchar` and `char` columns
under a SQL_ collation, columns whose view type DataGateway does not bind by, columns whose name is not a bare ASCII SQL
identifier (named by ordinal only), and sources whose view could not be read. A PostgreSQL script starts with
`SET LOCAL standard_conforming_strings = on;`, so its literals read the same way whatever the server's setting; run it
in one transaction.

The same call is the drift report. A script with no UPDATE statements has nothing to apply; it does not say the catalog
matches the views. Read its comments: they list the overridden, unreadable, ambiguous, SQL_-collation and missing
columns, and any view that could not be read.

Keeping store types current, for a person or an agent with database access:

1. Run the fill tool for the SQL Server sources.
2. Review each statement: it names the source, the column and the view's type.
3. Apply the statements to the catalog in one transaction.
4. Run the fill tool again; it should return no statements. Read the comments that remain.
5. Leave columns with `store_type_override` set alone unless their owner asks.
6. Leave a `varchar` or `char` column under a SQL_ collation to its owner: setting its `store_type` changes which rows
   some filters return ("The visible differences" above). The owner sets it by hand if that is acceptable.
7. For a column matching two view columns in different letter case, spell `column_name` in the catalog exactly as the
   view does, then run the fill tool again.

To return a column to the old binding, set its `store_type` to null.
