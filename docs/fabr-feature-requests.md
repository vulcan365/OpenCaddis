# Fabr Feature Requests

## 1. AddFabrServer / UseFabrServer should auto-register API controllers

**Status:** Workaround applied in OpenCaddis

### Problem

Calling `builder.AddFabrServer()` and `app.UseFabrServer()` does not register or map the Fabr API controllers (`ModelConfigController`, `AgentController`, `FileController`, `DiagnosticsController`, `EmbeddingsController`). This causes `FabrChatClientService` to fail at runtime when it calls `GET /fabrapi/ModelConfig/model/{name}` — no controller is listening.

```
System.Exception: Failed to get model configuration for 'default': NotFound
   at Fabr.Sdk.FabrChatClientService.GetModelConfiguration()
```

### Root Cause

- `AddFabrServer()` does not call `AddControllers()` or `AddApplicationPart(typeof(FabrServerOptions).Assembly)`
- `UseFabrServer()` does not call `MapControllers()`
- `Demo.Server` works because it explicitly calls both — but this isn't documented

### Current Workaround

Consuming app must add:

```csharp
builder.Services.AddControllers()
    .AddApplicationPart(typeof(FabrServerOptions).Assembly);

app.MapControllers();
```

### Expected Behavior

`AddFabrServer()` and `UseFabrServer()` should handle this automatically. The Fabr host's own `FabrChatClientService` depends on these endpoints, so they should be wired up by the same extension methods that register the service.

---

## 2. Discovery API for Agent Types, Plugins, and Tools

**Status:** Blocking — no workaround without duplicating internal Fabr reflection logic

### Problem

There is no way for a consuming application to discover which agent types, plugins, and tools are registered in the system at runtime. The only discovery mechanism is the internal reflection scan in `AgentGrain.CreateAgent()`, which is not exposed.

OpenCaddis has a setup page where users configure agents with:
- **Agent Type** — needs a dropdown of available types/aliases
- **Plugins** — needs a dropdown of available plugins
- **Tools** — needs a dropdown of available tools

All three are currently free-text fields because there is no API to populate them.

### What We Need

#### REST API

A combined endpoint on the existing `DiagnosticsController`:

```
GET /fabrapi/diagnostics/registry
```

Response:

```json
{
  "agentTypes": [
    {
      "typeName": "OpenCaddis.Agentic.Agents.AssistantAgent",
      "aliases": ["AssistantAgent", "assistant"]
    }
  ],
  "plugins": [
    {
      "typeName": "Fabr.Sdk.Plugins.WebSearchPlugin",
      "aliases": ["web-search"]
    }
  ],
  "tools": [
    {
      "typeName": "Fabr.Sdk.Tools.FileReadTool",
      "aliases": ["file-read"]
    }
  ]
}
```

Separate endpoints would also work:

```
GET /fabrapi/diagnostics/agent-types
GET /fabrapi/diagnostics/plugins
GET /fabrapi/diagnostics/tools
```

#### DI Service

For Blazor Server apps running in the same process (no HTTP round-trip needed):

```csharp
public interface IFabrRegistry
{
    List<RegistryEntry> GetAgentTypes();
    List<RegistryEntry> GetPlugins();
    List<RegistryEntry> GetTools();
}

public class RegistryEntry
{
    public string TypeName { get; set; }
    public List<string> Aliases { get; set; }
}
```

Registered as a singleton via `AddFabrServer()`.

### Implementation Notes

- The reflection pattern already exists in `AgentGrain.CreateAgent()` — scan `AppDomain.CurrentDomain.GetAssemblies()` for types decorated with `[AgentAlias]` and the equivalent attributes for plugins and tools
- Must include assemblies from `FabrServerOptions.AdditionalAssemblies`
- Results should be cached since loaded assemblies don't change at runtime
- Case-insensitive alias matching should be preserved
- Fits naturally alongside `GET /fabrapi/diagnostics/agents` (running instances) — this new endpoint covers "what's available to create"

### Why This Should Be in Fabr

Without this API, every consuming app that builds a configuration UI has to:

1. Know the exact reflection pattern Fabr uses internally
2. Know which attributes to scan for (agents, plugins, tools may each use different attributes)
3. Hope the discovery logic doesn't change between Fabr versions

Fabr knows how it discovers types — it should expose that knowledge rather than forcing consumers to reverse-engineer it.

---

## 3. IFabrAgentHost is not available to plugins via IServiceProvider

**Status:** Bug — affects all plugins that need to send messages, register timers, or interact with the agent runtime

### Problem

Plugins that implement `IFabrPlugin` receive an `IServiceProvider` during `InitializeAsync`, but `IFabrAgentHost` is never registered in that service provider. Calling `serviceProvider.GetService<IFabrAgentHost>()` always returns `null`.

This means plugins cannot:
- Send messages (to self or other agents)
- Register or unregister timers
- Register or unregister reminders
- Access the agent's handle
- Access chat history or custom state

Any plugin tool that depends on `IFabrAgentHost` silently fails at runtime.

### Root Cause

In `AgentGrain.ConfigureAgent()`, the grain (which implements `IFabrAgentHost`) passes itself directly to the agent proxy constructor:

```csharp
// AgentGrain.cs ~line 245
_agentProxy = (IFabrAgentProxy)ActivatorUtilities.CreateInstance(
    serviceProvider, agentType, config, this);  // 'this' is IFabrAgentHost
```

The agent proxy receives `IFabrAgentHost` as a constructor parameter, but it is never registered in the `IServiceProvider` that gets passed to plugins during `ResolveToolsAsync()` → `InitializeAsync()`.

**Initialization sequence:**

1. `AgentGrain.ConfigureAgent()` starts
2. Agent proxy is created, receiving `IFabrAgentHost` (the grain) via constructor injection
3. Proxy's `OnInitialize()` calls `ResolveConfiguredToolsAsync()`
4. `FabrToolRegistry.ResolveToolsAsync()` instantiates plugins and calls `InitializeAsync(config, serviceProvider)`
5. Plugin calls `serviceProvider.GetService<IFabrAgentHost>()` → **returns null**
6. Agent proxy finishes initialization

Plugins are initialized in step 4, but `IFabrAgentHost` is only available as a constructor arg in step 2 — it's never added to the DI container.

### Affected Code

The built-in `ReminderPlugin` pattern (from the Fabr docs/examples) does not work:

```csharp
public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
{
    _host = serviceProvider.GetService<IFabrAgentHost>();  // Always null
    return Task.CompletedTask;
}
```

Any tool method that checks `if (_host is null)` will return an error at runtime.

### Proposed Fix

Register `IFabrAgentHost` in the service provider before plugins are initialized. Options:

**Option A (minimal):** In `AgentGrain.ConfigureAgent()`, create a scoped service provider that includes the host before passing it to the proxy:

```csharp
var pluginServices = new ServiceCollection()
    .AddSingleton<IFabrAgentHost>(this)
    .BuildServiceProvider(/* wrapping existing provider */);
```

**Option B (cleanest):** Have `FabrToolRegistry.ResolveToolsAsync()` accept an `IFabrAgentHost` parameter and pass it to plugins. Add an overload or new interface:

```csharp
public interface IFabrPlugin
{
    Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider);
}

// New — backward compatible
public interface IFabrHostAwarePlugin : IFabrPlugin
{
    void SetHost(IFabrAgentHost host);
}
```

The tool registry would check for `IFabrHostAwarePlugin` after init and call `SetHost()`.

**Option C (simplest):** Register the grain as `IFabrAgentHost` in the Orleans silo's DI container as a scoped or transient service that resolves from the grain context.

### Why This Should Be Fixed in Fabr

- The `ReminderPlugin` example in the Fabr codebase uses this exact pattern, so it's the documented approach
- Plugins are the primary extensibility point for adding agent capabilities — many useful plugins need to send messages (file watchers, webhooks, scheduled tasks, event-driven tools)
- There is no workaround from the consuming application — the service provider is constructed internally by Fabr

### Reproduction

1. Create any plugin that calls `serviceProvider.GetService<IFabrAgentHost>()` in `InitializeAsync`
2. Use the stored `_host` in a tool method
3. `_host` is always `null`

Observed with Fabr 1.2.48-beta on .NET 10.

---

## Environment

- Fabr.Host / Fabr.Client / Fabr.Sdk: 1.2.48-beta
- Runtime: .NET 10
- App type: Blazor Server (not a Web API project)
