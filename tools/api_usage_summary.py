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
    return [dict(model=model, trigger=trigger, uncached_input_tokens=max(0, values["input_tokens"] - values["cached_tokens"]),
                 input_cache_hit_percent=round(100 * values["cached_tokens"] / values["input_tokens"], 2) if values["input_tokens"] else 0, **values)
            for (model, trigger), values in sorted(groups.items())]


def summarize_workflow(directory):
    totals = dict(reads=0, range_cache_hits=0, fully_covered_reads=0,
                  read_characters_saved=0, edit_batches=0, edits_in_batches=0,
                  retired_source_results=0, retired_images=0, edit_characters_saved=0,
                  diagnostics_requests=0, diagnostics_cache_hits=0, blocked_repeats=0,
                  recovered_hashes=0, rejected_arguments=0, combined_tool_calls=0,
                  retired_edit_exchanges=0, retired_edit_argument_characters=0)
    for path in Path(directory).glob("*.jsonl"):
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                try:
                    data = json.loads(line).get("data", {})
                except ValueError:
                    continue
                event = data.get("event_type")
                if event == "read_range_cache":
                    totals["reads"] += 1
                    totals["range_cache_hits"] += bool(data.get("cache_hit"))
                    totals["fully_covered_reads"] += bool(data.get("fully_covered"))
                    totals["read_characters_saved"] += data.get("characters_saved", 0)
                elif event == "edit_batch":
                    totals["edit_batches"] += 1
                    totals["edits_in_batches"] += data.get("edit_count", 0)
                elif event == "context_compacted":
                    totals["retired_source_results"] += data.get("removed_snapshots", 0)
                    totals["retired_images"] += data.get("retired_images", 0)
                    totals["retired_edit_exchanges"] += data.get("retired_edit_exchanges", 0)
                    totals["retired_edit_argument_characters"] += data.get("retired_edit_argument_characters", 0)
                elif event == "edit_context_compacted":
                    totals["edit_characters_saved"] += data.get("characters_saved", 0)
                elif event == "diagnostics_snapshot":
                    totals["diagnostics_requests"] += 1
                    totals["diagnostics_cache_hits"] += bool(data.get("cached"))
                elif event == "tool_hash_recovered":
                    totals["recovered_hashes"] += 1
                elif event == "tool_arguments_rejected":
                    totals["rejected_arguments"] += 1
                elif event == "tool_calls_batched":
                    totals["combined_tool_calls"] += data.get("calls", 0)
                elif event == "tool_repeat_blocked":
                    totals["blocked_repeats"] += 1
    return totals


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", default=os.environ.get("LAVENDER_API_LOG_DIRECTORY") or
                        str(Path(os.environ.get("LOCALAPPDATA", Path.home())) / "Lavender" / "ApiUsage"))
    parser.add_argument("--workflow", action="store_true", help="Include range-cache, batch, compaction and diagnostics counters")
    args = parser.parse_args()
    result = summarize(args.directory)
    if args.workflow:
        result = dict(api_usage=result, workflow=summarize_workflow(args.directory))
    print(json.dumps(result, indent=2))
