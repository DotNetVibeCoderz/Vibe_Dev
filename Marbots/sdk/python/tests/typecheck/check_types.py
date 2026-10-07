"""Static typing fixture: mypy must accept the plain lines and reject exactly the lines marked ``# E:``."""

from marbots_sdk import (
    AUTO_HOST, BotSpec, ContainerProfile, EventType, KernelPack, MarbotsClient, ModelRef, PermissionProfile, ScheduleSpec, TaskRecord,
)

mb = MarbotsClient()
spec = BotSpec(name="Sari", kernel_functions=[KernelPack.FILES, KernelPack.WEB],
               permission_profile=PermissionProfile.WORKSPACE_WRITE, model=ModelRef.of("azure", "gpt-5.6-luna"))
spec_bad_pack = BotSpec(name="x", kernel_functions=["files"])  # E: raw string instead of KernelPack
spec_bad_profile = BotSpec(name="x", permission_profile="developer-sfe")  # E: misspelled profile
spec_bad_auto = BotSpec(name="x", auto_learn="Always")  # E: not an AutoLearnMode
pack = KernelPack.FIELS  # E: misspelled enum member
bot = mb.bots.create(spec)
name: str = bot.name
steps: int = bot.max_stpes  # E: misspelled attribute
mb.bots.set_model(bot.id, ModelRef.of("azure", "gpt-5-mini"))
mb.bots.set_modle(bot.id, ModelRef.DEFAULT)  # E: misspelled method
mb.approvals.approve("apr_1", scope="Forever")  # E: not an ApprovalScope
result = mb.threads.send("thr_1", "hi", wait=True)
task: TaskRecord = result.task
done: bool = task.is_terminal
state_ok: bool = task.state == "Completed"
cost: str = task.cost_usd  # E: float is not str
for event in mb.events.stream("thr_1"):
    if event.type == EventType.TASK_STATE_CHANGED:
        break
    if event.type == EventType.TASK_COMPLETED:  # E: no such event type
        break
job = mb.schedules.create(ScheduleSpec(name="weekly", bot_id="atlas", prompt="brief", cron="0 8 * * 1"))
mb.schedules.create(ScheduleSpec(name="weekly", bot="atlas", prompt="brief"))  # E: wrong keyword
hosts = mb.agent_hosts.list()
caps: list[str] = hosts[0].capabilities
token = mb.agent_hosts.create_enrollment("lab-pc").token
mb.agent_hosts.boostrap("10.0.0.2", "dev", "http://10.0.0.1:5170")  # E: misspelled method
evals = mb.skills.evaluations()
healthy: bool = evals[0].verdict == "Healthy"
rolled: str = mb.skills.rollback("report-style")
remote = BotSpec(name="Nova", host_ref=AUTO_HOST, container=ContainerProfile(image="python:3.12-slim", cpus=1.5),
                 kernel_functions=[KernelPack.SHELL, KernelPack.DESKTOP, KernelPack.SUBAGENTS])
box_bad = ContainerProfile(image="python:3.12-slim", memory="1g")  # E: wrong keyword
