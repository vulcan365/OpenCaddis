#!/bin/bash
set -e

# Create default config files if they don't exist (no bind mount provided)
if [ ! -f /app/fabr.json ] && [ ! -d /app/fabr.json ]; then
    echo '{"ModelConfigurations":[],"ApiKeys":[]}' > /app/fabr.json
    echo "INFO: Created default fabr.json — configure via Settings UI at http://localhost:5000"
fi

if [ ! -f /app/opencaddis.json ] && [ ! -d /app/opencaddis.json ]; then
    echo '{"Agents":[],"Microsoft365":{"ClientId":"","EncryptedTokens":null,"UserDisplayName":null,"UserEmail":null}}' > /app/opencaddis.json
    echo "INFO: Created default opencaddis.json — configure via Settings UI at http://localhost:5000"
fi

# Warn if Docker created directories instead of files (host file didn't exist at mount time)
if [ -d /app/fabr.json ]; then
    echo "WARNING: /app/fabr.json is a directory, not a file."
    echo "  This happens when the host file doesn't exist before 'docker run'."
    echo "  Remove the container, create the file on the host, then try again."
fi

if [ -d /app/opencaddis.json ]; then
    echo "WARNING: /app/opencaddis.json is a directory, not a file."
    echo "  This happens when the host file doesn't exist before 'docker run'."
    echo "  Remove the container, create the file on the host, then try again."
fi

exec dotnet OpenCaddis.dll "$@"
