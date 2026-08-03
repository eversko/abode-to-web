import { Download, Pause, Play, Search, Wifi, WifiOff } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useApi } from '../api/context'
import type { LogEntry, LogSeverity, LogSource } from '../api/schema'
import { formatDateTime, sentenceCase } from '../lib/format'

const severities: LogSeverity[] = ['trace', 'debug', 'information', 'warning', 'error', 'critical']
const sources: LogSource[] = ['runtime', 'build', 'system', 'audit', 'cloudflare']

type LogViewerProps = {
  projectId?: string
  initialSource?: LogSource
  availableSources?: LogSource[]
  title?: string
}

export function LogViewer({ projectId, initialSource = 'runtime', availableSources = sources, title = 'Project logs' }: LogViewerProps) {
  const api = useApi()
  const [entries, setEntries] = useState<LogEntry[]>([])
  const [source, setSource] = useState<LogSource>(initialSource)
  const [minimumSeverity, setMinimumSeverity] = useState<LogSeverity>('information')
  const [search, setSearch] = useState('')
  const [autoScroll, setAutoScroll] = useState(true)
  const [connection, setConnection] = useState<'connecting' | 'live' | 'interrupted'>('connecting')
  const [error, setError] = useState<string>()
  const listRef = useRef<HTMLDivElement>(null)

  const streamOptions = useMemo(() => ({ sources: [source], minimumSeverity, search: search.trim() || undefined }), [minimumSeverity, search, source])

  useEffect(() => {
    setConnection('connecting')
    setError(undefined)
    const dispose = api.streamLogs(projectId, streamOptions, {
      onOpen: () => setConnection('live'),
      onMessage: (entry) => setEntries((current) => [...current, entry].slice(-1_000)),
      onError: (reason) => {
        setConnection('interrupted')
        setError(reason.message)
      },
    })
    return dispose
  }, [api, projectId, streamOptions])

  useEffect(() => {
    if (autoScroll) listRef.current?.scrollTo({ top: listRef.current.scrollHeight, behavior: 'smooth' })
  }, [autoScroll, entries])

  const minimumSeverityIndex = severities.indexOf(minimumSeverity)
  const visibleEntries = entries.filter((entry) => entry.source === source
    && severities.indexOf(entry.severity) >= minimumSeverityIndex
    && (!search || entry.message.toLowerCase().includes(search.toLowerCase())))

  return (
    <section className="logs-panel" aria-labelledby="logs-heading">
      <div className="logs-toolbar">
        <div><span className="eyebrow">Live stream</span><h3 id="logs-heading">{title}</h3></div>
        <span className={`stream-state stream-state--${connection}`} role="status">
          {connection === 'interrupted' ? <WifiOff aria-hidden="true" size={14} /> : <Wifi aria-hidden="true" size={14} />}
          {connection === 'live' ? 'Live' : connection === 'connecting' ? 'Connecting' : 'Interrupted'}
        </span>
      </div>
      <div className="log-controls">
        <label className="search-field"><Search aria-hidden="true" size={16} /><span className="sr-only">Search logs</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search this stream" /></label>
        {availableSources.length > 1 && <label className="compact-field"><span>Source</span><select value={source} onChange={(event) => { setSource(event.target.value as LogSource); setEntries([]) }}>{availableSources.map((value) => <option key={value} value={value}>{sentenceCase(value)}</option>)}</select></label>}
        <label className="compact-field"><span>Severity</span><select value={minimumSeverity} onChange={(event) => setMinimumSeverity(event.target.value as LogSeverity)}>{severities.map((value) => <option key={value} value={value}>{value === 'information' ? 'Info' : sentenceCase(value)}+</option>)}</select></label>
        <button type="button" className={`button button--secondary ${autoScroll ? 'is-active' : ''}`} aria-pressed={autoScroll} onClick={() => setAutoScroll((value) => !value)}>{autoScroll ? <Pause aria-hidden="true" size={15} /> : <Play aria-hidden="true" size={15} />}<span>{autoScroll ? 'Pause scroll' : 'Resume scroll'}</span></button>
        <a className="button button--quiet" href={api.getLogDownloadUrl(projectId, streamOptions)} download><Download aria-hidden="true" size={15} /><span>Download</span></a>
      </div>
      {error && <div className="inline-alert inline-alert--error" role="alert">{error}</div>}
      <div className="log-list" ref={listRef} role="log" aria-label={`${sentenceCase(source)} logs`} aria-live={autoScroll ? 'polite' : 'off'}>
        {visibleEntries.length ? visibleEntries.map((entry) => <div className="log-entry" key={`${entry.sequence}-${entry.timestamp}`} data-severity={entry.severity}>
          <time dateTime={entry.timestamp}>{formatDateTime(entry.timestamp)}</time>
          <span className="log-entry__severity">{entry.severity === 'information' ? 'INFO' : entry.severity.toUpperCase()}</span>
          <span className="log-entry__message">{entry.message}</span>
        </div>) : <div className="log-empty">Waiting for matching log entries…</div>}
      </div>
      <div className="logs-footer"><span>Showing the latest {visibleEntries.length} entries</span><span>Secrets are redacted by the Abode to Web service.</span></div>
    </section>
  )
}
