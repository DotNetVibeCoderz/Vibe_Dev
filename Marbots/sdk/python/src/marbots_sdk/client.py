"""Marbots REST client (zero dependencies). Every call returns typed dataclasses from :mod:`marbots_sdk.types`."""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, Iterator, List, Optional

from .types import (
    AgentEvent, ApprovalRequest, ApprovalScope, Bot, BotModelInfo, BotSpec, BotTemplate, ChatMessage, ChatThread,
    HostInfo, McpServer, MemoryKind, MemoryRecord, ModelCatalog, ScheduleJob, ScheduleSpec, SendResult, SkillInfo,
    SystemInfo, TaskRecord, WorkspaceFile,
)


class MarbotsError(Exception):
    """An API error (HTTP status + Problem Details message)."""

    def __init__(self, status: int, message: str) -> None:
        super().__init__(f"HTTP {status}: {message}")
        self.status = status
        self.message = message


def _q(s: str) -> str:
    return urllib.parse.quote(s, safe="")


class _Http:
    def __init__(self, base_url: str, api_key: Optional[str], timeout: float) -> None:
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout

    def request(self, method: str, path: str, body: Optional[bytes] = None, content_type: str = "application/json",
                accept: Optional[str] = None) -> urllib.request.Request:
        req = urllib.request.Request(self.base_url + path, data=body, method=method)
        if body is not None:
            req.add_header("Content-Type", content_type)
        if accept:
            req.add_header("Accept", accept)
        if self.api_key:
            req.add_header("X-Api-Key", self.api_key)
        return req

    def raw(self, method: str, path: str, body: Optional[bytes] = None, content_type: str = "application/json",
            timeout: Optional[float] = None) -> bytes:
        try:
            with urllib.request.urlopen(self.request(method, path, body, content_type), timeout=timeout or self.timeout) as resp:
                data: bytes = resp.read()
                return data
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")
            try:
                doc = json.loads(detail)
                if isinstance(doc, dict):
                    detail = str(doc.get("detail") or doc.get("title") or detail)
            except ValueError:
                pass
            raise MarbotsError(e.code, detail) from None

    def json(self, method: str, path: str, payload: Any = None, timeout: Optional[float] = None) -> Any:
        body = json.dumps(payload).encode("utf-8") if payload is not None else (b"" if method != "GET" else None)
        data = self.raw(method, path, body, timeout=timeout)
        return json.loads(data) if data else None


def _objs(v: Any) -> List[Dict[str, Any]]:
    return [x for x in v if isinstance(x, dict)] if isinstance(v, list) else []


def _obj(v: Any) -> Dict[str, Any]:
    return v if isinstance(v, dict) else {}


class BotsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self) -> List[Bot]:
        return [Bot.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/bots"))]

    def get(self, id_or_name: str) -> Bot:
        return Bot.from_wire(_obj(self._h.json("GET", f"/api/v1/bots/{_q(id_or_name)}")))

    def create(self, spec: BotSpec) -> Bot:
        return Bot.from_wire(_obj(self._h.json("POST", "/api/v1/bots", spec.to_wire())))

    def update(self, bot_id: str, spec: BotSpec) -> Bot:
        return Bot.from_wire(_obj(self._h.json("PUT", f"/api/v1/bots/{_q(bot_id)}", spec.to_wire(bot_id))))

    def delete(self, bot_id: str) -> None:
        self._h.json("DELETE", f"/api/v1/bots/{_q(bot_id)}")

    def hire(self, template_id: str, name: Optional[str] = None) -> Bot:
        """Create a bot from a gallery template."""
        return Bot.from_wire(_obj(self._h.json("POST", f"/api/v1/bots/from-template/{_q(template_id)}", {"name": name})))

    def get_model(self, bot_id: str) -> BotModelInfo:
        return BotModelInfo.from_wire(_obj(self._h.json("GET", f"/api/v1/bots/{_q(bot_id)}/model")))

    def set_model(self, bot_id: str, model: str) -> BotModelInfo:
        """``model``: ``ModelRef.DEFAULT``, ``ModelRef.of(provider, model)`` or a profile name."""
        return BotModelInfo.from_wire(_obj(self._h.json("PUT", f"/api/v1/bots/{_q(bot_id)}/model", {"model": model})))

    def pause(self, bot_id: str) -> None:
        self._h.json("POST", f"/api/v1/bots/{_q(bot_id)}/pause")

    def resume(self, bot_id: str) -> None:
        self._h.json("POST", f"/api/v1/bots/{_q(bot_id)}/resume")

    def export(self, bot_id: str, include_memory: bool = False) -> bytes:
        """Download a ``.marbot`` package (secrets are never included)."""
        return self._h.raw("GET", f"/api/v1/bots/{_q(bot_id)}/export?includeMemory={str(include_memory).lower()}")

    def import_package(self, package: bytes) -> Bot:
        return Bot.from_wire(_obj(json.loads(self._h.raw("POST", "/api/v1/bots/import", package, "application/zip"))))


class TemplatesApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self, query: str = "", category: str = "") -> List[BotTemplate]:
        return [BotTemplate.from_wire(d) for d in _objs(self._h.json("GET", f"/api/v1/templates?q={_q(query)}&category={_q(category)}"))]

    def get(self, template_id: str) -> BotTemplate:
        return BotTemplate.from_wire(_obj(self._h.json("GET", f"/api/v1/templates/{_q(template_id)}")))


class ModelsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self) -> ModelCatalog:
        return ModelCatalog.from_wire(_obj(self._h.json("GET", "/api/v1/models")))

    def set_default(self, model: str) -> str:
        """Change the workspace default model (used by every bot whose model is ``ModelRef.DEFAULT``)."""
        return str(_obj(self._h.json("PUT", "/api/v1/models/default", {"model": model})).get("default", ""))


class ThreadsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self, bot_id: Optional[str] = None) -> List[ChatThread]:
        path = "/api/v1/threads" + (f"?botId={_q(bot_id)}" if bot_id else "")
        return [ChatThread.from_wire(d) for d in _objs(self._h.json("GET", path))]

    def create(self, bot_id: str, title: Optional[str] = None) -> ChatThread:
        return ChatThread.from_wire(_obj(self._h.json("POST", "/api/v1/threads", {"botId": bot_id, "title": title})))

    def send(self, thread_id: str, text: str, wait: bool = False, timeout_seconds: int = 600) -> SendResult:
        """Send a message. With ``wait=True`` the call returns after the bot finished."""
        payload = {"text": text, "wait": wait, "timeoutSeconds": timeout_seconds}
        return SendResult.from_wire(_obj(self._h.json("POST", f"/api/v1/threads/{_q(thread_id)}/messages", payload, timeout=timeout_seconds + 30)))

    def messages(self, thread_id: str, after_seq: int = 0) -> List[ChatMessage]:
        return [ChatMessage.from_wire(d) for d in _objs(self._h.json("GET", f"/api/v1/threads/{_q(thread_id)}/messages?after={after_seq}"))]

    def files(self, thread_id: str) -> List[WorkspaceFile]:
        return [WorkspaceFile.from_wire(d) for d in _objs(self._h.json("GET", f"/api/v1/threads/{_q(thread_id)}/files"))]

    def download(self, thread_id: str, path: str) -> bytes:
        return self._h.raw("GET", f"/api/v1/threads/{_q(thread_id)}/files/{path}")

    def export_transcript(self, thread_id: str) -> str:
        return self._h.raw("GET", f"/api/v1/threads/{_q(thread_id)}/export").decode("utf-8")

    def delete(self, thread_id: str) -> None:
        self._h.json("DELETE", f"/api/v1/threads/{_q(thread_id)}")


class TasksApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self, thread_id: Optional[str] = None) -> List[TaskRecord]:
        path = "/api/v1/tasks" + (f"?threadId={_q(thread_id)}" if thread_id else "")
        return [TaskRecord.from_wire(d) for d in _objs(self._h.json("GET", path))]

    def get(self, task_id: str) -> TaskRecord:
        return TaskRecord.from_wire(_obj(self._h.json("GET", f"/api/v1/tasks/{_q(task_id)}")))

    def cancel(self, task_id: str) -> None:
        self._h.json("POST", f"/api/v1/tasks/{_q(task_id)}/cancel")


class ApprovalsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def get_skip_approvals(self) -> bool:
        """True when approvals are skipped (dangerous mode)."""
        return _obj(self._h.json("GET", "/api/v1/system/approvals")).get("dangerouslySkipApprovals") is True

    def set_skip_approvals(self, skip: bool) -> bool:
        """Dangerous, like ``--dangerously-skip-permissions``: allow every "ask" action without a human.
        Turning it on also approves everything pending. Actions a bot's profile denies stay denied."""
        doc = _obj(self._h.json("PUT", "/api/v1/system/approvals", {"dangerouslySkipApprovals": skip}))
        return doc.get("dangerouslySkipApprovals") is True

    def pending(self) -> List[ApprovalRequest]:
        return [ApprovalRequest.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/approvals?state=pending"))]

    def approve(self, approval_id: str, scope: ApprovalScope = "Once") -> ApprovalRequest:
        return ApprovalRequest.from_wire(_obj(self._h.json("POST", f"/api/v1/approvals/{_q(approval_id)}/approve", {"scope": scope})))

    def reject(self, approval_id: str) -> ApprovalRequest:
        return ApprovalRequest.from_wire(_obj(self._h.json("POST", f"/api/v1/approvals/{_q(approval_id)}/reject")))


class SkillsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self) -> List[SkillInfo]:
        return [SkillInfo.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/skills"))]

    def install(self, source: str) -> List[SkillInfo]:
        return [SkillInfo.from_wire(d) for d in _objs(self._h.json("POST", "/api/v1/skills/install", {"source": source}))]


class McpApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self) -> List[McpServer]:
        return [McpServer.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/mcp"))]

    def install(self, server_id: str) -> McpServer:
        return McpServer.from_wire(_obj(self._h.json("POST", f"/api/v1/mcp/{_q(server_id)}/install")))


class SchedulesApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self) -> List[ScheduleJob]:
        return [ScheduleJob.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/schedules"))]

    def create(self, spec: ScheduleSpec) -> ScheduleJob:
        return ScheduleJob.from_wire(_obj(self._h.json("POST", "/api/v1/schedules", spec.to_wire())))

    def delete(self, job_id: str) -> None:
        self._h.json("DELETE", f"/api/v1/schedules/{_q(job_id)}")


class MemoryApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def list(self, owner: str) -> List[MemoryRecord]:
        return [MemoryRecord.from_wire(d) for d in _objs(self._h.json("GET", f"/api/v1/memory/{_q(owner)}"))]

    def remember(self, owner: str, content: str, kind: MemoryKind = "Semantic") -> MemoryRecord:
        payload = {"owner": owner, "content": content, "kind": kind, "source": "sdk:python", "confidence": 1.0}
        return MemoryRecord.from_wire(_obj(self._h.json("POST", "/api/v1/memory", payload)))


class EventsApi:
    def __init__(self, h: _Http) -> None:
        self._h = h

    def stream(self, thread_id: Optional[str] = None, after_id: Optional[int] = None) -> Iterator[AgentEvent]:
        """Yield live events (Server-Sent Events). ``break`` out of the loop to stop."""
        path = f"/api/v1/threads/{_q(thread_id)}/events" if thread_id else "/api/v1/events"
        if after_id is not None:
            path += f"?after={after_id}"
        with urllib.request.urlopen(self._h.request("GET", path, accept="text/event-stream"), timeout=3600) as resp:
            for raw in resp:
                line = raw.decode("utf-8").rstrip("\r\n")
                if line.startswith("data: "):
                    doc = json.loads(line[6:])
                    if isinstance(doc, dict):
                        yield AgentEvent.from_wire(doc)


class MarbotsClient:
    """Client for a Marbots server.

    >>> from marbots_sdk import MarbotsClient, ModelRef
    >>> mb = MarbotsClient("http://localhost:5170")
    >>> atlas = mb.bots.set_model("atlas", ModelRef.of("azure", "gpt-5.6-luna"))
    >>> print(mb.chat("boss-man", "Plan a product launch"))
    """

    def __init__(self, base_url: str = "http://localhost:5170", api_key: Optional[str] = None, timeout: float = 120) -> None:
        self._h = _Http(base_url, api_key, timeout)
        self.bots = BotsApi(self._h)
        self.templates = TemplatesApi(self._h)
        self.models = ModelsApi(self._h)
        self.threads = ThreadsApi(self._h)
        self.tasks = TasksApi(self._h)
        self.approvals = ApprovalsApi(self._h)
        self.skills = SkillsApi(self._h)
        self.mcp = McpApi(self._h)
        self.schedules = SchedulesApi(self._h)
        self.memory = MemoryApi(self._h)
        self.events = EventsApi(self._h)

    @property
    def base_url(self) -> str:
        return self._h.base_url

    def system(self) -> SystemInfo:
        return SystemInfo.from_wire(_obj(self._h.json("GET", "/api/v1/system")))

    def hosts(self) -> List[HostInfo]:
        return [HostInfo.from_wire(d) for d in _objs(self._h.json("GET", "/api/v1/hosts"))]

    def chat(self, bot: str, text: str, timeout_seconds: int = 600) -> str:
        """Create a thread with ``bot``, send ``text``, wait, and return the reply text."""
        thread = self.threads.create(bot)
        return self.threads.send(thread.id, text, wait=True, timeout_seconds=timeout_seconds).text
