import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "preflight", Path(__file__).resolve().parents[1] / "tools" / "preflight.py"
)
preflight = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(preflight)


def valid_environment():
    return {
        "RES_PROJECT_ID": "pilot-project",
        "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000001",
        "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000002",
        "AZURE_LOCATION": "testregion",
        "RES_PYTHON_FEED_URL": "https://packages.example.invalid/python/simple",
        "RES_NUGET_FEED_URL": "https://packages.example.invalid/nuget/v3/index.json",
        "RES_NPM_FEED_URL": "https://packages.example.invalid/npm/",
    }


class ConfigurationTests(unittest.TestCase):
    def test_missing_inputs_fail_closed_without_defaults(self):
        results = preflight.configuration_checks({})
        self.assertEqual(len(results), 7)
        self.assertTrue(all(result["status"] == "BLOCKED" for result in results))
        self.assertTrue(all(result["reason"] == "required_input_missing" for result in results))

    def test_empty_and_invalid_project_inputs(self):
        for value in ("", " ", "SECRET/invalid\n", "a" * 64):
            with self.subTest(value=value):
                environment = valid_environment()
                environment["RES_PROJECT_ID"] = value
                result = preflight.configuration_checks(environment)
                project = next(item for item in result if item["id"] == "config.RES_PROJECT_ID")
                self.assertEqual(project["status"], "BLOCKED")
        for name in ("AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_LOCATION"):
            with self.subTest(name=name):
                environment = valid_environment()
                environment[name] = "not valid!"
                self.assertIn(
                    {"id": "config." + name, "status": "BLOCKED",
                     "reason": "invalid_project_input"},
                    preflight.configuration_checks(environment),
                )

    def test_valid_inputs_are_syntax_only(self):
        checks = preflight.configuration_checks(valid_environment())
        self.assertTrue(all(item["status"] == "PASS" for item in checks))
        self.assertTrue(all(item["reason"] == "syntax_valid_only_not_authorization"
                            for item in checks))

    def test_public_registries_and_subdomains_rejected_for_every_feed(self):
        public_urls = (
            "https://pypi.org/simple/",
            "https://files.pythonhosted.org/packages/",
            "https://pypi.python.org/simple",
            "https://api.nuget.org/v3/index.json",
            "https://www.nuget.org/api/v2/",
            "https://registry.npmjs.org/",
            "https://registry.npmjs.com/",
            "https://registry.yarnpkg.com/",
            "https://REGISTRY.NPMJS.ORG.:443/",
        )
        for name in preflight.FEED_INPUTS:
            for url in public_urls:
                with self.subTest(name=name, url=url):
                    environment = valid_environment()
                    environment[name] = url
                    result = next(item for item in preflight.configuration_checks(environment)
                                  if item["id"] == "config." + name)
                    self.assertEqual(result["status"], "BLOCKED")
                    self.assertEqual(result["reason"], "direct_public_registry_forbidden")

    def test_invalid_urls_credentials_and_fallback_lists_rejected(self):
        for url in (
            "http://packages.example.invalid/simple",
            "file:///local/feed",
            "https://user:SECRET@packages.example.invalid/simple",
            "https://SECRET@packages.example.invalid/simple",
            "https://packages.example.invalid/simple?token=SECRET",
            "https://packages.example.invalid/simple#SECRET",
            "https://packages.example.invalid/?",
            "https://packages.example.invalid/#",
            "https://packages.example.invalid:invalid/simple",
            "https://packages.example.invalid:70000/simple",
            "https://packages.example.invalid:444/simple",
            "https://[broken",
            "https://packages.example.invalid/ https://pypi.org",
            "https://packages.example.invalid/,https://pypi.org",
            "https://packages.example.invalid/;https://pypi.org",
            "https://packages.example.invalid/|https://pypi.org",
            "https://packages.example.invalid\\@pypi.org",
            "https://packages.example.invalid/\nSECRET",
            "https://packages.example.invalid/\x00SECRET",
            "https://packages.example.invalid/\x7fSECRET",
            "https://pýpi.org/",
            "https://pypi%2eorg/",
            "https:///simple",
        ):
            with self.subTest(url=url):
                self.assertEqual(preflight.feed_error(url), "invalid_https_feed_endpoint")

    def test_process_only_inputs_no_files_or_other_environment_values(self):
        environment = valid_environment()
        environment["UNRELATED_SECRET"] = "never-output-this"
        with patch.object(preflight.os, "environ", environment), \
                patch.object(preflight, "tool_checks", return_value=[]), \
                patch("builtins.open", side_effect=AssertionError("must not read any env file")):
            report = preflight.build_report()
        self.assertTrue(all(item["status"] == "PASS" for item in report["checks"]
                            if item["id"].startswith("config.")))
        self.assertNotIn("never-output-this", json.dumps(report))
        self.assertNotIn("UNRELATED_SECRET", json.dumps(report))


class ToolTests(unittest.TestCase):
    def test_safe_bounded_probes_with_no_output_capture_or_secret_environment(self):
        secret = "SECRET-process-environment"
        with patch.object(preflight.shutil, "which", return_value="/safe/tool"), \
                patch.object(preflight.os, "environ", {"PATH": "/safe/bin", "SECRET": secret}), \
                patch.object(preflight.subprocess, "run",
                             return_value=subprocess.CompletedProcess([], 0)) as run:
            results = preflight.tool_checks()
        self.assertTrue(all(item["status"] == "PASS" for item in results))
        self.assertEqual(run.call_count, len(preflight.TOOLS))
        for invocation, (_, arguments) in zip(run.call_args_list, preflight.TOOLS):
            self.assertEqual(invocation.args, (["/safe/tool", *arguments],))
            self.assertIs(invocation.kwargs["shell"], False)
            self.assertEqual(invocation.kwargs["timeout"], 5)
            for stream in ("stdin", "stdout", "stderr"):
                self.assertEqual(invocation.kwargs[stream], subprocess.DEVNULL)
            self.assertNotIn("SECRET", invocation.kwargs["env"])
            self.assertNotIn(secret, str(invocation))

    def test_missing_tool_does_not_run_install_or_fallback(self):
        with patch.object(preflight.shutil, "which", return_value=None), \
                patch.object(preflight.subprocess, "run") as run:
            results = preflight.tool_checks()
        run.assert_not_called()
        self.assertTrue(all(item["reason"] == "required_tool_missing" for item in results))
        self.assertTrue(all(item["status"] == "BLOCKED" for item in results))

    def test_nonzero_process_exit_redacts_output(self):
        with patch.object(preflight.shutil, "which", return_value="/SECRET/tool"), \
                patch.object(preflight.subprocess, "run", return_value=subprocess.CompletedProcess(
                    [], 2, stdout="SECRET-stdout", stderr="SECRET-stderr")):
            results = preflight.tool_checks()
        self.assertTrue(all(item["reason"] == "tool_probe_nonzero_exit" for item in results))
        self.assertNotIn("SECRET", json.dumps(results))

    def test_process_errors_and_timeouts_redact_all_exception_text(self):
        failures = (
            (OSError("/SECRET/path SECRET-credential"), "tool_probe_error"),
            (RuntimeError("SECRET-exception"), "tool_probe_error"),
            (subprocess.CalledProcessError(
                2, ["SECRET-command"], output="SECRET-output", stderr="SECRET-stderr"),
             "tool_probe_error"),
            (subprocess.TimeoutExpired(
                ["SECRET-command"], 5, output="SECRET-output", stderr="SECRET-stderr"),
             "tool_probe_timeout"),
        )
        for failure, reason in failures:
            with self.subTest(reason=reason, exception=type(failure).__name__), \
                    patch.object(preflight.shutil, "which", return_value="/SECRET/tool"), \
                    patch.object(preflight.subprocess, "run", side_effect=failure):
                results = preflight.tool_checks()
                self.assertTrue(all(item["reason"] == reason for item in results))
                self.assertTrue(all(item["status"] == "BLOCKED" for item in results))
                self.assertNotIn("SECRET", json.dumps(results))

    def test_executable_resolution_errors_are_redacted(self):
        with patch.object(preflight.shutil, "which", side_effect=OSError("SECRET")), \
                patch.object(preflight.subprocess, "run") as run:
            results = preflight.tool_checks()
        run.assert_not_called()
        self.assertTrue(all(item["reason"] == "tool_probe_error" for item in results))
        self.assertNotIn("SECRET", json.dumps(results))


class ReportTests(unittest.TestCase):
    def test_deterministic_redacted_report_and_unresolved_evidence_gates(self):
        environment = valid_environment()
        environment["RES_NPM_FEED_URL"] = "https://SECRET@packages.example.invalid"
        with patch.object(preflight, "tool_checks", return_value=[]), \
                patch.object(preflight.sys, "version_info", (3, 13, 9)):
            report = preflight.build_report(environment)
            reversed_report = preflight.build_report(dict(reversed(list(environment.items()))))
        self.assertEqual(report, reversed_report)
        self.assertEqual(report["status"], "BLOCKED")
        self.assertEqual(report["implementation_status"], "implemented_not_independently_reviewed")
        self.assertEqual([item["id"] for item in report["checks"]],
                         sorted(item["id"] for item in report["checks"]))
        gates = [item for item in report["checks"] if item["id"].startswith("evidence.")]
        self.assertEqual(len(gates), 6)
        self.assertTrue(all(item["status"] == "BLOCKED" for item in gates))
        text = json.dumps(report)
        for value in environment.values():
            self.assertNotIn(value, text)
        self.assertNotIn("SECRET", text)

    def test_only_python_3_13_is_accepted(self):
        for version, expected in (((3, 12, 9), "BLOCKED"), ((3, 13, 0), "PASS"),
                                  ((3, 14, 0), "BLOCKED")):
            with self.subTest(version=version), \
                    patch.object(preflight.sys, "version_info", version), \
                    patch.object(preflight, "tool_checks", return_value=[]):
                report = preflight.build_report(valid_environment())
            runtime = next(item for item in report["checks"] if item["id"] == "runtime.python")
            self.assertEqual(runtime["status"], expected)

    def test_cli_writes_deterministic_json_and_exits_nonzero_even_with_valid_inputs(self):
        outputs = []
        with patch.object(preflight.os, "environ", valid_environment()), \
                patch.object(preflight, "tool_checks", return_value=[]):
            for _ in range(2):
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    exit_code = preflight.main([])
                self.assertEqual(exit_code, 1)
                self.assertEqual(json.loads(output.getvalue())["status"], "BLOCKED")
                outputs.append(output.getvalue())
        self.assertEqual(outputs[0], outputs[1])

    def test_unsupported_arguments_redacted_no_file_or_tool_access(self):
        output = io.StringIO()
        with patch.object(preflight, "build_report") as build, \
                contextlib.redirect_stdout(output):
            result = preflight.main(["--env-file", "/SECRET/private.env"])
        build.assert_not_called()
        self.assertEqual(result, 1)
        self.assertEqual(json.loads(output.getvalue())["checks"], [
            {"id": "cli.arguments", "status": "BLOCKED", "reason": "unsupported_arguments"}
        ])
        self.assertNotIn("SECRET", output.getvalue())


if __name__ == "__main__":
    unittest.main()
