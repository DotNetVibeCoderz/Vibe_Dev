"""Static typing checks (run: mypy tests/typecheck). Lines marked `# E:` must be reported by mypy."""
from dataclasses import dataclass
from typing import Annotated

from pydantic import BaseModel, Field

from dotcode_sdk import (BuiltinTool, DotCodeClient, PermissionDecisionReject, PermissionHandler, ProviderConfig,
                         SystemMessageConfig, ToolCompletedEvent, define_tool)


@dataclass
class WeatherParams:
    city: Annotated[str, "City name"]


class IssueParams(BaseModel):
    id: str = Field(description="Issue identifier")


@define_tool(description="Weather")
def weather(params: WeatherParams) -> str:
    return params.cty  # E: misspelled attribute


@define_tool(description="Issue")
def issue(params: IssueParams) -> str:
    return params.id


async def main() -> None:
    client = DotCodeClient()
    session = await client.create_session(
        model="openai:gpt-5",
        tools=[weather, issue],
        available_tools=[BuiltinTool.READ, BuiltinTool.GREP],
        allowed_tools=[BuiltinTool.BASH.rule("npm test:*")],
        providers={"local": ProviderConfig(type="ollama", base_url="http://localhost:11434")},
        system_message=SystemMessageConfig("Be brief."),
        on_permission_request=PermissionHandler.approve_all,
        permission_mode="acceptEdit",  # E: invalid literal
    )
    await client.create_session(providers={"x": ProviderConfig(type="open-ai")})  # E: invalid provider type
    await client.create_session(available_tools=[BuiltinTool.RAED])  # E: unknown built-in tool
    await client.create_session(on_permission_request=lambda req, inv: PermissionDecisionReject(req.tool_nam))  # E: typo
    await client.create_session(modle="x")  # E: unknown option
    session.on(ToolCompletedEvent, lambda e: print(e.outptu))  # E: typo in event field
    result = await session.send_and_wait("hi")
    print(result.cost_usd)
