# Multi-tenant mode, databases and sign-in

[English](../en/multi-tenant.md) · [Bahasa Indonesia](../id/multi-tenant.md)

One Marbots server can serve several organisations (tenants). Each tenant gets its own bots, chats, tasks, memory,
skills, MCP servers, secrets, schedules, channels, computers and workspace folder. Nothing is shared between tenants
except the server process and, if you choose, the database server.

## Databases

| Provider | `Marbots:Database:Provider` | Good for |
|---|---|---|
| SQLite (default) | `sqlite` | One machine, local-first. In multi-tenant mode every tenant gets its own file. |
| PostgreSQL | `postgresql` | Shared server database, many tenants |
| SQL Server / Azure SQL | `sqlserver` | Shared server database in Microsoft environments |
| MySQL / MariaDB | `mysql` | Shared server database |

```json
"Marbots": {
  "Database": { "Provider": "sqlserver", "ConnectionString": "Server=sql01;Database=marbots;User ID=marbots;Password=…;TrustServerCertificate=true" }
}
```

- Every row carries a `tenant` column, and every query filters on it. Server databases use tables named `mb_*`.
- Tables and indexes are created on start-up. SQLite files from older versions are migrated automatically, with no
  data lost.
- Memory search is hybrid on every provider. It combines keyword ranking (SQLite FTS5 BM25, or BM25 in process on the
  others) with vector similarity, fused by reciprocal rank.
- The same storage conformance tests run against all four databases in CI (`MARBOTS_TEST_POSTGRES`,
  `MARBOTS_TEST_SQLSERVER`, `MARBOTS_TEST_MYSQL`).

### Memory embeddings

`Marbots:EmbeddingModel` is `hash` (default) or `provider/model`:

- `hash`: offline character n-gram hashing. It needs no network and matches word forms (`invoices` → `invoicing`).
- `provider/model`, for example `azure/text-embedding-3-small`: any OpenAI-compatible `/embeddings` endpoint, which
  gives real semantic recall.

## Turning on multi-tenant mode

```json
"Marbots": {
  "MultiTenant": true,
  "ApiKey": "<platform admin key>",
  "Database": { "Provider": "postgresql", "ConnectionString": "Host=db;Database=marbots;Username=marbots;Password=…" }
}
```

The `default` tenant always exists. A platform admin creates other tenants and their first owner key:

```bash
export MARBOTS_API_KEY=<platform admin key>
marbots tenants create acme --name "Acme Corp"
marbots tenants key acme owner --role Owner      # prints mbk_acme_… once
```

Then the tenant's owner works with their own key. It is bound to the tenant, so no extra settings are needed:

```bash
export MARBOTS_API_KEY=mbk_acme_…
marbots whoami                                    # acme · Owner
marbots keys create ci --role Operator
marbots members add ana@acme.example --role Admin
```

Each tenant runs in its own runtime: its own engine, scheduler, channel pollers, MCP processes and host connections,
started when the server starts. Disabling a tenant (`marbots tenants disable acme`) stops that runtime and locks out its
keys.

### Choosing the tenant in a request

| How | Example | Used for |
|---|---|---|
| Tenant key | `X-Api-Key: mbk_acme_…` | Apps, SDKs, CLI |
| Path prefix | `https://marbots.example/t/acme/api/v1/…` | Webhooks, channel inbound URLs, agent hosts, SDK base URLs |
| Header | `X-Marbots-Tenant: acme` | Platform key or OIDC token acting in a tenant |

Agent hosts of a tenant get an enrollment command that already points at `/t/<tenant>`.

## Roles

| Role | Can |
|---|---|
| Viewer | Read chats, tasks, events, usage; see Chat, Office, Tasks, Dashboard |
| Operator | Viewer + chat, run and cancel tasks, answer approvals, write memory, register push devices |
| Admin | Operator + bots, templates, skills, MCP, channels, computers, schedules, models, settings; see keys and members |
| Owner | Admin + create and revoke the tenant's API keys and members |
| Platform admin | Every tenant: create, disable, enable, and create keys for any tenant |

The API enforces roles on every route, and the web UI hides and blocks pages a role cannot use.

## Signing in

**API keys.** A global key (`Marbots:ApiKey`) gives platform-admin rights. Tenant keys (`mbk_…`) are stored as SHA-256
hashes and are shown once, when they are created.

**Web UI.** In multi-tenant mode the UI asks for a key at `/login`. A single-tenant server without a key stays an open
local console, as before. The tenant switcher at the top of the side rail lists the tenants you may use.

**OIDC single sign-on** (Microsoft Entra ID, Google, Keycloak, Auth0, …):

```json
"Auth": {
  "Mode": "oidc",
  "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
  "ClientId": "<app id>",
  "ClientSecret": "OIDC_CLIENT_SECRET",
  "TenantClaim": "tenant",
  "RoleClaim": "roles",
  "PlatformAdmins": ["it-admin@acme.example"]
}
```

- `ClientSecret` names a secret (environment variable or `Marbots:Secrets`); it is not the value itself.
- The UI signs in with the authorization-code flow (PKCE). The API accepts the same IdP's access tokens as
  `Authorization: Bearer …`.
- A user's tenant and role come from their **membership** (Access page, or `marbots members add`). Without one, they
  come from the `tenant` and `roles` claims, and a tenant claim alone gives Viewer. Platform admins are owners everywhere.
- `JwtSigningKey` (HS256, at least 32 characters) accepts tokens from your own gateway when no IdP is involved.

## The Access page

**Access** in the side rail (Admins can view it; Owners can make changes) shows the tenant's API keys (prefix, role,
last used, revoke), the members for single sign-on, and, for platform admins, every tenant with its runtime state.

![Access page](../images/access.png)

---
*Marbots: Created by Gravicode Studios, led by Kang Fadhil.*
