package com.gravicode.marbots;

import java.io.File;
import java.net.ServerSocket;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * SDK conformance test: runs against a real Marbots.Server process with the offline mock model.
 * Build the server first ({@code dotnet build Marbots.slnx}) or set MARBOTS_SERVER_DLL. Exit code 0 = all passed.
 */
public final class ConformanceTest {
    private static int passed;

    private ConformanceTest() {}

    public static void main(String[] args) throws Exception {
        String dll = System.getenv().getOrDefault("MARBOTS_SERVER_DLL", "../../src/Marbots.Server/bin/Debug/net10.0/Marbots.Server.dll");
        File dllFile = new File(dll).getCanonicalFile();
        if (!dllFile.exists()) {
            System.out.println("SKIPPED: server not built at " + dllFile);
            return;
        }
        int port;
        try (ServerSocket s = new ServerSocket(0)) { port = s.getLocalPort(); }
        Path data = Files.createTempDirectory("mb-java-");
        ProcessBuilder pb = new ProcessBuilder("dotnet", dllFile.getPath(), "--urls", "http://127.0.0.1:" + port)
            .directory(dllFile.getParentFile()).redirectErrorStream(true).redirectOutput(ProcessBuilder.Redirect.DISCARD);
        pb.environment().put("Marbots__DataDirectory", data.toString());
        pb.environment().put("Marbots__Providers__0__Name", "lab");
        pb.environment().put("Marbots__Providers__0__Kind", "mock");
        pb.environment().put("Marbots__Providers__0__Models__0", "lab-small");
        pb.environment().put("Marbots__Providers__0__Models__1", "lab-large");
        Process server = pb.start();
        try {
            MarbotsClient mb = MarbotsClient.create("http://127.0.0.1:" + port);
            boolean ready = false;
            for (int i = 0; i < 120 && !ready; i++) {
                try { mb.system(); ready = true; } catch (RuntimeException e) { Thread.sleep(500); }
            }
            check(ready, "server started");
            run(mb);
            System.out.println("OK: " + passed + " checks passed");
        } finally {
            server.destroyForcibly().waitFor(10, TimeUnit.SECONDS);
        }
    }

    private static void run(MarbotsClient mb) throws Exception {
        WhoAmI me = mb.tenancy().whoami();
        check(me.tenant().equals("default") && me.role() == TenantRole.OWNER && !me.multiTenant(), "whoami single-tenant");
        SystemInfo sys = mb.system();
        check("Marbots".equals(sys.product()) && sys.creditsEn().contains("Gravicode"), "system info");
        check(mb.bots().list().stream().anyMatch(b -> "boss-man".equals(b.id()) && b.isSystem()), "boss man exists");

        Bot dina = mb.bots().hire("data-analyst", "Dina");
        check(dina.usesDefaultModel(), "hired bot follows the default model");
        check(dina.kernelFunctions().contains(KernelPack.SHELL), "data analyst has the shell pack");
        check(dina.permissionProfile() == PermissionProfile.DEVELOPER_SAFE, "developer-safe profile");

        BotModelInfo info = mb.bots().setModel(dina.id(), ModelRef.of("lab", "lab-large"));
        check("lab/lab-large".equals(info.effective()) && !info.usesDefault(), "per-bot model");
        try {
            mb.bots().setModel(dina.id(), ModelRef.of("ghost", "x"));
            check(false, "unknown provider rejected");
        } catch (MarbotsException e) {
            check(e.status() == 400, "unknown provider rejected with 400");
        }

        ChatThread thread = mb.threads().create(dina.id(), null);
        SendResult r = mb.threads().send(thread.id(), "hello", true, 30);
        check(r.task().state() == TaskState.COMPLETED, "chat completed");
        check("lab/lab-large".equals(r.task().model()), "task records the model");
        check(r.text().contains("mock"), "mock reply");

        Bot imported = mb.bots().importPackage(mb.bots().export(dina.id(), false));
        check("lab/lab-large".equals(imported.model()), "export/import keeps the model");
        mb.bots().delete(imported.id());
        mb.bots().delete(dina.id());

        Bot typed = mb.bots().create(BotSpec.builder("Typed Java").kernelFunctions(KernelPack.FILES)
            .permissionProfile(PermissionProfile.READ_ONLY).model(ModelRef.of("lab", "lab-small")));
        check(typed.kernelFunctions().equals(List.of(KernelPack.FILES)) && typed.permissionProfile() == PermissionProfile.READ_ONLY, "create with spec");
        check("lab/lab-small".equals(mb.bots().getModel(typed.id()).effective()), "spec model");
        mb.bots().delete(typed.id());

        ModelCatalog catalog = mb.models().list();
        check(catalog.choices().contains("lab/lab-small"), "model choices");
        check("lab/lab-small".equals(mb.models().setDefault(ModelRef.of("lab", "lab-small"))), "set default model");
        BotModelInfo wren = mb.bots().getModel("wren");
        check(wren.usesDefault() && "lab/lab-small".equals(wren.effective()), "default bots follow the default");

        ChatThread atlas = mb.threads().create("atlas", null);
        CountDownLatch done = new CountDownLatch(1);
        try (AutoCloseable sub = mb.events().subscribe(atlas.id(), e -> {
            if (e.type() == EventType.TASK_STATE_CHANGED && TaskState.COMPLETED.wire().equals(e.data())) done.countDown();
        })) {
            Thread.sleep(500);
            mb.threads().send(atlas.id(), "ping", false);
            check(done.await(30, TimeUnit.SECONDS), "event stream reports completion");
        }

        check(mb.approvals().setSkipApprovals(true) && mb.approvals().skipApprovals(), "skip approvals on");
        check(!mb.approvals().setSkipApprovals(false), "skip approvals off");
        check(mb.approvals().pending().isEmpty(), "no pending approvals");

        ScheduleJob job = mb.schedules().create(ScheduleSpec.cron("weekly", "atlas", "brief", "0 8 * * 1"));
        check(job.nextRunAt() != null, "schedule next run");
        mb.schedules().delete(job.id());
        try {
            mb.schedules().create(ScheduleSpec.cron("bad", "atlas", "x", "nope"));
            check(false, "bad cron rejected");
        } catch (MarbotsException e) {
            check(e.status() == 400, "bad cron rejected");
        }
        check(mb.templates().list("designer", null).stream().anyMatch(t -> "ux-designer".equals(t.id())), "template search");

        HostInfo local = mb.agentHosts().list().stream().filter(h -> HostRef.LOCAL.equals(h.id())).findFirst().orElseThrow();
        check(local.capabilities().contains("shell"), "local host capabilities");
        EnrollmentToken token = mb.agentHosts().createEnrollment("lab-pc", 10);
        check(token.token().startsWith("mbe_") && token.enrollCommand().contains("marbots-host enroll"), "enrollment token");
        try {
            mb.agentHosts().disable("host-that-does-not-exist");
            check(false, "unknown host rejected");
        } catch (MarbotsException e) {
            check(e.status() == 404, "unknown host rejected with 404");
        }

        Bot placed = mb.bots().create(BotSpec.builder("Placed Java").hostRef(HostRef.AUTO)
            .container(new ContainerProfile("python:3.12-slim", 1.5, 512, false)).kernelFunctions(KernelPack.SHELL, KernelPack.SUBAGENTS));
        check(HostRef.AUTO.equals(placed.hostRef()) && placed.container() != null && placed.container().memoryMb() == 512
            && !placed.container().network() && placed.kernelFunctions().contains(KernelPack.SUBAGENTS), "placement and container round trip");

        List<SkillEvaluation> evals = mb.skills().evaluations();
        check(!evals.isEmpty() && evals.stream().allMatch(e -> e.verdict() == SkillVerdict.COLLECTING_EVIDENCE), "skill evaluations");
        check(mb.skills().setAutoRollback(true) && mb.skills().autoRollback(), "auto rollback on");
        check(!mb.skills().setAutoRollback(false), "auto rollback off");
        try {
            mb.skills().rollback("no-such-skill");
            check(false, "unknown skill rollback rejected");
        } catch (MarbotsException e) {
            check(e.status() == 404, "unknown skill rollback rejected with 404");
        }
    }

    private static void check(boolean ok, String what) {
        if (!ok) {
            System.err.println("FAILED: " + what);
            System.exit(1);
        }
        passed++;
    }
}
