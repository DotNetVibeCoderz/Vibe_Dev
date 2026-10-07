"""SDK conformance test: runs against a real Marbots.Server process with the offline mock model.

Build the server first (``dotnet build Marbots.slnx``) or set MARBOTS_SERVER_DLL. Run from sdk/python/tests:
``PYTHONPATH=../src python -m unittest -v``.
"""

from __future__ import annotations

import os
import pathlib
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.request
from typing import List, Optional

from marbots_sdk import (
    AUTO_HOST, BOSS_MAN, LOCAL_HOST, AgentEvent, ContainerProfile, BotSpec, EventType, KernelPack, MarbotsClient, MarbotsError, ModelRef, PermissionProfile,
    ScheduleSpec,
)

ROOT = pathlib.Path(__file__).resolve().parents[3]
DLL = pathlib.Path(os.environ.get("MARBOTS_SERVER_DLL", ROOT / "src/Marbots.Server/bin/Debug/net10.0/Marbots.Server.dll"))


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


@unittest.skipUnless(DLL.exists(), f"server not built: {DLL}")
class ConformanceTest(unittest.TestCase):
    proc: Optional["subprocess.Popen[bytes]"] = None
    data: str = ""
    mb: MarbotsClient

    @classmethod
    def setUpClass(cls) -> None:
        port = _free_port()
        cls.data = tempfile.mkdtemp(prefix="mb-py-")
        env = {**os.environ, "Marbots__DataDirectory": cls.data,
               "Marbots__Providers__0__Name": "lab", "Marbots__Providers__0__Kind": "mock",
               "Marbots__Providers__0__Models__0": "lab-small", "Marbots__Providers__0__Models__1": "lab-large"}
        cls.proc = subprocess.Popen(["dotnet", str(DLL), "--urls", f"http://127.0.0.1:{port}"], cwd=DLL.parent, env=env,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.mb = MarbotsClient(f"http://127.0.0.1:{port}", timeout=60)
        for _ in range(120):
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/api/v1/system", timeout=2)
                return
            except OSError:
                time.sleep(0.5)
        raise RuntimeError("server did not start")

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.proc:
            cls.proc.kill()
            cls.proc.wait(10)
        shutil.rmtree(cls.data, ignore_errors=True)

    def test_system_and_team(self) -> None:
        sys_info = self.mb.system()
        self.assertEqual("Marbots", sys_info.product)
        self.assertIn("Gravicode", sys_info.credits_en)
        bots = self.mb.bots.list()
        self.assertTrue(any(b.id == BOSS_MAN and b.is_system for b in bots))

    def test_hire_model_chat_export_import(self) -> None:
        bot = self.mb.bots.hire("data-analyst", "Dina")
        self.assertTrue(bot.uses_default_model)
        self.assertIn(KernelPack.SHELL.value, bot.kernel_functions)
        self.assertEqual(PermissionProfile.DEVELOPER_SAFE.value, bot.permission_profile)

        info = self.mb.bots.set_model(bot.id, ModelRef.of("lab", "lab-large"))
        self.assertEqual("lab/lab-large", info.effective)
        self.assertFalse(info.uses_default)
        with self.assertRaises(MarbotsError) as err:
            self.mb.bots.set_model(bot.id, ModelRef.of("ghost", "x"))
        self.assertEqual(400, err.exception.status)

        thread = self.mb.threads.create(bot.id)
        result = self.mb.threads.send(thread.id, "hello", wait=True, timeout_seconds=30)
        self.assertEqual("Completed", result.task.state)
        self.assertEqual("lab/lab-large", result.task.model)
        self.assertIn("mock", result.text)

        imported = self.mb.bots.import_package(self.mb.bots.export(bot.id))
        self.assertEqual("lab/lab-large", imported.model)
        self.mb.bots.delete(imported.id)
        self.mb.bots.delete(bot.id)

    def test_create_with_spec(self) -> None:
        bot = self.mb.bots.create(BotSpec(name="Typed Tom", role="Tester", kernel_functions=[KernelPack.FILES],
                                          permission_profile=PermissionProfile.READ_ONLY, model=ModelRef.of("lab", "lab-small")))
        self.assertEqual(["files"], bot.kernel_functions)
        self.assertEqual("read-only", bot.permission_profile)
        self.assertEqual("lab/lab-small", self.mb.bots.get_model(bot.id).effective)
        self.mb.bots.delete(bot.id)

    def test_models_catalog_and_default(self) -> None:
        catalog = self.mb.models.list()
        self.assertIn("lab/lab-small", catalog.choices)
        self.assertEqual("lab/lab-small", self.mb.models.set_default(ModelRef.of("lab", "lab-small")))
        wren = self.mb.bots.get_model("wren")
        self.assertTrue(wren.uses_default)
        self.assertEqual("lab/lab-small", wren.effective)

    def test_events_stream_reports_completion(self) -> None:
        thread = self.mb.threads.create("atlas")
        seen: List[AgentEvent] = []

        def listen() -> None:
            for e in self.mb.events.stream(thread.id):
                seen.append(e)
                if e.is_task_finished():
                    return

        t = threading.Thread(target=listen, daemon=True)
        t.start()
        time.sleep(0.5)
        self.mb.threads.send(thread.id, "ping")
        t.join(30)
        self.assertTrue(any(e.type == EventType.TASK_STATE_CHANGED and e.data == "Completed" for e in seen))

    def test_skip_approvals_toggle(self) -> None:
        self.assertFalse(self.mb.approvals.get_skip_approvals())
        self.assertTrue(self.mb.approvals.set_skip_approvals(True))
        self.assertTrue(self.mb.approvals.get_skip_approvals())
        self.assertFalse(self.mb.approvals.set_skip_approvals(False))

    def test_templates_approvals_schedules(self) -> None:
        self.assertTrue(any(t.id == "ux-designer" for t in self.mb.templates.list("designer")))
        self.assertEqual([], self.mb.approvals.pending())
        job = self.mb.schedules.create(ScheduleSpec(name="weekly", bot_id="atlas", prompt="brief", cron="0 8 * * 1"))
        self.assertIsNotNone(job.next_run_at)
        self.mb.schedules.delete(job.id)
        with self.assertRaises(MarbotsError):
            self.mb.schedules.create(ScheduleSpec(name="bad", bot_id="atlas", prompt="x", cron="nope"))

    def test_agent_hosts_and_enrollment(self) -> None:
        hosts = self.mb.agent_hosts.list()
        local = next(h for h in hosts if h.id == LOCAL_HOST)
        self.assertIn("shell", local.capabilities)
        token = self.mb.agent_hosts.create_enrollment("lab-pc", valid_minutes=10)
        self.assertTrue(token.token.startswith("mbe_"))
        self.assertIn("marbots-host enroll", token.enroll_command)
        with self.assertRaises(MarbotsError):
            self.mb.agent_hosts.disable("host-that-does-not-exist")

    def test_remote_placement_and_container_round_trip(self) -> None:
        bot = self.mb.bots.create(BotSpec(name="Placed Py", host_ref=AUTO_HOST,
                                          container=ContainerProfile(image="python:3.12-slim", cpus=1.5, memory_mb=512, network=False),
                                          kernel_functions=[KernelPack.SHELL, KernelPack.SUBAGENTS]))
        self.assertEqual(AUTO_HOST, bot.host_ref)
        self.assertIsNotNone(bot.container)
        assert bot.container is not None
        self.assertEqual((1.5, 512, False), (bot.container.cpus, bot.container.memory_mb, bot.container.network))

    def test_skill_evaluations_and_auto_rollback(self) -> None:
        evals = self.mb.skills.evaluations()
        self.assertTrue(evals)
        self.assertTrue(all(e.verdict == "CollectingEvidence" for e in evals))
        self.assertTrue(self.mb.skills.set_auto_rollback(True))
        self.assertTrue(self.mb.skills.auto_rollback())
        self.assertFalse(self.mb.skills.set_auto_rollback(False))
        with self.assertRaises(MarbotsError):
            self.mb.skills.rollback("no-such-skill")

if __name__ == "__main__":
    unittest.main()
