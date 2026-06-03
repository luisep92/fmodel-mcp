# fmodel-mcp

Minimal MCP server for inspecting and exporting Unreal Engine assets via [CUE4Parse](https://github.com/FabianFG/CUE4Parse), the parsing library that [FModel](https://github.com/4sval/FModel) is built on top of. Designed to live alongside an LLM coding assistant (Claude Code, etc.) so it can search, read, and export UE assets without round-tripping through the FModel GUI.

> **Not a fork of FModel.** FModel itself is a WPF app and has no CLI. This project wraps the underlying CUE4Parse library directly. It is, in spirit, a sibling tool — exposing programmatic access to what FModel exposes interactively.

## Why

Working with assets ripped from a UE game (here: *Expedition 33*) inside another engine (here: Unity 2019.4 + Vivify for Beat Saber) means a lot of "open FModel, navigate, export, copy to project". The friction is the same friction that motivated wrapping the Unity Editor in [unity-mcp](https://github.com/CoplayDev/unity-mcp). Same answer: ship a tool that the assistant can drive directly.

## Architecture

```
fmodel-mcp/
├── Cli/                # standalone .NET 9 binary
│   └── ...             # uses CUE4Parse, single-file publish
├── Server/             # FastMCP Python server (stdio transport)
│   └── src/
└── mappings/           # .usmap files (gitignored, game-specific)
```

Two layers by design:

- **Cli/** is the only thing that links to CUE4Parse. It exposes subcommands (`status`, `search`, `read`, `inspect`, `export-tex`, `export-mesh`, `export-mesh-uf`, `export-anim`, `export-raw`, `list`) that print JSON to stdout. Useful on its own for ad-hoc scripts.
- **Server/** is a thin Python MCP server that invokes the CLI as subprocess and exposes its functionality as MCP tools. Cheap to evolve independently of CUE4Parse.

## Tools (Tier 1)

| Tool | What it does |
|---|---|
| `fmodel_status` | Sanity check: provider initialized, # paks, # files indexed |
| `fmodel_search(pattern)` | Glob/regex over package names: `**/MI_Curator*` |
| `fmodel_read(path)` | Returns the package JSON without exporting |
| `fmodel_inspect_material(path)` | Shortcut: only Textures + Scalars + Vectors + Parent + BlendMode |
| `fmodel_export_texture(path)` | PNG/TGA to `Output/Exports/...` |
| `fmodel_export_mesh(path)` | SkeletalMesh/StaticMesh as ActorX `.psk`/`.pskx` to `Output/Exports/...` |
| `fmodel_export_mesh_uf(path)` | SkeletalMesh/StaticMesh as `.uemodel` (UEFormat) — bind matches `.ueanim` from `fmodel_export_anim` |
| `fmodel_export_anim(path)` | AnimSequence/AnimMontage as `.ueanim` (UEFormat, keeps bone scale) to `Output/Exports/...` |
| `fmodel_export_raw(path)` | Full JSON dump (the kind material instance JSONs end up as) |
| `fmodel_list_exports(prefix)` | List what is already exported |

## Project conventions

- Output dir is configured in `config.json` (see Setup below) and is treated as scratch — same role FModel's `Output/Exports/` plays.
- The "promote curated subset to the real project" step is intentionally out of scope here; the tool exports, you copy what you keep.

## Setup

### Build the CLI

```pwsh
cd <repo>/Cli
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin/publish
```

This produces a single `fmodel-cli.exe` (~90 MB, .NET runtime embedded). The first run downloads the Oodle DLL (`oodle-data-shared.dll`) from [WorkingRobot/OodleUE](https://github.com/WorkingRobot/OodleUE) into the same folder. Oodle is not redistributable so the repo doesn't ship it.

### Configure for your game

Copy `config.json.example` to `config.json` next to the .exe (or anywhere, then point `FMODEL_MCP_CONFIG` at it) and edit the paths for your title. The example values target *Expedition 33* — the game this CLI was originally built for — as a working reference.

Config keys:

- `PaksDir`: directory containing `*.pak` / `*.utoc` / `*.ucas` for your game.
- `OutputDir`: where exports land.
- `UeVersion`: CUE4Parse version enum (e.g. `GAME_UE5_4`).
- `MappingsFile`: path to a `.usmap` file (some games need it; nullable).
- `AesKey`: encryption key as a hex string (most games don't need it; nullable).

### Install the MCP server

```pwsh
cd <repo>/Server
uv sync
```

### Wire into Claude Code

Add to `~/.claude.json` under `mcpServers` (use the absolute path to your local checkout):

```json
"fmodel": {
  "type": "stdio",
  "command": "uv",
  "args": ["run", "--directory", "<absolute-path-to>/fmodel-mcp/Server", "python", "src/server.py"]
}
```

Restart Claude Code; the `mcp__fmodel__*` tools should appear.

## Status

Built initially for *Expedition 33* (UE 5.4, no AES) — those values ship in `config.json.example`. Not generalized to other titles yet: the package-path normalizer in `Program.cs` hardcodes E33's `Sandfall/Content` mount point, so a second game with a different mount point would need that helper made configurable.
