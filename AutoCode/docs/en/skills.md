# Skills

> Auto Code — Gravicode Studios, led by Kang Fadhil

A skill is a workflow you have written down once so it runs the same way every time. It becomes a
slash command, and the agent can also select it on its own when a task matches.

## Anatomy

```
.autocode/skills/
└── release/
    ├── SKILL.md          # required
    └── checklist.md      # anything else the skill references
```

`SKILL.md` is front matter plus markdown instructions:

```markdown
---
name: release
description: Cut a release — version bump, changelog, tag, publish
allowed-tools: [Read, Edit, Bash, Grep]
---

Follow these steps in order. Stop and report if any step fails.

1. Confirm the working tree is clean (`git status --porcelain`).
2. Run the full test suite. Do not continue if anything fails.
3. Bump `VersionPrefix` in `Directory.Build.props` — patch unless told otherwise.
4. Add a `CHANGELOG.md` entry from the commits since the last tag.
5. Commit as `release: v<version>`, then tag `v<version>`.
6. Report the version and the changelog entry. Do not push.
```

## Front matter

| Field | Purpose |
| --- | --- |
| `name` | Invocation name. Defaults to the directory name. |
| `description` | **When to use this skill.** This is what the agent reads to decide. |
| `allowed-tools` | Restrict the skill to these tools. Optional. |
| `model` | Run this workflow on a different model. Optional. |
| `templated` | Enable Semantic Kernel prompt templating. Optional. |

The `description` does the real work. Write it as a trigger, not a title:

- Weak: *"Release skill"*
- Strong: *"Cut a release — use when asked to ship, tag, or publish a new version"*

## Where skills are found

Later locations override earlier ones by name:

1. `~/.autocode/skills/` — yours, every project
2. `<workspace>/.autocode/skills/` — the project's
3. `<workspace>/.claude/skills/` — Claude Code layout, read as-is
4. Anything in `skillDirectories`
5. Plugin skills

## Using one

```
› /release
› /release patch version only
```

Anything after the command name is passed as arguments and appended under an `## Arguments` heading.

Skills also appear in the system prompt with their descriptions, so the agent can invoke one itself
when the task matches — which is the point of writing a good description.

List what is installed:

```
› /skills
```

## Templating

Set `templated: true` to use Semantic Kernel's prompt template syntax:

```markdown
---
name: review-pr
description: Review a pull request by number
templated: true
---

Review pull request #{{$arguments}}.

Fetch it with `gh pr view {{$arguments}} --json title,body,files`, read every changed file,
and report defects with a concrete failure scenario each.
```

Both `{{$arguments}}` and `{{$input}}` are bound to whatever followed the command.

Without `templated: true` the body is passed through literally, which is what you want most of the
time — braces in code samples then stay braces.

## Writing skills that work

**Be specific about order and stopping conditions.** "Run the tests" is weaker than "Run the tests;
do not continue if anything fails."

**Encode the things people forget.** The value of a skill is not the happy path — the agent can
infer that. It is the step your team keeps missing.

**Say what not to do.** "Do not push" and "do not amend an existing commit" prevent more damage than
any positive instruction.

**Keep it to one workflow.** A skill that does three unrelated things will be selected for the wrong
one.

## Example: a debugging workflow

```markdown
---
name: debug
description: Investigate a failing test or a bug report methodically
allowed-tools: [Read, Grep, Glob, Bash, Edit]
---

Work through this in order. Do not skip to a fix.

1. Reproduce it. Run the failing test or the reported scenario, and paste the actual output.
2. Read the code in the stack trace — the actual code, not what you assume it says.
3. Form one hypothesis and state it explicitly.
4. Test the hypothesis with the smallest possible change or a targeted print.
5. Only once confirmed, write the fix.
6. Re-run the failing case and then the full suite.

Report: the root cause, the fix, and the evidence that it works.
If steps 3 and 4 disproved your hypothesis, say so and start again from 2 rather than guessing.
```
