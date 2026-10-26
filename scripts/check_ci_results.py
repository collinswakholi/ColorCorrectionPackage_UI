"""Required aggregate check: failed prerequisites must never become skipped success."""
import json
import os


def failures(needs: dict) -> list[str]:
    expected = {"changes", "documentation", "packages", "macos_source"}
    if set(needs) != expected:
        return ["missing or unknown validation jobs"]
    problems = [name for name in ("changes", "documentation")
                if needs[name].get("result") != "success"]
    native = needs["changes"].get("outputs", {}).get("native")
    if native not in ("true", "false"):
        problems.append("missing native classification")
    for name in ("packages", "macos_source"):
        required = "success" if native == "true" else "skipped"
        if needs[name].get("result") != required:
            problems.append(name)
    return problems


if __name__ == "__main__":
    problems = failures(json.loads(os.environ["NEEDS_JSON"]))
    if problems:
        raise SystemExit("Validation failed or incomplete: " + ", ".join(problems))
    print("Required documentation/native validation completed for this change set.")
