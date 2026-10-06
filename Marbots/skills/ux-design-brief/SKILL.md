---
name: ux-design-brief
description: Produce UX briefs: personas, user flows, information architecture and annotated wireframes.
version: 1.0.0
requires:
  tools: [write_file]
permissions:
  network: false
  shell: false
---
# UX design brief

1. **Problem & users** – who, context, top 3 jobs-to-be-done, pain points.
2. **Success metrics** – task success, time on task, conversion, satisfaction.
3. **User flow** – Mermaid flowchart of the primary path and key alternatives.
4. **Information architecture** – navigation tree.
5. **Wireframes** – low-fi HTML (grey boxes, real labels) or ASCII per key screen, with numbered annotations.
6. **Interaction & states** – empty, loading, error, success for each screen.
7. **Accessibility** – contrast, focus order, labels, touch targets ≥ 44px.
8. **Open questions** for research.

Write copy in plain language from the user's point of view ("Save changes", not "Submit").
Deliver `design/<feature>-ux-brief.md` and wireframes in `design/wireframes/`.
