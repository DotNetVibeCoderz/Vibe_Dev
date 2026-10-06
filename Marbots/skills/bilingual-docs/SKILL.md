---
name: bilingual-docs
description: Write documentation in both English and Bahasa Indonesia with consistent terminology.
version: 1.0.0
requires:
  tools: [write_file, read_file]
permissions:
  network: false
  shell: false
---
# Bilingual documentation (EN + ID)

- Produce paired files: `docs/en/<page>.md` and `docs/id/<page>.md` with identical structure and anchors.
- Indonesian follows EYD V, formal-friendly register ("Anda"), and keeps established tech terms in English when common
  (e.g. *deploy*, *repository*), italicized on first use if helpful.
- Maintain `docs/glossary.md` with EN ↔ ID terms; reuse it consistently.
- Code blocks, commands and UI labels stay identical across languages (translate only comments when helpful).
- Each page: purpose, prerequisites, steps (numbered), expected result, troubleshooting.
- Cross-link the language versions at the top: `[English](../en/page.md) · [Bahasa Indonesia](../id/page.md)`.
