import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type Session = {
  id: string
  title: string
  model: string
  isInitialized: boolean
  createdAt: string
  updatedAt: string
  version: number
}

type Message = {
  role: 'user' | 'assistant'
  content: string
}

type SessionDetails = {
  session: Session
  messages: Message[]
}

async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
  })

  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status})`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

function App() {
  const [sessions, setSessions] = useState<Session[]>([])
  const [selected, setSelected] = useState<Session | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [prompt, setPrompt] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refreshSessions = useCallback(async () => {
    const data = await request<Session[]>('/api/sessions')
    setSessions(data)
    return data
  }, [])

  useEffect(() => {
    refreshSessions().catch((reason: Error) => setError(reason.message))
  }, [refreshSessions])

  async function selectSession(session: Session) {
    setBusy(true)
    setError(null)
    try {
      const details = await request<SessionDetails>(`/api/sessions/${session.id}`)
      setSelected(details.session)
      setMessages(details.messages)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to load the session.')
    } finally {
      setBusy(false)
    }
  }

  async function createSession() {
    setBusy(true)
    setError(null)
    try {
      const session = await request<Session>('/api/sessions', {
        method: 'POST',
        body: JSON.stringify({ title: 'New session' }),
      })
      setSessions((current) => [session, ...current])
      setSelected(session)
      setMessages([])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to create a session.')
    } finally {
      setBusy(false)
    }
  }

  async function sendMessage(event: FormEvent) {
    event.preventDefault()
    const text = prompt.trim()
    if (!selected || !text || busy) {
      return
    }

    setPrompt('')
    setBusy(true)
    setError(null)
    setMessages((current) => [...current, { role: 'user', content: text }])

    try {
      const response = await request<{ message: Message }>(
        `/api/sessions/${selected.id}/messages`,
        {
          method: 'POST',
          body: JSON.stringify({ prompt: text }),
        },
      )
      setMessages((current) => [...current, response.message])
      const refreshed = await refreshSessions()
      setSelected(refreshed.find((session) => session.id === selected.id) ?? selected)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Copilot did not return a response.')
    } finally {
      setBusy(false)
    }
  }

  async function deleteSession() {
    if (!selected || busy) {
      return
    }

    setBusy(true)
    setError(null)
    try {
      await request<void>(`/api/sessions/${selected.id}`, { method: 'DELETE' })
      setSessions((current) => current.filter((session) => session.id !== selected.id))
      setSelected(null)
      setMessages([])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to delete the session.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">CP</div>
          <div>
            <strong>Session Lab</strong>
            <span>Copilot SDK + SessionFS</span>
          </div>
        </div>

        <button className="new-session" type="button" onClick={createSession} disabled={busy}>
          <span aria-hidden="true">+</span> New session
        </button>

        <nav aria-label="Sessions">
          <p className="nav-label">Persisted sessions</p>
          {sessions.length === 0 ? (
            <p className="empty-list">No sessions yet.</p>
          ) : (
            sessions.map((session) => (
              <button
                className={`session-item ${selected?.id === session.id ? 'active' : ''}`}
                type="button"
                key={session.id}
                onClick={() => selectSession(session)}
                disabled={busy}
              >
                <span>{session.title}</span>
                <small>{new Date(session.updatedAt).toLocaleString()}</small>
              </button>
            ))
          )}
        </nav>

        <div className="storage-card">
          <span className="status-dot" aria-hidden="true"></span>
          <div>
            <strong>SQLite connected</strong>
            <span>Application + agent state</span>
          </div>
        </div>
      </aside>

      <main className="chat">
        <header className="chat-header">
          <div>
            <h1>{selected?.title ?? 'Copilot Session Persistence'}</h1>
            <p>
              {selected
                ? `${selected.model} / ${selected.isInitialized ? 'resumable' : 'not initialized'}`
                : 'Create or select a session to start'}
            </p>
          </div>
          {selected && (
            <button className="delete-button" type="button" onClick={deleteSession} disabled={busy}>
              Delete
            </button>
          )}
        </header>

        <section className="messages" aria-live="polite">
          {!selected ? (
            <div className="hero">
              <span className="eyebrow">RESTART-SAFE CONVERSATIONS</span>
              <h2>Session state that outlives the Web process.</h2>
              <p>
                Conversation events, workspace files, checkpoints, and plans are persisted through
                a custom SessionFS provider backed by SQLite.
              </p>
              <button type="button" onClick={createSession} disabled={busy}>
                Create the first session
              </button>
            </div>
          ) : messages.length === 0 ? (
            <div className="first-message">
              <div className="spark">✦</div>
              <h2>What should we work on?</h2>
              <p>The first message initializes this Copilot session in SQLite.</p>
            </div>
          ) : (
            <div className="message-stack">
              {messages.map((message, index) => (
                <article className={`message ${message.role}`} key={`${message.role}-${index}`}>
                  <span className="avatar">{message.role === 'assistant' ? 'CP' : 'You'}</span>
                  <div>
                    <strong>{message.role === 'assistant' ? 'Copilot' : 'You'}</strong>
                    <p>{message.content}</p>
                  </div>
                </article>
              ))}
              {busy && <p className="thinking">Copilot is thinking...</p>}
            </div>
          )}
        </section>

        {error && <div className="error-banner">{error}</div>}

        <footer className="composer-area">
          <form className="composer" onSubmit={sendMessage}>
            <textarea
              value={prompt}
              onChange={(event) => setPrompt(event.target.value)}
              placeholder={selected ? 'Message Copilot...' : 'Select a session first'}
              disabled={!selected || busy}
              maxLength={16000}
              rows={2}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault()
                  event.currentTarget.form?.requestSubmit()
                }
              }}
            />
            <button type="submit" disabled={!selected || !prompt.trim() || busy}>
              Send
            </button>
          </form>
          <p className="diagnostic">
            {selected ? `Session ${selected.id}` : 'Persistence backend: SQLite'}
          </p>
        </footer>
      </main>
    </div>
  )
}

export default App
