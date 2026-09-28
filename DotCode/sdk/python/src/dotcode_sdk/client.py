"""Async client for the DotCode JSON-RPC server (``dotcode serve``)."""

from __future__ import annotations

import asyncio
import inspect
import json
import os
import shutil
from typing import Any, AsyncIterator, Callable, Dict, List, Optional, Union

from .types import (
    AgentEvent,
    EventHandler,
    PermissionDecision,
    PermissionHandler,
    QuestionHandler,
    SendResult,
    Tool,
)

PROTOCOL_VERSION = "1.0"


class DotCodeError(Exception):
    def __init__(self, code: int, message: str):
        super().__init__(message)
        self.code = code


async def _maybe_await(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


def tool(name: str, description: str, input_schema: Optional[Dict[str, Any]] = None, read_only: bool = False):
    """Decorator turning a (sync or async) function into a :class:`Tool`.

    ::

        @tool("get_weather", "Weather for a city", {"type": "object", "properties": {"city": {"type": "string"}}})
        async def get_weather(args):
            return f"{args['city']}: sunny"
    """

    def wrap(fn: Callable[[Dict[str, Any]], Any]) -> Tool:
        return Tool(name=name, description=description, handler=fn,
                    input_schema=input_schema or {"type": "object", "properties": {}}, read_only=read_only)

    return wrap


class DotCodeClient:
    """Starts ``dotcode serve`` and manages sessions. Use as an async context manager."""

    def __init__(self, cli_path: Optional[str] = None, cwd: Optional[str] = None,
                 env: Optional[Dict[str, str]] = None, server_args: Optional[List[str]] = None):
        self._cli = cli_path or os.environ.get("DOTCODE_CLI_PATH") or shutil.which("dotcode") or "dotcode"
        self._cwd = cwd
        self._env = env or {}
        self._server_args = server_args or []
        self._proc: Optional[asyncio.subprocess.Process] = None
        self._next_id = 1
        self._pending: Dict[int, asyncio.Future] = {}
        self._sessions: Dict[str, "Session"] = {}
        self._reader_task: Optional[asyncio.Task] = None
        self._write_lock = asyncio.Lock()
        self.server_version: Optional[str] = None

    async def __aenter__(self) -> "DotCodeClient":
        await self.start()
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.close()

    async def start(self) -> None:
        if self._proc is not None:
            return
        args = ["dotnet", self._cli, "serve"] if self._cli.endswith(".dll") else [self._cli, "serve"]
        env = {**os.environ, **self._env}
        try:
            self._proc = await asyncio.create_subprocess_exec(
                *args, *self._server_args,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                cwd=self._cwd, env=env, limit=64 * 1024 * 1024)
        except FileNotFoundError as e:
            raise RuntimeError(f"Could not start '{self._cli}'. Install the DotCode CLI or set DOTCODE_CLI_PATH.") from e
        self._reader_task = asyncio.create_task(self._read_loop())
        init = await self._request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "clientInfo": {"name": "dotcode-sdk-python", "version": "0.1.0"},
            "capabilities": {"permissions": True, "questions": True},
        })
        self.server_version = (init or {}).get("serverInfo", {}).get("version")

    async def close(self) -> None:
        if self._proc is None:
            return
        try:
            await asyncio.wait_for(self._request("shutdown", {}), timeout=1.5)
        except Exception:
            pass
        if self._proc.stdin:
            self._proc.stdin.close()
        try:
            await asyncio.wait_for(self._proc.wait(), timeout=3)
        except asyncio.TimeoutError:
            self._proc.kill()
        if self._reader_task:
            self._reader_task.cancel()
        self._proc = None

    # ------------------------------------------------------------------ JSON-RPC plumbing

    async def _write(self, msg: Dict[str, Any]) -> None:
        assert self._proc and self._proc.stdin
        data = (json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8")
        async with self._write_lock:
            self._proc.stdin.write(data)
            await self._proc.stdin.drain()

    async def _request(self, method: str, params: Dict[str, Any]) -> Any:
        msg_id = self._next_id
        self._next_id += 1
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[msg_id] = fut
        await self._write({"jsonrpc": "2.0", "id": msg_id, "method": method, "params": params})
        return await fut

    async def _read_loop(self) -> None:
        assert self._proc and self._proc.stdout
        while True:
            line = await self._proc.stdout.readline()
            if not line:
                break
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "method" not in msg:
                fut = self._pending.pop(msg.get("id"), None)
                if fut and not fut.done():
                    if "error" in msg:
                        fut.set_exception(DotCodeError(msg["error"].get("code", 0), msg["error"].get("message", "error")))
                    else:
                        fut.set_result(msg.get("result"))
                continue
            params = msg.get("params") or {}
            if msg.get("id") is None:
                if msg["method"] == "session.event":
                    session = self._sessions.get(params.get("sessionId"))
                    if session:
                        session._dispatch(params.get("event", {}))
                continue
            asyncio.create_task(self._answer(msg["id"], msg["method"], params))
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(RuntimeError("DotCode server connection closed"))

    async def _answer(self, msg_id: Any, method: str, params: Dict[str, Any]) -> None:
        session = self._sessions.get(params.get("sessionId"))
        try:
            if session is None:
                raise DotCodeError(-32001, "Unknown session")
            result = await session._handle_server_request(method, params)
            await self._write({"jsonrpc": "2.0", "id": msg_id, "result": result})
        except DotCodeError as e:
            await self._write({"jsonrpc": "2.0", "id": msg_id, "error": {"code": e.code, "message": str(e)}})
        except Exception as e:  # noqa: BLE001 - report handler failures to the server
            await self._write({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32603, "message": str(e)}})

    # ------------------------------------------------------------------ API

    async def create_session(self, *, model: Optional[str] = None, cwd: Optional[str] = None,
                             permission_mode: Optional[str] = None, system_prompt: Optional[str] = None,
                             append_system_prompt: Optional[str] = None, allowed_tools: Optional[List[str]] = None,
                             disallowed_tools: Optional[List[str]] = None, builtin_tools: Optional[List[str]] = None,
                             tools: Optional[List[Tool]] = None, mcp_servers: Optional[Dict[str, Any]] = None,
                             settings: Optional[Dict[str, Any]] = None, max_turns: Optional[int] = None,
                             effort: Optional[str] = None, persist_session: bool = True, no_mcp: bool = False,
                             worktree: Optional[Union[bool, str]] = None,
                             on_permission_request: Optional[PermissionHandler] = None,
                             on_question: Optional[QuestionHandler] = None,
                             on_plan_review: Optional[Callable[[str], Any]] = None,
                             on_event: Optional[EventHandler] = None,
                             resume_session_id: Optional[str] = None, fork: bool = False) -> "Session":
        """Create (or resume) an agent session. Without ``on_permission_request`` the session is deny-by-default."""
        await self.start()
        params: Dict[str, Any] = {
            "cwd": cwd or self._cwd, "model": model, "permissionMode": permission_mode,
            "systemPrompt": system_prompt, "appendSystemPrompt": append_system_prompt,
            "allowedTools": allowed_tools, "disallowedTools": disallowed_tools, "tools": builtin_tools,
            "hostTools": [t.to_wire() for t in tools] if tools else None,
            "mcpServers": mcp_servers, "settings": settings, "maxTurns": max_turns, "effort": effort,
            "persistSession": persist_session, "noMcp": no_mcp, "worktree": worktree or None,
        }
        params = {k: v for k, v in params.items() if v is not None}
        if resume_session_id:
            params["sessionId"] = resume_session_id
            params["fork"] = fork
        info = await self._request("session.resume" if resume_session_id else "session.create", params)
        session = Session(self, info, tools or [], on_permission_request, on_question, on_plan_review, on_event)
        self._sessions[session.id] = session
        return session

    async def list_models(self) -> Dict[str, Any]:
        await self.start()
        return await self._request("models.list", {"cwd": self._cwd} if self._cwd else {})

    async def list_sessions(self) -> Dict[str, Any]:
        await self.start()
        return await self._request("session.list", {"cwd": self._cwd} if self._cwd else {})


class Session:
    def __init__(self, client: DotCodeClient, info: Dict[str, Any], tools: List[Tool],
                 on_permission_request: Optional[PermissionHandler], on_question: Optional[QuestionHandler],
                 on_plan_review: Optional[Callable[[str], Any]], on_event: Optional[EventHandler]):
        self._client = client
        self.info = info
        self.id: str = info["sessionId"]
        self.model: str = info.get("model", "")
        self._tools = {t.name: t for t in tools}
        self._on_permission = on_permission_request
        self._on_question = on_question
        self._on_plan_review = on_plan_review
        self._on_event = on_event
        self._queues: List[asyncio.Queue] = []

    def _dispatch(self, event: AgentEvent) -> None:
        if event.get("type") == "model.changed":
            self.model = event.get("model", self.model)  # type: ignore[call-overload]
        if self._on_event:
            self._on_event(event)
        for q in self._queues:
            q.put_nowait(event)

    async def _handle_server_request(self, method: str, params: Dict[str, Any]) -> Any:
        if method == "permission.request":
            if not self._on_permission:
                return {"decision": "deny", "feedback": "No permission handler registered in the SDK host (deny by default)."}
            decision: PermissionDecision = await _maybe_await(self._on_permission(params["request"]))
            return decision
        if method == "user.question":
            answers = await _maybe_await(self._on_question(params["questions"])) if self._on_question else []
            return {"answers": answers}
        if method == "plan.review":
            ok = True if not self._on_plan_review else await _maybe_await(self._on_plan_review(params.get("plan", "")))
            return {"approval": "approve" if ok else "reject"}
        if method == "tool.call":
            t = self._tools.get(params.get("name"))
            if t is None:
                raise DotCodeError(-32601, f"Unknown host tool {params.get('name')}")
            try:
                content = await _maybe_await(t.handler(params.get("input") or {}))
                return {"content": content if isinstance(content, (str, list)) else json.dumps(content)}
            except Exception as e:  # noqa: BLE001 - tool errors are reported to the model
                return {"content": f"Error: {e}", "isError": True}
        raise DotCodeError(-32601, method)

    async def send(self, prompt: str, attachments: Optional[List[Dict[str, Any]]] = None) -> SendResult:
        """Run a prompt to completion and return the final result."""
        params: Dict[str, Any] = {"sessionId": self.id, "prompt": prompt}
        if attachments:
            params["attachments"] = attachments
        return await self._client._request("session.send", params)

    async def stream(self, prompt: str) -> AsyncIterator[AgentEvent]:
        """Run a prompt and yield events as they arrive; the last event is ``turn.completed``."""
        queue: asyncio.Queue = asyncio.Queue()
        self._queues.append(queue)
        task = asyncio.create_task(self.send(prompt))
        try:
            while True:
                getter = asyncio.create_task(queue.get())
                done, _ = await asyncio.wait({getter, task}, return_when=asyncio.FIRST_COMPLETED)
                if getter in done:
                    event = getter.result()
                    yield event
                    if event.get("type") == "turn.completed" and not event.get("parentToolUseId"):
                        break
                else:
                    getter.cancel()
                    while not queue.empty():
                        yield queue.get_nowait()
                    break
            await task
        finally:
            self._queues.remove(queue)

    async def abort(self) -> None:
        await self._client._request("session.abort", {"sessionId": self.id})

    async def set_model(self, model: str) -> None:
        await self._client._request("session.setModel", {"sessionId": self.id, "model": model})
        self.model = model

    async def set_permission_mode(self, mode: str) -> None:
        await self._client._request("session.setMode", {"sessionId": self.id, "mode": mode})

    async def compact(self, instructions: Optional[str] = None) -> None:
        await self._client._request("session.compact", {"sessionId": self.id, "instructions": instructions})

    async def messages(self) -> List[Dict[str, Any]]:
        return (await self._client._request("session.messages", {"sessionId": self.id}))["messages"]

    async def close(self) -> None:
        await self._client._request("session.close", {"sessionId": self.id})
        self._client._sessions.pop(self.id, None)
