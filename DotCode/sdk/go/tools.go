package dotcode

import (
	"encoding/json"
	"fmt"
	"reflect"
	"strings"
)

// ToolInvocation describes one call of a custom tool.
type ToolInvocation struct {
	SessionID  string
	ToolCallID string
	ToolName   string
	// Arguments are the raw arguments sent by the model.
	Arguments json.RawMessage
}

// ToolResultType marks a result as success or failure.
type ToolResultType string

const (
	ToolResultSuccess ToolResultType = "success"
	ToolResultFailure ToolResultType = "failure"
)

// BinaryResult is an image returned to the model (base64 data).
type BinaryResult struct {
	Data     string
	MimeType string
}

// ToolResult gives full control over a tool result.
type ToolResult struct {
	TextResultForLLM    string
	ResultType          ToolResultType // default success
	BinaryResultsForLLM []BinaryResult
}

// Tool is a custom tool implemented by your application. Prefer DefineTool, which derives Parameters from a
// Go type; build the struct directly only when you need a hand-tuned schema.
type Tool struct {
	Name        string
	Description string
	// Parameters is the JSON Schema of the arguments (nil: no parameters).
	Parameters map[string]any
	// ReadOnly tools may run in plan mode. Custom tools never prompt for permission.
	ReadOnly bool
	Handler  func(invocation ToolInvocation) (ToolResult, error)
}

// DefineTool creates a tool whose parameters are the fields of T (a struct). The JSON Schema is generated from
// T: names come from `json` tags, descriptions from `jsonschema` tags, allowed values from `enum` tags
// ("a,b,c"); `omitempty` and pointer fields are optional. Arguments are decoded into T before handler runs, so a
// misspelled field is a compile error.
//
//	type WeatherParams struct {
//		City string `json:"city" jsonschema:"City name"`
//		Unit string `json:"unit,omitempty" jsonschema:"Temperature unit" enum:"celsius,fahrenheit"`
//	}
//	weather := dotcode.DefineTool("get_weather", "Weather for a city",
//		func(p WeatherParams, inv dotcode.ToolInvocation) (any, error) { return p.City + ": sunny", nil })
//
// The handler returns a string, a ToolResult or any JSON-serializable value.
func DefineTool[T any](name, description string, handler func(params T, invocation ToolInvocation) (any, error)) Tool {
	schema := SchemaFor[T]()
	return Tool{
		Name:        name,
		Description: description,
		Parameters:  schema,
		Handler: func(inv ToolInvocation) (ToolResult, error) {
			var params T
			raw := inv.Arguments
			if len(raw) == 0 || string(raw) == "null" {
				raw = json.RawMessage("{}")
			}
			if err := validate(schema, raw); err != nil {
				return ToolResult{}, err
			}
			if err := json.Unmarshal(raw, &params); err != nil {
				return ToolResult{}, fmt.Errorf("invalid arguments: %w", err)
			}
			out, err := handler(params, inv)
			if err != nil {
				return ToolResult{}, err
			}
			return toToolResult(out)
		},
	}
}

// WithReadOnly marks the tool read-only (allowed in plan mode).
func (t Tool) WithReadOnly() Tool {
	t.ReadOnly = true
	return t
}

func toToolResult(v any) (ToolResult, error) {
	switch x := v.(type) {
	case nil:
		return ToolResult{}, nil
	case string:
		return ToolResult{TextResultForLLM: x}, nil
	case ToolResult:
		return x, nil
	case *ToolResult:
		return *x, nil
	}
	b, err := json.Marshal(v)
	if err != nil {
		return ToolResult{}, err
	}
	return ToolResult{TextResultForLLM: string(b)}, nil
}

func (r ToolResult) wire() map[string]any {
	var content any = r.TextResultForLLM
	if len(r.BinaryResultsForLLM) > 0 {
		parts := []map[string]any{{"type": "text", "text": r.TextResultForLLM}}
		for _, b := range r.BinaryResultsForLLM {
			parts = append(parts, map[string]any{"type": "image", "data": b.Data, "mediaType": b.MimeType})
		}
		content = parts
	}
	return map[string]any{"content": content, "isError": r.ResultType == ToolResultFailure}
}

// SchemaFor returns the JSON Schema DefineTool generates for T.
func SchemaFor[T any]() map[string]any {
	return schemaOf(reflect.TypeOf((*T)(nil)).Elem(), map[reflect.Type]bool{})
}

type enumType interface{ EnumValues() []string }

var enumIface = reflect.TypeOf((*enumType)(nil)).Elem()

func schemaOf(t reflect.Type, seen map[reflect.Type]bool) map[string]any {
	if t.Implements(enumIface) {
		values := reflect.Zero(t).Interface().(enumType).EnumValues()
		return map[string]any{"type": "string", "enum": values}
	}
	switch t.Kind() {
	case reflect.Pointer:
		return schemaOf(t.Elem(), seen)
	case reflect.String:
		return map[string]any{"type": "string"}
	case reflect.Bool:
		return map[string]any{"type": "boolean"}
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64,
		reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return map[string]any{"type": "integer"}
	case reflect.Float32, reflect.Float64:
		return map[string]any{"type": "number"}
	case reflect.Slice, reflect.Array:
		return map[string]any{"type": "array", "items": schemaOf(t.Elem(), seen)}
	case reflect.Map:
		return map[string]any{"type": "object", "additionalProperties": schemaOf(t.Elem(), seen)}
	case reflect.Interface:
		return map[string]any{}
	case reflect.Struct:
		if seen[t] {
			return map[string]any{"type": "object"}
		}
		seen[t] = true
		defer delete(seen, t)
		properties := map[string]any{}
		required := []string{}
		for i := 0; i < t.NumField(); i++ {
			f := t.Field(i)
			if !f.IsExported() {
				continue
			}
			name, opts, _ := strings.Cut(f.Tag.Get("json"), ",")
			if name == "-" {
				continue
			}
			if name == "" {
				name = f.Name
			}
			prop := schemaOf(f.Type, seen)
			if d := f.Tag.Get("jsonschema"); d != "" {
				prop["description"] = d
			}
			if e := f.Tag.Get("enum"); e != "" {
				prop["enum"] = strings.Split(e, ",")
			}
			properties[name] = prop
			if !strings.Contains(opts, "omitempty") && f.Type.Kind() != reflect.Pointer {
				required = append(required, name)
			}
		}
		s := map[string]any{"type": "object", "properties": properties, "additionalProperties": false}
		if len(required) > 0 {
			s["required"] = required
		}
		return s
	}
	panic(fmt.Sprintf("dotcode: unsupported tool parameter type %s", t))
}

// validate checks required properties and primitive types so the model gets a clear error.
func validate(schema map[string]any, raw json.RawMessage) error {
	var v any
	if err := json.Unmarshal(raw, &v); err != nil {
		return fmt.Errorf("invalid arguments: %w", err)
	}
	return check(schema, v, "")
}

func check(schema map[string]any, v any, path string) error {
	where := path
	if where == "" {
		where = "arguments"
	}
	if v == nil {
		return nil
	}
	switch schema["type"] {
	case "string":
		s, ok := v.(string)
		if !ok {
			return fmt.Errorf("%s: expected a string", where)
		}
		if values, ok := schema["enum"].([]string); ok {
			for _, e := range values {
				if e == s {
					return nil
				}
			}
			return fmt.Errorf("%s: expected one of %s", where, strings.Join(values, ", "))
		}
	case "boolean":
		if _, ok := v.(bool); !ok {
			return fmt.Errorf("%s: expected a boolean", where)
		}
	case "integer", "number":
		n, ok := v.(float64)
		if !ok {
			return fmt.Errorf("%s: expected a number", where)
		}
		if schema["type"] == "integer" && n != float64(int64(n)) {
			return fmt.Errorf("%s: expected an integer", where)
		}
	case "array":
		items, ok := v.([]any)
		if !ok {
			return fmt.Errorf("%s: expected an array", where)
		}
		if itemSchema, ok := schema["items"].(map[string]any); ok {
			for i, item := range items {
				if err := check(itemSchema, item, fmt.Sprintf("%s[%d]", path, i)); err != nil {
					return err
				}
			}
		}
	case "object":
		obj, ok := v.(map[string]any)
		if !ok {
			return fmt.Errorf("%s: expected an object", where)
		}
		if required, ok := schema["required"].([]string); ok {
			for _, r := range required {
				if _, present := obj[r]; !present {
					return fmt.Errorf("%s: is required", join(path, r))
				}
			}
		}
		if props, ok := schema["properties"].(map[string]any); ok {
			for k, pv := range obj {
				if ps, ok := props[k].(map[string]any); ok {
					if err := check(ps, pv, join(path, k)); err != nil {
						return err
					}
				}
			}
		}
	}
	return nil
}

func join(path, key string) string {
	if path == "" {
		return key
	}
	return path + "." + key
}
