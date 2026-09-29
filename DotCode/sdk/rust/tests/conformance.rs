//! SDK conformance tests: they drive a real `dotcode serve` with the offline scripted model (no network, no keys).
//! Requires the CLI: `dotnet build` in DotCode/ (or set DOTCODE_CLI_PATH).

use dotcode_sdk::tool::{define_tool, schema_for, JsonSchema};
use dotcode_sdk::{
    Client, ClientOptions, PermissionDecision, ProviderConfig, ProviderType, SessionConfig,
    SessionEventData, StopReason, Tool,
};
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::time::Duration;

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
    Client::start(ClientOptions {
        cli_path: Some(cli),
        cwd: Some(dir.to_path_buf()),
        env,
        ..Default::default()
    })
    .expect("start dotcode serve")
}

fn base(dir: &Path, responses: Value) -> SessionConfig {
    let path = dir.join("script.json");
    std::fs::write(&path, json!({ "responses": responses }).to_string()).unwrap();
    SessionConfig::default()
        .with_model("mock:scripted")
        .with_provider(
            "mock",
            ProviderConfig::new(ProviderType::Mock).with_script(path.to_string_lossy()),
        )
        .with_persist_session(false)
        .with_disable_mcp(true)
}

#[derive(Deserialize, JsonSchema)]
struct WeatherParams {
    /// City name
    city: String,
}

fn weather_tool() -> Tool {
    define_tool(
        "get_weather",
        "Weather for a city",
        |inv, p: WeatherParams| {
            Ok::<_, String>(format!("{}: rainy, 24°C ({})", p.city, inv.tool_name))
        },
    )
    .read_only()
}

#[test]
fn schema_is_generated_from_the_params_type() {
    #[derive(Deserialize, JsonSchema)]
    #[allow(dead_code)]
    struct Params {
        /// City name
        city: String,
        days: Option<u32>,
    }
    let schema = schema_for::<Params>();
    assert_eq!(schema["type"], "object");
    assert_eq!(schema["properties"]["city"]["description"], "City name");
    assert_eq!(schema["required"], json!(["city"]));
    assert!(schema.get("$schema").is_none());
}

#[test]
fn custom_tool_and_streaming() {
    let Some(cli) = cli_path() else {
        eprintln!("DotCode CLI not built; skipping");
        return;
    };
    let dir = temp_dir("tool");
    let client = client(&dir, cli);
    let session = client
        .create_session(
            base(
                &dir,
                json!([
                    {"text": "Checking the weather.", "toolCalls": [{"name": "get_weather", "input": {"city": "Bogor"}}]},
                    {"text": "It is rainy in Bogor."}
                ]),
            )
            .with_tools([weather_tool()]),
        )
        .unwrap();

    let completed = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&completed);
    let _sub = session.on(move |e| {
        if let SessionEventData::ToolCompleted { name, output, .. } = &e.data {
            seen.lock().unwrap().push((name.clone(), output.clone()));
        }
    });
    let mut stream = session.stream("weather?");
    let events: Vec<_> = stream.by_ref().collect();
    let result = stream.result().unwrap();
    assert_eq!(result.result, "It is rainy in Bogor.");
    assert_eq!(result.stop_reason, StopReason::EndTurn);
    let last = events.last().unwrap();
    assert!(last.is_turn_completed(), "last event was {:?}", last.data);
    assert!(events
        .iter()
        .any(|e| matches!(e.data, SessionEventData::AssistantTextDelta { .. })));
    let completed = completed.lock().unwrap();
    assert_eq!(completed.len(), 1);
    assert_eq!(completed[0].0, "get_weather");
    assert!(
        completed[0].1.contains("rainy, 24°C (get_weather)"),
        "{}",
        completed[0].1
    );
    assert_eq!(session.get_messages().unwrap().len(), 4);
    session.disconnect().unwrap();
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
    let responses = json!([
        {"toolCalls": [{"name": "Write", "input": {"file_path": target.to_string_lossy(), "content": "from rust"}}]},
        {"text": "written"}
    ]);

    let asked = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&asked);
    let session = client
        .create_session(
            base(&dir, responses.clone()).on_permission_request(move |req, _| {
                seen.lock().unwrap().push(req.tool_name.clone());
                PermissionDecision::approve_once()
            }),
        )
        .unwrap();
    let (tx, rx) = mpsc::channel();
    session
        .on(move |e| {
            if let SessionEventData::TurnCompleted { result_text, .. } = &e.data {
                let _ = tx.send(result_text.clone());
            }
        })
        .detach();
    session.send("write a file").unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(60)).unwrap(), "written");
    assert_eq!(*asked.lock().unwrap(), vec!["Write".to_string()]);
    assert_eq!(std::fs::read_to_string(&target).unwrap(), "from rust");

    // Without a handler the session is deny-by-default: the write never happens.
    std::fs::remove_file(&target).unwrap();
    let denied = client.create_session(base(&dir, responses)).unwrap();
    denied.send_and_wait("write a file").unwrap();
    assert!(!target.exists());
    let transcript = denied
        .get_messages()
        .unwrap()
        .iter()
        .map(Value::to_string)
        .collect::<String>();
    assert!(transcript.contains("deny by default"), "{transcript}");
}

#[test]
fn invalid_arguments_are_reported_to_the_model() {
    let Some(cli) = cli_path() else {
        eprintln!("DotCode CLI not built; skipping");
        return;
    };
    let dir = temp_dir("args");
    let client = client(&dir, cli);
    let session = client
        .create_session(
            base(&dir, json!([{"toolCalls": [{"name": "get_weather", "input": {"city": 42}}]}, {"text": "done"}]))
                .with_tools([weather_tool()]),
        )
        .unwrap();
    let events = session.subscribe();
    let result = session
        .send_and_wait_timeout("weather?", Duration::from_secs(60))
        .unwrap();
    assert_eq!(result.result, "done");
    let failed = events.try_iter().find_map(|e| match e.data {
        SessionEventData::ToolCompleted {
            is_error: true,
            output,
            ..
        } => Some(output),
        _ => None,
    });
    assert!(
        failed.is_some_and(|o| o.contains("city") || o.contains("invalid")),
        "tool error not reported"
    );
}
