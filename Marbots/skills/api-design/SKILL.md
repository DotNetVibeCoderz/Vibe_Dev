---
name: api-design
description: Design clean REST/HTTP APIs: resources, versioning, errors, pagination and OpenAPI contracts.
version: 1.0.0
requires:
  tools: [write_file]
permissions:
  network: false
  shell: false
---
# API design

- Resources are nouns, plural: `/api/v1/orders/{id}`. Version from day one.
- Methods: GET (safe), POST (create/action), PUT (replace), PATCH (partial), DELETE.
- Status codes: 200/201/204, 400 validation, 401/403 auth, 404, 409 conflict, 422 semantic, 429, 5xx.
- Errors use RFC 9457 Problem Details: `{ "type", "title", "status", "detail", "errors" }`.
- Pagination: `?limit=&cursor=` returning `{ items, nextCursor }`.
- Idempotency: accept `Idempotency-Key` on POSTs that create side effects.
- Timestamps ISO-8601 UTC; ids opaque strings.

## Deliverables
1. Resource list with fields and types.
2. Endpoint table: method, path, purpose, request, response, errors.
3. `openapi.yaml` (OpenAPI 3.1) in the workspace.
4. Example requests with curl.
