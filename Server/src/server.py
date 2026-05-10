"""MCP server that wraps the fmodel-cli .NET binary.

Tools speak JSON. The CLI is invoked per call via `dotnet exec`. The CLI handles
all CUE4Parse plumbing; this server only marshals arguments and parses results.
"""

from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any

from fastmcp import FastMCP

mcp = FastMCP("fmodel-mcp")

# Locate the CLI binary. Prefer the published self-contained .exe; fall back
# to the framework-dependent .dll (development builds) if the .exe is absent.
# Override either by setting FMODEL_CLI_BIN to an absolute path.
_CLI_ROOT = Path(__file__).resolve().parents[2] / "Cli" / "bin"
_DEFAULT_EXE = _CLI_ROOT / "publish" / "fmodel-cli.exe"
_DEFAULT_DLL = _CLI_ROOT / "Debug" / "net9.0" / "fmodel-cli.dll"


def _resolve_cli_bin() -> Path:
    override = os.environ.get("FMODEL_CLI_BIN")
    if override:
        return Path(override)
    if _DEFAULT_EXE.exists():
        return _DEFAULT_EXE
    return _DEFAULT_DLL


CLI_BIN = _resolve_cli_bin()


def _run(args: list[str]) -> dict[str, Any]:
    if not CLI_BIN.exists():
        return {
            "ok": False,
            "error": (
                f"CLI binary not found at {CLI_BIN}. Build it first: "
                "`dotnet publish -c Release -r win-x64 --self-contained true "
                "-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "
                "-o bin/publish` in fmodel-mcp/Cli/."
            ),
        }

    if CLI_BIN.suffix.lower() == ".dll":
        cmd = ["dotnet", "exec", str(CLI_BIN), *args]
    else:
        cmd = [str(CLI_BIN), *args]

    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        cwd=str(CLI_BIN.parent),  # so the Oodle DLL next to the binary is picked up
    )

    stdout = proc.stdout.strip()
    if not stdout:
        return {
            "ok": False,
            "error": proc.stderr.strip() or "CLI produced no output",
            "returncode": proc.returncode,
        }

    try:
        return json.loads(stdout)
    except json.JSONDecodeError as e:
        return {
            "ok": False,
            "error": f"failed to parse CLI JSON: {e}",
            "raw_stdout": stdout[:1000],
            "stderr": proc.stderr[:500],
            "returncode": proc.returncode,
        }


@mcp.tool
def fmodel_status() -> dict[str, Any]:
    """Sanity check the provider: pak directory, mappings loaded, total files indexed."""
    return _run(["status"])


@mcp.tool
def fmodel_search(pattern: str, limit: int = 200) -> dict[str, Any]:
    """Find packages by glob or substring.

    Pattern is a glob over package paths (e.g. `**/MI_Curator*`,
    `Game/Content/Characters/**/MyCharacter/**/*`). `*` matches a single path
    component, `**` matches any number of components.
    """
    return _run(["search", pattern, str(limit)])


@mcp.tool
def fmodel_read(path: str) -> dict[str, Any]:
    """Return all UObjects in the package as their full JSON form (the same
    shape FModel writes when you 'Save Properties (.json)')."""
    return _run(["read", path])


@mcp.tool
def fmodel_inspect_material(path: str) -> dict[str, Any]:
    """Summarized view of a material / material instance: parent, blend mode,
    two-sided, opacity clip, plus the Texture/Scalar/Vector parameter values.
    Use this instead of `fmodel_read` when you only care about which textures
    a material wires up and the tweak knobs above it."""
    return _run(["inspect", path])


@mcp.tool
def fmodel_export_texture(path: str) -> dict[str, Any]:
    """Decode a UTexture2D to PNG under Output/Exports/, mirroring the
    package path. Returns the on-disk path and dimensions."""
    return _run(["export-tex", path])


@mcp.tool
def fmodel_export_mesh(path: str) -> dict[str, Any]:
    """Export a SkeletalMesh / StaticMesh as PSK/PSKX under Output/Exports/."""
    return _run(["export-mesh", path])


@mcp.tool
def fmodel_export_raw(path: str) -> dict[str, Any]:
    """Dump the full FModel-style JSON for the package (all UObjects, all
    properties) to Output/Exports/ as a .json file. Use for blueprints,
    skeletal mesh metadata, anything where the summarized inspect is too thin."""
    return _run(["export-raw", path])


@mcp.tool
def fmodel_list_exports(prefix: str = "") -> dict[str, Any]:
    """List files already on disk under Output/Exports/ matching an optional
    prefix (e.g. `Game/Content/Characters/Enemies/MyEnemy`)."""
    return _run(["list", prefix])


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
