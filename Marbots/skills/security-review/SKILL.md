---
name: security-review
description: Defensive security review of code and configuration (OWASP Top 10, secrets, dependencies).
version: 1.0.0
requires:
  tools: [read_file, grep, list_files]
permissions:
  network: false
  shell: false
---
# Security review (defensive only)

Search systematically with `grep`:
- Secrets: `(?i)(api[_-]?key|secret|password|token)\s*[:=]`, private key headers.
- Injection: string-built SQL, `subprocess(..., shell=True)`, `Process.Start` with user input, `eval`, `innerHTML`.
- Path traversal: user input joined to file paths without normalization/allow-listing.
- AuthN/Z: endpoints without auth attributes/middleware; IDOR (ids from request used without ownership check).
- Crypto: MD5/SHA1 for passwords, hard-coded IVs, disabled TLS validation.
- SSRF: server-side fetching of user-supplied URLs.
- Deserialization of untrusted data with type info.
- Dependencies: note outdated or abandoned packages from manifests.

Rate each finding: Critical / High / Medium / Low, with likelihood and impact.

## Output
| Severity | Location | Issue | Impact | Fix |
|---|---|---|---|---|

Never perform exploitation or destructive actions; describe the class of issue and the fix.
