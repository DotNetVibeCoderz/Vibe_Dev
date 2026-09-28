"""SDK conformance test against a real `dotcode serve` with the deterministic scripted provider."""

import asyncio
import json
import os
import pathlib
import tempfile
import unittest

from dotcode_sdk import DotCodeClient, tool

ROOT = pathlib.Path(__file__).resolve().parents[3]
CLI = os.environ.get("DOTCODE_CLI_PATH") or str(ROOT / "src" / "DotCode.Cli" / "bin" / "Debug" / "net10.0" / "dotcode.dll")


@tool("get_weather", "Weather for a city",
      {"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}, read_only=True)
async def get_weather(args):
    return f"{args['city']}: rainy, 24°C"


@unittest.skipUnless(os.path.exists(CLI), "DotCode CLI not built")
class ConformanceTest(unittest.IsolatedAsyncioTestCase):
    async def test_custom_tool_and_streaming(self):
        with tempfile.TemporaryDirectory() as d:
            script = os.path.join(d, "s.json")
            with open(script, "w", encoding="utf-8") as f:
                json.dump({"responses": [
                    {"text": "Checking the weather.", "toolCalls": [{"name": "get_weather", "input": {"city": "Bogor"}}]},
                    {"text": "It is rainy in Bogor."},
                ]}, f)
            async with DotCodeClient(cli_path=CLI, cwd=d, env={"DOTCODE_CONFIG_DIR": os.path.join(d, ".cfg")}) as client:
                session = await client.create_session(
                    model="mock:scripted", settings={"providers": {"mock": {"type": "mock", "script": script}}},
                    persist_session=False, no_mcp=True, tools=[get_weather])
                events = [e async for e in session.stream("weather?")]
                self.assertEqual(events[-1]["type"], "turn.completed")
                self.assertEqual(events[-1]["resultText"], "It is rainy in Bogor.")
                done = [e for e in events if e["type"] == "tool.completed"]
                self.assertEqual(done[0]["name"], "get_weather")
                self.assertIn("rainy", done[0]["output"])
                self.assertEqual(len(await session.messages()), 4)

    async def test_permission_handler(self):
        with tempfile.TemporaryDirectory() as d:
            script = os.path.join(d, "s.json")
            target = os.path.join(d, "out.txt")
            with open(script, "w", encoding="utf-8") as f:
                json.dump({"responses": [
                    {"toolCalls": [{"name": "Write", "input": {"file_path": target, "content": "from python"}}]},
                    {"text": "written"},
                ]}, f)
            asked = []

            def allow(req):
                asked.append(req["toolName"])
                return {"decision": "allow"}

            async with DotCodeClient(cli_path=CLI, cwd=d, env={"DOTCODE_CONFIG_DIR": os.path.join(d, ".cfg")}) as client:
                session = await client.create_session(
                    model="mock:scripted", settings={"providers": {"mock": {"type": "mock", "script": script}}},
                    persist_session=False, no_mcp=True, on_permission_request=allow)
                result = await session.send("write a file")
                self.assertEqual(result["result"], "written")
                self.assertEqual(asked, ["Write"])
                with open(target, encoding="utf-8") as f:
                    self.assertEqual(f.read(), "from python")


if __name__ == "__main__":
    unittest.main()
