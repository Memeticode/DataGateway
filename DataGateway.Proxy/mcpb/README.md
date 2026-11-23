# DataGateway.Proxy as a Claude Desktop extension

Claude Desktop does not sign in with Windows. `DataGateway.Proxy` runs on the user's PC as the signed-in user and forwards Claude's tool calls to a DataGateway server, signing in with that user's Windows account (Kerberos, or NTLM as fallback). This folder packages it as a `.mcpb` file, a Claude Desktop extension that a user installs by opening it. The program inside is self-contained, so the PC needs no .NET install.

The server side is [DataGateway.Web on IIS](../../DataGateway.Web/IIS.md), or any DataGateway host that requires Windows sign-in.

A prebuilt package, [datagateway-proxy.mcpb](datagateway-proxy.mcpb), is in this folder on this branch, built from this branch's source for Windows x64. Build your own as below to change the manifest or pick up later changes.

## Build the package

On Windows, with the .NET 10 SDK and PowerShell 7, from the public source root:

```powershell
pwsh -File DataGateway.Proxy/mcpb/pack.ps1
```

It writes `publish/mcpb/datagateway-proxy.mcpb`, about 35 MB (the program inside is about 75 MB). Pass `-Out <folder>` to write elsewhere; the script refuses to overwrite an earlier package.

## Install it

1. Open the `.mcpb` file, or in Claude Desktop go to Settings > Extensions and install it from there.
2. Enter the **Gateway address**: the server's MCP address, for example `https://data.example.com/mcp`. Claude Desktop accepts only `https` addresses, so the server needs a certificate the PC trusts.
3. Enable the extension. Its tools appear as the server names them.

To give it to several users, hand out the same file; each user enters the address once. For more than one gateway, install the package once per address under different extension names by changing `name` and `display_name` in [manifest.json](manifest.json) before packing.

## What happens when it runs

- It connects once, at start, and lists the server's tools. An unreachable server or a refused sign-in ends it with exit code 1, and Claude Desktop shows the extension as failed; the reason is in the log.
- Each tool call goes to the server signed in as the Windows user running Claude Desktop. The server sees that user, not a shared account.
- Its log is `logs/datagateway-proxy-<date>.log` beside the program, inside Claude Desktop's extension folder (`%APPDATA%\Claude\Claude Extensions\`). Protocol traffic never goes to the log.
- Error codes the proxy adds, such as `ServiceUnreachable` and `ServiceRefused`, are described in [the host README](../../DataGateway.Web/README.md#mcp).

## Check it without Claude Desktop

From the published folder (`publish/mcpb/stage/server`), signed in as a domain user:

```powershell
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' |
    .\DataGateway.Proxy.exe https://data.example.com/mcp
```

A reply line with the server's `serverInfo` means the proxy reached the server and signed in.
