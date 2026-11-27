"""Restore and run a fresh consumer, verifying the exact release package payloads."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

from check_existing import REGISTRIES
from package_payload import payload


RELEASE_SCRIPTS = Path(__file__).resolve().parent


def write_consumer(consumer, source, registry, policy):
    shutil.copyfile(RELEASE_SCRIPTS.parents[1] / "global.json", consumer / "global.json")
    shutil.copyfile(RELEASE_SCRIPTS / "consumer.cs", consumer / "Program.cs")

    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="release", value=source)
    mappings = ET.SubElement(config, "packageSourceMapping")
    release = ET.SubElement(mappings, "packageSource", key="release")
    if registry == "nuget":
        ET.SubElement(release, "package", pattern="*")
    else:
        ET.SubElement(release, "package", pattern="Knightfall.ScaleSet.*")
        ET.SubElement(sources, "add", key="dependencies", value=dict(REGISTRIES)["NuGet"])
        dependencies = ET.SubElement(mappings, "packageSource", key="dependencies")
        ET.SubElement(dependencies, "package", pattern="*")
    ET.ElementTree(config).write(consumer / "NuGet.Config", encoding="utf-8")

    versions = ET.Element("Project")
    properties = ET.SubElement(versions, "PropertyGroup")
    ET.SubElement(properties, "ManagePackageVersionsCentrally").text = "true"
    packages = ET.SubElement(versions, "ItemGroup")
    for package_id in policy["packages"]:
        ET.SubElement(packages, "PackageVersion", Include=package_id, Version=policy["version"])
    ET.ElementTree(versions).write(consumer / "Directory.Packages.props", encoding="utf-8")

    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in (
        ("OutputType", "Exe"),
        ("TargetFramework", "net10.0"),
        ("Nullable", "enable"),
        ("ImplicitUsings", "enable"),
        ("TreatWarningsAsErrors", "true"),
        ("RestorePackagesWithLockFile", "true"),
    ):
        ET.SubElement(properties, name).text = value
    references = ET.SubElement(project, "ItemGroup")
    for package_id in policy["packages"]:
        ET.SubElement(references, "PackageReference", Include=package_id)
    path = consumer / "Consumer.csproj"
    ET.ElementTree(project).write(path, encoding="utf-8")
    return path


def verify_published(registry, root, policy):
    sources = {name.lower(): url for name, url in REGISTRIES}
    source = str((root / "packages").resolve(strict=True)) if registry == "candidate" else sources[registry]
    env = os.environ.copy()
    credentials = "NuGetPackageSourceCredentials_release"
    env.pop(credentials, None)
    if registry == "github":
        env[credentials] = f"Username=knightfall;Password={env['GH_TOKEN']};ValidAuthenticationTypes=Basic"

    with tempfile.TemporaryDirectory(prefix=f"consumer-{registry}-", dir=env["RUNNER_TEMP"]) as directory:
        consumer = Path(directory)
        project = write_consumer(consumer, source, registry, policy)
        cache = consumer / "obj" / "packages"

        def dotnet(args, failure):
            try:
                subprocess.run(["dotnet", *args], cwd=consumer, env=env, check=True)
            except subprocess.CalledProcessError as error:
                raise RuntimeError(failure) from error

        for _ in range(12):
            result = subprocess.run(
                ["dotnet", "restore", project, "--packages", cache, "--no-http-cache"], cwd=consumer, env=env,
            )
            if result.returncode == 0:
                break
            time.sleep(10)
        else:
            raise RuntimeError("Published packages did not become available")

        dotnet(["restore", project, "--locked-mode", "--packages", cache], "Published locked consumer restore failed")
        version = policy["version"]
        for package_id in policy["packages"]:
            name = package_id.lower()
            downloaded = cache / name / version / f"{name}.{version}.nupkg"
            original = root / "packages" / f"{package_id}.{version}.nupkg"
            if payload(original) != payload(downloaded):
                raise RuntimeError(f"Published package content differs: {package_id}")
            if registry == "nuget":
                dotnet(["nuget", "verify", downloaded, "--all"], f"NuGet repository signature verification failed: {package_id}")
        dotnet(["run", "--project", project, "--configuration", "Release", "--no-restore"], "Published consumer execution failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", required=True, choices=("candidate", "nuget", "github"))
    parser.add_argument("--release-directory", required=True, type=Path)
    args = parser.parse_args()
    policy = json.loads((RELEASE_SCRIPTS / "policy.json").read_text(encoding="utf-8"))
    try:
        verify_published(args.registry, args.release_directory, policy)
    except (KeyError, OSError, ValueError, RuntimeError) as error:
        sys.exit(str(error))


if __name__ == "__main__":
    main()
