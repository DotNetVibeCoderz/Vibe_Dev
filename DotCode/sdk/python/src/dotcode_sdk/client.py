"""Async client for the DotCode JSON-RPC server (``dotcode serve``)."""

from __future__ import annotations

import asyncio
import inspect
import json
import os
import shutil
from typing import (Any, AsyncIterator, Callable, Dict, List, Mapping, Optional, Sequence, Type, TypeVar, Union,
                    overload)

from .events import ModelChangedEvent, SessionEvent, TurnCompletedEvent, ErrorEvent, parse_event
from .tools import Tool, ToolInvocation
from .types import (
    Attachment, BuiltinTool, ExitPlanModeHandler, ExitPlanModeRequest, ExitPlanModeResult, Invocation, McpServerConfig,
    ModelInfo, PermissionDecisionReject, PermissionHandlerFunc, PermissionMode, PermissionRequest, ProviderConfig,
    ReasoningEffort, SendResult, SessionMetadata, SystemMessageConfig, ToolInfo, UserInputHandler, UserInputRequest,
    attachment_to_wire, decision_to_wire,
)

PROTOCOL_VERSION = "1.0"
SDK_VERSION = "0.2.0"

E = TypeVar("E")
EventHandler = Callable[[SessionEvent], None]


class DotCodeError(Exception):
    """A JSON-RPC error returned by the server."""

    def __init__(self, code: int, message: str):
        super().__init__(message)
        self.code = code


async def _maybe_await(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


class DotCodeClient:
    """Starts ``dotcode serve`` and manages sessions. Use as an async context manager::

        async with DotCodeClient() as client:
            async with await client.create_session(model="openai:gpt-5",
                                                   on_permission_request=PermissionHandler.approve_all) as session:
                result = await session.send_and_wait("Summarize README.md")
    """

    def __init__(self, *, cli_path: Optional[str] = None, cli_args: Optional[Sequence[str]] = None,
                 cwd: Optional[str] = None, env: Optional[Mapping[str, str]] = None):
        self._cli = cli_path or os.environ.get("DOTCODE_CLI_PATH") or shutil.which("dotcode") or "dotcode"
        self._cwd = cwd
        self._env = dict(env or {})
        self._cli_args = list(cli_args or [])
        self._proc: Optional[asyncio.subprocess.Process] = None
        self._next_id = 1
        self._pending: Dict[int, "asyncio.Future[Any]"] = {}
        self._sessions: Dict[str, "DotCodeSession"] = {}
        self._early: Dict[str, List[SessionEvent]] = {}
        self._opening = 0
        self._reader_task: Optional["asyncio.Task[None]"] = None
        self._write_lock = asyncio.Lock()
        self.server_version: Optional[str] = None

    async def __aenter__(self) -> "DotCodeClient":
        await self.start()
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.stop()

    async def start(self) -> None:
        """Starts the server and performs the protocol handshake (other methods call it lazily)."""
        if self._proc is not None:
            return
        args = ["dotnet", self._cli, "serve"] if self._cli.endswith(".dll") else [self._cli, "serve"]
        try:
            self._proc = await asyncio.create_subprocess_exec(
                *args, *self._cli_args,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                cwd=self._cwd, env={**os.environ, **self._env}, limit=64 * 1024 * 1024)
        except FileNotFoundError as e:
            raise RuntimeError(f"Could not start '{self._cli}'. Install the DotCode CLI or set DOTCODE_CLI_PATH.") from e
        self._reader_task = asyncio.create_task(self._read_loop())
        init = await self._request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "clientInfo": {"name": "dotcode-sdk-python", "version": SDK_VERSION},
            "capabilities": {"permissions": True, "questions": True},
        })
        self.server_version = (init or {}).get("serverInfo", {}).get("version")

    async def stop(self) -> None:
        """Disconnects all sessions and stops the server."""
        if self._proc is None:
            return
        try:
            await asyncio.wait_for(self._request("shutdown", {}), timeout=1.5)
        except Exception:  # noqa: BLE001
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
        self._sessions.clear()

    async def force_stop(self) -> None:
        """Kills the server without a graceful shutdown."""
        if self._proc is not None:
            self._proc.kill()
            await self._proc.wait()
        if self._reader_task:
            self._reader_task.cancel()
        self._proc = None
        self._sessions.clear()

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
        fut: "asyncio.Future[Any]" = asyncio.get_running_loop().create_future()
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
                    self._on_event(params.get("sessionId"), params.get("event") or {})
                continue
            asyncio.create_task(self._answer(msg["id"], msg["method"], params))
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(RuntimeError("DotCode server connection closed"))

    def _on_event(self, session_id: Optional[str], raw: Dict[str, Any]) -> None:
        event = parse_event(raw)
        session = self._sessions.get(session_id or "")
        if session:
            session._dispatch(event)
        elif self._opening > 0 and session_id:
            self._early.setdefault(session_id, []).append(event)

    async def _answer(self, msg_id: Any, method: str, params: Dict[str, Any]) -> None:
        session = self._sessions.get(params.get("sessionId", ""))
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

    async def create_session(
        self, *,
        model: Optional[str] = None,
        fallback_model: Optional[str] = None,
        working_directory: Optional[str] = None,
        permission_mode: Optional[PermissionMode] = None,
        reasoning_effort: Optional[ReasoningEffort] = None,
        system_message: Optional[SystemMessageConfig] = None,
        tools: Optional[Sequence[Tool]] = None,
        available_tools: Optional[Sequence[BuiltinTool]] = None,
        allowed_tools: Optional[Sequence[str]] = None,
        excluded_tools: Optional[Sequence[str]] = None,
        mcp_servers: Optional[Mapping[str, McpServerConfig]] = None,
        disable_mcp: bool = False,
        providers: Optional[Mapping[str, ProviderConfig]] = None,
        settings: Optional[Mapping[str, Any]] = None,
        max_turns: Optional[int] = None,
        persist_session: bool = True,
        worktree: Union[bool, str, None] = None,
        on_permission_request: Optional[PermissionHandlerFunc] = None,
        on_user_input_request: Optional[UserInputHandler] = None,
        on_exit_plan_mode: Optional[ExitPlanModeHandler] = None,
        on_event: Optional[EventHandler] = None,
    ) -> "DotCodeSession":
        """Creates a session. Without ``on_permission_request`` it is deny-by-default.

        :param model: ``provider:model``, alias or role, e.g. ``"anthropic:claude-sonnet-4-5"``, ``"ollama:qwen3-coder"``.
        :param available_tools: restrict the built-in tools.
        :param allowed_tools: permission rules to pre-approve, e.g. ``BuiltinTool.BASH.rule("npm test:*")``.
        :param excluded_tools: tools to remove / rules to deny.
        :param providers: named providers (BYOK), referenced as ``"<name>:<model>"``.
        :param settings: advanced: raw settings merged over the settings files.
        """
        return await self._open("session.create", {}, locals())

    async def resume_session(
        self, session_id: str, *,
        fork: bool = False,
        model: Optional[str] = None,
        fallback_model: Optional[str] = None,
        working_directory: Optional[str] = None,
        permission_mode: Optional[PermissionMode] = None,
        reasoning_effort: Optional[ReasoningEffort] = None,
        system_message: Optional[SystemMessageConfig] = None,
        tools: Optional[Sequence[Tool]] = None,
        available_tools: Optional[Sequence[BuiltinTool]] = None,
        allowed_tools: Optional[Sequence[str]] = None,
        excluded_tools: Optional[Sequence[str]] = None,
        mcp_servers: Optional[Mapping[str, McpServerConfig]] = None,
        disable_mcp: bool = False,
        providers: Optional[Mapping[str, ProviderConfig]] = None,
        settings: Optional[Mapping[str, Any]] = None,
        max_turns: Optional[int] = None,
        persist_session: bool = True,
        worktree: Union[bool, str, None] = None,
        on_permission_request: Optional[PermissionHandlerFunc] = None,
        on_user_input_request: Optional[UserInputHandler] = None,
        on_exit_plan_mode: Optional[ExitPlanModeHandler] = None,
        on_event: Optional[EventHandler] = None,
    ) -> "DotCodeSession":
        """Resumes a saved session (``fork=True`` continues under a new id). Options as in :meth:`create_session`."""
        config = locals()
        return await self._open("session.resume", {"sessionId": config.pop("session_id"), "fork": config.pop("fork")}, config)

    async def _open(self, method: str, extra: Dict[str, Any], c: Dict[str, Any]) -> "DotCodeSession":
        await self.start()
        system: Optional[SystemMessageConfig] = c.get("system_message")
        settings: Dict[str, Any] = dict(c.get("settings") or {})
        if c.get("providers"):
            settings["providers"] = {**settings.get("providers", {}), **{k: v.to_wire() for k, v in c["providers"].items()}}
        tools: List[Tool] = list(c.get("tools") or [])
        params: Dict[str, Any] = {
            "cwd": c.get("working_directory") or self._cwd,
            "model": c.get("model"),
            "fallbackModel": c.get("fallback_model"),
            "permissionMode": c.get("permission_mode"),
            "effort": c.get("reasoning_effort"),
            "systemPrompt": system.content if system and system.mode == "replace" else None,
            "appendSystemPrompt": system.content if system and system.mode != "replace" else None,
            "hostTools": [t.to_wire() for t in tools] or None,
            "tools": [str(t) for t in c["available_tools"]] if c.get("available_tools") is not None else None,
            "allowedTools": list(c["allowed_tools"]) if c.get("allowed_tools") else None,
            "disallowedTools": list(c["excluded_tools"]) if c.get("excluded_tools") else None,
            "mcpServers": {k: v.to_wire() for k, v in c["mcp_servers"].items()} if c.get("mcp_servers") else None,
            "noMcp": True if c.get("disable_mcp") else None,
            "settings": settings or None,
            "maxTurns": c.get("max_turns"),
            "persistSession": c.get("persist_session", True),
            "worktree": c.get("worktree") or None,
            **extra,
        }
        params = {k: v for k, v in params.items() if v is not None}
        self._opening += 1
        try:
            info = await self._request(method, params)
            session = DotCodeSession(self, info, tools, c.get("on_permission_request"), c.get("on_user_input_request"),
                                     c.get("on_exit_plan_mode"), c.get("on_event"))
            self._sessions[session.session_id] = session
            for event in self._early.pop(session.session_id, []):
                session._dispatch(event)
            return session
        finally:
            self._opening -= 1
            if self._opening == 0:
                self._early.clear()

    async def list_models(self) -> List[ModelInfo]:
        await self.start()
        r = await self._request("models.list", {"cwd": self._cwd} if self._cwd else {})
        return [ModelInfo(m.get("provider", ""), m.get("id", ""), m.get("qualifiedId", "")) for m in r.get("models", [])]

    async def list_sessions(self) -> List[SessionMetadata]:
        await self.start()
        r = await self._request("session.list", {"cwd": self._cwd} if self._cwd else {})
        return [SessionMetadata(s.get("id", ""), s.get("firstPrompt", ""), s.get("modified", ""), s.get("messageCount", 0), s.get("title"))
                for s in r.get("sessions", [])]

    async def ping(self) -> None:
        await self.start()
        await self._request("ping", {})


class DotCodeSession:
    """A conversation with the agent. Use as an async context manager to disconnect automatically."""

    def __init__(self, client: DotCodeClient, info: Dict[str, Any], tools: List[Tool],
                 on_permission_request: Optional[PermissionHandlerFunc], on_user_input_request: Optional[UserInputHandler],
                 on_exit_plan_mode: Optional[ExitPlanModeHandler], on_event: Optional[EventHandler]):
        self._client = client
        self.info = info
        self.session_id: str = info["sessionId"]
        self.model: str = info.get("model", "")
        self._tools = {t.name: t for t in tools}
        self._on_permission = on_permission_request
        self._on_user_input = on_user_input_request
        self._on_exit_plan = on_exit_plan_mode
        self._on_event = on_event
        self._handlers: List[EventHandler] = []

    async def __aenter__(self) -> "DotCodeSession":
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.disconnect()

    # ------------------------------------------------------------------ events

    @overload
    def on(self, handler: Callable[[SessionEvent], None], /) -> Callable[[], None]: ...
    @overload
    def on(self, event_type: Type[E], handler: Callable[[E], None], /) -> Callable[[], None]: ...

    def on(self, a: Any, b: Any = None) -> Callable[[], None]:
        """Subscribes to all events, or to one event class (``session.on(ToolCompletedEvent, handler)``).
        Returns an unsubscribe function."""
        handler: EventHandler = a if b is None else (lambda e: b(e) if isinstance(e, a) else None)
        self._handlers.append(handler)
        return lambda: self._handlers.remove(handler) if handler in self._handlers else None

    def _dispatch(self, event: SessionEvent) -> None:
        if isinstance(event, ModelChangedEvent):
            self.model = event.model
        for h in ([self._on_event] if self._on_event else []) + list(self._handlers):
            try:
                h(event)
            except Exception:  # noqa: BLE001 - a failing listener must not break the session
                pass

    async def _handle_server_request(self, method: str, params: Dict[str, Any]) -> Any:
        invocation = Invocation(self.session_id)
        if method == "permission.request":
            if not self._on_permission:
                return decision_to_wire(PermissionDecisionReject("No permission handler registered in the SDK host (deny by default)."))
            request = PermissionRequest.from_wire(params.get("request") or {})
            return decision_to_wire(await _maybe_await(self._on_permission(request, invocation)))
        if method == "user.question":
            if not self._on_user_input:
                return {"answers": []}
            answers = await _maybe_await(self._on_user_input(UserInputRequest.from_wire(params.get("questions") or []), invocation))
            return {"answers": [{"question": a.question, "answer": a.answer} for a in answers]}
        if method == "plan.review":
            if not self._on_exit_plan:
                return {"approval": "approve"}
            r: ExitPlanModeResult = await _maybe_await(self._on_exit_plan(ExitPlanModeRequest(params.get("plan", "")), invocation))
            if r.approved:
                return {"approval": "approve_accept_edits" if r.accept_edits else "approve"}
            return {"approval": "reject", **({"feedback": r.feedback} if r.feedback else {})}
        if method == "tool.call":
            tool = self._tools.get(params.get("name", ""))
            if tool is None:
                raise DotCodeError(-32601, f"Unknown host tool {params.get('name')}")
            inv = ToolInvocation(self.session_id, params.get("toolUseId", ""), tool.name, params.get("input"))
            return await tool.invoke(params.get("input"), inv)
        raise DotCodeError(-32601, method)

    # ------------------------------------------------------------------ turns

    def _send_params(self, prompt: str, attachments: Optional[Sequence[Attachment]]) -> Dict[str, Any]:
        params: Dict[str, Any] = {"sessionId": self.session_id, "prompt": prompt}
        if attachments:
            params["attachments"] = [attachment_to_wire(a) for a in attachments]
        return params

    async def send(self, prompt: str, *, attachments: Optional[Sequence[Attachment]] = None) -> None:
        """Starts a turn and returns once it is dispatched; follow it with :meth:`on` (``TurnCompletedEvent`` ends it).
        Failures are reported as an :class:`ErrorEvent`."""

        async def run() -> None:
            try:
                await self._client._request("session.send", self._send_params(prompt, attachments))
            except Exception as e:  # noqa: BLE001
                self._dispatch(ErrorEvent("send_failed", str(e), False, session_id=self.session_id))

        asyncio.create_task(run())
        await asyncio.sleep(0)

    async def send_and_wait(self, prompt: str, *, attachments: Optional[Sequence[Attachment]] = None,
                            timeout: Optional[float] = None) -> SendResult:
        """Runs a turn to completion and returns its result. With ``timeout`` (seconds) the turn is aborted when it
        takes longer and :class:`asyncio.TimeoutError` is raised."""
        request = self._client._request("session.send", self._send_params(prompt, attachments))
        try:
            return SendResult.from_wire(await asyncio.wait_for(request, timeout))
        except asyncio.TimeoutError:
            await self.abort()
            raise

    async def stream(self, prompt: str, *, attachments: Optional[Sequence[Attachment]] = None) -> AsyncIterator[SessionEvent]:
        """Runs a turn and yields its events as they arrive; the last one is :class:`TurnCompletedEvent`."""
        queue: "asyncio.Queue[SessionEvent]" = asyncio.Queue()
        unsubscribe = self.on(queue.put_nowait)
        task = asyncio.create_task(self._client._request("session.send", self._send_params(prompt, attachments)))
        try:
            while True:
                getter = asyncio.create_task(queue.get())
                done, _ = await asyncio.wait({getter, task}, return_when=asyncio.FIRST_COMPLETED)
                if getter in done:
                    event = getter.result()
                    yield event
                    if isinstance(event, TurnCompletedEvent) and not event.parent_tool_use_id:
                        break
                else:
                    getter.cancel()
                    while not queue.empty():
                        yield queue.get_nowait()
                    break
            await task
        finally:
            unsubscribe()

    # ------------------------------------------------------------------ control

    async def _call(self, method: str, **extra: Any) -> Any:
        return await self._client._request(method, {"sessionId": self.session_id, **{k: v for k, v in extra.items() if v is not None}})

    async def abort(self) -> None:
        """Cancels the running turn."""
        await self._call("session.abort")

    async def set_model(self, model: str) -> None:
        await self._call("session.setModel", model=model)
        self.model = model

    async def set_permission_mode(self, mode: PermissionMode) -> None:
        await self._call("session.setMode", mode=mode)

    async def set_reasoning_effort(self, effort: ReasoningEffort) -> None:
        await self._call("session.setEffort", effort=effort)

    async def compact(self, instructions: Optional[str] = None) -> None:
        """Summarizes the conversation to free context."""
        await self._call("session.compact", instructions=instructions)

    async def clear(self) -> None:
        await self._call("session.clear")

    async def get_messages(self) -> List[Dict[str, Any]]:
        """The conversation so far (provider-neutral messages)."""
        messages: List[Dict[str, Any]] = (await self._call("session.messages"))["messages"]
        return messages

    async def list_tools(self) -> List[ToolInfo]:
        r = await self._call("tools.list")
        return [ToolInfo(t.get("name", ""), t.get("description", ""), t.get("inputSchema")) for t in r.get("tools", [])]

    async def disconnect(self) -> None:
        """Closes the session; the transcript stays on disk for :meth:`DotCodeClient.resume_session`."""
        try:
            await self._call("session.close")
        finally:
            self._client._sessions.pop(self.session_id, None)
            self._handlers.clear()
