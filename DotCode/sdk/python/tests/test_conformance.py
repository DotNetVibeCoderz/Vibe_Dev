"""SDK conformance test against a real `dotcode serve` with the deterministic scripted provider."""

import asyncio
import json
import os
import pathlib
import tempfile
import unittest
from dataclasses import dataclass
from typing import Annotated, Any, Dict, List

from dotcode_sdk import (
    DotCodeClient,
    PermissionDecisionApproveOnce,
    PermissionRequest,
    ProviderConfig,
    ToolCompletedEvent,
    ToolInvocation,
    TurnCompletedEvent,
    define_tool,
)

ROOT = pathlib.Path(__file__).resolve().parents[3]
CLI = os.environ.get("DOTCODE_CLI_PATH") or str(ROOT / "src" / "DotCode.Cli" / "bin" / "Debug" / "net10.0" / "dotcode.dll")


@dataclass
class WeatherParams:
    city: Annotated[str, "City name"]


@define_tool(description="Weather for a city", read_only=True)
async def get_weather(params: WeatherParams, invocation: ToolInvocation) -> str:
    return f"{params.city}: rainy, 24°C ({invocation.tool_name})"


def scripted(d: str, responses: List[Dict[str, Any]]) -> Dict[str, Any]:
    script = os.path.join(d, "s.json")
    with open(script, "w", encoding="utf-8") as f:
        json.dump({"responses": responses}, f)
    return dict(model="mock:scripted", providers={"mock": ProviderConfig(type="mock", script=script)},
                persist_session=False, disable_mcp=True)


def client_for(d: str) -> DotCodeClient:
    return DotCodeClient(cli_path=CLI, cwd=d, env={"DOTCODE_CONFIG_DIR": os.path.join(d, ".cfg")})


@unittest.skipUnless(os.path.exists(CLI), "DotCode CLI not built")
class ConformanceTest(unittest.IsolatedAsyncioTestCase):
    async def test_custom_tool_and_streaming(self):
        with tempfile.TemporaryDirectory() as d:
            config = scripted(d, [
                {"text": "Checking the weather.", "toolCalls": [{"name": "get_weather", "input": {"city": "Bogor"}}]},
                {"text": "It is rainy in Bogor."},
            ])
            async with client_for(d) as client:
                async with await client.create_session(**config, tools=[get_weather]) as session:
                    completed: List[ToolCompletedEvent] = []
                    session.on(ToolCompletedEvent, completed.append)
                    events = [e async for e in session.stream("weather?")]
                    last = events[-1]
                    assert isinstance(last, TurnCompletedEvent)
                    self.assertEqual(last.result_text, "It is rainy in Bogor.")
                    self.assertEqual(completed[0].name, "get_weather")
                    self.assertIn("rainy, 24°C (get_weather)", completed[0].output)
                    self.assertEqual(len(await session.get_messages()), 4)

    async def test_permission_handler(self):
        with tempfile.TemporaryDirectory() as d:
            target = os.path.join(d, "out.txt")
            config = scripted(d, [
                {"toolCalls": [{"name": "Write", "input": {"file_path": target, "content": "from python"}}]},
                {"text": "written"},
            ])
            asked = []

            def allow(req: PermissionRequest, _):
                asked.append(req.tool_name)
                return PermissionDecisionApproveOnce()

            async with client_for(d) as client:
                session = await client.create_session(**config, on_permission_request=allow)
                done = asyncio.Event()
                results: List[TurnCompletedEvent] = []
                session.on(TurnCompletedEvent, lambda e: (results.append(e), done.set()))
                await session.send("write a file")
                await asyncio.wait_for(done.wait(), 30)
                self.assertEqual(results[0].result_text, "written")
                self.assertEqual(asked, ["Write"])
                with open(target, encoding="utf-8") as f:
                    self.assertEqual(f.read(), "from python")

    async def test_send_and_wait_reports_invalid_arguments(self):
        with tempfile.TemporaryDirectory() as d:
            config = scripted(d, [
                {"toolCalls": [{"name": "get_weather", "input": {"city": 42}}]},
                {"text": "done"},
            ])
            async with client_for(d) as client:
                session = await client.create_session(**config, tools=[get_weather])
                completed: List[ToolCompletedEvent] = []
                session.on(ToolCompletedEvent, completed.append)
                result = await session.send_and_wait("weather?", timeout=30)
                self.assertEqual(result.result, "done")
                self.assertTrue(completed[0].is_error)
                self.assertIn("city", completed[0].output)


if __name__ == "__main__":
    unittest.main()
