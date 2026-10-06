// Compile-time checks (`npm test` runs `tsc` on this file): each @ts-expect-error line must fail to compile.
import { MarbotsClient, KernelPack, PermissionProfile, EventType, ModelRef, isTaskFinished, type BotSpec, type TaskRecord } from "../src/index.js";

async function main() {
  const mb = new MarbotsClient({ baseUrl: "http://localhost:5170" });

  const ok: BotSpec = {
    name: "Sari",
    kernelFunctions: [KernelPack.Files, KernelPack.Web],
    permissionProfile: PermissionProfile.WorkspaceWrite,
    model: ModelRef.of("azure", "gpt-5.6-luna"),
  };
  const bot = await mb.bots.create(ok);
  const id: string = bot.id;

  // @ts-expect-error misspelled kernel pack
  const badPack: BotSpec = { name: "x", kernelFunctions: ["fils"] };
  // @ts-expect-error misspelled permission profile
  const badProfile: BotSpec = { name: "x", permissionProfile: "developer-sfe" };
  // @ts-expect-error Boss Man's profile cannot be given to other bots
  const managerProfile: BotSpec = { name: "x", permissionProfile: PermissionProfile.Manager };
  // @ts-expect-error not an auto-learn mode
  const badAuto: BotSpec = { name: "x", autoLearn: "Always" };
  // @ts-expect-error unknown property
  const badProp: BotSpec = { name: "x", modle: "default" };
  // @ts-expect-error misspelled constant
  void KernelPack.Fiels;
  void badPack; void badProfile; void managerProfile; void badAuto; void badProp;

  await mb.bots.setModel(id, ModelRef.Default);
  // @ts-expect-error misspelled method
  await mb.bots.setModle(id, ModelRef.Default);
  // @ts-expect-error not an approval scope
  await mb.approvals.approve("apr_1", "Forever");

  const r = await mb.threads.send("thr_1", "hi", { wait: true });
  const task: TaskRecord = r.task;
  const cost: number = task.costUsd;
  // @ts-expect-error costUsd is a number
  const costText: string = task.costUsd;
  // @ts-expect-error not a task state
  const neverState = task.state === "Done";
  void cost; void costText; void neverState;

  for await (const e of mb.events("thr_1")) {
    if (e.type === EventType.ToolCallStarted) console.log(e.message);
    // @ts-expect-error no such event type
    if (e.type === "TaskCompleted") break;
    if (isTaskFinished(e)) break;
  }

  await mb.schedules.create({ name: "weekly", botId: "atlas", prompt: "brief", cron: "0 8 * * 1" });
  // @ts-expect-error wrong property name
  await mb.schedules.create({ name: "weekly", bot: "atlas", prompt: "brief" });
}
void main;
