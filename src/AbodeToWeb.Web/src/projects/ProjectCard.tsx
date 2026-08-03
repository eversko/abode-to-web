import { ArrowUpRight, ExternalLink, FileText, Maximize2, MoreHorizontal, Play, RefreshCw, Rocket, Square } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import type { ProjectSummary } from '../api/schema'
import { useOperations } from '../operations/OperationContext'
import { formatRelativeTime, projectMonogram } from '../lib/format'
import { Button } from '../components/Button'
import { CardBadge } from '../components/CardBadge'
import { HealthSignal } from '../components/HealthSignal'
import { ProjectDrawer } from './ProjectDrawer'

export function ProjectCard({ project, onChanged }: { project: ProjectSummary; onChanged?: () => void }) {
  const api = useApi()
  const { runOperation } = useOperations()
  const [menuOpen, setMenuOpen] = useState(false)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const running = ['running', 'starting'].includes(project.runtimeState)
  const run = (action: 'start' | 'stop' | 'restart' | 'deploy' | 'check-for-updates', label: string) => {
    setMenuOpen(false)
    void runOperation(api.runProjectAction(project.id, action), `${label} ${project.name}`, onChanged)
  }

  return (
    <article className="project-card">
      <div className="project-card__head">
        <Link className="project-identity" to={`/projects/${project.id}`} aria-label={`View ${project.name}`}>
          <span className="project-monogram" aria-hidden="true">{projectMonogram(project.name)}</span>
          <span><strong>{project.name}</strong><small>{project.description}</small></span>
        </Link>
        <div className="project-card__head-actions">
          <CardBadge kind="abode-to-web" />
          <button className="icon-button" type="button" aria-label={`Quick view ${project.name}`} onClick={() => setDrawerOpen(true)}>
            <Maximize2 aria-hidden="true" size={17} />
          </button>
          <div className="menu-wrap">
            <button className="icon-button" type="button" aria-label={`More actions for ${project.name}`} aria-expanded={menuOpen} onClick={() => setMenuOpen((value) => !value)}>
              <MoreHorizontal aria-hidden="true" size={19} />
            </button>
            {menuOpen && <div className="action-menu" role="menu">
              {running && <button role="menuitem" type="button" onClick={() => run('restart', 'Restart')}><RefreshCw aria-hidden="true" />Restart</button>}
              <button role="menuitem" type="button" onClick={() => run('check-for-updates', 'Check updates for')}><ArrowUpRight aria-hidden="true" />Check for updates</button>
              <Link role="menuitem" to={`/projects/${project.id}/logs`}><FileText aria-hidden="true" />View logs</Link>
            </div>}
          </div>
        </div>
      </div>

      <div className="project-card__status">
        <HealthSignal runtimeState={project.runtimeState} health={project.health} />
        {project.git?.updateAvailable && <span className="update-label"><ArrowUpRight aria-hidden="true" size={13} />Update available</span>}
      </div>

      <div className="project-card__meta">
        <div><span>Version</span><strong>{project.git ? `${project.git.branch}@${project.git.deployedCommit ?? 'not deployed'}` : 'No repository'}</strong></div>
        <div><span>Last deployed</span><strong>{formatRelativeTime(project.lastDeploymentAt)}</strong></div>
      </div>

      {project.hostname ? <a className="hostname-link" href={`https://${project.hostname}`} target="_blank" rel="noreferrer">
        {project.hostname}<ExternalLink aria-hidden="true" size={14} />
      </a> : <span className="hostname-link hostname-link--empty">No public hostname</span>}

      <div className="project-card__actions">
        {running
          ? <Button icon={Square} onClick={() => run('stop', 'Stop')}>Stop</Button>
          : <Button icon={Play} onClick={() => run('start', 'Start')}>Start</Button>}
        <Button variant="primary" icon={Rocket} onClick={() => run('deploy', 'Deploy')}>Deploy</Button>
        {project.hostname && running && <a className="button button--quiet button--icon" href={`https://${project.hostname}`} target="_blank" rel="noreferrer" aria-label={`Open ${project.name}`}><ExternalLink aria-hidden="true" size={16} /></a>}
      </div>

      {drawerOpen && <ProjectDrawer project={project} onClose={() => setDrawerOpen(false)} onChanged={onChanged} />}
    </article>
  )
}
