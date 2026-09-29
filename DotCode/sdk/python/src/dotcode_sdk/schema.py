"""JSON Schema generation and argument binding for typed tool parameters.

Tool parameters are declared as a class, never as a hand-written schema, so a type checker (mypy, pyright) catches
typos in the handler. Supported parameter types:

* ``@dataclass`` classes (descriptions via ``Annotated[str, "City name"]`` or ``field(metadata={"description": ...})``)
* ``TypedDict`` classes
* pydantic ``BaseModel`` subclasses (if pydantic is installed; ``Field(description=...)`` works as usual)

Field types: ``str``, ``int``, ``float``, ``bool``, ``Enum``, ``Literal[...]``, ``Optional[T]``, ``list[T]``,
``dict[str, T]`` and nested classes of the kinds above.
"""

from __future__ import annotations

import collections.abc
import dataclasses
import enum
import sys
import types
import typing
from typing import Any, Dict, List, Tuple, Union, get_args, get_origin, get_type_hints

_NONE = type(None)
_UNION_TYPES = (Union, getattr(types, "UnionType", Union))
_SEQUENCES = (list, tuple, set, frozenset, collections.abc.Sequence, collections.abc.Set)
_MAPPINGS = (dict, collections.abc.Mapping)


class ToolArgumentError(ValueError):
    """Raised when the model's arguments do not match the declared parameter class."""


def _is_pydantic(tp: Any) -> bool:
    return isinstance(tp, type) and hasattr(tp, "model_json_schema") and hasattr(tp, "model_validate")


def _is_typeddict(tp: Any) -> bool:
    return isinstance(tp, type) and issubclass(tp, dict) and hasattr(tp, "__annotations__") and hasattr(tp, "__total__")


def _hints(tp: Any) -> Dict[str, Any]:
    return get_type_hints(tp, globalns=vars(sys.modules[tp.__module__]), include_extras=True)


def _split_annotated(tp: Any) -> Tuple[Any, str | None]:
    if get_origin(tp) is typing.Annotated:
        base, *meta = get_args(tp)
        description = next((m for m in meta if isinstance(m, str)), None)
        return base, description
    return tp, None


def _optional_inner(tp: Any) -> Any | None:
    """Returns T for Optional[T] / T | None, else None."""
    if get_origin(tp) in _UNION_TYPES:
        args = [a for a in get_args(tp) if a is not _NONE]
        if len(args) == 1 and len(get_args(tp)) == 2:
            return args[0]
    return None


def schema_for(tp: Any) -> Dict[str, Any]:
    """JSON Schema of a parameter class (or any supported field type)."""
    tp, description = _split_annotated(tp)
    schema = _schema(tp)
    if description:
        schema["description"] = description
    return schema


def _schema(tp: Any) -> Dict[str, Any]:
    inner = _optional_inner(tp)
    if inner is not None:
        return schema_for(inner)
    if tp is str:
        return {"type": "string"}
    if tp is bool:
        return {"type": "boolean"}
    if tp is int:
        return {"type": "integer"}
    if tp is float:
        return {"type": "number"}
    if tp is Any or tp is object:
        return {}
    if isinstance(tp, type) and issubclass(tp, enum.Enum):
        return {"type": "string" if all(isinstance(m.value, str) for m in tp) else "integer", "enum": [m.value for m in tp]}
    origin = get_origin(tp)
    if origin is typing.Literal:
        values = list(get_args(tp))
        kind = "string" if all(isinstance(v, str) for v in values) else "integer" if all(isinstance(v, int) for v in values) else None
        return {**({"type": kind} if kind else {}), "enum": values}
    if origin in _SEQUENCES:
        args = get_args(tp)
        return {"type": "array", "items": schema_for(args[0]) if args else {}}
    if origin in _MAPPINGS:
        args = get_args(tp)
        return {"type": "object", "additionalProperties": schema_for(args[1]) if len(args) == 2 else {}}
    if _is_pydantic(tp):
        schema: Dict[str, Any] = tp.model_json_schema()
        return schema
    if dataclasses.is_dataclass(tp) and isinstance(tp, type):
        hints = _hints(tp)
        properties: Dict[str, Any] = {}
        required: List[str] = []
        for f in dataclasses.fields(tp):
            if not f.init:
                continue
            prop = schema_for(hints[f.name])
            if "description" in f.metadata:
                prop["description"] = f.metadata["description"]
            has_default = f.default is not dataclasses.MISSING or f.default_factory is not dataclasses.MISSING
            if f.default is not dataclasses.MISSING and isinstance(f.default, (str, int, float, bool)):
                prop["default"] = f.default.value if isinstance(f.default, enum.Enum) else f.default
            if not has_default and _optional_inner(_split_annotated(hints[f.name])[0]) is None:
                required.append(f.name)
            properties[f.name] = prop
        return _object(properties, required)
    if _is_typeddict(tp):
        hints = _hints(tp)
        required_keys = getattr(tp, "__required_keys__", set(hints) if tp.__total__ else set())
        properties = {k: schema_for(v) for k, v in hints.items()}
        return _object(properties, [k for k in hints if k in required_keys])
    raise TypeError(f"Unsupported tool parameter type: {tp!r}")


def _object(properties: Dict[str, Any], required: List[str]) -> Dict[str, Any]:
    schema: Dict[str, Any] = {"type": "object", "properties": properties, "additionalProperties": False}
    if required:
        schema["required"] = required
    return schema


def bind(tp: Any, value: Any, path: str = "") -> Any:
    """Converts JSON arguments to an instance of ``tp``, validating types. Raises :class:`ToolArgumentError`."""
    tp, _ = _split_annotated(tp)
    where = path or "arguments"
    inner = _optional_inner(tp)
    if inner is not None:
        return None if value is None else bind(inner, value, path)
    if tp is Any or tp is object:
        return value
    if tp is str:
        if not isinstance(value, str):
            raise ToolArgumentError(f"{where}: expected a string")
        return value
    if tp is bool:
        if not isinstance(value, bool):
            raise ToolArgumentError(f"{where}: expected a boolean")
        return value
    if tp is int:
        if isinstance(value, bool) or not isinstance(value, int):
            if isinstance(value, float) and value.is_integer():
                return int(value)
            raise ToolArgumentError(f"{where}: expected an integer")
        return value
    if tp is float:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ToolArgumentError(f"{where}: expected a number")
        return float(value)
    if isinstance(tp, type) and issubclass(tp, enum.Enum):
        try:
            return tp(value)
        except ValueError:
            raise ToolArgumentError(f"{where}: expected one of {', '.join(str(m.value) for m in tp)}") from None
    origin = get_origin(tp)
    if origin is typing.Literal:
        if value not in get_args(tp):
            raise ToolArgumentError(f"{where}: expected one of {', '.join(map(str, get_args(tp)))}")
        return value
    if origin in _SEQUENCES:
        if not isinstance(value, list):
            raise ToolArgumentError(f"{where}: expected an array")
        args = get_args(tp)
        items = [bind(args[0], v, f"{path}[{i}]") if args else v for i, v in enumerate(value)]
        return tuple(items) if origin is tuple else set(items) if origin in (set, frozenset) else items
    if origin in _MAPPINGS:
        if not isinstance(value, dict):
            raise ToolArgumentError(f"{where}: expected an object")
        args = get_args(tp)
        return {k: bind(args[1], v, f"{path}.{k}" if path else k) for k, v in value.items()} if len(args) == 2 else dict(value)
    if _is_pydantic(tp):
        try:
            return tp.model_validate(value)
        except Exception as e:  # pydantic.ValidationError
            raise ToolArgumentError(str(e)) from None
    if not isinstance(value, dict):
        raise ToolArgumentError(f"{where}: expected an object")
    if dataclasses.is_dataclass(tp) and isinstance(tp, type):
        hints = _hints(tp)
        kwargs: Dict[str, Any] = {}
        for f in dataclasses.fields(tp):
            if not f.init:
                continue
            child = f"{path}.{f.name}" if path else f.name
            if f.name in value and value[f.name] is not None:
                kwargs[f.name] = bind(hints[f.name], value[f.name], child)
            elif f.default is dataclasses.MISSING and f.default_factory is dataclasses.MISSING:
                if _optional_inner(_split_annotated(hints[f.name])[0]) is None:
                    raise ToolArgumentError(f"{child}: is required")
                kwargs[f.name] = None
        return tp(**kwargs)
    if _is_typeddict(tp):
        hints = _hints(tp)
        required_keys = getattr(tp, "__required_keys__", set(hints) if tp.__total__ else set())
        out: Dict[str, Any] = {}
        for k, t in hints.items():
            child = f"{path}.{k}" if path else k
            if k in value:
                out[k] = bind(t, value[k], child)
            elif k in required_keys:
                raise ToolArgumentError(f"{child}: is required")
        return out
    raise TypeError(f"Unsupported tool parameter type: {tp!r}")


def to_jsonable(value: Any) -> Any:
    """Converts dataclasses, pydantic models and enums to JSON-compatible values."""
    if hasattr(value, "model_dump"):
        return value.model_dump(mode="json")
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {k: to_jsonable(v) for k, v in dataclasses.asdict(value).items()}
    if isinstance(value, enum.Enum):
        return value.value
    if isinstance(value, dict):
        return {k: to_jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [to_jsonable(v) for v in value]
    return value


__all__ = ["schema_for", "bind", "to_jsonable", "ToolArgumentError"]
