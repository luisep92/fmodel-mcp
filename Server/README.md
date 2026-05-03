# fmodel-mcp Server

Thin Python MCP server. Spawns `dotnet exec ../Cli/bin/Debug/net9.0/fmodel-cli.dll <subcommand>` per tool call.

## Run standalone (for debugging)

```pwsh
cd d:\vivify_repo\fmodel-mcp\Server
uv run python src/server.py
```

This starts a stdio MCP server. To wire into Claude Code, add to your MCP config:

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "uv",
      "args": ["run", "--project", "d:/vivify_repo/fmodel-mcp/Server", "python", "src/server.py"]
    }
  }
}
```

## Override the CLI binary

`FMODEL_CLI_DLL=path/to/fmodel-cli.dll` in env. Otherwise the server looks at `../Cli/bin/Debug/net9.0/fmodel-cli.dll` relative to itself.
