#!/usr/bin/env python3
"""Offline Stage A checks; this is not installation or deployment authorization."""

import json
import os
import re
import shutil
import subprocess
import sys
from urllib.parse import urlsplit


PROBE_TIMEOUT_SECONDS = 5
TOOLS = (
    ("az", ("--version",)),
    ("bicep", ("--version",)),
    ("dotnet", ("--list-sdks",)),
    ("gh", ("--version",)),
    ("git", ("--version",)),
    ("node", ("--version",)),
    ("npm", ("--version",)),
)
PROJECT_INPUTS = {
    "RES_PROJECT_ID": r"[a-z][a-z0-9-]{0,62}",
    "AZURE_TENANT_ID": r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
    "AZURE_SUBSCRIPTION_ID": r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
    "AZURE_LOCATION": r"[a-z][a-z0-9]{1,39}",
}
FEED_INPUTS = (
    "RES_PYTHON_FEED_URL",
    "RES_NUGET_FEED_URL",
    "RES_NPM_FEED_URL",
)
PUBLIC_REGISTRIES = (
    "pypi.org",
    "pythonhosted.org",
    "pypi.python.org",
    "nuget.org",
    "npmjs.org",
    "npmjs.com",
    "yarnpkg.com",
)
EVIDENCE_GATES = {
    "evidence.feed_approval": "approved_feed_and_authentication_evidence_required",
    "evidence.provenance": "authentic_publisher_and_artifact_integrity_evidence_required",
    "evidence.age": "upstream_publication_age_strictly_over_seven_days_required",
    "evidence.lock_graph": "locked_direct_transitive_development_build_graph_required",
    "evidence.compatibility": "pinned_tool_and_runtime_compatibility_evidence_required",
    "evidence.azure": "region_quota_api_private_network_identity_and_role_evidence_required",
}


def check(check_id, status, reason):
    return {"id": check_id, "status": status, "reason": reason}


def feed_error(value):
    """Return only fixed reason codes, never parser diagnostics or input text."""
    if (
        not value.isascii()
        or any(character.isspace() or ord(character) < 32 or ord(character) == 127
               for character in value)
        or any(character in value for character in "\\,;|")
    ):
        return "invalid_https_feed_endpoint"
    try:
        endpoint = urlsplit(value)
        hostname = (endpoint.hostname or "").lower().rstrip(".")
        if (
            endpoint.scheme != "https"
            or not endpoint.netloc
            or endpoint.username is not None
            or endpoint.password is not None
            or endpoint.query
            or endpoint.fragment
            or "?" in value
            or "#" in value
            or endpoint.port not in (None, 443)
            or len(hostname) > 253
            or not hostname
            or any(
                re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label) is None
                for label in hostname.split(".")
            )
        ):
            return "invalid_https_feed_endpoint"
    except ValueError:
        return "invalid_https_feed_endpoint"
    if any(hostname == public or hostname.endswith("." + public)
           for public in PUBLIC_REGISTRIES):
        return "direct_public_registry_forbidden"
    return None


def configuration_checks(environ):
    results = []
    for name in sorted(PROJECT_INPUTS.keys() | set(FEED_INPUTS)):
        value = environ.get(name)
        reason = None
        if value is None or value == "":
            reason = "required_input_missing"
        elif name in PROJECT_INPUTS:
            if re.fullmatch(PROJECT_INPUTS[name], value, flags=re.ASCII) is None:
                reason = "invalid_project_input"
        else:
            reason = feed_error(value)
        results.append(check(
            "config." + name,
            "BLOCKED" if reason else "PASS",
            reason or "syntax_valid_only_not_authorization",
        ))
    return results


def tool_checks():
    results = []
    for name, arguments in TOOLS:
        reason = "local_probe_completed_only_not_compatibility"
        try:
            executable = shutil.which(name)
            if executable is None:
                reason = "required_tool_missing"
            else:
                process = subprocess.run(
                    [executable, *arguments],
                    shell=False,
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=PROBE_TIMEOUT_SECONDS,
                    check=False,
                    env={
                        "PATH": os.environ.get("PATH", os.defpath),
                        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                        "AZURE_CORE_COLLECT_TELEMETRY": "false",
                        "GH_PROMPT_DISABLED": "1",
                    },
                )
                if process.returncode != 0:
                    reason = "tool_probe_nonzero_exit"
        except subprocess.TimeoutExpired:
            reason = "tool_probe_timeout"
        except Exception:
            # Exception messages, tool output and discovered paths are untrusted.
            reason = "tool_probe_error"
        results.append(check(
            "tool." + name,
            "PASS" if reason == "local_probe_completed_only_not_compatibility" else "BLOCKED",
            reason,
        ))
    return results


def make_report(checks):
    return {
        "schema_version": 1,
        "implementation_status": "implemented_not_independently_reviewed",
        "status": "BLOCKED" if any(item["status"] != "PASS" for item in checks) else "PASS",
        "checks": sorted(checks, key=lambda item: item["id"]),
    }


def build_report(environ=None):
    if environ is None:
        environ = os.environ
    python_supported = sys.version_info[:2] == (3, 13)
    checks = [
        check("runtime.python", "PASS" if python_supported else "BLOCKED",
              "python_3_13_detected" if python_supported else "python_3_13_required"),
        *configuration_checks(environ),
        *tool_checks(),
        *(check(gate, "BLOCKED", reason) for gate, reason in EVIDENCE_GATES.items()),
    ]
    return make_report(checks)


def main(argv=None):
    arguments = sys.argv[1:] if argv is None else argv
    if arguments == ["--help"]:
        print("Usage: python3.13 tools/preflight.py\n"
              "Offline environment-only checks. See docs/preflight.md.\n"
              "No env files, installs, restores or cloud probes are supported.")
        return 0
    if arguments:
        report = make_report([check("cli.arguments", "BLOCKED", "unsupported_arguments")])
    else:
        report = build_report()
    print(json.dumps(report, sort_keys=True, separators=(",", ":")))
    return 1 if report["status"] == "BLOCKED" else 0


if __name__ == "__main__":
    sys.exit(main())
