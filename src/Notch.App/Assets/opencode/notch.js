// Notch plugin for opencode 2. Installed by Notch (Settings > "Show what my coding agents are doing");
// untick the option and Notch removes this file again. Do not edit: Notch rewrites it when it updates.
//
// It tells the Notch app when opencode starts working, needs your input, or is done, by writing
// one line to a Windows named pipe that only Notch's own user can read. It reads nothing from
// opencode but event names, session ids and folders, and it sends nothing over the network.
//
// notch-opencode-plugin 1

import net from "node:net"

const SHARED_PIPES = ["notch-agent-shared", "notch-agent-shared-debug"]

// Notch understands Claude Code's hook event names, so opencode's events are reported as them.
const WORKING = "UserPromptSubmit"
const RESUMED = "PostToolUse"
const NEEDS_INPUT = "Notification"
const DONE = "Stop"
const INTERRUPTED = "SessionEnd"

function send(eventName, directory) {
  try {
    // A terminal Notch started names its own pipe; anything else goes to the pipe every Notch listens on.
    const own = process.env.NOTCH_PIPE && process.env.NOTCH_SESSION
    const pipes = own ? [process.env.NOTCH_PIPE] : SHARED_PIPES
    const payload = JSON.stringify({ hook_event_name: eventName, cwd: directory, agent: "opencode" })
    const line = JSON.stringify({ session: own ? process.env.NOTCH_SESSION : "", payload, pid: process.pid }) + "\n"
    for (const pipe of pipes) {
      const socket = net.connect("\\\\.\\pipe\\" + pipe)
      socket.on("error", () => socket.destroy())
      socket.setTimeout(1000, () => socket.destroy())
      socket.on("connect", () => socket.end(line))
    }
  } catch {
    // Notch is not running, or this is not Windows. opencode carries on regardless.
  }
}

// Which sessions are working, per folder, so a sub-session finishing does not report the whole
// project as done while the main session still works.
function tracker() {
  const busy = new Map()

  const start = (directory, id) => {
    const sessions = busy.get(directory) ?? new Set()
    busy.set(directory, sessions)
    sessions.add(id)
    send(WORKING, directory)
  }
  const finish = (directory, id, report) => {
    const sessions = busy.get(directory)
    sessions?.delete(id)
    if (!sessions || sessions.size === 0) {
      busy.delete(directory)
      send(report, directory)
    }
  }

  return (event, fallbackDirectory) => {
    const data = event?.data ?? {}
    const id = data.sessionID ?? "?"
    const directory = event?.location?.directory ?? fallbackDirectory

    switch (event?.type) {
      case "session.execution.started":
        return start(directory, id)
      case "session.execution.succeeded":
      case "session.execution.failed":
        return finish(directory, id, DONE)
      case "session.execution.interrupted":
        return finish(directory, id, INTERRUPTED)

      // A permission prompt waits for the user; answering it resumes the work.
      case "permission.asked":
        return send(NEEDS_INPUT, directory)
      case "permission.replied":
      case "permission.rejected":
        return send(RESUMED, directory)
    }
  }
}

export default {
  id: "notch",
  async setup(context) {
    const handle = tracker()
    const stop = new AbortController()
    void (async () => {
      try {
        for await (const event of context.event.subscribe({ signal: stop.signal })) {
          handle(event, context.location?.directory ?? process.cwd())
        }
      } catch {
        // The stream ended with the plugin; nothing to report.
      }
    })()
    return () => stop.abort()
  },
}
