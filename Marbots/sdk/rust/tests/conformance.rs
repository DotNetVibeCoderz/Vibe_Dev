//! SDK conformance test: runs against a real Marbots.Server process with the offline mock model.
//! Build the server first (`dotnet build Marbots.slnx`) or set MARBOTS_SERVER_DLL; skipped when it is missing.

use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use marbots_sdk::*;

struct Server {
    child: Child,
    data: PathBuf,
    client: Client,
}

impl Drop for Server {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.data);
    }
}

fn start() -> Option<Server> {
    let dll = std::env::var("MARBOTS_SERVER_DLL")
        .map(PathBuf::from)
        .unwrap_or_else(|_| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("../../src/Marbots.Server/bin/Debug/net10.0/Marbots.Server.dll")
        });
    if !dll.exists() {
        eprintln!("skipped: server not built at {}", dll.display());
        return None;
    }
    let port = TcpListener::bind("127.0.0.1:0")
        .ok()?
        .local_addr()
        .ok()?
        .port();
    let data = std::env::temp_dir().join(format!("mb-rs-{port}"));
    let child = Command::new("dotnet")
        .arg(&dll)
        .args(["--urls", &format!("http://127.0.0.1:{port}")])
        .current_dir(dll.parent()?)
        .env("Marbots__DataDirectory", &data)
        .env("Marbots__Providers__0__Name", "lab")
        .env("Marbots__Providers__0__Kind", "mock")
        .env("Marbots__Providers__0__Models__0", "lab-small")
        .env("Marbots__Providers__0__Models__1", "lab-large")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .ok()?;
    let client = Client::new(format!("http://127.0.0.1:{port}"));
    let server = Server {
        child,
        data,
        client,
    };
    for _ in 0..120 {
        if server.client.system().is_ok() {
            return Some(server);
        }
        thread::sleep(Duration::from_millis(500));
    }
    panic!("server did not start");
}

#[test]
fn conformance() {
    let Some(server) = start() else { return };
    let mb = &server.client;

    let sys = mb.system().unwrap();
    assert_eq!(sys.product, "Marbots");
    assert!(sys.credits_en.contains("Gravicode"));
    assert!(mb
        .bots()
        .list()
        .unwrap()
        .iter()
        .any(|b| b.id == BOSS_MAN && b.is_system));

    // hire, per-bot model, chat, export/import
    let dina = mb.bots().hire("data-analyst", Some("Dina")).unwrap();
    assert!(dina.uses_default_model());
    assert!(dina.kernel_functions.contains(&KernelPack::Shell));
    assert_eq!(dina.permission_profile, PermissionProfile::DeveloperSafe);
    let info = mb
        .bots()
        .set_model(&dina.id, &ModelRef::of("lab", "lab-large"))
        .unwrap();
    assert_eq!(info.effective, "lab/lab-large");
    assert!(!info.uses_default);
    let err = mb
        .bots()
        .set_model(&dina.id, &ModelRef::of("ghost", "x"))
        .unwrap_err();
    assert_eq!(err.status(), Some(400));

    let thread = mb.threads().create(&dina.id, None).unwrap();
    let r = mb
        .threads()
        .send_with_timeout(&thread.id, "hello", true, 30)
        .unwrap();
    assert_eq!(r.task.state, TaskState::Completed);
    assert_eq!(r.task.model.as_deref(), Some("lab/lab-large"));
    assert!(r.text().contains("mock"));

    let imported = mb
        .bots()
        .import_package(&mb.bots().export(&dina.id, false).unwrap())
        .unwrap();
    assert_eq!(imported.model, "lab/lab-large");
    mb.bots().delete(&imported.id).unwrap();
    mb.bots().delete(&dina.id).unwrap();

    // typed spec
    let typed = mb
        .bots()
        .create(
            BotSpec::new("Typed Ferris")
                .kernel_functions([KernelPack::Files])
                .permission_profile(PermissionProfile::ReadOnly)
                .model(ModelRef::of("lab", "lab-small")),
        )
        .unwrap();
    assert_eq!(typed.kernel_functions, vec![KernelPack::Files]);
    assert_eq!(typed.permission_profile, PermissionProfile::ReadOnly);
    assert_eq!(
        mb.bots().get_model(&typed.id).unwrap().effective,
        "lab/lab-small"
    );
    mb.bots().delete(&typed.id).unwrap();

    // models
    let catalog = mb.models().list().unwrap();
    assert!(catalog.choices.iter().any(|c| c == "lab/lab-small"));
    assert_eq!(
        mb.models()
            .set_default(&ModelRef::of("lab", "lab-small"))
            .unwrap(),
        "lab/lab-small"
    );
    let wren = mb.bots().get_model("wren").unwrap();
    assert!(wren.uses_default);
    assert_eq!(wren.effective, "lab/lab-small");

    // events
    let atlas = mb.threads().create("atlas", None).unwrap();
    let (tx, rx) = mpsc::channel();
    let stream = mb.events().stream(Some(&atlas.id)).unwrap();
    thread::spawn(move || {
        for e in stream.flatten() {
            if e.event_type == EventType::TaskStateChanged && e.data.as_deref() == Some("Completed")
            {
                let _ = tx.send(true);
                return;
            }
        }
    });
    mb.threads().send(&atlas.id, "ping", false).unwrap();
    assert!(
        rx.recv_timeout(Duration::from_secs(30)).unwrap_or(false),
        "event stream reports completion"
    );

    // approvals & schedules & templates
    assert!(mb.approvals().set_skip_approvals(true).unwrap());
    assert!(mb.approvals().skip_approvals().unwrap());
    assert!(!mb.approvals().set_skip_approvals(false).unwrap());
    assert!(mb.approvals().pending().unwrap().is_empty());
    let job = mb
        .schedules()
        .create(&ScheduleSpec::cron("weekly", "atlas", "brief", "0 8 * * 1"))
        .unwrap();
    assert!(job.next_run_at.is_some());
    mb.schedules().delete(&job.id).unwrap();
    assert_eq!(
        mb.schedules()
            .create(&ScheduleSpec::cron("bad", "atlas", "x", "nope"))
            .unwrap_err()
            .status(),
        Some(400)
    );
    assert!(mb
        .templates()
        .list("designer", "")
        .unwrap()
        .iter()
        .any(|t| t.id == "ux-designer"));
}
