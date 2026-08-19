#!/usr/bin/env python3
"""Build a Docker container-create payload from a trusted docker inspect snapshot."""

import json
import pathlib
import sys


MANAGED_BOT_MEMORY_BYTES = 768 * 1024 * 1024


def main() -> int:
    if len(sys.argv) != 4:
        print(
            "Usage: build-managed-bot-payload.py <inspect.json> <image> <payload.json>",
            file=sys.stderr,
        )
        return 2

    inspect_path = pathlib.Path(sys.argv[1])
    target_image = sys.argv[2]
    payload_path = pathlib.Path(sys.argv[3])

    inspected = json.loads(inspect_path.read_text(encoding="utf-8"))
    if not isinstance(inspected, list) or len(inspected) != 1:
        raise ValueError("inspect snapshot must contain exactly one container")

    container = inspected[0]
    config = dict(container["Config"])
    config["Image"] = target_image

    endpoints = {}
    old_id = container["Id"]
    for network_name, endpoint in container["NetworkSettings"]["Networks"].items():
        aliases = [
            alias
            for alias in endpoint.get("Aliases") or []
            if alias not in {old_id, old_id[:12]}
        ]
        preserved = {
            "IPAMConfig": endpoint.get("IPAMConfig"),
            "Links": endpoint.get("Links"),
            "Aliases": aliases or None,
            "MacAddress": endpoint.get("MacAddress"),
            "DriverOpts": endpoint.get("DriverOpts"),
        }
        if endpoint.get("GwPriority") is not None:
            preserved["GwPriority"] = endpoint["GwPriority"]
        endpoints[network_name] = {
            key: value for key, value in preserved.items() if value is not None
        }

    host_config = dict(container["HostConfig"])
    host_config["Memory"] = MANAGED_BOT_MEMORY_BYTES

    payload = config
    payload["HostConfig"] = host_config
    payload["NetworkingConfig"] = {"EndpointsConfig": endpoints}
    payload_path.write_text(
        json.dumps(payload, separators=(",", ":")),
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
