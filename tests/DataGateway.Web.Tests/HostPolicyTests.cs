using System.Net;
using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class HostPolicyTests
{
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));

    [Fact]
    public async Task T01_route_refusals()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var name in HostPolicyHost.Names)
            foreach (var operation in HostPolicyHost.Operations)
            {
                var before = host.Host.Logs.Failures.Length;
                var response = await host.Send(host.Api(name, operation), body: HostPolicyHost.Body(name, operation));
                Assert.Equal(HostPolicyHost.Title(name) + " credentials were rejected.", HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
                var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual(name));
                Assert.Equal(new[] { true, false }, host.Signals.Accepted[response.Call]);
                HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, operation, HostPolicyHost.Actual(name));
            }
        host.NoWork();
        await host.Witnesses();
    }

    [Fact]
    public async Task T02_mcp_refusals()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var name in HostPolicyHost.Names)
        {
            var before = host.Host.Logs.Failures.Length;
            var response = await host.SendMcp(name);
            Assert.Equal(HostPolicyHost.Title(name) + " credentials were rejected.", HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
            var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual(name));
            Assert.True(host.Signals.Accepted[response.Call][0]);
            HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, "mcp", HostPolicyHost.Actual(name));
        }
        host.NoWork();
    }

    [Fact]
    public async Task T03_page_refusals()
    {
        foreach (var pageOnly in new[] { false, true })
        {
            await using var host = new HostPolicyHost(nested: true, pageOnly: pageOnly);
            await host.Start();
            foreach (var name in HostPolicyHost.Names)
                foreach (var suffix in HostPolicyHost.Pages)
                {
                    var before = host.Host.Logs.Failures.Length;
                    var response = await host.Send(host.Page(name, suffix));
                    Assert.Equal(HostPolicyHost.Title(name) + " credentials were rejected.", HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
                    var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual(name));
                    HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, suffix == "openapi/v1.json" ? "document" : "page", HostPolicyHost.Actual(name));
                    var signed = await host.Send(host.Page(name, suffix), signed: true);
                    if (suffix == "scalar")
                    {
                        Assert.Equal(302, signed.Status);
                        Assert.EndsWith("scalar/", signed.Headers["Location"]);
                    }
                    else
                        Assert.Equal(200, signed.Status);
                    if (suffix.StartsWith("scalar/", StringComparison.Ordinal) && suffix.Contains('.'))
                        Assert.Equal(PageAssets.Packaged(suffix[7..]), signed.Bytes);
                }
            host.NoWork();
        }
    }

    [Fact]
    public async Task T04_unrecorded_challenges()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        var prior = await host.Send(host.Api("north", "list"), body: "{}");
        HostPolicyAssert.Envelope(prior, HostPolicyHost.Actual("north"));
        var before = host.Host.Logs.Failures.Length;
        foreach (var name in HostPolicyHost.Names)
        {
            var responses = new List<HostPolicyResponse>();
            foreach (var operation in HostPolicyHost.Operations)
                responses.Add(await host.Send(host.Api(name, operation), "challenge", body: HostPolicyHost.Body(name, operation)));
            responses.Add(await host.SendMcp(name, "challenge"));
            foreach (var suffix in HostPolicyHost.Pages)
                responses.Add(await host.Send(host.Page(name, suffix), "challenge"));
            foreach (var response in responses)
            {
                HostPolicyAssert.Envelope(response, HostPolicyHost.Fallback(name), recorded: false, status: 401);
                Assert.Equal(HostPolicyHost.Title(name) + " credentials are required.", HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
                Assert.Equal("Synthetic challenge-leg", response.Headers["WWW-Authenticate"]);
            }
        }
        Assert.Equal(before, host.Host.Logs.Failures.Length);
        host.NoWork();
    }

    [Fact]
    public async Task T05_duplicate_install_and_record()
    {
        await using var host = new HostPolicyHost(twice: true);
        await host.Start();
        foreach (var kind in new[] { "list", "mcp", "page" })
        {
            var before = host.Host.Logs.Failures.Length;
            var writes = host.Writes.Attempts;
            var response = kind == "mcp" ? await host.SendMcp("north", "duplicate") :
                await host.Send(kind == "page" ? host.Page("north", "scalar/") : host.Api("north", "list"), "duplicate", body: kind == "list" ? "{}" : null);
            var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual("north"));
            Assert.Equal(new[] { true, false }, host.Signals.Accepted[response.Call]);
            HostPolicyAssert.Line(host.Host.Logs, before, incident!, "north", kind, HostPolicyHost.Actual("north"));
            Assert.Equal(writes + 1, host.Writes.Attempts);
        }
        Assert.Equal(3, host.Signals.Traversals);
        await using var bare = new HostPolicyHost(boundary: false);
        await bare.Start();
        var noBoundary = await bare.Send(bare.Api("north", "list"), body: "{}");
        Assert.False(bare.Signals.Accepted[noBoundary.Call][0]);
        HostPolicyAssert.Bare(noBoundary);
        Assert.Empty(bare.Host.Logs.Failures);
    }

    [Fact]
    public async Task T06_only_empty_401_is_completed()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var mode in new[] { "length", "type", "started", "403", "200", "empty" })
        {
            var before = host.Host.Logs.Failures.Length;
            var writes = host.Writes.Attempts;
            var probe = await host.Probe("north", "list", async context =>
            {
                Assert.True(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("north")));
                Assert.Equal(before, host.Host.Logs.Failures.Length);
                Assert.Equal(writes, host.Writes.Attempts);
                Assert.Equal(0, context.Response.Body.Length);
                context.Response.StatusCode = mode == "403" ? 403 : mode == "200" ? 200 : 401;
                context.Response.Headers["X-Host"] = "preserved";
                context.Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
                if (mode == "length")
                    context.Response.ContentLength = 0;
                if (mode == "type")
                    context.Response.ContentType = "host/type";
                if (mode == "started")
                {
                    await context.Response.WriteAsync("host-owned body");
                    ((HostPolicyResponseFeature)context.Features.Get<IHttpResponseFeature>()!).HasStarted = true;
                    Assert.False(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("south")));
                }
            });
            Assert.Equal("preserved", probe.Context.Response.Headers["X-Host"]);
            Assert.Equal("Synthetic challenge-leg", probe.Context.Response.Headers.WWWAuthenticate);
            Assert.False(probe.Context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("north")));
            if (mode == "empty")
            {
                var incident = HostPolicyAssert.Envelope(probe.Reply, HostPolicyHost.Actual("north"));
                HostPolicyAssert.Line(host.Host.Logs, before, incident!, "north", "list", HostPolicyHost.Actual("north"));
                Assert.Equal(writes + 1, host.Writes.Attempts);
            }
            else
            {
                HostPolicyAssert.Bare(probe.Reply, mode == "403" ? 403 : mode == "200" ? 200 : 401, mode == "started" ? "host-owned body" : "");
                Assert.Equal(before, host.Host.Logs.Failures.Length);
                Assert.Equal(writes, host.Writes.Attempts);
                if (mode == "type")
                    Assert.Equal("host/type", probe.Context.Response.ContentType);
                if (mode == "length")
                    Assert.Equal(0, probe.Context.Response.ContentLength);
            }
        }
        var startedProbe = await host.Probe("north", "list", context =>
        {
            Assert.False(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("north")));
            return Task.CompletedTask;
        }, started: true);
        Assert.Empty(startedProbe.Output.ToArray());
    }

    [Fact]
    public async Task T07_selected_endpoint_boundary()
    {
        await using var host = new HostPolicyHost(nested: true);
        await host.Start();
        foreach (var path in new[] { "/host", "/north-similar", "/north/host-neighbor", "/south/host-neighbor", "/north-docs/host-neighbor", "/south-docs/host-neighbor" })
        {
            var response = await host.Send(host.Root + path, "challenge", signed: true);
            HostPolicyAssert.Bare(response);
            Assert.Equal("Synthetic challenge-leg", response.Headers["WWW-Authenticate"]);
            Assert.Equal("False", response.Headers["X-Host-Record"]);
        }
        var fault = await host.Send(host.Root + "/host-fault", signed: true);
        HostPolicyAssert.Bare(fault, 591, "outer host fault");
        HostPolicyAssert.Bare(await host.Send(host.Root + "/absent", signed: true), 404);
        HostPolicyAssert.Bare(await host.Send(host.Api("north", "list"), signed: true), 405);
        Assert.Empty(host.Host.Logs.Failures);
        foreach (var name in HostPolicyHost.Names)
        {
            var before = host.Host.Logs.Failures.Length;
            var response = await host.Send(host.Api(name, "list"), body: "{}");
            var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual(name));
            HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, "list", HostPolicyHost.Actual(name));
        }
        await host.Witnesses();
    }

    [Fact]
    public async Task T08_host_fault_before_handler()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var name in HostPolicyHost.Names)
            foreach (var operation in new[] { "list", "mcp", "document", "page" })
            {
                var error = new HostPolicyFault("synthetic private host detail");
                host.Middleware = (_, _) => throw error;
                var before = host.Host.Logs.Failures.Length;
                var response = operation == "mcp" ? await host.SendMcp(name) :
                    await host.Send(operation == "list" ? host.Api(name, "list") : host.Page(name, operation == "document" ? "openapi/v1.json" : "scalar/"), body: operation == "list" ? "{}" : null);
                var incident = HostPolicyAssert.Envelope(response, ErrorEnvelope.Unexpected, status: 500);
                Assert.Equal(ErrorEnvelope.UnexpectedErrorMessage, HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
                var line = HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, operation,
                    ErrorEnvelope.Unexpected with { ExceptionType = "HostPolicyFault" }, ordinary: false);
                Assert.Equal(error.ToString(), line.Fields["ExceptionText"]);
                Assert.Null(line.Fields["Request"]);
                Assert.Null(line.Fields["Sql"]);
            }
        host.NoWork();
        Assert.DoesNotContain(host.Host.Logs.Events, line => line.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && line.Exception is not null);
    }

    [Fact]
    public async Task T09_host_fault_after_handler_aborts()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var name in HostPolicyHost.Names)
        {
            var entered = Gate();
            var release = Gate();
            var error = new HostPolicyFault("synthetic after-handler detail");
            var started = false;
            host.Middleware = async (context, next) =>
            {
                await next(context);
                await context.Response.Body.FlushAsync();
                started = context.Response.HasStarted;
                entered.TrySetResult();
                await Wait(release.Task);
                throw error;
            };
            var before = host.Host.Logs.Failures.Length;
            var writes = host.Writes.Attempts;
            var id = Guid.NewGuid().ToString("N");
            var done = host.Signals.Expect(id);
            var body = HostPolicyHost.Body(name, "query");
            using var request = HostPolicyHost.Request(host.Api(name, "query"), "record", true, body, id);
            using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await Wait(entered.Task);
            Assert.True(started);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            release.TrySetResult();
            var errorRead = await Record.ExceptionAsync(() => response.Content.ReadAsByteArrayAsync());
            Assert.NotNull(errorRead);
            Assert.True(errorRead is HttpRequestException or IOException, errorRead.ToString());
            await Wait(done);
            var line = Assert.Single(host.Host.Logs.Failures.Skip(before));
            HostPolicyAssert.Line(host.Host.Logs, before, (string)line.Fields["IncidentId"]!, name, "query",
                ErrorEnvelope.Unexpected with { Step = "response", ExceptionType = "HostPolicyFault" }, "synthetic-reader", ordinary: false);
            Assert.Equal(body, line.Fields["Request"]);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), line.Fields["RequestBytes"]);
            Assert.Equal(false, line.Fields["RequestCut"]);
            Assert.Equal(host.Engines[name == "north" ? 0 : 1].Compiled.Last().Sql, line.Fields["Sql"]);
            var executed = host.Engines[name == "north" ? 0 : 1].Executed.Last().Query;
            var parameters = Assert.IsType<Dictionary<string, object?>>(line.Fields["SqlParameters"]);
            Assert.NotEmpty(executed.Parameters);
            Assert.Equal(executed.Parameters.Select(pair => (pair.Key, Type: (Type?)pair.Value.GetType(), Value: (object?)pair.Value)),
                parameters.Select(pair => (pair.Key, Type: pair.Value?.GetType(), pair.Value)));
            Assert.Equal(101, line.Fields["SourceId"]);
            Assert.Equal("synthetic_" + name, line.Fields["SourceName"]);
            Assert.Equal(error.ToString(), line.Fields["ExceptionText"]);
            Assert.Equal(writes, host.Writes.Attempts);
        }
        var count = host.Host.Logs.Failures.Length;
        var attempts = host.Writes.Attempts;
        var sentinel = await host.Probe("north", "query", async context =>
        {
            await context.Response.WriteAsync("flushed sentinel");
            await context.Response.Body.FlushAsync();
            ((HostPolicyResponseFeature)context.Features.Get<IHttpResponseFeature>()!).HasStarted = true;
            throw new HostPolicyFault("sentinel detail");
        });
        Assert.Equal("flushed sentinel", Encoding.UTF8.GetString(sentinel.Output.ToArray()));
        Assert.Equal(1, sentinel.Lifetime.Aborts);
        Assert.Equal(attempts, host.Writes.Attempts);
        var sentinelLine = Assert.Single(host.Host.Logs.Failures.Skip(count));
        Assert.Equal("response", sentinelLine.Fields["Step"]);
        Assert.Equal("query", sentinelLine.Fields["Operation"]);
    }

    [Fact]
    public async Task T10_bad_http_request_mapping()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        foreach (var name in HostPolicyHost.Names)
            foreach (var status in new[] { 413, 400 })
            {
                var before = host.Host.Logs.Failures.Length;
                var response = await host.Send(host.Api(name, "list"), "bad" + status, body: "{}");
                var message = status == 413 ? "The request body is larger than DataGateway accepts. Send a smaller request." : "The request body could not be read in full. Send the complete JSON body in one request.";
                var failure = new CatalogFailure(status == 413 ? "StructuralLimitExceeded" : "InvalidJson", "$", message) { Step = "request", ExceptionType = "BadHttpRequestException" };
                Assert.Equal(message, HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
                var incident = HostPolicyAssert.Envelope(response, failure, status: 400);
                HostPolicyAssert.Line(host.Host.Logs, before, incident!, name, "list", failure);
                Assert.DoesNotContain("synthetic private body detail", Encoding.UTF8.GetString(response.Bytes), StringComparison.Ordinal);
            }
        var count = host.Host.Logs.Failures.Length;
        var probe = await host.Probe("north", "list", _ => throw new BadHttpRequestException("synthetic started detail", 413), started: true);
        Assert.Equal(1, probe.Lifetime.Aborts);
        Assert.Empty(probe.Output.ToArray());
        var line = Assert.Single(host.Host.Logs.Failures.Skip(count));
        Assert.Equal("UnexpectedError", line.Fields["Code"]);
        Assert.Equal("response", line.Fields["Step"]);
        Assert.Equal("BadHttpRequestException", line.Fields["ExceptionType"]);
    }

    [Fact]
    public async Task T11_caller_ended_mcp_is_silent()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        using var headers = new HostPolicyMcpHeaders();
        using var http = new HttpClient(headers) { BaseAddress = host.Client.BaseAddress };
        var completed = host.Signals.Expect(headers.CallId);
        await using (var client = await NamedGatewayMcpWire.Connect(http, host.Mcp("north"), null, CancellationToken.None))
        {
            var entered = Gate();
            host.Engines[0].BeforeExecute = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pending = McpWire.Call(client, "query_source", HostPolicyHost.Body("north", "query"), cancel.Token).AsTask();
            await Wait(entered.Task);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await client.DisposeAsync();
            Assert.Equal(headers.CallId, await completed.WaitAsync(TimeSpan.FromSeconds(15)));
            var line = Assert.Single(host.Host.Logs.Failures);
            Assert.Equal("QueryCancelled", line.Fields["Code"]);
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal("query_source", line.Fields["Operation"]);
            Assert.Equal("north", line.Scopes["Gateway"]);
        }
        await using var server = McpServer.Create(new StreamableHttpServerTransport { Stateless = true }, new McpServerOptions());
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        foreach (var cancelled in new[] { false, true })
            foreach (var present in new[] { false, true })
                foreach (var error in new Exception[] { new OperationCanceledException("synthetic cancellation"), new IOException("synthetic IO"), new HostPolicyFault("synthetic live detail") })
                {
                    var before = host.Host.Logs.Failures.Length;
                    var writes = host.Writes.Attempts;
                    var probe = await host.Probe("north", "mcp", _ => throw error,
                        token: cancelled ? aborted.Token : CancellationToken.None,
                        configure: context =>
                        {
                            if (present)
                                context.Features.Set(server);
                        });
                    var silent = cancelled && present && error is OperationCanceledException or IOException;
                    Assert.Equal(before + (silent ? 0 : 1), host.Host.Logs.Failures.Length);
                    Assert.Equal(writes + (cancelled ? 0 : 1), host.Writes.Attempts);
                    if (cancelled)
                        Assert.Empty(probe.Output.ToArray());
                    else
                        HostPolicyAssert.Envelope(probe.Reply, ErrorEnvelope.Unexpected, status: 500);
                    if (!silent)
                    {
                        var line = Assert.Single(host.Host.Logs.Failures.Skip(before));
                        HostPolicyAssert.Line(host.Host.Logs, before, (string)line.Fields["IncidentId"]!, "north", "mcp",
                            ErrorEnvelope.Unexpected with { ExceptionType = error.GetType().Name },
                            level: cancelled ? LogLevel.Warning : LogLevel.Error, ordinary: false);
                        Assert.Equal(error.ToString(), line.Fields["ExceptionText"]);
                    }
                }
    }

    [Fact]
    public async Task T12_caller_gone_before_server()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        var entered = Gate();
        var done = Gate();
        host.Middleware = async (context, _) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            finally
            {
                done.TrySetResult();
            }
        };
        var id = Guid.NewGuid().ToString("N");
        var completed = host.Signals.Expect(id);
        using var request = HostPolicyHost.Request(host.Mcp("north"), "record", true, NamedGatewayMcpWire.LegacyList, id);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = host.Client.SendAsync(request, cancel.Token);
        await Wait(entered.Task);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Wait(done.Task);
        await Wait(completed);
        var line = Assert.Single(host.Host.Logs.Failures);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("UnexpectedError", line.Fields["Code"]);
        Assert.Equal("mcp", line.Fields["Operation"]);
        Assert.Equal("north", line.Scopes["Gateway"]);
        Assert.Equal(0, host.Writes.Attempts);
        using var aborted = new CancellationTokenSource();
        var before = host.Host.Logs.Failures.Length;
        var probe = await host.Probe("south", "mcp", context =>
        {
            Assert.True(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("south")));
            context.Response.StatusCode = 401;
            aborted.Cancel();
            Assert.False(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("south")));
            return Task.CompletedTask;
        }, token: aborted.Token);
        Assert.Empty(probe.Output.ToArray());
        Assert.Equal(before, host.Host.Logs.Failures.Length);
        Assert.Equal(0, host.Writes.Attempts);
        var refused = await host.Probe("south", "mcp", context =>
        {
            Assert.False(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("south")));
            return Task.CompletedTask;
        }, token: aborted.Token);
        Assert.Empty(refused.Output.ToArray());
    }

    [Fact]
    public async Task T13_handler_errors_are_not_logged_twice()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        host.Client.DefaultRequestHeaders.Add("X-Synthetic-User", "synthetic-reader");
        foreach (var name in HostPolicyHost.Names)
        {
            var before = host.Host.Logs.Failures.Length;
            var known = await host.Send(host.Api(name, "describe"), signed: true, body: "{\"source\":\"absent\"}");
            var expected = new CatalogFailure("UnknownSource", "$.source", "Choose an active source. Available sources: synthetic_" + name + ".");
            var incident = HostPolicyAssert.Envelope(known, expected, status: 404);
            var line = Assert.Single(host.Host.Logs.Failures.Skip(before));
            Assert.Equal(incident, line.Fields["IncidentId"]);
            Assert.Equal("describe", line.Fields["Operation"]);
            var engine = host.Engines[name == "north" ? 0 : 1];
            engine.CompileFault = () => new HostPolicyFault("synthetic handler detail");
            before = host.Host.Logs.Failures.Length;
            var unexpected = await host.Send(host.Api(name, "query"), signed: true, body: HostPolicyHost.Body(name, "query"));
            incident = HostPolicyAssert.Envelope(unexpected, ErrorEnvelope.Unexpected, status: 500);
            line = Assert.Single(host.Host.Logs.Failures.Skip(before));
            Assert.Equal(incident, line.Fields["IncidentId"]);
            Assert.Equal("query", line.Fields["Operation"]);
            using var headers = new HostPolicyMcpHeaders();
            using var http = new HttpClient(headers) { BaseAddress = host.Client.BaseAddress };
            var completed = host.Signals.Expect(headers.CallId);
            await using var client = await NamedGatewayMcpWire.Connect(http, host.Mcp(name), null, CancellationToken.None);
            before = host.Host.Logs.Failures.Length;
            var result = await McpWire.Call(client, "query_source", HostPolicyHost.Body(name, "query"));
            Assert.Equal(headers.CallId, await completed.WaitAsync(TimeSpan.FromSeconds(15)));
            incident = NamedGatewayMcpWire.Failed(result, WireGolden.Cases["UnexpectedError/tool"].Text[0], unexpected: true);
            line = Assert.Single(host.Host.Logs.Failures.Skip(before));
            Assert.Equal(incident, line.Fields["IncidentId"]);
            Assert.Equal("query_source", line.Fields["Operation"]);
            Assert.Equal(name, line.Scopes["Gateway"]);
            engine.CompileFault = null;
        }
    }

    [Fact]
    public async Task T14_policy_documents_are_isolated()
    {
        await using var host = new HostPolicyHost();
        await using var baseline = new HostPolicyHost(boundary: false, policy: false);
        await host.Start();
        await baseline.Start();
        var requests = Enumerable.Range(0, 3).SelectMany(_ => HostPolicyHost.Names.Select(name => host.Send(host.Page(name, "openapi/v1.json"), signed: true))).ToArray();
        var documents = await Task.WhenAll(requests);
        for (var i = 0; i < documents.Length; i++)
        {
            var name = HostPolicyHost.Names[i % 2];
            var original = await baseline.Send(baseline.Page(name, "openapi/v1.json"), signed: true);
            var doc = HostPolicyAssert.Json(documents[i].Bytes);
            var plain = HostPolicyAssert.Json(original.Bytes);
            Assert.Equal("A read-only data gateway. " + HostPolicyHost.Description(name), doc.GetProperty("info").GetProperty("description").GetString());
            Assert.Equal(name, doc.GetProperty("info").GetProperty("title").GetString());
            Assert.Equal(new[] { "/" + name + "/query", "/" + name + "/sources/describe", "/" + name + "/sources/list" }, doc.GetProperty("paths").EnumerateObject().Select(path => path.Name).Order(StringComparer.Ordinal));
            foreach (var path in doc.GetProperty("paths").EnumerateObject())
            {
                var operation = path.Value.GetProperty("post");
                var operationName = path.Name.EndsWith("/list", StringComparison.Ordinal) ? "list" : path.Name.EndsWith("/describe", StringComparison.Ordinal) ? "describe" : "query";
                Assert.Equal(operationName, operation.GetProperty("operationId").GetString());
                var responses = operation.GetProperty("responses");
                var refusal = responses.GetProperty("401");
                Assert.Equal(HostPolicyHost.ResponseDescription(name), refusal.GetProperty("description").GetString());
                var schema = refusal.GetProperty("content").GetProperty("application/json").GetProperty("schema");
                Assert.Equal(new[] { "code", "path", "message", "incidentId" }, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
                Assert.Equal(new[] { "code", "path", "message" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal("^[0-9a-f]{32}$", schema.GetProperty("properties").GetProperty("incidentId").GetProperty("pattern").GetString());
                Assert.Equal("NotAuthenticated", Assert.Single(schema.GetProperty("properties").GetProperty("code").GetProperty("enum").EnumerateArray()).GetString());
                var baselineResponses = plain.GetProperty("paths").GetProperty(path.Name).GetProperty("post").GetProperty("responses");
                Assert.Equal(baselineResponses.EnumerateObject().Select(response => response.Name), responses.EnumerateObject().Where(response => response.Name != "401").Select(response => response.Name));
                foreach (var response in baselineResponses.EnumerateObject())
                    Assert.Equal(response.Value.GetRawText(), responses.GetProperty(response.Name).GetRawText());
            }
            Assert.DoesNotContain("\"security\"", Encoding.UTF8.GetString(documents[i].Bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("\"securitySchemes\"", Encoding.UTF8.GetString(documents[i].Bytes), StringComparison.Ordinal);
        }
        var recorded = await host.Send(host.Api("north", "list"), body: "{}");
        HostPolicyAssert.Envelope(recorded, HostPolicyHost.Actual("north"));
        var challenge = await host.Send(host.Api("north", "list"), "challenge", body: "{}");
        HostPolicyAssert.Envelope(challenge, HostPolicyHost.Fallback("north"), recorded: false);
        await using var mixed = new HostPolicyHost(northOnlyPolicy: true);
        await mixed.Start();
        var south = await mixed.Send(mixed.Page("south", "openapi/v1.json"), signed: true);
        var southPlain = await baseline.Send(baseline.Page("south", "openapi/v1.json"), signed: true);
        Assert.Equal(southPlain.Bytes, south.Bytes);
        var north = await mixed.Send(mixed.Page("north", "openapi/v1.json"), signed: true);
        Assert.Equal("A read-only data gateway. North policy applies to every call.", HostPolicyAssert.Json(north.Bytes).GetProperty("info").GetProperty("description").GetString());
        const string exampleMessage = "Sign in with Windows to call DataGateway: send the request with Windows credentials (HTTP Negotiate).";
        const string exampleResponse = "Unauthorized. The code is one of: NotAuthenticated. The first leg of sign-in answers with WWW-Authenticate: Negotiate and no incidentId; a refused handshake adds an incidentId.";
        await using var compatibility = new HostPolicyHost(fallback: _ => new("NotAuthenticated", "$", exampleMessage) { Step = "sign-in" },
            description: "Every call needs Windows sign-in.", responseDescription: exampleResponse);
        await compatibility.Start();
        var compatible = await compatibility.Send(compatibility.Page("north", "openapi/v1.json"), signed: true);
        var compatibleDoc = HostPolicyAssert.Json(compatible.Bytes);
        Assert.Equal("A read-only data gateway. Every call needs Windows sign-in.", compatibleDoc.GetProperty("info").GetProperty("description").GetString());
        foreach (var path in compatibleDoc.GetProperty("paths").EnumerateObject())
            Assert.Equal(exampleResponse, path.Value.GetProperty("post").GetProperty("responses").GetProperty("401").GetProperty("description").GetString());
        var compatibleChallenge = await compatibility.Send(compatibility.Api("north", "list"), "challenge", body: "{}");
        HostPolicyAssert.Envelope(compatibleChallenge, new("NotAuthenticated", "$", exampleMessage), recorded: false);
        Assert.Equal(exampleMessage, HostPolicyAssert.Json(compatibleChallenge.Bytes).GetProperty("message").GetString());
    }

    [Fact]
    public void T15_policy_configuration_contract()
    {
        Assert.Equal("app", Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.UseDataGatewayErrors(null!)).ParamName);
        Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.TryRecordDataGatewayRefusal(null!, HostPolicyHost.Actual("north"))).ParamName);
        Assert.Equal("failure", Assert.Throws<ArgumentNullException>(() => new DefaultHttpContext().TryRecordDataGatewayRefusal(null!)).ParamName);
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var valid = HostPolicyHost.Fallback("north");
        foreach (var parameter in new[] { "failure", "description", "responseDescription" })
        {
            var services = new ServiceCollection();
            DataGatewayBuilder? captured = null;
            var error = Assert.Throws<ArgumentNullException>(() => services.AddDataGateway("north", gateway =>
            {
                captured = gateway;
                gateway.UseHostPolicy(parameter == "failure" ? null! : valid, parameter == "description" ? null! : "sentence", parameter == "responseDescription" ? null! : "reply");
            }));
            Assert.Equal(parameter, error.ParamName);
            Assert.Empty(services);
            Assert.Equal("Gateway 'north'. Registration is already complete.", Assert.Throws<InvalidOperationException>(() => captured!.UseHostPolicy(valid, "text", "reply")).Message);
        }
        var cases = new List<(CatalogFailure Failure, string Description, string Response, string Reason)>();
        foreach (var code in new[] { "UnknownSource", "made-up-code", "QueryCancelled", "CatalogCancelled", "InternalError" })
            cases.Add((valid with { Code = code }, "text", "reply", "Policy failure must have a client-visible 401 code."));
        foreach (var blank in new[] { "", " \t\r\n" })
        {
            cases.Add((valid, blank, "reply", "Policy description must contain a non-whitespace character."));
            cases.Add((valid, "text", blank, "Policy response description must contain a non-whitespace character."));
        }
        foreach (var item in cases)
        {
            var services = new ServiceCollection();
            DataGatewayBuilder? captured = null;
            var error = Assert.Throws<InvalidOperationException>(() => services.AddDataGateway("north", gateway =>
            {
                captured = gateway;
                gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine()).UseHostPolicy(item.Failure, item.Description, item.Response);
            }));
            Assert.Equal("Gateway 'north'. " + item.Reason, error.Message);
            Assert.Empty(services);
            Assert.Equal("Gateway 'north'. Registration is already complete.", Assert.Throws<InvalidOperationException>(() => captured!.UseHostPolicy(valid, "text", "reply")).Message);
        }
        var configured = new ServiceCollection();
        DataGatewayBuilder? frozen = null;
        configured.AddDataGateway("north", gateway =>
        {
            frozen = gateway;
            gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine());
            gateway.UseHostPolicy(valid with { Code = "unknown" }, "", "");
            Assert.Same(gateway, gateway.UseHostPolicy(valid, "  retained text\t", " retained reply "));
        });
        Assert.Equal("Gateway 'north'. Registration is already complete.", Assert.Throws<InvalidOperationException>(() => frozen!.UseHostPolicy(valid, "text", "reply")).Message);
        var north = (GatewayRegistration)configured.Single(item => item.ServiceType == typeof(GatewayRegistration)).ImplementationInstance!;
        Assert.Equal("  retained text\t", north.HostPolicy!.Description);
        Assert.Equal(" retained reply ", north.HostPolicy.ResponseDescription);
        configured.AddDataGateway("south", gateway => gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine()).UseHostPolicy(HostPolicyHost.Fallback("south"), "South text", "South reply"));
        Assert.Equal("  retained text\t", north.HostPolicy.Description);
        Assert.Equal("North credentials are required.", north.HostPolicy.Failure.Message);
        Assert.Equal(2, configured.Count(item => item.ServiceType == typeof(GatewayRegistration)));
    }

    [Fact]
    public async Task T16_no_opt_in_preserves_behavior()
    {
        await using var baseline = new HostPolicyHost(boundary: false, policy: false);
        await using var boundary = new HostPolicyHost(policy: false);
        await baseline.Start();
        await boundary.Start();
        foreach (var name in HostPolicyHost.Names)
        {
            foreach (var operation in HostPolicyHost.Operations)
            {
                var a = await baseline.Send(baseline.Api(name, operation), signed: true, body: HostPolicyHost.Body(name, operation));
                var b = await boundary.Send(boundary.Api(name, operation), signed: true, body: HostPolicyHost.Body(name, operation));
                Assert.Equal(200, a.Status);
                Assert.Equal(a.Bytes, b.Bytes);
            }
            var mcpA = await baseline.SendMcp(name, signed: true);
            var mcpB = await boundary.SendMcp(name, signed: true);
            Assert.Equal(200, mcpA.Status);
            Assert.Equal(mcpA.Bytes, mcpB.Bytes);
            foreach (var suffix in HostPolicyHost.Pages)
            {
                var a = await baseline.Send(baseline.Page(name, suffix), signed: true);
                var b = await boundary.Send(boundary.Page(name, suffix), signed: true);
                Assert.Equal(a.Status, b.Status);
                Assert.Equal(a.Bytes, b.Bytes);
            }
            var failedA = await baseline.Send(baseline.Api(name, "list"), signed: true, body: "[]");
            var failedB = await boundary.Send(boundary.Api(name, "list"), signed: true, body: "[]");
            Assert.Equal(NamedGatewayMcpWire.Normalize(Encoding.UTF8.GetString(failedA.Bytes), out _), NamedGatewayMcpWire.Normalize(Encoding.UTF8.GetString(failedB.Bytes), out _));
            Assert.Equal(400, failedA.Status);
            Assert.Equal(failedA.Status, failedB.Status);
            Assert.Equal("InvalidStructure", HostPolicyAssert.Json(failedA.Bytes).GetProperty("code").GetString());
            HostPolicyAssert.Bare(await baseline.Send(baseline.Api(name, "list"), mode: "challenge", body: "{}"));
            Assert.Empty(baseline.Signals.Accepted);
            HostPolicyAssert.Bare(await boundary.Send(boundary.Api(name, "list"), "challenge", body: "{}"));
            var recorded = await boundary.Send(boundary.Api(name, "list"), body: "{}");
            HostPolicyAssert.Envelope(recorded, HostPolicyHost.Actual(name));
        }
        await using var policyOnly = new HostPolicyHost(boundary: false);
        await policyOnly.Start();
        var bare = await policyOnly.Send(policyOnly.Api("north", "list"), body: "{}");
        HostPolicyAssert.Bare(bare);
        Assert.False(policyOnly.Signals.Accepted[bare.Call][0]);
        var doc = await policyOnly.Send(policyOnly.Page("north", "openapi/v1.json"), signed: true);
        Assert.Equal("A read-only data gateway. North policy applies to every call.", HostPolicyAssert.Json(doc.Bytes).GetProperty("info").GetProperty("description").GetString());
        await using var open = new HostPolicyHost(protect: false);
        await open.Start();
        foreach (var name in HostPolicyHost.Names)
        {
            var list = await open.Send(open.Api(name, "list"), body: "{}");
            Assert.Equal(200, list.Status);
            Assert.Equal("synthetic_" + name, Assert.Single(HostPolicyAssert.Json(list.Bytes).GetProperty("sources").EnumerateArray()).GetProperty("name").GetString());
            var query = await open.Send(open.Api(name, "query"), body: HostPolicyHost.Body(name, "query"));
            Assert.Equal(200, query.Status);
            Assert.Equal(name == "north" ? 1 : 11, HostPolicyAssert.Json(query.Bytes).GetProperty("rows")[0].GetProperty("id").GetInt32());
        }
        Assert.Empty(open.Host.Logs.Failures);
    }

    [Fact]
    public async Task T17_public_category_and_diagnostic_fields()
    {
        const string marker = "synthetic-token-private-marker";
        const string connection = "synthetic-connection-private-marker";
        await using var host = new HostPolicyHost(fallback: name => HostPolicyHost.Fallback(name) with { DriverException = new HostPolicyFault(marker + connection) },
            logCategory: DataGatewayExtensions.FailureLogCategory);
        await host.Start();
        Assert.Equal("DataGateway.Failure", DataGatewayExtensions.FailureLogCategory);
        host.Client.DefaultRequestHeaders.Add("X-Private-Marker", marker);
        host.Client.DefaultRequestHeaders.Add("X-Private-Connection", connection);
        host.Signals.Recorded = name => HostPolicyHost.Actual(name) with { DriverException = new HostPolicyFault(marker + connection) };
        var response = await host.Send(host.Api("north", "list"), body: "{}");
        Assert.Single(host.Host.Logs.Events);
        var incident = HostPolicyAssert.Envelope(response, HostPolicyHost.Actual("north"));
        var line = HostPolicyAssert.Line(host.Host.Logs, 0, incident!, "north", "list", HostPolicyHost.Actual("north"));
        Assert.Equal("NorthPolicyFault", line.Fields["ExceptionType"]);
        Assert.Null(line.Fields["DriverMessage"]);
        Assert.Null(line.Fields["ExceptionText"]);
        foreach (var value in new[] { marker, connection })
        {
            Assert.DoesNotContain(value, Encoding.UTF8.GetString(response.Bytes), StringComparison.Ordinal);
            foreach (var refusal in host.Host.Logs.Failures)
                Assert.DoesNotContain(value, JsonSerializer.Serialize(refusal), StringComparison.Ordinal);
        }
        var challenge = await host.Send(host.Api("north", "list"), "challenge", body: "{}");
        HostPolicyAssert.Envelope(challenge, HostPolicyHost.Fallback("north"), recorded: false);
        Assert.Single(host.Host.Logs.Failures);
        var scalarFailure = HostPolicyHost.Actual("south") with
        {
            Path = "$.hostPolicy",
            Step = "host-check",
            SourceId = 77,
            SourceName = "synthetic diagnostic source",
            SqlNumber = 412,
            DriverException = new HostPolicyFault(marker + connection)
        };
        host.Signals.Recorded = _ => scalarFailure;
        var scalarResponse = await host.Send(host.Api("south", "list"), body: "{}");
        HostPolicyAssert.Envelope(scalarResponse, scalarFailure);
        var scalarLine = Assert.Single(host.Host.Logs.Failures.Skip(1));
        Assert.Equal(77, scalarLine.Fields["SourceId"]);
        Assert.Equal("synthetic diagnostic source", scalarLine.Fields["SourceName"]);
        Assert.Equal(412, scalarLine.Fields["SqlNumber"]);
        Assert.Equal("$.hostPolicy", scalarLine.Fields["Path"]);
        Assert.Equal("host-check", scalarLine.Fields["Step"]);
        Assert.Equal("SouthPolicyFault", scalarLine.Fields["ExceptionType"]);
        Assert.Null(scalarLine.Fields["DriverMessage"]);
        Assert.Null(scalarLine.Fields["ExceptionText"]);
        foreach (var value in new[] { marker, connection })
            Assert.DoesNotContain(value, JsonSerializer.Serialize(scalarLine), StringComparison.Ordinal);
        var error = new HostPolicyFault("synthetic unexpected private detail");
        host.Middleware = (_, _) => throw error;
        var fault = await host.Send(host.Api("north", "list"), body: "{}");
        incident = HostPolicyAssert.Envelope(fault, ErrorEnvelope.Unexpected, status: 500);
        line = HostPolicyAssert.Line(host.Host.Logs, 2, incident!, "north", "list", ErrorEnvelope.Unexpected with { ExceptionType = "HostPolicyFault" }, ordinary: false);
        Assert.Equal(error.ToString(), line.Fields["ExceptionText"]);
        Assert.Equal(ErrorEnvelope.UnexpectedErrorMessage, line.Fields["Message"]);
        Assert.DoesNotContain(error.Message, Encoding.UTF8.GetString(fault.Bytes), StringComparison.Ordinal);
        Assert.Equal(new[] { "NotAuthenticated", "NotAuthenticated", "UnexpectedError" },
            host.Host.Logs.Events.Select(item => Assert.IsType<string>(item.Fields["Code"])));
        Assert.All(host.Host.Logs.Events, item => Assert.Equal(DataGatewayExtensions.FailureLogCategory, item.Category));
    }

    [Fact]
    public async Task T18_concurrent_gateways_do_not_share_state()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        var allEntered = Gate();
        var release = Gate();
        var entered = 0;
        host.Middleware = async (context, _) =>
        {
            if (Interlocked.Increment(ref entered) == 4)
                allEntered.TrySetResult();
            await Wait(release.Task);
            var name = context.Request.Path.ToString().Contains("south", StringComparison.Ordinal) ? "south" : "north";
            var caller = HostPolicyHost.Title(name) + "Caller";
            context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, caller)], "SyntheticPolicyCheck"));
            if (context.Request.Headers["X-Policy-Mode"] == "record")
                Assert.True(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual(name)));
            else
                context.Response.Headers.WWWAuthenticate = "Synthetic challenge-leg";
            context.Response.StatusCode = 401;
        };
        var calls = HostPolicyHost.Names.SelectMany(name => new[] { "record", "challenge" }.Select(mode => (Name: name, Mode: mode))).ToArray();
        var tasks = calls.Select(call => host.Send(host.Api(call.Name, "list"), call.Mode, body: "{}")).ToArray();
        await Wait(allEntered.Task);
        release.TrySetResult();
        var responses = await Task.WhenAll(tasks);
        var incidents = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < responses.Length; i++)
        {
            var call = calls[i];
            var recorded = call.Mode == "record";
            var incident = HostPolicyAssert.Envelope(responses[i], recorded ? HostPolicyHost.Actual(call.Name) : HostPolicyHost.Fallback(call.Name), recorded);
            if (recorded)
            {
                Assert.True(incidents.Add(incident!));
                var line = Assert.Single(host.Host.Logs.Failures, item => Equals(item.Fields["IncidentId"], incident));
                Assert.Equal(call.Name, line.Scopes["Gateway"]);
                Assert.Equal(HostPolicyHost.Title(call.Name) + "Caller", line.Fields["Caller"]);
                Assert.Equal("list", line.Fields["Operation"]);
                Assert.Equal(HostPolicyHost.Actual(call.Name).Message, line.Fields["Message"]);
                Assert.Equal("sign-in", line.Fields["Step"]);
                Assert.Equal(HostPolicyHost.Title(call.Name) + "PolicyFault", line.Fields["ExceptionType"]);
            }
            else
                Assert.Equal("Synthetic challenge-leg", responses[i].Headers["WWW-Authenticate"]);
        }
        Assert.Equal(2, host.Host.Logs.Failures.Length);
        Assert.Equal(2, incidents.Count);
        host.Middleware = null;
        var neighbor = await host.Send(host.Root + "/scope", signed: true);
        Assert.Equal(200, neighbor.Status);
        var neighborLine = Assert.Single(host.Host.Logs.Events, item => item.Category == "HostPolicy.Neighbor");
        Assert.False(neighborLine.Scopes.ContainsKey("Gateway"));
        await host.Witnesses();
    }

    [Fact]
    public async Task T19_failed_refusal_write_does_not_log_again()
    {
        await using var host = new HostPolicyHost();
        await host.Start();
        host.Writes.Throw = true;
        var original = Console.Error;
        using var emergency = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        HostPolicyProbe probe;
        try
        {
            Console.SetError(emergency);
            probe = await host.Probe("north", "list", context =>
            {
                Assert.True(context.TryRecordDataGatewayRefusal(HostPolicyHost.Actual("north")));
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            });
        }
        finally
        {
            Console.SetError(original);
        }
        Assert.Equal(1, host.Writes.Attempts);
        Assert.Equal(1, probe.Lifetime.Aborts);
        Assert.Empty(probe.Output.ToArray());
        var line = Assert.Single(host.Host.Logs.Failures);
        var incident = (string)line.Fields["IncidentId"]!;
        Assert.Matches("^[0-9a-f]{32}$", incident);
        HostPolicyAssert.Line(host.Host.Logs, 0, incident, "north", "list", HostPolicyHost.Actual("north"));
        Assert.DoesNotContain(host.Host.Logs.Failures, item => Equals(item.Fields["Code"], "UnexpectedError"));
        Assert.Equal($"DataGateway.Failure: incident {incident}: the error reply could not be sent (DataGateway.Web.Tests.HostPolicyFault); the failure was DataGateway.Web.Tests.HostPolicyFault.{Environment.NewLine}", emergency.ToString());
        host.Writes.Throw = false;
        var control = await host.Send(host.Api("north", "list"), body: "{}");
        HostPolicyAssert.Envelope(control, HostPolicyHost.Actual("north"));
        Assert.Equal(2, host.Writes.Attempts);
    }

    [Fact]
    public async Task T20_recorded_code_controls_status()
    {
        await using var host = new HostPolicyHost(policy: false);
        await host.Start();
        foreach (var (code, returned, status, message) in new[]
        {
            ("UnknownSource", "UnknownSource", 404, "Synthetic source is absent."),
            ("QueryTimeout", "QueryTimeout", 504, "Synthetic policy lookup timed out."),
            ("unknown-internal-policy-code", "InternalError", 500, "Synthetic private diagnostic only.")
        })
        {
            var failure = new CatalogFailure(code, "$.policy", message) { Step = "policy", ExceptionType = "PolicyLookupFault" };
            host.Signals.Recorded = _ => failure;
            var before = host.Host.Logs.Failures.Length;
            var response = await host.Send(host.Api("north", "list"), body: "{}");
            var incident = HostPolicyAssert.Envelope(response, failure, status: status);
            var line = HostPolicyAssert.Line(host.Host.Logs, before, incident!, "north", "list", failure);
            Assert.Equal(code, line.Fields["Code"]);
            Assert.Equal(returned, line.Fields["ReturnedCode"]);
            Assert.Equal(returned, HostPolicyAssert.Json(response.Bytes).GetProperty("code").GetString());
            Assert.Equal(code == returned ? message : ErrorEnvelope.InternalErrorMessage, HostPolicyAssert.Json(response.Bytes).GetProperty("message").GetString());
            if (code != returned)
            {
                Assert.Equal("$", HostPolicyAssert.Json(response.Bytes).GetProperty("path").GetString());
                Assert.DoesNotContain(message, Encoding.UTF8.GetString(response.Bytes), StringComparison.Ordinal);
                Assert.Equal(message, line.Fields["Message"]);
            }
        }
        host.NoWork();
    }
}
