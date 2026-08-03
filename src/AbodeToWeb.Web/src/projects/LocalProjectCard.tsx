import { AlertTriangle, Copy, GitBranch, Hourglass } from 'lucide-react'
import { useState } from 'react'
import type { LocalProjectSummary } from '../api/schema'
import { CardBadge } from '../components/CardBadge'
import { formatRelativeTime, projectMonogram } from '../lib/format'

export function LocalProjectCard({ project }: { project: LocalProjectSummary }) {
  const planned = Boolean(project.plannedHostname)
  const [copied, setCopied] = useState(false)
  const copyPath = () => {
    void navigator.clipboard.writeText(project.repositoryPath).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <article className={`project-card local-project-card ${planned ? 'local-project-card--planned' : ''}`}>
      <div className="project-card__head">
        <div className="project-identity">
          <span className="project-monogram" aria-hidden="true">{projectMonogram(project.name)}</span>
          <span><strong>{project.name}</strong><small>{project.description}</small></span>
        </div>
        <CardBadge kind={planned ? 'planned' : 'local'} />
      </div>
      <div className="project-card__meta">
        <div><span>Branch</span><strong>{project.branch ? <><GitBranch aria-hidden="true" size={12} /> {project.branch}</> : '—'}</strong></div>
        <div><span>Last commit</span><strong>{project.lastCommitShortSha ? `${project.lastCommitShortSha} · ${formatRelativeTime(project.lastCommitDate)}` : '—'}</strong></div>
      </div>
      {project.lastCommitMessage && <p className="local-project-card__commit" title={project.lastCommitMessage}>{project.lastCommitMessage}</p>}
      {planned && <span className="local-project-card__planned-hint"><Hourglass aria-hidden="true" size={13} />Planned → {project.plannedHostname} · needs first deploy</span>}
      {!project.exists && <p className="local-project-card__warning" role="status"><AlertTriangle aria-hidden="true" size={14} />{project.safeStatusMessage ?? 'The repository folder was not found.'}</p>}
      {project.notes && <p className="local-project-card__notes">{project.notes}</p>}
      <div className="local-project-card__path">
        <code title={project.repositoryPath}>{project.repositoryPath}</code>
        <button className="icon-button" type="button" aria-label={`Copy path for ${project.name}`} onClick={copyPath}>
          <Copy aria-hidden="true" size={14} />
        </button>
        {copied && <span className="local-project-card__copied" role="status">Copied</span>}
      </div>
    </article>
  )
}
