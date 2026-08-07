using System.Net;
using System.Net.Http.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface.CommandCenter;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCaddis.Server;

namespace OpenCaddis.Server.Tests;

[TestClass]
public sealed class OpenCaddisAgentRegistryServiceTests
{
    [TestMethod]
    public async Task Registry_returns_registered_agents_plugins_tools_and_metadata()
    {
        var response = new DiscoveryResponse
        {
            Agents =
            [
                new DiscoveryRegistryEntry
                {
                    TypeName = "Sample.ResearchAgent",
                    Aliases = ["research-agent"],
                    Description = "Researches a topic.",
                    Capabilities = "Web and document research.",
                    Notes = ["Requires a configured model."]
                }
            ],
            Plugins =
            [
                new DiscoveryRegistryEntry
                {
                    TypeName = "Sample.SearchPlugin",
                    Aliases = ["search"],
                    Methods =
                    [
                        new DiscoveryRegistryMethod
                        {
                            Name = "Find",
                            Description = "Find matching records."
                        }
                    ]
                }
            ],
            Tools =
            [
                new DiscoveryRegistryEntry
                {
                    TypeName = "Sample.UtilityTools.Format",
                    Aliases = ["format"]
                }
            ]
        };
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            Assert.AreEqual(
                "http://localhost:5083/fabrcoreapi/Discovery",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response)
            });
        }));
        var service = new OpenCaddisAgentRegistryService(
            httpClient,
            NullLoggerFactory.Instance);

        var registry = await service.GetRegistryAsync(new Uri("http://localhost:5083/"));

        Assert.HasCount(1, registry.Agents);
        Assert.AreEqual("research-agent", registry.Agents[0].Aliases.Single());
        Assert.AreEqual("Web and document research.", registry.Agents[0].Capabilities);
        Assert.AreEqual("Find", registry.Plugins[0].Methods.Single().Name);
        Assert.AreEqual("format", registry.Tools[0].Aliases.Single());
    }

    [TestMethod]
    public async Task Create_agent_uses_selected_registry_aliases_and_can_add_it_to_surface()
    {
        AgentConfiguration? postedConfiguration = null;
        SurfacePreferences? savedPreferences = null;
        var handler = new DelegateHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/Agent/create", StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual("local-user", request.Headers.GetValues("x-user-handle").Single());
                postedConfiguration = (await request.Content!
                    .ReadFromJsonAsync<List<AgentConfiguration>>())!.Single();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CreateAgentsResponse
                    {
                        TotalRequested = 1,
                        SuccessCount = 1,
                        Results =
                        [
                            new AgentHealthStatus
                            {
                                Handle = "local-user:research-agent-a1b2c3d4",
                                State = HealthState.Healthy,
                                Timestamp = DateTime.UtcNow,
                                IsConfigured = true,
                                AgentType = "research-agent"
                            }
                        ]
                    })
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Put)
            {
                savedPreferences = await request.Content!.ReadFromJsonAsync<SurfacePreferences>();
                Assert.AreEqual("local-user", request.Headers.GetValues("x-user-handle").Single());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler);
        var service = new OpenCaddisAgentRegistryService(
            httpClient,
            NullLoggerFactory.Instance);

        var health = await service.CreateAgentAsync(
            new Uri("http://localhost:5083/"),
            new OpenCaddisAgentCreationOptions
            {
                AgentHandle = "research-agent-a1b2c3d4",
                AgentType = "research-agent",
                ModelName = "default",
                Description = "My research agent",
                SystemPrompt = "Research carefully.",
                Plugins = ["search"],
                Tools = ["format"],
                AddToSurface = true
            });

        Assert.AreEqual("local-user:research-agent-a1b2c3d4", health.Handle);
        Assert.IsNotNull(postedConfiguration);
        // The typed client carries the principal in x-user-handle and posts the bare alias.
        Assert.AreEqual("research-agent-a1b2c3d4", postedConfiguration.Handle);
        Assert.AreEqual("research-agent", postedConfiguration.AgentType);
        Assert.AreEqual("default", postedConfiguration.Models);
        CollectionAssert.AreEqual(new[] { "search" }, postedConfiguration.Plugins);
        CollectionAssert.AreEqual(new[] { "format" }, postedConfiguration.Tools);
        Assert.IsFalse(postedConfiguration.ForceReconfigure);
        Assert.IsNotNull(savedPreferences);
        Assert.Contains(health.Handle, savedPreferences.SurfaceAgentHandles);
    }

    [DataRow("principal:with-prefix", "agent")]
    [DataRow("local-user", "local-user:agent")]
    [TestMethod]
    public async Task Create_agent_rejects_ambiguous_principal_or_agent_handles(
        string principalHandle,
        string agentHandle)
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(_ =>
            throw new AssertFailedException("Invalid handles must fail before an HTTP request.")));
        var service = new OpenCaddisAgentRegistryService(
            httpClient,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAgentAsync(
            new Uri("http://localhost:5083/"),
            new OpenCaddisAgentCreationOptions
            {
                PrincipalHandle = principalHandle,
                AgentHandle = agentHandle,
                AgentType = "research-agent"
            }));
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
