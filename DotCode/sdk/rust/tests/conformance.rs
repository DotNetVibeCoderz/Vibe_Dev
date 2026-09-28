//! SDK conformance tests: they drive a real `dotcode serve` with the offline scripted model (no network, no keys).
//! Requires the CLI: `dotnet build` in DotCode/ (or set DOTCODE_CLI_PATH).

use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionOptions, Tool};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

fn cli_path() -> Option<String> {
    if let Ok(p) = std::env::var("DOTCODE_CLI_PATH") {
        if !p.is_empty() {
            return Some(p);
        }
    }
    let p = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll");
    p.exists()
        .then(|| p.canonicalize().unwrap().to_string_lossy().into_owned())
}

fn temp_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("dotcode-rust-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn client(dir: &Path, cli: String) -> Client {
    let mut env = HashMap::new();
    env.insert(
        "DOTCODE_CONFIG_DIR".to_string(),
        dir.join(".cfg").to_string_lossy().into_owned(),
    );
    Client::new(ClientOptions {
        cli_path: Some(cli),
        cwd: Some(dir.to_path_buf()),
        env,
        ..Default::default()
    })
    .expect("start dotcode serve")
}

fn scripted(dir: &Path, script: Value) -> Value {
    let path = dir.join("script.json");
    std::fs::write(&path, script.to_string()).unwrap();
    json!({"providers": {"mock": {"type": "mock", "script": path.to_string_lossy()}}})
}

fn base(dir: &Path, script: Value) -> SessionOptions {
    SessionOptions {
        persist_session: Some(false),
        no_mcp: true,
        ..Default::default()
    }
    .model("mock:scripted")
    .settings(scripted(dir, script))
}

#[test]
fn custom_tool_and_streaming() {
    let Some(cli) = cli_path() else {
        eprintln!("DotCode CLI not built; skipping");
        return;
    };
    let dir = temp_dir("tool");
    let client = client(&dir, cli);
    assert!(!client.server_version.is_empty());
    let weather = Tool::new(
        "get_weather",
        "Weather for a city",
        json!({"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}),
        |input| {
            Ok(format!(
                "{}: rainy, 24°C",
                input["city"].as_str().unwrap_or("?")
            ))
        },
    )
    .read_only();
    let session = client
        .create_session(base(
            &dir,
            json!({"responses": [
                {"text": "Checking the weather.", "toolCalls": [{"name": "get_weather", "input": {"city": "Bogor"}}]},
                {"text": "It is rainy in Bogor."}
            ]}),
        ).tool(weather))
        .unwrap();

    let mut stream = session.stream("weather?");
    let events: Vec<_> = stream.by_ref().collect();
    let result = stream.result().unwrap();
    assert_eq!(result.result, "It is rainy in Bogor.");
    let last = events.last().unwrap();
    assert!(last.is_turn_completed(), "last event was {}", last.kind);
    assert_eq!(last.result_text.as_deref(), Some("It is rainy in Bogor."));
    assert!(events.iter().any(|e| e.kind == "tool.completed"
        && e.name.as_deref() == Some("get_weather")
        && e.output.as_deref().unwrap_or("").contains("rainy")));
    assert!(events.iter().any(|e| e.kind == "assistant.text.delta"));
    assert_eq!(session.messages().unwrap().len(), 4);
    session.close().unwrap();
}

#[test]
fn permission_handler_and_deny_by_default() {
    let Some(cli) = cli_path() else {
        eprintln!("DotCode CLI not built; skipping");
        return;
    };
    let dir = temp_dir("perm");
    let client = client(&dir, cli);
    let target = dir.join("out.txt");
    let script = json!({"responses": [
        {"toolCalls": [{"name": "Write", "input": {"file_path": target.to_string_lossy(), "content": "from rust"}}]},
        {"text": "written"}
    ]});

    let asked = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&asked);
    let session = client
        .create_session(
            base(&dir, script.clone()).on_permission_request(move |req| {
                seen.lock().unwrap().push(req.tool_name.clone());
                PermissionDecision::allow()
            }),
        )
        .unwrap();
    let result = session.send("write a file").unwrap();
    assert_eq!(result.result, "written");
    assert_eq!(*asked.lock().unwrap(), vec!["Write".to_string()]);
    assert_eq!(std::fs::read_to_string(&target).unwrap(), "from rust");

    // Without a handler the session is deny-by-default: the write never happens.
    std::fs::remove_file(&target).unwrap();
    let denied = client.create_session(base(&dir, script)).unwrap();
    denied.send("write a file").unwrap();
    assert!(!target.exists());
    let transcript = denied
        .messages()
        .unwrap()
        .iter()
        .map(Value::to_string)
        .collect::<String>();
    assert!(transcript.contains("deny by default"), "{transcript}");
}
