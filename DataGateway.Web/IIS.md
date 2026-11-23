# Host DataGateway.Web on IIS with Windows sign-in

This guide runs the example host on IIS for a Windows domain. Callers sign in with their own Windows account, by Kerberos or NTLM as fallback, and no password or token is involved anywhere. Claude Desktop reaches the site through `DataGateway.Proxy`, which runs on each user's PC as that user ([Claude Desktop](#9-connect-claude-desktop)).

The code is the same as in [README.md](README.md). Under IIS, IIS does the sign-in handshake and passes the signed-in user to the app. The app's Negotiate handler sees that IIS has Windows Authentication turned on and leaves the handshake to IIS. [web.config](web.config) turns Windows Authentication on and Anonymous Authentication off for the site.

Two identities are involved, and they are different:

- **The caller** is the person signed in to Windows. IIS checks who they are, and every request must carry their sign-in.
- **The app pool identity** runs the app. It is the only account that reads SQL Server, and the caller's own SQL rights are never used. Whoever can sign in can read every active source in the catalog, so the catalog is the boundary. To limit who may sign in at all, see [Limit who can sign in](#limit-who-can-sign-in).

The examples use `data.example.com` for the site, `EXAMPLE` for the domain, `sql01` for SQL Server and `DataGateway` for the app pool. Replace them with your own.

## Requirements

- Windows Server joined to the domain, with the **Web Server (IIS)** role and its **Windows Authentication** role service (Web Server > Security).
- The ASP.NET Core Hosting Bundle for .NET 10, installed after IIS. It adds the ASP.NET Core Module. Run `net stop was /y` then `net start w3svc` after installing it.
- A TLS certificate for the site's host name that the client PCs trust, for example from your domain's certificate authority. Claude Desktop accepts only `https` addresses.
- The SQL Server catalog, set up as in [README.md](README.md#get-started) steps 2 and 4. Step 3 is replaced by step 2 below: the app pool identity signs in to SQL Server with Windows authentication, so no SQL login or password is needed.
- The .NET 10 SDK on the PC that publishes the app.

## 1. Choose the app pool identity

Pick one:

- **A group managed service account (gMSA)**, for example `EXAMPLE\svc-datagateway$`. Recommended: the domain manages its password. Install it on the web server with `Install-ADServiceAccount`.
- **ApplicationPoolIdentity** (the IIS default). The pool runs as `IIS APPPOOL\DataGateway`. It reaches a SQL Server on another machine as the web server's computer account, `EXAMPLE\WEBSERVER$`, so every app pool on that server shares those SQL rights.

## 2. Give the identity read-only access in SQL Server

Run this once for the catalog database and once for every database that holds a source view. Use the login name of the identity you picked: `[EXAMPLE\svc-datagateway$]`, `[EXAMPLE\WEBSERVER$]`, or `[IIS APPPOOL\DataGateway]` when SQL Server runs on the web server itself.

```sql
USE [master];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'EXAMPLE\svc-datagateway$')
    CREATE LOGIN [EXAMPLE\svc-datagateway$] FROM WINDOWS WITH DEFAULT_DATABASE = [master];
GO
USE [Catalog];   -- then repeat from here for each source database
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'EXAMPLE\svc-datagateway$')
    CREATE USER [EXAMPLE\svc-datagateway$] FOR LOGIN [EXAMPLE\svc-datagateway$];
ALTER ROLE [db_datareader] ADD MEMBER [EXAMPLE\svc-datagateway$];
ALTER ROLE [db_denydatawriter] ADD MEMBER [EXAMPLE\svc-datagateway$];
GO
```

These are the same rights `db/gateway-reader.sql` gives `gateway_reader`. Each source's connection string in the catalog then uses Windows authentication too: it names the server and the database with the Server and Database keywords, sets Integrated Security to true, and has no User ID or Password. Register it as in README step 4.

## 3. Unlock the authentication sections

IIS locks the two authentication sections at server level by default, so a site's web.config cannot set them. Without this step the site answers HTTP 500.19. Run it once per server from an elevated prompt:

```powershell
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
& $appcmd unlock config -section:system.webServer/security/authentication/windowsAuthentication
& $appcmd unlock config -section:system.webServer/security/authentication/anonymousAuthentication
```

## 4. Publish

From the public source root, on the publishing PC:

```powershell
dotnet publish DataGateway.Web -c Release -o publish/DataGateway.Web
```

Copy `publish/DataGateway.Web` to the server, for example to `C:\inetpub\DataGateway`. The folder holds `web.config`, with the program path filled in by publish, and `appsettings.json`, which the app needs at start.

## 5. Create the app pool and site

From an elevated PowerShell on the server:

```powershell
Import-Module WebAdministration
$site = 'C:\inetpub\DataGateway'
New-WebAppPool -Name DataGateway
Set-ItemProperty IIS:\AppPools\DataGateway -Name managedRuntimeVersion -Value ''   # No Managed Code
# For a gMSA (skip for ApplicationPoolIdentity):
Set-ItemProperty IIS:\AppPools\DataGateway -Name processModel.identityType -Value SpecificUser
Set-ItemProperty IIS:\AppPools\DataGateway -Name processModel.userName -Value 'EXAMPLE\svc-datagateway$'   # a gMSA has no password to set
New-Website -Name DataGateway -PhysicalPath $site -ApplicationPool DataGateway -HostHeader data.example.com -Port 443 -Ssl -SslFlags 1   # 1 = SNI
# Bind the certificate (its thumbprint from Cert:\LocalMachine\My):
(Get-WebBinding -Name DataGateway -Protocol https).AddSslCertificate('<thumbprint>', 'My')
```

Give the identity read access to the folder and write access to its `logs` subfolder:

```powershell
$identity = 'EXAMPLE\svc-datagateway$'   # or 'IIS APPPOOL\DataGateway'
New-Item -ItemType Directory -Force "$site\logs" | Out-Null
icacls $site /grant "${identity}:(OI)(CI)RX"
icacls "$site\logs" /grant "${identity}:(OI)(CI)M"
```

## 6. Set the catalog connection and the log path

The app reads the catalog connection only from an environment variable or user secrets, never from a settings file. Give it to the app pool, together with an absolute log path, so that both live in IIS's own configuration and a new publish does not replace them. With Windows authentication the connection holds no password.

```powershell
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
$pool = 'DataGateway'
# Server and Database keywords, Integrated Security set to true, no User ID or Password; one line, no apostrophes.
$catalog = Read-Host 'Catalog connection'
& $appcmd set config -section:system.applicationHost/applicationPools "/+[name='$pool'].environmentVariables.[name='DATAGATEWAY_CATALOG_CONNECTION',value='$catalog']" /commit:apphost
& $appcmd set config -section:system.applicationHost/applicationPools "/+[name='$pool'].environmentVariables.[name='Serilog__WriteTo__File__Args__path',value='C:\inetpub\DataGateway\logs\datagateway-.jsonl']" /commit:apphost
Restart-WebAppPool -Name $pool
```

The catalog connection must name a server and a database, or the app refuses to start ([catalog connection](CONFIGURATION.md#catalog-connection)).

## 7. Kerberos and the service principal name

IIS uses Kerberos when the client can get a ticket for the site's name, and falls back to NTLM when it cannot. Both sign the caller in. Kerberos is preferred: it is required across domain trusts, and NTLM may be turned off by policy.

With IIS defaults (kernel-mode authentication on), the ticket is decrypted by the web server's computer account, even when the pool runs as a gMSA. A site reached by the server's own name needs nothing more. A site reached by another name, such as `data.example.com`, needs that name registered on the computer account:

```powershell
setspn -S HTTP/data.example.com EXAMPLE\WEBSERVER$
```

Register each name only once in the domain; `setspn -S` refuses a duplicate, which would break Kerberos for that name. To check, call the site from a client (step 8), then run `klist` there: a ticket for `HTTP/data.example.com` means Kerberos was used.

## 8. Check it from a client PC

Signed in as a domain user, on a PC that is not the server:

```powershell
Invoke-RestMethod -Method Post -Uri https://data.example.com/sources/list -UseDefaultCredentials -ContentType 'application/json' -Body '{}'
```

It returns the active sources. The same call without `-UseDefaultCredentials` gets HTTP 401 with `WWW-Authenticate: Negotiate`. Under IIS that 401 comes from IIS itself, so its body is IIS's page and not the JSON `NotAuthenticated` body described in README.md: IIS answers the challenge before the app sees the request.

## 9. Connect Claude Desktop

Claude Desktop does not sign in with Windows itself. `DataGateway.Proxy` runs on the user's PC as the signed-in user and forwards Claude's calls to the site with that user's Windows sign-in. Build the extension package as described in [the proxy's package guide](../DataGateway.Proxy/mcpb/README.md) and give users the `.mcpb` file. Each user opens it in Claude Desktop (Settings > Extensions, or double-click the file) and enters `https://data.example.com/mcp`.

## Limit who can sign in

Windows Authentication lets in any account the domain accepts. To admit only a group, use IIS URL Authorization; it needs the **URL Authorization** role service (Web Server > Security). In IIS Manager, open the site's **Authorization Rules**, remove the inherited **Allow All Users** rule, and add an **Allow** rule for the group, for example `EXAMPLE\DataGateway Users`. Callers outside the group get HTTP 401 from IIS.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| HTTP 500.19 | The authentication sections are still locked (step 3), or the Windows Authentication role service is missing. |
| HTTP 500.30 or 500.31 | The app did not start: the Hosting Bundle is missing, the catalog connection is missing or invalid (step 6), or the identity cannot write the log folder. The Windows Application event log holds the reason. For a one-off diagnosis set `stdoutLogEnabled="true"` in the published web.config, then set it back. |
| An error that Windows Authentication is disabled | IIS has Windows Authentication off for the site: the app's handler requires the server's own implementation when one exists. |
| A sign-in prompt, or 401 for every user | No usable Kerberos ticket and NTLM is blocked: check the SPN (step 7), and that the client reaches the name the SPN uses. |
| `SourceUnavailable` or `CatalogUnavailable` | The app pool identity cannot reach or read SQL Server: check the login and database users (step 2) and the connection strings. |
