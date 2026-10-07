package marbots_test

// SDK conformance test: runs against a real Marbots.Server process with the offline mock model.
// Build the server first (`dotnet build Marbots.slnx`) or set MARBOTS_SERVER_DLL.

import (
	"context"
	"errors"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	marbots "github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go"
)

var client *marbots.Client

func TestMain(m *testing.M) {
	dll := os.Getenv("MARBOTS_SERVER_DLL")
	if dll == "" {
		dll, _ = filepath.Abs("../../src/Marbots.Server/bin/Debug/net10.0/Marbots.Server.dll")
	}
	if _, err := os.Stat(dll); err != nil {
		os.Exit(m.Run()) // tests skip themselves when client is nil
	}
	l, _ := net.Listen("tcp", "127.0.0.1:0")
	port := l.Addr().(*net.TCPAddr).Port
	l.Close()
	data, _ := os.MkdirTemp("", "mb-go-")
	cmd := exec.Command("dotnet", dll, "--urls", "http://127.0.0.1:"+strconv.Itoa(port))
	cmd.Dir = filepath.Dir(dll)
	cmd.Env = append(os.Environ(), "Marbots__DataDirectory="+data, "Marbots__Providers__0__Name=lab", "Marbots__Providers__0__Kind=mock",
		"Marbots__Providers__0__Models__0=lab-small", "Marbots__Providers__0__Models__1=lab-large")
	if err := cmd.Start(); err != nil {
		panic(err)
	}
	c := marbots.New("http://127.0.0.1:" + strconv.Itoa(port))
	ready := false
	for i := 0; i < 120 && !ready; i++ {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		_, err := c.System(ctx)
		cancel()
		if err == nil {
			ready = true
		} else {
			time.Sleep(500 * time.Millisecond)
		}
	}
	if ready {
		client = c
	}
	code := m.Run()
	_ = cmd.Process.Kill()
	_, _ = cmd.Process.Wait()
	_ = os.RemoveAll(data)
	os.Exit(code)
}

func need(t *testing.T) context.Context {
	t.Helper()
	if client == nil {
		t.Skip("Marbots server not available")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	t.Cleanup(cancel)
	return ctx
}

func TestWhoAmISingleTenant(t *testing.T) {
	ctx := need(t)
	me, err := client.Tenancy.WhoAmI(ctx)
	if err != nil || me.Tenant != "default" || me.Role != marbots.RoleOwner || me.MultiTenant {
		t.Fatalf("whoami: %+v %v", me, err)
	}
}

func TestSystemAndTeam(t *testing.T) {
	ctx := need(t)
	info, err := client.System(ctx)
	if err != nil || info.Product != "Marbots" || !strings.Contains(info.CreditsEn, "Gravicode") {
		t.Fatalf("system: %+v %v", info, err)
	}
	bots, err := client.Bots.List(ctx)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, b := range bots {
		found = found || (b.ID == marbots.BossMan && b.IsSystem)
	}
	if !found {
		t.Fatal("boss-man missing")
	}
}

func TestHireModelChatExportImport(t *testing.T) {
	ctx := need(t)
	bot, err := client.Bots.Hire(ctx, "data-analyst", "Dina")
	if err != nil {
		t.Fatal(err)
	}
	if !bot.UsesDefaultModel() || bot.PermissionProfile != marbots.PermissionDeveloperSafe {
		t.Fatalf("unexpected bot: %+v", bot)
	}
	hasShell := false
	for _, k := range bot.KernelFunctions {
		hasShell = hasShell || k == marbots.KernelPackShell
	}
	if !hasShell {
		t.Fatal("data analyst should have the shell pack")
	}
	info, err := client.Bots.SetModel(ctx, bot.ID, marbots.MustModel("lab", "lab-large"))
	if err != nil || info.Effective != "lab/lab-large" || info.UsesDefault {
		t.Fatalf("set model: %+v %v", info, err)
	}
	_, err = client.Bots.SetModel(ctx, bot.ID, marbots.MustModel("ghost", "x"))
	var apiErr *marbots.Error
	if !errors.As(err, &apiErr) || apiErr.Status != 400 {
		t.Fatalf("expected 400 for unknown provider, got %v", err)
	}
	thread, err := client.Threads.Create(ctx, bot.ID, "")
	if err != nil {
		t.Fatal(err)
	}
	r, err := client.Threads.Send(ctx, thread.ID, "hello", marbots.SendOptions{Wait: true, TimeoutSeconds: 30})
	if err != nil || r.Task.State != marbots.TaskCompleted || r.Task.Model != "lab/lab-large" || !strings.Contains(r.Text(), "mock") {
		t.Fatalf("chat: %+v %v", r, err)
	}
	pkg, err := client.Bots.Export(ctx, bot.ID, false)
	if err != nil {
		t.Fatal(err)
	}
	imported, err := client.Bots.Import(ctx, pkg)
	if err != nil || imported.Model != "lab/lab-large" {
		t.Fatalf("import: %+v %v", imported, err)
	}
	_ = client.Bots.Delete(ctx, imported.ID)
	_ = client.Bots.Delete(ctx, bot.ID)
}

func TestCreateWithSpec(t *testing.T) {
	ctx := need(t)
	bot, err := client.Bots.Create(ctx, marbots.BotSpec{
		Name: "Typed Gopher", KernelFunctions: []marbots.KernelPack{marbots.KernelPackFiles},
		PermissionProfile: marbots.PermissionReadOnly, Model: marbots.MustModel("lab", "lab-small"),
	})
	if err != nil || len(bot.KernelFunctions) != 1 || bot.PermissionProfile != marbots.PermissionReadOnly {
		t.Fatalf("create: %+v %v", bot, err)
	}
	m, err := client.Bots.GetModel(ctx, bot.ID)
	if err != nil || m.Effective != "lab/lab-small" {
		t.Fatalf("model: %+v %v", m, err)
	}
	_ = client.Bots.Delete(ctx, bot.ID)
}

func TestModelsCatalogAndDefault(t *testing.T) {
	ctx := need(t)
	cat, err := client.Models.List(ctx)
	if err != nil {
		t.Fatal(err)
	}
	ok := false
	for _, c := range cat.Choices {
		ok = ok || c == "lab/lab-small"
	}
	if !ok {
		t.Fatalf("choices: %v", cat.Choices)
	}
	def, err := client.Models.SetDefault(ctx, marbots.MustModel("lab", "lab-small"))
	if err != nil || def != "lab/lab-small" {
		t.Fatalf("default: %s %v", def, err)
	}
	wren, err := client.Bots.GetModel(ctx, "wren")
	if err != nil || !wren.UsesDefault || wren.Effective != "lab/lab-small" {
		t.Fatalf("wren: %+v %v", wren, err)
	}
}

func TestEventStreamReportsCompletion(t *testing.T) {
	ctx := need(t)
	thread, err := client.Threads.Create(ctx, "atlas", "")
	if err != nil {
		t.Fatal(err)
	}
	sctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	events, _ := client.Events.Stream(sctx, thread.ID)
	time.Sleep(400 * time.Millisecond)
	if _, err := client.Threads.Send(ctx, thread.ID, "ping", marbots.SendOptions{}); err != nil {
		t.Fatal(err)
	}
	for e := range events {
		if e.Type == marbots.EventTaskStateChanged && marbots.TaskState(e.Data) == marbots.TaskCompleted {
			return
		}
	}
	t.Fatal("no completion event")
}

func TestSkipApprovalsAndSchedules(t *testing.T) {
	ctx := need(t)
	if on, err := client.Approvals.SetSkipApprovals(ctx, true); err != nil || !on {
		t.Fatalf("skip on: %v %v", on, err)
	}
	if on, err := client.Approvals.SetSkipApprovals(ctx, false); err != nil || on {
		t.Fatalf("skip off: %v %v", on, err)
	}
	job, err := client.Schedules.Create(ctx, marbots.ScheduleSpec{Name: "weekly", BotID: "atlas", Prompt: "brief", Cron: "0 8 * * 1"})
	if err != nil || job.NextRunAt == nil {
		t.Fatalf("schedule: %+v %v", job, err)
	}
	_ = client.Schedules.Delete(ctx, job.ID)
	if _, err := client.Schedules.Create(ctx, marbots.ScheduleSpec{Name: "bad", BotID: "atlas", Prompt: "x", Cron: "nope"}); err == nil {
		t.Fatal("expected error for bad cron")
	}
	tpls, err := client.Templates.List(ctx, "designer", "")
	if err != nil || len(tpls) == 0 {
		t.Fatalf("templates: %v", err)
	}
}

func TestAgentHostsAndEnrollment(t *testing.T) {
	ctx := need(t)
	hosts, err := client.AgentHosts.List(ctx)
	if err != nil {
		t.Fatal(err)
	}
	local := false
	for _, h := range hosts {
		if h.ID == marbots.HostLocal {
			for _, c := range h.Capabilities {
				local = local || c == "shell"
			}
		}
	}
	if !local {
		t.Fatalf("local host without shell capability: %+v", hosts)
	}
	tok, err := client.AgentHosts.CreateEnrollment(ctx, "lab-pc", 10)
	if err != nil || !strings.HasPrefix(tok.Token, "mbe_") || !strings.Contains(tok.EnrollCommand, "marbots-host enroll") {
		t.Fatalf("enrollment: %+v %v", tok, err)
	}
	if err := client.AgentHosts.Disable(ctx, "host-that-does-not-exist"); err == nil {
		t.Fatal("expected an error for an unknown host")
	}
}

func TestPlacementAndContainerRoundTrip(t *testing.T) {
	ctx := need(t)
	bot, err := client.Bots.Create(ctx, marbots.BotSpec{
		Name: "Placed Go", HostRef: marbots.HostAuto,
		Container:       &marbots.ContainerProfile{Image: "python:3.12-slim", Cpus: 1.5, MemoryMb: 512, Network: false},
		KernelFunctions: []marbots.KernelPack{marbots.KernelPackShell, marbots.KernelPackSubagents},
	})
	if err != nil {
		t.Fatal(err)
	}
	if bot.HostRef != marbots.HostAuto || bot.Container == nil || bot.Container.Cpus != 1.5 || bot.Container.MemoryMb != 512 || bot.Container.Network {
		t.Fatalf("round trip: %+v %+v", bot, bot.Container)
	}
}

func TestSkillEvaluationsAndAutoRollback(t *testing.T) {
	ctx := need(t)
	evals, err := client.Skills.Evaluations(ctx)
	if err != nil || len(evals) == 0 {
		t.Fatalf("evaluations: %d %v", len(evals), err)
	}
	for _, e := range evals {
		if e.Verdict != marbots.VerdictCollectingEvidence {
			t.Fatalf("fresh skill %s has verdict %s", e.Name, e.Verdict)
		}
	}
	if on, err := client.Skills.SetAutoRollback(ctx, true); err != nil || !on {
		t.Fatalf("set auto rollback: %v %v", on, err)
	}
	if on, err := client.Skills.AutoRollback(ctx); err != nil || !on {
		t.Fatalf("auto rollback: %v %v", on, err)
	}
	if on, _ := client.Skills.SetAutoRollback(ctx, false); on {
		t.Fatal("auto rollback should be off")
	}
	if _, err := client.Skills.Rollback(ctx, "no-such-skill"); err == nil {
		t.Fatal("expected an error rolling back an unknown skill")
	}
}
