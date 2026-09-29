"""DotCode Python SDK sample. Run: python samples/sdk/python/example.py azure:gpt-5-mini"""
import asyncio
import pathlib
import sys
from dataclasses import dataclass
from typing import Annotated

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3] / "sdk" / "python" / "src"))
from dotcode_sdk import (  # noqa: E402
    AssistantTextDeltaEvent, DotCodeClient, PermissionDecisionApproveOnce, PermissionRequest, ToolCompletedEvent,
    ToolStartedEvent, define_tool,
)


@dataclass
class ExchangeRateParams:
    from_currency: Annotated[str, "ISO currency code, e.g. USD"]
    to_currency: Annotated[str, "ISO currency code, e.g. IDR"]


@define_tool(description="Get the exchange rate between two currencies", read_only=True)
def get_exchange_rate(params: ExchangeRateParams) -> str:
    rate = "16,250" if params.to_currency == "IDR" else "0.92"
    return f"1 {params.from_currency} = {rate} {params.to_currency} (demo data)"


def allow(request: PermissionRequest, _invocation: object) -> PermissionDecisionApproveOnce:
    print(f"  [permission] {request.display_name} → allowed")
    return PermissionDecisionApproveOnce()


async def main() -> None:
    async with DotCodeClient() as client:
        async with await client.create_session(
            model=sys.argv[1] if len(sys.argv) > 1 else None, persist_session=False,
            tools=[get_exchange_rate], on_permission_request=allow,
        ) as session:
            session.on(AssistantTextDeltaEvent, lambda e: print(e.text, end="", flush=True))
            session.on(ToolStartedEvent, lambda e: print(f"● {e.display_name}"))
            session.on(ToolCompletedEvent, lambda e: print(f"  ⎿  {e.output}"))
            print(f"DotCode SDK (Python) · model {session.model}\n")
            result = await session.send_and_wait(
                "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.")
            print(f"\n\n✔ {result.num_model_calls} model calls · ${result.cost_usd:.4f} · {result.duration_ms} ms")


asyncio.run(main())
