using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.NodeIngress.Tests;

/// <summary>
/// HTTP through an ingress tunnel, end to end: a request enters a port on the panel and comes out of
/// a socket on the node, over the connection the node dialled out.
///
/// <para>
/// This is the test that says the feature works. Everything in it is the production article — the
/// real gateway on a real port, real mutually-authenticated TLS against the panel's own CA, the real
/// framing over a real socket, the real target check on the node, and a real HTTP server at the far
/// end. What is being verified is not that the classes agree with their interfaces but that the two
/// codebases agree with each other.
/// </para>
/// </summary>
public sealed class IngressEndToEndTests : IAsyncLifetime
{
    private readonly IngressHarness _harness = new();

    public async Task InitializeAsync() => await _harness.StartPanelAsync();

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    // --- the path itself ---

    [Fact]
    public async Task A_request_reaches_the_app_and_its_answer_comes_back()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "hello from the node"));
        await _harness.StartNodeAsync("node-1", app.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", app.Port));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("hello from the node");
        app.Served.Should().Be(1);
    }

    [Fact]
    public async Task The_method_path_headers_and_body_all_survive_the_crossing()
    {
        HttpRequestLine? seen = null;

        var app = _harness.StartApp(request =>
        {
            seen = request;
            return new OriginResponse(Body: "ok");
        });

        await _harness.StartNodeAsync("node-1", app.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", app.Port));

        var request = new HttpRequestMessage(HttpMethod.Post, "/orders?id=42")
        {
            Content = new StringContent("{\"item\":\"cheese\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        await client.SendAsync(request);

        // A tunnel that quietly dropped a header or rewrote a path would still pass a "did it
        // return 200" test, and would break every app that reads one.
        seen.Should().NotBeNull();
        seen!.Method.Should().Be("POST");
        seen.Path.Should().Be("/orders?id=42");
        seen.Body.Should().Be("{\"item\":\"cheese\"}");
        seen.Headers.Should().ContainKey("X-Forwarded-For").WhoseValue.Should().Be("203.0.113.7");
    }

    /// <summary>
    /// Bigger than one frame, so the response has to be split and reassembled. A framing bug that a
    /// short body hides shows up here as a truncated or corrupted payload rather than as a crash.
    /// </summary>
    [Fact]
    public async Task A_response_larger_than_one_frame_arrives_whole_and_in_order()
    {
        var size = TunnelFraming.MaxPayloadBytes * 3 + 1237;
        var body = string.Create(size, size, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('a' + i % 26);
        });

        var app = _harness.StartApp(_ => new OriginResponse(Body: body));
        await _harness.StartNodeAsync("node-1", app.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", app.Port));

        var received = await client.GetStringAsync("/big");

        received.Length.Should().Be(size);
        received.Should().Be(body);
    }

    [Fact]
    public async Task A_request_body_larger_than_one_frame_arrives_whole()
    {
        var body = new string('x', TunnelFraming.MaxPayloadBytes * 2 + 99);
        var receivedLength = 0;

        var app = _harness.StartApp(request =>
        {
            receivedLength = request.Body.Length;
            return new OriginResponse(Body: request.Body.Length.ToString());
        });

        await _harness.StartNodeAsync("node-1", app.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", app.Port));

        await client.PostAsync("/upload", new StringContent(body));

        receivedLength.Should().Be(body.Length);
    }

    /// <summary>
    /// One connection, many conversations. Streams are keyed by id, and an off-by-one would send one
    /// visitor's page to another — which is the kind of bug that only appears under load.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_are_multiplexed_without_crossing_over()
    {
        var app = _harness.StartApp(request => new OriginResponse(Body: $"answer for {request.Path}"));
        await _harness.StartNodeAsync("node-1", app.Port);

        var panelPort = _harness.Bind("node-1", app.Port);

        var answers = await Task.WhenAll(Enumerable.Range(0, 25).Select(async i =>
        {
            using var client = IngressHarness.ClientFor(panelPort);
            return (Sent: i, Got: await client.GetStringAsync($"/page-{i}"));
        }));

        foreach (var (sent, got) in answers)
            got.Should().Be($"answer for /page-{sent}");

        app.Served.Should().Be(25);
    }

    [Fact]
    public async Task Several_apps_on_one_node_each_get_their_own_panel_port()
    {
        var shop = _harness.StartApp(_ => new OriginResponse(Body: "shop"));
        var blog = _harness.StartApp(_ => new OriginResponse(Body: "blog"));

        await _harness.StartNodeAsync("node-1", shop.Port, blog.Port);

        using var toShop = IngressHarness.ClientFor(_harness.Bind("node-1", shop.Port));
        using var toBlog = IngressHarness.ClientFor(_harness.Bind("node-1", blog.Port));

        (await toShop.GetStringAsync("/")).Should().Be("shop");
        (await toBlog.GetStringAsync("/")).Should().Be("blog");
    }

    /// <summary>
    /// Two nodes, one gateway. A listener bound for one must never reach the other's machine, or a
    /// tunnel would be a way into somebody else's server.
    /// </summary>
    [Fact]
    public async Task One_nodes_listener_never_reaches_another_nodes_app()
    {
        var first = _harness.StartApp(_ => new OriginResponse(Body: "node-1's app"));
        var second = _harness.StartApp(_ => new OriginResponse(Body: "node-2's app"));

        await _harness.StartNodeAsync("node-1", first.Port);
        await _harness.StartNodeAsync("node-2", second.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", first.Port));

        (await client.GetStringAsync("/")).Should().Be("node-1's app");

        // Node 1 does not publish node 2's port, so naming it reaches nothing — even though the
        // app is listening on this same machine and the gateway knows the number.
        using var crossing = IngressHarness.ClientFor(_harness.Bind("node-1", second.Port));

        await Refused(crossing);
        second.Served.Should().Be(0);
    }

    // --- what the node refuses ---

    /// <summary>
    /// The security boundary, exercised through the whole stack rather than against the resolver.
    /// If this failed, the tunnel would be a port-forward into the customer's private network.
    /// </summary>
    [Fact]
    public async Task A_port_the_node_does_not_publish_is_refused()
    {
        var published = _harness.StartApp(_ => new OriginResponse(Body: "published"));
        var unpublished = _harness.StartApp(_ => new OriginResponse(Body: "should never be reached"));

        // Only the first is registered as a workload port.
        await _harness.StartNodeAsync("node-1", published.Port);

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", unpublished.Port));

        await Refused(client);

        unpublished.Served.Should().Be(0, "the node refuses to dial a port it did not publish");
        published.Served.Should().Be(0);
    }

    [Fact]
    public async Task A_port_stops_being_reachable_once_its_workload_is_deleted()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "still here"));
        var node = await _harness.StartNodeAsync("node-1", app.Port);

        var panelPort = _harness.Bind("node-1", app.Port);

        using (var before = IngressHarness.ClientFor(panelPort))
            (await before.GetStringAsync("/")).Should().Be("still here");

        node.Workloads.Remove($"w-{app.Port}");

        // No withdrawal step to forget: what the tunnel can reach is derived from what is deployed.
        using var after = IngressHarness.ClientFor(panelPort);
        await Refused(after);
    }

    // --- when things break ---

    [Fact]
    public async Task A_port_bound_before_the_node_connects_refuses_rather_than_hangs()
    {
        // The panel binds from its reservations at startup, before any node has dialled in. A
        // request in that window must fail fast: a proxy reads a refusal as "upstream is down", and
        // a held connection as an app that hangs.
        var panelPort = _harness.Bind("node-never-arrives", 30000);

        using var client = IngressHarness.ClientFor(panelPort);

        await Refused(client);
    }

    [Fact]
    public async Task Losing_the_tunnel_makes_the_port_refuse_and_regaining_it_restores_service()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "served"));
        var node = await _harness.StartNodeAsync("node-1", app.Port);

        var panelPort = _harness.Bind("node-1", app.Port);

        using (var before = IngressHarness.ClientFor(panelPort))
            (await before.GetStringAsync("/")).Should().Be("served");

        await node.DisconnectAsync();
        await IngressHarness.WaitUntilAsync(() => !_harness.Ingress.IsConnected("node-1"), "the tunnel to drop");

        using (var during = IngressHarness.ClientFor(panelPort))
            await Refused(during);

        await node.ConnectAsync($"localhost:{_harness.GatewayPort}", CancellationToken.None);
        await IngressHarness.WaitUntilAsync(() => _harness.Ingress.IsConnected("node-1"), "the tunnel to return");

        // The same panel port, because the routes naming it were never rewritten. This is the whole
        // reason listeners survive a tunnel dropping.
        using var after = IngressHarness.ClientFor(panelPort);
        (await after.GetStringAsync("/")).Should().Be("served");
    }

    [Fact]
    public async Task A_reconnecting_node_replaces_its_tunnel_rather_than_adding_a_second()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "served"));
        var node = await _harness.StartNodeAsync("node-1", app.Port);

        await node.ConnectAsync($"localhost:{_harness.GatewayPort}", CancellationToken.None);
        await IngressHarness.WaitUntilAsync(() => _harness.Ingress.IsConnected("node-1"), "the replacement tunnel");

        using var client = IngressHarness.ClientFor(_harness.Bind("node-1", app.Port));

        // If the old socket's teardown had removed the new socket's registration, this would refuse.
        (await client.GetStringAsync("/")).Should().Be("served");
        _harness.Ingress.ActiveTunnels.Should().Be(1);
    }

    [Fact]
    public async Task An_app_that_is_not_listening_fails_the_request_instead_of_hanging()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "briefly here"));
        await _harness.StartNodeAsync("node-1", app.Port);

        var panelPort = _harness.Bind("node-1", app.Port);

        // The port is still published — the node will dial it — but nothing answers any more. That
        // is a crashed container, and it must surface as a failed request.
        app.Dispose();

        using var client = IngressHarness.ClientFor(panelPort);
        await Refused(client);
    }

    // --- who may open a tunnel ---

    /// <summary>
    /// The gateway identifies a node by the certificate it presents, not by the id in its
    /// registration frame. A certificate the panel's CA never signed gets no tunnel.
    /// </summary>
    [Fact]
    public async Task A_certificate_the_panel_never_signed_gets_no_tunnel()
    {
        var connect = async () => await _harness.StartUnknownNodeAsync("impostor");

        await connect.Should().ThrowAsync<Exception>();

        _harness.Ingress.IsConnected("impostor").Should().BeFalse();
    }

    [Fact]
    public async Task A_revoked_node_gets_no_tunnel()
    {
        var app = _harness.StartApp(_ => new OriginResponse(Body: "served"));
        var node = await _harness.StartNodeAsync("node-1", app.Port);

        await node.DisconnectAsync();

        var row = _harness.Db.Nodes.Single(n => n.NodeId == "node-1");
        row.RevokedAt = DateTimeOffset.UtcNow;
        await _harness.Db.SaveChangesAsync();

        var reconnect = async () => await node.ConnectAsync($"localhost:{_harness.GatewayPort}", CancellationToken.None);

        await reconnect.Should().ThrowAsync<InvalidOperationException>();
        _harness.Ingress.IsConnected("node-1").Should().BeFalse();
    }

    // --- helpers ---

    /// <summary>
    /// Assert that a request fails rather than hangs. Which exception depends on where the refusal
    /// came from — a closed listener, a reset stream, an unreachable app — and pinning one would
    /// make the test about the operating system rather than about the tunnel.
    /// </summary>
    private static async Task Refused(HttpClient client)
    {
        var request = async () => await client.GetStringAsync("/");

        var thrown = (await request.Should().ThrowAsync<Exception>()).Which;

        (thrown is HttpRequestException or SocketException or IOException or TaskCanceledException)
            .Should().BeTrue($"a refused request should fail as a transport error, not as {thrown.GetType().Name}");
    }
}
