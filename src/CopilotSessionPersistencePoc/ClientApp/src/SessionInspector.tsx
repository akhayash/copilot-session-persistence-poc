import { useCallback, useEffect, useRef, useState } from 'react'
import { apiRequest } from './api'

type SessionFsEntry = {
  path: string
  pathCategory: 'canonical-session-state' | 'host-shaped-virtual-key' | 'virtual'
  kind: 'file' | 'directory'
  sizeBytes: number
  birthTime: string
  modifiedTime: string
  version: number
}

type StorageEvidence = {
  persistenceMode: string
  applicationMetadataBackend: string
  sessionFsBackend: string
  sessionLockBackend: string
  artifactBackend: string
  backend: string
  storageLocation: string
  storageObjectExists: boolean
  storageSizeBytes: number
  hostSessionDirectory: string
  hostSessionDirectoryExists: boolean
  individualSessionFilesDetected: boolean
  conclusion: string
}

type DiagnosticsSnapshot = {
  sessionId: string
  nodeCount: number
  fileCount: number
  directoryCount: number
  contentBytes: number
  eventCount: number
  lastModifiedTime: string | null
  storage: StorageEvidence
  entries: SessionFsEntry[]
}

type EntryDetails = {
  entry: SessionFsEntry
  content: string | null
  contentTruncated: boolean
  originalCharacterCount: number
  storageObject: string
  sessionKey: string
  pathKey: string
}

type SessionInspectorProps = {
  sessionId: string
  open: boolean
  refreshKey: number
  onClose: () => void
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 / 1024).toFixed(1)} MB`
}

function pathDepth(path: string) {
  return Math.max(0, path.split('/').filter(Boolean).length - 1)
}

const pathGroups = [
  {
    category: 'canonical-session-state',
    title: 'Canonical SessionFS',
    description: 'Configured /session-state namespace. Every item below is a virtual node.',
  },
  {
    category: 'host-shaped-virtual-key',
    title: 'Host-shaped virtual keys',
    description:
      'Windows-shaped strings supplied by the SDK. They remain virtual keys and are not proof of disk files.',
  },
  {
    category: 'virtual',
    title: 'Other virtual keys',
    description: 'Additional paths handled by the custom SessionFS provider.',
  },
] as const

export default function SessionInspector({
  sessionId,
  open,
  refreshKey,
  onClose,
}: SessionInspectorProps) {
  const [snapshot, setSnapshot] = useState<DiagnosticsSnapshot | null>(null)
  const [selected, setSelected] = useState<EntryDetails | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const snapshotRequest = useRef<AbortController | null>(null)
  const entryRequest = useRef<AbortController | null>(null)

  const loadSnapshot = useCallback(async () => {
    snapshotRequest.current?.abort()
    entryRequest.current?.abort()
    const controller = new AbortController()
    snapshotRequest.current = controller
    setLoading(true)
    setError(null)
    setSelected(null)
    try {
      const data = await apiRequest<DiagnosticsSnapshot>(
        `/api/sessions/${sessionId}/diagnostics`,
        { signal: controller.signal },
      )
      if (controller.signal.aborted || data.sessionId !== sessionId) return
      setSnapshot(data)
    } catch (reason) {
      if (controller.signal.aborted) return
      setError(reason instanceof Error ? reason.message : 'Unable to inspect SessionFS.')
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [sessionId])

  useEffect(() => {
    snapshotRequest.current?.abort()
    entryRequest.current?.abort()
    setSnapshot(null)
    setSelected(null)
    if (open) {
      void loadSnapshot()
    }

    return () => {
      snapshotRequest.current?.abort()
      entryRequest.current?.abort()
    }
  }, [loadSnapshot, open, refreshKey, sessionId])

  async function selectEntry(entry: SessionFsEntry) {
    entryRequest.current?.abort()
    const controller = new AbortController()
    entryRequest.current = controller
    setLoading(true)
    setError(null)
    try {
      const details = await apiRequest<EntryDetails>(
        `/api/sessions/${sessionId}/diagnostics/entry?path=${encodeURIComponent(entry.path)}`,
        { signal: controller.signal },
      )
      if (controller.signal.aborted || details.sessionKey !== sessionId) return
      setSelected(details)
    } catch (reason) {
      if (controller.signal.aborted) return
      setError(reason instanceof Error ? reason.message : 'Unable to read the storage entry.')
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }

  if (!open) return null

  return (
    <aside className="inspector" aria-label="SessionFS storage inspector">
      <header className="inspector-header">
        <div>
          <span className="eyebrow">READ-ONLY DIAGNOSTICS</span>
          <h2>SessionFS Inspector</h2>
        </div>
        <div className="inspector-actions">
          <button type="button" onClick={() => void loadSnapshot()} disabled={loading}>
            Refresh
          </button>
          <button type="button" onClick={onClose} aria-label="Close inspector">
            ×
          </button>
        </div>
      </header>

      {error && <div className="inspector-error">{error}</div>}
      {!snapshot ? (
        <div className="inspector-loading">{loading ? 'Reading storage...' : 'No data.'}</div>
      ) : (
        <div className="inspector-body">
          <section
            className={`evidence-card ${
              snapshot.storage.individualSessionFilesDetected ? 'warning' : 'verified'
            }`}
          >
            <div className="evidence-title">
              <span className="evidence-icon">
                {snapshot.storage.individualSessionFilesDetected ? '!' : '✓'}
              </span>
              <div>
                <strong>
                  {snapshot.storage.individualSessionFilesDetected
                    ? 'Host files detected'
                    : 'External SessionFS verified'}
                </strong>
                <span>{snapshot.storage.backend}</span>
              </div>
            </div>
            <p>{snapshot.storage.conclusion}</p>
          </section>

          <section>
            <h3>Active persistence mode</h3>
            <dl className="evidence-list backend-map">
              <div>
                <dt>Mode</dt>
                <dd>{snapshot.storage.persistenceMode}</dd>
              </div>
              <div>
                <dt>Session list</dt>
                <dd>{snapshot.storage.applicationMetadataBackend}</dd>
              </div>
              <div>
                <dt>Conversation / SessionFS</dt>
                <dd>{snapshot.storage.sessionFsBackend}</dd>
              </div>
              <div>
                <dt>Session lock</dt>
                <dd>{snapshot.storage.sessionLockBackend}</dd>
              </div>
              <div>
                <dt>Artifacts</dt>
                <dd>{snapshot.storage.artifactBackend}</dd>
              </div>
            </dl>
          </section>

          <section>
            <h3>Persistence evidence</h3>
            <dl className="evidence-list">
              <div>
                <dt>Storage location</dt>
                <dd>{snapshot.storage.storageLocation}</dd>
              </div>
              <div>
                <dt>Storage object</dt>
                <dd>
                  {snapshot.storage.storageObjectExists ? 'Exists' : 'Missing'} ·{' '}
                  {formatBytes(snapshot.storage.storageSizeBytes)}
                </dd>
              </div>
              <div>
                <dt>Disk leak check</dt>
                <dd>
                  <strong className="not-storage-label">Not a storage location</strong>
                  Default CLI path tested only to prove absence:
                  <br />
                  {snapshot.storage.hostSessionDirectory}
                </dd>
              </div>
              <div>
                <dt>Disk check result</dt>
                <dd
                  className={
                    snapshot.storage.individualSessionFilesDetected
                      ? 'disk-result detected'
                      : 'disk-result absent'
                  }
                >
                  {snapshot.storage.individualSessionFilesDetected
                    ? 'Individual files detected'
                    : 'NOT PRESENT — content remains in the configured external store'}
                </dd>
              </div>
            </dl>
          </section>

          <section>
            <h3>SessionFS contents</h3>
            <div className="stats-grid">
              <div>
                <strong>{snapshot.nodeCount}</strong>
                <span>Rows</span>
              </div>
              <div>
                <strong>{snapshot.fileCount}</strong>
                <span>Files</span>
              </div>
              <div>
                <strong>{snapshot.directoryCount}</strong>
                <span>Directories</span>
              </div>
              <div>
                <strong>{formatBytes(snapshot.contentBytes)}</strong>
                <span>Content</span>
              </div>
              <div>
                <strong>{snapshot.eventCount}</strong>
                <span>Events</span>
              </div>
            </div>
          </section>

          <section>
            <h3>Virtual filesystem rows</h3>
            {snapshot.entries.length === 0 ? (
              <p className="inspector-empty">No SessionFS rows yet. Send the first message.</p>
            ) : (
              <div className="fs-groups">
                {pathGroups.map((group) => {
                  const entries = snapshot.entries.filter(
                    (entry) => entry.pathCategory === group.category,
                  )
                  if (entries.length === 0) return null

                  return (
                    <div className="fs-group" key={group.category}>
                      <div className="fs-group-header">
                        <div>
                          <strong>{group.title}</strong>
                          <span>{group.description}</span>
                        </div>
                        <span className="sqlite-row-badge">{entries.length} virtual nodes</span>
                      </div>
                      <div className="fs-tree">
                        {entries.map((entry) => (
                          <button
                            type="button"
                            key={entry.path}
                            className={selected?.entry.path === entry.path ? 'selected' : ''}
                            style={{ paddingLeft: `${12 + pathDepth(entry.path) * 14}px` }}
                            onClick={() => selectEntry(entry)}
                          >
                            <span className="entry-kind">
                              {entry.kind === 'file' ? 'F' : 'D'}
                            </span>
                            <span className="entry-path">{entry.path}</span>
                            {entry.kind === 'file' && (
                              <span className="entry-size">{formatBytes(entry.sizeBytes)}</span>
                            )}
                          </button>
                        ))}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </section>

          {selected && (
            <section className="entry-details">
              <h3>Selected storage entry</h3>
              <dl className="evidence-list">
                <div>
                  <dt>Storage object</dt>
                  <dd>{selected.storageObject}</dd>
                </div>
                <div>
                  <dt>Primary key</dt>
                  <dd>
                    session_id={selected.sessionKey}
                    <br />
                    path={selected.pathKey}
                  </dd>
                </div>
                <div>
                  <dt>Path origin</dt>
                  <dd>
                    {selected.entry.pathCategory === 'host-shaped-virtual-key'
                      ? 'Host-shaped virtual key — not resolved as a host path by this UI'
                      : selected.entry.pathCategory}
                  </dd>
                </div>
                <div>
                  <dt>Version / modified</dt>
                  <dd>
                    {selected.entry.version} ·{' '}
                    {new Date(selected.entry.modifiedTime).toLocaleString()}
                  </dd>
                </div>
              </dl>
              {selected.content !== null && (
                <>
                  <div className="content-label">
                    <span>Content preview</span>
                    <span>
                      {selected.originalCharacterCount.toLocaleString()} chars
                      {selected.contentTruncated ? ' · truncated' : ''}
                    </span>
                  </div>
                  <pre>{selected.content}</pre>
                  <p className="content-warning">
                    Prompt and agent state may be sensitive. Known token patterns are redacted.
                  </p>
                </>
              )}
            </section>
          )}
        </div>
      )}
    </aside>
  )
}
