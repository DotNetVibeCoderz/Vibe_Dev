---
name: docker-devops
description: Containerize apps and write CI/CD pipelines (Dockerfile, compose, GitHub Actions).
version: 1.0.0
requires:
  tools: [write_file, run_shell]
permissions:
  network: false
  shell: true
---
# Docker & CI/CD

## Dockerfile
- Multi-stage: build stage with SDK, runtime stage minimal (`-alpine`/`-chiseled` where possible).
- Pin base image tags; run as non-root (`USER app`); `HEALTHCHECK` for services.
- Copy dependency manifests first to maximize layer caching.

## docker-compose.yml
- Named volumes for data, env via `.env` (never commit real secrets), healthchecks + `depends_on: condition: service_healthy`.

## GitHub Actions
```yaml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      # setup toolchain, restore cache, build, test, upload artifacts
```
- Cache dependencies, fail fast, separate deploy job gated on `main` + environment approval.

## Verify
If Docker is available run `docker build .`; otherwise validate YAML syntax and explain how to run.
