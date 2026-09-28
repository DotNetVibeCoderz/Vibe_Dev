"""DotCode Python SDK sample. Run: python samples/sdk/python/example.py azure:gpt-5-mini"""
import asyncio
import pathlib
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3] / "sdk" / "python" / "src"))
from dotcode_sdk import DotCodeClient, tool  # noqa: E402


@tool("get_exchange_rate", "Get the exchange rate between two currencies",
      {"type": "object", "properties": {"from": {"type": "string"}, "to": {"type": "string"}}, "required": ["from", "to"]},
      read_only=True)
def exchange_rate(args):
    return f"1 {args['from']} = {'16,250' if args['to'] == 'IDR' else '0.92'} {args['to']} (demo data)"


async def main():
    async with DotCodeClient() as client:
        session = await client.create_session(
            model=sys.argv[1] if len(sys.argv) > 1 else None, persist_session=False, tools=[exchange_rate],
            on_permission_request=lambda req: {"decision": "allow"})
        print(f"DotCode SDK (Python) · model {session.model}\n")
        async for e in session.stream("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence."):
            if e["type"] == "assistant.text.delta":
                print(e["text"], end="", flush=True)
            elif e["type"] == "tool.started":
                print(f"● {e['displayName']}")
            elif e["type"] == "tool.completed":
                print(f"  ⎿  {e['output']}")
            elif e["type"] == "turn.completed":
                print(f"\n\n✔ {e['numModelCalls']} model calls · ${e['costUsd']:.4f} · {e['durationMs']} ms")


asyncio.run(main())
