//! DotCode Rust SDK sample. Run: cargo run -- azure:gpt-5-mini
use dotcode_sdk::tool::{define_tool, JsonSchema};
use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionConfig, SessionEventData};
use serde::Deserialize;
use std::io::Write;

/// Tool parameters are a Rust type: the JSON schema is generated from it and a typo does not compile.
#[derive(Deserialize, JsonSchema)]
struct ExchangeRateParams {
    /// ISO currency code, e.g. USD
    from: String,
    /// ISO currency code, e.g. IDR
    to: String,
}

fn main() -> Result<(), dotcode_sdk::Error> {
    let get_exchange_rate = define_tool(
        "get_exchange_rate",
        "Get the exchange rate between two currencies",
        |_inv, p: ExchangeRateParams| {
            let rate = if p.to == "IDR" { "16,250" } else { "0.92" };
            Ok::<_, String>(format!("1 {} = {rate} {} (demo data)", p.from, p.to))
        },
    )
    .read_only();

    let client = Client::start(ClientOptions::default())?;
    let mut config = SessionConfig::default()
        .with_persist_session(false)
        .with_tools([get_exchange_rate])
        .on_permission_request(|req, _| {
            println!("  [permission] {} → allowed", req.display_name);
            PermissionDecision::approve_once()
        });
    if let Some(model) = std::env::args().nth(1) {
        config = config.with_model(model);
    }
    let session = client.create_session(config)?;
    println!("DotCode SDK (Rust) · model {}\n", session.model());

    let _events = session.on(|e| match &e.data {
        SessionEventData::AssistantTextDelta { text } => {
            print!("{text}");
            std::io::stdout().flush().ok();
        }
        SessionEventData::ToolStarted { display_name, .. } => println!("\n● {display_name}"),
        SessionEventData::ToolCompleted { output, .. } => println!("  ⎿  {output}"),
        _ => {}
    });
    let result = session.send_and_wait(
        "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.",
    )?;
    println!(
        "\n\n✔ {} model calls · ${:.4} · {} ms",
        result.num_model_calls, result.cost_usd, result.duration_ms
    );
    session.disconnect()?;
    client.stop()
}
