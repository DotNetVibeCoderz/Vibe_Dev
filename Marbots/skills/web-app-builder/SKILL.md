---
name: web-app-builder
description: Build small, polished static web apps (HTML/CSS/JS) that run by opening index.html.
version: 1.0.0
requires:
  tools: [write_file, read_file, run_shell]
permissions:
  network: false
  shell: false
---
# Web app builder

Goal: a self-contained app in a folder (e.g. `apps/<name>/index.html`, `styles.css`, `app.js`) that works offline by opening `index.html`.

## Steps
1. Write a 5-line brief: purpose, primary user, main screen, data model, persistence (localStorage?).
2. Design tokens first: 4-6 colors as CSS custom properties, one or two typefaces (system stack is fine), spacing scale.
   Support dark mode with `@media (prefers-color-scheme: dark)`.
3. Semantic HTML (`header`, `main`, `nav`, `button`, `label`), responsive layout with CSS grid/flex, visible focus states.
4. Vanilla JS modules; no build step. Keep state in one object; render functions update the DOM.
5. Persist with `localStorage` wrapped in try/catch.
6. Self-check: open the file mentally from top to bottom; look for typos in ids/classes, unclosed tags, console errors.
   If Python is available: `python -m http.server` is optional; static files must work without it.
7. Deliver the folder path and a short feature list.

## Quality bar
- Mobile-first, works at 360px width.
- Empty states and error messages that tell the user what to do.
- No external CDNs unless asked.
