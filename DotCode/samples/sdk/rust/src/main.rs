//! DotCode Rust SDK sample. Run: cargo run -- azure:gpt-5-mini
use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionOptions, Tool};
use serde_json::json;
use std::io::Write;

fn main() -> Result<(), dotcode_sdk::Error> {
    let client = Client::new(ClientOptions::default())?;
    let mut options = SessionOptions {
        persist_session: Some(false),
        ..Default::default()
    }
    .tool(
        Tool::new(
            "get_exchange_rate",
            "Get the exchange rate between two currencies",
            json!({"type": "object", "properties": {"from": {"type": "string"}, "to": {"type": "string"}}, "required": ["from", "to"]}),
            |input| {
                let (from, to) = (input["from"].as_str().unwrap_or("?"), input["to"].as_str().unwrap_or("?"));
                let rate = if to == "IDR" { "16,250" } else { "0.92" };
                Ok(format!("1 {from} = {rate} {to} (demo data)"))
            },
        )
        .read_only(),
    )
    .on_permission_request(|_| PermissionDecision::allow());
    if let Some(model) = std::env::args().nth(1) {
        options = options.model(model);
    }
    let session = client.create_session(options)?;
    println!("DotCode SDK (Rust) · model {}\n", session.model());

    let mut stream = session.stream(
        "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.",
    );
    let mut mid_line = false;
    for e in stream.by_ref() {
        match e.kind.as_str() {
            "assistant.text.delta" => {
                print!("{}", e.text.unwrap_or_default());
                std::io::stdout().flush().ok();
                mid_line = true;
            }
            "tool.started" => {
                if std::mem::take(&mut mid_line) {
                    println!();
                }
                println!("● {}", e.display_name.unwrap_or_default());
            }
            "tool.completed" => println!("  ⎿  {}", e.output.unwrap_or_default()),
            "turn.completed" => println!(
                "\n\n✔ {} model calls · ${:.4} · {} ms",
                e.num_model_calls, e.cost_usd, e.duration_ms
            ),
            _ => {}
        }
    }
    stream.result()?;
    Ok(())
}
