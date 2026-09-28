use crate::session::{Session, SessionOptions, SessionShared};
use crate::types::{Error, Result, SessionInfo};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

/// DotCode agent protocol version implemented by this SDK.
pub const PROTOCOL_VERSION: &str = "1.0";

/// How the SDK starts the DotCode server.
#[derive(Debug, Clone, Default)]
pub struct ClientOptions {
    /// The `dotcode` executable (or `dotcode.dll`, started with `dotnet`). Defaults to `$DOTCODE_CLI_PATH`, then
    /// `dotcode` on `PATH`.
    pub cli_path: Option<String>,
    /// Extra arguments for `dotcode serve`.
    pub server_args: Vec<String>,
    /// Default working directory for sessions (and the server process).
    pub cwd: Option<PathBuf>,
    /// Extra environment variables for the server process.
    pub env: HashMap<String, String>,
}

type Reply = std::result::Result<Value, Error>;

pub(crate) struct Inner {
    stdin: Mutex<Option<ChildStdin>>,
    next_id: AtomicI64,
    pending: Mutex<HashMap<i64, Sender<Reply>>>,
    pub(crate) sessions: Mutex<HashMap<String, Arc<SessionShared>>>,
    closed: AtomicBool,
    pub(crate) default_cwd: Option<String>,
}

impl Inner {
    fn write(&self, message: &Value) -> Result<()> {
        let mut line = serde_json::to_vec(message)?;
        line.push(b'\n');
        let mut guard = self.stdin.lock().unwrap();
        let stdin = guard.as_mut().ok_or(Error::Closed)?;
        stdin.write_all(&line)?;
        stdin.flush()?;
        Ok(())
    }

    /// Sends a request and waits for its response (optionally with a timeout).
    pub(crate) fn call(
        &self,
        method: &str,
        params: Value,
        timeout: Option<Duration>,
    ) -> Result<Value> {
        if self.closed.load(Ordering::SeqCst) {
            return Err(Error::Closed);
        }
        let id = self.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        let (tx, rx) = mpsc::channel();
        self.pending.lock().unwrap().insert(id, tx);
        if let Err(e) =
            self.write(&json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params}))
        {
            self.pending.lock().unwrap().remove(&id);
            return Err(e);
        }
        let reply = match timeout {
            Some(t) => rx.recv_timeout(t).map_err(|_| Error::Timeout),
            None => rx.recv().map_err(|_| Error::Closed),
        };
        match reply {
            Ok(r) => r,
            Err(e) => {
                self.pending.lock().unwrap().remove(&id);
                Err(e)
            }
        }
    }

    fn session(&self, id: &str) -> Option<Arc<SessionShared>> {
        self.sessions.lock().unwrap().get(id).cloned()
    }

    fn fail_pending(&self) {
        self.closed.store(true, Ordering::SeqCst);
        for (_, tx) in self.pending.lock().unwrap().drain() {
            let _ = tx.send(Err(Error::Closed));
        }
    }
}

/// Owns the `dotcode serve` process and its sessions. Dropping the client shuts the server down.
pub struct Client {
    inner: Arc<Inner>,
    child: Mutex<Option<Child>>,
    /// Server version reported during the handshake.
    pub server_version: String,
}

impl Client {
    /// Starts `dotcode serve` and performs the protocol handshake.
    pub fn new(options: ClientOptions) -> Result<Client> {
        let cli = options
            .cli_path
            .clone()
            .or_else(|| {
                std::env::var("DOTCODE_CLI_PATH")
                    .ok()
                    .filter(|p| !p.is_empty())
            })
            .unwrap_or_else(|| "dotcode".to_string());
        let mut command = if cli.to_lowercase().ends_with(".dll") {
            let mut c = Command::new("dotnet");
            c.arg(&cli);
            c
        } else {
            Command::new(&cli)
        };
        command.arg("serve").args(&options.server_args);
        if let Some(cwd) = &options.cwd {
            command.current_dir(cwd);
        }
        command
            .envs(&options.env)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit());
        let mut child = command.spawn().map_err(|e| {
            Error::Spawn(format!(
                "could not start {cli:?} (install the DotCode CLI or set DOTCODE_CLI_PATH): {e}"
            ))
        })?;
        let stdin = child.stdin.take();
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| Error::Spawn("no stdout".into()))?;
        let inner = Arc::new(Inner {
            stdin: Mutex::new(stdin),
            next_id: AtomicI64::new(0),
            pending: Mutex::new(HashMap::new()),
            sessions: Mutex::new(HashMap::new()),
            closed: AtomicBool::new(false),
            default_cwd: options
                .cwd
                .as_ref()
                .map(|p| p.to_string_lossy().into_owned()),
        });
        let reader = Arc::clone(&inner);
        thread::Builder::new()
            .name("dotcode-sdk-reader".into())
            .spawn(move || read_loop(reader, stdout))
            .map_err(Error::Io)?;

        let mut client = Client {
            inner,
            child: Mutex::new(Some(child)),
            server_version: String::new(),
        };
        let init = client.inner.call(
            "initialize",
            json!({
                "protocolVersion": PROTOCOL_VERSION,
                "clientInfo": {"name": "dotcode-sdk-rust", "version": env!("CARGO_PKG_VERSION")},
                "capabilities": {"permissions": true, "questions": true}
            }),
            Some(Duration::from_secs(60)),
        );
        match init {
            Ok(v) => {
                client.server_version = v["serverInfo"]["version"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                Ok(client)
            }
            Err(e) => {
                client.close();
                Err(e)
            }
        }
    }

    /// Starts a new agent session.
    pub fn create_session(&self, options: SessionOptions) -> Result<Session> {
        self.open("session.create", options, None)
    }

    /// Reopens a saved session; `fork` copies it under a new id.
    pub fn resume_session(
        &self,
        session_id: &str,
        options: SessionOptions,
        fork: bool,
    ) -> Result<Session> {
        self.open(
            "session.resume",
            options,
            Some(json!({"sessionId": session_id, "fork": fork})),
        )
    }

    fn open(&self, method: &str, options: SessionOptions, extra: Option<Value>) -> Result<Session> {
        let mut params = options.wire(self.inner.default_cwd.as_deref());
        if let (Some(Value::Object(extra)), Value::Object(map)) = (extra, &mut params) {
            map.extend(extra);
        }
        let info: SessionInfo = serde_json::from_value(self.inner.call(method, params, None)?)?;
        let shared = Arc::new(SessionShared::new(options, info.model.clone()));
        self.inner
            .sessions
            .lock()
            .unwrap()
            .insert(info.session_id.clone(), Arc::clone(&shared));
        Ok(Session::new(Arc::clone(&self.inner), shared, info))
    }

    /// Configured providers and their models.
    pub fn list_models(&self) -> Result<Value> {
        self.inner
            .call("models.list", json!({"cwd": self.inner.default_cwd}), None)
    }

    /// Saved sessions for the working directory.
    pub fn list_sessions(&self) -> Result<Value> {
        self.inner
            .call("session.list", json!({"cwd": self.inner.default_cwd}), None)
    }

    /// Shuts the server down (also done on drop).
    pub fn close(&mut self) {
        if !self.inner.closed.load(Ordering::SeqCst) {
            let _ = self
                .inner
                .call("shutdown", json!({}), Some(Duration::from_millis(1500)));
        }
        self.inner.stdin.lock().unwrap().take();
        if let Some(mut child) = self.child.lock().unwrap().take() {
            let deadline = Instant::now() + Duration::from_secs(3);
            loop {
                match child.try_wait() {
                    Ok(Some(_)) => break,
                    Ok(None) if Instant::now() < deadline => {
                        thread::sleep(Duration::from_millis(50))
                    }
                    _ => {
                        let _ = child.kill();
                        let _ = child.wait();
                        break;
                    }
                }
            }
        }
        self.inner.fail_pending();
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        self.close();
    }
}

fn read_loop(inner: Arc<Inner>, stdout: std::process::ChildStdout) {
    let reader = BufReader::with_capacity(1 << 20, stdout);
    for line in reader.lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let Ok(msg) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        let method = msg.get("method").and_then(Value::as_str);
        let id = msg.get("id").filter(|v| !v.is_null());
        match (method, id) {
            // Response to one of our requests.
            (None, Some(id)) => {
                let Some(id) = id.as_i64() else { continue };
                if let Some(tx) = inner.pending.lock().unwrap().remove(&id) {
                    let reply = match msg.get("error") {
                        Some(err) if !err.is_null() => Err(Error::Rpc {
                            code: err["code"].as_i64().unwrap_or(0),
                            message: err["message"].as_str().unwrap_or("error").to_string(),
                        }),
                        _ => Ok(msg.get("result").cloned().unwrap_or(Value::Null)),
                    };
                    let _ = tx.send(reply);
                }
            }
            // Engine event for a session.
            (Some("session.event"), None) => {
                let params = &msg["params"];
                if let Some(session) = params["sessionId"].as_str().and_then(|s| inner.session(s)) {
                    session.dispatch(&params["event"]);
                }
            }
            // Server → client request (permission, question, plan review, host tool): answered on a worker thread
            // so a slow handler never blocks event delivery.
            (Some(method), Some(id)) => {
                let inner = Arc::clone(&inner);
                let method = method.to_string();
                let id = id.clone();
                let params = msg.get("params").cloned().unwrap_or(Value::Null);
                thread::spawn(move || {
                    let session = params["sessionId"].as_str().and_then(|s| inner.session(s));
                    let answer = match session {
                        None => {
                            json!({"jsonrpc": "2.0", "id": id, "error": {"code": -32001, "message": "unknown session"}})
                        }
                        Some(s) => match s.handle_server_request(&method, &params) {
                            Ok(result) => json!({"jsonrpc": "2.0", "id": id, "result": result}),
                            Err(message) => {
                                json!({"jsonrpc": "2.0", "id": id, "error": {"code": -32603, "message": message}})
                            }
                        },
                    };
                    let _ = inner.write(&answer);
                });
            }
            _ => {}
        }
    }
    inner.fail_pending();
}
