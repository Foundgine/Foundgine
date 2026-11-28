#!/usr/bin/env python3
"""Fail CI when a security or penetration test was skipped or never ran.

How it decides what is a security test (nothing is listed by hand):
  * a test FILE counts when it contains tests and its file name, its project/module directory,
    or its class name matches SECURITY below;
  * a test RESULT counts when its fully qualified class name (namespace + class) matches.

Two rules are enforced against the TRX (.NET) or JUnit XML (Java surefire/failsafe) reports:
  1. SKIPPED: no security test result may be skipped / not executed. A skip is a failure.
  2. COVERAGE: every security test file found in the source must have at least one executed
     result in the reports. A security test that no job runs is a failure.

A test that genuinely cannot run in CI goes in tools/ci/security-test-exemptions.txt with a
reason. The file is reviewed like code; the verifier prints every exemption it honours.

Usage:
  verify_security_tests.py --language dotnet --reports DIR [DIR ...]
  verify_security_tests.py --language java   --reports DIR [DIR ...]
  verify_security_tests.py --language dotnet --list
"""
from __future__ import annotations

import argparse
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

SECURITY = re.compile(
    r"penetration|pen_?test|red_?team|adversarial|security|hostile|attack|authori[sz]|warrant|"
    r"replay|tamper|spoof|escalat|cross_?tenant|revocation|injection(?!container)",
    re.IGNORECASE,
)
ROOT = pathlib.Path(__file__).resolve().parents[2]
IGNORED = {"bin", "obj", "target", "node_modules", ".git"}
EXEMPTIONS = pathlib.Path(__file__).with_name("security-test-exemptions.txt")

CS_TEST = re.compile(r"\[\s*(?:\w+\.)*\w*(?:Fact|Theory)\b")
JAVA_TEST = re.compile(r"@(?:Test|ParameterizedTest|RepeatedTest|TestFactory)\b")
CLASS = re.compile(r"\b(?:class|record)\s+([A-Za-z_]\w*)")


def walk(base: pathlib.Path, suffix: str):
    for p in base.rglob(f"*{suffix}"):
        if not IGNORED.intersection(p.relative_to(ROOT).parts):
            yield p


def project_dir(path: pathlib.Path, language: str) -> str:
    """Name of the test project (dotnet: dir holding the csproj; java: module dir above src/test)."""
    parts = path.relative_to(ROOT).parts
    if language == "java" and "src" in parts:
        i = len(parts) - 1 - parts[::-1].index("src")
        return parts[i - 1] if i else ""
    for parent in path.parents:
        if parent == ROOT:
            break
        if any(parent.glob("*.csproj")):
            return parent.name
    return path.parent.name


def discover(language: str):
    """Return {relative file path: set(class names)} for every security test file."""
    suffix, marker = (".cs", CS_TEST) if language == "dotnet" else (".java", JAVA_TEST)
    base = ROOT / ("src/csharp" if language == "dotnet" else "src/java")
    found: dict[str, set[str]] = {}
    for path in walk(base, suffix):
        if language == "java" and "src/test" not in path.as_posix():
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        if not marker.search(text):
            continue
        classes = {c for c in CLASS.findall(text) if not c.endswith("Attribute")}
        if SECURITY.search(path.stem) or SECURITY.search(project_dir(path, language)) or any(
            SECURITY.search(c) for c in classes
        ):
            found[str(path.relative_to(ROOT))] = classes
    return found


def load_exemptions():
    out = {}
    if EXEMPTIONS.exists():
        for line in EXEMPTIONS.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            pattern, _, reason = line.partition("|")
            out[pattern.strip()] = reason.strip() or "(no reason given)"
    return out


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_trx(path: pathlib.Path):
    root = ET.parse(path).getroot()
    names = {}
    for el in root.iter():
        if local(el.tag) == "UnitTest":
            for m in el.iter():
                if local(m.tag) == "TestMethod":
                    names[el.get("id")] = (m.get("className") or "", m.get("name") or "")
    for el in root.iter():
        if local(el.tag) == "UnitTestResult":
            cls, name = names.get(el.get("testId"), ("", el.get("testName") or ""))
            outcome = (el.get("outcome") or "").lower()
            yield cls, name, "executed" if outcome in {"passed", "failed"} else "skipped"


def read_junit(path: pathlib.Path):
    for case in ET.parse(path).getroot().iter():
        if local(case.tag) != "testcase":
            continue
        state = "executed"
        for child in case:
            if local(child.tag) in {"skipped"}:
                state = "skipped"
        yield case.get("classname") or "", case.get("name") or "", state


def results(language: str, dirs):
    for d in dirs:
        base = pathlib.Path(d)
        pattern, reader = ("*.trx", read_trx) if language == "dotnet" else ("TEST-*.xml", read_junit)
        for f in sorted(base.rglob(pattern)):
            try:
                yield from reader(f)
            except ET.ParseError as exc:
                print(f"::error::Unreadable report {f}: {exc}")
                raise SystemExit(1)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--language", choices=["dotnet", "java"], required=True)
    ap.add_argument("--reports", nargs="*", default=[])
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    discovered = discover(args.language)
    if args.list:
        for f, cls in sorted(discovered.items()):
            print(f"{f}: {', '.join(sorted(cls))}")
        print(f"{len(discovered)} security test files", file=sys.stderr)
        return 0
    if not discovered:
        print("::error::No security test files were discovered; the discovery rules are broken.")
        return 1

    security_classes = set().union(*discovered.values())
    exemptions = load_exemptions()
    exempt = lambda s: next((r for p, r in exemptions.items() if re.search(p, s)), None)

    executed: set[str] = set()
    skipped: list[str] = []
    total = 0
    for cls, name, state in results(args.language, args.reports):
        total += 1
        tail = cls.rsplit(".", 1)[-1]
        shorts = {tail, re.split(r"[+$]", tail)[0], *re.split(r"[+$]", tail)}
        full = f"{cls}.{name}"
        if state == "executed":
            executed.update(shorts)
        elif tail in security_classes or SECURITY.search(cls):
            skipped.append(full)

    errors = 0
    if total == 0:
        print("::error::No test results were found in the report directories.")
        return 1

    for s in sorted(set(skipped)):
        reason = exempt(s)
        if reason:
            print(f"exempt (skipped): {s} -- {reason}")
        else:
            print(f"::error::Security test was SKIPPED: {s}")
            errors += 1

    for f, classes in sorted(discovered.items()):
        if classes & executed:
            continue
        reason = exempt(f)
        if reason:
            print(f"exempt (not run): {f} -- {reason}")
        else:
            print(f"::error::Security test file was never executed by any job: {f}")
            errors += 1

    print(
        f"{args.language}: {len(discovered)} security test files, {total} results read, "
        f"{len(set(skipped))} skipped security tests, {errors} problems."
    )
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
