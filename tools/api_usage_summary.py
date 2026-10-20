"""Summarize locally recorded API attempts and provider-reported tokens."""
import argparse
from collections import defaultdict
import json
import os
from pathlib import Path


def summarize(directory):
    requests = {}
    events = []
    for path in Path(directory).glob("*.jsonl"):
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            try:
                record = json.loads(line)
                events.append(record)
                data = record["data"]
                if data["event_type"] == "request":
                    requests[data["request_id"]] = record
            except (ValueError, KeyError):
                continue  # A process may still be writing the final line.
    groups = defaultdict(lambda: dict(attempts=0, errors=0, missing_usage=0,
                                     input_tokens=0, output_tokens=0, cached_tokens=0,
                                     reasoning_tokens=0, total_tokens=0))
    streams = {r.get("logical_call_id"): r["data"].get("usage") for r in events
               if r["data"]["event_type"] == "stream_usage"}
    responses = {r["data"]["request_id"]: r["data"] for r in events
                 if r["data"]["event_type"] in ("response", "error")}
    for request_id, record in requests.items():
        body = json.loads(record["data"].get("request_body") or "{}")
        context = record.get("context") or {}
        group = groups[(body.get("model", "unknown"), context.get("trigger", "unknown"))]
        group["attempts"] += 1
        response = responses.get(request_id, {})
        if response.get("event_type") == "error" or response.get("status_code", 200) >= 400:
            group["errors"] += 1
        usage = response.get("usage")
        if response.get("streaming"):
            usage = streams.get(record.get("logical_call_id"))
        if usage is None:
            group["missing_usage"] += 1
            continue
        group["input_tokens"] += usage.get("input_tokens", usage.get("prompt_tokens", 0))
        group["output_tokens"] += usage.get("output_tokens", usage.get("completion_tokens", 0))
        group["total_tokens"] += usage.get("total_tokens", 0)
        group["cached_tokens"] += (usage.get("cached_tokens") or
            (usage.get("input_tokens_details") or usage.get("prompt_tokens_details") or {}).get("cached_tokens", 0))
        group["reasoning_tokens"] += (usage.get("reasoning_tokens") or
            (usage.get("output_tokens_details") or usage.get("completion_tokens_details") or {}).get("reasoning_tokens", 0))
    return [dict(model=model, trigger=trigger, **values)
            for (model, trigger), values in sorted(groups.items())]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", default=os.environ.get("LAVENDER_API_LOG_DIRECTORY") or
                        str(Path(os.environ.get("LOCALAPPDATA", Path.home())) / "Lavender" / "ApiUsage"))
    args = parser.parse_args()
    print(json.dumps(summarize(args.directory), indent=2))
