# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies
COPY src/OpenCaddis/OpenCaddis.csproj src/OpenCaddis/
RUN dotnet restore src/OpenCaddis/OpenCaddis.csproj

# Build and publish
COPY src/ src/
RUN dotnet publish src/OpenCaddis/OpenCaddis.csproj -c Release -o /app/publish

# Install Playwright Chromium into a shared path
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet tool install --global PowerShell && \
    export PATH="$PATH:/root/.dotnet/tools" && \
    pwsh /app/publish/playwright.ps1 install chromium

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Install Chromium system dependencies required by Playwright
RUN apt-get update && apt-get install -y --no-install-recommends \
    libglib2.0-0 \
    libnss3 \
    libnspr4 \
    libdbus-1-3 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxcb1 \
    libxkbcommon0 \
    libatspi2.0-0 \
    libx11-6 \
    libxcomposite1 \
    libxdamage1 \
    libxext6 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libasound2t64 \
    fonts-liberation \
    fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# Copy published app and Playwright browsers (owned by app user)
COPY --from=build --chown=app:app /app/publish .
COPY --from=build --chown=app:app /ms-playwright /ms-playwright

# Copy entrypoint script (strip Windows CRLF line endings if present)
COPY --chown=app:app entrypoint.sh /app/entrypoint.sh
RUN sed -i 's/\r$//' /app/entrypoint.sh && chmod +x /app/entrypoint.sh

# Make /app writable (for config files created from Settings UI) and create data dirs
# /working is intended as a mount point for plugin root paths (FileSystem, PowerShell, etc.)
RUN chown app:app /app && \
    mkdir -p /app/data /app/.keys /working && \
    chown app:app /app/data /app/.keys /working

USER app

ENTRYPOINT ["/app/entrypoint.sh"]
