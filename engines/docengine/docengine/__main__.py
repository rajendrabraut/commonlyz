"""docengine: document conversion and AI upscaling engine for Kamala.

Reads one JSON job from stdin, writes output files into the job's output directory and prints a JSON summary
on stdout. Errors are printed as {"error": "..."} with exit code 2 (bad input) or 1 (internal).

Run as: python -I -B -m docengine   (isolated mode: ignores PYTHON* env vars and the user site directory)
"""

import json
import sys
import traceback

from . import BadInput, commands

MAX_JOB_BYTES = 1 << 20


def main() -> int:
    try:
        job = json.loads(sys.stdin.read(MAX_JOB_BYTES))
        command = commands.COMMANDS.get(job.get("command"))
        if command is None:
            raise BadInput(f"Unknown command: {job.get('command')!r}")
        result = command(job)
        print(json.dumps(result))
        return 0
    except BadInput as e:
        print(json.dumps({"error": str(e)}))
        return 2
    except Exception as e:  # noqa: BLE001 - engine boundary: report, never crash silently
        traceback.print_exc(file=sys.stderr)
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
