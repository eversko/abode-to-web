import { ChevronRight, ExternalLink, Play, RefreshCw, Rocket, Square, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import type { ProjectSummary } from '../api/schema'
import { Button } from '../components/Button'
import { DeployTimeline } from '../components/DeployTimeline'
import { HealthSignal } from '../components/HealthSignal'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { useResource } from '../hooks/useResource'
import { LogViewer } from '../logs/LogViewer'
import { formatRelativeTime, projectMonogram } from '../lib/format'
import { useOperations } from '../operations/OperationContext'

// In-grid progressive disclosure: a quick-look panel over the existing project
// APIs. Presentation only — actions reuse runProjectAction exactly like the card;
// the full page (/projects/:id) remains the deep destination.
export function ProjectDrawer({ project, onClose, onChanged }: {
  project: ProjectSummary
  onClose: () => void
  onChanged?: () => void
}) {
  const api = useApi()
  const { runOperation } = useOperations()
  const load = useCallback((signal: AbortSignal) => api.getProject(project.id, signal), [api, project.id])
  const { data, error, loading, reload } = useResource(load)
  const [showLogs, setShowLogs] = useState(false)
  const closeRef = useRef<HTMLButtonElement>(null)

  useEffect(() => { closeRef.current?.focus() }, [])
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  const running = ['running', 'starting'].includes(project.runtimeState)
  const run = (action: 'start' | 'stop' | 'restart' | 'deploy', label: string) =>
    void runOperation(api.runProjectAction(project.id, action), `${label} ${project.name}`, () => { reload(); onChanged?.() })

  const detail = data
  const latest = detail?.activeDeployment ?? detail?.recentDeployments[0] ?? null

  return (
    <>
      <button type="button" className="drawer-scrim" aria-label="Close quick view" onClick={onClose} />
      <aside className="project-drawer" role="dialog" aria-modal="true" aria-label={`Quick view: ${project.name}`}>
        <header className="project-drawer__head">
          <span className="project-monogram" aria-hidden="true">{projectMonogram(project.name)}</span>
          <div className="project-drawer__title">
            <h2>{project.name}</h2>
            <small>{project.description}</small>
          </div>
          <button ref={closeRef} type="button" className="icon-button" aria-label="Close quick view" onClick={onClose}>
            <X aria-hidden="true" size={18} />
          </button>
        </header>

        <div className="project-drawer__body">
          {loading && !data ? <LoadingState label="Loading project" /> : error && !data ? <ErrorState error={error} retry={reload} /> : detail ? <>
            <div className="project-drawer__signal">
              <HealthSignal runtimeState={project.runtimeState} health={project.health} size="lg" />
              {project.hostname && <a className="hostname-link" href={`https://${project.hostname}`} target="_blank" rel="noreferrer">{project.hostname}<ExternalLink aria-hidden="true" size={13} /></a>}
            </div>

            <section>
              <span className="eyebrow">Deploy timeline</span>
              {latest
                ? <DeployTimeline deployment={latest} variant="vertical" />
                : <p className="muted-copy">No deployments yet.</p>}
            </section>

            <dl className="drawer-facts">
              <div><dt>Internal port</dt><dd>{detail.desiredConfiguration.runtime.internalPort ?? '—'}</dd></div>
              <div><dt>Version</dt><dd><code>{project.git ? `${project.git.branch}@${project.git.deployedCommit ?? '—'}` : 'No repo'}</code></dd></div>
              <div><dt>Adapter</dt><dd>{detail.desiredConfiguration.deployment.adapter}</dd></div>
              <div><dt>Last deploy</dt><dd>{formatRelativeTime(project.lastDeploymentAt)}</dd></div>
            </dl>

            <details className="drawer-section">
              <summary>Recent deployments <ChevronRight aria-hidden="true" className="drawer-caret" size={16} /></summary>
              <div className="drawer-section__body">
                {detail.recentDeployments.length ? detail.recentDeployments.slice(0, 5).map((deployment) => (
                  <Link key={deployment.id} className="drawer-deploy-row" to={`/projects/${project.id}/deployments`}>
                    <code>{deployment.shortSha}</code>
                    <span>{deployment.commitMessage ?? 'Deployment'}</span>
                    <small>{formatRelativeTime(deployment.completedAt ?? deployment.createdAt)}</small>
                  </Link>
                )) : <p className="muted-copy">None recorded.</p>}
              </div>
            </details>

            <details className="drawer-section">
              <summary>Health &amp; runtime <ChevronRight aria-hidden="true" className="drawer-caret" size={16} /></summary>
              <div className="drawer-section__body muted-copy">
                {detail.desiredConfiguration.health.type === 'http'
                  ? `HTTP ${detail.desiredConfiguration.health.path} every ${detail.desiredConfiguration.health.intervalSeconds}s · healthy after ${detail.desiredConfiguration.health.healthyThreshold} · unhealthy after ${detail.desiredConfiguration.health.unhealthyThreshold}.`
                  : `${detail.desiredConfiguration.health.type} health check.`}
                {' '}Restart policy: {detail.desiredConfiguration.runtime.restartPolicy} (max {detail.desiredConfiguration.runtime.maxRestarts}).
              </div>
            </details>

            <details className="drawer-section">
              <summary>Repository <ChevronRight aria-hidden="true" className="drawer-caret" size={16} /></summary>
              <div className="drawer-section__body muted-copy">
                {detail.desiredConfiguration.repository
                  ? <>Remote <code>{detail.desiredConfiguration.repository.remote}</code> · branch <code>{detail.desiredConfiguration.repository.branch}</code> · deploys an exact commit into an isolated worktree.</>
                  : 'No repository configured.'}
              </div>
            </details>

            <details className="drawer-section" onToggle={(event) => setShowLogs((event.target as HTMLDetailsElement).open)}>
              <summary>Recent logs <ChevronRight aria-hidden="true" className="drawer-caret" size={16} /></summary>
              <div className="drawer-section__body">
                {showLogs ? <LogViewer projectId={project.id} title="Recent logs" /> : null}
              </div>
            </details>
          </> : null}
        </div>

        <footer className="project-drawer__actions">
          {running
            ? <Button icon={Square} onClick={() => run('stop', 'Stop')}>Stop</Button>
            : <Button icon={Play} onClick={() => run('start', 'Start')}>Start</Button>}
          <Button variant="quiet" icon={RefreshCw} onClick={() => run('restart', 'Restart')}>Restart</Button>
          <Button variant="primary" icon={Rocket} onClick={() => run('deploy', 'Deploy')}>Deploy</Button>
          <Link className="text-link drawer-fullpage" to={`/projects/${project.id}`}>Full details<ChevronRight aria-hidden="true" size={15} /></Link>
        </footer>
      </aside>
    </>
  )
}
