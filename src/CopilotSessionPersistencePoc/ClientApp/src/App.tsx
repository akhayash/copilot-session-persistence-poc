import { useCallback, useEffect, useState } from 'react'
import type { ChangeEvent, FormEvent } from 'react'
import { apiRequest, uploadArtifact } from './api'
import SessionInspector from './SessionInspector'
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

type Health = {
  persistence: string
  pythonExecution: string
  presentationExecution: string
}

type Artifact = {
  artifactId: string
  fileName: string
  contentType: string
  sha256: string
  sizeBytes: number
}

function App() {
  const [sessions, setSessions] = useState<Session[]>([])
  const [selected, setSelected] = useState<Session | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [prompt, setPrompt] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [inspectorOpen, setInspectorOpen] = useState(false)
  const [inspectorRefreshKey, setInspectorRefreshKey] = useState(0)
  const [persistenceBackend, setPersistenceBackend] = useState('SQLite')
  const [pythonExecution, setPythonExecution] = useState('disabled')
  const [presentationExecution, setPresentationExecution] = useState('disabled')
  const [artifacts, setArtifacts] = useState<Artifact[]>([])

  const refreshSessions = useCallback(async () => {
    const data = await apiRequest<Session[]>('/api/sessions')
    setSessions(data)
    return data
  }, [])

  const refreshArtifacts = useCallback(async (sessionId: string, backend: string) => {
    if (backend !== 'AzureStorage') {
      setArtifacts([])
      return
    }

    const data = await apiRequest<Artifact[]>(
      `/api/sessions/${encodeURIComponent(sessionId)}/artifacts`,
    )
    setArtifacts(data)
  }, [])

  useEffect(() => {
    refreshSessions().catch((reason: Error) => setError(reason.message))
    fetch('/api/health')
      .then(async (response) => (await response.json()) as Health)
      .then((health) => {
        setPersistenceBackend(health.persistence)
        setPythonExecution(health.pythonExecution)
        setPresentationExecution(health.presentationExecution)
      })
      .catch(() => undefined)
  }, [refreshSessions])

  async function selectSession(session: Session) {
    setBusy(true)
    setError(null)
    try {
      const details = await apiRequest<SessionDetails>(`/api/sessions/${session.id}`)
      setSelected(details.session)
      setSessions((current) =>
        current.map((item) => (item.id === details.session.id ? details.session : item)),
      )
      setMessages(details.messages)
      await refreshArtifacts(details.session.id, persistenceBackend)
      setInspectorRefreshKey((current) => current + 1)
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
      const session = await apiRequest<Session>('/api/sessions', {
        method: 'POST',
        body: JSON.stringify({ title: 'New session' }),
      })
      setSessions((current) => [session, ...current])
      setSelected(session)
      setMessages([])
      setArtifacts([])
      setInspectorRefreshKey((current) => current + 1)
      await refreshArtifacts(session.id, persistenceBackend)
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
      const response = await apiRequest<{ message: Message }>(
        `/api/sessions/${selected.id}/messages`,
        {
          method: 'POST',
          body: JSON.stringify({ prompt: text }),
        },
      )
      setMessages((current) => [...current, response.message])
      const refreshed = await refreshSessions()
      setSelected(refreshed.find((session) => session.id === selected.id) ?? selected)
      setInspectorRefreshKey((current) => current + 1)
      await refreshArtifacts(selected.id, persistenceBackend)
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
      await apiRequest<void>(`/api/sessions/${selected.id}`, { method: 'DELETE' })
      setSessions((current) => current.filter((session) => session.id !== selected.id))
      setSelected(null)
      setMessages([])
      setArtifacts([])
      setInspectorOpen(false)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to delete the session.')
    } finally {
      setBusy(false)
    }

  }

  async function addArtifact(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!selected || !file || busy) {
      return
    }

    setBusy(true)
    setError(null)
    try {
      await uploadArtifact<Artifact>(selected.id, file)
      await refreshArtifacts(selected.id, persistenceBackend)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to upload the artifact.')
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
            <strong>{persistenceBackend} connected</strong>
            <span>
              {persistenceBackend === 'AzureStorage'
                ? 'Table metadata + Blob SessionFS'
                : 'SQLite metadata + SessionFS'}
            </span>
            {pythonExecution !== 'disabled' && <span>Python: {pythonExecution}</span>}
            {presentationExecution !== 'disabled' && (
              <span>PowerPoint: {presentationExecution}</span>
            )}
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
            <div className="header-actions">
              {persistenceBackend === 'AzureStorage' && (
                <label className="upload-button">
                  Upload data
                  <input type="file" onChange={addArtifact} disabled={busy} />
                </label>
              )}
              <button
                className="inspect-button"
                type="button"
                onClick={() => setInspectorOpen(true)}
              >
                Inspect storage
              </button>
              <button className="delete-button" type="button" onClick={deleteSession} disabled={busy}>
                Delete
              </button>
            </div>
          )}
        </header>

        <section className="messages" aria-live="polite">
          {!selected ? (
            <div className="hero">
              <span className="eyebrow">RESTART-SAFE CONVERSATIONS</span>
              <h2>Session state that outlives the Web process.</h2>
              <p>
                Conversation events, workspace files, checkpoints, and plans are persisted through
                a custom SessionFS provider backed by {persistenceBackend}.
              </p>
              <button type="button" onClick={createSession} disabled={busy}>
                Create the first session
              </button>
            </div>
          ) : messages.length === 0 ? (
            <div className="first-message">
              <div className="spark">✦</div>
              <h2>What should we work on?</h2>
              <p>The first message initializes this Copilot session in {persistenceBackend}.</p>
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

        {selected && persistenceBackend === 'AzureStorage' && artifacts.length > 0 && (
          <section className="artifacts" aria-label="Session artifacts">
            <strong>Artifacts</strong>
            <div>
              {artifacts.map((artifact) => (
                <a
                  key={`${artifact.artifactId}/${artifact.fileName}`}
                  href={`/api/sessions/${encodeURIComponent(selected.id)}/artifacts/${encodeURIComponent(artifact.artifactId)}/${encodeURIComponent(artifact.fileName)}`}
                  download={artifact.fileName}
                >
                  {artifact.fileName}
                  <small>{Math.ceil(artifact.sizeBytes / 1024)} KB</small>
                </a>
              ))}
            </div>
          </section>
        )}

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
            {selected ? `Session ${selected.id}` : `Persistence backend: ${persistenceBackend}`}
          </p>
        </footer>
      </main>
      {selected && (
        <SessionInspector
          sessionId={selected.id}
          open={inspectorOpen}
          refreshKey={inspectorRefreshKey}
          onClose={() => setInspectorOpen(false)}
        />
      )}
    </div>
  )
}

export default App
