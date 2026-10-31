"""Publish a stable NuGet version taken from a newly pushed version tag."""

import json
import os
from pathlib import Path
import re
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


def project_property(content, name, default=None):
    root = ET.fromstring(content)
    value = root.findtext(f"./PropertyGroup/{name}")
    return value.strip() if value else default


def release_plan(env, get_json):
    if env.get("GITHUB_REF_TYPE") != "tag":
        raise ValueError("Releases must be triggered by a version tag.")
    tag = env.get("GITHUB_REF_NAME", "")
    version = tag.removeprefix("v")
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError("Use a stable version tag such as v1.1.0 or 1.1.0.")
    if any(int(part) > 2147483647 for part in version.split(".")):
        raise ValueError("Each NuGet version component must be at most 2147483647.")

    project = Path(env["PROJECT_PATH"])
    content = project.read_bytes()
    package_id = project_property(content, "PackageId", project.stem)

    owner = quote(env["GITHUB_REPOSITORY_OWNER"], safe="")
    owner_scope = "orgs" if env["OWNER_TYPE"] == "Organization" else "users"
    package_name = quote(package_id, safe="")
    page = 1
    while True:
        try:
            versions = get_json(
                f"/{owner_scope}/{owner}/packages/nuget/{package_name}/versions?per_page=100&page={page}"
            )
        except HTTPError as error:
            if error.code == 404 and page == 1:
                break  # The package has not been published yet.
            raise
        if any(item["name"] == version for item in versions):
            return False, version, package_id, f"Version {version} is already published; skipping build and publish."
        if len(versions) < 100:
            break
        page += 1

    return True, version, package_id, f"New release: {package_id} {version}."


def main():
    api_url = os.environ["GITHUB_API_URL"].rstrip("/")
    headers = {
        "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "MonsterSyncDrive-release-check",
    }

    def get_json(path):
        request = Request(api_url + path, headers=headers)
        with urlopen(request, timeout=30) as response:
            return json.load(response)

    publish, version, _package_id, reason = release_plan(os.environ, get_json)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"should_publish={str(publish).lower()}\n")
        output.write(f"package_version={version}\n")
    print(reason)
    if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(f"{reason}\n")


if __name__ == "__main__":
    main()
