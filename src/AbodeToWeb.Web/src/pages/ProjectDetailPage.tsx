import {
  ArrowLeft,
  ArrowUpRight,
  CheckCircle2,
  Download,
  ExternalLink,
  FileKey,
  GitBranch,
  Globe2,
  Play,
  RefreshCw,
  Rocket,
  ShieldAlert,
  Square,
  Trash2,
} from 'lucide-react'
import { useCallback, useState, type SyntheticEvent } from 'react'
import { Link, NavLink, useLocation, useNavigate, useParams } from 'react-router'
import { useApi } from '../api/context'
import type { AppVisibility, DeploymentSummary, GitStatus, ProjectDetail } from '../api/schema'
import { Button } from '../components/Button'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { StatusPill } from '../components/StatusPill'
import { useResource } from '../hooks/useResource'
import { formatDateTime, formatRelativeTime, projectMonogram, sentenceCase } from '../lib/format'
import { LogViewer } from '../logs/LogViewer'
import { useOperations } from '../operations/OperationContext'

const tabs = [
  { id: 'overview', label: 'Overview' },
  { id: 'deployments', label: 'Deployments' },
  { id: 'runtime', label: 'Runtime' },
  { id: 'logs', label: 'Logs' },
  { id: 'environment', label: 'Environment' },
  { id: 'domain', label: 'Domain' },
  { id: 'settings', label: 'Settings' },
] as const

type TabId = typeof tabs[number]['id']

export function ProjectDetailPage() {
  const { projectId = '' } = useParams()
  const api = useApi()
  const location = useLocation()
  const load = useCallback((signal: AbortSignal) => api.getProject(projectId, signal), [api, projectId])
  const { data, error, loading, reload } = useResource(load)
  const routeTab = location.pathname.split('/')[3]
  const activeTab: TabId = tabs.some((tab) => tab.id === routeTab) ? routeTab as TabId : 'overview'

  if (loading && !data) return <LoadingState label="Loading project" />
  if (error && !data) return <ErrorState error={error} retry={reload} title="Project unavailable" />
  if (!data) return null

  return (
    <div className="page-stack">
      <Link to="/projects" className="back-link"><ArrowLeft aria-hidden="true" size={15} />Projects</Link>
      <ProjectHeader project={data} reload={reload} />
      <nav className="tab-list" aria-label="Project sections">
        {tabs.map((tab) => <NavLink key={tab.id} to={`/projects/${projectId}${tab.id === 'overview' ? '' : `/${tab.id}`}`} end={tab.id === 'overview'} className={({ isActive }) => isActive || activeTab === tab.id && tab.id === 'overview' ? 'is-active' : ''}>{tab.label}</NavLink>)}
      </nav>
      <div className="tab-panel" role="region" aria-label={`${tabs.find((tab) => tab.id === activeTab)?.label} for ${data.summary.name}`}>
        {activeTab === 'overview' && <ProjectOverview project={data} reload={reload} />}
        {activeTab === 'deployments' && <ProjectDeployments project={data} reload={reload} />}
        {activeTab === 'runtime' && <ProjectRuntime project={data} reload={reload} />}
        {activeTab === 'logs' && <LogViewer projectId={projectId} availableSources={['runtime', 'build']} />}
        {activeTab === 'environment' && <ProjectEnvironment project={data} reload={reload} />}
        {activeTab === 'domain' && <ProjectDomain project={data} reload={reload} />}
        {activeTab === 'settings' && <ProjectSettings project={data} />}
      </div>
    </div>
  )
}

function ProjectHeader({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const { summary } = project
  const running = ['running', 'starting'].includes(summary.runtimeState)
  const run = (action: 'start' | 'stop' | 'restart' | 'deploy', label: string) => void runOperation(api.runProjectAction(summary.id, action), `${label} ${summary.name}`, reload)
  return <header className="project-header">
    <div className="project-header__identity"><span className="project-monogram project-monogram--large" aria-hidden="true">{projectMonogram(summary.name)}</span><div><span className="eyebrow">Project</span><h2>{summary.name}</h2><p>{summary.description}</p></div></div>
    <div className="project-header__actions">
      {summary.hostname && running && <a className="button button--quiet" href={`https://${summary.hostname}`} target="_blank" rel="noreferrer"><ExternalLink aria-hidden="true" size={16} /><span>Open</span></a>}
      {running ? <><Button icon={RefreshCw} onClick={() => run('restart', 'Restart')}>Restart</Button><Button icon={Square} onClick={() => run('stop', 'Stop')}>Stop</Button></> : <Button icon={Play} onClick={() => run('start', 'Start')}>Start</Button>}
      <Button icon={Rocket} onClick={() => run('deploy', 'Deploy')}>Deploy</Button>
    </div>
  </header>
}

function gitCheckLabel(git: GitStatus | null) {
  if (git?.error) return <span className="git-check-label git-check-label--failed">Check failed</span>
  if (!git?.checkedAt) return <span className="git-check-label">Not checked</span>
  if (git.updateAvailable) return <span className="update-label"><ArrowUpRight aria-hidden="true" size={13} />Update available</span>
  return <span className="git-check-label git-check-label--current">Up to date</span>
}

function ProjectOverview({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const { summary, activeDeployment, desiredConfiguration } = project
  const checkForUpdates = () => void runOperation(
    api.runProjectAction(summary.id, 'check-for-updates'),
    `Check updates for ${summary.name}`,
    reload,
  )
  return <div className="detail-grid">
    <section className="panel detail-hero"><div className="panel__heading"><div><span className="eyebrow">Current state</span><h3>Application</h3></div><StatusPill value={summary.runtimeState} /></div><div className="state-pair"><div><span>Health</span><StatusPill value={summary.health} /></div><div><span>Visibility</span><strong>{sentenceCase(summary.visibility)}</strong></div><div><span>Started</span><strong>{formatRelativeTime(summary.startedAt)}</strong></div><div><span>Internal port</span><strong>{desiredConfiguration.runtime.internalPort}</strong></div></div>{project.safeStatusMessage && <div className="inline-alert">{project.safeStatusMessage}</div>}</section>
    <section className="panel"><div className="panel__heading"><div><span className="eyebrow">Source</span><h3>Git status</h3></div><div className="panel-heading-actions"><GitBranch aria-hidden="true" /><Button variant="quiet" icon={RefreshCw} disabled={!desiredConfiguration.repository} onClick={checkForUpdates}>Check for updates</Button></div></div><dl className="review-list"><div><dt>Branch</dt><dd>{summary.git?.branch ?? desiredConfiguration.repository?.branch ?? '—'}</dd></div><div><dt>Deployed</dt><dd><code>{summary.git?.deployedCommit ?? '—'}</code></dd></div><div><dt>Remote head</dt><dd><code>{summary.git?.remoteCommit ?? '—'}</code></dd></div><div><dt>Status</dt><dd>{gitCheckLabel(summary.git)}</dd></div><div><dt>Last checked</dt><dd>{summary.git?.checkedAt ? formatRelativeTime(summary.git.checkedAt) : 'Never'}</dd></div></dl>{summary.git?.error && <div className="inline-alert inline-alert--error" role="alert">{summary.git.error}</div>}{summary.git?.commitMessage && <div className="commit-card"><strong>{summary.git.commitMessage}</strong><span>{summary.git.commitAuthor ? `${summary.git.commitAuthor} · ` : ''}{formatRelativeTime(summary.git.commitDate)}</span></div>}</section>
    <section className="panel detail-span"><div className="panel__heading"><div><span className="eyebrow">Active release</span><h3>Deployment</h3></div>{activeDeployment && <StatusPill value={activeDeployment.status} />}</div>{activeDeployment ? <dl className="deployment-facts"><div><dt>Commit</dt><dd><code>{activeDeployment.shortSha}</code></dd></div><div><dt>Completed</dt><dd>{formatDateTime(activeDeployment.completedAt)}</dd></div><div><dt>Duration</dt><dd>{activeDeployment.duration ?? '—'}</dd></div><div><dt>Build</dt><dd>{activeDeployment.buildSucceeded ? 'Passed' : 'Failed'}</dd></div><div><dt>Health</dt><dd>{activeDeployment.healthSucceeded ? 'Passed' : 'Failed'}</dd></div></dl> : <p className="muted-copy">This project has no active deployment.</p>}</section>
  </div>
}

function ProjectDeployments({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const [rollback, setRollback] = useState<DeploymentSummary>()
  const confirmRollback = () => {
    if (!rollback) return
    const deployment = rollback
    setRollback(undefined)
    void runOperation(api.rollback(project.summary.id, deployment.id), `Roll back ${project.summary.name} to ${deployment.shortSha}`, reload)
  }
  return <section className="panel"><div className="panel__heading"><div><span className="eyebrow">Release history</span><h3>Deployments</h3></div><span className="count-label">{project.recentDeployments.length} retained</span></div>
    <div className="table-scroll"><table className="data-table"><thead><tr><th scope="col">Version</th><th scope="col">Result</th><th scope="col">Build</th><th scope="col">Health</th><th scope="col">Completed</th><th scope="col"><span className="sr-only">Actions</span></th></tr></thead><tbody>{project.recentDeployments.map((deployment) => <tr key={deployment.id}><td><strong>{deployment.commitMessage ?? 'Deployment'}</strong><span><code>{deployment.shortSha}</code> · {deployment.branch}</span></td><td><StatusPill value={deployment.status} /></td><td>{deployment.buildSucceeded ? <span className="check-label"><CheckCircle2 aria-hidden="true" />Passed</span> : 'Failed'}</td><td>{deployment.healthSucceeded ? <span className="check-label"><CheckCircle2 aria-hidden="true" />Passed</span> : 'Not passed'}</td><td>{formatDateTime(deployment.completedAt)}</td><td>{deployment.isActive ? <span className="current-label">Current</span> : deployment.isKnownGood ? <Button variant="quiet" onClick={() => setRollback(deployment)}>Rollback</Button> : null}</td></tr>)}</tbody></table></div>
    <ConfirmDialog open={Boolean(rollback)} title={`Roll back to ${rollback?.shortSha ?? ''}?`} confirmLabel="Roll back" danger onCancel={() => setRollback(undefined)} onConfirm={confirmRollback}><p>Traffic will switch to this known-good retained deployment. The current release will be stopped after the configured drain period.</p></ConfirmDialog>
  </section>
}

function ProjectRuntime({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const [confirm, setConfirm] = useState(false)
  const forceStop = () => { setConfirm(false); void runOperation(api.runProjectAction(project.summary.id, 'force-stop'), `Force-stop ${project.summary.name}`, reload) }
  const runtime = project.desiredConfiguration.runtime
  return <div className="detail-grid"><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Supervisor</span><h3>Runtime policy</h3></div><StatusPill value={project.summary.runtimeState} /></div><dl className="review-list"><div><dt>Internal port</dt><dd>{runtime.internalPort}</dd></div><div><dt>Restart policy</dt><dd>{sentenceCase(runtime.restartPolicy)}</dd></div><div><dt>Restart limit</dt><dd>{runtime.maxRestarts}</dd></div><div><dt>Shutdown grace</dt><dd>{runtime.gracefulShutdownSeconds}s</dd></div></dl></section><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Recovery</span><h3>Process control</h3></div><ShieldAlert aria-hidden="true" /></div><p className="muted-copy">Abode to Web tracks the process identity and start time, supervises its Windows Job Object, and applies backoff after unexpected exits.</p><div className="danger-action"><div><strong>Force stop process tree</strong><span>Use only when graceful shutdown does not finish.</span></div><Button variant="danger" onClick={() => setConfirm(true)}>Force stop</Button></div></section><ConfirmDialog open={confirm} title={`Force-stop ${project.summary.name}?`} confirmLabel="Force stop" danger onCancel={() => setConfirm(false)} onConfirm={forceStop}><p>Abode to Web will terminate the supervised process tree without waiting for graceful shutdown. In-flight requests may be interrupted.</p></ConfirmDialog></div>
}

type SecretAction = { kind: 'replace' | 'delete'; key: string; value: string }

function ProjectEnvironment({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const [action, setAction] = useState<SecretAction>()
  const [newKey, setNewKey] = useState('')
  const [newValue, setNewValue] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string>()
  const [secretError, setSecretError] = useState<string>()
  const addSecret = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    const key = newKey.trim()
    if (project.secretKeys.some((secret) => secret.key.toLowerCase() === key.toLowerCase())) {
      setSecretError(`${key} already exists. Use Replace so Abode to Web can confirm the change.`)
      return
    }
    setBusy(true)
    setSecretError(undefined)
    try {
      await api.replaceSecret(project.summary.id, key, newValue)
      setNewKey('')
      setNewValue('')
      setMessage(`${key} was added.`)
      reload()
    } catch (reason) {
      setSecretError(reason instanceof Error ? reason.message : 'Abode to Web could not add this secret.')
    } finally { setBusy(false) }
  }
  const confirm = async () => {
    if (!action) return
    setBusy(true)
    setSecretError(undefined)
    try {
      if (action.kind === 'replace') await api.replaceSecret(project.summary.id, action.key, action.value)
      else await api.deleteSecret(project.summary.id, action.key)
      setMessage(`${action.key} was ${action.kind === 'replace' ? 'replaced' : 'deleted'}.`)
      setAction(undefined)
      reload()
    } catch (reason) {
      setSecretError(reason instanceof Error ? reason.message : 'Abode to Web could not update this secret.')
    } finally { setBusy(false) }
  }
  return <section className="panel"><div className="panel__heading"><div><span className="eyebrow">Protected local file</span><h3>Environment</h3></div><div className="panel-heading-actions"><FileKey aria-hidden="true" /><a className="button button--quiet" href={api.getEnvironmentExampleUrl(project.summary.id)} download={`${project.summary.id}.env.example`}><Download aria-hidden="true" size={16} /><span>Download .env.example</span></a></div></div><p className="muted-copy">Secret values are never returned to this browser. Add or replace a value in the protected per-project environment file; after saving, only its key remains visible. The downloadable example contains names with empty values only.</p>{message && <div className="inline-alert" role="status">{message}</div>}{secretError && <div className="inline-alert inline-alert--error" role="alert">{secretError}</div>}
    <form className="secret-create" onSubmit={(event) => { void addSecret(event) }}>
      <label className="field"><span>Secret key</span><input required maxLength={128} pattern="[A-Za-z_][A-Za-z0-9_]{0,127}" autoComplete="off" value={newKey} onChange={(event) => setNewKey(event.target.value)} placeholder="API_TOKEN" /></label>
      <label className="field"><span>Secret value</span><input type="password" autoComplete="new-password" value={newValue} onChange={(event) => setNewValue(event.target.value)} /></label>
      <Button type="submit" busy={busy} disabled={!newKey.trim()}>Add secret</Button>
    </form>
    <div className="secret-list">{project.secretKeys.map((secret) => <div className="secret-row" key={secret.key}><div><strong>{secret.key}</strong><span>{secret.hasValue ? '••••••••••••' : 'Not set'} · Updated {formatRelativeTime(secret.updatedAt)}</span></div><div><Button variant="quiet" onClick={() => setAction({ kind: 'replace', key: secret.key, value: '' })}>Replace</Button><Button variant="quiet" onClick={() => setAction({ kind: 'delete', key: secret.key, value: '' })}>Delete</Button></div></div>)}</div>
    <ConfirmDialog open={Boolean(action)} title={`${action?.kind === 'replace' ? 'Replace' : 'Delete'} ${action?.key ?? 'secret'}?`} confirmLabel={action?.kind === 'replace' ? 'Replace secret' : 'Delete secret'} danger={action?.kind === 'delete'} busy={busy} onCancel={() => setAction(undefined)} onConfirm={() => { void confirm() }}>
      {action?.kind === 'replace' ? <><p>The existing value cannot be recovered. Enter the replacement; it will not be shown again.</p><label className="field"><span>New value</span><input type="password" autoComplete="new-password" value={action.value} onChange={(event) => setAction({ ...action, value: event.target.value })} /></label></> : <p>Abode to Web will remove this key from the protected environment file. The application may fail to start until it is replaced.</p>}
    </ConfirmDialog>
  </section>
}

const VISIBILITY_HINTS: Record<AppVisibility, string> = {
  public: 'Reachable through the application gateway and eligible for tunnel export.',
  protected: 'Routed only with a valid Cloudflare Access assertion. Requires the Access application audience to be configured.',
  disabled: 'Never published by the gateway, and deployments are refused until re-enabled.',
}

function ProjectDomain({ project, reload }: { project: ProjectDetail; reload: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const [hostname, setHostname] = useState(project.summary.hostname ?? '')
  const [confirm, setConfirm] = useState(false)
  const [visibility, setVisibility] = useState<AppVisibility>(project.summary.visibility)
  const [confirmVisibility, setConfirmVisibility] = useState(false)
  const save = () => { setConfirm(false); void runOperation(api.changeHostname(project.summary.id, hostname), `Change hostname for ${project.summary.name}`, reload) }
  const applyVisibility = () => { setConfirmVisibility(false); void runOperation(api.changeVisibility(project.summary.id, visibility), `Change visibility for ${project.summary.name}`, reload) }
  return <div className="detail-grid"><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Public route</span><h3>Domain</h3></div><Globe2 aria-hidden="true" /></div><form onSubmit={(event: SyntheticEvent<HTMLFormElement>) => { event.preventDefault(); setConfirm(true) }}><label className="field"><span>Hostname</span><input required value={hostname} onChange={(event) => setHostname(event.target.value)} /></label><p className="field-hint">Abode to Web routes this hostname at the loopback gateway. Generate and apply the matching Cloudflare Tunnel configuration separately.</p>{!api.capabilities.changeHostname && <div className="inline-alert">This server build can display the desired hostname but does not yet expose the atomic hostname-change endpoint.</div>}<Button type="submit" disabled={hostname === project.summary.hostname || !api.capabilities.changeHostname}>Save hostname</Button></form></section><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Connection</span><h3>Routing status</h3></div><StatusPill value={project.summary.health} /></div><dl className="review-list"><div><dt>Gateway</dt><dd>127.0.0.1:4580</dd></div><div><dt>Offline page</dt><dd>{project.desiredConfiguration.routing.offlinePage ? 'Enabled' : 'Disabled'}</dd></div><div><dt>Public URL</dt><dd>{project.summary.hostname ? <a className="text-link" href={`https://${project.summary.hostname}`} target="_blank" rel="noreferrer">Open <ExternalLink aria-hidden="true" size={13} /></a> : '—'}</dd></div></dl><label className="field"><span>Visibility</span><select value={visibility} onChange={(event) => setVisibility(event.target.value as AppVisibility)} disabled={!api.capabilities.changeVisibility}><option value="public">Public</option><option value="protected">Protected (Cloudflare Access)</option><option value="disabled">Disabled</option></select></label><p className="field-hint">{VISIBILITY_HINTS[visibility]}</p><Button disabled={visibility === project.summary.visibility || !api.capabilities.changeVisibility} onClick={() => setConfirmVisibility(true)}>Apply visibility</Button></section><ConfirmDialog open={confirm} title="Change the public hostname?" confirmLabel="Change hostname" onCancel={() => setConfirm(false)} onConfirm={save}><p>Requests for the old hostname will stop routing after the manifest reloads. Update the Cloudflare Tunnel route before relying on the new address.</p><p><strong>{project.summary.hostname}</strong> → <strong>{hostname}</strong></p></ConfirmDialog><ConfirmDialog open={confirmVisibility} title={`Make ${project.summary.name} ${visibility}?`} confirmLabel="Apply visibility" danger={visibility === 'disabled'} onCancel={() => setConfirmVisibility(false)} onConfirm={applyVisibility}><p>{VISIBILITY_HINTS[visibility]}</p>{visibility === 'protected' && !project.desiredConfiguration.routing.accessApplicationAudience && <p><strong>This project has no Access application audience configured</strong> — the manifest will reject protected visibility until it is set.</p>}<p><strong>{sentenceCase(project.summary.visibility)}</strong> → <strong>{sentenceCase(visibility)}</strong></p></ConfirmDialog></div>
}

function ProjectSettings({ project }: { project: ProjectDetail }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const navigate = useNavigate()
  const [confirm, setConfirm] = useState(false)
  const remove = () => { setConfirm(false); void runOperation(api.deleteProject(project.summary.id, false), `Delete ${project.summary.name}`, () => { void navigate('/projects') }) }
  const configuration = project.desiredConfiguration
  return <div className="detail-grid"><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Desired configuration</span><h3>Settings</h3></div></div><dl className="review-list"><div><dt>Repository</dt><dd>{configuration.repository?.localPath ?? 'No repository'}</dd></div><div><dt>Branch</dt><dd>{configuration.repository?.branch ?? '—'}</dd></div><div><dt>Adapter</dt><dd>{sentenceCase(configuration.deployment.adapter)}</dd></div><div><dt>Autostart</dt><dd>{configuration.autostart ? 'Enabled' : 'Disabled'}</dd></div><div><dt>Protected</dt><dd>{project.summary.protected ? 'Yes' : 'No'}</dd></div></dl></section><section className="panel panel--danger"><div className="panel__heading"><div><span className="eyebrow">Danger zone</span><h3>Remove project</h3></div><Trash2 aria-hidden="true" /></div><p className="muted-copy">Stop this project and remove it from Abode to Web's desired configuration and gateway. Its secrets are deleted and its project ID is permanently retired.</p>{!api.capabilities.deleteProject && <div className="inline-alert">This server build does not yet expose the project-removal endpoint.</div>}<Button variant="danger" disabled={!api.capabilities.deleteProject} onClick={() => setConfirm(true)}>Delete project</Button></section><ConfirmDialog open={confirm} title={`Delete ${project.summary.name}?`} confirmLabel="Delete project" danger onCancel={() => setConfirm(false)} onConfirm={remove}><p>This stops the project, deletes its Abode to Web-managed secrets, and permanently retires its project ID so it cannot be reused. The source repository and immutable deployment and audit history remain.</p></ConfirmDialog></div>
}
