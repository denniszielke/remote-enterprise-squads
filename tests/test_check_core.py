import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "check_core", Path(__file__).resolve().parents[1] / "tools/check_core.py"
)
check_core = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check_core)


class OfflineCoreTests(unittest.TestCase):
    def test_external_package_declarations_are_rejected(self):
        for declaration in (
            '<PackageReference Include="Example" Version="1.0.0" />',
            '<PackageDownload Include="Example" Version="[1.0.0]" />',
        ):
            with self.subTest(declaration=declaration), tempfile.TemporaryDirectory() as directory:
                project = Path(directory) / "Test.csproj"
                project.write_text(f"<Project><ItemGroup>{declaration}</ItemGroup></Project>")
                with self.assertRaisesRegex(check_core.CheckError, "package dependencies"):
                    check_core.validate_project(project)

    def test_missing_and_malformed_manifests_fail_explicitly(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory) / "Test.csproj"
            with self.assertRaises(check_core.CheckError):
                check_core.validate_project(project)
            project.write_text("<Project>")
            with self.assertRaises(check_core.CheckError):
                check_core.validate_project(project)

    def test_child_environment_does_not_inherit_secrets_or_package_settings(self):
        with patch.object(check_core.os, "environ", {
            "PATH": "/safe/bin", "SECRET": "hidden", "RestoreSources": "unapproved",
            "NUGET_PLUGIN_PATHS": "unapproved", "NUGET_PACKAGES": "/shared/cache",
        }):
            environment = check_core.child_environment(Path("/isolated"))
        self.assertEqual(environment["PATH"], "/safe/bin")
        self.assertEqual(environment["NUGET_PACKAGES"], "/isolated/packages")
        for name in ("SECRET", "RestoreSources", "NUGET_PLUGIN_PATHS"):
            self.assertNotIn(name, environment)

    def test_failed_or_timed_out_steps_do_not_succeed_or_leak_exceptions(self):
        for result in (
            subprocess.CompletedProcess([], 1),
            subprocess.TimeoutExpired(["SECRET"], 120),
            OSError("SECRET"),
        ):
            with self.subTest(result=type(result).__name__):
                arguments = {"side_effect": result} if isinstance(result, Exception) else {
                    "return_value": result
                }
                with patch.object(check_core.subprocess, "run", **arguments):
                    with self.assertRaises(check_core.CheckError) as raised:
                        check_core.run_step(["dotnet", "build"], {})
                self.assertNotIn("SECRET", str(raised.exception))

    def test_restore_has_no_sources_and_later_steps_cannot_restore(self):
        commands = []

        def execute(command, environment, *, capture=False):
            commands.append(command)
            if capture:
                return "10.0.200"
            if command[1] == "restore":
                config = Path(command[command.index("--configfile") + 1])
                self.assertIn("<packageSources><clear /></packageSources>", config.read_text())
                self.assertIn("-p:RestoreSources=", command)
                self.assertIn("-p:RestoreAdditionalProjectSources=", command)
                self.assertIn("-p:RestoreIgnoreFailedSources=false", command)

        with patch.object(check_core.sys, "version_info", (3, 13, 1)), \
                patch.object(check_core.shutil, "which", return_value="/safe/dotnet"), \
                patch.object(check_core, "validate_project"), \
                patch.object(check_core, "run_step", side_effect=execute), \
                contextlib.redirect_stdout(io.StringIO()):
            check_core.run_checks()
        self.assertEqual([command[1] for command in commands], ["--version", "restore", "build", "run"])
        self.assertIn("--no-restore", commands[2])
        self.assertIn("--no-restore", commands[3])
        self.assertIn("--no-build", commands[3])

    def test_missing_sdk_or_unsupported_version_prevents_restore(self):
        with patch.object(check_core.shutil, "which", return_value=None), \
                patch.object(check_core, "run_step") as run:
            with self.assertRaises(check_core.CheckError):
                check_core.run_checks()
            run.assert_not_called()
        for version in ("8.0.419", "11.0.100", "10.0.100-preview.1", "SECRET"):
            with self.subTest(version=version), \
                    patch.object(check_core.sys, "version_info", (3, 13, 1)), \
                    patch.object(check_core.shutil, "which", return_value="/safe/dotnet"), \
                    patch.object(check_core, "validate_project"), \
                    patch.object(check_core, "run_step", return_value=version) as run:
                with self.assertRaises(check_core.CheckError):
                    check_core.run_checks()
                self.assertEqual(run.call_count, 1)

    def test_cli_reports_failure_and_stops(self):
        output = io.StringIO()
        with patch.object(check_core, "run_checks", side_effect=check_core.CheckError("blocked")), \
                contextlib.redirect_stderr(output):
            self.assertEqual(check_core.main([]), 1)
        self.assertEqual(output.getvalue(), "blocked\n")
        with patch.object(check_core, "run_checks") as run, \
                contextlib.redirect_stderr(io.StringIO()) as output:
            self.assertEqual(check_core.main(["SECRET"]), 2)
            run.assert_not_called()
        self.assertNotIn("SECRET", output.getvalue())


if __name__ == "__main__":
    unittest.main()
