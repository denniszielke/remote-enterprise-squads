#!/usr/bin/env python3
"""Build and exercise the core and HTTP boundary with no package sources."""

import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
PROJECTS = (
    ROOT / "src/RemoteSquads.Core/RemoteSquads.Core.csproj",
    ROOT / "tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj",
    ROOT / "src/RemoteSquads.ControlPlane/RemoteSquads.ControlPlane.csproj",
)
TEST_PROJECT = PROJECTS[1]
TIMEOUT_SECONDS = 120


class CheckError(Exception):
    """A validation prerequisite or execution step failed."""


def validate_project(path):
    try:
        project = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise CheckError("Cannot read a valid core project manifest.") from error
    for element in project.iter():
        if element.tag.rsplit("}", 1)[-1] in {"PackageReference", "PackageDownload"}:
            raise CheckError("Offline core validation does not permit package dependencies.")


def child_environment(home):
    return {
        "PATH": os.environ.get("PATH", os.defpath),
        "DOTNET_CLI_HOME": str(home),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "false",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true",
        "NUGET_PACKAGES": str(home / "packages"),
        "NUGET_HTTP_CACHE_PATH": str(home / "http-cache"),
        "NUGET_PLUGINS_CACHE_PATH": str(home / "plugin-cache"),
    }


def run_step(command, environment, *, capture=False):
    try:
        result = subprocess.run(
            command,
            cwd=ROOT,
            env=environment,
            stdin=subprocess.DEVNULL,
            shell=False,
            timeout=TIMEOUT_SECONDS,
            check=False,
            text=True,
            stdout=subprocess.PIPE if capture else None,
            stderr=subprocess.DEVNULL if capture else None,
        )
    except subprocess.TimeoutExpired as error:
        raise CheckError("Core validation step timed out.") from error
    except OSError as error:
        raise CheckError("Core validation process could not start.") from error
    if result.returncode != 0:
        raise CheckError("Core validation step failed; no deployment was attempted.")
    return result.stdout.strip() if capture else None


def run_checks():
    if sys.version_info[:2] != (3, 13):
        raise CheckError("Python 3.13 is required.")
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise CheckError("An existing .NET 10 SDK is required; nothing will be installed.")
    for path in PROJECTS:
        validate_project(path)
    with tempfile.TemporaryDirectory(prefix="remote-squads-core-") as directory:
        home = Path(directory)
        environment = child_environment(home)
        version = run_step([dotnet, "--version"], environment, capture=True)
        if re.fullmatch(r"10\.\d+\.\d+", version) is None:
            raise CheckError("The selected SDK must be a stable .NET 10 SDK.")
        config = home / "NuGet.Config"
        config.write_text(
            "<configuration>"
            "<packageSources><clear /></packageSources>"
            "<fallbackPackageFolders><clear /></fallbackPackageFolders>"
            "</configuration>\n",
            encoding="utf-8",
        )
        run_step(
            [
                dotnet, "restore", str(TEST_PROJECT), "--configfile", str(config),
                "--packages", str(home / "packages"),
                "-p:RestoreSources=", "-p:RestoreAdditionalProjectSources=",
                "-p:RestoreFallbackFolders=", "-p:RestoreAdditionalProjectFallbackFolders=",
                "-p:RestoreIgnoreFailedSources=false", "-p:NuGetAudit=false",
            ],
            environment,
        )
        run_step(
            [dotnet, "build", str(TEST_PROJECT), "--configuration", "Release", "--no-restore"],
            environment,
        )
        run_step(
            [dotnet, "run", "--project", str(TEST_PROJECT), "--configuration", "Release",
             "--no-build", "--no-restore"],
            environment,
        )
    print("Offline core checks passed. No deployment or package-source approval is implied.")


def main(argv=None):
    arguments = sys.argv[1:] if argv is None else argv
    if arguments == ["--help"]:
        print("Usage: python3.13 tools/check_core.py\n"
              "Builds and tests the no-package core using installed .NET 10 packs only.\n"
              "No deployment. See docs/development.md.")
        return 0
    if arguments:
        print("Unsupported arguments.", file=sys.stderr)
        return 2
    try:
        run_checks()
    except CheckError as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
