# Desktop and mobile apps

[English](../en/apps.md) · [Bahasa Indonesia](../id/apps.md)

Both apps connect to a Marbots server through the .NET SDK and follow its live event stream. The web app, CLI, desktop
and phone always show the same state.

## Desktop (Avalonia + Three.Net): the 3D office

```bash
dotnet run --project src/Marbots.Desktop                  # connects to MARBOTS_URL (default http://localhost:5170)
dotnet run --project src/Marbots.Desktop -- --start-local # starts a local server when none answers
```

![3D office](../images/desktop-office.png)

The office is the same floor plan as the web Office view, rendered in 3D:

- **Back wall:** a research desk (web), a library (files and skills), a workshop (shell) and a tool room (MCP).
- **Middle:** a row of desks.
- **Front:** a meeting room (delegation), the manager's office and the approval desk.

Every bot is a rigged robot with its own colour ring and a status lamp. Events move the robots:

| Event | What the robot does |
|---|---|
| `ToolCallStarted` | walks along the aisles to the matching station and works there (`typing`; `talk` in the meeting room) |
| `AgentThinkingStarted` | goes back to its desk, `thinking` |
| streaming text | back to its desk, `typing` |
| `ApprovalRequested` | walks to the approval desk and `wave`s until someone decides |
| `TaskDelegated` | a beam from the manager to the bot for a few seconds |
| task finished | back to its desk, `idle` |

Name tags follow the robots. Camera presets are Overview, Desks, Stations and Front rooms, plus auto orbit. The other
pages are **Chat** (with streaming replies), **Approvals** and **Server** (connect, or start a local server).

![Desktop chat](../images/desktop-chat.png) ![Desktop approvals](../images/desktop-approvals.png)

### How the office assets were made

| Asset | Tool |
|---|---|
| Robot, desks, chairs, executive desk, plant, sofa, whiteboard, coffee station | **Rodin** (Hyper3D) text-to-3D, through the Rodin MCP server's model |
| Wall art, floor texture, app icon | **Nano Banana 2** |
| Rigging and animation clips of the robot (`idle`, `walk`, `typing`, `thinking`, `wave`, `talk`); decimation and texture downsizing of every prop | **Blender** through the Blender MCP server |

The robot gets an 11-bone armature with weights computed from its body regions, which works more reliably than
automatic weights on generated meshes. Each clip is an NLA track exported as a glTF animation (700 KB). Props were cut
to 6–10k polygons with 1024 px textures, about 4 MB in total.

![Robot clips](../images/office-robot-clips.png)
![Props](../images/office-props.png)

The office logic (`OfficeDirector`: stations, slots, aisle routes, activities) is plain C# and unit-tested without a GPU.

## Mobile (.NET MAUI Blazor Hybrid)

```bash
dotnet build src/Marbots.Mobile -f net10.0-android     # Android (emulator reaches the PC at http://10.0.2.2:5170)
dotnet build src/Marbots.Mobile -f net10.0-windows10.0.19041.0
```

![Mobile app](../images/mobile-app.png)

The app has four tabs:

- **Chat:** the team list with live activity; conversations with Markdown and streaming replies.
- **Approvals:** approve, approve for the thread, or reject.
- **Activity:** the live feed.
- **Settings:** server URL and API key (kept in the device's secure storage), and notifications.

The phone notifies you when a bot is waiting for approval, and when a task you started from the phone finishes or
fails. Notifications use Android notification channels, `UNUserNotificationCenter` on iOS and Mac, and app
notifications on Windows.

For a phone on the same network, start the server with `--urls http://0.0.0.0:5170` and enter
`http://<pc-address>:5170`. Use HTTPS (a reverse proxy) outside your LAN.

The mobile project is not part of `Marbots.slnx`, because CI on Linux has no MAUI workloads. Build it with the
commands above.

---
*Marbots: Created by Gravicode Studios, led by Kang Fadhil.*
