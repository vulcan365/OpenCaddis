# OpenCaddis

OpenCaddis is an AI agent orchestration app built with [Blazor Server](https://learn.microsoft.com/en-us/aspnet/core/blazor/) and the [Fabr](https://github.com/vulcan365/Fabr) agent framework. It provides a web-based chat interface for interacting with multiple AI agents, each equipped with configurable plugins and tools.

## Features

- **Multi-agent chat** -- create and switch between conversations with different agents
- **Thinking agent** -- a plan-then-execute agent that reasons through complex multi-step tasks
- **Plugin system** -- agents gain capabilities through composable plugins
- **Persistent memory** -- vector-based long-term memory across conversations
- **Configurable from the UI** -- model endpoints, plugin settings, and connections managed through the Settings page

## Plugins

| Plugin | Description |
|---|---|
| **FileSystem** | Read, write, list, and search files on disk |
| **WebBrowser** | Fetch web pages, take screenshots (Playwright) |
| **PowerShell** | Execute PowerShell commands and scripts with safety blocklist |
| **TaskManager** | Plan and track multi-step goals with priorities and dependencies |
| **Docker** | Run commands inside Docker containers |
| **Microsoft365Email** | Read, search, and send email via Microsoft Graph |
| **Memory** | Save and search long-term memories using vector embeddings |
| **Reminders** | Schedule one-time or recurring reminders |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An Azure OpenAI (or compatible) endpoint with a deployed chat model and an embeddings model
- (Optional) [PowerShell 7+](https://github.com/PowerShell/PowerShell) for the PowerShell plugin
- (Optional) [Docker](https://www.docker.com/) for the Docker plugin
- (Optional) An Azure AD app registration for Microsoft 365 email access

## Getting Started

1. **Clone the repository**

   ```bash
   git clone https://github.com/vulcan365/OpenCaddis.git
   cd OpenCaddis
   ```

2. **Copy the example config files**

   ```bash
   cp src/OpenCaddis/fabr.json.example src/OpenCaddis/fabr.json
   cp src/OpenCaddis/opencaddis.json.example src/OpenCaddis/opencaddis.json
   ```

3. **Configure your API keys**

   Edit `src/OpenCaddis/fabr.json`:
   - Replace `YOUR_AZURE_ENDPOINT` with your Azure OpenAI resource URL
   - Replace `YOUR_API_KEY` with your API key

4. **Run the app**

   ```bash
   dotnet run --project src/OpenCaddis
   ```

   Open your browser to `https://localhost:7117` (or the URL shown in the console).

## Docker

Run OpenCaddis in a container without installing .NET.

```bash
docker pull vulcan365/opencaddis
```

**Run the container:**

Linux / macOS:
```bash
docker run -d \
  -p 5000:5000 \
  -v opencaddis-data:/app/data \
  -v opencaddis-keys:/app/.keys \
  --name opencaddis \
  vulcan365/opencaddis
```

Windows (PowerShell):
```powershell
docker run -d `
  -p 5000:5000 `
  -v opencaddis-data:/app/data `
  -v opencaddis-keys:/app/.keys `
  --name opencaddis `
  vulcan365/opencaddis
```

Open your browser to `http://localhost:5000` and go to **Settings** to configure your AI model endpoint and API key. The app creates its config files automatically when you save.

To persist configuration across `docker rm`, bind-mount the config files. Create them first on the host:

Linux / macOS:
```bash
cp src/OpenCaddis/fabr.json.example fabr.json
cp src/OpenCaddis/opencaddis.json.example opencaddis.json
```

Windows (PowerShell):
```powershell
copy src\OpenCaddis\fabr.json.example fabr.json
copy src\OpenCaddis\opencaddis.json.example opencaddis.json
```

Then add them to the run command:

Linux / macOS:
```bash
docker run -d \
  -p 5000:5000 \
  -v ./fabr.json:/app/fabr.json \
  -v ./opencaddis.json:/app/opencaddis.json \
  -v opencaddis-data:/app/data \
  -v opencaddis-keys:/app/.keys \
  --name opencaddis \
  vulcan365/opencaddis
```

Windows (PowerShell):
```powershell
docker run -d `
  -p 5000:5000 `
  -v ${PWD}\fabr.json:/app/fabr.json `
  -v ${PWD}\opencaddis.json:/app/opencaddis.json `
  -v opencaddis-data:/app/data `
  -v opencaddis-keys:/app/.keys `
  --name opencaddis `
  vulcan365/opencaddis
```

- `opencaddis-data` persists the SQLite vector database across restarts
- `opencaddis-keys` persists ASP.NET data protection keys

**Working directory for plugins** -- plugins like FileSystem, PowerShell, and TaskManager default to `/tmp/OpenCaddis/` inside the container, which doesn't persist. To give agents access to a host directory, mount it to `/working` and set the plugin root paths in agent config (via Settings > Agents or `opencaddis.json`):

Linux / macOS:
```bash
docker run -d \
  -p 5000:5000 \
  -v ~/opencaddis-working:/working \
  -v opencaddis-data:/app/data \
  -v opencaddis-keys:/app/.keys \
  --name opencaddis \
  vulcan365/opencaddis
```

Windows (PowerShell):
```powershell
docker run -d `
  -p 5000:5000 `
  -v C:\temp\opencaddis-working:/working `
  -v opencaddis-data:/app/data `
  -v opencaddis-keys:/app/.keys `
  --name opencaddis `
  vulcan365/opencaddis
```

Then set the plugin paths in your agent's Args (these are container paths, the same on every host):

| Arg | Example value |
|---|---|
| `FileSystem:RootPath` | `/working/files/` |
| `PowerShell:WorkingDirectory` | `/working/` |
| `TaskManager:RootPath` | `/working/tasks/` |
| `WebBrowser:ScreenshotPath` | `/working/screenshots/` |

**Docker Compose** -- save as `docker-compose.yml`:

```yaml
services:
  opencaddis:
    image: vulcan365/opencaddis
    ports:
      - "5000:5000"
    volumes:
      - ~/opencaddis-working:/working   # Linux/macOS
      # - C:\temp\opencaddis-working:/working  # Windows
      - opencaddis-data:/app/data
      - opencaddis-keys:/app/.keys
    restart: unless-stopped

volumes:
  opencaddis-data:
  opencaddis-keys:
```

```bash
docker compose up -d
```

## Configuration

### fabr.json

Configures AI model endpoints and API keys used by the Fabr framework. Requires at minimum:

- **`default`** -- a chat/completion model (e.g., `gpt-4o`, `gpt-5-nano`)
- **`embeddings`** -- a text embedding model (e.g., `text-embedding-ada-002`)

### opencaddis.json

Defines agents, their plugins, system prompts, and per-plugin settings. Each agent entry includes:

- **Handle** -- display name and routing identifier
- **AgentType** -- `assistant` (standard) or `thinking` (plan-execute loop)
- **Models** -- which model configuration to use from `fabr.json`
- **Plugins** -- list of plugin aliases to attach
- **Args** -- per-plugin settings (e.g., `"FileSystem:RootPath"`, `"PowerShell:TimeoutSeconds"`)

Plugin settings configured here override the defaults shown in the Settings UI.

### Microsoft 365 Setup

To use the Microsoft365Email plugin:

1. Register an app in [Azure AD](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade):
   - **Supported account types**: Accounts in any organizational directory and personal Microsoft accounts
   - **Redirect URI**: not needed (device code flow)
   - **API permissions**: Add `Microsoft Graph` > Delegated > `Mail.Read`, `Mail.Send`, `User.Read`
   - Enable **Allow public client flows** under Authentication
2. Copy the **Application (client) ID**
3. In the OpenCaddis Settings > Connections tab, paste the Client ID and click **Connect to Microsoft 365**
4. Follow the device code flow to authenticate

## Project Structure

```
src/OpenCaddis/
  Agentic/
    Agents/         # Agent type implementations (Assistant, Thinking)
    Plugins/        # Plugin implementations
    Tools/          # Standalone tools (DateTime, Notifications, AgentMessage)
  Components/
    Layout/         # MainLayout, ReconnectModal
    Pages/
      Chat/         # Chat UI (conversation list, chat window)
      Setup/        # Settings tabs (models, agents, connections)
  Services/         # App services (config, memory, embeddings, vector store)
```

## License

This project is licensed under the [MIT License](LICENSE).

## Acknowledgments

Built on the [Fabr](https://github.com/vulcan365/Fabr) agent framework (Apache 2.0).
