//! Custom tools implemented by the SDK host.
//!
//! With the default `derive` feature, parameters are a Rust type deriving `Deserialize` and [`JsonSchema`]; the
//! JSON Schema is generated from it and arguments are decoded before the handler runs:
//!
//! ```no_run
//! use dotcode_sdk::tool::{define_tool, JsonSchema};
//! use serde::Deserialize;
//!
//! #[derive(Deserialize, JsonSchema)]
//! struct WeatherParams {
//!     /// City name
//!     city: String,
//! }
//!
//! let weather = define_tool("get_weather", "Weather for a city", |_inv, p: WeatherParams| {
//!     Ok::<_, String>(format!("{}: sunny", p.city))
//! });
//! ```

use serde_json::Value;
use std::fmt::{self, Display};
use std::sync::Arc;

#[cfg(feature = "derive")]
pub use schemars::{self, JsonSchema};

/// One call of a custom tool.
#[derive(Debug, Clone)]
pub struct ToolInvocation {
    pub session_id: String,
    pub tool_call_id: String,
    pub tool_name: String,
    /// Raw arguments as sent by the model.
    pub arguments: Value,
}

/// The result of a tool call. `String`, `&str` and `serde_json::Value` convert into it.
#[derive(Debug, Clone, PartialEq)]
pub enum ToolResult {
    /// Text for the model.
    Text(String),
    /// A JSON value (sent as text).
    Json(Value),
    /// Text with images (base64 data, mime type) for vision-capable models.
    Rich {
        text: String,
        images: Vec<(String, String)>,
    },
    /// A failed call; the text explains the problem to the model.
    Failure(String),
}

impl From<String> for ToolResult {
    fn from(s: String) -> Self {
        ToolResult::Text(s)
    }
}

impl From<&str> for ToolResult {
    fn from(s: &str) -> Self {
        ToolResult::Text(s.to_string())
    }
}

impl From<Value> for ToolResult {
    fn from(v: Value) -> Self {
        ToolResult::Json(v)
    }
}

impl ToolResult {
    pub(crate) fn wire(&self) -> Value {
        match self {
            ToolResult::Text(t) => serde_json::json!({"content": t}),
            ToolResult::Json(v) => serde_json::json!({"content": v.to_string()}),
            ToolResult::Failure(t) => serde_json::json!({"content": t, "isError": true}),
            ToolResult::Rich { text, images } => {
                let mut parts = vec![serde_json::json!({"type": "text", "text": text})];
                parts.extend(images.iter().map(|(data, mime)| serde_json::json!({"type": "image", "data": data, "mediaType": mime})));
                serde_json::json!({"content": parts})
            }
        }
    }
}

/// An error returned by a tool handler (shown to the model).
#[derive(Debug, Clone)]
pub struct ToolError(pub String);

impl Display for ToolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ToolError {}

/// Implemented by named tool types (use [`define_tool`] for closures).
pub trait ToolHandler: Send + Sync {
    fn call(&self, invocation: ToolInvocation) -> Result<ToolResult, ToolError>;
}

/// A tool offered to the model.
#[derive(Clone)]
pub struct Tool {
    pub name: String,
    pub description: String,
    /// JSON Schema of the arguments (`None`: no parameters).
    pub parameters: Option<Value>,
    /// Read-only tools may run in plan mode. Custom tools never prompt for permission.
    pub read_only: bool,
    pub(crate) handler: Option<Arc<dyn ToolHandler>>,
}

impl Tool {
    /// A tool declaration; attach a handler with [`Tool::with_handler`].
    pub fn new(name: impl Into<String>) -> Tool {
        Tool {
            name: name.into(),
            description: String::new(),
            parameters: None,
            read_only: false,
            handler: None,
        }
    }

    pub fn with_description(mut self, description: impl Into<String>) -> Tool {
        self.description = description.into();
        self
    }

    /// Sets the JSON Schema (prefer [`schema_for`] over a hand-written value).
    pub fn with_parameters(mut self, schema: Value) -> Tool {
        self.parameters = Some(schema);
        self
    }

    pub fn with_handler(mut self, handler: Arc<dyn ToolHandler>) -> Tool {
        self.handler = Some(handler);
        self
    }

    pub fn read_only(mut self) -> Tool {
        self.read_only = true;
        self
    }

    pub(crate) fn wire(&self) -> Value {
        let schema = self
            .parameters
            .clone()
            .unwrap_or_else(|| serde_json::json!({"type": "object", "properties": {}}));
        serde_json::json!({"name": self.name, "description": self.description, "inputSchema": schema, "readOnly": self.read_only})
    }
}

impl fmt::Debug for Tool {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Tool")
            .field("name", &self.name)
            .field("read_only", &self.read_only)
            .finish()
    }
}

/// JSON Schema of `T`, ready for the model (no `$schema`/`title` noise).
#[cfg(feature = "derive")]
pub fn schema_for<T: JsonSchema>() -> Value {
    let mut schema = serde_json::to_value(schemars::schema_for!(T)).unwrap_or(Value::Null);
    if let Value::Object(map) = &mut schema {
        map.remove("$schema");
        map.remove("title");
    }
    schema
}

#[cfg(feature = "derive")]
struct FnTool<P, F> {
    f: F,
    _params: std::marker::PhantomData<fn(P)>,
}

#[cfg(feature = "derive")]
impl<P, F, R, E> ToolHandler for FnTool<P, F>
where
    P: serde::de::DeserializeOwned,
    F: Fn(&ToolInvocation, P) -> Result<R, E> + Send + Sync,
    R: Into<ToolResult>,
    E: Display,
{
    fn call(&self, invocation: ToolInvocation) -> Result<ToolResult, ToolError> {
        let args = if invocation.arguments.is_null() {
            serde_json::json!({})
        } else {
            invocation.arguments.clone()
        };
        let params: P = serde_json::from_value(args)
            .map_err(|e| ToolError(format!("invalid arguments: {e}")))?;
        (self.f)(&invocation, params)
            .map(Into::into)
            .map_err(|e| ToolError(e.to_string()))
    }
}

/// Defines a tool from a closure. `P` (deriving `Deserialize` + [`JsonSchema`]) describes the parameters; doc
/// comments on its fields become descriptions. The closure returns `Ok(String | &str | Value | ToolResult)` or an
/// error shown to the model.
#[cfg(feature = "derive")]
pub fn define_tool<P, F, R, E>(
    name: impl Into<String>,
    description: impl Into<String>,
    handler: F,
) -> Tool
where
    P: serde::de::DeserializeOwned + JsonSchema + 'static,
    F: Fn(&ToolInvocation, P) -> Result<R, E> + Send + Sync + 'static,
    R: Into<ToolResult> + 'static,
    E: Display + 'static,
{
    Tool::new(name)
        .with_description(description)
        .with_parameters(schema_for::<P>())
        .with_handler(Arc::new(FnTool {
            f: handler,
            _params: std::marker::PhantomData,
        }))
}
