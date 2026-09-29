"""Custom tools implemented by the SDK host."""

from __future__ import annotations

import inspect
import json
import sys
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Dict, List, Literal, Optional, TypeVar, Union, get_type_hints, overload

from .schema import bind, schema_for, to_jsonable


@dataclass(frozen=True)
class ToolInvocation:
    session_id: str
    tool_call_id: str
    tool_name: str
    #: Raw arguments as sent by the model.
    arguments: Any


@dataclass(frozen=True)
class BinaryResult:
    #: Base64 data.
    data: str
    mime_type: str


@dataclass(frozen=True)
class ToolResult:
    """Full control over a tool result."""

    text_result_for_llm: str
    result_type: Literal["success", "failure"] = "success"
    #: Images returned to the model (vision-capable models).
    binary_results_for_llm: List[BinaryResult] = field(default_factory=list)


@dataclass
class Tool:
    """A tool offered to the model. Create it with :func:`define_tool`."""

    name: str
    description: str
    #: Parameter class (dataclass, TypedDict or pydantic model); ``None`` for tools without parameters.
    params_type: Optional[type]
    handler: Callable[..., Any]
    #: Read-only tools may run in plan mode. Host tools never prompt for permission.
    read_only: bool = False

    def to_wire(self) -> Dict[str, Any]:
        schema = schema_for(self.params_type) if self.params_type is not None else {"type": "object", "properties": {}}
        return {"name": self.name, "description": self.description, "inputSchema": schema, "readOnly": self.read_only}

    async def invoke(self, arguments: Any, invocation: ToolInvocation) -> Dict[str, Any]:
        """Runs the handler and returns the wire result (errors are reported to the model)."""
        try:
            args = [bind(self.params_type, arguments if arguments is not None else {})] if self.params_type is not None else []
            if _wants_invocation(self.handler, len(args)):
                args.append(invocation)
            result = self.handler(*args)
            if inspect.isawaitable(result):
                result = await result
            return _result_to_wire(result)
        except Exception as e:  # noqa: BLE001 - tool errors go back to the model
            return {"content": f"Error: {e}", "isError": True}


def _wants_invocation(handler: Callable[..., Any], used: int) -> bool:
    params = [p for p in inspect.signature(handler).parameters.values()
              if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)]
    return len(params) > used


def _result_to_wire(value: Any) -> Dict[str, Any]:
    if isinstance(value, str):
        return {"content": value}
    if isinstance(value, ToolResult):
        if not value.binary_results_for_llm:
            content: Any = value.text_result_for_llm
        else:
            content = [{"type": "text", "text": value.text_result_for_llm}] + [
                {"type": "image", "data": b.data, "mediaType": b.mime_type} for b in value.binary_results_for_llm]
        return {"content": content, "isError": value.result_type == "failure"}
    return {"content": "" if value is None else json.dumps(to_jsonable(value), ensure_ascii=False)}


F = TypeVar("F", bound=Callable[..., Any])


def _params_type_of(fn: Callable[..., Any]) -> Optional[type]:
    params = list(inspect.signature(fn).parameters.values())
    if not params:
        return None
    hints = get_type_hints(fn, globalns=vars(sys.modules[fn.__module__]))
    first = hints.get(params[0].name)
    if first is None:
        raise TypeError(f"{fn.__name__}: annotate the first parameter with a parameter class (dataclass, TypedDict or pydantic model)")
    return None if first is ToolInvocation else first


@overload
def define_tool(fn: F, /) -> Tool: ...
@overload
def define_tool(name: Optional[str] = None, *, description: str, read_only: bool = False) -> Callable[[F], Tool]: ...


def define_tool(name: Any = None, *, description: Optional[str] = None, read_only: bool = False) -> Any:
    """Turns a function into a :class:`Tool`. Parameters come from the type of its first argument::

        @dataclass
        class WeatherParams:
            city: Annotated[str, "City name"]

        @define_tool(description="Weather for a city")
        async def get_weather(params: WeatherParams, invocation: ToolInvocation) -> str:
            return f"{params.city}: sunny"

    The handler takes ``(params)``, ``(params, invocation)``, ``(invocation)`` or nothing, may be sync or async, and
    returns a string, a :class:`ToolResult` or any JSON-serializable value (dataclasses and pydantic models included).
    The tool name defaults to the function name; the description defaults to its docstring.
    """

    def wrap(fn: Callable[..., Any], tool_name: Optional[str]) -> Tool:
        desc = description or inspect.getdoc(fn)
        if not desc:
            raise TypeError(f"{fn.__name__}: pass description=... or add a docstring")
        return Tool(tool_name or fn.__name__, desc, _params_type_of(fn), fn, read_only)

    if callable(name):
        return wrap(name, None)
    return lambda fn: wrap(fn, name)


ToolHandlerResult = Union[str, ToolResult, Any, Awaitable[Any]]
