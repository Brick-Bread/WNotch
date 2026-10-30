// Notch plugin for opencode 2. Installed by Notch (Settings > "Show what my coding agents are doing");
// untick the option and Notch removes this file again. Do not edit: Notch rewrites it when it updates.
//
// It tells the Notch app when opencode starts working, needs your input, or is done, by writing
// one line to a Windows named pipe that only Notch's own user can read. When opencode asks for a
// permission it shows the prompt in the notch too and takes the answer you give there; opencode's own
// prompt stays, and whichever you answer first counts. It reads event names, session ids, folders,
// what a permission prompt asks, the token use and cost of each step, and (inside OpenChamber) session
// titles, and it sends nothing over the network.
//
// notch-opencode-plugin 2

import net from "node:net"

const SHARED_PIPES = ["notch-agent-shared", "notch-agent-shared-debug"]

// Notch understands Claude Code's hook event names, so opencode's events are reported as them.
const WORKING = "UserPromptSubmit"
const NEEDS_INPUT = "Notification"
const DONE = "Stop"
const INTERRUPTED = "SessionEnd"
const PERMISSION = "PermissionRequest"
const PERMISSION_REPLIED = "PermissionReplied"
const USAGE = "Usage"

// OpenChamber runs its own opencode server and says so in the environment it gives it.
const HOST = process.env.OPENCHAMBER_INJECTED_ENV || process.env.OPENCHAMBER_AGENT_TOOL_URL || process.env.OPENCHAMBER_RUNTIME
  ? "openchamber"
  : undefined

function pipes() {
  // A terminal Notch started names its own pipe; anything else goes to the pipe every Notch listens on.
  const own = process.env.NOTCH_PIPE && process.env.NOTCH_SESSION
  return { names: own ? [process.env.NOTCH_PIPE] : SHARED_PIPES, session: own ? process.env.NOTCH_SESSION : "" }
}

function line(fields, reply) {
  const { session } = pipes()
  const payload = JSON.stringify({ agent: "opencode", host: HOST, ...fields })
  return JSON.stringify({ session, payload, pid: process.pid, ...(reply ? { reply: true } : {}) }) + "\n"
}

function send(fields) {
  try {
    const text = line(fields, false)
    for (const pipe of pipes().names) {
      const socket = net.connect("\\\\.\\pipe\\" + pipe)
      socket.on("error", () => socket.destroy())
      socket.setTimeout(1000, () => socket.destroy())
      socket.on("connect", () => socket.end(text))
    }
  } catch {
    // Notch is not running, or this is not Windows. opencode carries on regardless.
  }
}

// Sends a prompt and waits for Notch's answer: the first one that is not empty, from any Notch listening.
function ask(fields) {
  return new Promise((resolve) => {
    let open = 0
    let answered = false
    const sockets = []
    const done = (answer) => {
      if (answered) return
      if (answer === undefined && --open > 0) return
      answered = true
      for (const socket of sockets) socket.destroy()
      resolve(answer)
    }

    let text
    try {
      text = line(fields, true)
    } catch {
      return resolve(undefined)
    }

    for (const pipe of pipes().names) {
      open++
      const socket = net.connect("\\\\.\\pipe\\" + pipe)
      sockets.push(socket)
      let buffer = ""
      let finished = false
      const end = (answer) => {
        if (finished) return
        finished = true
        done(answer)
      }
      socket.setEncoding("utf8")
      socket.on("error", () => end(undefined))
      socket.on("close", () => end(undefined))
      socket.on("connect", () => socket.write(text))
      socket.on("data", (chunk) => {
        buffer += chunk
        const newline = buffer.indexOf("\n")
        if (newline < 0) return
        const reply = buffer.slice(0, newline).trim()
        try {
          end(reply ? JSON.parse(reply) : undefined)
        } catch {
          end(undefined)
        }
      })
    }
  })
}

// Which sessions are working, per folder, so a sub-session finishing does not report the whole
// project as done while the main session still works.
function tracker(context) {
  const busy = new Map()
  const titles = new Map()

  // Inside OpenChamber each session is listed in the notch, under its title.
  const title = async (id) => {
    if (!HOST || !id || id === "?") return undefined
    if (titles.has(id)) return titles.get(id)
    try {
      const session = await context.session.get({ sessionID: id })
      titles.set(id, session?.title)
      return session?.title
    } catch {
      return undefined
    }
  }
  const report = async (eventName, directory, id, extra) =>
    send({ hook_event_name: eventName, cwd: directory, sessionID: id, title: await title(id), ...extra })

  const start = (directory, id) => {
    const sessions = busy.get(directory) ?? new Set()
    busy.set(directory, sessions)
    sessions.add(id)
    return report(WORKING, directory, id)
  }
  const finish = (directory, id, outcome) => {
    const sessions = busy.get(directory)
    sessions?.delete(id)
    if (!sessions || sessions.size === 0) {
      busy.delete(directory)
      return report(outcome, directory, id)
    }
  }

  const permission = async (directory, data) => {
    await report(NEEDS_INPUT, directory, data.sessionID)
    const answer = await ask({
      hook_event_name: PERMISSION,
      cwd: directory,
      sessionID: data.sessionID,
      title: await title(data.sessionID),
      requestID: data.id,
      action: data.action,
      resources: data.resources,
      save: data.save,
      metadata: data.metadata,
      message: data.message,
    })
    if (answer?.decision === "once" || answer?.decision === "always" || answer?.decision === "reject") {
      try {
        await context.permission.reply({ sessionID: data.sessionID, requestID: data.id, decision: answer.decision })
      } catch {
        // Answered in opencode first; nothing left to do.
      }
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

      // A permission prompt waits for the user, in opencode and in the notch; answering it resumes the work.
      case "permission.asked":
        return permission(directory, data)
      case "permission.replied":
        return report(PERMISSION_REPLIED, directory, data.sessionID, { requestID: data.requestID })

      // What each step cost, for the usage card.
      case "session.step.ended": {
        const tokens = data.tokens ?? {}
        return send({
          hook_event_name: USAGE,
          cwd: directory,
          sessionID: data.sessionID,
          cost: typeof data.cost === "number" ? data.cost : 0,
          input: tokens.input ?? 0,
          output: tokens.output ?? 0,
          reasoning: tokens.reasoning ?? 0,
          cache_read: tokens.cache?.read ?? 0,
          cache_write: tokens.cache?.write ?? 0,
        })
      }
    }
  }
}

export default {
  id: "notch",
  async setup(context) {
    const handle = tracker(context)
    const stop = new AbortController()
    void (async () => {
      try {
        for await (const event of context.event.subscribe({ signal: stop.signal })) {
          void Promise.resolve(handle(event, context.location?.directory ?? process.cwd())).catch(() => {})
        }
      } catch {
        // The stream ended with the plugin; nothing to report.
      }
    })()
    return () => stop.abort()
  },
}
